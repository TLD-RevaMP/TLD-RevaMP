using System.Collections.Generic;
using UnityEngine;

namespace TLDRevamp.Net
{
    /// PartWatch for loose items: a shared item lying within PartWatchNearbyM that shows but that the game's pickup ray
    /// (fpscontroller, pickupLayer) can't hit — "I could see it just fine" — can't be picked up, and nothing says why.
    /// An item that can't be picked up at all is one of item health's checks (CheckPickup), so the background watch
    /// (Entities.HealthWatch), Home and the tab all show it. This is the crosshair check: the item under the crosshair
    /// within reach that has no collider the pickup ray hits, or a hidden object in front of it that takes the ray. Not checked: an item
    /// in a hand (anyone's), stored, fitted, a car, or a ghost the mod made on purpose (taken out of a container, a car's
    /// cargo). Each item is reported once: an event (item-untargetable) and an automatic mark (Entities.AutoMark).
    public static partial class Entities
    {
        public static bool ItemWatch = true;   // A/B: false = no checks of loose items
        public static long ItemWatchFlags;
        public static string ItemWatchLast = "";

        private static readonly Dictionary<uint, WatchedPart> _itemWatched = new Dictionary<uint, WatchedPart>();
        private static readonly List<Ent> _nearItems = new List<Ent>();
        private static readonly List<Collider> _iwCols = new List<Collider>();

        private struct ItemReachInfo { public bool Visible, Targetable; public int Cols, Live, Pickup, Held; }

        private static bool Loose(Ent e, fpscontroller pl) =>
            e.Root != null && e.Root.gameObject.activeInHierarchy && e.Root.P != null && e.Root.car == null
            && (e.Root.attachable == null || !e.Root.attachable.attached) && !e.Held && !e.Stored && !e.ReleaseGhost && !e.CargoGhost
            && !InLocalHands(pl, e);

        /// A/B (bridge `set`), the health checks' false alarms of 2026-11-10 (false = before): a part fitted to a car compared
        /// with its last pose from before it was fitted; a lamp held in hand while seated (fpscontroller.inHandP, not
        /// pickedUp) "can't be picked up"; a worn hat's missing body; an item UnfreezeOnGround keeps frozen "kinematic".
        public static bool HealthSkipFitted = true, HealthSkipInHand = true, HealthSkipWorn = true, HealthSkipFrozen = true;

        /// In the local player's hands: picked up, or held in hand (a lamp while seated: inHandP).
        private static bool InLocalHands(fpscontroller pl, Ent e) =>
            pl != null && e.Root != null && e.Root.P != null && (pl.pickedUp == e.Root.P || HealthSkipInHand && pl.inHandP == e.Root.P);

        private static ItemReachInfo ItemReach(Ent e, fpscontroller pl, WatchedPart w)
        {
            var r = new ItemReachInfo();
            _iwCols.Clear(); e.Root.GetComponentsInChildren(true, _iwCols);
            int mask = pl.pickupLayer.value;
            foreach (var c in _iwCols)
            {
                if (c == null) continue;
                r.Cols++;
                bool live = c.enabled && c.gameObject.activeInHierarchy;
                if (live) r.Live++;
                if (c.gameObject.layer == mainscript.pickedUpLayer) r.Held++;
                if (live && (mask & (1 << c.gameObject.layer)) != 0) r.Pickup++;
            }
            r.Targetable = r.Pickup > 0;
            foreach (var rd in w.Renderers) if (rd != null && rd.enabled && rd.gameObject.activeInHierarchy) { r.Visible = true; break; }
            return r;
        }

        /// From PartWatchScan: the loose items near the player.
        private static void ItemWatchScan(fpscontroller pl, float now)
        {
            _nearItems.Clear();
            if (!ItemWatch) return;
            var at = pl.transform.position; float r2 = PartWatchNearbyM * PartWatchNearbyM;
            foreach (var e in ByNet.Values)
                if (e.Root != null && (e.Root.transform.position - at).sqrMagnitude <= r2 && Loose(e, pl)) _nearItems.Add(e);
            List<uint> dead = null;
            foreach (var k in _itemWatched.Keys) if (!ByNet.ContainsKey(k)) (dead ?? (dead = new List<uint>())).Add(k);
            if (dead != null) foreach (var k in dead) _itemWatched.Remove(k);
            // their meshes, for the crosshair check (an item that can't be picked up at all is item health's: CheckPickup)
            foreach (var e in _nearItems)
            {
                if (!_itemWatched.TryGetValue(e.NetId, out var w)) _itemWatched[e.NetId] = w = new WatchedPart();
                w.Renderers.Clear(); e.Root.GetComponentsInChildren(true, w.Renderers);
            }
        }

        /// From PartWatchLook: the loose item under the crosshair.
        private static void ItemWatchLook(fpscontroller pl, float now)
        {
            if (_nearItems.Count == 0) return;
            var cam = pl.Cam.transform; var ray = new Ray(cam.position, cam.forward);
            float reach = pl.FrayRange; Ent best = null; float bestD = float.MaxValue;
            foreach (var e in _nearItems)
            {
                if (e.Root == null || !_itemWatched.TryGetValue(e.NetId, out var w)) continue;
                foreach (var rd in w.Renderers)
                    if (rd != null && rd.enabled && rd.gameObject.activeInHierarchy && rd.bounds.IntersectRay(ray, out float d) && d <= reach && d < bestD) { bestD = d; best = e; }
            }
            foreach (var kv in _itemWatched) if (best == null || kv.Key != best.NetId) kv.Value.LookAt = -1f;
            if (best == null || !Loose(best, pl)) return;
            var bw = _itemWatched[best.NetId];
            var r = ItemReach(best, pl, bw);
            string why = null, blocker = null;
            // a ragdoll's root has no colliders of its own (its bones are unparented, each a pickupable): 0/0 is no problem
            if (!r.Targetable) { if (r.Visible && r.Cols > 0 && !Ragdoll(best.Root)) why = HealthWatchRules.ItemWhy(r.Live, r.Held); }
            else if (Physics.Raycast(ray, out var hit, reach, pl.pickupLayer) && hit.distance < bestD - 0.05f && !hit.collider.transform.IsChildOf(best.Root.transform) && !Shows(hit.collider.transform.root))
            { why = PartWatchRules.Blocked; blocker = hit.collider.transform.root.name; }
            if (why == null) { bw.LookAt = -1f; return; }
            if (bw.LookAt < 0f) bw.LookAt = now;
            else if (now - bw.LookAt >= PartWatchRules.LookAfterS) ItemWatchFound(best, bw, r, why, "crosshair", blocker);
        }

        private static void ItemWatchFound(Ent e, WatchedPart w, ItemReachInfo r, string why, string how, string blocker)
        {
            if (w.Done) return;
            w.Done = true; ItemWatchFlags++;
            ItemWatchLast = PrefabName(e.Root.id) + " net " + e.NetId + ": " + why + " — (" + how + ", colliders " + r.Live + "/" + r.Cols + " on, " + r.Pickup + " on the pickup layer, "
                            + r.Held + " on the held layer" + (blocker != null ? ", in front: " + blocker : "") + ", " + (e.Proxy ? "a copy" : "ours") + ")";
            SessionLog.Event("item-untargetable", e.NetId, from: e.OwnerId, epoch: e.Epoch, count: r.Cols, value: r.Live, dx: r.Pickup, dy: r.Held, result: why);
            Plugin.Log.LogWarning("item watch: " + ItemWatchLast);
            AutoMark("auto: " + ItemWatchLast, e.NetId);
        }
    }
}
