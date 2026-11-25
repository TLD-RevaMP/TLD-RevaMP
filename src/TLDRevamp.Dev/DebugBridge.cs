using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;
using static TLDRevamp.GroundPlacement;

namespace TLDRevamp
{
    /// Line-based TCP command server on 127.0.0.1 only. Each line is one command; the reply is one JSON line.
    /// Commands run on Unity's main thread (queued, executed in Update).
    public sealed class DebugBridge
    {
        private readonly int _port;
        private TcpListener _listener;
        private Thread _thread;
        private volatile bool _running;
        private readonly ConcurrentQueue<Job> _jobs = new ConcurrentQueue<Job>();

        private class Job
        {
            public string Cmd;
            public string Result;
            public readonly ManualResetEventSlim Done = new ManualResetEventSlim();
        }

        public DebugBridge(int port) => _port = port;

        /// The port this bridge LISTENS on. A port of 0 means "any free port": the real one is the one
        /// the listener was given, and it is reported so a client can tell which copy it is talking to.
        /// Static because the bridge's command handling is static too: there is exactly one bridge per process.
        private static int _boundPort;

        /// The port this bridge listens on, as a client sees it. -1 before the listener started.
        public static int BoundPort => _boundPort;

        public void Start()
        {
            _running = true;
            _listener = new TcpListener(IPAddress.Loopback, _port);
            _listener.Start();
            // the listener's real port (the one the OS gave us when 0 was asked for)
            try { _boundPort = ((IPEndPoint)_listener.LocalEndpoint).Port; } catch { _boundPort = _port; }
            _thread = new Thread(AcceptLoop) { IsBackground = true, Name = "TLDRevampBridge" };
            _thread.Start();
        }

        public void Stop()
        {
            if (!_running) return;
            _running = false;
            try { _listener?.Stop(); } catch { /* best effort: listener may already be disposed during teardown */ }
            // Unblock any client thread waiting on the main thread so nothing is left hanging at quit
            while (_jobs.TryDequeue(out var job)) { job.Result = "{\"error\":\"shutting down\"}"; job.Done.Set(); }
        }

        private void AcceptLoop()
        {
            try
            {
                while (_running)
                {
                    TcpClient client;
                    try { client = _listener.AcceptTcpClient(); }
                    catch { return; } // listener stopped
                    new Thread(() => Serve(client)) { IsBackground = true, Name = "TLDRevampBridgeClient" }.Start();
                }
            }
            catch (Exception e) { SafeLog("bridge accept loop error: " + e.Message); }
        }

        private static void SafeLog(string msg)
        {
            try { Plugin.Log.LogWarning(msg); } catch { /* best effort: fallback logging must not throw */ }
        }

        private void Serve(TcpClient client)
        {
            // Must never throw: an unhandled exception on a background thread crashed the game once
            // (Mono died while formatting its stack trace, 2026-09-24).
            try { ServeLines(client); }
            catch (IOException) { } // client went away
            catch (ObjectDisposedException) { }
            catch (Exception e) { SafeLog("bridge client error: " + e.GetType().Name + ": " + e.Message); }
        }

        private void ServeLines(TcpClient client)
        {
            using (client)
            using (var stream = client.GetStream())
            using (var reader = new StreamReader(stream, Encoding.UTF8))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true })
            {
                string line;
                while (_running && (line = reader.ReadLine()) != null)
                {
                    if (line.Trim().Length == 0) continue;
                    // A web page can make the browser send text to 127.0.0.1 (a form or fetch POST): its request line and
                    // headers arrive before the body, so dropping the connection there runs nothing it carries.
                    if (LooksLikeHttp(line)) { SafeLog("bridge: refused an HTTP request"); return; }
                    if (line.Trim() == "alive")
                    {
                        // Answered off the main thread: tells us whether Update is ticking at all
                        writer.WriteLine("{\"bridge\":true,\"updates\":" + Runner.Updates + "}");
                        continue;
                    }
                    var job = new Job { Cmd = line.Trim() };
                    _jobs.Enqueue(job);
                    // Main thread may be stalled on a loading screen; don't hang forever
                    writer.WriteLine(job.Done.Wait(30000) ? job.Result : "{\"error\":\"timeout\"}");
                }
            }
        }

        private static readonly System.Text.RegularExpressions.Regex HttpLine = new System.Text.RegularExpressions.Regex(
            @"^\s*(GET|POST|PUT|HEAD|OPTIONS|DELETE|PATCH|CONNECT|TRACE|PRI)\s+\S+\s+HTTP/|\bHTTP/[0-9]|^[A-Za-z][A-Za-z0-9-]*:\s",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        internal static bool LooksLikeHttp(string line) => HttpLine.IsMatch(line);

        public void PumpMainThread()
        {
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            PumpJobs();
            double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            BridgeTiming.LastPumpMs = ms;
            BridgeTiming.TotalPumpMs += ms;
            if (ms > BridgeTiming.MaxPumpMs) BridgeTiming.MaxPumpMs = ms;
        }

        private void PumpJobs()
        {
            while (_jobs.TryDequeue(out var job))
            {
                try { job.Result = Execute(job.Cmd); }
                catch (Exception e) { job.Result = "{\"error\":" + Json.Str(e.ToString()) + "}"; }
                job.Done.Set();
            }
        }

        private static string TpStatus()
        {
            var state = GroundPlacement.Observe();
            return "{\"waiting\":" + (state.Waiting ? "true" : "false") + ",\"on\":" + Json.Str(state.On) + ",\"placed\":" + state.Placed + ",\"waited\":" + state.Waited + "}";
        }

        private static string Ground(float x, float z)
        {
            var h = GroundHit(x, z);
            if (h == null) return "{\"hit\":false}";
            return "{\"hit\":true,\"y\":" + h.Value.point.y.ToString("F2") + ",\"collider\":" + Json.Str(h.Value.collider.name) +
                   ",\"type\":" + Json.Str(h.Value.collider.GetType().Name) + "}";
        }

        private static string TpaLandingCheck()
        {
            var pl = mainscript.s != null ? mainscript.s.player : null;
            if (pl == null) return "{\"error\":\"no player\"}";
            var p = pl.transform.position;
            var floor = Net.TpaPlacement.Landing(p, pl.transform.root, out bool room);
            return "{\"floor\":" + (floor.HasValue ? "true" : "false") + ",\"gap\":" +
                (floor.HasValue ? (p.y - floor.Value.point.y).ToString("F3", System.Globalization.CultureInfo.InvariantCulture) : "null") +
                ",\"room\":" + (room ? "true" : "false") + ",\"waiting\":" + (TpBusy ? "true" : "false") + "}";
        }

        private static string TpaIndoor()
        {
            var pl = mainscript.s != null ? mainscript.s.player : null;
            if (pl == null) return "{\"error\":\"no player\"}";
            foreach (var poi in Resources.FindObjectsOfTypeAll<poiScript>())
            {
                if (poi == null || !poi.gameObject.scene.IsValid() || !poi.gameObject.activeInHierarchy ||
                    (poi.transform.position - pl.transform.position).sqrMagnitude > 90000) continue;
                for (int x = -12; x <= 12; x++) for (int z = -12; z <= 12; z++)
                {
                    var from = poi.transform.position + new Vector3(x, 10, z);
                    foreach (var h in Physics.RaycastAll(from, Vector3.down, 20, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
                    {
                        if (h.normal.y < .65f || h.collider.attachedRigidbody != null || h.collider.GetComponentInParent<poiScript>() != poi || !Room(h.point, pl.transform.root)) continue;
                        var body = h.point + Vector3.up * 1.1f;
                        if (!Physics.Raycast(body, Vector3.up, out var ceiling, 5, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore) || ceiling.collider.GetComponentInParent<poiScript>() != poi) continue;
                        foreach (var dir in new[] { Vector3.right, Vector3.left, Vector3.forward, Vector3.back })
                        {
                            if (!Physics.Raycast(body, dir, out var wall, 1.7f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore) ||
                                wall.distance < 1f || wall.distance > 1.5f || Mathf.Abs(wall.normal.y) > .3f || wall.collider.attachedRigidbody != null || wall.collider.GetComponentInParent<poiScript>() != poi) continue;
                            bool sideRoom = Room(h.point + dir * 1.25f, pl.transform.root);
                            if (sideRoom) continue;
                            float yaw = Mathf.Atan2(-dir.z, dir.x) * Mathf.Rad2Deg;
                            pl.Teleport(body, new Vector3(0, yaw, 0)); TpSettle(pl);
                            return "{\"indoor\":true,\"wallM\":" + wall.distance.ToString("F3", System.Globalization.CultureInfo.InvariantCulture) + ",\"sideRoom\":false,\"wallRight\":" + Vector3.Dot(pl.BodyRot.right, dir).ToString("F3", System.Globalization.CultureInfo.InvariantCulture) + "}";
                        }
                    }
                }
            }
            return "{\"missing\":\"no reachable loaded building floor with ceiling, room and adjacent wall within 300 m\"}";
        }

        // The read-only commands: they change nothing, so a session that denies developer access still allows
        // them (a joiner must be able to ask what the policy is, and the tests read the state through them).
        private static readonly System.Collections.Generic.HashSet<string> _readCmds = new System.Collections.Generic.HashSet<string> { "get", "gpos", "ground", "tpalanding", "errors", "ping", "status", "roadpoint", "seatraw", "items", "telemetry", "genbacklog", "devaccess", "vr", "terrainhorizon", "terrainhorizonstats",
                                                                                "ragdolls", "worldcheck" };
        private static readonly System.Collections.Generic.HashSet<string> _readMp = new System.Collections.Generic.HashSet<string> { "entities", "status", "copyjoints", "players", "breakables", "carparts", "physical", "sitestate", "farrec", "caps", "ownerlog",
                                                                                "attachedparts", "carslots", "pcs", "wheelpose", "physlocks", "ipdump", "attach", "anomalies", "recsize", "mods", "saverecords", "usables", "poiu", "poius",
                                                                                // the death test's reads: these are `mp` commands (they sat in the top-level list, so a joiner was refused them)
                                                                                "deathstats", "deathinfo", "itemslist", "shared", "relays", "registry", "registryserver", "registrystore", "ragdolls", "corpses",
                                                                                // the join's status reads: a joiner still connecting has no session policy yet
                                                                                // (DeveloperAccess: denied until Ready), and tests poll these while it joins
                                                                                "session" };
        /// `findgo <name part> [radius m]`: root objects whose name contains the text (any case) within the radius of the
        /// player (default 50 m): where, held by physics or frozen, colliders on, save id, and their components.
        private static string FindGo(string arg)
        {
            var a = (arg ?? "").Trim().Split(' ');
            string part = a[0] == "*" ? "" : a[0].ToLowerInvariant();   // *: every root object in range
            float r = a.Length > 1 && float.TryParse(a[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var rr) ? rr : 50f;
            var pl = mainscript.s != null ? mainscript.s.player : null;
            var at = pl != null ? pl.transform.position : Vector3.zero;
            var rows = new System.Collections.Generic.List<string>();
            foreach (var t in UnityEngine.Object.FindObjectsOfType<Transform>())
            {
                if (t.parent != null || !t.name.ToLowerInvariant().Contains(part) || (pl != null && (t.position - at).sqrMagnitude > r * r)) continue;
                var rb = t.GetComponent<Rigidbody>();
                var ts = t.GetComponent<tosaveitemscript>();
                int on = 0, all = 0, inactive = 0;
                foreach (var c in t.GetComponentsInChildren<Collider>(true)) { all++; if (!c.gameObject.activeInHierarchy) inactive++; else if (c.enabled && !c.isTrigger) on++; }
                var kids = new System.Collections.Generic.List<string>();
                foreach (Transform k in t) { kids.Add(k.name + (k.gameObject.activeSelf ? "" : "(off)")); if (kids.Count >= 12) break; }
                var kinds = new System.Collections.Generic.List<string>();
                foreach (var c in t.GetComponents<Component>()) if (c != null && !(c is Transform)) kinds.Add(c.GetType().Name);
                var g = mainscript.GlobalFromUnityPos(t.position);
                rows.Add("{\"name\":" + Json.Str(t.name) + ",\"active\":" + (t.gameObject.activeInHierarchy ? "true" : "false") + ",\"pos\":[" + Json.Num((float)g.x) + "," + Json.Num((float)g.y) + "," + Json.Num((float)g.z) + "]" +
                         ",\"dist\":" + Json.Num(pl != null ? (t.position - at).magnitude : -1f) + ",\"rb\":" + (rb == null ? "null" : "{\"kinematic\":" + (rb.isKinematic ? "true" : "false") + ",\"speed\":" + Json.Num(rb.velocity.magnitude) + "}") +
                         ",\"colliders\":[" + on + "," + all + "],\"collidersInactive\":" + inactive + ",\"children\":" + Json.Str(string.Join(" ", kids.ToArray())) + ",\"save\":" + (ts != null ? ts.idInSave.ToString() : "null") + ",\"components\":" + Json.Str(string.Join(" ", kinds.ToArray())) + "}");
                if (rows.Count >= 40) break;
            }
            return "{\"found\":[" + string.Join(",", rows.ToArray()) + "]}";
        }

        private static bool ReadOnlyCmd(string cmd, string arg)
        {
            if (_readCmds.Contains(cmd)) return true;
            if (cmd == "mp" && (arg == "liveusables" || arg == "usableprobe" || arg == "loading")) return true;   // bare `mp loading` reads; its sub-commands act
            // `mp ragdoll <net>` reads a body's limbs; `mp ragdoll <net> kill|grab` changes the world, so only the
            // reading form is a read-only command. It is checked before the general `mp` rule below, which returns
            // for every `mp` command (this line used to sit after it and was never reached).
            if (cmd == "mp" && arg.StartsWith("ragdoll ")) return arg.Split(' ').Length == 2;
            if (cmd == "mp") { int sp = arg.IndexOf(' '); return _readMp.Contains(sp < 0 ? arg : arg.Substring(0, sp)); }
            if (cmd == "mount") return arg.StartsWith("pose") || arg.StartsWith("slots");
            // The cinematic camera's status is a pure query and safe for any peer; every other cinema command
            // changes what this machine shows, so it is a mutation and follows the host's developer policy.
            // The rule itself is in the pure core (CinematicCommandPolicy), so it can be tested offline.
            if (cmd == "cinema") return Cinematic.CinematicCommandPolicy.IsReadOnly(arg);
            if (cmd == "replay") return Replay.ReplayCommandPolicy.IsReadOnly(arg);
            // `openxr` on its own reads the state, and so does `openxr state`: both are the SAME read, so they
            // must classify the same way. Only `openxr start|stop|recenter|retry` changes the session, so it is a
            // developer command and needs the session's developer access. (Before this, `openxr state` was
            // refused when developer access was denied while the identical bare `openxr` was allowed.)
            // The XR command's read/mutation split is the policy's (VrBridgeCommandPolicy), so it is the same
            // rule the bridge itself dispatches by: `openxr` and `openxr state` read the state, and
            // `openxr start|retry|stop|recenter` change the session and need the session's developer access.
            if (cmd == "openxr") return VR.VrBridgeCommandPolicy.IsQuery(arg);
            // `vrnative` alone reads the native VR state; `vrnative sim|comfort|hands ...` drives or changes it
            if (cmd == "vrnative") { var v = arg.Trim(); return v.Length == 0 || v == "state" || v == "body" || v.StartsWith("items") || v.StartsWith("visible") || v == "post list" || v == "cull list"; }
            return false;
        }

        /// A reflection path that names the host's password (or the session's): the bridge never shows its value.
        /// Where a LOCAL evidence screenshot goes: a mod-owned folder that is NOT the shared report root, so a
        /// screenshot can never be swept into a shared report by accident.
        ///
        /// The separation is the point: the telemetry deliberately holds no screenshots (docs/TELEMETRY.md),
        /// and that rule is about what is SHARED. A local, explicit capture is a different thing and is what
        /// the camera, the terrain and the watermark evidence need.
        private static string LocalShotFolder()
        {
            string dir = Path.Combine(BepInEx.Paths.BepInExRootPath, "tldrevamp-evidence");
            Directory.CreateDirectory(dir);
            return dir;
        }

        /// Takes a local screenshot and returns its path. The file name is built from the TIME and a sanitised
        /// name: a name typed by a player would otherwise end up in a folder listing.
        ///
        /// This is a LOCAL capture only. Nothing here is attached to telemetry or to a shared report.
        private static string CaptureLocalShot(string name)
        {
            try
            {
                string folder = LocalShotFolder();
                string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff");
                // the name is sanitised: only letters, digits, dash and underscore survive
                var clean = new StringBuilder();
                foreach (char ch in (name ?? "").Trim())
                {
                    if (char.IsLetterOrDigit(ch) || ch == '-' || ch == '_') clean.Append(ch);
                    if (clean.Length >= 32) break;
                }
                string file = clean.Length > 0 ? "shot-" + stamp + "-" + clean + ".png" : "shot-" + stamp + ".png";
                string path = Path.Combine(folder, file);
                // "shot eyes...": in the headset, both eyes side by side (the eye textures, not the window)
                if (clean.ToString().StartsWith("eyes", StringComparison.Ordinal) && UnityEngine.XR.XRSettings.enabled)
                    UnityEngine.ScreenCapture.CaptureScreenshot(path, UnityEngine.ScreenCapture.StereoScreenCaptureMode.BothEyes);
                else
                    UnityEngine.ScreenCapture.CaptureScreenshot(path);
                return "{\"path\":" + Json.Str(path) + ",\"name\":" + Json.Str(file) + ",\"shared\":false}";
            }
            catch (Exception e)
            {
                return "{\"error\":" + Json.Str("the screenshot could not be saved: " + e.Message) + "}";
            }
        }

        /// A reflection path that names the host's password (or the session's): the bridge never shows its value.
        private static bool SecretPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            var p = path.ToLowerInvariant();
            return p.EndsWith("password") || p.Contains("hostpassword") || p.EndsWith(".password");
        }

        private static readonly System.Collections.Generic.Dictionary<string, Func<string, string>> Commands = new System.Collections.Generic.Dictionary<string, Func<string, string>>(StringComparer.Ordinal);
        internal static void Register(string name, Func<string, string> command) => Commands[name] = command;
        internal static bool TryRegistered(string name, string arg, out string reply)
        {
            if (Commands.TryGetValue(name, out var command)) { reply = command(arg); return true; }
            reply = null; return false;
        }

        internal static string Execute(string line)
        {
            var parts = line.Split(new[] { ' ' }, 2);
            string cmd = parts[0].ToLowerInvariant();
            string arg = parts.Length > 1 ? parts[1] : "";
            // The host's own session control (the developer policy) is not a developer command: the host must be
            // able to change its own policy even when the policy denies developer access (the test restores it).
            // ONLY for the host: the exemption used to apply on every machine, so a joiner got a DIFFERENT
            // answer for `mp policy` ("not hosting") than for any other denied command (the policy error a
            // client uses to know the host denied it). The host check is now inside the exemption.
            if (!TLDRevamp.Net.SessionSettingsPolicyTools.BridgeAllowed(Net.Mp.Server != null, Net.DeveloperAccess.Allowed, ReadOnlyCmd(cmd, arg), cmd, arg))
            {
                TLDRevamp.Net.DeveloperAccessTools.DenyBridge();
                return Net.SessionSettingsPolicy.PolicyDenied;
            }
            // what a dev command changed is noted on the anomaly watch's lines (Entities.Anomaly); queries are not.
            // A command that carries the host's password never keeps its value: the anomaly watch and every report
            // that quotes it would carry the secret.
            if (!ReadOnlyCmd(cmd, arg))
            {
                var safe = Net.SessionSettingsRules.RedactCommand(line, Net.Mp.Server?.Password);
                Net.EntityAnomalyTools.LastDevCmd = safe.Length > 80 ? safe.Substring(0, 80) : safe;
                Net.EntityAnomalyTools.LastDevAt = Time.realtimeSinceStartup;
            }
            var p = Plugin.Instance;

            if (TryRegistered(cmd, arg, out var contributedCommand)) return contributedCommand;
            switch (cmd)
            {
                case "vr": return TLDRevamp.Fixes.VrContractBridge.Json();
                case "vrnative": return TLDRevamp.VR.Native.VrNativeTools.Command(arg);
                // The XR session (issue #41). The tool tools/quest3vr.py sends exactly this command, so without
                // the case the tool could never start a session, sample it or recenter: the bridge answered
                // "unknown command" and the tool stopped at its first call. "openxr" on its own is a pure read
                // (the state), so it needs no developer access; "start"/"stop"/"recenter" change the session and
                // therefore go through the developer-access policy like every other mutating command.
                // The XR session, through the bridge: `openxr` reads the state, `openxr start|retry|stop|recenter`
                // acts on it. The start is explicit (nothing starts a session behind the player's back) and a
                // failed start is retried only when it is asked for.
                case "openxr": return TLDRevamp.Fixes.OpenXrBridgeTools.Command(arg);
                // The detailed terrain ring (issue #37): how far it reaches, its tiles, what it raised.
                case "detailedterrain": return TLDRevamp.Fixes.DetailedTerrain.Stats();
                case "ping": return "{\"ok\":true,\"version\":\"" + Plugin.Version + "\",\"build\":\"" + Plugin.BuildHash + "\",\"commit\":\"" + Plugin.BuildCommit + "\",\"dirty\":" + (Plugin.BuildDirty ? "true" : "false") + ",\"game\":\"" + Plugin.GameBuild + "\",\"port\":" + _boundPort + ",\"dataPath\":" + Json.Str(Application.dataPath) + ",\"patchFailures\":" + Plugin.PatchFailures + ",\"failedPatches\":" + Json.Str(string.Join(",", Plugin.FailedPatches)) + ",\"gameLookups\":" + GameLookups.Json() + "}";
                case "stats": return p.Telemetry.FrameStatsJson();
                case "scene": return p.Telemetry.SceneStatsJson();
                case "breakdown": return p.Telemetry.Breakdown.Json();
                case "telemetry": return SessionLog.Stats();
                case "timers": return TLDRevamp.ScriptProfilerTools.Bridge(arg);
                case "il": return TLDRevamp.ScriptProfilerTools.Il(arg.Trim());
                case "telemetrytest": return TLDRevamp.SessionLogTools.SelfTest(arg.Length > 0 ? int.Parse(arg) : 20000);
                case "scenecount": return SceneCounts.Scan(SessionLog.St);
                // Objects in the scene by part of their name, near the player: what a mod made and whether it lives (issue 44)
                case "findgo": return FindGo(arg);
                case "mark": return Net.TelemetryMarks.Local(arg);
                case "markpanel": return TLDRevamp.MarkPanelTools.Bridge(arg);
                case "markrename": return TLDRevamp.MarkPanelTools.Bridge(arg, true);
                case "bug": return TLDRevamp.BugReportTools.Bridge(arg);
                case "blackbox": return SessionLog.Blackbox();
                case "trace": return TLDRevamp.Net.EntityTelemetry.TelemetryTrace();
                case "netcat": return Net.NetMeter.CategoryJson();
                case "rebaseprof": return arg == "stop" ? RebaseLab.Stop() : RebaseLab.Start();
                case "pourtest": return TLDRevamp.RebaseLabTools.PourTest(arg);
                case "loadsave": return TLDRevamp.TestSafetyTools.LoadSave(arg);
                case "worldcheck": return Net.WorldCheck.Status();
                case "mods": return string.IsNullOrWhiteSpace(arg) ? ModHost.Status() : arg.Trim() == "aliases" ? TLDRevamp.ModHost.AliasStatus() : TLDRevamp.Net.ModBrowserTools.Bridge(arg);
                case "poisaved":
                {
                    // test: every building prefab's saveable objects (tosaveitemscript) — the lease's "taken" path turns those
                    // off on the machines that didn't spawn the building: one at the root, or carrying most of the
                    // building's meshes, would take the building with it (the missing diner, playtest 2026-10-09/10)
                    var rows = new System.Collections.Generic.List<string>();
                    var pois = itemdatabase.s.pois;
                    for (int k = 0; k < pois.Length; k++)
                    {
                        var pf = pois[k]; if (pf == null) continue;
                        int all = pf.GetComponentsInChildren<Renderer>(true).Length;
                        var big = new System.Collections.Generic.List<string>();
                        int saved = 0;
                        foreach (var ts in pf.GetComponentsInChildren<tosaveitemscript>(true))
                        {
                            saved++;
                            int r = ts.GetComponentsInChildren<Renderer>(true).Length;
                            bool root = ts.transform == pf.transform;
                            if (root || r * 5 >= all && r >= 10)
                                big.Add("[" + Json.Str(ts.name) + "," + r + "," + (root ? "true" : "false") + "," + (ts.GetComponent<pickupable>() != null ? "true" : "false") + "]");
                        }
                        rows.Add("[" + k + "," + Json.Str(pf.name) + "," + all + "," + saved + ",[" + string.Join(",", big) + "]]");
                    }
                    return "{\"pois\":[" + string.Join(",", rows) + "]}";
                }
                case "guardlab":
                    // test: Fixes.WornDestroyed / FluidTypeGuard (tools/guards.py)
                    return arg.Trim() == "fluid" ? TLDRevamp.Fixes.FluidTypeGuardTools.Test() : arg.Trim() == "worn" ? TLDRevamp.Fixes.WornDestroyedTools.Test() : Fixes.WornDestroyed.Status();
                case "prefabswith":
                {
                    // test: item ids whose prefab carries a game script of that type (e.g. sprayscript)
                    var ty = GameLookups.RuntimeType(typeof(mainscript).Assembly, arg.Trim());
                    if (ty == null) return "{\"error\":\"no type " + arg + "\"}";
                    var rows = new System.Collections.Generic.List<string>();
                    for (int k = 0; k < itemdatabase.s.items.Length; k++)
                        if (itemdatabase.s.items[k] != null && itemdatabase.s.items[k].GetComponentInChildren(ty, true) != null) rows.Add("[" + k + "," + Json.Str(itemdatabase.s.items[k].name) + "]");
                    return "{\"items\":[" + string.Join(",", rows) + "]}";
                }
                case "rabbits": return arg.Trim() == "spawn" ? TLDRevamp.Fixes.RabbitsTools.TestSpawn() : Fixes.Rabbits.Stats();
                case "devspawn":
                {
                    // the dev menu's own spawn (kaposztaleves.Spawn → mainscript.Spawn(g, color, worn, rtype, paint))
                    int idx;   // an item id, or a prefab name (e.g. Car09full_slide)
                    if (!int.TryParse(arg, out idx)) { idx = -1; for (int k = 0; k < itemdatabase.s.items.Length; k++) if (itemdatabase.s.items[k] != null && itemdatabase.s.items[k].name == arg) { idx = k; break; } }
                    if (idx < 0) return "{\"error\":" + Json.Str("no item named " + arg) + "}";
                    var g = itemdatabase.s.items[idx];
                    if (g == null || kaposztaleves.s == null) return "{\"error\":\"no item " + idx + "\"}";
                    kaposztaleves.s.Spawn(g, Color.white);
                    return "{\"spawned\":" + Json.Str(g.name) + "}";
                }
                case "itemids":
                {
                    // `itemids <part of a prefab name>`: the item ids whose prefab name contains it (devspawn / spawncarg take the id)
                    var hits = new System.Collections.Generic.List<string>();
                    for (int k = 0; k < itemdatabase.s.items.Length; k++)
                        if (itemdatabase.s.items[k] != null && itemdatabase.s.items[k].name.IndexOf(arg.Trim(), System.StringComparison.OrdinalIgnoreCase) >= 0)
                            hits.Add("[" + k + "," + Json.Str(itemdatabase.s.items[k].name) + "]");
                    return "{\"items\":[" + string.Join(",", hits) + "]}";
                }
                case "itemsnap":
                {
                    var ia = arg.Split(' ');
                    if (ia[0] == "test") return TLDRevamp.Net.ItemSnapshotTools.Test(ia.Length > 1 ? int.Parse(ia[1]) : 10);
                    if (ia[0] == "group") return TLDRevamp.Net.ItemSnapshotTools.TestGroup();
                    if (ia[0] == "groupdiag") return TLDRevamp.Net.ItemSnapshotTools.GroupDiag(ia.Length > 1 ? float.Parse(ia[1], System.Globalization.CultureInfo.InvariantCulture) : 15f);
                    return "{\"error\":\"itemsnap test [n]\"}";
                }
                case "devaccess": { var da = Net.DeveloperAccess.Status(); return da.Substring(0, da.Length - 1) + ",\"patchFailures\":" + Plugin.PatchFailures + ",\"failedPatches\":" + Json.Str(string.Join(",", Plugin.FailedPatches)) + ",\"gameLookups\":" + GameLookups.Json() + "}"; }   // Status() closes its object: the patch fields go inside it
                case "mp":
                {
                    var ma = arg.Split(' ');
                    var mic = System.Globalization.CultureInfo.InvariantCulture;
                    switch (ma[0])
                    {
                        case "host": return Net.Mp.Host(ma.Length > 1 ? ushort.Parse(ma[1]) : (ushort)27070, ma.Length > 2 ? ma[2] : "");
                        case "join": return Net.Mp.Join(ma[1], ma.Length > 2 ? ma[2] : "");
                        case "joinid": return Net.Mp.JoinFriend(ulong.Parse(ma[1]), ma.Length > 2 ? ma[2] : "");
                        case "session": return Net.Session.Status();
                        case "hands": return Net.RemotePlayers.HandsJson();
                        case "loading":
                            if (ma.Length > 1 && ma[1] == "preview") return TLDRevamp.Net.JoinLoadingTools.PreviewFromBridge(ma);
                            if (ma.Length > 1 && ma[1] == "toggle") return TLDRevamp.Net.JoinLoadingTools.ToggleFromBridge(ma);
                            return Net.JoinLoading.Probe(ma.Length > 1 ? ma[1] : "");
                        case "kick": return "{\"kicked\":" + (Net.Mp.Server != null && Net.Mp.Server.Kick(int.Parse(ma[1])) ? "true" : "false") + "}";
                        case "chat":
                            if (ma.Length > 1 && ma[1] == "say") return "{\"said\":" + (Net.Session.Say(arg.Substring(arg.IndexOf("say ", StringComparison.Ordinal) + 4)) ? "true" : "false") + "}";
                            if (ma.Length > 1 && ma[1] == "open") Net.Chat.Open();
                            if (ma.Length > 1 && ma[1] == "close") Net.Chat.Close();
                            return TLDRevamp.Net.Chat.Status();
                        case "voice":
                            if (ma.Length > 1 && ma[1] == "tone") return TLDRevamp.Net.VoiceTools.Tone(float.Parse(ma[2], mic), -1, ma.Length > 3 && ma[3] == "all");
                            if (ma.Length > 1 && ma[1] == "feed") return TLDRevamp.Net.VoiceTools.Tone(float.Parse(ma[3], mic), int.Parse(ma[2]));
                            if (ma.Length > 1 && ma[1] == "probe") return TLDRevamp.Net.VoiceTools.Probe();
                            if (ma.Length > 2 && ma[1] == "everyone" && Net.Mp.Server != null) Net.Mp.Server.UpdateSessionSettings(x => x.VoiceEveryone = ma[2] == "on", false);   // the host's session setting
                            if (ma.Length > 2 && ma[1] == "key") Net.Voice.EveryoneKey = Math.Max(0, Math.Min(Net.Voice.EveryoneKeys.Length - 1, int.Parse(ma[2])));
                            if (ma.Length > 1 && ma[1] == "mode") Net.Voice.Setting = (Net.Voice.Mode)Enum.Parse(typeof(Net.Voice.Mode), ma[2], true);
                            if (ma.Length > 1 && ma[1] == "mute") { if (!Net.Voice.Muted.Remove(ulong.Parse(ma[2]))) Net.Voice.Muted.Add(ulong.Parse(ma[2])); }
                            return Net.Voice.Status();
                        case "screen": return TLDRevamp.Net.MpScreenTools.Test(ma.Length > 1 ? arg.Substring(arg.IndexOf("screen", StringComparison.Ordinal) + 6).Trim() : "");
                        case "botcar": return Net.LegacyBots.CaptureBotCar();
                        case "carset": return TLDRevamp.Net.EntityCarSignalsTools.CarSet(uint.Parse(ma[1]), ma[2], int.Parse(ma[3]));
                        case "botattach": return Net.LegacyBots.BotAttach(uint.Parse(ma[1]), ma[2]);
                        case "botitem": return Net.LegacyBots.AddItemBot(ma.Skip(1).ToArray());   // ox oy oz [lag] [held]: a bot's item in the nearest car
                        case "acts": return TLDRevamp.Net.PlayerActsTools.Probe(ma.Length > 1 ? ma[1] : "");
                        case "bots": return Net.LegacyBots.AddBots(int.Parse(ma[1]), ma.Length > 2 ? float.Parse(ma[2], mic) : 30f, ma.Length > 3 ? float.Parse(ma[3], mic) : 5f);
                        case "remotebots": return Net.LegacyBots.AddRemoteBots(int.Parse(ma[1]), ulong.Parse(ma[2]), ma.Length > 3 ? float.Parse(ma[3], mic) : 30f, ma.Length > 4 ? float.Parse(ma[4], mic) : 5f, ma.Length > 5 ? float.Parse(ma[5], mic) : 0f);
                        case "dropbots": return Net.LegacyBots.DropBots(int.Parse(ma[1]));
                        case "status": return Net.Mp.Status();
                        case "save": return Net.Mp.SaveTail();
                        case "players": return Net.RemotePlayers.List();
                        case "breakables": return TLDRevamp.Net.EntityAiTools.Breakables(float.Parse(ma[1], System.Globalization.CultureInfo.InvariantCulture), ma.Length > 2 ? ma[2] : "");
                        case "breaknear": return TLDRevamp.Net.EntityAiTools.BreakNear(ma[1], float.Parse(ma[2], System.Globalization.CultureInfo.InvariantCulture));
                        case "getouttry": return TLDRevamp.Net.EntitySeatTools.GetOutTry(int.Parse(ma[1]));
                        case "share": return Net.Entities.ShareCar(Net.EntityLookup.NearestCar());
                        case "entities": return Net.Entities.Status(ma.Length > 1 && int.TryParse(ma[1], out int entityLimit) && entityLimit > 0 ? entityLimit : int.MaxValue);
                        case "mods": return TLDRevamp.Net.ModObjects.StatusJson();
                        case "plausibility": return TLDRevamp.Net.Plausibility.Stats();
                        case "ask": return Net.MpScreen.AskProbe(ma.Length > 1 ? ma[1] : "");
                        case "modinstall": return TLDRevamp.Net.ModInstall.Stats();
                        case "sprayhold": return TLDRevamp.Net.EntitySprayTools.SprayHold(float.Parse(ma[1], System.Globalization.CultureInfo.InvariantCulture));
                        case "sprayfx": return TLDRevamp.Net.EntityEffects.SprayFxState(uint.Parse(ma[1]));
                        case "lookat": return TLDRevamp.Net.EntitySprayTools.LookAt(uint.Parse(ma[1]));
                        case "turn": return TLDRevamp.Net.EntitySprayTools.TurnItem(uint.Parse(ma[1]));
                        case "lamp": return TLDRevamp.Net.EntityEffects.LampState(uint.Parse(ma[1]));
                        case "cardoors": return TLDRevamp.Net.EntityDoors.CarDoors(uint.Parse(ma[1]));
                        case "busdoors": return TLDRevamp.Net.EntityBusDoorsTools.BusDoors(uint.Parse(ma[1]), ma.Length > 2 ? int.Parse(ma[2]) : -1, ma.Length > 3 ? int.Parse(ma[3]) : -1);
                        case "doorglue": return TLDRevamp.Net.EntityDoorAttachmentsTools.GlueToDoor(uint.Parse(ma[1]), uint.Parse(ma[2]), int.Parse(ma[3]), int.Parse(ma[4]));
                        case "doorattachment": return TLDRevamp.Net.EntityDoorAttachmentsTools.DoorAttachmentPose(uint.Parse(ma[1]));
                        case "opendoor": return TLDRevamp.Net.EntityCarDoorsTools.OpenDoor(uint.Parse(ma[1]), int.Parse(ma[2]));
                        case "headh": return Net.RemotePlayers.HeadHeights();
                        case "stimuli": return Net.RemotePlayers.Stimuli();
                        case "rabbit": return TLDRevamp.Net.EntityDoors.Rabbit(uint.Parse(ma[1]));
                        case "rabbits": return TLDRevamp.Net.EntityRabbits.RabbitList();
                        case "rabbitdo": return TLDRevamp.Net.EntityRabbitsTools.RabbitDo(ma);
                        case "body": return TLDRevamp.Net.EntityRabbitsTools.BodyState(uint.Parse(ma[1]), ma);
                        case "copymotion": return TLDRevamp.Net.EntityCarDoorsTools.CopyMotion(uint.Parse(ma[1]), ma.Length > 2 && ma[2] == "reset");
                        case "stall": Net.ClientSimulation.StallUntil = UnityEngine.Time.realtimeSinceStartup + float.Parse(ma[1], System.Globalization.CultureInfo.InvariantCulture); return "{\"stall\":" + ma[1] + ",\"held\":" + Net.ClientSimulation.StalledMessages + "}";
                        case "fault": Net.Mp.FaultPart = int.Parse(ma[1]); Net.Mp.FaultUntil = UnityEngine.Time.realtimeSinceStartup + float.Parse(ma[2], System.Globalization.CultureInfo.InvariantCulture); return "{\"fault\":" + ma[1] + ",\"faults\":" + Net.Mp.TickFaults + "}";
                        case "lag": Net.ClientSimulation.LagS = float.Parse(ma[1], System.Globalization.CultureInfo.InvariantCulture) / 1000f; return "{\"lagMs\":" + ma[1] + "}";
                        case "lookplayer": return Net.RemotePlayers.LookAtPlayer(int.Parse(ma[1]));
                        case "bite": return TLDRevamp.Net.EntityCarDoorsTools.Bite();
                        case "food": return TLDRevamp.Net.EntityDoors.Food(uint.Parse(ma[1]));
                        case "putincar": return TLDRevamp.Net.EntityCarDoorsTools.PutInCar(uint.Parse(ma[1]), uint.Parse(ma[2]), int.Parse(ma[3]));
                        case "cargoinfo": return TLDRevamp.Net.EntityCarDoorsTools.CargoInfo(uint.Parse(ma[1]));
                        case "putrel": { var ci = System.Globalization.CultureInfo.InvariantCulture; return TLDRevamp.Net.EntityCarDoorsTools.PutRel(uint.Parse(ma[1]), uint.Parse(ma[2]), float.Parse(ma[3], ci), float.Parse(ma[4], ci), float.Parse(ma[5], ci), ma.Length > 6 && ma[6] == "clear"); }
                        case "relpos": return TLDRevamp.Net.EntityCarDoorsTools.RelPos(uint.Parse(ma[1]), ma.Skip(2).Select(uint.Parse).ToArray());
                        case "standon": return TLDRevamp.Net.EntityCarDoorsTools.StandOn(uint.Parse(ma[1]), ma.Length > 2 ? float.Parse(ma[2], System.Globalization.CultureInfo.InvariantCulture) : 0.15f, ma.Length > 3 && ma[3] == "rear");
                        case "relplayers": return TLDRevamp.Net.EntityCarDoorsTools.RelPlayers(uint.Parse(ma[1]));
                        case "footprobe": return TLDRevamp.Net.EntityFootingTools.FootProbe();
                        case "standseries": return TLDRevamp.Net.EntityFootingTools.StandSeries(ma);
                        case "outsidebodies": return TLDRevamp.Net.EntityFootingTools.OutsideBodies(uint.Parse(ma[1]));
                        case "tow": return TLDRevamp.Net.EntityCarDoorsTools.Tow(ma);
                        case "crashlease": return TLDRevamp.Net.EntityCrashAuthorityTools.TestCrashLease(uint.Parse(ma[1]));
                        case "holdcopy": return TLDRevamp.Net.EntityCrashAuthorityTools.TestHoldCopy(uint.Parse(ma[1]), ma.Length > 2 ? float.Parse(ma[2], System.Globalization.CultureInfo.InvariantCulture) : 5f);
                        case "lockdiag": return TLDRevamp.Net.EntityPhysLockTools.LockDiag(uint.Parse(ma[1]));
                        case "wear": return TLDRevamp.Net.EntityWornTools.WearNet(uint.Parse(ma[1]));
                        case "takeoff": return TLDRevamp.Net.EntityWornTools.TakeOffNet(uint.Parse(ma[1]), ma.Length > 2 && ma[2] == "drop");
                        case "worn": return TLDRevamp.Net.EntityHands.WornStatus();
                        case "wearables": return TLDRevamp.Net.EntityHands.Wearables();
                        case "wearnear": return TLDRevamp.Net.EntityWornTools.WearNear(ma.Length > 1 ? ma[1] : "");
                        case "radio": return ma.Length > 2 && ma[1] == "advance" ? TLDRevamp.Net.EntityRadioTools.RadioAdvance(int.Parse(ma[2])) : TLDRevamp.Net.EntityRadio.RadioStatus();
                        case "physlock": { var ci = System.Globalization.CultureInfo.InvariantCulture; return TLDRevamp.Net.EntityPhysLockTools.PhysLockStatus(double.Parse(ma[1], ci), double.Parse(ma[2], ci), float.Parse(ma[3], ci)); }
                        case "deathfling":
                        {
                            var ci = System.Globalization.CultureInfo.InvariantCulture;
                            if (ma.Length < 4) return Net.SessionRules.DeathStats();
                            var prefabs = new System.Collections.Generic.List<int>();
                            // The arguments are validated: a malformed one is an error the caller can read, not a
                            // throw from inside the bridge (the bridge has no try/catch around the command switch).
                            float vx, vy, vz;
                            if (!float.TryParse(ma[1], System.Globalization.NumberStyles.Float, ci, out vx) ||
                                !float.TryParse(ma[2], System.Globalization.NumberStyles.Float, ci, out vy) ||
                                !float.TryParse(ma[3], System.Globalization.NumberStyles.Float, ci, out vz))
                                return "{\"error\":\"deathfling vx vy vz [prefab id…]\"}";
                            for (int i = 4; i < ma.Length; i++)
                                foreach (var raw in ma[i].Split(','))
                                {
                                    if (raw.Length == 0) continue;
                                    int id;
                                    if (!int.TryParse(raw, out id)) return "{\"error\":\"not an item id: " + Json.Str(raw) + "\"}";
                                    prefabs.Add(id);
                                }
                            return TLDRevamp.Net.EntityDeathTools.DeathFling(vx, vy, vz, prefabs.ToArray());
                        }
                        case "deathstats": return Net.SessionRules.DeathStats();
                        // The game's own Death, called again for a death that already happened: the duplicate path.
                        case "deathagain": return TLDRevamp.Net.EntityDeathTools.DeathAgain();
                        case "deathinfo": return TLDRevamp.Net.EntityRagdolls.DeathInfo();
                        case "corpses": return TLDRevamp.Net.EntityRagdolls.CorpseList();
                        case "outfit": return TLDRevamp.Net.EntityCorpsesTools.OutfitTest(ma.Length > 1 && ma[1] == "random");
                        case "ragdolls": return TLDRevamp.Net.EntityRagdolls.RagdollPrefabs();
                        case "grips": return TLDRevamp.Net.EntityCargoInteraction.GripPrefabs();
                        case "storms": return TLDRevamp.Net.EntityStormsTools.StormTest(ma);
                        case "sounds": return TLDRevamp.Net.EntityPlayerSoundsTools.PlayerSoundsTest(ma);
                        case "tumble": return TLDRevamp.Net.EntityTumbleweedsTools.TumbleTest(ma);
                        case "grip": return ma.Length > 1 ? TLDRevamp.Net.EntityPushGripTools.GripTest(ma) : TLDRevamp.Net.EntityCargoInteraction.GripStatus();
                        case "ragdoll": return ma.Length > 2 ? TLDRevamp.Net.EntityRagdollTools.RagdollTest(uint.Parse(ma[1]), ma[2]) : TLDRevamp.Net.EntityRagdollTools.RagdollStatus(uint.Parse(ma[1]));
                        case "carsig": return TLDRevamp.Net.EntityCarSignals.CarSignals(uint.Parse(ma[1]));
                        case "tune": return ma.Length > 1 ? TLDRevamp.Net.EntityCarTuneTools.CarTuneDiag(uint.Parse(ma[1])) : TLDRevamp.Net.CarTuneSave.Stats();
                        case "blood": return TLDRevamp.Net.EntityBloodTools.BloodTest(uint.Parse(ma[1]), ma.Length > 2 ? ma[2] : "");
                        case "key": return TLDRevamp.Net.EntityIgnitionKeyTools.KeyTest(uint.Parse(ma[1]), ma.Length > 2 ? float.Parse(ma[2], System.Globalization.CultureInfo.InvariantCulture) : 0f);
                        case "radiocar": return TLDRevamp.Net.EntityRadioTools.RadioCar(uint.Parse(ma[1]));
                        case "seatguard": return TLDRevamp.Net.EntitySeats.SeatGuardStats();
                        case "push": return TLDRevamp.Net.EntityPushTools.TestPush(uint.Parse(ma[1]), ma.Length > 2 ? float.Parse(ma[2], System.Globalization.CultureInfo.InvariantCulture) : 3f);
                        case "attach": return TLDRevamp.Net.Entities.AttachStats();
                        case "partwatch": return TLDRevamp.Net.EntityPartWatchTools.PartWatchCommand(ma);
                        case "healthwatch": return TLDRevamp.Net.EntityHealthWatchTools.HealthWatchCommand(ma);
                        case "keys": return TLDRevamp.KeyBindingsTools.Command(ma);
                        case "anomalies": return Net.Entities.AnomalyJson();
                        case "copystore": return TLDRevamp.Net.EntityPhysLockTools.TestCopyStore(uint.Parse(ma[1]), uint.Parse(ma[2]));
                        case "displace": return TLDRevamp.Net.EntityAnomalyTools.TestDisplace(uint.Parse(ma[1]), float.Parse(ma[2], System.Globalization.CultureInfo.InvariantCulture));
                        case "repairrows": { TLDRevamp.Net.EntityRepairTools.TryRows(uint.Parse(ma[1]), out var rr); return rr; }
                        case "censusnow": return TLDRevamp.Net.EntityRepairTools.CensusNow(uint.Parse(ma[1]));
                        case "places": return TLDRevamp.Net.Entities.PlacesJson();
                        case "copyjoints": return TLDRevamp.Net.EntityCopyJointsTools.CopyJointsJson();
                        case "trail": return ObjectTrail.Rows(uint.Parse(ma[1], mic));
                        case "nudgemember": return TLDRevamp.Net.EntityPlaceCheckTools.TestNudgeMember(uint.Parse(ma[1]), int.Parse(ma[2]), float.Parse(ma[3], System.Globalization.CultureInfo.InvariantCulture));
                        case "dropstates": TLDRevamp.Net.EntityPlaceCheckTools.TestDropStates = uint.Parse(ma[1]); return "{\"dropping\":" + ma[1] + "}";
                        case "copypartgone": return TLDRevamp.Net.EntityAnomalyTools.TestCopyPartGone(uint.Parse(ma[1]), ma[2]);
                        case "shotfx": return Net.EntityEffects.ShotFxStats();
                        case "blasts": return Net.EntityExplosions.ExplosionStats();
                        case "membergone": return Net.Entities.MemberGoneStats();
                        case "sharequota": return TLDRevamp.Net.EntityAuthorityInterest.ShareQuotaStats();
                        // `mp reports` stats; `mp report <note>`: what the report key does (asks / sends as a player would)
                        case "sharing":
                        case "reports": return TLDRevamp.Net.Reports.Stats();
                        case "report": { string rn = arg.Length > 6 ? arg.Substring(7) : ""; Net.Reports.LocalReport(rn.Length == 0 ? "bridge" : rn); return TLDRevamp.Net.Reports.Stats(); }
                        case "aistats": return Net.EntityAI.AiStats();
                        case "aisee": return TLDRevamp.Net.EntityAiTools.AiSee();
                        case "rpose": return Net.RemotePlayers.PoseJson();
                        case "poiusables": return Net.Entities.PoiUsableStats();
                        case "liveusables":
                            return Net.Entities.LiveUsableStats();
                        case "usables": return TLDRevamp.Net.EntityLiveUsablesTools.Usables(uint.Parse(ma[1]));
                        case "dragusable": return TLDRevamp.Net.EntityLiveUsablesTools.DragUsable(uint.Parse(ma[1]), int.Parse(ma[2]), int.Parse(ma[3]), float.Parse(ma[4], mic), ma.Length > 5 ? float.Parse(ma[5], mic) : 60f);
                        case "usableprobe": return ma.Length > 1 ? TLDRevamp.Net.EntityLiveUsablesTools.ProbeUsable(uint.Parse(ma[1]), int.Parse(ma[2]), int.Parse(ma[3]), float.Parse(ma[4], mic)) : TLDRevamp.Net.EntityLiveUsablesTools.UsableProbeJson();
                        case "editcheck": return TLDRevamp.Net.EntityEditCheck.EditCheckJson();
                        case "interest": return TLDRevamp.Net.EntityAuthorityInterest.InterestJson();
                        case "forgeedit": return TLDRevamp.Net.EntityEditCheckTools.TestForgeEdit(uint.Parse(ma[1]), ma[2], ma.Length > 3 ? int.Parse(ma[3]) : 1);
                        case "forgepoi": return TLDRevamp.Net.EntityEditCheckTools.TestForgePoi(ma);
                        case "poius": return TLDRevamp.Net.EntityUsables.PoiList(ma.Length > 1 ? float.Parse(ma[1], mic) : 100f);
                        case "poiu": return TLDRevamp.Net.Entities.PoiGet(ma);
                        case "poidrag": return TLDRevamp.Net.EntityPoiUsablesTools.PoiDrag(ma);
                        case "poiprobe": return TLDRevamp.Net.EntityPoiUsablesTools.PoiProbe(ma);
                        case "poiuse": return TLDRevamp.Net.EntityPoiUsablesTools.PoiUse(ma);
                        case "decals": { var ci = System.Globalization.CultureInfo.InvariantCulture; return ma.Length >= 5 ? TLDRevamp.Net.EntityShotFxTools.Decals(0, double.Parse(ma[1], ci), double.Parse(ma[2], ci), double.Parse(ma[3], ci), float.Parse(ma[4], ci)) : TLDRevamp.Net.EntityShotFxTools.Decals(uint.Parse(ma[1]), 0, 0, 0, 0); }
                        case "shootat": return TLDRevamp.Net.PlayerCombatTools.ShootAt(int.Parse(ma[1]), ma.Length > 2 ? float.Parse(ma[2], System.Globalization.CultureInfo.InvariantCulture) : -1f);
                        case "shootpoint": { var ci = System.Globalization.CultureInfo.InvariantCulture; return TLDRevamp.Net.PlayerCombatTools.ShootPoint(double.Parse(ma[1], ci), double.Parse(ma[2], ci), double.Parse(ma[3], ci), ma.Length > 4 ? ma[4] : ""); }
                        case "combat": return Net.PlayerCombat.Stats();
                        case "hitcheck": return Net.HitCheck.Stats();
                        case "copycost": return TLDRevamp.Net.EntityProxyPresentation.CopyCounts();
                        case "trysave": return TLDRevamp.JoinerSaveTools.Try(ma[1]);
                        case "say": return "{\"said\":" + (Net.Session.Say(arg.Substring(4)) ? "true" : "false") + "}";
                        case "tpa": return TLDRevamp.Net.Tpa.Stats();
                        case "gamemode": return TLDRevamp.Net.GameMode.Stats();
                        case "hardcore":   // `mp hardcore [on|off] [vanish <s>]`: the host's session setting (single player: the own switch, not saved), the rules' counters
                            if (ma.Length > 1 && (ma[1] == "on" || ma[1] == "off"))
                            {
                                if (Net.Mp.Server != null) Net.Mp.Server.UpdateSessionSettings(x => x.Hardcore = ma[1] == "on", false);
                                else if (Net.Mp.Client == null) Net.Hardcore.Own = ma[1] == "on";
                            }
                            if (ma.Length > 2 && ma[1] == "vanish") Net.Hardcore.VanishS = Math.Max(0f, float.Parse(ma[2], mic));
                            return Net.Hardcore.Stats();
                        case "friendhost": { var fh = Net.Session.FriendHosts(ulong.Parse(ma[1])); return fh == null ? "{\"hosting\":false}" : "{\"hosting\":true,\"version\":" + Json.Str(fh.Version) + ",\"mode\":" + Json.Str(fh.HasMode ? Net.GameMode.Describe(fh.Mode) : "") + "}"; }
                        case "playersave": if (ma.Length > 1 && ma[1] == "record") Net.PlayerSave.SendRecord(); return Net.PlayerSave.Stats();
                        // The host changes its own session's developer policy (the test's own host action: it needs
                        // the joiner to have developer access for the death test's bridge commands). Host only.
                        case "policy":
                        {
                            if (Net.Mp.Server == null) return "{\"error\":\"not hosting\"}";
                            if (ma.Length > 1 && ma[1].Length > 0)
                            {
                                var names = new[] { "everyone", "hostonly", "disabled" };
                                int idx = Array.IndexOf(names, ma[1].ToLowerInvariant());
                                if (idx < 0) return "{\"error\":\"policy everyone|hostonly|disabled\"}";
                                Net.Mp.Server.SetDeveloperPolicy((Net.DeveloperAccessPolicy)idx);
                            }
                            return Net.DeveloperAccess.Status();
                        }
                        case "forgehit": { var cif = System.Globalization.CultureInfo.InvariantCulture; return TLDRevamp.Net.PlayerCombatTools.Forge(int.Parse(ma[1]), byte.Parse(ma[2]), new Vector3d(double.Parse(ma[3], cif), double.Parse(ma[4], cif), double.Parse(ma[5], cif)), new UnityEngine.Vector3(float.Parse(ma[6], cif), float.Parse(ma[7], cif), float.Parse(ma[8], cif)), ma.Length > 10 ? uint.Parse(ma[9]) : 0u, ma.Length > 10 ? int.Parse(ma[10]) : 0); }
                        case "physical": return TLDRevamp.Net.Entities.PhysicalStatus();
                        case "crashauth": return TLDRevamp.Net.EntityPhysics.CrashAuthorityStatus();
                        case "stop": return Net.Mp.Stop();
                        default: if (TryRegistered("mp " + ma[0], arg, out var contributed)) return contributed; return "{\"error\":\"mp host [port] | join <ip:port> | bots <n> [radius] [speed] | status | stop\"}";
                    }
                }
                case "net":
                {
                    var na = arg.Split(' ');
                    switch (na[0])
                    {
                        case "pops": return TLDRevamp.Net.SteamCompatTools.Pops();
                        case "route": return Net.Mp.Route();
                        case "stats": return Net.Mp.NetStats();
                        case "meter":
                            if (na.Length > 1 && na[1] == "reset") { Net.NetMeter.Reset(); return "{\"reset\":true}"; }
                            if (na.Length > 1 && (na[1] == "show" || na[1] == "hide")) { TLDRevamp.Net.NetMeterTools.Show = na[1] == "show"; return "{\"show\":" + (TLDRevamp.Net.NetMeterTools.Show ? "true" : "false") + "}"; }
                            if (na.Length > 1 && na[1] == "panel") return "{\"text\":" + Json.Str(Net.NetMeterTools.PanelText(12)) + "}";
                            return Net.NetMeter.Json();
                        case "debugview": return DebugViewTools.Command(na.Length > 1 ? na[1] : "state");
                        case "log": return TLDRevamp.Net.NetMeterTools.Log(na.Length > 1 ? int.Parse(na[1]) : 50);
                        case "me": return "{\"steamId\":" + Json.Str(Steamworks.SteamUser.GetSteamID().m_SteamID.ToString()) + ",\"online\":" + (Steamworks.SteamUser.BLoggedOn() ? "true" : "false") + "}";
                        default: if (TryRegistered("net " + na[0], arg, out var contributed)) return contributed; return "{\"error\":\"net pair <count> <size> [loopback] | status | stats | meter [reset|show|hide|panel] | items [show|hide|labels] | log [n] | stop\"}";
                    }
                }
                case "handbrake": {
                    var pl = mainscript.s != null ? mainscript.s.player : null;
                    var car = pl != null ? pl.Car : null;
                    if (car == null) return "{\"error\":\"not in a car\"}";
                    bool on = arg.Trim().ToLower() != "off";
                    car.BhandBrake = on;
                    return "{\"handbrake\":" + (on ? "true" : "false") + "}";
                }
                case "shot": return CaptureLocalShot(arg);
                case "handlayers": return TLDRevamp.Fixes.HandLayersAfterVr.Json();
                case "seatwhy": { var sw2 = arg.Split(' '); return Net.EntitySeats.SeatWhy(uint.Parse(sw2[0]), int.Parse(sw2[1])); }
                case "leasetable": return TLDRevamp.Net.EntityWorldLeases.LeaseTable();
                case "picknearest": return TLDRevamp.Net.EntityLifecycleTools.PickupNearest(arg.Length > 0 ? float.Parse(arg, System.Globalization.CultureInfo.InvariantCulture) : 3f);
                case "pickupcar": return TLDRevamp.Net.EntityLifecycleTools.PickupCar(uint.Parse(arg, System.Globalization.CultureInfo.InvariantCulture));
                case "deleteshare": return Net.Entities.DeleteShared(uint.Parse(arg, System.Globalization.CultureInfo.InvariantCulture));
                case "leasearm": TLDRevamp.Net.EntityLeasesTools.LeaseArm(arg); return "{\"armed\":" + Json.Str(arg) + "}";
                case "errors": return TLDRevamp.Diagnostics.ErrorLogTools.Json(arg == "reset", arg == "raw");
                case "carprefabs":
                {
                    // every vehicle the game can spawn (an item whose prefab has a carscript): id, name, slot count,
                    // and slots outside the prefab root's own hierarchy (vehiclezoo.py)
                    var rows = new System.Collections.Generic.List<string>();
                    var items = itemdatabase.s.items;
                    for (int k = 0; k < items.Length; k++)
                    {
                        var g = items[k];
                        if (g == null || g.GetComponentInChildren<carscript>(true) == null) continue;
                        var ts = g.GetComponent<tosaveitemscript>();
                        int slots = ts != null && ts.partslotscripts != null ? ts.partslotscripts.Count : 0, outside = 0;
                        if (ts != null && ts.partslotscripts != null)
                            foreach (var sl in ts.partslotscripts) if (sl != null && !sl.transform.IsChildOf(g.transform)) outside++;
                        rows.Add("[" + k + "," + Json.Str(g.name) + "," + slots + "," + outside + "]");
                    }
                    return "{\"cars\":[" + string.Join(",", rows) + "]}";
                }
                case "slotmiss": return "{\"misses\":" + Net.ItemSnapshot.SlotMisses + ",\"log\":" + Json.Str(Net.ItemSnapshot.SlotMissLog) + "}";
                case "gpos":
                {
                    var u = mainscript.s.player.transform.position;
                    var gp = mainscript.GlobalFromUnityPos(u);
                    var ci = System.Globalization.CultureInfo.InvariantCulture;
                    return "{\"ux\":" + u.x.ToString("F1", ci) + ",\"uz\":" + u.z.ToString("F1", ci) +
                           ",\"gx\":" + gp.x.ToString("F2", ci) + ",\"gy\":" + gp.y.ToString("F2", ci) + ",\"gz\":" + gp.z.ToString("F2", ci) + "}";
                }
                case "tpalanding": return TpaLandingCheck();
                case "tpaindoor": return TpaIndoor();
                case "tprelease":
                    Release();
                    return "{\"released\":true}";
                case "tpground":
                {
                    // `tpground gx gz [gy]`: stand ON the ground at a GLOBAL position (TpGround below); `tpground status`
                    var tp = arg.Split(' ');
                    if (tp[0] == "status") return TpStatus();
                    var ci = System.Globalization.CultureInfo.InvariantCulture;
                    return TpGround(double.Parse(tp[0], ci), double.Parse(tp[1], ci), tp.Length > 2 ? double.Parse(tp[2], ci) : (double?)null);
                }
                case "carupright":
                {
                    // test: the player's vehicle stood up at its heading (a spawned bicycle falls over before anyone sits on it;
                    // ridden, the game's balance holds it — carscript.Balance)
                    var uc = mainscript.s != null && mainscript.s.player != null ? mainscript.s.player.Car : null;
                    if (uc == null || uc.RB == null) return "{\"error\":\"no vehicle\"}";
                    var ur = Quaternion.Euler(0f, uc.transform.eulerAngles.y, 0f);
                    uc.RB.position = uc.RB.position + Vector3.up * 0.15f; uc.RB.rotation = ur; uc.transform.SetPositionAndRotation(uc.RB.position, ur);
                    uc.RB.velocity = Vector3.zero; uc.RB.angularVelocity = Vector3.zero;
                    return "{\"upright\":true}";
                }
                case "tpg":
                {
                    // teleport to a GLOBAL position (floating-origin safe between machines): convert, hop high, ground
                    var gp = arg.Split(' ');
                    var ci = System.Globalization.CultureInfo.InvariantCulture;
                    var gv = new Vector3d(double.Parse(gp[0], ci), 0, double.Parse(gp[1], ci));
                    var uv = mainscript.UnityPosFromGlobal(gv);
                    var pl = mainscript.s.player;
                    // y: 120 m above the player's CURRENT height — after an origin shift (the y part too) heights at two
                    // places 200 km apart differ by hundreds of metres in Unity space, so this can be under the target's
                    // terrain; pushed out of it, the body flew off at 2,040,716 m/s (farworld run 6: landings 129–1,836 km
                    // off, a fall-damage death). `tpg gx gz hold`: settled (TpSettle) now and again at `tpground`.
                    pl.Teleport(new Vector3(uv.x, pl.transform.position.y + 120, uv.z));
                    pl.godModeTillGrounded = true;
                    if (gp.Length > 2 && gp[2] == "hold") { Hold(pl); }
                    return "{\"tpg\":true,\"ux\":" + uv.x.ToString("F1", ci) + ",\"uz\":" + uv.z.ToString("F1", ci) + "}";
                }
                // the terrain mode (issue #32): what is selected, what the running world uses, where it came from
                // `terrain`                   - the mode, its source and the road generator's own flags
                // `terrain select <Crossroads|MainRoadOnly>` - pick the mode for the next new game
                // `terrain host <mode>`       - take a host's mode (what Welcome does)
                case "terrain":
                {
                    if (arg.Length == 0) return TLDRevamp.WorldGen.TerrainChoice.Stats(WorldGen.TerrainHooks.CurrentMap, TLDRevamp.WorldGen.Old2023.OldWorld.Stats());
                    var tparts = arg.Split(' ');
                    // `terrain roads` - every road generator of the map (and any other one in the scene) and, for a main
                    // road only generator, whether its main road runs unbroken through the chunks the game keeps
                    if (tparts[0] == "roads") return TLDRevamp.WorldGen.MainRoadOnlyRoadsTools.Report(WorldGen.TerrainHooks.CurrentMap);
                    // `terrain oldcode` - where the 2023 game files were found and whether the old code is loaded
                    if (tparts[0] == "oldcode") return TLDRevamp.WorldGen.Old2023.OldBuild.Stats();
                    // `terrain oldroad <z>` - the old 2023 road's point at that z (old terrain only)
                    if (tparts[0] == "oldroad" && tparts.Length > 1)
                    {
                        var path = WorldGen.Old2023.OldRoad.Path;
                        if (path == null) return "{\"error\":\"the old terrain is not on\"}";
                        if (!double.TryParse(tparts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double oz))
                            return "{\"error\":\"terrain oldroad <z>\"}";
                        var op = path.At(oz);
                        return "{\"gx\":" + op.x.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + ",\"gy\":" +
                               op.y.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + ",\"gz\":" +
                               op.z.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + "}";
                    }
                    if (tparts[0] == "select" && tparts.Length > 1)
                    {
                        WorldGen.TerrainMode m;
                        if (!WorldGen.TerrainModes.TryParse(tparts[1], out m) || !WorldGen.TerrainModes.IsSelectable(m))
                            return "{\"error\":\"unknown or disabled mode: " + tparts[1] + "\"}";
                        if (!WorldGen.TerrainNewGameUi.Offered(m))
                            return "{\"error\":" + Json.Str(WorldGen.Old2023.OldBuildFiles.NewGameHint) + "}";
                        WorldGen.TerrainHooks.Select(m);
                        WorldGen.TerrainNewGameUi.Refresh();
                        return TLDRevamp.WorldGen.TerrainChoice.Stats(WorldGen.TerrainHooks.CurrentMap, TLDRevamp.WorldGen.Old2023.OldWorld.Stats());
                    }
                    if (tparts[0] == "host" && tparts.Length > 1)
                    {
                        WorldGen.TerrainMode m;
                        if (!WorldGen.TerrainModes.TryParse(tparts[1], out m) || !WorldGen.TerrainModes.IsSelectable(m))
                            return "{\"error\":\"unknown or disabled mode: " + tparts[1] + "\"}";
                        WorldGen.TerrainHooks.TakeHost(m);
                        return TLDRevamp.WorldGen.TerrainChoice.Stats(WorldGen.TerrainHooks.CurrentMap, TLDRevamp.WorldGen.Old2023.OldWorld.Stats());
                    }
                    return "{\"error\":\"usage: terrain [roads|oldcode|oldroad <z>|select <mode>|host <mode>]\"}";
                }
                case "genbacklog":
                    if (menuhandler.s == null || menuhandler.s.currentMainMap == null || menuhandler.s.currentMainMap.poiGens == null)
                        return "{\"error\":\"no POI generators\"}";
                    SessionLog.SampleGen(); return TLDRevamp.Fixes.PoiBacklog.Stats;
                case "noisecheck": return TLDRevamp.Fixes.FastNoiseTools.Check(arg.Length > 0 ? int.Parse(arg) : 1000000);
                case "randcheck": return TLDRevamp.Fixes.FastRandomTools.Check(arg.Length > 0 ? int.Parse(arg) : 1000000);
                case "reset": p.Telemetry.Reset(); return "{\"ok\":true}";
                case "report": return "{\"dir\":" + Json.Str(p.Feedback.Capture(arg)) + "}";
                case "screenshot": return CaptureLocalShot("");
                case "poigens": return TLDRevamp.Fixes.PoiItemsStore.Gens();
                case "poistore": return TLDRevamp.Fixes.PoiItemsStore.Stats;
                case "unfreezeground": return TLDRevamp.Fixes.UnfreezeOnGround.Stats;
                case "loadfreeze": return TLDRevamp.Fixes.LoadFreeze.Stats;
                case "fallrescue": return arg == "test" ? TLDRevamp.Fixes.FallRescueTools.Test() : TLDRevamp.Fixes.FallRescueTools.Stats;
                case "stillfrozen": return TLDRevamp.Fixes.LoadFreezeTools.StillFrozen(string.IsNullOrEmpty(arg) ? 150f : float.Parse(arg, System.Globalization.CultureInfo.InvariantCulture));
                case "terrainkeep": return TLDRevamp.Fixes.TerrainRestartKeep.Stats();
                case "streamrace": return TLDRevamp.Fixes.StreamRaceTools.Probe(arg);
                case "attachpoints":
                {
                    // attached-without-parenting items (attachablescript.Update puts them on `point` every frame): where the
                    // point hangs and whether the floating-origin shift moves it
                    var rows = new System.Collections.Generic.List<string>();
                    foreach (var at in UnityEngine.Object.FindObjectsOfType<attachablescript>())
                    {
                        if (at == null || !at.attached || at.parentAtAttach) continue;
                        var pt = at.point;
                        string chain = "";
                        for (var t = pt; t != null && chain.Length < 200; t = t.parent)
                            chain += t.name + (t.GetComponent<visszarako>() != null ? "[vr" + (t.GetComponent<visszarako>().dont ? " dont" : "") + (t.GetComponent<visszarako>().moveWhenParented ? " mwp" : "") + "]" : "") + "/";
                        rows.Add("[" + Json.Str(at.name) + "," + Json.Str(chain) + "," + Json.Str(mainscript.GlobalFromUnityPos(at.transform.position).ToString()) + "," + Json.Str(pt != null ? mainscript.GlobalFromUnityPos(pt.position).ToString() : "-") + "]");
                    }
                    return "{\"count\":" + rows.Count + ",\"items\":[" + string.Join(",", rows) + "]}";
                }
                case "whereis":
                {
                    // every loaded item whose name contains `arg`: id, global position, what it hangs on (parent root,
                    // attach point root and slot) — the loose car parts of farworld B
                    var rows = new System.Collections.Generic.List<string>();
                    foreach (var kvi in savedatascript.s.items)
                    {
                        var it = kvi.Value;
                        if (it == null || it.name.IndexOf(arg, StringComparison.OrdinalIgnoreCase) < 0) continue;
                        var at = it.attachable;
                        string onPt = "-";
                        if (at != null && at.point != null)
                        {
                            var pr = at.point.GetComponentInParent<tosaveitemscript>();
                            onPt = at.point.root.name + (pr != null ? " item #" + pr.idInSave + " " + pr.name : "");
                        }
                        var par = it.transform.parent != null ? it.transform.parent.GetComponentInParent<tosaveitemscript>() : null;
                        rows.Add("[" + kvi.Key + "," + Json.Str(it.name) + "," + Json.Str(mainscript.GlobalFromUnityPos(it.transform.position).ToString()) + "," +
                                 Json.Str("attached " + (at != null && at.attached) + " slot " + (at != null && at.slot != null ? at.slot.name : "-") + " on " + onPt +
                                          " parent " + (par != null ? "#" + par.idInSave + " " + par.name : "-")) + "]");
                    }
                    return "{\"count\":" + rows.Count + ",\"items\":[" + string.Join(",", rows) + "]}";
                }
                case "spawnpoints":
                {
                    // item spawn points (newRandomStuffSpawnScript) within r m of a GLOBAL point: path, global position,
                    // chance, the prefabs it can roll and how many places — four RubberWoman spawned into one spot (fallprobe)
                    var ci = System.Globalization.CultureInfo.InvariantCulture;
                    var sp = arg.Split(' ');
                    var at = new Vector3d(double.Parse(sp[0], ci), 0, double.Parse(sp[1], ci));
                    float rr = sp.Length > 2 ? float.Parse(sp[2], ci) : 3f;
                    var rows = new System.Collections.Generic.List<string>();
                    foreach (var ns in Resources.FindObjectsOfTypeAll<newRandomStuffSpawnScript>())
                    {
                        if (ns == null || !ns.gameObject.scene.IsValid()) continue;
                        var g = mainscript.GlobalFromUnityPos(ns.transform.position);
                        double dx = g.x - at.x, dz = g.z - at.z;
                        if (dx * dx + dz * dz > rr * rr) continue;
                        string spPath = ns.name;
                        for (var t = ns.transform.parent; t != null && spPath.Length < 240; t = t.parent) spPath = t.name + "/" + spPath;
                        var prefabs = new System.Collections.Generic.List<string>();
                        if (ns.items != null) foreach (var x in ns.items) prefabs.Add((x.G != null ? x.G.name : x.prefab) + "x" + (x.places != null ? x.places.Length : 0));
                        rows.Add("{\"path\":" + Json.Str(spPath) + ",\"g\":" + Json.Str(g.ToString()) + ",\"chance\":" + ns.chanceToSpawn.ToString("F2", ci) +
                                 ",\"spawned\":" + (ns.spawned ? "true" : "false") + ",\"active\":" + (ns.gameObject.activeInHierarchy ? "true" : "false") +
                                 ",\"id\":" + ns.GetInstanceID() + ",\"items\":" + Json.Str(string.Join(" ", prefabs)) + "}");
                    }
                    return "{\"count\":" + rows.Count + ",\"points\":[" + string.Join(",", rows) + "]}";
                }
                case "raydown":
                {
                    // every collider straight down from a GLOBAL point (gx gy gz, 50 m), once front faces only (the
                    // default), once with backfaces: is there a floor under the RubberWoman spawn spot (fallprobe)?
                    var ci = System.Globalization.CultureInfo.InvariantCulture;
                    var q = arg.Split(' ');
                    var u = mainscript.UnityPosFromGlobal(new Vector3d(double.Parse(q[0], ci), double.Parse(q[1], ci), double.Parse(q[2], ci)));
                    double oy = mainscript.s.visszarakva.y;
                    var res = new System.Collections.Generic.List<string>();
                    bool was = Physics.queriesHitBackfaces;
                    foreach (bool bf in new[] { false, true })
                    {
                        Physics.queriesHitBackfaces = bf;
                        var hs = Physics.RaycastAll(u, Vector3.down, 50f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Collide);
                        System.Array.Sort(hs, (h1, h2) => h1.distance.CompareTo(h2.distance));
                        var rr = new System.Collections.Generic.List<string>();
                        foreach (var h in hs)
                        {
                            var mc = h.collider as MeshCollider;
                            rr.Add(Json.Str((h.point.y - oy).ToString("F2", ci) + " " + h.collider.name + " (" + h.collider.transform.root.name + ") " + h.collider.GetType().Name +
                                            (mc != null ? (mc.convex ? " convex" : " concave") : "") + (h.collider.isTrigger ? " TRIGGER" : "") + " n.y " + h.normal.y.ToString("F2", ci) +
                                            " layer " + h.collider.gameObject.layer));
                        }
                        res.Add("\"" + (bf ? "backfaces" : "front") + "\":[" + string.Join(",", rr) + "]");
                    }
                    Physics.queriesHitBackfaces = was;
                    return "{" + string.Join(",", res) + "}";
                }
                case "zeroplaces":
                {
                    // every item-spawn entry (loaded prefabs + scene) that has no places: it spawns ON the spawn point's
                    // transform — OldHouse's WifeSpawn 1-4 all sit at the house origin, so up to four RubberWoman
                    // spawn inside each other and inside the floor (fallprobe)
                    var rows = new System.Collections.Generic.List<string>();
                    var seen = new System.Collections.Generic.HashSet<string>();
                    int scanned = 0;
                    foreach (var ns in Resources.FindObjectsOfTypeAll<newRandomStuffSpawnScript>())
                    {
                        if (ns == null || ns.items == null) continue;
                        scanned++;
                        bool inScene = ns.gameObject.scene.IsValid();
                        foreach (var x in ns.items)
                        {
                            if (x == null || (x.places != null && x.places.Length > 0)) continue;
                            string key = ns.transform.root.name.Replace("(Clone)", "") + "/" + ns.name + ":" + (x.G != null ? x.G.name : x.prefab) + (x.isitem ? " item" : x.G != null ? " G" : " category");
                            if (arg == "stacked")
                            {
                                // prefabs only (each building once): local position of the spawn point in its building
                                if (inScene) continue;
                                var lp = ns.transform.root.InverseTransformPoint(ns.transform.position);
                                rows.Add("[" + Json.Str(ns.transform.root.name) + "," + Json.Str(ns.name) + "," + Json.Str(x.G != null ? x.G.name : x.prefab) + "," +
                                         lp.x.ToString("F3", System.Globalization.CultureInfo.InvariantCulture) + "," + lp.y.ToString("F3", System.Globalization.CultureInfo.InvariantCulture) + "," +
                                         lp.z.ToString("F3", System.Globalization.CultureInfo.InvariantCulture) + "," + ns.chanceToSpawn.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) + "]");
                                continue;
                            }
                            if (seen.Add(key)) rows.Add(Json.Str(key + (inScene ? " [scene]" : "")));
                        }
                    }
                    return "{\"scanned\":" + scanned + ",\"count\":" + rows.Count + ",\"entries\":[" + string.Join(",", rows) + "]}";
                }
                case "ragdolls":
                {
                    // every loaded ragdoll item (tosave.ragdollPos): root global y + kinematic, and per limb (childs) y,
                    // Rigidbody kinematic / sleeping / vy, joint present — the RubberWoman that falls through a building
                    // on the laptop (fallprobe)
                    var ci = System.Globalization.CultureInfo.InvariantCulture;
                    double oy = mainscript.s.visszarakva.y;
                    var rows = new System.Collections.Generic.List<string>();
                    foreach (var kvi in savedatascript.s.items)
                    {
                        var it = kvi.Value;
                        if (it == null || (arg.Length == 0 ? it.ragdollPos == null : it.name.IndexOf(arg, StringComparison.OrdinalIgnoreCase) < 0)) continue;
                        var rb = it.GetComponent<Rigidbody>();
                        int colsOn = 0, colsAll = 0, colsTrig = 0;
                        foreach (var col in it.GetComponentsInChildren<Collider>(true)) { colsAll++; if (col.enabled && col.gameObject.activeInHierarchy) { colsOn++; if (col.isTrigger) colsTrig++; } }
                        int rbs = it.GetComponentsInChildren<Rigidbody>(true).Length;
                        string extra = ",\"cols\":[" + colsOn + "," + colsAll + "," + colsTrig + "],\"rbs\":" + rbs + ",\"parent\":" + Json.Str(it.transform.parent == null ? "-" : it.transform.parent.name) +
                                       ",\"buried\":" + (it.buried ? "true" : "false") + ",\"layer\":" + it.gameObject.layer +
                                       ",\"mode\":" + Json.Str(rb == null ? "-" : rb.collisionDetectionMode.ToString());
                        var limbs = new System.Collections.Generic.List<string>();
                        var ch = it.ragdollPos != null ? it.ragdollPos.childs : null;
                        for (int i = 0; ch != null && i < ch.Length; i++)
                        {
                            var c = ch[i];
                            if (c == null) { limbs.Add("null"); continue; }
                            var lrb = c.GetComponent<Rigidbody>();
                            limbs.Add("[" + Json.Str(c.name) + "," + (c.position.y - oy).ToString("F1", ci) + "," +
                                      (lrb == null ? "\"none\"" : (lrb.isKinematic ? "\"kin\"" : lrb.IsSleeping() ? "\"sleep\"" : "\"dyn\"")) + "," +
                                      (lrb == null ? "0" : lrb.velocity.y.ToString("F1", ci)) + "," + (c.GetComponent<Joint>() != null ? "1" : "0") + "," +
                                      Json.Str(c.parent == null ? "-" : c.parent == it.transform ? "root" : c.parent.name) + "]");
                        }
                        rows.Add("{\"id\":" + kvi.Key + ",\"name\":" + Json.Str(it.name) + ",\"pos\":" + Json.Str(mainscript.GlobalFromUnityPos(it.transform.position).ToString()) +
                                 ",\"y\":" + (it.transform.position.y - oy).ToString("F1", ci) + ",\"rb\":" +
                                 (rb == null ? "\"none\"" : (rb.isKinematic ? "\"kin\"" : rb.IsSleeping() ? "\"sleep\"" : "\"dyn\"")) +
                                 ",\"vy\":" + (rb == null ? "0" : rb.velocity.y.ToString("F1", ci)) + ",\"frozen\":" + (it.ragdollPos != null && it.ragdollPos.freezed ? "true" : "false") + extra +
                                 ",\"limbs\":[" + string.Join(",", limbs) + "]}");
                    }
                    return "{\"count\":" + rows.Count + ",\"items\":[" + string.Join(",", rows) + "]}";
                }
                // The host's password is never read out through the bridge: a diagnostic that carries it would
                // carry it into a log, a report or a test's output.
                case "switches": return Switches.Status(arg.Trim());   // every runtime switch with its value now (Switches.cs)
                case "get":
                    if (SecretPath(arg)) return "{\"value\":\"<set>\"}";
                    return "{\"value\":" + BridgeReflect.Get(arg) + "}";
                case "set":
                    var kv = arg.Split(new[] { ' ' }, 2);
                    if (SecretPath(kv[0])) return "{\"value\":\"<set>\"}";
                    return "{\"value\":" + BridgeReflect.Set(kv[0], kv[1]) + "}";
                case "serverstrip": return Net.DedicatedServer.Strip();
                case "admin":
                    // the server's admin commands (Net/Admin.cs), as its console file or an admin's chat runs them
                    return Net.Mp.Server == null ? "{\"error\":\"not hosting\"}" : "{\"out\":" + Json.Str(Net.Mp.Server.Run(arg)) + "}";
                case "groundg":
                {
                    // ground height at a GLOBAL point, in global y — plus this machine's floating origin (machines must agree)
                    var gg = arg.Split(' ');
                    var ci2 = System.Globalization.CultureInfo.InvariantCulture;
                    var ug = mainscript.UnityPosFromGlobal(new Vector3d(double.Parse(gg[0], ci2), 0, double.Parse(gg[1], ci2)));
                    var hit = GroundHit(ug.x, ug.z);
                    var o = mainscript.s.visszarakva;
                    return "{\"origin\":[" + o.x.ToString("F3", ci2) + "," + o.y.ToString("F3", ci2) + "," + o.z.ToString("F3", ci2) + "],\"gy\":" +
                           (hit != null ? (hit.Value.point.y - o.y).ToString("F3", ci2) : "null") + ",\"on\":" + Json.Str(hit != null ? hit.Value.collider.name : "") + "}";
                }
                case "ground":
                    // Terrain height at (x,z): ray from far above, first STATIC collider that isn't the player.
                    // {"hit":false} if the ground there isn't generated yet.
                    var gz = arg.Split(' ');
                    return Ground(float.Parse(gz[0]), float.Parse(gz[1]));
                case "loopprof":
                    if (arg == "start") return PlayerLoopProfiler.Start();
                    if (arg.StartsWith("stop")) return PlayerLoopProfiler.Stop(arg.Length > 5 ? int.Parse(arg.Substring(5)) : 40);
                    return "{\"error\":\"loopprof start|stop [n]\"}";
                case "profile":
                    if (arg == "start") return TLDRevamp.ScriptProfilerTools.Start(null);
                    if (arg.StartsWith("start ")) return TLDRevamp.ScriptProfilerTools.Start(arg.Substring(6).Split(' '));
                    if (arg.StartsWith("stop")) return TLDRevamp.ScriptProfilerTools.Stop(arg.Length > 5 ? int.Parse(arg.Substring(5)) : 25);
                    return "{\"error\":\"ground x z profile start [Type.Method ...]|stop [n]\"}";
                case "call":
                    var ca = arg.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    return "{\"value\":" + BridgeReflect.Call(ca[0], ca.Skip(1).ToArray()) + "}";
                case "bg":
                    // Keep simulating/rendering when the window is unfocused (needed for unattended benchmarks)
                    if (arg.Length > 0) Application.runInBackground = arg == "on";
                    return "{\"runInBackground\":" + (Application.runInBackground ? "true" : "false") + ",\"focused\":" + (Application.isFocused ? "true" : "false") + "}";
                case "overlay": p.Overlay.Visible = arg != "off"; return "{\"ok\":true}";
                case "timescale": Time.timeScale = float.Parse(arg); return "{\"ok\":true}";
                case "carmove":
                {
                    // nearest car to the player (or the car of network id cm[6]): put it at x y z facing yaw, at rest (scenario staging)
                    var cm = arg.Split(' ');
                    var cic = System.Globalization.CultureInfo.InvariantCulture;
                    var car = cm.Length > 6 ? Net.Entities.RootOfNet(uint.Parse(cm[6], cic)) : Net.EntityLookup.NearestCar();
                    if (car == null) return "{\"error\":\"no car\"}";
                    var crb = car.GetComponent<Rigidbody>();
                    var cpos = new Vector3(float.Parse(cm[0], cic), float.Parse(cm[1], cic), float.Parse(cm[2], cic));
                    float roll = cm.Length > 4 ? float.Parse(cm[4], cic) : 0f;   // e.g. 180 = upside down (crash tests)
                    car.transform.SetPositionAndRotation(cpos, Quaternion.Euler(0f, float.Parse(cm[3], cic), roll));
                    float cspeed = cm.Length > 5 ? float.Parse(cm[5], cic) : 0f;   // m/s along the car's forward (crash tests)
                    if (crb != null && !crb.isKinematic) { crb.velocity = car.transform.forward * cspeed; crb.angularVelocity = Vector3.zero; }
                    // launched: off the handbrake, or a parked car stops dead within half a second (partsync F: 8 m/s, moved 0 m)
                    if (cspeed > 0f && car.car != null) car.car.BhandBrake = false;
                    return "{\"ok\":true}";
                }
                case "teleportyaw":
                {
                    var ty = arg.Split(' ');
                    var tic = System.Globalization.CultureInfo.InvariantCulture;
                    if (mainscript.s == null || mainscript.s.player == null) return "{\"error\":\"not in game\"}";
                    mainscript.s.player.Teleport(new Vector3(float.Parse(ty[0], tic), float.Parse(ty[1], tic), float.Parse(ty[2], tic)), new Vector3(0f, float.Parse(ty[3], tic), 0f));
                    mainscript.s.player.FxRot = 0f;
                    return "{\"ok\":true}";
                }
                case "teleport":
                    var xyz = arg.Split(' ');
                    if (mainscript.s == null || mainscript.s.player == null) return "{\"error\":\"not in game\"}";
                    mainscript.s.player.Teleport(new Vector3(float.Parse(xyz[0]), float.Parse(xyz[1]), float.Parse(xyz[2])));
                    return "{\"ok\":true}";
                case "quality":
                    if (arg.Length > 0) QualitySettings.SetQualityLevel(int.Parse(arg), true);
                    return "{\"level\":" + QualitySettings.GetQualityLevel() + ",\"shadowDistance\":" + QualitySettings.shadowDistance + ",\"vSync\":" + QualitySettings.vSyncCount + ",\"targetFps\":" + Application.targetFrameRate + "}";
                case "help":
                    return "{\"commands\":\"alive ping stats scene breakdown net loopprof start|stop [n] reset report <note> screenshot overlay on|off bg on|off ground x z profile start [Type.Method ...]|stop [n] " + VR.VrBridgeCommandPolicy.Syntax + " vr devaccess switches [text] get <Type.member.path> set <path> <value> call <path.Method> [args] timescale <f> teleport x y z quality [n] " + string.Join(" ", Commands.Keys) + "\"}";
                default: return "{\"error\":\"unknown command, try help\"}";
            }
        }
    }
}
