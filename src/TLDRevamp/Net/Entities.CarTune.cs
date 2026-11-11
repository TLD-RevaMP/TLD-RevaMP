using System.Collections.Generic;
using UnityEngine;

namespace TLDRevamp.Net
{
    /// A car's tuning (CarTuning: final drive, gear ratios, wheel positions, suspension travel, which wheels drive and
    /// steer) is in no save record: the game never changes it, so the record never needed it. A mod that tunes a car
    /// (Runden's Suspension Tuner, EngineTuner, M-ultiTool) did it on one machine only: the owner drove the tuned car,
    /// everyone else saw its wheels where the prefab has them, and whoever drove it next drove it stock.
    ///
    /// Now the owner looks at its cars every CarTuneEveryS. A car whose tuning changed (or that differs from its prefab
    /// the first time it is looked at) goes out as CarTune: the server checks it comes from the owner, keeps the latest
    /// per car, and passes it on; later players get it with the car (SendObject). Every machine keeps what it applied,
    /// per car, and applies it again whenever the game places the car anew (away and back, the far store, the owner's own
    /// car streamed out and in), before the owner could mistake the prefab's values for a change. A copy's wheels are
    /// placed from its own colliders (ProxyWheelGraphics), so with the owner's tuning applied they sit where the owner's do.
    /// Kept with the world save too (CarTuneSave). A/B: CarTuneSync false = before (nothing sent, nothing applied).
    public static partial class Entities
    {
        public const byte CarTune = 113;   // both: u32 net, CarTuning
        public static bool CarTuneSync = true;
        public static float CarTuneEveryS = 0.5f;
        /// An owner sends a tuned car's tuning again this often (and once soon after it took the car over): a CarTune the
        /// server refused (sent while a claim was still provisional) or lost is replaced by the next one.
        public static float CarTuneResendS = 30f;
        public const int MaxTuneBytes = 2048;
        public static long TunesSent, TunesResent, TunesApplied, TunesReapplied, TunesReplayed, TunesRefused, TunesKept;
        public static string TuneLast = "";

        private sealed partial class SEnt { public byte[] Tune; }

        /// This machine's tuning per car: what it applied or sent, and the placing it went on.
        private sealed class TuneHere { public CarTuning T; public byte[] Bytes; public int Key; public Transform Done; public float SentAt = -1000f; }
        private static readonly Dictionary<uint, TuneHere> _tuneHere = new Dictionary<uint, TuneHere>();
        private static readonly Dictionary<int, int> _prefabTuneKey = new Dictionary<int, int>();
        private static readonly List<uint> _tuneGone = new List<uint>();
        private static float _tuneAt;

        // ------------------------------------------------------------------ the car's values

        /// The car's own wheel colliders, in hierarchy order (the same prefab gives the same order on every machine);
        /// a collider under another car's script (a trailer's, a car in a bed) is not this car's.
        internal static List<WheelCollider> TuneWheels(carscript c)
        {
            var list = new List<WheelCollider>();
            foreach (var w in c.GetComponentsInChildren<WheelCollider>(true))
                if (w != null && w.GetComponentInParent<carscript>(true) == c) list.Add(w);
            return list;
        }

        private static bool Has(List<WheelCollider> l, WheelCollider w) => l != null && l.Contains(w);

        internal static CarTuning CaptureTuning(carscript c)
        {
            var t = new CarTuning { Diff = c.differentialRatio };
            if (c.gears != null)
            {
                t.Gears = new float[Mathf.Min(c.gears.Length, CarTuning.MaxGears)];
                for (int i = 0; i < t.Gears.Length; i++) t.Gears[i] = c.gears[i] != null ? c.gears[i].ratio : 0f;
            }
            var ws = TuneWheels(c);
            t.Wheels = new CarTuning.Wheel[Mathf.Min(ws.Count, CarTuning.MaxWheels)];
            for (int i = 0; i < t.Wheels.Length; i++)
            {
                var w = ws[i]; var lp = w.transform.localPosition; var ce = w.center;
                t.Wheels[i] = new CarTuning.Wheel
                {
                    Name = w.name, X = lp.x, Y = lp.y, Z = lp.z, Sus = w.suspensionDistance, CX = ce.x, CY = ce.y, CZ = ce.z,
                    Roles = (byte)((Has(c.WHDriven, w) ? CarTuning.Driven : 0) | (Has(c.WHSteer, w) ? CarTuning.Steer : 0) |
                                   (Has(c.WHInvSteer, w) ? CarTuning.InvSteer : 0) | (Has(c.WHBrakeOnly, w) ? CarTuning.BrakeOnly : 0) |
                                   (Has(c.WHHandBrake, w) ? CarTuning.HandBrake : 0))
                };
            }
            return t;
        }

        private static void Role(ref List<WheelCollider> list, WheelCollider w, bool on)
        {
            if (on) { if (list == null) list = new List<WheelCollider>(); if (!list.Contains(w)) list.Add(w); }
            else if (list != null) list.Remove(w);
        }

        /// The tuning onto the car: wheels matched by index and name (another prefab's wheel is left alone); gear ratios
        /// only when the car has as many gears (a gear is also a stick position). Returns the wheels it matched.
        internal static int ApplyTuning(carscript c, CarTuning t)
        {
            c.differentialRatio = t.Diff;
            if (c.gears != null && c.gears.Length == t.Gears.Length)
                for (int i = 0; i < t.Gears.Length; i++) if (c.gears[i] != null) c.gears[i].ratio = t.Gears[i];
            var ws = TuneWheels(c);
            int matched = 0;
            for (int i = 0; i < ws.Count && i < t.Wheels.Length; i++)
            {
                var w = ws[i]; var x = t.Wheels[i];
                if (w.name != x.Name) continue;
                matched++;
                w.transform.localPosition = new Vector3(x.X, x.Y, x.Z);
                w.suspensionDistance = x.Sus;
                w.center = new Vector3(x.CX, x.CY, x.CZ);
                bool wasDriven = Has(c.WHDriven, w);
                Role(ref c.WHDriven, w, (x.Roles & CarTuning.Driven) != 0);
                Role(ref c.WHSteer, w, (x.Roles & CarTuning.Steer) != 0);
                Role(ref c.WHInvSteer, w, (x.Roles & CarTuning.InvSteer) != 0);
                Role(ref c.WHBrakeOnly, w, (x.Roles & CarTuning.BrakeOnly) != 0);
                Role(ref c.WHHandBrake, w, (x.Roles & CarTuning.HandBrake) != 0);
                if (wasDriven && (x.Roles & CarTuning.Driven) == 0) w.motorTorque = 0f;   // the game only writes driven wheels' torque
            }
            return matched;
        }

        /// The prefab's tuning key: a car that matches it has nothing to tell.
        internal static int PrefabTuneKey(tosaveitemscript root)
        {
            if (_prefabTuneKey.TryGetValue(root.id, out int k)) return k;
            var go = itemdatabase.s != null ? itemdatabase.s.Item(root.id) : null;
            var pc = go != null ? go.GetComponent<carscript>() : null;
            k = pc != null ? CaptureTuning(pc).Key() : 0;
            _prefabTuneKey[root.id] = k;
            return k;
        }

        // ------------------------------------------------------------------ every machine

        private static bool IsTunableCar(Ent e) => e.PartIndex < 0 && e.Root != null && e.Root.car != null;

        /// Every CarTuneEveryS: known tunings onto cars placed anew, then the owner's own cars checked for a change.
        private static void CarTuneTick()
        {
            if (!CarTuneSync || Time.realtimeSinceStartup < _tuneAt) return;
            _tuneAt = Time.realtimeSinceStartup + CarTuneEveryS;
            _tuneGone.Clear();
            foreach (var kv in _tuneHere)
            {
                if (!ByNet.TryGetValue(kv.Key, out var e)) { _tuneGone.Add(kv.Key); continue; }
                if (!Resolve(e) || !IsTunableCar(e) || kv.Value.Done == e.Root.transform) continue;
                ApplyTuning(e.Root.car, kv.Value.T);
                kv.Value.Done = e.Root.transform;
                kv.Value.Key = CaptureTuning(e.Root.car).Key();
                TunesReapplied++;
            }
            foreach (uint net in _tuneGone) _tuneHere.Remove(net);
            foreach (var e in Placed())
            {
                if (e.Proxy || !IsTunableCar(e)) continue;
                var c = e.Root.car;
                var t = CaptureTuning(c);
                int key = t.Key();
                bool resend = false;
                if (_tuneHere.TryGetValue(e.NetId, out var th))
                {
                    if (key == th.Key)
                    {
                        if (Time.realtimeSinceStartup - th.SentAt < CarTuneResendS || key == PrefabTuneKey(e.Root)) continue;
                        resend = true;
                    }
                }
                else if (key == PrefabTuneKey(e.Root)) continue;   // stock: nothing to tell
                var bytes = t.ToBytes();
                if (bytes.Length > MaxTuneBytes) continue;
                _tuneHere[e.NetId] = new TuneHere { T = t, Bytes = bytes, Key = key, Done = e.Root.transform, SentAt = Time.realtimeSinceStartup };
                W.Reset(); W.U8(CarTune); W.U32(e.NetId); W.Bytes(bytes, 0, bytes.Length);
                ToServer(W, true);
                if (resend) TunesResent++; else TunesSent++;
                TuneLast = (resend ? "resent " : "sent ") + e.NetId + " " + e.Root.name;
                SessionLog.Event("tune-sent", e.NetId, count: bytes.Length);
            }
        }

        /// A car's tuning from its owner (or replayed with the car): kept, and applied if the car is here.
        private static void ApplyCarTune(NetReader r)
        {
            uint net = r.U32();
            int off = r.Pos, len = r.Remaining;
            if (r.Bad || len > MaxTuneBytes) { TunesRefused++; return; }
            var t = CarTuning.FromBytes(r.Buf, off, len);
            if (t == null) { TunesRefused++; TuneLast = "unreadable " + net; return; }
            var bytes = new byte[len]; System.Buffer.BlockCopy(r.Buf, off, bytes, 0, len);
            var th = new TuneHere { T = t, Bytes = bytes, Key = t.Key() };
            _tuneHere[net] = th;
            if (!CarTuneSync || !ByNet.TryGetValue(net, out var e) || !Resolve(e) || !IsTunableCar(e)) return;   // the tick applies it once placed
            int matched = ApplyTuning(e.Root.car, t);
            th.Done = e.Root.transform;
            th.Key = CaptureTuning(e.Root.car).Key();
            TunesApplied++; TuneLast = "applied " + net + " " + e.Root.name + " (" + matched + "/" + t.Wheels.Length + " wheels)";
            SessionLog.Event("tune-applied", net, count: matched);
        }

        // ------------------------------------------------------------------ server

        /// Only the car's owner tunes it; the latest is kept for whoever gets the car later. False: dropped, not passed on.
        private static bool ServerKeepTune(int from, NetReader r)
        {
            if (!CarTuneSync) return false;
            uint net = r.U32();
            int off = r.Pos, len = r.Remaining;
            if (r.Bad || len > MaxTuneBytes || !Server.TryGetValue(net, out var se) || se.OwnerId != from || CarTuning.FromBytes(r.Buf, off, len) == null)
            {
                TunesRefused++;
                SessionLog.Event("tune-refused", net, from: from);
                return false;
            }
            se.Tune = new byte[len]; System.Buffer.BlockCopy(r.Buf, off, se.Tune, 0, len);
            TunesKept++;
            return true;
        }

        private static void SendTuneTo(int playerId, SEnt se)
        {
            if (se.Tune == null || !CarTuneSync) return;
            W.Reset(); W.U8(CarTune); W.U32(se.NetId); W.Bytes(se.Tune, 0, se.Tune.Length);
            ServerSendTo(playerId, W, true);
            TunesReplayed++;
        }

        private static void CarTuneReset() { _tuneHere.Clear(); _prefabTuneKey.Clear(); }

        /// For the world save (CarTuneSave): the local car id of every shared car with a known tuning, and its bytes.
        internal static void KnownTunings(Dictionary<uint, byte[]> into)
        {
            foreach (var kv in _tuneHere)
                if (ByNet.TryGetValue(kv.Key, out var e) && e.RootId != 0) into[e.RootId] = kv.Value.Bytes;
        }

        /// Shared cars this machine keeps a tuning for, by local id (CarTuneSave leaves those to the session).
        internal static void TunedRootIds(HashSet<uint> into)
        {
            foreach (var kv in _tuneHere) if (ByNet.TryGetValue(kv.Key, out var e) && e.RootId != 0) into.Add(e.RootId);
        }

        /// Diagnostics (bridge `mp tune <net>`): the car's tuning here, and what this machine (and the server) keeps.
        public static string CarTuneDiag(uint net)
        {
            if (!ByNet.TryGetValue(net, out var e) || !Resolve(e) || !IsTunableCar(e)) return "{\"error\":\"no such car here\"}";
            var t = CaptureTuning(e.Root.car);
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            var rows = new List<string>();
            foreach (var x in t.Wheels)
                rows.Add("[" + Json.Str(x.Name) + "," + x.X.ToString("F3", ci) + "," + x.Y.ToString("F3", ci) + "," + x.Z.ToString("F3", ci) + "," + x.Sus.ToString("F3", ci) + "," + x.Roles + "]");
            _tuneHere.TryGetValue(net, out var th);
            Server.TryGetValue(net, out var se);
            return "{\"proxy\":" + (e.Proxy ? "true" : "false") + ",\"car\":" + Json.Str(e.Root.name) + ",\"diff\":" + t.Diff.ToString("F3", ci) +
                   ",\"key\":" + t.Key() + ",\"prefabKey\":" + PrefabTuneKey(e.Root) + ",\"known\":" + (th != null ? th.Key.ToString() : "null") +
                   ",\"serverKeeps\":" + (se != null && se.Tune != null ? "true" : "false") + ",\"wheels\":[" + string.Join(",", rows) + "]" +
                   ",\"sent\":" + TunesSent + ",\"resent\":" + TunesResent + ",\"applied\":" + TunesApplied + ",\"reapplied\":" + TunesReapplied + ",\"replayed\":" + TunesReplayed +
                   ",\"refused\":" + TunesRefused + ",\"kept\":" + TunesKept + ",\"last\":" + Json.Str(TuneLast) + "}";
        }
    }
}
