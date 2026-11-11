using System;
using System.IO;
using System.Reflection;
using Mono.Cecil;
using Mono.Cecil.Cil;
using TLDRevamp;
using TypeAttributes = Mono.Cecil.TypeAttributes;
using MethodAttributes = Mono.Cecil.MethodAttributes;

// A made-up game (neutral names: the repository holds no game code). The "public game" a mod was built against has an
// int key; this one has a uint. Its float, which the mod reads as a double, is a real mismatch and must stay one.
public class Saved { public uint key; public float size; }

/// The REAL src/TLDRevamp/LoaderRetarget.cs on a mod written with Mono.Cecil against the public shape of the made-up
/// game above. Before: calling the mod's method throws MissingFieldException, as a workshop mod reading idInSave does
/// on the beta. After Apply: the same method runs and reads the right value. A mismatch that is not just the sign,
/// and a mod that needs nothing, are left alone.
static class LoaderRetargetTest
{
    static int failures;
    static void Check(string name, bool ok) { Console.WriteLine((ok ? "PASS " : "FAIL ") + name); if (!ok) failures++; }

    static ModuleDefinition MakeMod(string name)
    {
        var mod = ModuleDefinition.CreateModule(name, ModuleKind.Dll);
        var game = typeof(Saved).Assembly.GetName();
        var scope = new AssemblyNameReference(game.Name, game.Version);
        mod.AssemblyReferences.Add(scope);
        var saved = new TypeReference("", "Saved", mod, scope, false);
        var code = new TypeDefinition("Fake", "ModCode", TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed, mod.TypeSystem.Object);
        mod.Types.Add(code);

        var read = new MethodDefinition("Read", MethodAttributes.Public | MethodAttributes.Static, mod.TypeSystem.Int32);
        read.Parameters.Add(new ParameterDefinition(saved));
        var il = read.Body.GetILProcessor();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, new FieldReference("key", mod.TypeSystem.Int32, saved));   // int: the public game's
        il.Emit(OpCodes.Ret);
        code.Methods.Add(read);

        var size = new MethodDefinition("Size", MethodAttributes.Public | MethodAttributes.Static, mod.TypeSystem.Double);
        size.Parameters.Add(new ParameterDefinition(saved));
        il = size.Body.GetILProcessor();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, new FieldReference("size", mod.TypeSystem.Double, saved));   // not a sign difference
        il.Emit(OpCodes.Ret);
        code.Methods.Add(size);
        return mod;
    }

    // as a real mod: read from its bytes (Cecil lists references only for a module it read)
    static ModuleDefinition Reread(ModuleDefinition mod) { var ms = new MemoryStream(); mod.Write(ms); ms.Position = 0; return ModuleDefinition.ReadModule(ms, new ReaderParameters { ReadingMode = ReadingMode.Immediate }); }

    static Assembly LoadBytes(ModuleDefinition mod) { var ms = new MemoryStream(); mod.Write(ms); return Assembly.Load(ms.ToArray()); }

    static bool Throws(Func<object> f) { try { f(); return false; } catch (TargetInvocationException) { return true; } catch (MissingMemberException) { return true; } }

    static object Call(Assembly a, string m, Saved s) => a.GetType("Fake.ModCode").GetMethod(m).Invoke(null, new object[] { s });

    static int Main()
    {
        var gameAsm = typeof(Saved).Assembly;
        var sample = new Saved { key = 7, size = 1.5f };

        var before = LoadBytes(MakeMod("FakeModBefore"));
        Check("before: reading the int field fails", Throws(() => Call(before, "Read", sample)));

        var mod = Reread(MakeMod("FakeModAfter"));
        var changes = LoaderRetarget.Apply(mod, gameAsm);
        foreach (var c in changes) Console.WriteLine("  " + c);
        Check("one change: the int field is a uint now", changes.Count == 1 && changes[0] == "Saved.key Int32 -> UInt32");
        var after = LoadBytes(mod);
        Check("after: the field reads 7", (int)Call(after, "Read", sample) == 7);
        Check("a float read as a double stays missing", Throws(() => Call(after, "Size", sample)));
        Check("Apply again: no changes", LoaderRetarget.Apply(mod, gameAsm).Count == 0);

        string tmp = Path.Combine(Path.GetTempPath(), "retarget-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            // a mod that needs nothing is loaded from its file, as before
            var plain = ModuleDefinition.CreateModule("FakePlain", ModuleKind.Dll);
            plain.Types.Add(new TypeDefinition("Fake", "Plain", TypeAttributes.Public, plain.TypeSystem.Object));
            string plainPath = Path.Combine(tmp, "FakePlain.dll");
            plain.Write(plainPath);
            string logged = null;
            var asm = LoaderRetarget.Load(plainPath, gameAsm, new[] { tmp }, m => logged = m);
            Check("Load: a mod with nothing to change keeps its file", !string.IsNullOrEmpty(asm.Location) && logged == null);

            string modPath = Path.Combine(tmp, "FakeModFile.dll");
            MakeMod("FakeModFile").Write(modPath);
            asm = LoaderRetarget.Load(modPath, gameAsm, new[] { tmp }, m => logged = m);
            Check("Load: a mod that needed a change comes from bytes and logs it", string.IsNullOrEmpty(asm.Location) && logged != null && logged.Contains("Saved.key Int32 -> UInt32"));
            Check("Load: and runs", (int)Call(asm, "Read", new Saved { key = 9 }) == 9);

            File.WriteAllText(Path.Combine(tmp, "Broken.dll"), "not a dll");
            logged = null;
            bool threw = false;
            try { LoaderRetarget.Load(Path.Combine(tmp, "Broken.dll"), gameAsm, new[] { tmp }, m => logged = m); } catch (BadImageFormatException) { threw = true; }
            Check("Load: an unreadable file falls back to LoadFrom (its own error) and says so", threw && logged != null && logged.Contains("loaded unchanged"));
        }
        finally { try { Directory.Delete(tmp, true); } catch { } }

        Console.WriteLine(failures == 0 ? "ALL PASS" : failures + " FAILED");
        return failures == 0 ? 0 : 1;
    }
}
