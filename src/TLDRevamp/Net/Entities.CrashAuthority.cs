using System;
using System.Collections.Generic;
using UnityEngine;

namespace TLDRevamp.Net
{
    /// Approach B for car-to-car crashes (switch CrashAuthority; approach A = each machine simulates the crash itself,
    /// Entities.Physical): ONE machine simulates the whole crash. The machine whose own car first touches another
    /// player's physical copy (a real body with its real mass — Entities.Physical) takes that car over on the spot,
    /// keeping the crash's motion, and asks the server (CrashClaim). The server referees in arrival order: the first claim
    /// for a pair of cars wins; a later one (the other machine hitting "back" in its own simulation) is refused and that
    /// machine's copy returns. While a crash lease runs, normal driver-seat claims are refused. The hit player's machine
    /// shows its car as the crash machine simulates it; when the cars have separated or slowed (1–3 s) the crash machine
    /// hands it back (CrashRelease), and the server returns it anyway after 4 s. Both screens show the same crash.
    /// Costs: the hit player's inputs do nothing during the lease, and they see their car's crash one network delay late;
    /// the crash machine decides the crash (fairness: could be restricted to the host).
    public static partial class Entities
    {
        public const byte CrashClaim = 43, CrashRelease = 44;
        public static bool CrashAuthority = true;       // default since v0.57.67 (the user's call after the A/B and relay runs); false = approach A
        public static float CrashMinRelMps = 4f;        // softer contacts (shoving, touching) stay with approach A
        public static float CrashMinHoldS = 1.0f, CrashMaxHoldS = 3.0f, CrashLeaseS = 4.0f;
        public static long CrashClaims, CrashGranted, CrashRefused, CrashReleases, CrashAutoReturned;
        public static string LastCrash = "";
        public static bool CrashLeaveKeep = true;       // A/B: false = before v0.65.56 (crashlease.py)
        public static long CrashLeasesKept;

        /// Test (`mp crashlease <net>`, host): this machine claims `net` as if its car had just hit it — the server's crash
        /// lease without staging a crash.
        public static string TestCrashLease(uint net)
        {
            if (Mp.Server == null) return "{\"error\":\"host only\"}";
            if (!ByNet.TryGetValue(net, out var e) || !e.Proxy) return "{\"error\":\"not another player's object here\"}";
            ServerCrashClaim(MyId, net, 0);
            return "{\"leased\":" + net + ",\"returnTo\":" + (Server.TryGetValue(net, out var se) ? se.CrashReturnTo : -9) + "}";
        }

        /// Test (`mp holdcopy <net> <s>`): this machine's physical copy of `net` stays where it is for `s` seconds while its
        /// owner's poses keep coming — a copy left behind, as the host's copy of the joiner's car was at 10:49 (CrashGuard).
        public static string TestHoldCopy(uint net, float seconds)
        {
            if (!ByNet.TryGetValue(net, out var e) || !e.Proxy) return "{\"error\":\"not another player's object here\"}";
            if (!e.Physical) return "{\"error\":\"not a physical copy (no car simulated here within reach)\"}";
            _testHeld[net] = Time.time + seconds;
            return "{\"held\":" + net + ",\"seconds\":" + seconds.ToString("F1", System.Globalization.CultureInfo.InvariantCulture) + "}";
        }
        private static readonly Dictionary<uint, float> _testHeld = new Dictionary<uint, float>();
        private static bool TestHeld(Ent e) => _testHeld.Count > 0 && _testHeld.TryGetValue(e.NetId, out float until) && (Time.time < until || !_testHeld.Remove(e.NetId));

        private sealed class HeldCrash { public Ent E; public Transform Partner; public float Since, CalmSince = -1f; }
        private static readonly List<HeldCrash> _held = new List<HeldCrash>();

        /// A car handed back after a crash (or refused) is not taken again until it has come apart from the body it hit
        /// (their bounds 0.3 m apart). Before, a car still pressed against it was taken again 1.5–2 s after each return,
        /// over and over, while its own driver's seat claims were refused during each lease (a car driven against another
        /// player's parked bus, 2026-11-10). Contact meanwhile is the ordinary physical-copy contact (shoving).
        public static bool CrashRetakeApart = true;     // A/B: false = before 2026-11-10
        public static float CrashApartCheckS = 0.25f;   // how often a waiting car's bounds are compared (they walk every collider)
        public static long CrashRetakesSkipped;
        private sealed class AfterCrash { public Ent E; public Transform Partner; public float CheckedAt; }
        private static readonly List<AfterCrash> _after = new List<AfterCrash>();

        private static bool WaitingApart(Ent e)
        {
            foreach (var a in _after) if (a.E == e) return true;
            return false;
        }

        private static void AfterHeld(HeldCrash h)
        {
            if (!CrashRetakeApart || h.Partner == null || WaitingApart(h.E)) return;
            _after.Add(new AfterCrash { E = h.E, Partner = h.Partner, CheckedAt = Time.time });
        }

        private static void AfterTick()
        {
            float t = Time.time;
            for (int i = _after.Count - 1; i >= 0; i--)
            {
                var a = _after[i];
                if (!CrashRetakeApart || a.E.Root == null || a.Partner == null) { _after.RemoveAt(i); continue; }
                if (t - a.CheckedAt < CrashApartCheckS) continue;
                a.CheckedAt = t;
                if (Apart(a.E.Root.transform, a.Partner)) _after.RemoveAt(i);
            }
        }

        /// CopyContact: a hard contact between a physical copy and a car simulated here.
        /// A car towing a trailer stays its owner's in a crash: the takeover cascades to the trailer (CascadeHitches), which
        /// took the trailer's joint off and froze it mid-crash, put it back, and did it all again on the return 1-3 s
        /// later. The other car shoves its copy as against any copy. A/B: false = taken as any car (before).
        public static bool CrashSkipTowing = true;
        public static long CrashTowingSkipped;

        internal static bool TryTakeCrash(Ent e, Rigidbody mine, float relSpeed)
        {
            if (HitchTrailer(e)) return false;
            if (CrashSkipTowing && e.Root != null && e.Root.car != null && Fixes.UnfreezeOnGround.Partners(e.Root).Count > 0) { CrashTowingSkipped++; return false; }
            if (!CrashAuthority || !e.Proxy || relSpeed < CrashMinRelMps || e.CrashTaking) return false;
            foreach (var h in _held) if (h.E == e) return false;
            if (CrashRetakeApart && WaitingApart(e)) { CrashRetakesSkipped++; return false; }   // still against what it hit last time
            NetOf(mine.transform.root, out uint partnerNet, out bool partnerProxy);
            if (partnerProxy) return false;
            if (CrashCopyCheck && !CrashGuard.SpeedFits(relSpeed)) return Refuse(e, double.NaN, double.NaN, relSpeed, CrashGuard.TooFast);
            if (CrashCopyCheck && !CopyWhereOwnerHasIt(e)) return false;   // a crash with a stale copy: not a crash for its owner
            e.CrashTaking = true;
            // simulated here from this instant, with the crash's motion as it is (no reset to the copy's last velocity)
            SetProxy(e, false, keepMotion: true);
            // the owner's driver keeps pressing what they pressed: nobody drives it here, so the owner's last wheel torques
            // (brake, a seated player's automatic handbrake hold, throttle) stay on it — without them the hit car rolled
            // free and was pushed 9–12 m/s where single player gives 2–3 (crashtest A/B, v0.57.64)
            if (e.Root.car != null) PhysicalCars[e.Root.car] = e;
            _held.Add(new HeldCrash { E = e, Partner = mine.transform.root, Since = Time.time });
            W.Reset(); W.U8(CrashClaim); W.U32(e.NetId); W.U32(partnerNet);
            ToServer(W, true);
            CrashClaims++;
            LastCrash = "took net " + e.NetId + " (hit by " + mine.transform.root.name + " at " + relSpeed.ToString("F1") + " m/s)";
            return true;
        }

        /// FixedTick: hand crashes back once the cars have separated or slowed.
        internal static void CrashHoldTick()
        {
            AfterTick();
            for (int i = _held.Count - 1; i >= 0; i--)
            {
                var h = _held[i]; var e = h.E;
                if (e.Proxy || e.Root == null) { EndHeld(e); AfterHeld(h); _held.RemoveAt(i); continue; }   // refused / lost
                float t = Time.time;
                var rb = e.Root.GetComponent<Rigidbody>();
                var prb = h.Partner != null ? h.Partner.GetComponent<Rigidbody>() : null;
                bool calm = rb == null || prb == null ||
                            ((rb.velocity - prb.velocity).magnitude < 1.5f && ((rb.position - prb.position).magnitude > 6f || (rb.velocity.magnitude < 2f && prb.velocity.magnitude < 2f)));
                if (calm) { if (h.CalmSince < 0f) h.CalmSince = t; } else h.CalmSince = -1f;
                bool done = t - h.Since > CrashMaxHoldS || (t - h.Since > CrashMinHoldS && h.CalmSince >= 0f && t - h.CalmSince > 0.3f);
                if (!done) continue;
                W.Reset(); W.U8(CrashRelease); W.U32(e.NetId);
                ToServer(W, true);
                CrashReleases++;
                EndHeld(e);
                AfterHeld(h);
                _held.RemoveAt(i);
            }
        }

        public static bool CrashCopyCheck = true;   // A/B: false = before 2026-11-06, any copy could be taken over (CrashGuard)
        public static long CrashStaleRefused;

        /// The copy's body against its owner's newest pose (CrashGuard): taken over, the owner's car would start from here.
        private static bool CopyWhereOwnerHasIt(Ent e)
        {
            var rb = e.Root != null ? e.Root.GetComponent<Rigidbody>() : null;
            double now = Time.realtimeSinceStartupAsDouble;
            if (rb == null || e.Ip == null || !e.Ip.Newest(now, out var owner, out _, out var vel)) return Refuse(e, double.NaN, double.NaN, 0, CrashGuard.NoPose);
            var at = mainscript.GlobalFromUnityPos(rb.position);
            double dx = at.x - owner.x, dy = at.y - owner.y, dz = at.z - owner.z;
            double off = Math.Sqrt(dx * dx + dy * dy + dz * dz), age = e.Ip.NewestAgeS(now);
            if (CrashGuard.CopyFits(off, age, vel.magnitude, e.Ip.DelayMs / 1000.0, out string why, out double allow)) return true;
            return Refuse(e, off, age, allow, why);
        }

        /// The event: value = metres from the owner's pose, dx = the pose's age (s), dy = the metres allowed (contact too
        /// fast: the contact's speed).
        private static bool Refuse(Ent e, double off, double age, double allow, string why)
        {
            CrashStaleRefused++;
            LastCrash = "left net " + e.NetId + " to its owner: " + why + (why == CrashGuard.TooFast ? " (" + allow.ToString("F0") + " m/s)" : double.IsNaN(off) ? "" : " (" + off.ToString("F0") + " m, allowed " + allow.ToString("F0") + ", pose " + age.ToString("F1") + " s old)");
            SessionLog.Event("crash-stale", e.NetId, epoch: e.Epoch, result: why, value: double.IsNaN(off) ? -1f : (float)off,
                             dx: double.IsNaN(age) || double.IsInfinity(age) ? -1f : (float)age, dy: (float)allow);
            return false;
        }

        private static void EndHeld(Ent e)
        {
            e.CrashTaking = false;
            if (e.Root != null && e.Root.car != null && PhysicalCars.TryGetValue(e.Root.car, out var pe) && pe == e && !e.Physical) PhysicalCars.Remove(e.Root.car);
        }

        // ------------------------------------------------------------------ server (host)

        internal static void ServerCrashClaim(int from, uint net, uint partnerNet)
        {
            if (!Server.TryGetValue(net, out var se)) return;
            if (se.HitchCar != 0) { RefuseClaim(from, se, "hitched to car"); return; }
            bool refuse = se.OwnerId == from ? false
                        : se.CrashReturnTo >= 0                                                   // already in someone's crash
                          || (partnerNet != 0 && Server.TryGetValue(partnerNet, out var pe) && pe.CrashReturnTo == from);   // the other machine claimed first
            if (refuse)
            {
                CrashRefused++;
                W.Reset(); W.U8(Owner); W.U32(net); W.VarU32((uint)se.OwnerId); W.U32(se.Epoch);
                ServerSendTo(from, W, true);
                // the owner too (no change for it): it sends a fresh state. The claimer dropped the owner's states while it
                // held the car provisionally — a car that came to rest meanwhile sends nothing more, and the claimer's copy
                // stayed where its own brief simulation left it (3.9 m off, relay head-on, v0.57.65)
                ServerSendTo(se.OwnerId, W, true);
                return;
            }
            if (se.OwnerId == from) return;
            se.CrashReturnTo = se.OwnerId; se.CrashUntil = Time.realtimeSinceStartup + CrashLeaseS;
            OwnerLog(se, from, "crash claim");
            // a claim like any other: the crash machine's part-offs stamped with the old epoch, sent before this reply
            // reached it, still count (ClaimEpochOk) — a wheel thrown off in the first round trip was refused ("epoch")
            se.ClaimedBy = from; se.ClaimedAt = Time.realtimeSinceStartup; se.ClaimedFromEpoch = se.Epoch;
            se.OwnerId = from; se.Epoch++;
            CrashGranted++;
            W.Reset(); W.U8(Owner); W.U32(net); W.VarU32((uint)from); W.U32(se.Epoch);
            ServerSendAll(W, true, -1);
            CascadeOwner(se);
        }

        internal static void ServerCrashRelease(int from, uint net)
        {
            if (!Server.TryGetValue(net, out var se) || se.OwnerId != from || se.CrashReturnTo < 0) return;
            ReturnCrash(se);
        }

        private static void ReturnCrash(SEnt se)
        {
            OwnerLog(se, se.CrashReturnTo, "crash return");
            se.OwnerId = se.CrashReturnTo; se.CrashReturnTo = -1; se.Epoch++;
            W.Reset(); W.U8(Owner); W.U32(se.NetId); W.VarU32((uint)se.OwnerId); W.U32(se.Epoch);
            ServerSendAll(W, true, -1);
            CascadeOwner(se);
        }

        /// Host, twice a second: a crash lease nobody handed back (the crash machine left, a lost release) returns anyway.
        /// Not every physics step: it walks every shared object, 0.3 ms a frame in a world of ~12,000 (tools/perfmp.py).
        private static float _crashTickAt = -1f;
        internal static void ServerCrashTick()
        {
            float now = Time.realtimeSinceStartup;
            if (now - _crashTickAt < 0.5f) return;
            _crashTickAt = now;
            foreach (var se in Server.Values)
                if (se.CrashReturnTo >= 0 && now > se.CrashUntil) { ReturnCrash(se); CrashAutoReturned++; }
        }

        internal static bool InCrashLease(uint net) => Server.TryGetValue(net, out var se) && se.CrashReturnTo >= 0;

        public static string CrashAuthorityStatus() =>
            "{\"enabled\":" + (CrashAuthority ? "true" : "false") + ",\"claims\":" + CrashClaims + ",\"granted\":" + CrashGranted + ",\"refused\":" + CrashRefused +
            ",\"staleRefused\":" + CrashStaleRefused + ",\"releases\":" + CrashReleases + ",\"autoReturned\":" + CrashAutoReturned + ",\"held\":" + _held.Count +
            ",\"retakeApart\":" + (CrashRetakeApart ? "true" : "false") + ",\"waitingApart\":" + _after.Count + ",\"retakesSkipped\":" + CrashRetakesSkipped + ",\"last\":" + Json.Str(LastCrash) + "}";
    }
}
