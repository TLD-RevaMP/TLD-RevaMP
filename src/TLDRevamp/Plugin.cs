using System;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace TLDRevamp
{
    [BepInPlugin(Guid, "TLD Revamp", BaseVersion)]
    public class Plugin : BaseUnityPlugin
    {
        public const string Guid = "tldrevamp.core";
        /// The game's own Multiplayer button opens the game's built-in lobby (MpScreen.InsteadOfGameLobby).
        public static BepInEx.Configuration.ConfigEntry<bool> GameMultiplayerButton;
        public static BepInEx.Configuration.ConfigEntry<BepInEx.Configuration.KeyboardShortcut> KeyMpScreen, KeyOverlay, KeyReport, KeyItemTraffic, KeyMark, KeyDebugLayout, KeyItemHealth, KeyDevMenu, KeyOwnership, KeyOwnerGhost, KeyLinks, KeyCompare, KeyEvents, KeyRepro, KeyDebugView, KeyCinematic, KeyReplayRecord, KeyReplayPlay;
        internal static string KeyName(BepInEx.Configuration.ConfigEntry<BepInEx.Configuration.KeyboardShortcut> k)
        {
            if (k == null || k.Value.MainKey == KeyCode.None) return "no key";
            string name = k.Value.MainKey.ToString().Replace("PageUp", "PgUp").Replace("PageDown", "PgDn");
            foreach (var m in k.Value.Modifiers) name = m.ToString().Replace("Left", "").Replace("Right", "").Replace("Control", "Ctrl") + "+" + name;   // Ctrl+Insert, not "Insert + LeftControl"
            return name;
        }
        internal static bool KeyDown(BepInEx.Configuration.ConfigEntry<BepInEx.Configuration.KeyboardShortcut> k)
        {
            if (k == null || PlayerEdition.Blocked.Contains(k) || Net.Chat.Typing || MarkPanel.Opened || KeyBindings.Swallowed || k.Value.MainKey == KeyCode.None || !Input.GetKeyDown(k.Value.MainKey)) return false;
            // A key that types (a letter, a digit, space, punctuation) belongs to the text field being typed in: the Mods
            // search, the Multiplayer screen's passwords and the game's own fields (its check, RevaMP's fields added),
            // so a key bound to O does not toggle the overlay in the middle of a search.
            if (Typable(k.Value.MainKey) && mainscript.s != null && mainscript.s.SomeInputSelected()) return false;
            // Only on the frame the key went down: either Ctrl/Shift/Alt counts, and Ctrl+Insert does not also fire plain Insert.
            return BugReportLogic.ChordDown((int)k.Value.MainKey, Modifiers(k.Value), c => Input.GetKeyDown((KeyCode)c), c => Input.GetKey((KeyCode)c), OthersOnKey(k));
        }
        private static bool Typable(KeyCode c) { int v = (int)c; return v >= 32 && v <= 126 || v >= 256 && v <= 270 || v == 272; }   // ASCII keys, the keypad's digits and signs
        private static int[] Modifiers(BepInEx.Configuration.KeyboardShortcut s) => System.Linq.Enumerable.ToArray(System.Linq.Enumerable.Select(s.Modifiers, m => (int)m));
        private static System.Collections.Generic.IEnumerable<int[]> OthersOnKey(BepInEx.Configuration.ConfigEntry<BepInEx.Configuration.KeyboardShortcut> k)
        {
            foreach (var other in KeyBindings.Entries())   // every key in Settings > Revamp > Keys
                if (other != null && other != k && !PlayerEdition.Blocked.Contains(other) && other.Value.MainKey == k.Value.MainKey) yield return Modifiers(other.Value);
        }
        public const string BaseVersion = "0.69.4";
        public static readonly string Version = Metadata("RevampVersion", BaseVersion);
        /// Built with -p:RevampEdition=player (a release for players): PlayerEdition.
        public static readonly bool IsPlayerEdition = Metadata("RevampEdition", "") == "player";
        public static readonly string BuildCommit = Metadata("SourceCommit", "unknown");
        public static readonly bool BuildDirty = Metadata("SourceDirty", "true") != "false";
        public static string CommitLabel => (BuildCommit.Length == 40 ? BuildCommit.Substring(0, 7) : "unknown") + (BuildDirty ? "+changes" : "");
        public static string BuildIdentity => BuildCommit + ":" + (BuildDirty ? "dirty" : "clean") + ":" + BuildHash;

        static string Metadata(string key, string fallback)
        {
            foreach (var a in typeof(Plugin).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false))
                if (((System.Reflection.AssemblyMetadataAttribute)a).Key == key)
                    return ((System.Reflection.AssemblyMetadataAttribute)a).Value;
            return fallback;
        }

        /// Fingerprint of the DLL this game actually loaded (tools restart a game only when it runs a different build).
        public static readonly string BuildHash = ComputeBuildHash();

        private static string ComputeBuildHash()
        {
            try
            {
                var bytes = System.IO.File.ReadAllBytes(typeof(Plugin).Assembly.Location);
                using (var sha = System.Security.Cryptography.SHA1.Create())
                    return System.BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
            }
            catch { return "unknown"; }
        }

        /// The game's own code (Assembly-CSharp.dll, sha256, first 12 hex — what tools/game_build.json records): two players
        /// on different builds of the game can generate different worlds — a building that exists for one only (the
        /// missing diner, playtest 2026-10-09/10). Compared at join.
        public static readonly string GameBuild = ComputeGameBuild();

        /// The Harmony patches that did not apply. A patch that fails is dead code, and the game's log is the
        /// only place that says so: the count is here so the mod's own status can report it.
        public static int PatchFailures;
        public static readonly System.Collections.Generic.List<string> FailedPatches = new System.Collections.Generic.List<string>();
        public static BepInEx.Configuration.ConfigEntry<bool> ShowWipMark;

        /// The WIP mark's state. One source of truth: the overlay's runtime flag. The config entry is only the
        /// persistent backing (it is written when the value changes and read back at startup), so the settings
        /// screen and the overlay can never disagree about whether the mark is on.
        public static bool WipMarkEnabled
        {
            get
            {
                var overlay = Instance?.Overlay;
                if (overlay != null) return overlay.ShowWip;
                return ShowWipMark == null || ShowWipMark.Value;
            }
        }

        public static void SetWipMark(bool value)
        {
            var overlay = Instance?.Overlay;
            if (overlay != null) overlay.ShowWip = value;   // the runtime truth, first
            if (ShowWipMark != null && ShowWipMark.Value != value) ShowWipMark.Value = value;   // then the persistent copy
        }

        private static string ComputeGameBuild()
        {
            try
            {
                // The file in the game's Managed folder, not the loaded assembly's Location: when a BepInEx patcher
                // targets Assembly-CSharp (RevaMP's own LoaderAliases does) the game is loaded from patched bytes in
                // memory and Location is empty — every machine was "unknown" and every join refused (2026-11-10).
                string path = System.IO.Path.Combine(BepInEx.Paths.ManagedPath, "Assembly-CSharp.dll");
                if (!System.IO.File.Exists(path)) path = typeof(mainscript).Assembly.Location;
                var bytes = System.IO.File.ReadAllBytes(path);
                using (var sha = System.Security.Cryptography.SHA256.Create())
                    return System.BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").Substring(0, 12).ToLowerInvariant();
            }
            catch { return "unknown"; }
        }

        internal static Plugin Instance;
        internal static ManualLogSource Log;

        internal Telemetry Telemetry;
        internal Overlay Overlay;
        internal FeedbackReporter Feedback;

        private void Awake()
        {
            Instance = this;
            Log = Logger;
            ScriptProfiler.MainThreadId = System.Threading.Thread.CurrentThread.ManagedThreadId;

            Diagnostics.ErrorLog.Install();   // counts the game's errors for tests (bridge `errors`)
            Net.WorldCheck.Start();   // the game's data files hashed on a worker thread, for the join check
            TestSafety.Init();
            Telemetry = new Telemetry();
            SessionLog.Install();
            Overlay = new Overlay(Telemetry);
            // The cinematic camera (issue #36): a separate camera for filming. Unbound by default, so it never
            // steals a key the game or the player uses; set it in BepInEx/config/tldrevamp.core.cfg.
            KeyCinematic = Config.Bind("Keys", "CinematicCamera", new BepInEx.Configuration.KeyboardShortcut(KeyCode.None),
                "Opens or closes the cinematic camera (a separate camera for filming; the gameplay camera stays as it is). Unbound by default.");
            // the hint follows the key: bound or changed under Settings → Revamp → Keys, it still said "no key"
            void CinemaHint() => Cinematic.CinematicCamera.ExitKeyHint = KeyName(KeyCinematic) + " (Settings → Revamp → Keys, or BepInEx/config/tldrevamp.core.cfg)";
            CinemaHint(); KeyCinematic.SettingChanged += (_, __) => CinemaHint();
            // The replay (issue #36): record gameplay, then play it back with the cinematic camera.
            KeyReplayRecord = Config.Bind("Keys", "ReplayRecord", new BepInEx.Configuration.KeyboardShortcut(KeyCode.F9, KeyCode.LeftControl),
                "Starts or stops recording a replay (everything near you, every player; saved to BepInEx/tldrevamp-replays).");
            KeyReplayPlay = Config.Bind("Keys", "ReplayPlay", new BepInEx.Configuration.KeyboardShortcut(KeyCode.F10, KeyCode.LeftControl),
                "Plays the latest replay, or closes the one playing.");
            Feedback = new FeedbackReporter(Telemetry);
            Net.ModCatalog.Url = Config.Bind("Mods", "CatalogUrl", Net.ModCatalog.DefaultUrl,
                "Catalog URL (HTTPS, or file URI for local testing), fetched when opening Mods or pressing Refresh, and at start for the update check (Settings → Revamp → Mods section). Downloads must match the catalog DLL hash. The host never sends mod files.");
            // an older RevaMP's default (the address before the move to the TLD-RevaMP organisation) becomes today's, in the cfg too
            if (Net.ModCatalog.CatalogAddress(Net.ModCatalog.Url.Value) != Net.ModCatalog.Url.Value) Net.ModCatalog.Url.Value = Net.ModCatalog.DefaultUrl;
            Net.DedicatedServer.Awake(Config);
            Net.ModObjects.Install();   // the multiplayer API other mods reference (TLDRevamp.Multiplayer.dll)
            Replay.ReplayMods.Install();   // and its replay channels (API 4)
            // The XR session (issue #41). OpenXR is what the game ships and what it uses; the bridge only owns the
            // loader's life when the session is asked for. The no-pre-init loader is a deliberate fallback and is off
            // by default: the plain loader is the one the game expects.
            Fixes.OpenXrBridge.Enabled = Config.Bind("Multiplayer", "OpenXrEnabled", true,
                "Use the OpenXR loader for the VR session. Off: the game keeps its own XR setup.").Value;
            Fixes.OpenXrBridge.AllowNoPreInit = Config.Bind("Multiplayer", "OpenXrAllowNoPreInit", false,
                "Allow the OpenXR no-pre-init loader when it is the only one the XR manager holds. Off by default: the plain OpenXR loader is preferred.").Value;
            var cl = Environment.GetCommandLineArgs();

            // The mod's keys: none the game uses. The game's own F1–F10, Home and End (dev mode: F8 a test ragdoll, F9
            // kills you, F10 skips radio tracks) and its default bindings (F4–F7: drop cam, 3D/360 screenshots, 360 camera)
            // stay the game's (2026-10-08: F7–F9 were ours too). F12 is Steam's screenshot key.
            KeyMpScreen = Config.Bind("Keys", "MultiplayerScreen", new BepInEx.Configuration.KeyboardShortcut(KeyCode.F11),
                "Opens the multiplayer screen (also the game's own Multiplayer button).");
            GameMultiplayerButton = Config.Bind("Multiplayer", "GameMultiplayerButton", false,
                "The game's own Multiplayer button (and its T key) opens the game's built-in multiplayer lobby instead of " +
                "RevaMP's multiplayer screen. F11 still opens RevaMP's. Only one of the two can be on at a time.");
            KeyOverlay = Config.Bind("Keys", "PerformanceOverlay", new BepInEx.Configuration.KeyboardShortcut(KeyCode.PageUp),
                "Shows or hides the performance overlay (also Settings → Revamp).");
            KeyReport = Config.Bind("Keys", "DebugReport", new BepInEx.Configuration.KeyboardShortcut(KeyCode.PageDown),
                "Saves a debug report (BepInEx/tldrevamp-feedback); with Settings → Revamp → Debugging → \"Share my marks and reports\" on, the sharing players get a copy. Dev builds only: a player release ignores this key (its bug report is Ctrl+Insert).");
            KeyMark = Config.Bind("Keys", "Mark", new BepInEx.Configuration.KeyboardShortcut(KeyCode.ScrollLock),
                "Opens session marks: Enter saves a note, Escape cancels. Notes can be renamed from the list.");
            KeyRepro = Config.Bind("Keys", "ReproRecorder", new BepInEx.Configuration.KeyboardShortcut(KeyCode.Insert, KeyCode.LeftControl),
                "Takes a bug report: opens a note box, Enter saves the report zip, the recorded actions and the world save into one bundle in BepInEx/tldrevamp-feedback (or type /bug <note> in chat). Either Ctrl works.");
            KeyDevMenu = Config.Bind("Keys", "DevMenu", new BepInEx.Configuration.KeyboardShortcut(KeyCode.F1),
                "The game's own developer menu (spawning, god mode, …), with Dev Mode on in the game's settings. F1 in the game; RevaMP lets it be changed.");
            // Settings → Revamp → Keys: each of these rebound in game (KeyBindings)
            KeyBindings.Register(("Multiplayer screen", KeyMpScreen), ("Performance overlay", KeyOverlay),
                ("Debug report", KeyReport), ("Bug report", KeyRepro), ("Session marks", KeyMark),
                ("Cinematic camera", KeyCinematic),
                ("Record a replay", KeyReplayRecord), ("Watch the latest replay", KeyReplayPlay));
            // the game's Dev Mode keys on a page of their own: F1 (its developer menu) and the rest (DevModeKeys, unbound by default)
            var devRows = new System.Collections.Generic.List<(string, BepInEx.Configuration.ConfigEntry<BepInEx.Configuration.KeyboardShortcut>)> { ("Open the developer menu", KeyDevMenu) };
            devRows.AddRange(DevModeKeys.Bind(Config));
            KeyBindings.AddSection("Dev Mode keys", "The game's own developer keys, with Dev Mode on in the game's settings (Misc). F1 opens its developer menu; " +
                "the rest start unbound, so Dev Mode doesn't turn Caps Lock, X, U, 0, Home or the F keys into cheats until you choose keys. " +
                "The developer menu's key list shows the keys set here.", devRows.ToArray());

            // Worker fixes (terrain, roads) queue short jobs on the .NET thread pool and sometimes wait for them. Mono's pool
            // starts small and only adds threads slowly when busy, so batches could run on just a few threads.
            System.Threading.ThreadPool.GetMinThreads(out int minWorkers, out int minIo);
            System.Threading.ThreadPool.GetMaxThreads(out int maxWorkers, out int maxIo);
            int want = Math.Min(Environment.ProcessorCount, 16);
            if (minWorkers < want) System.Threading.ThreadPool.SetMinThreads(want, minIo);
            System.Threading.ThreadPool.GetMinThreads(out int nowMin, out _);
            Log.LogInfo($"Thread pool: cores {Environment.ProcessorCount}, min workers {minWorkers} -> {nowMin}, max {maxWorkers}");

            // Must run before the world creates its Vector3d-keyed collections
            Fixes.Vector3dComparer.Install();
            // Patch each class on its own so one broken fix can't take the others down
            var harmony = new Harmony(Guid);
            var patchTypes = Array.FindAll(typeof(Plugin).Assembly.GetTypes(), type => type.GetCustomAttributes(typeof(HarmonyPatch), false).Length != 0);
            foreach (var type in patchTypes)
            {
                try { var applied = harmony.CreateClassProcessor(type).Patch(); if (applied != null && applied.Count > 0) GameLookups.PatchApplied(); Log.LogInfo("Patched " + type.Name); }
                catch (Exception e)
                {
                    // A patch that fails is dead code: the feature it carries simply does not happen, and nothing
                    // else says so. The count is reported in the mod's own status, so a test can see it.
                    PatchFailures++;
                    FailedPatches.Add(type.Name);
                    Log.LogError("Patch failed for " + type.Name + ": " + e);
                }
            }
            GameLookups.Report(message => Log.LogInfo(message));

            // The BepInEx manager object can be destroyed or disabled by the game, which silently stops
            // Update/OnGUI. Run the per-frame work from our own hidden, persistent object instead.
            RevampSettings.Load(); // player's saved choices from the settings menu's Revamp tab
            PlayerEdition.Apply(KeyBindings.ByName());   // a player release: the developer things off (nothing in a dev build)
            WorldGen.Old2023.OldBuild.Probe();   // the 2023 game files for the old terrain (the player's own Steam download)
            if (!WorldGen.Old2023.OldBuild.Available && WorldGen.TerrainChoice.Selected == WorldGen.TerrainMode.OldTerrain)
                WorldGen.TerrainChoice.Selected = WorldGen.TerrainModes.Default;   // the remembered pick comes back with the files
            // The old settings file could hold the host's password in plaintext. It is taken into memory for this
            // session and removed from the file (backward compatible: the file keeps its other lines).
            try { if (Net.SessionSettingsDefaults.Migrate()) Log.LogInfo("session settings: the old plaintext host password was removed from the settings file (it is kept in memory for this session only)"); }
            catch (Exception e) { Log.LogWarning("session settings: " + e.Message); }
            var go = new GameObject("TLDRevampRunner") { hideFlags = HideFlags.HideAndDontSave };
            DontDestroyOnLoad(go);
            go.AddComponent<Runner>().useGUILayout = false; // overlay uses no GUILayout: skip IMGUI's layout pass
            go.AddComponent<ModGuiRunner>();   // the Loader mods' OnGUI and Config() with it: they use GUILayout
            go.AddComponent<Net.HandItemRender>();
            go.AddComponent<VR.Native.VrNativeRunner>();   // native VR: the hand laser after the game's own frame
            ModHost.Retarget = Config.Bind("Mods", "RetargetLoaderMods", true,
                "Point TLD Loader mods' public-game names at the beta's live fields and small method adapters, including integer-sign changes (idInSave). The bridge's mods aliases lists the mappings. Off: load mods unchanged.").Value;
            ModHost.Compat = Config.Bind("Mods", "LoaderCompat", true,
                "Answer what TLD Loader mods use of the public game's save ids and attach code (the spawner many workshop mods share) from the beta's own, and give the mods' DbLoad an item database without empty slots. Off: those calls fail as before.").Value;
            ModHost.FillEmptySlots = ModHost.Compat;
            ModHost.MustMatch = Config.Bind("Mods", "LoaderModsMustMatch", true,
                "When you host: refuse players whose TLD Loader mods (BepInEx/TLDLoaderMods) differ from yours (mod ID, version or DLL). Items those mods add reach other players as game items only when everyone has the same mods. Off: they join anyway, and what those mods add may be missing or wrong for them.").Value;
            ModHost.Start();   // TLDLoader mods from BepInEx/TLDLoaderMods (TLDLoader itself doesn't run on the beta)
            Net.EntityProxyPresentation.CopyFarShadowsM = Mathf.Max(0f, Config.Bind("Multiplayer", "CopyShadowsOffBeyondM", 0f,
                    "Other players' cars farther than this (metres) cast no shadows here; 0 = always. A slower machine in a big session: try 30 (17 cars 20-45 m away, all without shadows: the slowest frames 2.6 ms quicker on the test desktop).").Value);

            Net.PlayerLook.LocalDriverHands = Net.RemotePlayers.LocalDriverHands;   // the copies place a driver's hands (RemotePlayers.DriverHands)
            Net.Entities.TelemetryAllOwnedPhysics = Config.Bind("Telemetry", "AllOwnedPhysics", false,
                "Probe up to 4096 owned bodies per physics step; off limits anomaly probes to trace focus entities.").Value;

            if (Config.Bind("Telemetry", "CoarseTimers", true, "Bounded coarse method groups in black boxes; detailed ScriptTimers remain opt-in.").Value) ScriptProfiler.CoarseStart();
            SessionLog.TimerColumnsChanged();
            // Telemetry script timers (docs/TELEMETRY.md): off unless this machine's config turns them on; bridge `timers` too.
            var timers = Config.Bind("Telemetry", "ScriptTimers", false,
                "Developer profiling: time a list of the game's hottest scripts and RevaMP's own per-frame work (ms/frame, calls) into the play log and black box. Costs roughly 0.1 µs per timed call.").Value;
            var timerList = Config.Bind("Telemetry", "ScriptTimerMethods", "default",
                "Type.Method names separated by spaces; \"default\" = the built-in hot list, \"all\" = every Update/LateUpdate/FixedUpdate/OnGUI of the game.").Value;
            var timerAlloc = Config.Bind("Telemetry", "ScriptTimerAlloc", false,
                "Also estimate allocations per timed method from heap growth during the call (includes worker threads; slower).").Value;
            ScriptProfiler.MaxColumns = Mathf.Clamp(Config.Bind("Telemetry", "ScriptTimerColumns", 16,
                "How many of the timed methods (in list order) get their own black-box column (0-32).").Value, 0, 32);
            ScriptProfiler.TickTop = Mathf.Clamp(Config.Bind("Telemetry", "ScriptTimerTop", 10,
                "How many timed methods each 5 s play-log tick lists (slowest first).").Value, 1, 200);
            if (timers || Array.IndexOf(cl, "-tldtimers") >= 0)
                try
                {
                    Log.LogInfo("Script timers: " + ScriptProfiler.TimersStart(timerList.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries), timerAlloc));
                    SessionLog.TimerColumnsChanged();
                    Log.LogInfo("Player loop phases: " + PlayerLoopProfiler.Start()); // engine phases (physics, rendering) per tick
                }
                catch (Exception e) { Log.LogWarning("Script timers failed: " + e.Message); }

            SceneCounts.Growth = Diagnostics.SceneGrowth.Json;
            Application.quitting += () =>
            {
                Log.LogInfo("Game quitting: stopping background work");
                // the session ends now, not at Steam's timeout: the others saw "the host isn't responding" for 12-15 s, and a
                // joiner's last player record (up to 30 s old) was what the host kept. No host save here: the game makes
                // none on quit either (a scene-load stop)
                try
                {
                    if (Net.Mp.Client != null) Net.PlayerSave.ClientLeaving();
                    if (Net.Mp.Server != null || Net.Mp.Client != null) Net.Mp.Stop(fromSceneLoad: true);
                }
                catch (Exception e) { Log.LogWarning("session stop on quit: " + e.Message); }
                Fixes.AsyncTerrain.Enabled = false; // no new worker jobs during shutdown
                DevHooks.BridgeStopped?.Invoke();
                SessionLog.Shutdown();
                Net.Session.ClearPresence();
            };

            Log.LogInfo($"TLD Revamp {Version} loaded. Unity {Application.unityVersion}, game build {GameBuild}. ");
        }

        private void OnDisable() => Log.LogWarning("BepInEx manager object disabled:\n" + Environment.StackTrace);
        private void OnDestroy() => Log.LogWarning("BepInEx manager object destroyed:\n" + Environment.StackTrace);
    }

    public class Runner : MonoBehaviour
    {
        internal static long Updates;
        private bool _loggedUpdate, _loggedGui;

        private void Update()
        {
            Updates++;
            if (!_loggedUpdate) { _loggedUpdate = true; Plugin.Log.LogInfo("Runner: first Update"); }

            var p = Plugin.Instance;
            // each block on its own: one throw cut short the rest of Update — the network tick, the join screen, the keys
            // (the cinematic camera did once, every frame it was on)
            try {
            ScriptProfiler.FrameEnd(); // closes the frame window before telemetry samples it
            p.Telemetry.Tick();
            DevHooks.BridgePump?.Invoke();
            Fixes.ItemSpawnSpread.Tick();
            Fixes.Sandstorms.Tick();
            Fixes.Rabbits.Tick();
            Fixes.GrassCameraThrottle.Tick(); Fixes.IdleTextureStreaming.Tick(); Fixes.PartConditionIdle.Tick(); Fixes.FarItemLookup.Tick(); Fixes.RenderOptions.Tick(); Fixes.FallRescue.Tick();
            } catch (Exception e) { Fault(0, "fixes", e); }
            try {
            if (Plugin.KeyDown(Plugin.KeyReplayRecord)) Replay.ReplayRecorder.Toggle();
            if (Plugin.KeyDown(Plugin.KeyReplayPlay)) { if (Replay.ReplayViewer.Active) Replay.ReplayViewer.End("closed"); else Replay.ReplayViewer.PlayLatest(); }
            Replay.ReplayRecorder.Tick();
            Replay.ReplayViewer.Tick();   // before the camera, so the camera shows this frame's pose
            Cinematic.CinematicCamera.Tick();
            if (Plugin.KeyDown(Plugin.KeyCinematic) && Cinematic.CinematicCamera.KeyAllowed()) Cinematic.CinematicCamera.Toggle();
            } catch (Exception e) { Fault(1, "replay/camera", e); }
            try { Fixes.TerrainHorizon.Tick(); Fixes.OpenXrBridge.Tick(); Fixes.VrContractBridge.Tick(); } catch (Exception e) { Fault(2, "terrain/vr/labs", e); }
            try { Net.Mp.Tick(); } catch (Exception e) { Fault(3, "multiplayer", e); }
            try { Net.PlayerSave.Tick(); Net.Tpa.Tick(); Net.CarTuneSave.Tick(); Net.Hardcore.Tick(); } catch (Exception e) { Fault(4, "player save/tpa/hardcore", e); }
            try { Net.Voice.Tick(); Net.Chat.Tick(); } catch (Exception e) { Fault(5, "voice/chat", e); }
            try { Fixes.TinyRendererCull.Tick(); DevHooks.RebaseTick?.Invoke(); Fixes.RebaseParticles.Tick(); Net.DedicatedServer.Tick(); } catch (Exception e) { Fault(6, "labs/cull/server", e); }
            try { ModHost.Tick(); } catch (Exception e) { Fault(7, "mods", e); }
            try { WorldGen.NewGameLayout.Tick(); KeyBindings.Tick(); } catch (Exception e) { Fault(8, "menus/keys/debug", e); }
            try {
            if (Plugin.KeyDown(Plugin.KeyOverlay)) { p.Overlay.Visible = !p.Overlay.Visible; RevampSettings.Save(); } // same setting as the Revamp tab
            if (Plugin.KeyDown(Plugin.KeyReport)) Net.Reports.LocalReport("hotkey");
            if (Plugin.KeyDown(Plugin.KeyMark)) MarkPanel.Open();
            if (Plugin.KeyDown(Plugin.KeyRepro)) MarkPanel.Open(true);
            ReproRecorder.Tick();
            MarkPanel.Tick();
            p.Feedback.Tick();
            Net.Reports.Tick();
            } catch (Exception e) { Fault(9, "keys/panels/reports", e); }
            try { Net.ModCatalog.Tick(); } catch (Exception e) { Fault(10, "mod catalog", e); }
            try { Net.JoinLoading.Tick(); } catch (Exception e) { Fault(11, "join screen", e); }
            if (Plugin.KeyDown(Plugin.KeyMpScreen)) Net.MpScreen.Toggle();   // the multiplayer screen (also the game's own Multiplayer button)
        }

        private static readonly float[] _faultLogAt = new float[16];
        public static long Faults;
        /// A block of Update/FixedUpdate threw: logged at most every 10 s per block, the rest of the frame goes on.
        private static void Fault(int slot, string what, Exception e)
        {
            Faults++;
            float now = Time.realtimeSinceStartup;
            if (now - _faultLogAt[slot] < 10f) return;
            _faultLogAt[slot] = now;
            Plugin.Log.LogError("Runner " + what + ": " + e);
        }

        private void OnGUI()
        {
            if (!_loggedGui) { _loggedGui = true; Plugin.Log.LogInfo("Runner: first OnGUI"); }
            if (Net.JoinLoading.Showing) { Net.JoinLoading.Draw(); return; }
            Plugin.Instance.Overlay.Draw();
            // The cinematic camera's status and help: its own pass, so they stay visible when the performance
            // overlay is hidden (a filmmaker turns the overlay off to get a clean frame). GUI calls belong here, in
            // OnGUI: from Update they threw every frame the camera was on and cut short the rest of Update (the
            // network tick among it).
            Cinematic.CinematicCamera.Draw();
            Replay.ReplayViewer.Draw(); Replay.ReplayRecorder.Draw();
            ReproRecorder.Draw();
            MarkPanel.Draw();
            if (!ModHost.GuiOwnPass) ModGuiRunner.Draw();
        }

        private void FixedUpdate()
        {
            try { Net.Entities.FixedTick(); } catch (Exception e) { Fault(12, "entities fixed", e); }
            try { ModHost.FixedTick(); GroundPlacement.FixedTick(); } catch (Exception e) { Fault(13, "mods/placement fixed", e); }
            try { Fixes.DrivingAssists.FixedTick(); Net.JoinLoading.FixedTick(); Fixes.LoadFreeze.FixedTick(); } catch (Exception e) { Fault(14, "assists/join/load fixed", e); }
        }
        private void LateUpdate() { Net.EntityHands.WornLate(); Net.EntityDoors.LateSample(); }

        private void OnApplicationQuit() => Net.JoinLoading.End("quit");
        private void OnDisable() { Net.JoinLoading.End("disabled"); Plugin.Log.LogWarning("Runner disabled:\n" + Environment.StackTrace); }
        private void OnDestroy()
        {
            Net.JoinLoading.End("destroyed");
            Plugin.Log.LogWarning("Runner destroyed:\n" + Environment.StackTrace);
            DevHooks.BridgeStopped?.Invoke();
            ModHost.Shutdown();   // the world ends here: the Loader mods get their OnUnload
        }
    }
}
