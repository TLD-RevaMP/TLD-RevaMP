using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace TLDRevamp.Net
{
    /// The game's creatures (newAiScript — the haunted mansion's workers) and everything breakable, in MP.
    ///
    /// A creature is an item like any other: the owner's machine simulates it, the others show a kinematic copy. The game's
    /// creature only ever hunts the LOCAL player plus the official MP's player list (empty in our session), and a copy
    /// doesn't think at all (Update returns on a kinematic body) — so a creature ignored every player but its owner's
    /// (aiprobe run 5: the laptop in its face for 20 s, nothing; the host on the same spot, attacked at once), and the
    /// others saw a statue sliding around: no walk, no attack, no voice.
    /// Now the owner's creature sees remote players by the game's own rules (field of view, range, the first thing its
    /// eye ray meets), takes the closest seen, chases and attacks them the game's way; the damage goes to the victim's
    /// machine (DamageInstant there). The copies get the owner's state (asleep, chasing, attacking, the animation's
    /// walk/turn input, where it looks, its hands) and its voice.
    ///
    /// Breakables (bottles, windows, lamps, the creatures' bodies): a hit on a COPY — a shot, a push by a body this
    /// machine simulates — goes to the owner, which runs the game's own TryBreak there; the owner's Break is replayed on
    /// every copy (sound, slice, the creature's death) without a second gib — the gib is the owner's.
    public static partial class Entities
    {
        public const byte BreakHit = 48, BreakFx = 49, AiState = 50, AiSound = 51, WorldBreak = 112;
        public static long BreakHitsSent, BreakHitsRun, BreakFxSent, BreakFxApplied, AiStatesSent, AiStatesApplied, AiStatesStale, AiSoundsSent, AiSoundsApplied,
                           AiRemoteSeen, AiRemoteAttackFrames, AiDamageSent;

        private static bool _breakFxReplay;   // a copy replaying the owner's Break: no forwarding, no BreakFx back
        private static Vector3 _hitDir;       // the direction a forwarded force hit came from (wakes a creature)
        private static bool _aiSoundReplay;   // a copy playing the owner's creature voice

        // ================================================================ breakables
        /// A/B: false = before: a creature that was ever locked in a car could not die. The lock destroys its limbs' bodies
        /// (ragdollchildpos.Freeze) and the unlock makes new ones, but ragdollactivatescript.RBs still lists the destroyed ones:
        /// SetRagdol throws out of aiscript.Die, on every machine. Not ours to leave: a break made again would throw each time.
        public static bool RagdollListHealed = true;
        public static long RagdollListsHealed;

        [HarmonyPatch(typeof(ragdollactivatescript), nameof(ragdollactivatescript.SetRagdol))]
        private static class RagdollBodiesNow
        {
            [HarmonyPrefix]
            private static void Prefix(ragdollactivatescript __instance)
            {
                if (!RagdollListHealed || __instance.RBs == null) return;
                bool gone = false;
                foreach (var b in __instance.RBs) if (b == null) { gone = true; break; }
                if (!gone) return;
                var now = new List<Rigidbody>();
                foreach (var b in __instance.RBs) if (b != null) now.Add(b);
                var it = __instance.GetComponentInParent<tosaveitemscript>();   // the limbs that came back: the list can't say which
                if (it != null && it.ragdollPos != null && it.ragdollPos.joints != null)
                    foreach (var j in it.ragdollPos.joints)
                    {
                        var b = j != null ? j.GetComponent<Rigidbody>() : null;
                        if (b != null && b != __instance.RB && !now.Contains(b)) now.Add(b);
                    }
                __instance.RBs = now.ToArray();
                RagdollListsHealed++;
            }
        }

        [HarmonyPatch(typeof(breakablescript), nameof(breakablescript.TryBreakShoot))]
        private static class CopyBreakShoot
        {
            [HarmonyPrefix]
            private static bool Prefix(breakablescript __instance, float force, Vector3 dir)
            {
                if (!InSession || _breakFxReplay || !IsProxy(__instance)) return true;
                if (!__instance.noShoot) ForwardHit(__instance, 1, force, dir);
                return false;
            }
        }

        [HarmonyPatch(typeof(breakablescript), nameof(breakablescript.TryBreak), new[] { typeof(float) })]
        private static class CopyBreakForce
        {
            [HarmonyPrefix]
            private static bool Prefix(breakablescript __instance, float force)
            {
                if (!InSession || _breakFxReplay || !IsProxy(__instance)) return true;
                if (RabbitBreakOnCopy(__instance, force)) return false;   // a rabbit's bite: summed (Entities.Rabbits)
                // nothing to break: TryBreak(0) on the owner only woke and turned its creature (DamageFrom)
                if (force <= 0f && CopyTouchFilter) { ZeroHitsIgnored++; return false; }
                if (_blastDepth == 0) ForwardHit(__instance, 2, force, _hitDir);   // inside a blast the owner replays the blast
                return false;
            }
        }

        [HarmonyPatch(typeof(breakablescript), nameof(breakablescript.TryBreak), new[] { typeof(Rigidbody) })]
        private static class CopyBreakBody
        {
            [HarmonyPrefix]
            private static bool Prefix(breakablescript __instance, Rigidbody RB)
            {
                // a copy touched (its trigger: OnTriggerEnter) by a body this machine doesn't simulate — another copy, a
                // rabbit's copy brushing the next one: the owner sees that same touch with its own bodies. Forwarded, each
                // went to the owner as a hit: the "Playing" save's joiner sent 1124 in its first minutes (force 0, every one
                // waking the host's rabbit and turning it, DamageFrom), with nobody near them. As for collisions (CopyBreakCollision).
                if (InSession && !_breakFxReplay && CopyTouchFilter && IsProxy(__instance) && (RB == null || IsProxy(RB))) { CopyTouchesIgnored++; return false; }
                if (RB != null) _hitDir = RB.position - __instance.transform.position;
                if (HitTrace > 0 && InSession && IsProxy(__instance))
                    Plugin.Log.LogInfo("[hittrace] touched by " + (RB != null ? RB.name + " proxy " + IsProxy(RB) : "no body"));
                return true;
            }
            [HarmonyFinalizer]
            private static System.Exception Finalizer(System.Exception __exception, breakablescript __instance, Rigidbody RB)
            {
                _hitDir = Vector3.zero;
                if (__exception != null) BreakThrew(__exception, "TryBreak(Rigidbody)", __instance, RB, (object)RB == null);
                return __exception;
            }
        }
        public static bool CopyTouchFilter = true;   // A/B: false = before (a copy's every trigger touch went to its owner)
        public static long CopyTouchesIgnored, ZeroHitsIgnored;

        [HarmonyPatch(typeof(breakablescript), nameof(breakablescript.TryBreakExplosion))]
        private static class CopyBreakBlast
        {
            [HarmonyPrefix]
            private static bool Prefix(breakablescript __instance) => !InSession || _breakFxReplay || !IsProxy(__instance);
        }

        /// A copy bumped: only a body THIS machine simulates counts (the owner sees its own bodies and the static world).
        [HarmonyPatch(typeof(breakablescript), "OnCollisionEnter")]
        private static class CopyBreakCollision
        {
            [HarmonyPrefix]
            private static bool Prefix(breakablescript __instance, Collision c)
            {
                // the game reads c.collider.gameObject for anything with a creature on it, and a collider destroyed in this physics
                // step is gone by then: NullReferenceException in OnCollisionEnter (monkey runs, 1-3 a run, on both machines)
                if ((__instance.AI != null || __instance.newAI != null) && c.collider == null) { BreakBumpsNoCollider++; return false; }
                if (!InSession || !IsProxy(__instance)) return true;
                if (c.rigidbody == null || IsProxy(c.rigidbody)) return false;
                if (HitTrace > 0)
                {
                    var other = c.rigidbody.transform.root; var oe = EntOfRoot(other);
                    Plugin.Log.LogInfo("[hittrace] bumped by " + c.rigidbody.name + " (root " + other.name + (oe != null ? " net " + oe.NetId + " owner " + oe.OwnerId : " not shared") +
                                       (c.rigidbody.isKinematic ? ", kinematic" : "") + ") at " + c.relativeVelocity.magnitude.ToString("F2") + " m/s");
                }
                _hitDir = c.contactCount > 0 ? c.GetContact(0).point - __instance.transform.position : Vector3.zero;
                return true;
            }
            [HarmonyFinalizer]
            private static System.Exception Finalizer(System.Exception __exception, breakablescript __instance, Collision c)
            {
                _hitDir = Vector3.zero;
                if (__exception != null) BreakThrew(__exception, "OnCollisionEnter", __instance, c != null ? c.collider : null, c == null);
                return __exception;
            }
        }

        public static long BreakBumpsNoCollider;
        /// The game's error in these two methods (both machines, 1-3 a monkey run, sometimes none): the stack stops at the patched
        /// method, so what was null never showed. The facts of the first ones, for the log and the tests.
        public static string BreakThrewLast = "";
        private static int _breakThrewLogs;
        private static void BreakThrew(System.Exception ex, string where, breakablescript b, Object other, bool otherIsNull)
        {
            string body = otherIsNull ? "none" : other == null ? "destroyed" : other.name;
            string self = b == null ? "destroyed" : b.transform.root.name;
            string ai = b == null ? "" : (b.AI != null ? "AI" : b.newAI != null ? "newAI, target " + (b.newAI.target != null) : "no creature");
            BreakThrewLast = $"{where}: {ex.GetType().Name}, other {body}, this {self}, {ai}";
            if (_breakThrewLogs++ < 5) Plugin.Log.LogWarning("[breakable] " + BreakThrewLast);
        }

        public static int BreakTrace;   // test: log the next N breaks with their call stack
        public static int HitTrace;     // test: log the next N hits forwarded to an object's owner, with their call stack

        /// The owner's (or a lone machine's) break goes out to the copies.
        [HarmonyPatch(typeof(breakablescript), nameof(breakablescript.Break))]
        private static class BreakOut
        {
            [HarmonyPrefix]
            private static void Prefix(breakablescript __instance, out bool __state)
            {
                __state = __instance.destroyed;
                if (BreakTrace > 0 && !__state)
                {
                    BreakTrace--;
                    var e = EntOfRoot(__instance.transform.root);
                    var g = mainscript.GlobalFromUnityPos(__instance.transform.position);
                    var rb = __instance.GetComponentInParent<Rigidbody>();
                    Plugin.Log.LogInfo("[breaktrace] " + __instance.transform.root.name + " net " + (e != null ? e.NetId : 0) + " owner " + (e != null ? e.OwnerId : -9) +
                                       " proxy " + IsProxy(__instance) + " at " + g.x.ToString("F1") + "," + g.y.ToString("F1") + "," + g.z.ToString("F1") +
                                       " rb " + (rb == null ? "none" : (rb.isKinematic ? "kin" : "dyn") + " v " + rb.velocity.magnitude.ToString("F2")) + " t " + Time.time.ToString("F1") + "\n" + System.Environment.StackTrace);
                }
            }
            [HarmonyPostfix]
            private static void Postfix(breakablescript __instance, bool __state)
            {
                if (__state || !InSession || _breakFxReplay) return;
                if (IsProxy(__instance))
                {
                    if (BreaksKept) { var c0 = BreakOwner(__instance); SessionLog.Event("break-on-copy", c0 != null ? c0.NetId : 0, result: "not shared"); }
                    return;
                }
                if (!BreakRef(__instance, out uint net, out int bi)) { if (BreaksKept) SessionLog.Event("break-no-object", result: __instance.name); if (WorldBreakGibs) WorldBreakOut(__instance); return; }
                // a member that is an item of its own goes as a member (MemberGone): kept here too, its number would mean another one later
                bool kept = BreaksKept && (__instance.GetComponent<tosaveitemscript>() == null || (ByNet.TryGetValue(net, out var broke) && broke.Root != null && broke.Root.gameObject == __instance.gameObject));
                W.Reset(); W.U8(BreakFx); W.U32(net); W.VarU32((uint)bi); W.U8(kept ? BreakKeep : (byte)0);
                ToServer(W, true); BreakFxSent++;
                if (BreaksKept) SessionLog.Event("break-sent", net, item: bi, result: kept ? "kept" : "member");
                if (kept) KeepBreakHere(net, bi, ByNet.TryGetValue(net, out var here) && here.Root != null ? here.Root.transform : null);
                // the gib is the owner's: the copies replay the break without one, so it goes out as an object of its own
                if (ReshareSurvivors && _spawnedGib(__instance) is GameObject gib && gib != null)
                    foreach (var t in gib.GetComponentsInChildren<tosaveitemscript>(true)) QueueReshare(t);
            }
        }

        /// A piece of the world broken here: a cactus by the road (cut with a katana, shot, run over) is no shared object -
        /// every machine has its own, made from the seed. Before, its piece (the game's gib, cactigib01: an item you can drink
        /// from and carry) existed on the breaker's machine only, and the cactus stood on to the others: a playtest's
        /// cactus, cut, drunk from and put in the bus, was there for one player and not the other (2026-10-24,
        /// tools/busstool.py C). Now the gib is shared as objects of their own (as a shared breakable's is), and the others
        /// break their own copy of that piece of the world - the same name within WorldBreakM of where it stood - without
        /// a gib of their own.
        public static bool WorldBreakGibs = true;   // A/B: false = before v0.69.2
        public static float WorldBreakM = 1.5f;
        public static long WorldBreaksSent, WorldBreaksApplied, WorldBreaksMissed, WorldGibsShared;
        /// A/B: false = before: a piece not generated here when it broke, or a player who joined later, kept it standing.
        public static bool WorldBreaksKept = true;
        public static float WorldBreakNearM = 400f;
        public const int MaxWorldBreaks = 4096;
        public static long WorldBreaksReplayed, WorldBreaksLate, WorldBreakScans;
        private sealed class KeptBreak { public Vector3d G; public string Name; public float NextTry; public float Wait = 2f; }
        private static readonly List<KeptBreak> _worldBreaks = new List<KeptBreak>();          // server: every one of this session
        private static readonly List<KeptBreak> _worldBreaksWaiting = new List<KeptBreak>();   // here: not found yet

        private static void ServerKeepWorldBreak(NetReader r)
        {
            if (!WorldBreaksKept) return;
            var g = new Vector3d(r.F64(), r.F64(), r.F64()); string name = r.Str();
            if (r.Bad || string.IsNullOrEmpty(name) || _worldBreaks.Count >= MaxWorldBreaks) return;
            _worldBreaks.Add(new KeptBreak { G = g, Name = name });
        }

        private static void ServerWorldBreaksTo(int playerId)
        {
            foreach (var b in _worldBreaks)
            {
                W.Reset(); W.U8(WorldBreak); W.F64(b.G.x); W.F64(b.G.y); W.F64(b.G.z); W.Str(b.Name);
                ServerSendTo(playerId, W, true);
                WorldBreaksReplayed++;
            }
        }

        /// Per frame, cheap unless a kept break is near this player and due: one look over the world's breakables for all of them.
        /// A piece that isn't there is looked for less and less often; it may never be (another build's world).
        private static void WorldBreakTick()
        {
            if (_worldBreaksWaiting.Count == 0) return;
            float now = Time.realtimeSinceStartup;
            var here = Mp.LocalGlobal();
            List<KeptBreak> due = null;
            foreach (var b in _worldBreaksWaiting)
            {
                if (now < b.NextTry) continue;
                double dx = b.G.x - here.x, dz = b.G.z - here.z;
                if (dx * dx + dz * dz > WorldBreakNearM * WorldBreakNearM) continue;
                (due ?? (due = new List<KeptBreak>())).Add(b);
            }
            if (due == null) return;
            WorldBreakScans++;
            var all = Object.FindObjectsOfType<breakablescript>();
            foreach (var b in due)
            {
                if (BreakWorldPiece(all, b.G, b.Name)) { _worldBreaksWaiting.Remove(b); WorldBreaksLate++; continue; }
                b.NextTry = now + b.Wait; b.Wait = Mathf.Min(b.Wait * 2f, 60f);
            }
        }

        private static void WorldBreakOut(breakablescript br)
        {
            int pieces = 0;
            if (_spawnedGib(br) is GameObject gib && gib != null)
                foreach (var t in gib.GetComponentsInChildren<tosaveitemscript>(true)) { QueueReshare(t); WorldGibsShared++; pieces++; }
            if (WorldBreaksSent < 20)
                Plugin.Log.LogInfo($"world break: {WorldPieceName(br)} at {mainscript.GlobalFromUnityPos(br.transform.position)}, its gib {(_spawnedGib(br) != null ? _spawnedGib(br).name : "none")}: {pieces} pieces to share");
            // an item nobody shares (not yet, or never: a gib of a gib) isn't the same piece of the world elsewhere
            if (br.GetComponentInParent<tosaveitemscript>() != null) return;
            var g = mainscript.GlobalFromUnityPos(br.transform.position);
            W.Reset(); W.U8(WorldBreak); W.F64(g.x); W.F64(g.y); W.F64(g.z); W.Str(WorldPieceName(br));
            ToServer(W, true); WorldBreaksSent++;
        }

        /// The object's own name: the world generator puts all it makes under one root (ObjGenParent).
        private static string WorldPieceName(breakablescript br)
        {
            string n = br.name;
            return n.EndsWith("(Clone)") ? n.Substring(0, n.Length - 7) : n;
        }

        /// Another machine broke a piece of the world: ours, the same name near the same spot, the game's own Break
        /// (its sound, slice) without the gib - that comes as its own shared object.
        private static void ApplyWorldBreak(NetReader r)
        {
            var g = new Vector3d(r.F64(), r.F64(), r.F64()); string name = r.Str();
            if (r.Bad || string.IsNullOrEmpty(name) || double.IsNaN(g.x) || double.IsNaN(g.y) || double.IsNaN(g.z)) return;
            // a joiner gets every break of the session at once: all of them wait for the same one look, next frame
            if (WorldBreaksKept) { _worldBreaksWaiting.Add(new KeptBreak { G = g, Name = name }); return; }
            if (!BreakWorldPiece(Object.FindObjectsOfType<breakablescript>(), g, name)) WorldBreaksMissed++;
        }

        private static bool BreakWorldPiece(breakablescript[] all, Vector3d g, string name)
        {
            var at = mainscript.UnityPosFromGlobal(g);
            breakablescript best = null; float bd = WorldBreakM;
            foreach (var br in all)
            {
                if (br == null || br.destroyed || br.GetComponentInParent<tosaveitemscript>() != null || WorldPieceName(br) != name) continue;
                float d = Vector3.Distance(br.transform.position, at);
                if (d < bd) { bd = d; best = br; }
            }
            if (best == null) return false;
            var gib = best.Gib; var rgib = best.reducedGib;
            _breakFxReplay = true;
            try { best.Gib = null; best.reducedGib = null; best.Break(); WorldBreaksApplied++; }
            finally { _breakFxReplay = false; if (best != null) { best.Gib = gib; best.reducedGib = rgib; } }
            return true;
        }

        private static void ForwardHit(breakablescript br, byte kind, float force, Vector3 dir)
        {
            if (!BreakRef(br, out uint net, out int bi)) return;
            if (HitTrace > 0)
            {
                HitTrace--;
                var g = mainscript.GlobalFromUnityPos(br.transform.position);
                Plugin.Log.LogInfo("[hittrace] " + br.transform.root.name + " net " + net + " breakable " + bi + " kind " + kind + " force " + force.ToString("F2") +
                                   " at " + g.x.ToString("F1") + "," + g.y.ToString("F1") + "," + g.z.ToString("F1") + " frame " + Time.frameCount + "\n" + System.Environment.StackTrace);
            }
            W.Reset(); W.U8(BreakHit); W.U32(net); W.VarU32((uint)bi); W.U8(kind); W.F32(force); W.F32(dir.x); W.F32(dir.y); W.F32(dir.z);
            ToServer(W, true); BreakHitsSent++;
        }

        private static bool BreakRef(breakablescript br, out uint net, out int bi)
        {
            net = 0; bi = -1;
            if (!BreaksKept)
            {
                var whole = EntOfRoot(br.transform.root);
                if (whole == null) return false;
                bi = System.Array.IndexOf(br.transform.root.GetComponentsInChildren<breakablescript>(true), br);
                net = whole.NetId;
                return bi >= 0;
            }
            var e = BreakOwner(br);
            if (e == null) return false;
            bi = BreakablesOf(e, br).IndexOf(br);   // BreakOut asks after the game's Break: it counts as it did before
            net = e.NetId;
            return bi >= 0;
        }

        /// The object a breakable is part of: not whatever shares its transform's root. A rabbit locked in a bus has the bus as its
        /// root, and its break went out as one of the bus's, numbered among the bus's and everything locked in it (itemchaos
        /// aud-break-late: alive again in 2 of 6 runs once the rabbit was placed on its own).
        private static Ent BreakOwner(breakablescript br)
        {
            for (var it = br.GetComponentInParent<tosaveitemscript>(); it != null;
                 it = it.transform.parent != null ? it.transform.parent.GetComponentInParent<tosaveitemscript>() : null)
            {
                foreach (var e in ByNet.Values) if (e.Root == it) return e;
                foreach (var e in ByNet.Values) if (e.Items != null && e.Items.Contains(it)) return e;
            }
            return null;
        }

        /// The ones a break can still name, in the same order everywhere. The game destroys a broken one at the end of its frame,
        /// so counting all of them gave two breaks in a frame numbers that mean something else a frame later. Counting the
        /// unbroken ones, the numbers hold as long as the breaks are replayed in order, whichever frames they fall in.
        private static List<breakablescript> BreakablesOf(Ent e, breakablescript breaking = null)
        {
            var all = new List<breakablescript>(e.Root.transform.GetComponentsInChildren<breakablescript>(true));
            all.RemoveAll(b =>
            {
                if (b.destroyed && b != breaking) return true;
                var it = b.GetComponentInParent<tosaveitemscript>();
                return it != null && it != e.Root && !e.Items.Contains(it);   // something locked in it is an object of its own
            });
            return all;
        }

        private static breakablescript BreakableOf(uint net, int bi)
        {
            if (!ByNet.TryGetValue(net, out var e) || !Resolve(e) || e.Root == null) return null;
            if (!BreaksKept)
            {
                var whole = e.Root.transform.root.GetComponentsInChildren<breakablescript>(true);
                return bi >= 0 && bi < whole.Length ? whole[bi] : null;
            }
            var all = BreakablesOf(e);
            return bi >= 0 && bi < all.Count ? all[bi] : null;
        }

        private static readonly AccessTools.FieldRef<breakablescript, GameObject> _spawnedGib = AccessTools.FieldRefAccess<breakablescript, GameObject>("spawnedGib");
        private static readonly System.Reflection.MethodInfo DamageFromM = AccessTools.Method(typeof(breakablescript), "DamageFrom");

        /// Test hook (bridge `call TLDRevamp.Net.Entities.TestObjectHit break|explode|shotgun net bi kind`): send the host
        /// what a hit, a set-off or a passenger's controls on object `net` would (tools/objecthits.py). Returns how many
        /// breakables the object has here.
        public static string TestObjectHit(string what, uint net, int bi, int kind)
        {
            W.Reset();
            if (what == "break") { W.U8(BreakHit); W.U32(net); W.VarU32((uint)bi); W.U8((byte)kind); W.F32(1f); W.F32(0f); W.F32(0f); W.F32(0f); }
            else if (what == "explode") { W.U8(ExplodeReq); W.U32(net); W.U8(2); W.VarU32((uint)bi); W.F32(0f); W.Bool(false); }
            else if (what == "shotgun") { W.U8(ShotgunIn); W.U32(net); W.U8(0); W.U8(0); W.U16(0); W.U16(0); }
            else return "break, explode or shotgun";
            ToServer(W, true);
            int n = ByNet.TryGetValue(net, out var e) && Resolve(e) && e.Root != null ? e.Root.transform.root.GetComponentsInChildren<breakablescript>(true).Length : -1;
            return "sent; breakables here " + n;
        }

        /// Test (`mp breakables <r> [name]`): the breakables within r m of our player (how many of each kind, root/gib), the
        /// nearest 20 whose root or gib name has `name` in it - what it is, the
        /// shared object it belongs to (0: none, a piece of the world), whether it is broken, its gib.
        public static string Breakables(float radius, string filter)
        {
            var pl = mainscript.s != null ? mainscript.s.player : null;
            if (pl == null) return "{\"error\":\"no player\"}";
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            var found = new List<KeyValuePair<float, string>>();
            var kinds = new SortedDictionary<string, int>();
            foreach (var br in Object.FindObjectsOfType<breakablescript>())
            {
                if (br == null) continue;
                float d = Vector3.Distance(br.transform.position, pl.transform.position);
                if (d > radius) continue;
                string kind = br.transform.root.name + "/" + (br.Gib != null ? br.Gib.name : "");
                kinds[kind] = kinds.TryGetValue(kind, out int kn) ? kn + 1 : 1;
                if (!string.IsNullOrEmpty(filter) && kind.IndexOf(filter, System.StringComparison.OrdinalIgnoreCase) < 0) continue;
                var e = EntOfRoot(br.transform.root); var g = mainscript.GlobalFromUnityPos(br.transform.position);
                found.Add(new KeyValuePair<float, string>(d, "{\"name\":" + Json.Str(br.name) + ",\"root\":" + Json.Str(br.transform.root.name) + ",\"net\":" + (e != null ? e.NetId : 0) +
                    ",\"destroyed\":" + (br.destroyed ? "true" : "false") + ",\"gib\":" + Json.Str(br.Gib != null ? br.Gib.name : "") + ",\"health\":" + br.health.ToString("F1", ci) +
                    ",\"pos\":[" + g.x.ToString("F2", ci) + "," + g.y.ToString("F2", ci) + "," + g.z.ToString("F2", ci) + "],\"d\":" + d.ToString("F1", ci) + "}"));
            }
            found.Sort((a, b) => a.Key.CompareTo(b.Key));
            if (found.Count > 20) found.RemoveRange(20, found.Count - 20);
            var kl = new List<string>(); foreach (var kv in kinds) kl.Add(Json.Str(kv.Key) + ":" + kv.Value);
            return "{\"kinds\":{" + string.Join(",", kl) + "},\"breakables\":[" + string.Join(",", found.ConvertAll(x => x.Value)) + "]}";
        }

        /// Test (`mp breaknear <name> <r>`): a blow hard enough to break the nearest unbroken breakable whose root name has
        /// `name` in it, through the game's TryBreak (a melee hit's call: the katana's).
        public static string BreakNear(string filter, float radius)
        {
            var pl = mainscript.s != null ? mainscript.s.player : null;
            if (pl == null) return "{\"error\":\"no player\"}";
            breakablescript best = null; float bd = radius;
            foreach (var br in Object.FindObjectsOfType<breakablescript>())
            {
                if (br == null || br.destroyed || (br.transform.root.name + "/" + (br.Gib != null ? br.Gib.name : "")).IndexOf(filter, System.StringComparison.OrdinalIgnoreCase) < 0) continue;
                float d = Vector3.Distance(br.transform.position, pl.transform.position);
                if (d < bd) { bd = d; best = br; }
            }
            if (best == null) return "{\"error\":\"nothing to break\"}";
            var g = mainscript.GlobalFromUnityPos(best.transform.position);
            string name = best.transform.root.name;
            best.TryBreak(Mathf.Max(best.health, 1f) * 2f + 1000f);
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            return "{\"broke\":" + Json.Str(name) + ",\"destroyed\":" + (best != null && best.destroyed ? "true" : "false") +
                   ",\"pos\":[" + g.x.ToString("F2", ci) + "," + g.y.ToString("F2", ci) + "," + g.z.ToString("F2", ci) + "]}";
        }

        private static void ServerBreakHit(int from, NetReader r)
        {
            uint net = r.U32(); int bi = (int)r.VarU32(); byte kind = r.U8();
            if (r.Bad || !Server.TryGetValue(net, out var se) || se.OwnerId == from) return;
            // where the hit thing is: the host's own copy of it if it has one (a creature walks away from where its
            // object's record says), else the object's last position, with a shot's reach for anything
            var br = BreakableOf(net, bi);
            bool exact = br != null;
            var at = exact ? mainscript.GlobalFromUnityPos(br.transform.position) : se.HasState ? se.Pos : se.Where;
            double reach = kind == 1 || !exact ? HitCheck.ObjShotM : HitCheck.ObjForceM;
            if (!HitCheck.AllowOnObject(from, $"breakable {bi} of object {net} {(br != null ? br.transform.root.name : "not placed here")} ({(kind == 1 ? "shot" : kind == 3 ? "bite" : "blow")})", at, reach, exact ? 10f : HitCheck.ObjSizeM)) return;
            r.Pos = 0;
            WS.Reset(); WS.Bytes(r.Buf, 0, r.End);
            ServerSendTo(se.OwnerId, WS, true);
        }

        /// Owner: someone hit our breakable — the game's own call here.
        private static void ApplyBreakHit(NetReader r)
        {
            uint net = r.U32(); int bi = (int)r.VarU32(); byte kind = r.U8(); float force = r.F32();
            var dir = new Vector3(r.F32(), r.F32(), r.F32());
            int reps = kind == 3 ? (int)Mathf.Min(r.VarU32(), 600u) : 1;   // a rabbit's bite: so many frames of force each
            if (r.Bad) return;
            if (float.IsNaN(force) || float.IsInfinity(force) || force < 0f) return;   // as for player damage (PlayerCombat)
            if (float.IsNaN(dir.x) || float.IsNaN(dir.y) || float.IsNaN(dir.z) || float.IsInfinity(dir.sqrMagnitude)) dir = Vector3.zero;
            var br = BreakableOf(net, bi);
            if (br == null || IsProxy(br) || br.destroyed) return;
            BreakHitsRun++;
            if (kind == 1) br.TryBreakShoot(force, dir);
            else
            {
                if (dir != Vector3.zero && DamageFromM != null) DamageFromM.Invoke(br, new object[] { dir });
                for (int i = 0; i < reps && !br.destroyed; i++) br.TryBreak(force);
            }
        }

        private static void ServerFwdAll(int from, NetReader r, bool reliable)
        {
            r.Pos = 0;
            WS.Reset(); WS.Bytes(r.Buf, 0, r.End);
            ServerSendAll(WS, reliable, from);
        }

        /// A copy: the owner's break, replayed with the game's own Break (its sound, slice, a creature's death) — not its gib.
        private static void ApplyBreakFx(NetReader r)
        {
            uint net = r.U32(); int bi = (int)r.VarU32();
            byte flags = r.Remaining > 0 ? r.U8() : (byte)0;
            if (r.Bad) return;
            bool kept = BreaksKept && (flags & BreakKeep) != 0;
            // for a copy that just came: its members that are gone go at the end of this frame, and the numbers count without them
            if (BreaksKept) SessionLog.Event("break-got", net, item: bi, result: ((flags & BreakLate) != 0 ? "late " : "") + (kept ? "kept" : "member") + (ByNet.ContainsKey(net) ? "" : ", no object"));
            if (kept && (flags & BreakLate) != 0) { if (ByNet.ContainsKey(net)) KeepBreakHere(net, bi, null); return; }
            var br = BreakableOf(net, bi);
            if (br == null)
            {
                // stored far here: nothing to break yet, and the owner's message won't come again
                if (kept && ByNet.ContainsKey(net)) KeepBreakHere(net, bi, null);
                return;
            }
            if (br.destroyed || !IsProxy(br)) return;
            BreakQuietly(br);
            if (kept) KeepBreakHere(net, bi, ByNet.TryGetValue(net, out var here) && here.Root != null ? here.Root.transform : null);
        }

        private static void BreakQuietly(breakablescript br)
        {
            var gib = br.Gib; var rgib = br.reducedGib;
            _breakFxReplay = true;
            try { br.Gib = null; br.reducedGib = null; br.Break(); BreakFxApplied++; }
            finally { _breakFxReplay = false; if (br != null) { br.Gib = gib; br.reducedGib = rgib; } }
        }

        /// A/B: false = before: a break reached whoever had the object loaded then. A copy stored far, or a player who got the
        /// object later, stayed unbroken: a break is in no record and no census row.
        public static bool BreaksKept = true;
        public const int MaxBreaksPerObject = 256;
        private const byte BreakKeep = 1, BreakLate = 2;   // BreakFx's last byte
        public static long BreaksReplayed, BreaksAppliedLate;
        /// This machine's breaks, per object: a record has no breaks in it, so whenever the game places the object again (away and
        /// back, stored far when it broke) they are made again, in their order, on what it placed.
        private sealed class BreaksHere { public readonly List<int> Breaks = new List<int>(); public Transform Done; public int Tries; }
        public const int BreakAgainTries = 5;
        public static long BreaksThrewLate;
        private static readonly Dictionary<uint, BreaksHere> _breaksHere = new Dictionary<uint, BreaksHere>();
        private static readonly List<uint> _breaksDone = new List<uint>();
        private static float _breakWaitAt;

        private static void KeepBreakHere(uint net, int bi, Transform doneOn)
        {
            if (!_breaksHere.TryGetValue(net, out var b)) _breaksHere[net] = b = new BreaksHere();
            if (b.Breaks.Count < MaxBreaksPerObject) b.Breaks.Add(bi);
            // broken on this very placing: still counts as done only if nothing was owed on it
            if (doneOn == null) b.Done = null;
            else if (b.Breaks.Count == 1 || b.Done == doneOn) b.Done = doneOn;
        }

        private static void ServerKeepBreak(NetReader r)
        {
            if (!BreaksKept) return;
            uint net = r.U32(); int bi = (int)r.VarU32();
            byte flags = r.Remaining > 0 ? r.U8() : (byte)0;
            if (r.Bad || bi < 0 || (flags & BreakKeep) == 0 || !Server.TryGetValue(net, out var se)) return;
            if (se.Broken == null) se.Broken = new List<int>();
            if (se.Broken.Count < MaxBreaksPerObject) se.Broken.Add(bi);
        }

        private static void SendBreaksTo(int playerId, SEnt se)
        {
            if (se.Broken == null) return;
            foreach (int bi in se.Broken)
            {
                W.Reset(); W.U8(BreakFx); W.U32(se.NetId); W.VarU32((uint)bi); W.U8((byte)(BreakKeep | BreakLate));
                ServerSendTo(playerId, W, true);
                BreaksReplayed++;
            }
        }

        /// Once a second: an object placed anew since its breaks. Its owner may have changed since; the breaks stand.
        private static void BreakWaitTick()
        {
            if (_breaksHere.Count == 0 || Time.realtimeSinceStartup < _breakWaitAt) return;
            _breakWaitAt = Time.realtimeSinceStartup + 1f;
            _breaksDone.Clear();
            foreach (var kv in _breaksHere)
            {
                if (!ByNet.TryGetValue(kv.Key, out var e)) { _breaksDone.Add(kv.Key); SessionLog.Event("breaks-forgotten", kv.Key); continue; }
                if (!Resolve(e) || e.Root == null) continue;
                var placed = e.Root.transform;
                if (kv.Value.Done == placed) continue;
                bool threw = false;
                foreach (int bi in kv.Value.Breaks)
                {
                    var br = BreakableOf(kv.Key, bi);
                    if (br == null || br.destroyed) continue;
                    // the game's Die can throw: that is one object's
                    // trouble. Let out of here, it ended the whole Entities.Tick and every break after this one, every second.
                    try { BreakQuietly(br); BreaksAppliedLate++; SessionLog.Event("break-again", kv.Key, item: bi); }
                    catch (System.Exception ex)
                    {
                        threw = true;
                        if (BreaksThrewLate++ < 5)
                        {
                            var rag = br.GetComponentInParent<ragdollactivatescript>();
                            Plugin.Log.LogWarning($"break {bi} of net {kv.Key} ({br.transform.root.name}) not made again yet: {ex.GetType().Name}, ragdoll body {(rag == null ? "no ragdoll" : rag.RB == null ? "gone" : "there")}");
                        }
                        break;   // the numbers after this one count on it
                    }
                }
                if (threw && ++kv.Value.Tries < BreakAgainTries) continue;
                if (threw) SessionLog.Event("break-gave-up", kv.Key, result: "threw");
                kv.Value.Done = placed; kv.Value.Tries = 0;
            }
            foreach (uint net in _breaksDone) _breaksHere.Remove(net);
        }

        private static void BreaksReset()
        {
            _breaksHere.Clear();
            CarTuneReset();
            _worldBreaks.Clear(); _worldBreaksWaiting.Clear();
        }

        // ================================================================ creatures: the owner sees and attacks remote players
        private static readonly AccessTools.FieldRef<newAiScript, float> ChaseStartF = AccessTools.FieldRefAccess<newAiScript, float>("chaseStart");
        private static readonly AccessTools.FieldRef<newAiScript, float> CurChaseF = AccessTools.FieldRefAccess<newAiScript, float>("currentChaseTime");
        private static readonly RaycastHit[] AiHits = new RaycastHit[32];
        private static readonly Dictionary<int, float> _aiDmgAcc = new Dictionary<int, float>();
        private static readonly Dictionary<int, Vector3> _aiDmgDir = new Dictionary<int, Vector3>();
        private static readonly Dictionary<int, newAiScript> _aiDmgFrom = new Dictionary<int, newAiScript>();   // the creature (the host checks the hit: HitCheck)
        private static float _aiDmgFlushAt;
        private const float BodyR = 0.35f;
        private static newAiScript _aiSawRemote;

        /// Distance along the ray at which it enters remote body (feet..top), or -1; then whether anything the game's
        /// eye ray stops at (everything but glass, layer 23 — and not the creature itself if `skipSelf`) is in front of it.
        private static float SeenAt(newAiScript a, Vector3 from, Vector3 dir, float maxDist, Vector3 feet, Vector3 top, bool skipSelf)
        {
            float t = PlayerCombat.RayCapsule(new Ray(from, dir), feet, top, BodyR);
            if (t < 0f || t > maxDist) return -1f;
            int n = Physics.RaycastNonAlloc(from, dir, AiHits, t, a.lm);
            for (int i = 0; i < n; i++)
            {
                var h = AiHits[i];
                if (h.distance >= t) continue;
                if (skipSelf && h.collider.transform.IsChildOf(a.transform)) continue;
                if (h.collider.gameObject.layer != 23) return -1f;
            }
            return t;
        }

        /// The game's own test for the local player (newAiScript.Update): would it see it, and how far is it?
        private static float LocalSeen(newAiScript a)
        {
            var pl = mainscript.s != null ? mainscript.s.player : null;
            if (pl == null || pl.died) return -1f;
            var pv = pl.transform.position - a.transform.position;
            if (!(Vector3.Angle(a.head.forward, pv) < a.fov)) return -1f;
            int n = Physics.RaycastNonAlloc(a.head.position, pv, AiHits, a.range, a.lm);
            float best = float.MaxValue; bool seen = false;
            for (int i = 0; i < n; i++)
            {
                if (AiHits[i].distance >= best) continue;
                var h = AiHits[i];
                // the first non-glass hit decides
                bool glass = h.collider.gameObject.layer == 23;
                if (!glass) { best = h.distance; seen = h.collider.GetComponentInParent<fpscontroller>() != null; }
            }
            return seen ? pv.magnitude : -1f;
        }

        /// Which remote player each creature here chases (set when it sees them; kept through frames it doesn't).
        private static readonly Dictionary<newAiScript, int> AiChasing = new Dictionary<newAiScript, int>();
        public static bool AiStickyRemote = true;
        public static long AiKeptOnRemote;

        [HarmonyPatch(typeof(newAiScript), "Update")]
        private static class AiUpdate
        {
            [HarmonyPrefix]
            private static void Prefix(newAiScript __instance, out float __state)
            {
                __state = float.NaN;
                _aiSawRemote = null;
                var a = __instance;
                if (!InSession || RemotePlayers.Count == 0 || a.died || a.rb == null || a.rb.isKinematic || a.head == null) return;
                if (gamemodesettingsscript.s == null || gamemodesettingsscript.s.S.peacefulEnemies == 2) return;
                if (a.target == null) { a.TargetNull(); a.TargetReset(); }   // its remote target's body went away
                // the closest remote player the game's rules let it see
                Transform bestHead = null; float bestD = float.MaxValue; ulong bestSteam = 0;
                foreach (var t in RemotePlayers.AiTargets())
                {
                    var pv = t.body.position - a.transform.position;
                    float d = pv.magnitude;
                    if (d >= bestD || !(Vector3.Angle(a.head.forward, pv) < a.fov)) continue;
                    if (SeenAt(a, a.head.position, pv, a.range, t.feet, t.top, false) < 0f) continue;
                    bestD = d; bestHead = t.head; bestSteam = t.steam;
                }
                if (bestHead == null)
                {
                    // Not seen this frame (at grabbing range the sight test can miss: zombies run 7, 2026-10-08). The
                    // game would keep chasing a single player's last place; here its own sight pass would take the
                    // local player instead — the host 30 m off, the victim at arm's length (the creature turned and
                    // walked away). While the game's chase lasts, a farther local player doesn't take the creature.
                    if (!AiStickyRemote || !AiChasing.TryGetValue(a, out int rid) || !a.isChasing || Time.time - ChaseStartF(a) > CurChaseF(a)) { AiChasing.Remove(a); return; }
                    float rd = -1f;
                    foreach (var t in RemotePlayers.AiTargets()) if (t.id == rid) { rd = (t.body.position - a.transform.position).magnitude; break; }
                    float lo = LocalSeen(a);
                    if (rd < 0f || lo < 0f || lo < rd) { AiChasing.Remove(a); return; }   // gone, or the local player is nearer: the game's choice
                    AiKeptOnRemote++;
                    __state = a.fov;
                    a.fov = -1f;
                    return;
                }
                float local = LocalSeen(a);
                if (local >= 0f && local < bestD) { AiChasing.Remove(a); return; }   // the game takes the local player itself
                foreach (var t in RemotePlayers.AiTargets()) if (t.head == bestHead) { AiChasing[a] = t.id; break; }
                var tg = a.target;
                tg.position = bestHead.position;
                tg.SetParent(bestHead);
                a.first = true;
                if (!a.isChasing) { CurChaseF(a) = Random.Range(a.chaseTimeMin, a.chaseTimeMax); a.SoundNotice(); }
                a.isSeeing = true;
                ChaseStartF(a) = Time.time;
                a.isChasing = true;
                a.targetSteamID = bestSteam; a.targetIsSteamID = true;
                _aiSawRemote = a;
                AiRemoteSeen++;
                __state = a.fov;
                a.fov = -1f;   // the game's own sight pass this frame finds nobody: the remote player stays its target
            }

            [HarmonyPostfix]
            private static void Postfix(newAiScript __instance)
            {
                var a = __instance;
                if (!InSession) return;
                if (a.rb != null && a.rb.isKinematic) { if (IsProxy(a)) ShowCopy(a); return; }
                if (_aiSawRemote == a) a.isSeeing = true;   // the game clears it before its sight pass
                if (RemotePlayers.Count == 0 || a.died || a.rb == null || a.currentPlayer != null) return;
                if (gamemodesettingsscript.s == null || gamemodesettingsscript.s.S.peacefulEnemies == 2) return;
                RemoteAttack(a);
            }

            [HarmonyFinalizer]
            private static System.Exception Finalizer(newAiScript __instance, float __state, System.Exception __exception)
            {
                if (!float.IsNaN(__state)) __instance.fov = __state;
                return __exception;
            }
        }

        private static bool SensTouches(sens s, Vector3 feet, Vector3 top)
        {
            if (s == null || s.selfCol == null || !s.selfCol.enabled) return false;
            for (int i = 0; i <= 6; i++)
            {
                var p = Vector3.Lerp(feet, top, i / 6f);
                if ((s.selfCol.ClosestPoint(p) - p).sqrMagnitude <= BodyR * BodyR) return true;
            }
            return false;
        }

        /// The game's attack (newAiScript.Attack) on a remote player its grab sensors reach: a clear line to the body,
        /// both hands on them, damage × deltaTime — sent to the victim ten times a second.
        private static void RemoteAttack(newAiScript a)
        {
            foreach (var t in RemotePlayers.AiTargets())
            {
                if (!SensTouches(a.sensU, t.feet, t.top) && !SensTouches(a.sensD, t.feet, t.top)) continue;
                var dir = t.body.position - a.transform.position;
                if (SeenAt(a, a.transform.position, dir, a.rayAttackDistRange * 2f, t.feet, t.top, true) < 0f) continue;
                a.target.position = t.head.position;
                a.isAttacking = true;
                var hold = (t.head.position + (a.transform.position + a.transform.forward)) * 0.5f;
                a.anim.Hold(hold, leftHand: true);
                a.anim.Hold(hold, leftHand: false);
                _aiDmgAcc.TryGetValue(t.id, out float acc);
                _aiDmgAcc[t.id] = acc + a.damage * Time.deltaTime;
                _aiDmgDir[t.id] = dir;
                _aiDmgFrom[t.id] = a;
                AiRemoteAttackFrames++;
                break;
            }
            if (_aiDmgAcc.Count > 0 && Time.unscaledTime >= _aiDmgFlushAt)
            {
                _aiDmgFlushAt = Time.unscaledTime + 0.1f;
                foreach (var kv in _aiDmgAcc)
                {
                    var c = _aiDmgFrom[kv.Key];
                    if (c == null || !ItemRef(c.tosave != null ? c.tosave : c.GetComponentInParent<tosaveitemscript>(), out uint cNet, out int cIdx)) continue;
                    PlayerCombat.SendInstant(kv.Key, kv.Value, _aiDmgDir[kv.Key], c.transform.position, cNet, cIdx); AiDamageSent++;
                }
                _aiDmgAcc.Clear(); _aiDmgDir.Clear(); _aiDmgFrom.Clear();
            }
        }

        // ================================================================ creatures: copies show the owner's
        private sealed class AiView { public byte Flags; public float Fwd, Turn; public Vector3 Look; }
        private static readonly Dictionary<newAiScript, AiView> AiViews = new Dictionary<newAiScript, AiView>();
        private sealed class AiSent { public byte Flags; public sbyte Fwd, Turn; public Vector3 Look; public float At; public bool Audience; public uint Epoch; public byte RawFlags; public sbyte RawFwd, RawTurn; public Vector3 RawLook; public float RawAt = -10f; }
        private static readonly Dictionary<newAiScript, AiSent> AiLastSent = new Dictionary<newAiScript, AiSent>();
        private static readonly List<newAiScript> AiAll = new List<newAiScript>();
        private static float _aiScanAt, _aiSendAt;
        private static readonly List<newAiScript> _aiGone = new List<newAiScript>();

        private static void ShowCopy(newAiScript a)
        {
            if (!AiViews.TryGetValue(a, out var v)) return;
            a.sleeping = (v.Flags & 1) != 0; a.isChasing = (v.Flags & 2) != 0; a.isSeeing = (v.Flags & 4) != 0; a.isAttacking = (v.Flags & 8) != 0;
            if (a.anim != null)
            {
                a.anim.targetForward = v.Fwd; a.anim.targetTurn = v.Turn;
                a.anim.onGround = (v.Flags & 16) != 0; a.anim.duck = (v.Flags & 32) != 0;
            }
            a.TargetNull();
            if (a.target.parent != null) a.target.SetParent(null);
            a.target.position = a.transform.position + a.transform.rotation * v.Look;
            if (a.isAttacking && a.anim != null)
            {
                var hold = (a.target.position + (a.transform.position + a.transform.forward)) * 0.5f;
                a.anim.Hold(hold, leftHand: true);
                a.anim.Hold(hold, leftHand: false);
            }
            if ((v.Flags & 64) != 0 && !a.died) a.Die(_playSound: false);
        }

        private static byte AiFlags(newAiScript a) =>
            (byte)((a.sleeping ? 1 : 0) | (a.isChasing ? 2 : 0) | (a.isSeeing ? 4 : 0) | (a.isAttacking ? 8 : 0) |
                   (a.anim != null && a.anim.onGround ? 16 : 0) | (a.anim != null && a.anim.duck ? 32 : 0) | (a.died ? 64 : 0));

        /// Per frame: the owner's creatures' state out (10 Hz, on change; once a second regardless — it's unreliable).
        private static void AiTick()
        {
            if (RemotePlayers.Count == 0) return;   // nobody to show them to
            float now = Time.unscaledTime;
            if (now >= _aiScanAt)
            {
                _aiScanAt = now + 2f;
                AiAll.Clear();
                foreach (var e in ByNet.Values)
                    foreach (var it in e.Items) if (it != null && it.npc != null) AiAll.Add(it.npc);
                _aiGone.Clear();
                foreach (var k in AiViews.Keys) if (k == null) _aiGone.Add(k);
                foreach (var k in AiLastSent.Keys) if (k == null) _aiGone.Add(k);
                foreach (var k in _aiGone) { AiViews.Remove(k); AiLastSent.Remove(k); }
            }
            if (now < _aiSendAt) return;
            _aiSendAt = now + 0.1f;
            foreach (var a in AiAll)
            {
                if (a == null || IsProxy(a)) continue;
                if (!ItemRef(a.tosave != null ? a.tosave : a.GetComponentInParent<tosaveitemscript>(), out uint net, out int idx)) continue;
                byte f = AiFlags(a);
                sbyte fw = (sbyte)Mathf.Clamp(Mathf.RoundToInt((a.anim != null ? a.anim.targetForward : 0f) * 127f), -127, 127);
                sbyte tu = (sbyte)Mathf.Clamp(Mathf.RoundToInt((a.anim != null ? a.anim.targetTurn : 0f) * 127f), -127, 127);
                a.TargetNull();
                var look = Quaternion.Inverse(a.transform.rotation) * (a.target.position - a.transform.position);
                if (!AiLastSent.TryGetValue(a, out var ls)) AiLastSent[a] = ls = new AiSent { At = -10f };
                bool rawSend = BandwidthRules.CreatureDue(ls.RawFlags, f, ls.RawFwd == fw && ls.RawTurn == tu && (ls.RawLook - look).sqrMagnitude < 0.04f, now, ls.RawAt);
                if (rawSend) { ls.RawFlags = f; ls.RawFwd = fw; ls.RawTurn = tu; ls.RawLook = look; ls.RawAt = now; }
                if (WasteFixes && a.died) { f = 64; fw = tu = 0; look = Vector3.zero; }
                uint epoch = ByNet.TryGetValue(net, out var ent) ? ent.Epoch : 0;
                bool audience = OwnerAudience(mainscript.GlobalFromUnityPos(a.transform.position));
                bool entered = audience && !ls.Audience; ls.Audience = audience;
                if (!entered && ls.Epoch == epoch && ls.Flags == f && ls.Fwd == fw && ls.Turn == tu && (ls.Look - look).sqrMagnitude < 0.04f && (WasteFixes && a.died || now - ls.At < 1f))
                {
                    if (WasteFixes && a.died && rawSend) StatesSkippedDeadAnimation++;
                    continue;
                }
                if (!InterestRules.Due(audience, ls.Epoch != epoch || ((ls.Flags ^ f) & 65) != 0, now, ls.At, MpServer.CarBeyondInterval))
                { SkippedByInterest(0); continue; }
                ls.Flags = f; ls.Fwd = fw; ls.Turn = tu; ls.Look = look; ls.At = now; ls.Epoch = epoch;
                W.Reset(); W.U8(AiState); W.U32(net); W.VarU32((uint)idx); W.U8(f); W.U8((byte)fw); W.U8((byte)tu);
                W.F32(look.x); W.F32(look.y); W.F32(look.z);
                ToServer(W, false); AiStatesSent++;
            }
        }

        private static newAiScript AiOf(uint net, int idx)
        {
            if (!ByNet.TryGetValue(net, out var e) || !Resolve(e) || e.Items == null || idx < 0 || idx >= e.Items.Count || e.Items[idx] == null) return null;
            return e.Items[idx].GetComponentInChildren<newAiScript>(true);
        }

        private static void ApplyAiState(NetReader r)
        {
            uint net = r.U32(); int idx = (int)r.VarU32(); byte f = r.U8(); sbyte fw = (sbyte)r.U8(), tu = (sbyte)r.U8();
            var look = new Vector3(r.F32(), r.F32(), r.F32());
            if (r.Bad || !Finite(look) || look.sqrMagnitude > 1e6f) return;   // where the copy's creature looks
            var a = AiOf(net, idx);
            if (a == null || !IsProxy(a)) return;
            if (!AiViews.TryGetValue(a, out var v)) AiViews[a] = v = new AiView();
            v.Flags = f; v.Fwd = fw / 127f; v.Turn = tu / 127f; v.Look = look;
            AiStatesApplied++;
        }

        /// The owner's creature speaks: the same clip at the same pitch on every copy; a copy never speaks on its own.
        [HarmonyPatch(typeof(newAiScript), nameof(newAiScript.SoundSound), new[] { typeof(newAiScript.aiSoundType), typeof(int), typeof(float), typeof(bool) })]
        private static class AiVoice
        {
            [HarmonyPrefix]
            private static bool Prefix(newAiScript __instance) => !InSession || _aiSoundReplay || !IsProxy(__instance);

            [HarmonyPostfix]
            private static void Postfix(newAiScript __instance, newAiScript.aiSoundType _soundType, int _index, float _pitch)
            {
                if (!InSession || _aiSoundReplay || IsProxy(__instance)) return;
                if (!ItemRef(__instance.tosave != null ? __instance.tosave : __instance.GetComponentInParent<tosaveitemscript>(), out uint net, out int idx)) return;
                W.Reset(); W.U8(AiSound); W.U32(net); W.VarU32((uint)idx); W.U8((byte)_soundType); W.VarU32((uint)Mathf.Max(0, _index)); W.F32(_pitch);
                ToServer(W, true); AiSoundsSent++;
            }
        }

        private static void ApplyAiSound(NetReader r)
        {
            uint net = r.U32(); int idx = (int)r.VarU32(); int type = r.U8(); int clip = (int)r.VarU32(); float pitch = r.F32();
            if (r.Bad) return;
            var a = AiOf(net, idx);
            if (a == null || !IsProxy(a)) return;
            _aiSoundReplay = true;
            try { a.SoundSound((newAiScript.aiSoundType)Mathf.Clamp(type, 0, 4), clip, pitch, false); AiSoundsApplied++; }
            finally { _aiSoundReplay = false; }
        }

        /// Test readout (bridge `mp aisee`): every creature's view of every remote player, step by step as the owner
        /// tests it — angle vs fov, where its eye ray (vanilla's direction: root to root, from the head) meets the body,
        /// the first thing in front of it.
        /// Closest distance between a ray and a segment (diagnostics).
        private static float RayMiss(Ray ray, Vector3 a, Vector3 b)
        {
            float best = float.MaxValue;
            for (int i = 0; i <= 20; i++)
            {
                var p = Vector3.Lerp(a, b, i / 20f);
                float s = Mathf.Max(0f, Vector3.Dot(p - ray.origin, ray.direction));
                best = Mathf.Min(best, (ray.origin + ray.direction * s - p).magnitude);
            }
            return best;
        }

        public static string AiSee()
        {
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            var rows = new List<string>();
            foreach (var a in Object.FindObjectsOfType<newAiScript>())
            {
                if (a.head == null) continue;
                foreach (var t in RemotePlayers.AiTargets())
                {
                    var pv = t.body.position - a.transform.position;
                    float ang = Vector3.Angle(a.head.forward, pv);
                    float tc = PlayerCombat.RayCapsule(new Ray(a.head.position, pv), t.feet, t.top, BodyR);
                    string block = "";
                    int n = Physics.RaycastNonAlloc(a.head.position, pv, AiHits, tc > 0 ? tc : a.range, a.lm);
                    float bd = float.MaxValue;
                    for (int i = 0; i < n; i++)
                        if (AiHits[i].distance < bd && AiHits[i].collider.gameObject.layer != 23) { bd = AiHits[i].distance; block = AiHits[i].collider.transform.root.name + "/" + AiHits[i].collider.name + " L" + AiHits[i].collider.gameObject.layer + " @" + bd.ToString("F2", ci); }
                    // how far the eye ray passes from the body's axis, and the head's offset from the creature's root
                    float missBy = PlayerCombat.RayCapsule(new Ray(a.head.position, pv), t.feet, t.top, 50f) < 0f ? -1f : RayMiss(new Ray(a.head.position, pv), t.feet, t.top);
                    var ho = a.head.position - a.transform.position; ho.y = 0f;
                    rows.Add("{\"ai\":" + Json.Str(DescribeRoot(a.transform.root)) + ",\"player\":" + t.id + ",\"dist\":" + pv.magnitude.ToString("F2", ci) +
                             ",\"missBy\":" + missBy.ToString("F2", ci) + ",\"headOff\":" + ho.magnitude.ToString("F2", ci) +
                             ",\"angle\":" + ang.ToString("F0", ci) + ",\"fov\":" + a.fov.ToString("F0", ci) + ",\"rayAtBody\":" + tc.ToString("F2", ci) +
                             ",\"headY\":" + a.head.position.y.ToString("F2", ci) + ",\"feetY\":" + t.feet.y.ToString("F2", ci) + ",\"topY\":" + t.top.y.ToString("F2", ci) +
                             ",\"rootDy\":" + pv.y.ToString("F2", ci) + ",\"blockedBy\":" + Json.Str(block) + ",\"seenAt\":" +
                             SeenAt(a, a.head.position, pv, a.range, t.feet, t.top, false).ToString("F2", ci) + ",\"kin\":" + (a.rb != null && a.rb.isKinematic ? "true" : "false") + "}");
                }
            }
            return "{\"see\":[" + string.Join(",", rows) + "]}";
        }

        /// Server: a creature hit's attacker must own the creature (the shared object it belongs to); `where` is the
        /// creature as the host has it (its copy here, else the owner's last state), when it has one.
        internal static bool CreatureOf(int from, uint net, int idx, out Vector3d where, out bool known)
        {
            where = default; known = false;
            if (!Server.TryGetValue(net, out var se) || se.OwnerId != from) return false;
            var a = AiOf(net, idx);
            var ny = a == null && ByNet.TryGetValue(net, out var re) && Resolve(re) ? NyOf(re) : null;
            if (a != null) { where = mainscript.GlobalFromUnityPos(a.transform.position); known = true; }
            else if (ny != null) { where = mainscript.GlobalFromUnityPos(ny.transform.position); known = true; }
            else if (se.HasState) { where = se.Pos; known = true; }
            return true;
        }

        private static void AiReset() { AiViews.Clear(); AiLastSent.Clear(); AiAll.Clear(); _aiDmgAcc.Clear(); _aiDmgDir.Clear(); _aiDmgFrom.Clear(); AiChasing.Clear(); }

        public static string AiStats() => "{\"breakHitsSent\":" + BreakHitsSent + ",\"breakHitsRun\":" + BreakHitsRun + ",\"copyTouchesIgnored\":" + CopyTouchesIgnored + ",\"zeroHitsIgnored\":" + ZeroHitsIgnored + ",\"breakFxSent\":" + BreakFxSent +
                                          ",\"breakFxApplied\":" + BreakFxApplied + ",\"statesSent\":" + AiStatesSent + ",\"statesApplied\":" + AiStatesApplied +
                                          ",\"soundsSent\":" + AiSoundsSent + ",\"soundsApplied\":" + AiSoundsApplied + ",\"remoteSeen\":" + AiRemoteSeen + ",\"keptOnRemote\":" + AiKeptOnRemote +
                                          ",\"remoteAttackFrames\":" + AiRemoteAttackFrames + ",\"damageSent\":" + AiDamageSent +
                                          ",\"damageTaken\":" + PlayerCombat.DamageTaken + "}";
    }
}
