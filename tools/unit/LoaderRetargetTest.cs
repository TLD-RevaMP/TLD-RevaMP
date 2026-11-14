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
        Aliases();
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

    static void Aliases()
    {
        string dll = Path.Combine(Path.GetDirectoryName(typeof(LoaderRetargetTest).Assembly.Location), "AliasMod.dll");
        byte[] original = File.ReadAllBytes(dll);
        using (var mod = ModuleDefinition.ReadModule(dll))
        {
            var changes = LoaderRetarget.Apply(mod, typeof(mainscript).Assembly);
            Check("all twelve fields and fourteen method shapes are retargeted", changes.Count == 26);
            Check("aliases are listed with their beta targets", System.Linq.Enumerable.Count(LoaderRetarget.AliasDescriptions()) == 27);
            var rewritten = LoadBytes(mod).GetType("AliasMod");
            var p = new fpscontroller(); var r = new radioscript(); var c = new carscript();
            var m = new mainscript(); var menu = new menuhandler();
            var body = new UnityEngine.Transform(); var data = new DataFromMenuScript(); var settings = new settingsscript();
            var mode = new gamemodesettingsscript.gamemodesettings();
            gamemodesettingsscript.s = new gamemodesettingsscript();
            kaposztaleves.s = new kaposztaleves();
            object[] args = { p, r, c, m, menu, body, data, settings, mode };
            foreach (var operation in new[] { "Write", "Address" })
            {
                p.BodyRot = null; mainscript.s = null; r.Bvolume.tunedLerp = 0; c.lightUsable.currentTurnState = 0;
                c.debugCanvas = null;
                DataFromMenuScript.s = null; settingsscript.s = null; gamemodesettingsscript.s.S = null;
                kaposztaleves.s.settings = new kaposztaleves.devSettings();
                rewritten.GetMethod(operation).Invoke(null, args);
                Check(operation + ": Tb writes BodyRot", p.BodyRot == body);
                Check(operation + ": M writes the current singleton", mainscript.s == m);
                Check(operation + ": v writes the usable's volume", r.Bvolume.tunedLerp == 0.625f);
                Check(operation + ": lampak writes the usable's light state", c.lightUsable.currentTurnState == 2);
                Check(operation + ": KmhObj writes the speed display object", c.debugCanvas == (UnityEngine.GameObject)rewritten.GetField("Display").GetValue(null));
                Check(operation + ": DFMS writes the current singleton", DataFromMenuScript.s == data);
                Check(operation + ": settings writes the current singleton", settingsscript.s == settings);
                Check(operation + ": GSettings writes the live mode", gamemodesettingsscript.s.S == mode);
                Check(operation + ": godmode writes through the beta's struct", kaposztaleves.s.settings.god);
                Check(operation + ": Showkmh writes through the beta's struct", kaposztaleves.s.settings.kmh);
                Check(operation + ": every old field reads live beta storage", (bool)rewritten.GetMethod("Read").Invoke(null, args));
            }
            // Replace the beta's storage after the mod loaded: its reads must follow the new objects.
            body = new UnityEngine.Transform(); data = new DataFromMenuScript(); settings = new settingsscript();
            mode = new gamemodesettingsscript.gamemodesettings(); m = new mainscript();
            p.BodyRot = body; mainscript.s = m; DataFromMenuScript.s = data; settingsscript.s = settings;
            gamemodesettingsscript.s = new gamemodesettingsscript { S = mode };
            args = new object[] { p, r, c, m, menu, body, data, settings, mode };
            Check("reads follow replaced beta storage", (bool)rewritten.GetMethod("Read").Invoke(null, args));
            var h = new headlightscript(); var s = new survivalscript();
            rewritten.GetMethod("Calls").Invoke(null, new object[] { p, r, m, h, s });
            Check("RightHandRelease enables the beta's sync argument", p.Released);
            Check("UpdValues drops the unused public argument", r.Updates == 1);
            Check("PressedEscape uses the game's local input arguments", m.Screenshot && !m.MainMenu);
            Check("both SwitchState overloads call the beta and wrap", h.Calls == 2 && h.switchToState == 0 && h.Sender == null);
            Check("Damage preserves amount and show, supplies no direction", s.Lost == 3 && s.Shown && s.Direction.x == 0 && s.Direction.y == 0 && s.Direction.z == 0);
            Check("DamageInstant preserves amount and show, supplies no direction", s.InstantLost == 4 && !s.InstantShown && s.InstantDirection.x == 0 && s.InstantDirection.y == 0 && s.InstantDirection.z == 0);
            Check("ChGodMode writes the beta's god flag", !kaposztaleves.s.settings.god);
            Check("ChKmh writes the beta's speed display flag", !kaposztaleves.s.settings.kmh);
            Check("FoodCh calls the moved callback with its argument", kaposztaleves.s.Food == 1);
            Check("WaterCh calls the moved callback with its argument", kaposztaleves.s.Water == 2);
            Check("HpCh calls the moved callback with its argument", kaposztaleves.s.Hp == 3);
            Check("PissCh calls the moved callback with its argument", kaposztaleves.s.Piss == 4);
            Check("ShitCh calls the moved callback with its argument", kaposztaleves.s.Shit == 5);
            Check("the alias pass is idempotent", LoaderRetarget.Apply(mod, typeof(mainscript).Assembly).Count == 0);
            string logged = null;
            var loaded = LoaderRetarget.Load(dll, typeof(mainscript).Assembly, new[] { Path.GetDirectoryName(dll) }, line => logged = line);
            Check("Load logs the aliases used and loads the rewritten bytes", string.IsNullOrEmpty(loaded.Location) && logged != null && logged.Contains("radioscript.v -> radioscript.Bvolume.tunedLerp") && logged.Contains("mainscript.FoodCh(System.Single) -> kaposztaleves.s.PressedFoodCh(System.Single)"));
            Check("the public-shaped fixture stays unchanged on disk", System.Linq.Enumerable.SequenceEqual(original, File.ReadAllBytes(dll)));
        }
    }
}
