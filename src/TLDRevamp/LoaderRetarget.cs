using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Mono.Cecil;

namespace TLDRevamp
{
    /// TLDLoader mods are compiled against the public game, where some game fields are an integer of the other sign:
    /// tosaveitemscript.idInSave is an int there and a uint on the beta. A mod that reads it stops with
    /// MissingFieldException in every method that does. Of the TLD Workshop's 224 DLLs that reference the game
    /// (2026-11-11, metadata only), 103 read idInSave; with this, the ones that find every game member they use go from
    /// 51 to 58, and the rest lose one blocker in those methods.
    ///
    /// Before ModHost loads a mod, this points the mod's own field references (never the game) at the beta's type when
    /// the field exists under the same name and only the sign of a same-size integer differs. IL keeps both as the same
    /// value on the stack, so nothing else in the mod changes. Nothing else is guessed at: a member that is really gone
    /// stays missing. Kept free of Unity and BepInEx, so tools/unit/LoaderRetargetTest runs it against a made-up game.
    public static class LoaderRetarget
    {
        /// Rewrites <paramref name="mod"/>'s field references into <paramref name="game"/> (matched by assembly name)
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
            return changes;
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
