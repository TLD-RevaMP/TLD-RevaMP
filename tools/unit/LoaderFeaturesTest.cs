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

            Section("LoadedMods, Config at load");
            var loaded = (System.Collections.IList)loader.GetProperty("LoadedMods").GetValue(null);
            Check("ModLoader.LoadedMods has the mod", loaded.Count == 1 && loaded[0].GetType() == modType);
            Check("Config() ran once at load (where SettingAPI reads the saved values)", (int)modType.GetField("ConfigCalls").GetValue(null) == 1 && ModHost.ConfigsAtLoad == 1);
            ModHost.ConfigScreenOpen = false; ModHost.DrawConfigs();
            Check("and not again while no settings window is open", (int)modType.GetField("ConfigCalls").GetValue(null) == 1);
            ModHost.ConfigScreenOpen = true; ModHost.DrawConfigs(); ModHost.ConfigScreenOpen = false;
            Check("it runs again in the settings window", (int)modType.GetField("ConfigCalls").GetValue(null) == 2);

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

            mainscript.s = null; ModHost.Tick();
            Check("back in the menu: isOnMainMenu true again", (bool)loader.GetField("isOnMainMenu").GetValue(null));

            Section("SettingStore: the settings file");
            var storeType = Type.GetType("TLDLoader.SettingStore, TLDLoader");
            string path = Path.Combine(tmp, "Settings.txt");
            var store = Activator.CreateInstance(storeType, path);
            var set = storeType.GetMethod("Set");
            set.Invoke(store, new object[] { "Show Gauge", true });
            set.Invoke(store, new object[] { "Max Speed", 12.5f });
            set.Invoke(store, new object[] { "Mode", 3 });
            set.Invoke(store, new object[] { "Notes", "two\tlines\nand a \\ backslash" });
            var again = Activator.CreateInstance(storeType, path);
            object Get(string t) { var a = new object[] { t, null }; return (bool)storeType.GetMethod("TryGet").Invoke(again, a) ? a[1] : "<none>"; }
            Check("a bool comes back a bool", Get("Show Gauge") is bool b && b);
            Check("a float comes back exactly", Get("max speed") is float f && f == 12.5f);
            Check("an int comes back an int", Get("Mode") is int i && i == 3);
            Check("a string with tabs, newlines and backslashes comes back exactly", (string)Get("Notes") == "two\tlines\nand a \\ backslash");
            Check("the id is the title lower case with '_' for spaces", File.ReadAllText(path).Contains("show_gauge\t"));
            var written = File.GetLastWriteTimeUtc(path);
            System.Threading.Thread.Sleep(30);
            set.Invoke(again, new object[] { "Show Gauge", true });
            Check("setting the same value doesn't rewrite the file", File.GetLastWriteTimeUtc(path) == written);
            File.WriteAllText(path, "garbage line\nshow_gauge\tbool\tnotabool\nok\tint\t7\n");
            var odd = Activator.CreateInstance(storeType, path);
            var a2 = new object[] { "ok", null };
            Check("a damaged file: bad lines skipped, good ones read", (bool)storeType.GetMethod("TryGet").Invoke(odd, a2) && (int)a2[1] == 7);
        }
        catch (Exception e) { Check("no exception: " + e, false); }
        return Done(tmp);
    }

    static int Fnv1(string s) { unchecked { int h = -2128831035; foreach (char c in s) { h ^= c; h *= 16777619; } return h; } }

    static int Done(string tmp)
    {
        try { Directory.Delete(tmp, true); } catch { }
        Console.WriteLine(failures == 0 ? "ALL PASS" : failures + " FAILED");
        return failures == 0 ? 0 : 1;
    }
}
