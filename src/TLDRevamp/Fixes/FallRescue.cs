using System.Collections.Generic;
using UnityEngine;

namespace TLDRevamp.Fixes
{
    /// Something this machine simulates that fell through the world comes back up. Parts thrown off a car in a crash pass
    /// through the terrain's thin collider in one physics step at 60 m/s (DevTest 11-10: a rim came off at 223 km/h and lay
    /// 266 m under the ground 7 s later), and an engine sank through the ground on a player's machine (cause not found:
    /// itemchaos aud-regrab doesn't reproduce it); the black box only recorded them, and the things were gone for good.
    ///
    /// Once a second, an object loose in the world (a save item with its own body, not frozen, not in a hand) that is
    /// falling, more than UnderM under the ground's surface where it is, with nothing under it to land on, is put back on
    /// that surface with no speed. Copies of other players' objects are frozen here: their owner rescues them.
    ///
    /// Runtime switch (bridge `set`): TLDRevamp.Fixes.FallRescue.Enabled (A/B: false = they keep falling, as before)
    public static class FallRescue
    {
        public static bool Enabled = true;
        public static float UnderM = 5f, FallingMs = 5f, EveryS = 1f, AboveM = 0.5f;
        public static long Rescued, Checked;
        public static string Last = "";
        private static float _next;
        private static int _groundMask = -1;
        private static readonly List<Collider> _cols = new List<Collider>();

        internal static void Tick()
        {
            if (!Enabled || savedatascript.s == null || savedatascript.s.items == null || Time.realtimeSinceStartup < _next) return;
            _next = Time.realtimeSinceStartup + EveryS;
            if (_groundMask < 0) _groundMask = LayerMask.GetMask(Net.Entities.GroundLayers.Split(','));
            if (_groundMask == 0) return;
            foreach (var it in savedatascript.s.items.Values)
            {
                // cheap tests first: a big world has thousands of items placed
                if (it == null || it.RB == null || it.RB.isKinematic || it.transform.parent != null || it.RB.velocity.y > -FallingMs) continue;
                if (it.P != null && (it.P.pickedUp || it.P.inRightHand || it.P.physlock != null)) continue;   // a locked one follows its container
                Checked++;
                var p = it.transform.position;
                if (!Physics.Raycast(new Vector3(p.x, p.y + 1000f, p.z), Vector3.down, out var top, 2000f, _groundMask, QueryTriggerInteraction.Ignore)) continue;
                if (p.y > top.point.y - UnderM || Below(it, p)) continue;
                Rescue(it, p, top.point.y);
            }
        }

        /// Anything under it (not itself) within 300 m to land on: a cave, a cellar, a building's floor.
        private static bool Below(tosaveitemscript it, Vector3 p)
        {
            var root = it.transform.root;
            foreach (var h in Physics.RaycastAll(p + Vector3.up * 0.5f, Vector3.down, 300f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
                if (h.collider != null && h.collider.transform.root != root) return true;
            return false;
        }

        private static void Rescue(tosaveitemscript it, Vector3 p, float ground)
        {
            // its lowest point AboveM over the surface
            _cols.Clear(); it.transform.GetComponentsInChildren(false, _cols);
            float bottom = p.y;
            foreach (var c in _cols) if (c != null && c.enabled && !c.isTrigger) bottom = Mathf.Min(bottom, c.bounds.min.y);
            var to = new Vector3(p.x, ground + AboveM + (p.y - bottom), p.z);
            float depth = ground - p.y, speed = it.RB.velocity.magnitude;
            it.RB.velocity = Vector3.zero; it.RB.angularVelocity = Vector3.zero;
            it.RB.position = to; it.transform.position = to;
            Rescued++;
            Last = $"{it.name.Replace("(Clone)", "")} {depth:F0} m under the ground at {speed:F0} m/s";
            Plugin.Log.LogWarning($"fell through the world: {Last}, put back on the surface at {mainscript.GlobalFromUnityPos(to)}");
            if (Net.Entities.InSession) { var e = Net.Entities.PlacedOf(it); SessionLog.Event("fall-rescued", e != null ? e.NetId : 0, result: Last); }
        }

        /// Bridge `fallrescue test` (tests): the nearest loose item to the player, not frozen, put 30 m under the ground where it
        /// is, falling at 20 m/s.
        public static string Test()
        {
            var pl = mainscript.s != null ? mainscript.s.player : null;
            if (pl == null || savedatascript.s == null) return "{\"error\":\"no world\"}";
            tosaveitemscript best = null; float bd = float.MaxValue;
            foreach (var it in savedatascript.s.items.Values)
            {
                if (it == null || it.RB == null || it.RB.isKinematic || it.transform.parent != null || it.car != null || (it.P != null && it.P.physlock != null)) continue;
                float d = (it.transform.position - pl.transform.position).sqrMagnitude;
                if (d < bd) { bd = d; best = it; }
            }
            if (best == null) return "{\"error\":\"no loose item near\"}";
            _test = best;
            var p = best.transform.position + Vector3.down * 30f;
            best.RB.position = p; best.transform.position = p; best.RB.velocity = Vector3.down * 20f;
            return "{\"dropped\":" + Json.Str(best.name) + ",\"y\":" + Json.Num(p.y) + "}";
        }

        private static tosaveitemscript _test;
        public static string Stats => "{\"enabled\":" + (Enabled ? "true" : "false") + ",\"rescued\":" + Rescued + ",\"checked\":" + Checked + ",\"last\":" + Json.Str(Last) +
                                      ",\"testUnder\":" + (_test != null && _groundMask > 0 && Physics.Raycast(_test.transform.position + Vector3.up * 1000f, Vector3.down, out var g, 2000f, _groundMask, QueryTriggerInteraction.Ignore) ? Json.Num(g.point.y - _test.transform.position.y) : "null") + "}";
    }
}
