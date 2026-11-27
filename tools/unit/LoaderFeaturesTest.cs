using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using TLDRevamp;

/// TLD Loader's features beyond the lifecycle, run for real: the REAL src/TLDRevamp/ModHost.cs and the REAL shim
/// (src/TLDLoaderShim) with a fixture mod (fixtures/loader/FeaturesMod) that uses them. Covers:
/// - a DLL with a type that can't load (its base class is in a DLL that isn't there) still loads its mod — before
///   2026-11-11 the whole DLL was skipped, and on the game's runtime a throwing type check stopped every mod;
/// - ModLoader.LoadedMods, isOnMainMenu, GetCurrentScene, Mod.Category (TLD Loader's FNV-1 of the ID);
/// - UseLogger: the mod's own log file with each level;
/// - Config() once at load (settings are read there), then only in the settings window;
/// - SettingStore, the settings file behind SettingAPI.
static class LoaderFeaturesTest
{
    static int failures;
    static void Check(string name, bool ok) { Console.WriteLine((ok ? "PASS " : "FAIL ") + name); if (!ok) failures++; }
    static void Section(string s) { Console.WriteLine("--- " + s); }

    static int Main()
    {
        string tmp = Path.Combine(Path.GetTempPath(), "loaderfeatures-" + Guid.NewGuid().ToString("N"));
        string modsDir = Path.Combine(tmp, "BepInEx", "TLDLoaderMods");
        Directory.CreateDirectory(modsDir);
        Directory.CreateDirectory(Path.Combine(tmp, "BepInEx", "config"));
        string here = Path.GetDirectoryName(typeof(LoaderFeaturesTest).Assembly.Location);
        foreach (var f in new[] { "FeaturesMod.dll" })
        {
            if (!File.Exists(Path.Combine(here, f))) { Console.Error.WriteLine(f + " is missing from the test output; rebuild LoaderFeaturesTest.csproj"); return 2; }
            File.Copy(Path.Combine(here, f), Path.Combine(modsDir, f), true);
        }
        Check("the fixture's MissingDep.dll is NOT shipped (its type must fail to load)", !File.Exists(Path.Combine(here, "MissingDep.dll")) && !File.Exists(Path.Combine(modsDir, "MissingDep.dll")));
        BepInEx.Paths.BepInExRootPath = Path.Combine(tmp, "BepInEx");
        BepInEx.Paths.ConfigPath = Path.Combine(tmp, "BepInEx", "config");
        UnityEngine.SceneManagement.SceneManager.ActiveSceneName = "menu";
        mainscript.s = null;
        try
        {
            Section("a DLL with a type that can't load still loads its mod");
            ModHost.Start();
            string st = ModHost.Status();
            Check("the mod loaded (1 mod)", st.Contains("1 mod(s)") && st.Contains("Features Mod"));
            Check("the host did not fail", !st.Contains("\"info\":\"failed"));

            var modType = Type.GetType("FeaturesMod, FeaturesMod");
            var loader = Type.GetType("TLDLoader.ModLoader, TLDLoader");
            Check("the fixture and the loader are loaded", modType != null && loader != null);
            if (modType == null || loader == null) return Done(tmp);
            var seen = (List<string>)modType.GetField("Seen").GetValue(null);

            bool threw = false;
            try { modType.Assembly.GetTypes(); } catch (ReflectionTypeLoadException) { threw = true; }
            Check("(the fixture's DLL really has a type that fails to load)", threw);

            Check("a reference to a DLL in the mods folder gets that DLL, the one already loaded (one mod's field of another mod's type)",
                  ModHost.ModAssemblyFor("FeaturesMod, Version=0.0.0.0, Culture=neutral, PublicKeyToken=null") == modType.Assembly);
            Check("one that isn't there gets nothing from the host", ModHost.ModAssemblyFor("MissingDep") == null);

            Section("LoadedMods, Config at load");
            var loaded = (System.Collections.IList)loader.GetProperty("LoadedMods").GetValue(null);
            Check("ModLoader.LoadedMods has the mod", loaded.Count == 1 && loaded[0].GetType() == modType);
            Check("Config() ran once at load (where SettingAPI reads the saved values)", (int)modType.GetField("ConfigCalls").GetValue(null) == 1 && ModHost.ConfigsAtLoad == 1);
            ModHost.CloseConfig(); ModHost.DrawConfigs();
            Check("and not again while no settings window is open", (int)modType.GetField("ConfigCalls").GetValue(null) == 1);
            var info = ModHost.LoaderMods().Find(x => x.Id == "features.mod");
            Check("the mods list sees it, with a Settings window (its Config() has code) and its author", info != null && info.HasSettings && info.Author != null);
            Check("its window opens", ModHost.OpenConfig(info.Id) && ModHost.ConfigScreenOpen && ModHost.ConfigOpenFor == info.Id);
            ModHost.DrawConfigs(); ModHost.CloseConfig();
            Check("it runs again in its settings window, and only there", (int)modType.GetField("ConfigCalls").GetValue(null) == 2 && !ModHost.ConfigScreenOpen);
            Check("a mod that isn't there has no window", !ModHost.OpenConfig("no.such.mod"));

            Section("isOnMainMenu and the scene follow the world");
            ModHost.Tick();   // the menu: OnMenuLoad
            Check("in the menu: isOnMainMenu true, scene MainMenu", seen.Contains("menu:True:MainMenu"));
            mainscript.s = new mainscript { player = new TLDRevamp.Fixtures.Player() };
            ModHost.Tick();   // the world: OnLoad
            Check("in the world: isOnMainMenu false, scene Game", seen.Contains("menu:False:Game"));
            Check("the mod finds itself in LoadedMods", seen.Contains("loaded:1:me"));
            Check("Category is TLD Loader's FNV-1 of the ID", seen.Contains("category:" + Fnv1("features.mod")));

            Section("UseLogger: the mod's own log file");
            string logDir = Path.Combine(modsDir, "Logs", "features.mod");
            var files = Directory.Exists(logDir) ? Directory.GetFiles(logDir, "*.log") : new string[0];
            Check("a log file in Logs/<ID>", files.Length == 1 && Path.GetFileName(files[0]).StartsWith("features.mod_"));
            string log = files.Length == 1 ? File.ReadAllText(files[0]) : "";
            Check("it starts with the mod's name and version", log.Contains("Features Mod v2.0 started"));
            Check("each level is written", log.Contains("[Warning] a warning from the mod") && log.Contains("[Debug] a debug line") && log.Contains("[Critical] a critical line"));
            var mod = loaded[0];
            Check("the mod's Logger is TLDLoader.Logger", mod.GetType().BaseType.GetProperty("Logger").GetValue(mod).GetType().FullName == "TLDLoader.Logger");

            Section("a world replaced between two ticks (a save loaded from inside a world)");
            int loads = seen.Count(x => x.StartsWith("loaded:")), unloads = seen.Count(x => x == "unload");
            mainscript.s = new mainscript { player = new TLDRevamp.Fixtures.Player() };   // a new world object, a player already
            ModHost.Tick();
            Check("the old world's mods get OnUnload", seen.Count(x => x == "unload") == unloads + 1);
            Check("and OnLoad again for the new world", seen.Count(x => x.StartsWith("loaded:")) == loads + 1);
            ModHost.Tick();
            Check("the same world on the next tick: nothing again", seen.Count(x => x.StartsWith("loaded:")) == loads + 1 && seen.Count(x => x == "unload") == unloads + 1);

            mainscript.s = null; ModHost.Tick();
            Check("back in the menu: isOnMainMenu true again", (bool)loader.GetField("isOnMainMenu").GetValue(null));

            Section("mainscript.M.load: the world came from a save (latched; the game clears its own flag on the first frame)");
            bool pending = false, fromSave = false, up = false;
            // one look per frame: (world up now, load flag or SetLoad hook seen this frame) -> what a mod's OnLoad would read
            bool Look(bool worldUp, bool loadSeen, bool joiner = false) { bool r = ModHost.FromSaveLatch(up, worldUp, loadSeen, joiner, ref pending, ref fromSave); up = worldUp; return r; }
            Look(false, false);
            Check("new game: false at OnLoad", !Look(true, false));
            Look(false, false);                      // that world ends
            Look(false, true);                       // a save chosen in the menu
            Check("loaded save: true at OnLoad though the game's flag is already cleared", Look(true, false));
            Check("still true later in that world", Look(true, false));
            Look(false, false);                      // it ends
            Check("a new game after a loaded one: false again", !Look(true, false));
            Look(true, true);                        // a save chosen from inside the world (the flag set while it is up)
            Look(false, false);                      // the world goes down; the game cleared its flag already
            Check("a save loaded from inside a world: true", Look(true, false));
            Look(false, false);
            Check("and the next new game: false", !Look(true, false));
            Look(false, false);
            Check("a joiner (the host's world, never new): true even with no load seen", Look(true, false, joiner: true));

            Section("SettingStore: the settings file, in TLD Loader 3.0.3's format");
            var storeType = Type.GetType("TLDLoader.SettingStore, TLDLoader");
            string path = Path.Combine(tmp, "Settings.json");
            object NewStore(string p, string import = null, string legacy = null) => Activator.CreateInstance(storeType, p, new Type[0], import, legacy);
            var store = NewStore(path);
            var set = storeType.GetMethod("Set");
            set.Invoke(store, new object[] { "Show Gauge", true });
            set.Invoke(store, new object[] { "Max Speed", 12.5f });
            set.Invoke(store, new object[] { "Mode", 3 });
            set.Invoke(store, new object[] { "Notes", "two\tlines\nand a \\ backslash \"quoted\"" });
            object GetFrom(object st, string t) { var a = new object[] { t, null }; return (bool)storeType.GetMethod("TryGet").Invoke(st, a) ? a[1] : "<none>"; }
            var again = NewStore(path);
            object Get(string t) => GetFrom(again, t);
            Check("a bool comes back a bool", Get("Show Gauge") is bool b && b);
            Check("a float comes back as its number", Convert.ToSingle(Get("max speed")) == 12.5f);
            Check("an int comes back an int", Get("Mode") is int i && i == 3);
            Check("a string with tabs, newlines, quotes and backslashes comes back exactly", (string)Get("Notes") == "two\tlines\nand a \\ backslash \"quoted\"");
            string json = File.ReadAllText(path);
            Check("TLD Loader's shape: {\"settings\":[{\"ID\":..,\"Value\":..}]}", json.StartsWith("{\"settings\":[{") && json.Contains("{\"ID\":\"show_gauge\",\"Value\":true}"));
            Check("the id is the title lower case with '_' for spaces", json.Contains("\"ID\":\"max_speed\""));
            var written = File.GetLastWriteTimeUtc(path);
            System.Threading.Thread.Sleep(30);
            set.Invoke(again, new object[] { "Show Gauge", true });
            Check("setting the same value doesn't rewrite the file", File.GetLastWriteTimeUtc(path) == written);

            // a file as TLD Loader 3.0.3 writes it (DataContractJsonSerializer over its Settings/Setting classes)
            string theirs = Path.Combine(tmp, "theirs.json");
            File.WriteAllText(theirs, "{\"settings\":[{\"ID\":\"enable_hud\",\"Value\":false},{\"ID\":\"volume\",\"Value\":0.75},{\"ID\":\"preset\",\"Value\":2},{\"ID\":\"name\",\"Value\":\"car\"}]}");
            var read = NewStore(theirs);
            Check("a TLD Loader file reads: bool, float, int and text", GetFrom(read, "Enable HUD") is bool hb && !hb && Convert.ToDouble(GetFrom(read, "Volume")) == 0.75
                && Convert.ToInt32(GetFrom(read, "Preset")) == 2 && (string)GetFrom(read, "Name") == "car");

            // a player's settings from TLD Loader's own folder, the first time; that file is never written
            string ours = Path.Combine(tmp, "imported", "Settings.json"), theirsBefore = File.ReadAllText(theirs);
            var imported = NewStore(ours, theirs);
            Check("TLD Loader's settings taken over the first time", (string)storeType.GetField("Source").GetValue(imported) == "import" && File.Exists(ours)
                && Convert.ToDouble(GetFrom(NewStore(ours), "Volume")) == 0.75);
            Check("TLD Loader's own file left as it was", File.ReadAllText(theirs) == theirsBefore);
            Check("once ours exists, it is ours that counts", (string)storeType.GetField("Source").GetValue(NewStore(ours, theirs)) == "file");

            // RevaMP's own settings file before 2026-11-27
            string legacyDir = Path.Combine(tmp, "legacy"); Directory.CreateDirectory(legacyDir);
            File.WriteAllText(Path.Combine(legacyDir, "Settings.txt"), "garbage line\nshow_gauge\tbool\ttrue\nmode\tint\t7\nkey\tkey\t32\nnote\tstring\ta\\tb\n");
            var moved = NewStore(Path.Combine(legacyDir, "Settings.json"), null, Path.Combine(legacyDir, "Settings.txt"));
            Check("RevaMP's older Settings.txt carried over (bad lines skipped)", (string)storeType.GetField("Source").GetValue(moved) == "legacy"
                && GetFrom(moved, "Show Gauge") is bool lb && lb && Convert.ToInt32(GetFrom(moved, "Mode")) == 7 && (string)GetFrom(moved, "Note") == "a\tb"
                && File.Exists(Path.Combine(legacyDir, "Settings.json")));

            File.WriteAllText(path, "{ not json");
            var odd = NewStore(path);
            Check("a damaged file: nothing read, no exception, and a change writes a good file again",
                GetFrom(odd, "Show Gauge") is string none && none == "<none>" && ((Func<bool>)(() => { set.Invoke(odd, new object[] { "x", 1 }); return File.ReadAllText(path).Contains("\"ID\":\"x\""); }))());

            Section("TLD Loader 3.0.3's item registry: the places in the beta's item table (TLDLoader.ItemSlots)");
            Registry(loader.Assembly, Path.Combine(here, "TLDLoader.dll"));
        }
        catch (Exception e) { Check("no exception: " + e, false); }
        return Done(tmp);
    }

    /// A mod's registered items get places in the beta's item table: what the save keeps (tosaveitemscript.id) and what
    /// the game spawns from (itemdatabase.Item). The same registrations must get the same places in every session and on
    /// every machine, two items that hash alike must both get one, and a mod reads its own numbers back from a place.
    static void Registry(Assembly stub, string stubFile)
    {
        var slots = stub.GetType("TLDLoader.ItemSlots");
        var assign = slots.GetMethod("Assign"); var find = slots.GetMethod("Find"); var tryKey = slots.GetMethod("TryKey");
        var hash = slots.GetMethod("Hash"); var clear = slots.GetMethod("Clear");
        int first = (int)slots.GetField("First").GetValue(null), count = (int)slots.GetField("Count").GetValue(null);
        int Assign(int c, int i) => (int)assign.Invoke(null, new object[] { c, i });
        int Find(int c, int i) => (int)find.Invoke(null, new object[] { c, i });
        int Hash(int c, int i) => (int)hash.Invoke(null, new object[] { c, i });
        (bool, int, int) Key(int slot) { var a = new object[] { slot, 0, 0 }; bool ok = (bool)tryKey.Invoke(null, a); return (ok, (int)a[1], (int)a[2]); }
        void Clear() => clear.Invoke(null, null);

        Check("the places start above the beta's own items (up to 9400 in 2026-11)", first > 9400 && count > 0);
        int modA = Fnv1("example.items.a"), modB = Fnv1("example.items.b");
        Clear();
        int a = Assign(modA, 1), b = Assign(modA, 2), c = Assign(modB, 1);
        Check("each item a place of its own, in the range", a != b && b != c && a != c && new[] { a, b, c }.All(x => x >= first && x < first + count));
        Check("a place is the hash's when nothing was there", a == first + Hash(modA, 1));
        Check("asking again gives the same place", Assign(modA, 1) == a && Find(modA, 2) == b);
        Check("an item never registered has none", Find(modA, 3) == -1);
        var k = Key(b);
        Check("the place gives the mod's category and its own id back", k.Item1 && k.Item2 == modA && k.Item3 == 2);
        int empty = Enumerable.Range(first, 4).First(x2 => x2 != a && x2 != b && x2 != c);
        Check("a place nothing registered gives nothing, nor a game item's id", !Key(empty).Item1 && !Key(116).Item1);
        Check("a negative category comes back as it was", Key(Assign(-5, 7)).Item2 == -5 && Key(Find(-5, 7)).Item3 == 7);
        Clear();
        Check("a new item database: nothing registered", Find(modA, 1) == -1 && !Key(a).Item1);
        int c2 = Assign(modB, 1), a2 = Assign(modA, 1), b2 = Assign(modA, 2);
        Check("the same items get the same places in another session, whatever order they come in", a2 == a && b2 == b && c2 == c);
        Check("the hash is a fixed function (another machine: the same places)", Hash(0, 0) == (int)(Fnv1aZeroes() % (uint)count));
        // two items with the same hash: the second takes the next free place
        Clear();
        int x = 1, y = 2;
        var seen = new Dictionary<int, int>();
        for (int i = 1; i < 200000; i++) { int h = Hash(modB, i); if (seen.TryGetValue(h, out int j)) { x = j; y = i; break; } seen[h] = i; }
        int px = Assign(modB, x), py = Assign(modB, y);
        Check("two items whose hashes collide (" + x + ", " + y + ") both get a place, the second the next one", Hash(modB, x) == Hash(modB, y) && px != py && py == first + (px - first + 1) % count);
        Clear();

        // items a mod puts in the table itself (ModHost moves them here, by mod ID, name and how many of that name before)
        var appended = slots.GetMethod("AssignAppended");
        int Appended(string mod, string name, int n) => (int)appended.Invoke(null, new object[] { mod, name, n });
        int s1 = Appended("example.snow", "Snowman", 0), s2 = Appended("example.snow", "Snowman", 1), s3 = Appended("example.nitro", "Snowman", 0);
        int r1 = Assign(modA, 1);
        Check("an appended item gets a place in the range, each its own (another of that name, another mod's of that name, a registered one)",
              new[] { s1, s2, s3 }.All(v => v >= first && v < first + count) && new[] { s1, s2, s3, r1 }.Distinct().Count() == 4);
        Check("asking again gives the same place", Appended("example.snow", "Snowman", 0) == s1);
        Check("an appended item's place is not a registered item's (a mod reads its id there as it is)", !Key(s1).Item1 && Key(r1).Item1);
        Clear();
        int t3 = Appended("example.nitro", "Snowman", 0), r2 = Assign(modA, 1), t1 = Appended("example.snow", "Snowman", 0), t2 = Appended("example.snow", "Snowman", 1);
        Check("the same places in another session, with the mods in another order", t1 == s1 && t2 == s2 && t3 == s3 && r2 == r1);
        Clear();
        int u1 = Appended("example.snow", "Snowman", 0);
        Check("the same place without the other mods (another mod coming or going moves nothing)", u1 == s1 && !Key(u1).Item1);
        Clear();

        // the members mods built on TLD Loader 3.0.3 bind to, as they reference them (a missing one is a
        // MissingMethodException when the mod's method runs: before 2026-11-24, get_Instance, and nothing was registered)
        using var m = Mono.Cecil.ModuleDefinition.ReadModule(stubFile);
        if (!m.AssemblyReferences.Any(r => r.Name == "UnityEngine.CoreModule")) { Console.WriteLine("SKIP the registry's members: the stub was built without Unity (no lib/)"); return; }
        var have = new HashSet<string>(m.Types.SelectMany(t => t.Methods.Select(x2 => x2.FullName).Concat(t.Fields.Select(f => f.FullName))));
        foreach (var want in new[]
        {
            "TLDLoader.ModLoader TLDLoader.ModLoader::get_Instance()",
            "TLDLoader.ItemDatabase TLDLoader.ModLoader::get_Database()",
            "System.Boolean TLDLoader.ModLoader::dbmodsloaded",
            "TLDLoader.ItemRegistrationBuilder TLDLoader.Mod::RegisterItem(UnityEngine.GameObject,System.Int32,System.String)",
            "TLDLoader.ItemRegistrationBuilder TLDLoader.Mod::RegisterItem(UnityEngine.GameObject,System.Int32)",
            "UnityEngine.GameObject TLDLoader.Mod::GetItem(System.Int32)",
            "System.Int32 TLDLoader.Mod::get_Category()",
            "System.Void TLDLoader.ItemRegistrationBuilder::Register()",
            "TLDLoader.ItemRegistrationBuilder TLDLoader.ItemRegistrationBuilder::WithRigidbody(System.Single,System.Single,System.Single)",
            "TLDLoader.ItemRegistrationBuilder TLDLoader.ItemRegistrationBuilder::AsPickupable(TLDLoader.PickupableOptions)",
            "TLDLoader.ItemRegistrationBuilder TLDLoader.ItemRegistrationBuilder::AsTank(mainscript/fluidcontainer,System.Boolean,System.Boolean)",
            "System.Void TLDLoader.PickupableOptions::.ctor()",
            "System.Void TLDLoader.PickupableOptions::set_Attachable(System.Boolean)",
            "System.Void TLDLoader.PickupableOptions::set_CanEquip(System.Boolean)",
            "System.Void TLDLoader.PickupableOptions::set_CanInventory(System.Boolean)",
            "UnityEngine.GameObject TLDLoader.ItemDatabase::GetById(System.Int32,System.Int32)",
        })
            Check("the stub has " + want, have.Contains(want));
        var ml = m.Types.First(t => t.FullName == "TLDLoader.ModLoader");
        Check("ModLoader is the loader's component, as in 3.0.3 (mods cast Instance to a Unity object), and not static",
              ml.BaseType?.FullName == "UnityEngine.MonoBehaviour" && !(ml.IsAbstract && ml.IsSealed));
    }

    /// FNV-1a of eight zero bytes: the hash of (0, 0) as TLDLoader.ItemSlots computes it, worked out here on its own.
    static uint Fnv1aZeroes() { unchecked { uint h = 2166136261; for (int i = 0; i < 8; i++) { h ^= 0; h *= 16777619; } return h; } }

    static int Fnv1(string s) { unchecked { int h = -2128831035; foreach (char c in s) { h ^= c; h *= 16777619; } return h; } }

    static int Done(string tmp)
    {
        try { Directory.Delete(tmp, true); } catch { }
        Console.WriteLine(failures == 0 ? "ALL PASS" : failures + " FAILED");
        return failures == 0 ? 0 : 1;
    }
}
