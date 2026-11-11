using System.Collections.Generic;
using UnityEngine;

namespace TLDRevamp.Net
{
    /// The shared objects placed in the world here (their item loaded), once a frame. A world's shared objects pile up as
    /// players go places, and nearly all of them are stored away far off: the DevTest world of 2026-11-10 had ~12,000
    /// (9,894 items, 1,134 animals, 162 cars), ~25 of them placed. The per-frame loops (hitches, item pushes, copies moved
    /// by body, cargo zones, the collider LOD, resync, the owner's states) each walked all of them for those few, a Unity
    /// null check each: 11 ms a frame on the host (tools/perfmp.py), and in the session 2 physics steps a frame — 35 ms
    /// frames where the same world in 11-06's session (~1,200) took 16. Those loops walk this list now.
    ///
    /// The full pass over every object binds what the game placed since (Resolve: what ResyncFrameTick did for every object
    /// every frame) and drops the destroyed item a stored-away object pointed at (a plain null check after that). It runs
    /// when something may have changed: the game's placed items or the shared objects counted differently, an object bound
    /// or replaced elsewhere (_placedDirty), or every FullEveryFrames frames. Otherwise last frame's list is kept, less what
    /// went: 1.4 ms a frame at the DevTest gas station before, standing. A/B: LoopPlacedOnly false = every loop walks every
    /// object, as before.
    public static partial class Entities
    {
        public static bool LoopPlacedOnly = true;
        public static int FullEveryFrames = 30, DormantEveryFrames = 15;
        public static long PlacedRebuilds, PlacedFullPasses;
        public static int PlacedCount;

        private static readonly List<Ent> _placed = new List<Ent>(), _all = new List<Ent>();
        /// The full pass's indexes: an item, and the item's own transform, to its shared object (a group before a part of
        /// it). Lookups by reference: Unity's == on every object was the cost. Kept with the list; an entry whose object was
        /// stored away since is checked by the lookup.
        private sealed class RefEq<T> : IEqualityComparer<T> where T : class
        {
            public static readonly RefEq<T> I = new RefEq<T>();
            public bool Equals(T a, T b) => ReferenceEquals(a, b);
            public int GetHashCode(T o) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(o);
        }
        private static readonly Dictionary<tosaveitemscript, Ent> _byItem = new Dictionary<tosaveitemscript, Ent>(RefEq<tosaveitemscript>.I);
        private static readonly Dictionary<Transform, Ent> _byOwn = new Dictionary<Transform, Ent>(RefEq<Transform>.I);
        private static int _placedFrame = -1, _fullFrame = -1000, _itemsSeen = -1, _netsSeen = -1, _dormantFrame = -1000;
        private static bool _placedDirty = true;
        /// Host: the server's objects locked in something (a container, a car's bed), which follow it at its states.
        private static readonly HashSet<SEnt> _serverLocked = new HashSet<SEnt>();

        /// This frame's placed objects (every shared object with LoopPlacedOnly off). Valid until the next frame; an object
        /// removed meanwhile can still be in it (its Root then gone: the loops skip it as before).
        private static List<Ent> Placed()
        {
            if (_placedFrame == Time.frameCount) return _placed;
            _placedFrame = Time.frameCount; PlacedRebuilds++;
            if (!LoopPlacedOnly) { _placed.Clear(); _placed.AddRange(ByNet.Values); PlacedCount = _placed.Count; return _placed; }
            int items = savedatascript.s != null && savedatascript.s.items != null ? savedatascript.s.items.Count : -1;
            if (_placedDirty || items != _itemsSeen || ByNet.Count != _netsSeen || Time.frameCount - _fullFrame >= FullEveryFrames)
            {
                _placedDirty = false; _itemsSeen = items; _netsSeen = ByNet.Count; _fullFrame = Time.frameCount; PlacedFullPasses++;
                _placed.Clear(); _byItem.Clear(); _byOwn.Clear();
                foreach (var e in ByNet.Values)
                {
                    if (!ReferenceEquals(e.Root, null) && e.Root == null) StoredAway(e);
                    if (ReferenceEquals(e.Root, null) && (e.RootId == 0 || !Resolve(e))) continue;
                    _placed.Add(e);
                    if (!_byItem.TryGetValue(e.Root, out var o) || (o.PartIndex >= 0 && e.PartIndex < 0)) { _byItem[e.Root] = e; _byOwn[e.Root.transform] = e; }
                }
            }
            else
            {
                // nothing placed or shared since: last frame's, less what was stored away or removed
                for (int i = _placed.Count - 1; i >= 0; i--)
                {
                    var e = _placed[i];
                    if (!ReferenceEquals(e.Root, null) && e.Root == null) StoredAway(e);
                    if (ReferenceEquals(e.Root, null) || !ByNet.TryGetValue(e.NetId, out var now) || now != e) _placed.RemoveAt(i);
                }
            }
            PlacedCount = _placed.Count;
            return _placed;
        }

        /// The game destroyed its item: a plain null from now on, and what a copy moved by its body had to stop
        /// (BodyMotionTick did it for every unplaced object each step), and a physical copy (the physical loop).
        private static void StoredAway(Ent e)
        {
            e.Root = null;
            if (e.MovedByBody) e.MovedByBody = false;
            if (e.Physical) e.Physical = false;
        }

        /// What a lookup searches: this frame's placed objects, or every object when one was shared or bound since the list
        /// was made (it would be missing).
        private static List<Ent> LookupSet()
        {
            var p = Placed();
            return LoopPlacedOnly && (_placedDirty || ByNet.Count != _netsSeen) ? AllEnts() : p;
        }

        /// The shared object whose item is `ts` (a group before a part of it), by reference.
        internal static Ent PlacedOf(tosaveitemscript ts, bool rebuild = true)
        {
            if (ReferenceEquals(ts, null)) return null;
            if (LoopPlacedOnly)
            {
                if (rebuild) Placed();
                if (_byItem.TryGetValue(ts, out var e) && ReferenceEquals(e.Root, ts) && ByNet.TryGetValue(e.NetId, out var now) && now == e) return e;
                if (!_placedDirty && ByNet.Count == _netsSeen) return null;
            }
            Ent best = null;
            foreach (var e in ByNet.Values) if (ReferenceEquals(e.Root, ts) && (best == null || e.PartIndex < 0 && best.PartIndex >= 0)) best = e;
            return best;
        }

        /// The shared object whose item's own transform is `t` (a car's root, not something stored in it).
        internal static Ent PlacedOfTransform(Transform t)
        {
            if (ReferenceEquals(t, null)) return null;
            if (LoopPlacedOnly)
            {
                Placed();
                if (_byOwn.TryGetValue(t, out var e) && !ReferenceEquals(e.Root, null) && e.Root != null && ReferenceEquals(e.Root.transform, t) && ByNet.TryGetValue(e.NetId, out var now) && now == e) return e;
                if (!_placedDirty && ByNet.Count == _netsSeen) return null;
            }
            Ent best = null;
            foreach (var e in ByNet.Values)
                if (!ReferenceEquals(e.Root, null) && e.Root != null && ReferenceEquals(e.Root.transform, t) && (best == null || e.PartIndex < 0 && best.PartIndex >= 0)) best = e;
            return best;
        }

        /// The game's streaming placed items (once a second): bind them this frame, not up to FullEveryFrames later. As
        /// many go out as come in while driving, so the counts didn't change and new objects waited for the fallback
        /// pass: no states from their owner, a copy not yet a copy.
        [HarmonyLib.HarmonyPatch(typeof(itemPlaceRemoveScript), nameof(itemPlaceRemoveScript.PlaceStuff))]
        private static class PlacedAfterStreaming
        {
            [HarmonyLib.HarmonyPostfix] private static void Postfix() => _placedDirty = true;
        }

        /// Every shared object (the loops with LoopPlacedOnly off).
        private static List<Ent> AllEnts() { _all.Clear(); _all.AddRange(ByNet.Values); return _all; }

        /// A stored-away object's far record: a copy riding a container follows it there, and a pose waiting goes out.
        private static void DormantTick(Ent e)
        {
            if (e.Proxy && e.PoseLocked && e.ReceivedStateEpoch == e.Epoch && LockedWorld(e, out var lockedPos, out var lockedRot)) UpdateFarRecord(e, lockedPos, lockedRot);
            if (e.FarDirty && Time.realtimeSinceStartup >= e.FarFlushAt) { e.FarFlushAt = Time.realtimeSinceStartup + 1f; FlushFarRecords(e); }
        }
    }
}
