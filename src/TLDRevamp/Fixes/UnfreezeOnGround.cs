using HarmonyLib;
using UnityEngine;

namespace TLDRevamp.Fixes
{
    /// The game's item streaming (itemPlaceRemoveScript, once a second) places stored items within itemSpawnDist and
    /// unfreezes every item within itemUnFreezeDist (tosaveitemscript.DistUnFreezeRB: isKinematic = false) — with no
    /// check that anything is under it yet. Arriving faster than the world generates (a teleport, a join at another
    /// player, a slow machine), items are dropped before the building's floor or the terrain collider exists: they fall
    /// through and are gone (farworld run 4: 242 m; run 10: a building's items 245 m down, on the host arriving).
    ///
    /// An item about to be unfrozen first looks straight down (1 km, solid colliders only, not its own): nothing there →
    /// it stays frozen this second and is asked again the next. Already unfrozen items are not checked again.
    ///
    /// Anything within 1 km was enough before: inside a building whose floor isn't there yet (the game places a building
    /// once the roads around it are done, its items stream in by distance) the ray found the terrain far under the floor,
    /// and the items fell (Volkan, 2026-11-10: "items in building fall under the ground since the buildings ground only
    /// loads later"; his autosave held a car, a bus front and ~30 items 320-410 m under the gas station). Now what it
    /// stands on must be within reach (NearM): the item's lowest collider to the nearest hit below. Out of reach it is held
    /// too, at most HoldMaxS, then let go as before. A/B: Near false = anything within RayM counts (before).
    ///
    /// A car and the trailer hitched to it are let go together, when both have ground (HitchPairs): one let go alone
    /// while the other stays frozen is dragged by the joint. While a world loads nothing is let go (LoadFreeze).
    ///
    /// Runtime switch (bridge `set`): TLDRevamp.Fixes.UnfreezeOnGround.Enabled
    [HarmonyPatch(typeof(tosaveitemscript), nameof(tosaveitemscript.DistUnFreezeRB))]
    public static class UnfreezeOnGround
    {
        public static bool Enabled = true, Near = true;
        public static float RayM = 1000f, NearM = 1.5f, HoldMaxS = 120f;
        public static long Held, Released, HeldFar, TimedOut;
        private static readonly RaycastHit[] _hits = new RaycastHit[16];
        private static readonly System.Collections.Generic.Dictionary<int, float> _held = new System.Collections.Generic.Dictionary<int, float>();
        private static readonly System.Collections.Generic.List<Collider> _cols = new System.Collections.Generic.List<Collider>();

        /// This item is being kept frozen here (nothing within reach under it yet): kinematic on purpose (health checks).
        internal static bool Holding(tosaveitemscript t) => t != null && _held.ContainsKey(t.GetInstanceID());

        [HarmonyPrefix, HarmonyPriority(Priority.Low)]
        private static bool Prefix(tosaveitemscript __instance, bool __runOriginal)
        {
            // an earlier prefix refused (Entities: a network copy): Harmony runs this one anyway. It raycast for every copy
            // in range each second, and could let a copy's hitch partner go against that refusal
            if (!__runOriginal) return false;
            if (Net.Tpa.Holding(__instance.RB) || LoadFreeze.Holding) return false;
            if (!Enabled || __instance.RB == null || !__instance.RB.isKinematic || __instance.buried) return true;
            if (_partnerCall) return true;   // let go with its hitch partner: both were checked there
            int id = __instance.GetInstanceID();
            var p = __instance.transform.position;
            bool ok = OnGround(__instance, id, out float nearest);
            bool something = nearest < float.MaxValue, partner = false;
            // a car and the trailer hitched to it go together: one let go while the other stays frozen drags it on the
            // joint (the frozen one doesn't give), and the one let go is thrown
            if (ok && HitchPairs && Hitchable(__instance))
                foreach (var q in Partners(__instance))
                    if (q.RB != null && q.RB.isKinematic && !q.buried && !OnGround(q, q.GetInstanceID(), out _)) { ok = false; partner = true; break; }
            if (ok)
            {
                if (_held.Remove(id)) Released++;
                _reach.Remove(id);
                if (HitchPairs && Hitchable(__instance)) LetPartnersGo(__instance);
                return true;
            }
            if (_held.Count > 4096) _held.Clear();   // ids of items long gone
            float now = Time.realtimeSinceStartup;
            if (!_held.TryGetValue(id, out float since))
            {
                _held[id] = now;
                Held++; if (something) HeldFar++; if (partner) HeldForPartner++;
                if (Held <= 10) Plugin.Log.LogWarning($"unfreeze held ({(partner ? "what it is hitched to has nothing under it" : something ? $"what is under it is {nearest:F1} m down" : "nothing under it")} yet): {__instance.name} at global {mainscript.GlobalFromUnityPos(p).ToString()}");
            }
            else if (now - since > HoldMaxS)
            {
                // still out of reach, or still nothing under it: what it stood on may be gone for good, or it was saved a
                // little into the ground (a car's pivot under the terrain: the ray starts under a one-sided collider and
                // never finds it, and the car stayed frozen for good); let go as before
                _held.Remove(id); _reach.Remove(id); TimedOut++;
                if (HitchPairs && Hitchable(__instance)) LetPartnersGo(__instance);
                return true;
            }
            return false;
        }

        /// Something solid under the item within its reach (loose items), or anything under it within RayM (items in or on
        /// something). nearest: how far down the first solid thing is (MaxValue: none).
        internal static bool OnGround(tosaveitemscript it, int id, out float nearest)
        {
            var root = it.transform.root;
            var p = it.transform.position;
            int n = Physics.RaycastNonAlloc(p + Vector3.up * 0.5f, Vector3.down, _hits, RayM, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
            nearest = float.MaxValue;   // the hits come in no order
            for (int i = 0; i < n; i++)
            {
                var c = _hits[i].collider;
                if (c == null || c.transform.root == root) continue;
                if (_hits[i].distance < nearest) nearest = _hits[i].distance;
            }
            // the reach rule for loose items only: one in something (a car's trunk, bolted on) has that something as its
            // root, and its whole hierarchy's colliders (a car's ~200) were measured for each such item every second —
            // the streamer's once-a-second hitch grew from ~120 to ~300 ms (DevTest, 2026-11-10)
            bool loose = it.transform.parent == null;
            return nearest < float.MaxValue && (!Near || !loose || nearest <= Reach(it, id, p) + NearM);
        }

        /// Hitched cars and trailers (A/B: HitchPairs false = each on its own, as before).
        public static bool HitchPairs = true;
        public static long HeldForPartner, PartnersLetGo;
        private static bool _partnerCall;
        private static readonly System.Collections.Generic.List<tosaveitemscript> _partners = new System.Collections.Generic.List<tosaveitemscript>();

        /// A car or a trailer (the trailer's record points at its grip's part script, which knows the trailer).
        internal static bool Hitchable(tosaveitemscript it) => it.car != null || (it.attachable != null && it.attachable.utanfuto != null);

        /// The trailers hitched to this car, or the car this trailer is hitched to.
        internal static System.Collections.Generic.List<tosaveitemscript> Partners(tosaveitemscript it)
        {
            _partners.Clear();
            var root = it.transform.root;
            foreach (var g in TrailerGrip.All)
            {
                if (g == null || g.Grip == null || g.tosaveitem == null) continue;
                var at = g.tosaveitem.attachable;
                if (at == null || !at.attached || at.point == null) continue;
                var car = at.point.root;
                if (g.tosaveitem.transform.root == root) { var c = car.GetComponent<tosaveitemscript>(); if (c != null && c != it) _partners.Add(c); }
                else if (car == root && g.tosaveitem != it) _partners.Add(g.tosaveitem);
            }
            return _partners;
        }

        private static void LetPartnersGo(tosaveitemscript it)
        {
            var list = new System.Collections.Generic.List<tosaveitemscript>(Partners(it));
            _partnerCall = true;
            try
            {
                foreach (var q in list)
                {
                    if (q.RB == null || !q.RB.isKinematic) continue;
                    _held.Remove(q.GetInstanceID());
                    q.DistUnFreezeRB();
                    if (!q.RB.isKinematic) PartnersLetGo++;
                }
            }
            finally { _partnerCall = false; }
        }

        /// From the ray's start (0.5 m above the item's position) down to the item's lowest solid collider. A frozen item
        /// doesn't move: measured once while it waits.
        private static readonly System.Collections.Generic.Dictionary<int, float> _reach = new System.Collections.Generic.Dictionary<int, float>();
        private static float Reach(tosaveitemscript it, int id, Vector3 p)
        {
            if (_reach.TryGetValue(id, out float r)) return r;
            _cols.Clear(); it.transform.GetComponentsInChildren(false, _cols);
            float bottom = float.MaxValue;
            foreach (var c in _cols) if (c != null && c.enabled && !c.isTrigger && !(c is WheelCollider)) bottom = System.Math.Min(bottom, c.bounds.min.y);
            r = bottom == float.MaxValue ? 0.5f : System.Math.Max(0f, p.y + 0.5f - bottom);
            if (_reach.Count > 4096) _reach.Clear();
            _reach[id] = r;
            return r;
        }

        public static string Stats => "{\"enabled\":" + (Enabled ? "true" : "false") + ",\"near\":" + (Near ? "true" : "false") + ",\"held\":" + Held + ",\"heldFar\":" + HeldFar +
                                      ",\"released\":" + Released + ",\"timedOut\":" + TimedOut + ",\"waiting\":" + _held.Count +
                                      ",\"hitchPairs\":" + (HitchPairs ? "true" : "false") + ",\"heldForPartner\":" + HeldForPartner + ",\"partnersLetGo\":" + PartnersLetGo + "}";
    }
}
