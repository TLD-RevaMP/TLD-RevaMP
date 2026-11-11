using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace TLDRevamp.Fixes
{
    /// "EVERYTHING should be frozen on load" (Volkan, 2026-11-11). The game spawns a save's items frozen and its streamer
    /// lets them go a second later, while the ground, the buildings and the trailers' hitches are still being made; a
    /// trailer's grip (its own body between the trailer and the car) is never frozen at all. His red pickup with a car
    /// trailer hitched blew apart at every load: in the first physics step after the streamer let them go, the grip went
    /// to 113 m/s, the trailer to 101 and the pickup to 67, the trailer into the car parked beside it (tools/loadfreeze.py).
    /// Nothing overlapped at that moment and the hitch was lined up; the grip is made 1.4 km away (attachgripscript.FStart,
    /// where the trailer's prefab was) and only then put on the ball, held on its two joints in between.
    ///
    /// After a world loads (solo or to host; a player joining has Net.JoinLoading):
    ///  - nothing is let go: the streamer's unfreeze waits (UnfreezeOnGround checks Holding), and every other moving
    ///    body (trailer grips, doors, anything) is frozen until the release;
    ///  - the release comes once the streamer has placed what is around (two passes), the player has ground under the
    ///    feet, and every car and trailer within the unfreeze distance has ground under it (UnfreezeOnGround.OnGround),
    ///    at least MinS after the world's first frame, at most MaxS;
    ///  - everything is let go in the same frame (the streamer's pass run there; hitched pairs go together), then for
    ///    SettleS every body's speed is held to SettleMs and overlaps are pushed apart at SettleDepen: whatever was saved
    ///    a little inside something, or with a joint a little apart, eases out instead of being thrown.
    ///
    /// Runtime switch (bridge `set`): TLDRevamp.Fixes.LoadFreeze.Enabled (A/B: false = the game's own release)
    [HarmonyPatch(typeof(fpscontroller), "Update")]
    public static class LoadFreeze
    {
        public static bool Enabled = true;
        public static float MinS = 2f, MaxS = 30f, SettleS = 1.5f, SettleMs = 3f, SettleTurn = 3f, SettleDepen = 1f;
        public static long Loads, Releases, TimedOut, Frozen, Clamped;
        public static float ClampedTop;
        public static string Last = "", Waiting = "";
        private static mapSettingScript _map;
        private static bool _holding;
        private static int _passes;
        private static float _first = -1, _checkAt, _settleUntil;
        private static readonly List<Rigidbody> _frozen = new List<Rigidbody>();
        private static readonly List<KeyValuePair<Rigidbody, float>> _settle = new List<KeyValuePair<Rigidbody, float>>();

        /// A world is loading here and nothing may be let go yet.
        internal static bool Holding => _holding;

        [HarmonyPostfix]
        private static void Postfix(fpscontroller __instance)
        {
            var map = menuhandler.s != null ? menuhandler.s.currentMainMap : null;
            if (map != _map)
            {
                _map = map; _frozen.Clear(); EndSettle();
                _first = -1; _passes = 0; _checkAt = 0; Waiting = "";
                _holding = map != null && Enabled && Net.Mp.Client == null;
                if (_holding) Loads++;
            }
            if (!_holding) return;
            if (!Enabled || Net.Mp.Client != null || Net.JoinLoading.Showing) { Release("switched off or joining", false); return; }
            var pl = mainscript.s != null ? mainscript.s.player : null;
            if (pl == null || __instance != pl || !map.firstRun || savedatascript.s == null) return;
            float now = Time.realtimeSinceStartup;
            if (_first < 0) _first = now;
            if (now < _checkAt) return;
            _checkAt = now + 0.25f;
            FreezeMoving(pl);
            if (now - _first > MaxS) { TimedOut++; Release("timed out, waiting for " + Waiting, true); return; }
            if (now - _first >= MinS && Ready(pl)) Release("ready", true);
        }

        /// The streamer's passes since the world's first frame (it places and lets go once a second).
        [HarmonyPatch(typeof(itemPlaceRemoveScript), nameof(itemPlaceRemoveScript.RemoveStuff))]
        private static class Passes
        {
            [HarmonyPostfix] private static void Postfix() { if (_holding && _first >= 0) _passes++; }
        }

        private static void FreezeMoving(fpscontroller pl)
        {
            var own = pl.transform.root;
            foreach (var rb in Object.FindObjectsOfType<Rigidbody>())
            {
                if (rb == null || rb.isKinematic || rb.transform.root == own || Kept(rb)) continue;
                rb.velocity = Vector3.zero; rb.angularVelocity = Vector3.zero;
                rb.isKinematic = true;
                _frozen.Add(rb); Frozen++;
            }
        }

        /// In a hand or locked into a container: the game keeps it as it is.
        private static bool Kept(Rigidbody rb)
        {
            var p = rb.GetComponent<pickupable>();
            return p != null && (p.pickedUp || p.inRightHand || p.physlock != null);
        }

        private static readonly RaycastHit[] _hits = new RaycastHit[16];
        private static bool Ready(fpscontroller pl)
        {
            if (_passes < 2) { Waiting = "the item streamer"; return false; }
            var own = pl.transform.root;
            bool ground = false;
            int n = Physics.RaycastNonAlloc(pl.transform.position + Vector3.up * 0.5f, Vector3.down, _hits, LoadGroundHold.GroundM + 0.5f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < n && !ground; i++) ground = _hits[i].collider != null && _hits[i].collider.transform.root != own;
            if (!ground && !pl.Bsitting) { Waiting = "the ground under the player"; return false; }
            float reach = itemPlaceRemoveScript.s != null ? itemPlaceRemoveScript.s.itemUnFreezeDist : 100f;
            var p = pl.transform.position;
            int waiting = 0; string first = null;
            foreach (var it in savedatascript.s.items.Values)
            {
                if (it == null || it.RB == null || !it.RB.isKinematic || it.buried || !UnfreezeOnGround.Hitchable(it)) continue;
                if ((it.transform.position - p).sqrMagnitude > reach * reach) continue;
                if (UnfreezeOnGround.OnGround(it, it.GetInstanceID(), out _)) continue;
                waiting++; first ??= it.name;
            }
            if (waiting > 0) { Waiting = $"the ground under {waiting} vehicle(s) ({first})"; return false; }
            return true;
        }

        private static void Release(string why, bool pass)
        {
            _holding = false; Releases++;
            float t = _first < 0 ? 0f : Time.realtimeSinceStartup - _first;
            int bodies = 0;
            var kinds = new Dictionary<string, int>();
            foreach (var rb in _frozen)
            {
                if (rb == null || Kept(rb)) continue;   // the game froze it meanwhile for its own reasons
                rb.isKinematic = false; rb.velocity = Vector3.zero; rb.angularVelocity = Vector3.zero; bodies++;
                var k = rb.transform.root.name.Replace("(Clone)", "");
                kinds[k] = kinds.TryGetValue(k, out int c) ? c + 1 : 1;
            }
            var top = new List<KeyValuePair<string, int>>(kinds); top.Sort((x, y) => y.Value.CompareTo(x.Value));
            var what = string.Join(", ", top.GetRange(0, System.Math.Min(6, top.Count)).ConvertAll(kv => kv.Key + " " + kv.Value));
            _frozen.Clear();
            // the streamer's own pass, now: everything in its unfreeze distance goes in this frame (the ground rule and
            // hitched pairs still apply)
            if (pass && itemPlaceRemoveScript.s != null) itemPlaceRemoveScript.s.RemoveStuff();
            if (pass) StartSettle();
            Last = $"let go after {t:F1} s ({why}): {bodies} other bodies ({what}), {_settle.Count} settling";
            Plugin.Log.LogInfo("load freeze: " + Last);
        }

        private static void StartSettle()
        {
            EndSettle();
            Clamped = 0; ClampedTop = 0;
            foreach (var rb in Object.FindObjectsOfType<Rigidbody>())
            {
                if (rb == null || rb.isKinematic) continue;
                _settle.Add(new KeyValuePair<Rigidbody, float>(rb, rb.maxDepenetrationVelocity));
                rb.maxDepenetrationVelocity = SettleDepen;
            }
            _settleUntil = Time.realtimeSinceStartup + SettleS;
        }

        private static void EndSettle()
        {
            if (_settle.Count == 0) return;
            foreach (var kv in _settle) if (kv.Key != null) kv.Key.maxDepenetrationVelocity = kv.Value;
            Plugin.Log.LogInfo($"load freeze: settled {_settle.Count} bodies, {Clamped} speeds held back (fastest {ClampedTop:F1} m/s)");
            _settle.Clear();
        }

        /// Plugin.FixedUpdate: the settling bodies' speeds held down, every physics step.
        internal static void FixedTick()
        {
            if (_settle.Count == 0) return;
            if (Time.realtimeSinceStartup > _settleUntil) { EndSettle(); return; }
            foreach (var kv in _settle)
            {
                var rb = kv.Key;
                if (rb == null || rb.isKinematic) continue;
                var v = rb.velocity; float m = v.magnitude;
                if (m > SettleMs) { rb.velocity = v * (SettleMs / m); Clamped++; if (m > ClampedTop) ClampedTop = m; }
                var w = rb.angularVelocity; float wm = w.magnitude;
                if (wm > SettleTurn) rb.angularVelocity = w * (SettleTurn / wm);
            }
        }

        public static string Stats => "{\"enabled\":" + (Enabled ? "true" : "false") + ",\"holding\":" + (_holding ? "true" : "false") + ",\"loads\":" + Loads +
                                      ",\"releases\":" + Releases + ",\"timedOut\":" + TimedOut + ",\"frozen\":" + Frozen + ",\"settling\":" + _settle.Count +
                                      ",\"clamped\":" + Clamped + ",\"clampedTop\":" + ClampedTop.ToString("F1", System.Globalization.CultureInfo.InvariantCulture) +
                                      ",\"waiting\":" + Json.Str(Waiting) + ",\"last\":" + Json.Str(Last) + "}";
    }
}
