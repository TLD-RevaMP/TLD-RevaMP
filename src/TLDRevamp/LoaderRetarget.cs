using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace TLDRevamp
{
    /// Public-game names with the same meaning on the beta. Only the mod's code changes. Field paths address the
    /// beta's own storage, including stores and managed addresses; there is no second copy to keep up to date.
    /// Kept free of Unity and BepInEx so the unit fixtures run the real rewriter against a made-up game.
    public static class LoaderRetarget
    {
        // old owner, old field, field type, beta root, beta field path. A different root starts at a static field.
        private static readonly string[][] Fields =
        {
            new[] { "fpscontroller", "Tb", "UnityEngine.Transform", "fpscontroller", "BodyRot" },
            new[] { "fpscontroller", "M", "mainscript", "mainscript", "s" },
            new[] { "radioscript", "v", "System.Single", "radioscript", "Bvolume.tunedLerp" },
            new[] { "carscript", "lampak", "System.Int32", "carscript", "lightUsable.currentTurnState" },
            new[] { "carscript", "KmhObj", "UnityEngine.GameObject", "carscript", "debugCanvas" },
            new[] { "menuhandler", "DFMS", "DataFromMenuScript", "DataFromMenuScript", "s" },
            new[] { "mainscript", "DFMS", "DataFromMenuScript", "DataFromMenuScript", "s" },
            new[] { "mainscript", "SettingObj", "settingsscript", "settingsscript", "s" },
            new[] { "menuhandler", "Settings", "settingsscript", "settingsscript", "s" },
            new[] { "mainscript", "GSettings", "gamemodesettingsscript/gamemodesettings", "gamemodesettingsscript", "s.S" },
            new[] { "mainscript", "godmode", "System.Boolean", "kaposztaleves", "s.settings.god" },
            new[] { "mainscript", "Showkmh", "System.Boolean", "kaposztaleves", "s.settings.kmh" },
        };

        private static readonly string[][] Setters =
        {
            new[] { "ChGodMode", "godmode" }, new[] { "ChKmh", "Showkmh" },
        };

        private static readonly string[][] MovedMethods =
        {
            new[] { "FoodCh", "PressedFoodCh" }, new[] { "WaterCh", "PressedWaterCh" },
            new[] { "HpCh", "PressedHpCh" }, new[] { "PissCh", "PressedPissCh" }, new[] { "ShitCh", "PressedShitCh" },
        };

        // old owner, method, old parameter list, beta parameter list, beta arguments. Numbers name old arguments
        // (including this at zero); the other words supply the beta's ordinary local-call defaults.
        private static readonly string[][] Methods =
        {
            new[] { "fpscontroller", "RightHandRelease", "", "System.Boolean", "true" },
            new[] { "radioscript", "UpdValues", "System.Boolean", "", "" },
            new[] { "mainscript", "PressedEscape", "", "System.Boolean,System.Boolean", "true,false" },
            new[] { "headlightscript", "SwitchState", "System.Int32", "System.Int32,usablescript", "1,null" },
            new[] { "headlightscript", "SwitchState", "", "System.Int32,usablescript", "nextLight,null" },
            new[] { "survivalscript", "Damage", "System.Single,System.Boolean", "System.Single,UnityEngine.Vector3,System.Boolean", "1,zero,2" },
            new[] { "survivalscript", "DamageInstant", "System.Single,System.Boolean", "System.Single,UnityEngine.Vector3,System.Boolean", "1,zero,2" },
        };

        public static IEnumerable<string> AliasDescriptions()
        {
            foreach (var a in Fields) yield return a[0] + "." + a[1] + " -> " + a[3] + "." + a[4] + " (read/write/address)";
            foreach (var a in Methods) yield return a[0] + "." + a[1] + "(" + a[2] + ") -> " + a[0] + "." + a[1] + "(" + a[3] + ") [" + a[4] + "]";
            foreach (var a in Setters)
                foreach (var f in Fields)
                    if (f[0] == "mainscript" && f[1] == a[1]) yield return "mainscript." + a[0] + "(System.Boolean) -> store " + f[3] + "." + f[4];
            foreach (var a in MovedMethods) yield return "mainscript." + a[0] + "(System.Single) -> kaposztaleves.s." + a[1] + "(System.Single)";
            yield return "same-name integer fields -> beta integer sign (including tosaveitemscript.idInSave Int32 -> UInt32)";
        }

        /// Rewrites <paramref name="mod"/>'s member accesses into <paramref name="game"/> (matched by assembly name)
        /// and returns one line per change; empty when the mod needed nothing.
        public static List<string> Apply(ModuleDefinition mod, Assembly game)
        {
            var changes = new List<string>();
            string gameName = game.GetName().Name;
            foreach (var mr in mod.GetMemberReferences())
            {
                if (!(mr is FieldReference fr)) continue;
                var owner = GameType(game, gameName, fr.DeclaringType);
                if (owner == null) continue;
                var fi = owner.GetField(fr.Name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
                if (fi == null) continue;
                var swap = OtherSign(fr.FieldType.MetadataType, fi.FieldType);
                if (swap == null) continue;
                string was = fr.FieldType.Name;
                fr.FieldType = swap(mod.TypeSystem);
                changes.Add(fr.DeclaringType.FullName + "." + fr.Name + " " + was + " -> " + fr.FieldType.Name);
            }
            // Snapshot the methods: adapters added below must not themselves be visited.
            var bodies = new List<MethodDefinition>();
            foreach (var t in mod.Types) Collect(t, bodies);
            TypeDefinition helpers = null;
            var adapters = new Dictionary<string, MethodDefinition>();
            foreach (var method in bodies)
            foreach (var ins in method.Body.Instructions)
            {
                var member = ins.Operand as MemberReference;
                if (member == null || GameType(game, gameName, member.DeclaringType) == null) continue;
                // A field prefix cannot prefix a call. Leave such unusual IL alone.
                if (ins.Previous != null && ins.Previous.OpCode.OpCodeType == OpCodeType.Prefix) continue;
                string key = ins.OpCode.Code + " " + member.FullName;
                MethodDefinition adapter;
                if (!adapters.TryGetValue(key, out adapter))
                {
                    string description;
                    adapter = FieldAdapter(mod, game, ins, out description) ?? MethodAdapter(mod, game, ins, out description);
                    if (adapter == null) continue;
                    if (helpers == null)
                    {
                        string name = "__TLDRevampAliases";
                        while (mod.GetType(name) != null) name += "_";
                        helpers = new TypeDefinition("", name, Mono.Cecil.TypeAttributes.NotPublic | Mono.Cecil.TypeAttributes.Abstract | Mono.Cecil.TypeAttributes.Sealed, mod.TypeSystem.Object);
                        mod.Types.Add(helpers);
                    }
                    adapter.Name = "Alias" + adapters.Count;
                    helpers.Methods.Add(adapter);
                    adapters.Add(key, adapter);
                    if (!changes.Contains(description)) changes.Add(description);
                }
                // Replacing the instruction in place preserves branches, switch targets and exception boundaries.
                ins.OpCode = OpCodes.Call;
                ins.Operand = adapter;
            }
            return changes;
        }

        private static void Collect(TypeDefinition type, List<MethodDefinition> methods)
        {
            foreach (var m in type.Methods) if (m.HasBody) methods.Add(m);
            foreach (var t in type.NestedTypes) Collect(t, methods);
        }

        private const BindingFlags PublicFields = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static;
        private static string TypeName(Type t) => t.FullName.Replace('+', '/');
        private static string Parameters(MethodReference m) => string.Join(",", System.Linq.Enumerable.Select(m.Parameters, p => p.ParameterType.FullName));

        private static MethodDefinition NewAdapter(ModuleDefinition mod, TypeReference owner, TypeReference result)
        {
            var m = new MethodDefinition("", Mono.Cecil.MethodAttributes.Assembly | Mono.Cecil.MethodAttributes.Static, result);
            m.Parameters.Add(new ParameterDefinition(mod.ImportReference(owner)));
            return m;
        }

        private static MethodDefinition FieldAdapter(ModuleDefinition mod, Assembly game, Instruction ins, out string description)
        {
            description = null;
            var old = ins.Operand as FieldReference;
            bool store = ins.OpCode == OpCodes.Stfld, address = ins.OpCode == OpCodes.Ldflda;
            if (old == null || (!store && !address && ins.OpCode != OpCodes.Ldfld)) return null;
            foreach (var a in Fields)
            {
                if (old.DeclaringType.FullName != a[0] || old.Name != a[1] || old.FieldType.FullName != a[2]) continue;
                if (game.GetType(a[0]).GetField(a[1], PublicFields) != null) return null; // a public build already has it
                var root = game.GetType(a[3]);
                if (root == null) return null;
                var path = new List<FieldInfo>();
                var current = root;
                foreach (var name in a[4].Split('.'))
                {
                    var f = current.GetField(name, PublicFields);
                    if (f == null || (path.Count > 0 && f.IsStatic)) return null;
                    path.Add(f); current = f.FieldType;
                }
                if (TypeName(current) != a[2] || (a[3] != a[0] && !path[0].IsStatic)) return null;
                var last = path[path.Count - 1];
                if ((store || address) && last.IsInitOnly) return null;
                var ft = mod.ImportReference(current);
                var m = NewAdapter(mod, old.DeclaringType, store ? mod.TypeSystem.Void : address ? new ByReferenceType(ft) : ft);
                if (store) m.Parameters.Add(new ParameterDefinition(ft));
                var il = m.Body.GetILProcessor();
                if (!path[0].IsStatic) il.Emit(OpCodes.Ldarg_0);
                for (int i = 0; i < path.Count; i++)
                {
                    var f = path[i];
                    bool end = i == path.Count - 1;
                    if (end && store) il.Emit(OpCodes.Ldarg_1);
                    bool byAddress = end ? address : f.FieldType.IsValueType;
                    il.Emit(end && store ? (f.IsStatic ? OpCodes.Stsfld : OpCodes.Stfld) : byAddress ? (f.IsStatic ? OpCodes.Ldsflda : OpCodes.Ldflda) : (f.IsStatic ? OpCodes.Ldsfld : OpCodes.Ldfld), mod.ImportReference(f));
                }
                il.Emit(OpCodes.Ret);
                description = a[0] + "." + a[1] + " -> " + a[3] + "." + a[4];
                return m;
            }
            return null;
        }

        private static MethodDefinition MethodAdapter(ModuleDefinition mod, Assembly game, Instruction ins, out string description)
        {
            description = null;
            var old = ins.Operand as MethodReference;
            if (old == null || !old.HasThis || old.ReturnType.MetadataType != MetadataType.Void || (ins.OpCode != OpCodes.Call && ins.OpCode != OpCodes.Callvirt)) return null;
            if (old.DeclaringType.FullName == "mainscript")
            {
                // These callbacks moved with the developer menu. Use its own methods and storage.
                foreach (var a in Setters)
                    if (old.Name == a[0] && Parameters(old) == "System.Boolean" && game.GetType("mainscript").GetMethod(a[0]) == null)
                    {
                        var setter = FieldAdapter(mod, game, Instruction.Create(OpCodes.Stfld, new FieldReference(a[1], mod.TypeSystem.Boolean, old.DeclaringType)), out description);
                        if (setter != null) description = "mainscript." + a[0] + "(System.Boolean) -> " + description;
                        return setter;
                    }
                foreach (var a in MovedMethods)
                    if (old.Name == a[0] && Parameters(old) == "System.Single" && game.GetType("mainscript").GetMethod(a[0]) == null)
                    {
                        var root = game.GetType("kaposztaleves");
                        var singleton = root?.GetField("s", PublicFields);
                        var target = root?.GetMethod(a[1], new[] { typeof(float) });
                        if (singleton == null || !singleton.IsStatic || singleton.FieldType != root || target == null || target.IsStatic || target.ReturnType != typeof(void)) return null;
                        var m = NewAdapter(mod, old.DeclaringType, mod.TypeSystem.Void);
                        m.Parameters.Add(new ParameterDefinition(mod.TypeSystem.Single));
                        var il = m.Body.GetILProcessor();
                        il.Emit(OpCodes.Ldsfld, mod.ImportReference(singleton)); il.Emit(OpCodes.Ldarg_1);
                        il.Emit(OpCodes.Callvirt, mod.ImportReference(target)); il.Emit(OpCodes.Ret);
                        description = "mainscript." + a[0] + "(System.Single) -> kaposztaleves.s." + a[1] + "(System.Single)";
                        return m;
                    }
            }
            foreach (var a in Methods)
            {
                if (old.DeclaringType.FullName != a[0] || old.Name != a[1] || Parameters(old) != a[2]) continue;
                var owner = game.GetType(a[0]);
                MethodInfo target = null;
                foreach (var candidate in owner.GetMethods(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (candidate.Name != a[1]) continue;
                    string args = string.Join(",", System.Linq.Enumerable.Select(candidate.GetParameters(), p => TypeName(p.ParameterType)));
                    if (args == a[2]) return null;
                    if (args == a[3] && candidate.ReturnType == typeof(void)) target = candidate;
                }
                if (target == null) return null;
                var nextLight = owner.GetField("switchToState", PublicFields);
                var states = owner.GetField("states", PublicFields);
                var count = states?.FieldType.GetProperty("Count")?.GetGetMethod();
                if (a[4].Contains("nextLight") && (nextLight == null || nextLight.FieldType != typeof(int) || count == null || count.ReturnType != typeof(int))) return null;
                var m = NewAdapter(mod, old.DeclaringType, mod.TypeSystem.Void);
                foreach (var p in old.Parameters) m.Parameters.Add(new ParameterDefinition(mod.ImportReference(p.ParameterType)));
                var il = m.Body.GetILProcessor();
                il.Emit(OpCodes.Ldarg_0);
                var argsToPass = a[4].Length == 0 ? new string[0] : a[4].Split(',');
                for (int i = 0; i < argsToPass.Length; i++)
                {
                    switch (argsToPass[i])
                    {
                        case "true": il.Emit(OpCodes.Ldc_I4_1); break;
                        case "false": il.Emit(OpCodes.Ldc_I4_0); break;
                        case "null": il.Emit(OpCodes.Ldnull); break;
                        case "nextLight":
                            var next = new VariableDefinition(mod.TypeSystem.Int32);
                            m.Body.Variables.Add(next);
                            il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldfld, mod.ImportReference(nextLight));
                            il.Emit(OpCodes.Ldc_I4_1); il.Emit(OpCodes.Add); il.Emit(OpCodes.Stloc, next);
                            il.Emit(OpCodes.Ldloc, next); il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldfld, mod.ImportReference(states));
                            il.Emit(OpCodes.Callvirt, mod.ImportReference(count));
                            var wrapped = Instruction.Create(OpCodes.Ldloc, next);
                            il.Emit(OpCodes.Blt, wrapped); il.Emit(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Stloc, next);
                            il.Append(wrapped); break;
                        case "zero":
                            var local = new VariableDefinition(mod.ImportReference(target.GetParameters()[i].ParameterType));
                            m.Body.Variables.Add(local); m.Body.InitLocals = true;
                            il.Emit(OpCodes.Ldloca, local); il.Emit(OpCodes.Initobj, local.VariableType); il.Emit(OpCodes.Ldloc, local); break;
                        default: il.Emit(OpCodes.Ldarg, m.Parameters[int.Parse(argsToPass[i])]); break;
                    }
                }
                il.Emit(OpCodes.Callvirt, mod.ImportReference(target));
                il.Emit(OpCodes.Ret);
                description = a[0] + "." + a[1] + "(" + a[2] + ") -> " + a[0] + "." + a[1] + "(" + a[3] + ") [" + a[4] + "]";
                return m;
            }
            return null;
        }

        /// Reads the mod's DLL, rewrites it if it needs it and loads it: from the bytes when something changed (the
        /// assembly then has no Location), as before with LoadFrom when nothing did or the rewrite failed.
        public static Assembly Load(string dll, Assembly game, IEnumerable<string> searchDirs, Action<string> log)
        {
            try
            {
                var resolver = new DefaultAssemblyResolver();   // writing may resolve the mod's references (enum arguments of attributes)
                foreach (var d in searchDirs) if (Directory.Exists(d)) resolver.AddSearchDirectory(d);
                using (var mod = ModuleDefinition.ReadModule(new MemoryStream(File.ReadAllBytes(dll)),
                           new ReaderParameters { AssemblyResolver = resolver, ReadingMode = ReadingMode.Immediate }))
                {
                    var changes = Apply(mod, game);
                    if (changes.Count > 0)
                    {
                        var ms = new MemoryStream();
                        mod.Write(ms);
                        var asm = Assembly.Load(ms.ToArray());
                        log(Path.GetFileName(dll) + " retargeted to the beta: " + string.Join(", ", changes.ToArray()));
                        return asm;
                    }
                }
            }
            catch (Exception e) { log(Path.GetFileName(dll) + ": retargeting failed, loaded unchanged (" + e.Message + ")"); }
            return Assembly.LoadFrom(dll);
        }

        /// The game's runtime type for a reference into it (nested types too), or null for anything else.
        private static Type GameType(Assembly game, string gameName, TypeReference tr)
        {
            if (tr == null || tr is TypeSpecification) return null;
            var root = tr;
            while (root.DeclaringType != null) root = root.DeclaringType;
            if (!(root.Scope is AssemblyNameReference scope) || scope.Name != gameName) return null;
            string name = tr.Name;
            for (var d = tr.DeclaringType; d != null; d = d.DeclaringType) name = d.Name + "+" + name;
            if (!string.IsNullOrEmpty(root.Namespace)) name = root.Namespace + "." + name;
            return game.GetType(name, false);
        }

        private static Func<TypeSystem, TypeReference> OtherSign(MetadataType mine, Type real)
        {
            switch (mine)
            {
                case MetadataType.Int32: return real == typeof(uint) ? ts => ts.UInt32 : (Func<TypeSystem, TypeReference>)null;
                case MetadataType.UInt32: return real == typeof(int) ? ts => ts.Int32 : (Func<TypeSystem, TypeReference>)null;
                case MetadataType.Int64: return real == typeof(ulong) ? ts => ts.UInt64 : (Func<TypeSystem, TypeReference>)null;
                case MetadataType.UInt64: return real == typeof(long) ? ts => ts.Int64 : (Func<TypeSystem, TypeReference>)null;
                case MetadataType.Int16: return real == typeof(ushort) ? ts => ts.UInt16 : (Func<TypeSystem, TypeReference>)null;
                case MetadataType.UInt16: return real == typeof(short) ? ts => ts.Int16 : (Func<TypeSystem, TypeReference>)null;
                case MetadataType.SByte: return real == typeof(byte) ? ts => ts.Byte : (Func<TypeSystem, TypeReference>)null;
                case MetadataType.Byte: return real == typeof(sbyte) ? ts => ts.SByte : (Func<TypeSystem, TypeReference>)null;
                default: return null;
            }
        }
    }
}
