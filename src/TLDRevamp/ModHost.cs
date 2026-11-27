using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEngine;

namespace TLDRevamp
{
    /// Runs TLDLoader mods under BepInEx on the beta (TLDLoader itself doesn't work there). The mods' DLLs go in
    /// BepInEx/TLDLoaderMods/; the stub TLDLoader.dll next to our plugin provides the API they reference (Mod, its
    /// logger, the config folder). Lifecycle as TLDLoader's: OnMenuLoad in the main menu (mods with LoadInMenu), OnLoad
    /// once a game world is up (the player exists), OnUnload when that world ends, Config for the mod's own settings
    /// window, and Update/FixedUpdate/OnGUI for mods that override them.
    ///
    /// Everything goes through reflection: TLDRevamp has no compile-time reference to the stub, so a missing stub or a
    /// broken mod can never stop TLDRevamp's own types (and patches) from loading.
    public static partial class ModHost
    {
        private sealed class Loaded
        {
            public object Mod; public string Id, Name, Version, File, Hash;   // Hash: SHA-256 of the DLL (the join check)
            public string Author;   // as the mod says (the mods list shows it)
            public bool? HasSettings;   // its Config() has code (ModHost.HasSettings; read once)
            public MethodInfo OnMenuLoad, OnLoad, OnUnload, Config, Update, FixedUpdate, OnGUI;   // null: not overridden
            public MethodInfo DbLoad, DbLoadOld;   // with LoadInDB: DbLoad and the older dbLoad, both as TLDLoader calls them
            public bool DbLoadInWorld;   // its DbLoad threw in the menu: again when the world is up
            public string LastError;
            public bool Menu;   // LoadInMenu: its Update and OnGUI run in the menu too
            public int BrokenTypes;   // types in its DLL that failed to load on the beta (UseHarmony is not applied then)
        }

        private static readonly List<Loaded> Mods = new List<Loaded>();
        private static bool _started, _worldUp, _gameLoadedCalled, _menuCalled, _unloadCalled;
        private static object _worldObj;
        public static int WorldsReplaced;
        public static string Info = "not started";
        public static int DbLoadRuns;
        /// Whether any TLD Loader mod is loaded (LoaderCompat's per-frame work is for theirs only).
        internal static bool AnyLoaderMods => Mods.Count > 0;
        private static itemdatabase _dbFor;
        private static object _aliasM, _aliasD, _aliasSd;

        public static string ModsDir => Path.Combine(BepInEx.Paths.BepInExRootPath, "TLDLoaderMods");

        public static void Start()
        {
            if (_started) return;
            _started = true;
            try
            {
                if (!Directory.Exists(ModsDir)) { Info = "no " + ModsDir; return; }
                var dlls = Directory.GetFiles(ModsDir, "*.dll");
                if (dlls.Length == 0) { Info = "no mods in " + ModsDir; return; }
                string stubPath = Path.Combine(Path.GetDirectoryName(typeof(ModHost).Assembly.Location), "TLDLoader.dll");
                if (!File.Exists(stubPath)) { Info = "stub TLDLoader.dll missing next to TLDRevamp.dll"; Plugin.Log.LogWarning("ModHost: " + Info); return; }
                var stub = Assembly.LoadFrom(stubPath);
                var modBase = stub.GetType("TLDLoader.Mod");
                stub.GetType("TLDLoader.ModLoader").GetField("ConfigRoot").SetValue(null, Path.Combine(BepInEx.Paths.ConfigPath, "TLDLoaderMods"));
                stub.GetType("TLDLoader.ModLoader").GetField("ModsFolder")?.SetValue(null, ModsDir);   // assets: <ModsDir>/Assets/<ID>
                var hostLogger = stub.GetType("TLDLoader.HostLogger");
                Loader = stub.GetType("TLDLoader.ModLoader");
                var gameLog = BepInEx.Logging.Logger.CreateLogSource("TLD Loader mods");
                Loader.GetField("Echo")?.SetValue(null, (Action<string, int>)((m, lv) => { if (lv == 2) gameLog.LogError(m); else if (lv == 1) gameLog.LogWarning(m); else gameLog.LogInfo(m); }));
                MakeLoadingPanel();
                MakeLoaderObject(stub);
                BindEvents(stub);
                _modFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var dll in dlls)
                    try { _modFiles[AssemblyName.GetAssemblyName(dll).Name] = dll; }
                    catch (Exception e) { Plugin.Log.LogWarning("ModHost: " + Path.GetFileName(dll) + ": no assembly name: " + e.Message); }
                AppDomain.CurrentDomain.AssemblyResolve += (s, e) => ModAssemblyFor(e.Name);
                foreach (var dll in dlls)
                {
                    // A DLL whose types partly fail to load (one names a game type the beta lacks) still offers the rest:
                    // before 2026-11-11 the whole DLL was skipped, and a type whose check threw stopped every mod after it
                    // (an item mod: "Could not load type of field" for its spawner's building - no Loader mod loaded).
                    Type[] types; int broken = 0;
                    try { types = ModAssembly(dll).GetTypes(); }
                    catch (ReflectionTypeLoadException e)
                    {
                        types = Array.FindAll(e.Types ?? new Type[0], x => x != null);
                        broken += (e.Types?.Length ?? 0) - types.Length;
                        Plugin.Log.LogWarning("ModHost: " + Path.GetFileName(dll) + ": " + ((e.Types?.Length ?? 0) - types.Length) + " type(s) failed to load, the other " + types.Length + " are used: " + (e.LoaderExceptions.Length > 0 ? e.LoaderExceptions[0]?.Message : ""));
                    }
                    catch (Exception e) { Plugin.Log.LogError("ModHost: " + Path.GetFileName(dll) + " failed to load: " + e.Message); continue; }
                    // every type checked first, so a mod knows how many of its DLL's types failed before it starts
                    var modTypes = new List<Type>();
                    foreach (var t in types)
                    {
                        try { if (!t.IsAbstract && modBase.IsAssignableFrom(t)) modTypes.Add(t); }
                        catch (Exception e) { broken++; Plugin.Log.LogWarning("ModHost: " + Path.GetFileName(dll) + ": type " + t.FullName + " skipped: " + e.Message); }
                    }
                    foreach (var t in modTypes)
                    {
                        try
                        {
                            var mod = Activator.CreateInstance(t);
                            var l = new Loaded { Mod = mod, File = Path.GetFileName(dll), BrokenTypes = broken, Hash = FileHash(dll) };
                            l.Id = (string)modBase.GetProperty("ID").GetValue(mod);
                            l.Name = (string)modBase.GetProperty("Name").GetValue(mod);
                            l.Version = (string)modBase.GetProperty("Version").GetValue(mod);
                            try { l.Author = modBase.GetProperty("Author")?.GetValue(mod) as string; } catch { }   // the list's "by"
                            var log = BepInEx.Logging.Logger.CreateLogSource(l.Id);
                            // UseLogger: the mod's own log file (TLDLoader.Logger, echoed to BepInEx's log); otherwise BepInEx's
                            object logger = null;
                            if (Flag(modBase, mod, "UseLogger"))
                                try { logger = Activator.CreateInstance(stub.GetType("TLDLoader.Logger"), mod); }
                                catch (Exception e) { Plugin.Log.LogWarning("ModHost: " + l.Id + ": its log file: " + (e.InnerException ?? e).Message); }
                            modBase.GetProperty("Logger").SetValue(mod, logger ?? Activator.CreateInstance(hostLogger,
                                (Action<string>)(m => log.LogInfo(m)), (Action<string>)(m => log.LogWarning(m)), (Action<string>)(m => log.LogError(m))));
                            Loader.GetMethod("HostAdd", BindingFlags.NonPublic | BindingFlags.Static)?.Invoke(null, new[] { mod });
                            bool menu = (bool)modBase.GetProperty("LoadInMenu").GetValue(mod);
                            // Every lifecycle method is bound the same way: the mod's own
                            // override, or nothing.  (OnMenuLoad additionally needs the mod
                            // to ask for it with LoadInMenu.)  The base class's methods are
                            // empty, so calling them would be a no-op; not binding them
                            // keeps "did the mod override it" answerable.
                            l.Menu = menu;
                            l.OnMenuLoad = menu ? Overridden(t, modBase, "OnMenuLoad") : null;
                            l.OnLoad = Overridden(t, modBase, "OnLoad") ?? modBase.GetMethod("OnLoad");
                            l.OnUnload = Overridden(t, modBase, "OnUnload");
                            l.Config = Overridden(t, modBase, "Config");
                            l.Update = Overridden(t, modBase, "Update");
                            l.FixedUpdate = Overridden(t, modBase, "FixedUpdate");
                            l.OnGUI = Overridden(t, modBase, "OnGUI");
                            if (modBase.GetProperty("LoadInDB") is PropertyInfo inDb && (bool)inDb.GetValue(mod))
                            {
                                l.DbLoad = Overridden(t, modBase, "DbLoad");
                                l.DbLoadOld = Overridden(t, modBase, "dbLoad");
                            }
                            Mods.Add(l);
                            Plugin.Log.LogInfo($"ModHost: loaded {l.Name} {l.Version} ({l.Id}) from {l.File}");
                            StartLikeLoader(l, modBase);
                        }
                        catch (Exception e) { Plugin.Log.LogError("ModHost: creating " + t.FullName + " failed: " + (e.InnerException ?? e)); }
                    }
                }
                Info = Mods.Count + " mod(s)";
                DevHooks.ModsLoaded?.Invoke();   // test tooling: a local autopilot test mod made aware of other players
            }
            catch (Exception e) { Info = "failed: " + e.Message; Plugin.Log.LogError("ModHost: " + e); }
        }

        /// A/B (config Mods.RetargetLoaderMods, read before the mods load): false = before 2026-11-11, every mod loaded
        /// as it is, without LoaderRetarget's integer-sign fixes or public-name adapters.
        public static bool Retarget = true;
        /// A/B (config Mods.LoaderCompat, read before the mods load): false = before 2026-11-23, no LoaderCompat helpers for
        /// the public game's save ids and attach members (issue 44).
        public static bool Compat = true;
        public static int Retargeted;
        static partial void CompatType(ref Type type);   // ModHost.Game.cs: LoaderCompat
        private static Type CompatHelpers() { Type t = null; if (Compat) CompatType(ref t); return t; }

        private static Assembly LoadMod(string dll)
        {
            if (!Retarget) return Assembly.LoadFrom(dll);
            var asm = LoaderRetarget.Load(dll, typeof(mainscript).Assembly,
                new[] { ModsDir, BepInEx.Paths.ManagedPath, BepInEx.Paths.BepInExAssemblyDirectory, Path.GetDirectoryName(typeof(ModHost).Assembly.Location) },
                m => Plugin.Log.LogInfo("ModHost: " + m), CompatHelpers());
            if (string.IsNullOrEmpty(asm.Location)) Retargeted++;
            return asm;
        }
        /// Mods that use another mod's types (one mod's field of an AI mod's type): a rewritten mod loads from bytes, and
        /// the runtime finds neither a mod loaded that way nor one not loaded yet (the DLLs load in name order, that mod
        /// first). A reference to another DLL in the mods folder gets that DLL, loaded once, here or in the loop.
        /// Before 2026-11-24 that mod's mod type failed to load.
        private static Dictionary<string, string> _modFiles;   // assembly name -> DLL
        private static readonly Dictionary<string, Assembly> _modAssemblies = new Dictionary<string, Assembly>(StringComparer.OrdinalIgnoreCase);
        internal static Assembly ModAssemblyFor(string name)
        {
            if (_modFiles == null || string.IsNullOrEmpty(name)) return null;
            string simple;
            try { simple = new AssemblyName(name).Name; } catch { return null; }
            return _modFiles.TryGetValue(simple, out var dll) ? ModAssembly(dll) : null;
        }
        private static Assembly ModAssembly(string dll)
        {
            lock (_modAssemblies)
            {
                if (_modAssemblies.TryGetValue(dll, out var known)) return known;   // null while it loads: a cycle gets nothing
                _modAssemblies[dll] = null;
                try { var asm = Initialized(LoadMod(dll), dll); _modAssemblies[dll] = asm; return asm; }
                catch { _modAssemblies.Remove(dll); throw; }
            }
        }
        /// The DLL's module initializer first. Mods built with Costura carry their libraries inside (Newtonsoft.Json, NLayer:
        /// five of the workshop's mods) and hook the resolver there; the type
        /// check below otherwise found their types unloadable before any of the mod's code ran, and they stayed so.
        private static Assembly Initialized(Assembly asm, string dll)
        {
            try { System.Runtime.CompilerServices.RuntimeHelpers.RunModuleConstructor(asm.ManifestModule.ModuleHandle); }
            catch (Exception e) { Plugin.Log.LogWarning("ModHost: " + Path.GetFileName(dll) + ": its module initializer: " + (e.InnerException ?? e).Message); }
            KnownModFixes(asm);
            return asm;
        }
        private static Type Loader;
        private static MethodInfo _hostScene;
        public static int HarmonyPatched, ConfigsAtLoad, ConfigsAtLoadFailed;

        private static bool Flag(Type modBase, object mod, string name)
        {
            try { return modBase.GetProperty(name) is PropertyInfo p && (bool)p.GetValue(mod); }
            catch { return false; }
        }

        /// What TLD Loader does with a mod right after creating it, in its order: Config() once with the settings window
        /// closed (a SettingAPI mod reads its saved values there; a mod that draws directly throws, as under TLD Loader,
        /// and is told once), then UseHarmony (ModHost.Game.cs: guarded, not TLD Loader's plain PatchAll). OnMenuLoad comes later.
        private static void StartLikeLoader(Loaded l, Type modBase)
        {
            var pass = Loader.GetMethod("HostConfigPass", BindingFlags.NonPublic | BindingFlags.Static);
            if (l.Config != null && pass != null)
            {
                pass.Invoke(null, new[] { l.Mod, (object)false });
                try { l.Config.Invoke(l.Mod, null); ConfigsAtLoad++; }
                catch (Exception e) { ConfigsAtLoadFailed++; Plugin.Log.LogInfo("ModHost: " + l.Id + ": its Config() at load (settings not read there): " + (e.InnerException ?? e).Message); }
                finally { pass.Invoke(null, new[] { l.Mod, (object)true }); }
            }
            if (Flag(modBase, l.Mod, "UseHarmony")) PatchModSafely(l);   // ModHost.Game.cs: per patch class, checked first
        }

        // the game-only parts (ModHost.Game.cs; not in the unit test's build): the loading panel and TLD Loader's events
        static partial void MakeLoadingPanel();
        static partial void MakeLoaderObject(Assembly stub);   // ModHost.Game.cs: ModLoader.Instance, the item registry
        static partial void BindEvents(Assembly stub);
        static partial void PatchModSafely(Loaded l);
        static partial void KnownModFixes(Assembly asm);
        static partial void GameAliases(bool worldUp);

        /// Was the world now up loaded from a save? The game's own flag (DataFromMenuScript.load) is set when a save is
        /// chosen - while the old world is still up when loading from inside one - and cleared on the new world's first
        /// frame (mapSettingScript.Update), which can be before the mods' OnLoad, where they read mainscript.M.load to skip
        /// their new-world work: a save would get that work twice. So a load seen at any time (the flag, or the game's
        /// save screen's Load or the menu's Continue since the last look) is pending until the next world comes up, and decides that world: true from
        /// then until it ends. A joiner's world is the host's, never new: always true there.
        internal static bool FromSaveLatch(bool wasUp, bool worldUp, bool loadSeen, bool joiner, ref bool pending, ref bool fromSave)
        {
            if (loadSeen) pending = true;
            if (wasUp && !worldUp) fromSave = false;
            if (!wasUp && worldUp) { fromSave = pending; pending = false; }
            return joiner || fromSave;
        }

        private static void Scene(bool inWorld)
        {
            if (Loader == null) return;
            if (_hostScene == null) _hostScene = Loader.GetMethod("HostScene", BindingFlags.NonPublic | BindingFlags.Static);
            try { _hostScene?.Invoke(null, new object[] { inWorld }); } catch (System.Exception quietError) { Diagnostics.QuietErrors.Once("ModHost.Scene", quietError); }
        }

        private static MethodInfo Overridden(Type t, Type modBase, string name)
        {
            // A lookup that throws (an odd declaration) must not cost the mod its
            // whole registration: the caller is inside a try, but a throw here would
            // skip the rest of the mod's setup.
            try
            {
                var m = t.GetMethod(name, BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
                return m != null && m.DeclaringType != modBase ? m : null;
            }
            catch { return null; }
        }

        private static bool Call(Loaded l, MethodInfo m, string what)
        {
            if (m == null) return true;
            try { m.Invoke(l.Mod, null); return true; }
            catch (Exception e)
            {
                var inner = e.InnerException ?? e;
                string err = what + ": " + inner.Message;
                // once, not per frame; the stack says which line of the mod (the error alone doesn't)
                if (err != l.LastError) { l.LastError = err; Plugin.Log.LogError("ModHost: " + l.Id + " " + err + "\n" + inner.StackTrace); }
                return false;
            }
        }

        /// Per frame (Runner.Update): menu/game transitions, then the mods' own Update.
        /// The beta's item database has empty slots: filled for the mods' DbLoad (ModHost.Game.cs). A/B: false = before 2026-11-23.
        public static bool FillEmptySlots = true;
        /// A mod's DbLoad that threw in the menu runs again when the world is up, before its OnLoad. A/B: false = before 2026-11-24.
        public static bool DbLoadAgainInWorld = true;
        /// OnMenuLoad (once) before the first DbLoad, as under TLD Loader. A/B: false = before 2026-11-24.
        public static bool OnMenuLoadBeforeDbLoad = true;
        /// DbLoad for the menu's item database too. A/B: true = before 2026-11-24.
        public static bool DbLoadInMenu = false;
        public static int DbLoadsInWorld;
        public static int EmptySlotsFilled;
        static partial void FillHoles();
        static partial void RestoreHoles();
        /// An item a mod appends to the item table keeps the id its prefab came with, but the beta saves an item by its id
        /// and loads the prefab at that place in the table (a snowman mod's parts came back as other items after a reload).
        /// After each mod's DbLoad, what it appended gets its place as its id (ModHost.Game.cs). A/B: false = before 2026-11-25.
        public static bool NumberAppendedItems = true;
        public static int AppendedItemsNumbered;
        static partial void ItemCount(ref int count);
        static partial void NumberAppended(int from);

        /// The stub's ModLoader.HostDbLoad: the mods' DbLoad starts (fresh: for a new item database) or has ended. TLD Loader
        /// 3.0.3 takes RegisterItem only in between, and mods ask it (its dbmodsloaded) whether they are in DbLoad.
        private static void DbLoadMark(bool starting, bool fresh)
        {
            try { Loader?.GetMethod("HostDbLoad", BindingFlags.NonPublic | BindingFlags.Static)?.Invoke(null, new object[] { starting, fresh }); }
            catch (Exception e) { Plugin.Log.LogWarning("ModHost: TLD Loader's DbLoad mark: " + (e.InnerException ?? e).Message); }
        }

        /// The main menu: another scene than the game's (the public game had its own), or the beta's own flag. The beta's
        /// menu and world are one scene, loaded again for each world, so before 2026-11-26 its scene name never told them
        /// apart: DbLoad ran on the menu's item database, then on the world's, the same prefabs again (27 of 201 workshop
        /// mods added a component twice, two of them threw). Before the flag's object exists (the first frames) it is the
        /// menu. A/B: MenuByFlag = false is before 2026-11-26.
        public static bool MenuByFlag = true;
        private static object _menuDbSeen;
        private static bool InMainMenu() =>
            UnityEngine.SceneManagement.SceneManager.GetActiveScene().name != menuhandler.sceneName ||
            MenuByFlag && (DataFromMenuScript.s == null || DataFromMenuScript.s.mainmenu);

        public static void Tick()
        {
            if (Mods.Count == 0) return;
            PublicNames();
            // DbLoad: once per item database (a new one comes with each scene load), before a world can place items.
            // Mods change the game's item prefabs or add their own there. The order is the mods' IDs (ordinal), the same
            // on every machine: what one mod does can depend on another's.
            // Not the menu's: the beta has one there, the public game didn't, and it goes with the menu scene. TLD Loader
            // ran DbLoad in the world only; what a mod made there lived in the menu (a gauge mod read the missing player
            // every frame). Left unmarked: a database still up when the game scene is (a scene change) gets it then.
            // A/B: DbLoadInMenu = true is before 2026-11-24.
            bool menuDb = !DbLoadInMenu && (mainscript.s == null || mainscript.s.player == null) && InMainMenu();
            // The beta's menu database stays the menu's: its Start clears the flag a frame before the scene loads again.
            // (The public game's menu scene left: its database, still up in the game scene, gets DbLoad there.)
            if (menuDb && UnityEngine.SceneManagement.SceneManager.GetActiveScene().name == menuhandler.sceneName) _menuDbSeen = itemdatabase.s;
            else if (!DbLoadInMenu && MenuByFlag && itemdatabase.s != null && ReferenceEquals(itemdatabase.s, _menuDbSeen)) menuDb = true;
            if (itemdatabase.s != null && itemdatabase.s != _dbFor && !menuDb)
            {
                _dbFor = itemdatabase.s;
                DbLoadRuns++;
                // TLD Loader had called OnMenuLoad when it made the mod, long before the public game's item database (in the
                // world) existed: a mod sets up there what its DbLoad uses (one mod's prefab manager). The beta's
                // database is up with the menu; before 2026-11-24 the first DbLoad came first and threw.
                if (OnMenuLoadBeforeDbLoad && !_menuCalled) { _menuCalled = true; foreach (var l in Mods) Call(l, l.OnMenuLoad, "OnMenuLoad"); }
                var order = new List<Loaded>(Mods);
                order.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
                if (FillEmptySlots) FillHoles();
                // the public game ran DbLoad in the world, the player there: a mod that uses it threw
                // here in the menu, the rest of its DbLoad not done. Before 2026-11-24 it stayed that way.
                bool noWorld = mainscript.s == null || mainscript.s.player == null;
                DbLoadMark(true, true);   // a new database: TLD Loader 3.0.3's registry starts empty, its items come again
                try
                {
                    foreach (var l in order)
                    {
                        int before = 0; ItemCount(ref before);
                        bool ok = Call(l, l.DbLoad, "DbLoad") & Call(l, l.DbLoadOld, "dbLoad");
                        if (NumberAppendedItems) NumberAppended(before);
                        // a registered item makes the table longer (TLDLoader.ItemSlots): the next mods see no empty slots there either
                        if (FillEmptySlots) FillHoles();
                        l.DbLoadInWorld = !ok && noWorld && DbLoadAgainInWorld;
                        if (!ok) Plugin.Log.LogInfo("ModHost: " + l.Id + " DbLoad threw " + (noWorld ? "before the world was up: again when it is" : "with the world up"));
                        else if (l.LastError != null && l.LastError.StartsWith("DbLoad", StringComparison.OrdinalIgnoreCase))
                        {
                            Plugin.Log.LogInfo("ModHost: " + l.Id + " DbLoad ran this time (" + l.LastError + " before)");
                            l.LastError = null;   // the error the status shows is over
                        }
                    }
                }
                finally { DbLoadMark(false, false); if (FillEmptySlots) RestoreHoles(); }
            }
            // The world's own signal is the PLAYER, not the scene name: the game loads
            // scenes for a restart and for a new game, and the active scene is briefly
            // something else while it does. Keying the unload on the scene would fire
            // OnUnload for a scene change that does not end the world. The player
            // appears with the world and is gone when it ends, which is what OnLoad
            // and OnUnload are about.
            bool worldUp = mainscript.s != null && mainscript.s.player != null;
            // Another world in this one's place between two ticks: a save loaded from inside a world (the save screen's
            // Load reloads the scene, and the new world's player exists before the next tick). The player check alone
            // saw no change: before 2026-11-11 the mods got no OnUnload for the old world and no OnLoad for the new one.
            object worldNow = worldUp ? (object)mainscript.s : null;
            bool replaced = worldUp && _worldUp && !ReferenceEquals(worldNow, _worldObj);
            _worldObj = worldNow;
            if (replaced)
            {
                WorldsReplaced++;
                _unloadCalled = true; foreach (var l in Mods) Call(l, l.OnUnload, "OnUnload");
                _gameLoadedCalled = false;
                GameAliases(false);   // the old world ended (for mainscript.M.load)
            }
            if (worldUp != _worldUp)
            {
                bool wasUp = _worldUp;
                _worldUp = worldUp;
                Scene(worldUp);   // ModLoader.isOnMainMenu / GetCurrentScene
                _gameLoadedCalled = false;
                // Leaving the world unloads the mod: TLDLoader calls OnUnload when the
                // game it loaded into goes away, so the mod can undo what it did.
                if (wasUp && !worldUp) { _unloadCalled = true; foreach (var l in Mods) Call(l, l.OnUnload, "OnUnload"); }
            }
            // The menu is the game's own main-menu scene; the world is up whenever the
            // player exists. In the menu the player does not exist, so a mod that asked
            // for the menu gets its OnMenuLoad there: once, as TLD Loader calls it when it makes the mod (in the menu,
            // never again). Before 2026-11-23 each return to the menu called it again (a bundle it loads there twice).
            GameAliases(worldUp);   // ModHost.Game.cs: mainscript.M.seed/load/napszak, before the mods' OnLoad below
            bool inMenu = !worldUp && !_gameLoadedCalled && InMainMenu();
            if (inMenu && !_menuCalled) { _menuCalled = true; foreach (var l in Mods) Call(l, l.OnMenuLoad, "OnMenuLoad"); }
            // OnLoad once the world is actually up (the player exists), as TLDLoader calls it after the game loaded
            if (worldUp && !_gameLoadedCalled)
            {
                // straight into a world, the menu never seen (the beta's auto continue): TLD Loader had called it already
                if (!_menuCalled) { _menuCalled = true; foreach (var l in Mods) Call(l, l.OnMenuLoad, "OnMenuLoad"); }
                _gameLoadedCalled = true;
                _unloadCalled = false;
                // the empty slots filled here too: mods go through the items in OnLoad as well (a tool mod's vehicle list
                // stopped at the first one, and the rest of its OnLoad with it)
                if (FillEmptySlots) FillHoles();
                try
                {
                    foreach (var l in Mods)
                        if (l.DbLoadInWorld)
                        {
                            l.DbLoadInWorld = false; DbLoadsInWorld++; l.LastError = null;   // a second failure is logged too
                            DbLoadMark(true, false);
                            bool ok; int before = 0; ItemCount(ref before);
                            try { ok = Call(l, l.DbLoad, "DbLoad") & Call(l, l.DbLoadOld, "dbLoad"); }
                            finally { DbLoadMark(false, false); }
                            if (NumberAppendedItems) NumberAppended(before);
                            if (FillEmptySlots) FillHoles();
                            Plugin.Log.LogInfo("ModHost: " + l.Id + " DbLoad again in the world" + (ok ? "" : ": it threw again"));
                            if (ok) l.LastError = null;
                        }
                    foreach (var l in Mods) Call(l, l.OnLoad, "OnLoad");
                }
                finally { if (FillEmptySlots) RestoreHoles(); }
            }
            // TLDLoader runs a mod's Update in a world only, unless it asked for the menu (LoadInMenu): mods read
            // mainscript.M.player there with no null check. Before 2026-11-23 every mod's ran in the menu.
            foreach (var l in Mods) if (worldUp || l.Menu) Call(l, l.Update, "Update");
            DevHooks.ModsTick?.Invoke();
        }

        /// The public game's names for the singletons (TLDRevamp.Preloader.LoaderAliases adds the fields) pointed at the
        /// beta's, whenever one of them is a new object. Without the preloader the fields don't exist: nothing to do.
        private static void PublicNames()
        {
            Alias(typeof(mainscript), "M", mainscript.s, ref _aliasM);
            Alias(typeof(itemdatabase), "d", itemdatabase.s, ref _aliasD);
            Alias(typeof(savedatascript), "d", savedatascript.s, ref _aliasSd);
        }
        private static void Alias(Type type, string name, object now, ref object last)
        {
            if (ReferenceEquals(now, last)) return;
            last = now;
            try { type.GetField(name, BindingFlags.Public | BindingFlags.Static)?.SetValue(null, now); }
            catch (Exception e) { Plugin.Log.LogWarning("ModHost: " + type.Name + "." + name + ": " + e.Message); }
        }

        /// The game is quitting or the plugin is going away: the world ends without a
        /// scene change, so OnUnload has to be called here or the mods never get to
        /// undo what they did.
        public static void Shutdown()
        {
            if (!_worldUp) return;
            _worldUp = false;
            _gameLoadedCalled = false;
            _unloadCalled = true;
            foreach (var l in Mods) Call(l, l.OnUnload, "OnUnload");
        }

        /// As TLDLoader: FixedUpdate once the mods' OnLoad ran in a world, OnGUI then too or in the menu for a LoadInMenu mod.
        public static void FixedTick() { if (!ModsLoaded()) return; foreach (var l in Mods) Call(l, l.FixedUpdate, "FixedUpdate"); }
        /// The mods' OnGUI and Config() in their own IMGUI pass with layout (ModGuiRunner). A/B: false = before 2026-11-24.
        public static bool GuiOwnPass = true;
        public static void Gui() { bool loaded = ModsLoaded(); foreach (var l in Mods) if (loaded || l.Menu) Call(l, l.OnGUI, "OnGUI"); }
        private static bool ModsLoaded() => _worldUp && _gameLoadedCalled;

        /// A Loader mod's settings, as TLD Loader's mod list shows them (2026-11-27): the mods list's Settings button opens
        /// one mod's window, and only then is its Config() called (Unity runs the IMGUI pass several times a frame; a mod
        /// that does one-time work in Config() must not run it for as long as the game is open). Before, every mod's
        /// Config() drew at once over the Revamp settings tab, each where it liked.
        public static bool ConfigScreenOpen => _configId != null;
        private static string _configId;

        /// The open settings window's mod (null: none).
        public static string ConfigOpenFor => _configId;

        /// Opens a mod's settings window (TLD Loader's: only a mod whose Config() has code). False if it has none.
        public static bool OpenConfig(string id)
        {
            var l = Mods.Find(x => x.Id == id);
            if (l == null || !HasSettings(l)) return false;
            _configId = id; ConfigWindowReset();
            return true;
        }

        public static void CloseConfig() => _configId = null;

        /// The open mod's settings window (ModGuiRunner's IMGUI pass; ModHost.Settings.cs draws it).
        public static void DrawConfigs()
        {
            if (!ConfigScreenOpen) return;
            var l = Mods.Find(x => x.Id == _configId);
            if (l == null) { _configId = null; return; }
            DrawConfigWindow(l);
        }
        static partial void DrawConfigWindow(Loaded l);
        static partial void ConfigWindowReset();

        /// TLD Loader's test for the Settings button: Config() overridden with more than an empty body (a "ret" alone, or
        /// "nop; ret", is two bytes of IL).
        private static bool HasSettings(Loaded l)
        {
            if (l.Config == null) return false;
            if (l.HasSettings == null)
            {
                try { l.HasSettings = (l.Config.GetMethodBody()?.GetILAsByteArray()?.Length ?? 0) > 2; }
                catch { l.HasSettings = true; }
            }
            return l.HasSettings.Value;
        }

        /// A Loader mod as the mods list shows it.
        public sealed class LoaderModInfo { public string Id, Name, Author, Version, Error; public bool HasSettings; }

        /// Every Loader mod running here, in load order.
        public static List<LoaderModInfo> LoaderMods()
        {
            var list = new List<LoaderModInfo>();
            foreach (var l in Mods)
                list.Add(new LoaderModInfo { Id = l.Id, Name = l.Name, Author = l.Author, Version = l.Version, Error = l.LastError, HasSettings = HasSettings(l) });
            return list;
        }

        /// Whether any mod has a settings window of its own.
        public static bool AnyConfig => Mods.Exists(HasSettings);

        /// Whether OnUnload has run since the last OnLoad (the status line reports it).
        public static bool UnloadCalled => _unloadCalled;

        /// Host: refuse joiners whose TLD Loader mods differ (config Mods.LoaderModsMustMatch).
        public static bool MustMatch = true;
        public static int JoinsRefused, JoinsAdmittedDiffering;

        /// Every TLD Loader mod running here, as the join check compares them (Net/LoaderModCheck).
        public static List<Net.LoaderModCheck.Entry> JoinList()
        {
            var list = new List<Net.LoaderModCheck.Entry>();
            foreach (var l in Mods) list.Add(new Net.LoaderModCheck.Entry(l.Id, l.Name, l.Version, l.Hash));
            return list;
        }

        private static readonly Dictionary<string, string> Hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private static string FileHash(string path)
        {
            if (Hashes.TryGetValue(path, out var h)) return h;
            try { using (var sha = System.Security.Cryptography.SHA256.Create()) h = BitConverter.ToString(sha.ComputeHash(File.ReadAllBytes(path))).Replace("-", "").ToLowerInvariant(); }
            catch (Exception e) { h = ""; Plugin.Log.LogWarning("ModHost: hashing " + Path.GetFileName(path) + ": " + e.Message); }
            return Hashes[path] = h;
        }

        public static string Status()
        {
            var rows = new List<string>();
            foreach (var l in Mods) rows.Add("{\"id\":" + Json.Str(l.Id) + ",\"name\":" + Json.Str(l.Name) + ",\"version\":" + Json.Str(l.Version) + ",\"hash\":" + Json.Str(l.Hash ?? "") + ",\"lastError\":" + Json.Str(l.LastError ?? "") + "}");
            return "{\"info\":" + Json.Str(Info) + ",\"retargeted\":" + Retargeted + ",\"mustMatch\":" + (MustMatch ? "true" : "false") +
                   ",\"joinsRefused\":" + JoinsRefused + ",\"joinsAdmittedDiffering\":" + JoinsAdmittedDiffering + ",\"inGame\":" + (_worldUp ? "true" : "false") + ",\"onLoadCalled\":" + (_gameLoadedCalled ? "true" : "false") +
                   ",\"unloadedSinceLastLoad\":" + (_unloadCalled ? "true" : "false") + ",\"hasConfig\":" + (AnyConfig ? "true" : "false") + ",\"mods\":[" + string.Join(",", rows) + "]}";
        }
    }
}
