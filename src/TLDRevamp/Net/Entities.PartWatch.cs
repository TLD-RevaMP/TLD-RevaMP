using System.Collections.Generic;
using UnityEngine;

namespace TLDRevamp.Net
{
    /// Fitted parts near the player that show but can't be reached (PartWatchRules): found by the machine itself, not by
    /// a player who happens to report it. Two checks: every NearbyEveryS the fitted parts within NearbyM, and every
    /// LookEveryS the part under the crosshair (its meshes on the camera's ray within reach) — there also a hidden object
    /// in front of a reachable part. A find is an event (part-untargetable) and, at most once per AutoMarkEveryS, an
    /// automatic mark (AutoMark): the state of this machine and, through the host, of the other players' — the side a report from
    /// one machine never has (the host's copy at 10:49). Each part is reported once.
    public static partial class Entities
    {
        public static bool PartWatch = true;   // A/B: false = no automatic checks of fitted parts
        public static bool PartWatchMarks = true;   // false: the event only, no automatic mark
        public static float PartWatchNearbyEveryS = 2f, PartWatchLookEveryS = 0.2f, PartWatchNearbyM = 12f;   // marks: AutoMarkEveryS
        public static long PartWatchFlags, PartWatchMarksSaved;
        public static string PartWatchLast = "";

        private sealed class WatchedPart
        {
            public bool Done; public float LostAt = -1f, LookAt = -1f;
            public readonly List<Renderer> Renderers = new List<Renderer>();
        }
        private static readonly Dictionary<attachablescript, WatchedPart> _watched = new Dictionary<attachablescript, WatchedPart>();
        private static readonly List<attachablescript> _nearParts = new List<attachablescript>();
        private static readonly List<Collider> _pwCols = new List<Collider>();
        private static float _pwScanAt = -99f, _pwLookAt = -99f;

        private static void PartWatchTick()
        {
            if (!PartWatch) return;
            var pl = mainscript.s != null ? mainscript.s.player : null;
            if (pl == null || pl.Cam == null) return;
            float now = Time.realtimeSinceStartup;
            if (now - _pwScanAt >= PartWatchNearbyEveryS) { _pwScanAt = now; PartWatchScan(pl, now); ItemWatchScan(pl, now); }
            if (now - _pwLookAt >= PartWatchLookEveryS) { _pwLookAt = now; PartWatchLook(pl, now); ItemWatchLook(pl, now); }
        }

        private struct PartReach { public bool Visible, Targetable, Expected; public int Cols, Live, UseLayer, Held; }

        /// The game's own rule (partslotscript.Craft): crafted into a slot that switches colliders off, or onto an item that
        /// is (its holder's slot), the part's colliders are off on purpose — and so are those of what is fitted onto it.
        private static bool CollidersExpected(attachablescript part)
        {
            var at = part;
            for (int depth = 0; at != null && at.slot != null && depth < 8; depth++)
            {
                if (at.slot.disableCollidersOnCraft) return false;
                var holder = at.slot.pick;
                at = holder != null && holder.tosave != null ? holder.tosave.attachable : null;
            }
            return true;
        }

        /// The part as the game's use ray finds it: its own colliders on useLayer, or its slot's (the dismount hits either).
        private static PartReach Reach(attachablescript part, fpscontroller pl)
        {
            var r = new PartReach();
            _pwCols.Clear(); part.GetComponentsInChildren(true, _pwCols);
            int mask = pl.useLayer.value;
            foreach (var c in _pwCols)
            {
                if (c == null) continue;
                r.Cols++;
                bool live = c.enabled && c.gameObject.activeInHierarchy;
                if (live) r.Live++;
                if (c.gameObject.layer == mainscript.pickedUpLayer) r.Held++;
                if (live && (mask & (1 << c.gameObject.layer)) != 0) r.UseLayer++;
            }
            var sc = part.slot != null ? part.slot.GetComponent<Collider>() : null;
            bool slotReach = sc != null && sc.enabled && sc.gameObject.activeInHierarchy && (mask & (1 << sc.gameObject.layer)) != 0;
            r.Targetable = r.UseLayer > 0 || slotReach;
            r.Expected = CollidersExpected(part);
            if (_watched.TryGetValue(part, out var w) && w.Renderers.Count > 0)
            { foreach (var rd in w.Renderers) if (rd != null && rd.enabled && rd.gameObject.activeInHierarchy) { r.Visible = true; break; } }
            else foreach (var rd in part.GetComponentsInChildren<Renderer>(true)) if (rd.enabled && rd.gameObject.activeInHierarchy) { r.Visible = true; break; }
            return r;
        }

        /// The fitted parts within PartWatchNearbyM of the player: in a slot of an item of a shared group near it.
        private static void NearParts(fpscontroller pl, List<attachablescript> into)
        {
            into.Clear();
            var at = pl.transform.position;
            float groupR = PartWatchNearbyM + 10f;
            foreach (var e in ByNet.Values)
            {
                if (e.Root == null || e.Items == null || (e.Root.transform.position - at).sqrMagnitude > groupR * groupR) continue;
                foreach (var it in e.Items)
                {
                    if (it == null || it.partslotscripts == null) continue;
                    foreach (var ps in it.partslotscripts)
                    {
                        if (ps == null || !ps.hasPart()) continue;
                        var part = ps.part();
                        if (part == null || part.tosave == null || part.tosave.P == null || into.Contains(part)) continue;
                        if ((part.transform.position - at).sqrMagnitude > PartWatchNearbyM * PartWatchNearbyM) continue;
                        into.Add(part);
                    }
                }
            }
        }

        private static void PartWatchScan(fpscontroller pl, float now)
        {
            NearParts(pl, _nearParts);
            // gone parts (destroyed objects compare equal to null)
            List<attachablescript> dead = null;
            foreach (var k in _watched.Keys) if (k == null) (dead ?? (dead = new List<attachablescript>())).Add(k);
            if (dead != null) foreach (var k in dead) _watched.Remove(k);
            foreach (var part in _nearParts)
            {
                if (!_watched.TryGetValue(part, out var w)) _watched[part] = w = new WatchedPart();
                w.Renderers.Clear(); part.GetComponentsInChildren(true, w.Renderers);
                if (pl.pickedUp == part.tosave.P || HeldAbove(part.transform, pl)) { w.LostAt = -1f; continue; }   // carried, or on something carried
                var r = Reach(part, pl);
                if (!PartWatchRules.Broken(r.Visible, r.Targetable, r.Expected, r.Held, r.Live)) { w.LostAt = -1f; continue; }
                if (w.LostAt < 0f) w.LostAt = now;
                else if (now - w.LostAt >= PartWatchRules.NearbyAfterS) PartWatchFound(part, w, r, PartWatchRules.Why(r.Live, r.Held), "nearby", null, now);
            }
        }

        private static void PartWatchLook(fpscontroller pl, float now)
        {
            if (_nearParts.Count == 0) return;
            var cam = pl.Cam.transform; var ray = new Ray(cam.position, cam.forward);
            float reach = pl.FrayRange; attachablescript best = null; float bestD = float.MaxValue;
            foreach (var part in _nearParts)
            {
                if (part == null || !_watched.TryGetValue(part, out var w)) continue;
                foreach (var rd in w.Renderers)
                    if (rd != null && rd.enabled && rd.gameObject.activeInHierarchy && rd.bounds.IntersectRay(ray, out float d) && d <= reach && d < bestD) { bestD = d; best = part; }
            }
            foreach (var kv in _watched) if (kv.Key != best) kv.Value.LookAt = -1f;
            if (best == null || pl.pickedUp == best.tosave.P || HeldAbove(best.transform, pl)) return;
            var bw = _watched[best];
            var r = Reach(best, pl);
            string why = null, blocker = null;
            if (!r.Targetable) { if (PartWatchRules.Broken(r.Visible, false, r.Expected, r.Held, r.Live)) why = PartWatchRules.Why(r.Live, r.Held); }
            else if (Physics.Raycast(ray, out var hit, reach, pl.useLayer) && hit.distance < bestD - 0.05f && !OfPart(hit.collider, best) && !Shows(hit.collider.transform.root))
            { why = PartWatchRules.Blocked; blocker = hit.collider.transform.root.name; }
            if (why == null) { bw.LookAt = -1f; return; }
            if (bw.LookAt < 0f) bw.LookAt = now;
            else if (now - bw.LookAt >= PartWatchRules.LookAfterS) PartWatchFound(best, bw, r, why, "crosshair", blocker, now);
        }

        private static bool OfPart(Collider c, attachablescript part) =>
            c.transform.IsChildOf(part.transform) || (part.slot != null && c.transform == part.slot.transform);

        private static bool Shows(Transform root)
        {
            foreach (var rd in root.GetComponentsInChildren<Renderer>()) if (rd.enabled && rd.gameObject.activeInHierarchy) return true;
            return false;
        }

        private static void PartWatchFound(attachablescript part, WatchedPart w, PartReach r, string why, string how, string blocker, float now)
        {
            if (w.Done) return;
            w.Done = true; PartWatchFlags++;
            var ts = part.tosave;
            var own = OwnEntity(ts); var group = EditEntityOf(ts);
            uint partNet = own != null ? own.NetId : 0, groupNet = group != null ? group.NetId : 0;
            var so = part.slot != null ? part.slot.tosaveitem : null;
            int slot = so != null && so.partslotscripts != null ? so.partslotscripts.IndexOf(part.slot) : -1;
            string kind = PrefabName(ts.id), on = so != null ? PrefabName(so.id) : "?";
            PartWatchLast = kind + " net " + (partNet != 0 ? partNet : groupNet) + " on " + on + " #" + slot + ": " + why + " — (" + how + ", colliders " + r.Live + "/" + r.Cols + " on, " + r.UseLayer + " on the use layer, "
                            + r.Held + " on the held layer" + (blocker != null ? ", in front: " + blocker : "") + ", " + (own != null ? (own.Proxy ? "a copy" : "ours") : group != null ? (group.Proxy ? "a copy's part" : "our part") : "not shared") + ")";
            SessionLog.Event("part-untargetable", partNet != 0 ? partNet : groupNet, other: groupNet, slot: slot, count: r.Cols, value: r.Live, dx: r.UseLayer, dy: r.Held, result: why);
            Plugin.Log.LogWarning("part watch: " + PartWatchLast);
            if (PartWatchMarks && AutoMark("auto: " + PartWatchLast, partNet != 0 ? partNet : groupNet)) PartWatchMarksSaved++;
        }

        /// Debugging V2's tab line for an object: its colliders switched on (of all), its body (kinematic, a physical copy,
        /// dynamic; collisions off; part colliders off for distance) and its fitted parts the player can't reach
        /// (PartWatchRules) — the 10:57 hood would have read "1 part unreachable: Car08Hood". Worked out within
        /// OverlayCollidersM, at most once a second per object (a car has 200+ colliders and the tabs refresh 4 times a second).
        internal static float OverlayCollidersM = 30f;
        internal static int SharedCount => ByNet.Count;   // Debugging V2's "shared objects here" field
        private static readonly Dictionary<uint, (float at, string text)> _colText = new Dictionary<uint, (float, string)>();
        internal static string OverlayColliders(Ent e, bool aimed = false)
        {
            var pl = mainscript.s != null ? mainscript.s.player : null;
            if (e.Root == null || pl == null) return "";
            if (!aimed && (e.Root.transform.position - pl.transform.position).sqrMagnitude > OverlayCollidersM * OverlayCollidersM)
                return "colliders: come within " + OverlayCollidersM.ToString("F0") + " m (or aim at it)";
            float now = Time.realtimeSinceStartup;
            if (_colText.TryGetValue(e.NetId, out var cached) && now - cached.at < 1f) return cached.text;
            _pwCols.Clear(); e.Root.GetComponentsInChildren(true, _pwCols);
            int all = _pwCols.Count, on = 0;
            foreach (var c in _pwCols) if (c != null && c.enabled && c.gameObject.activeInHierarchy) on++;
            var rb = e.Root.GetComponent<Rigidbody>();
            string body = rb == null ? "no body" : e.Physical ? "physical copy" : rb.isKinematic ? "kinematic" : "dynamic";
            if (rb != null && !rb.detectCollisions) body += ", no collisions";
            if (e.PartsColOff) body += ", part colliders off (far)";
            int broken = 0; string first = null;
            void Check(attachablescript part)
            {
                if (part == null || part.tosave == null || part.tosave.P == null || pl.pickedUp == part.tosave.P) return;
                var r = Reach(part, pl);
                if (!PartWatchRules.Broken(r.Visible, r.Targetable, r.Expected, r.Held)) return;
                broken++; if (first == null) first = PrefabName(part.tosave.id);
            }
            if (e.Root.attachable != null && e.Root.attachable.attached && e.Root.attachable.slot != null) Check(e.Root.attachable);
            if (e.Items != null)
                foreach (var it in e.Items)
                    if (it != null && it.partslotscripts != null)
                        foreach (var ps in it.partslotscripts) if (ps != null && ps.hasPart()) Check(ps.part());
            string text = "colliders " + on + "/" + all + " on · " + body + (broken > 0 ? " · " + broken + (broken == 1 ? " part" : " parts") + " unreachable: " + first : "");
            if (_colText.Count > 512) _colText.Clear();
            _colText[e.NetId] = (now, text);
            return text;
        }

        /// The bug report's parts.json: every fitted part within PartWatchNearbyM as the player can reach it — shown,
        /// reachable by the use ray, whether the game keeps its colliders on, the dismount conditions, its colliders and
        /// their layers (Entities.MountFacts) — and what the crosshair's rays hit. A part whose colliders were off was in no
        /// file before: lock.json starts from what the crosshair hits, and that part is what it can't hit (10:57's hood).
        internal static string PartsDiagnostics()
        {
            var pl = mainscript.s != null ? mainscript.s.player : null;
            if (pl == null || pl.Cam == null) return null;
            var parts = new List<attachablescript>(); NearParts(pl, parts);
            var b = new System.Text.StringBuilder("{\"radiusM\":").Append(Json.Num(PartWatchNearbyM)).Append(",\"reachM\":").Append(Json.Num(pl.FrayRange))
                .Append(",\"watch\":{\"flags\":").Append(PartWatchFlags).Append(",\"last\":").Append(Json.Str(PartWatchLast)).Append("},\"crosshair\":{");
            var cam = pl.Cam.transform; var ray = new Ray(cam.position, cam.forward);
            bool firstMask = true;
            foreach (var (label, mask) in new[] { ("use", pl.useLayer.value), ("pickup", pl.pickupLayer.value), ("attach", pl.attachLayer.value) })
            {
                var hits = Physics.RaycastAll(ray, pl.FrayRange + 2f, mask);
                System.Array.Sort(hits, (x, y) => x.distance.CompareTo(y.distance));
                if (!firstMask) b.Append(',');
                firstMask = false;
                b.Append(Json.Str(label)).Append(":[");
                for (int i = 0; i < hits.Length && i < 4; i++)
                {
                    var c = hits[i].collider; var root = c.transform.root;
                    if (i > 0) b.Append(',');
                    b.Append("{\"name\":").Append(Json.Str(c.name)).Append(",\"layer\":").Append(Json.Str(LayerMask.LayerToName(c.gameObject.layer)))
                     .Append(",\"root\":").Append(Json.Str(root.name)).Append(",\"net\":").Append(NetOfRoot(root)).Append(",\"m\":").Append(Json.Num(hits[i].distance))
                     .Append(",\"shows\":").Append(Shows(root) ? "true" : "false").Append(",\"trigger\":").Append(c.isTrigger ? "true" : "false").Append('}');
                }
                b.Append(']');
            }
            b.Append("},\"parts\":[");
            for (int i = 0; i < parts.Count; i++)
            {
                var part = parts[i]; var ps = part.slot; var P = part.tosave.P;
                var r = Reach(part, pl);
                var so = ps != null ? ps.tosaveitem : null;
                var group = EditEntityOf(part.tosave);
                if (i > 0) b.Append(',');
                b.Append("{\"holder\":").Append(Json.Str(so != null ? PrefabName(so.id) : "?")).Append(",\"entity\":").Append(group != null ? group.NetId : 0)
                 .Append(",\"copy\":").Append(group != null && group.Proxy ? "true" : "false")
                 .Append(",\"slotIndex\":").Append(so != null && so.partslotscripts != null ? so.partslotscripts.IndexOf(ps) : -1)
                 .Append(",\"slot\":").Append(Json.Str(ps != null ? ps.name : "")).Append(",\"m\":").Append(Json.Num((part.transform.position - pl.transform.position).magnitude))
                 .Append(",\"visible\":").Append(r.Visible ? "true" : "false").Append(",\"reachable\":").Append(r.Targetable ? "true" : "false")
                 .Append(",\"collidersExpected\":").Append(r.Expected ? "true" : "false").Append(",\"broken\":").Append(PartWatchRules.Broken(r.Visible, r.Targetable, r.Expected, r.Held) ? "true" : "false");
                if (ps != null)
                {
                    var sc = ps.GetComponent<Collider>();
                    b.Append(",\"slotCollider\":").Append(Json.Str(sc == null ? "none" : LayerMask.LayerToName(sc.gameObject.layer) + (sc.enabled && sc.gameObject.activeInHierarchy ? "" : ":off")))
                     .Append(",\"disableCollidersOnCraft\":").Append(ps.disableCollidersOnCraft ? "true" : "false");
                    b.Append(",\"slotState\":{\"slot\":").Append(Json.Str(ps.name)).Append(",\"cantCraft\":").Append(ps.cantCraft ? "true" : "false")
                     .Append(",\"cantUnCraft\":").Append(ps.cantUnCraft ? "true" : "false").Append(",\"attachTarget\":").Append(ps.attachTarget ? "true" : "false");
                    MountFacts(ps, part, P, pl, b, -1);
                    b.Append('}');
                }
                b.Append('}');
            }
            return b.Append("]}").ToString();
        }

        private static uint NetOfRoot(Transform root)
        {
            foreach (var e in ByNet.Values) if (e.Root != null && e.Root.transform == root) return e.NetId;
            return 0;
        }

        /// `mp partwatch`: the checks' state. `mp partwatch report`: the bug report's parts.json now. `mp partwatch break <car> <slot> off|held|fix`: the part in that slot of the car's
        /// root broken on purpose here (colliders off / on the held layer) or put right — to test the checks.
        public static string PartWatchCommand(string[] a)
        {
            if (a.Length >= 4 && a[1] == "break")
            {
                if (!ByNet.TryGetValue(uint.Parse(a[2]), out var car) || !Resolve(car) || car.Root == null) return "{\"error\":\"no such car here\"}";
                int s = int.Parse(a[3]);
                if (s < 0 || s >= car.Root.partslotscripts.Count || car.Root.partslotscripts[s] == null || !car.Root.partslotscripts[s].hasPart()) return "{\"error\":\"no part in slot " + s + "\"}";
                var part = car.Root.partslotscripts[s].part(); string mode = a.Length > 4 ? a[4] : "off";
                var slotCol = car.Root.partslotscripts[s].GetComponent<Collider>();
                foreach (var c in part.GetComponentsInChildren<Collider>(true))
                {
                    if (mode == "off") c.enabled = false;
                    else if (mode == "held") c.gameObject.layer = mainscript.pickedUpLayer;
                    else c.enabled = true;
                }
                if (slotCol != null && mode != "fix") slotCol.enabled = false; else if (slotCol != null) slotCol.enabled = true;
                if (mode == "fix") part.tosave.P.resetLayer();
                return "{\"broken\":" + Json.Str(part.name) + ",\"mode\":" + Json.Str(mode) + "}";
            }
            if (a.Length >= 2 && a[1] == "report") return PartsDiagnostics() ?? "{\"error\":\"no player\"}";
            var pl = mainscript.s != null ? mainscript.s.player : null;
            var rows = new List<string>();
            if (pl != null)
                foreach (var part in _nearParts)
                {
                    if (part == null || !_watched.TryGetValue(part, out var w)) continue;
                    var r = Reach(part, pl);
                    rows.Add("{\"part\":" + Json.Str(part.name) + ",\"visible\":" + (r.Visible ? "true" : "false") + ",\"targetable\":" + (r.Targetable ? "true" : "false")
                             + ",\"expected\":" + (r.Expected ? "true" : "false") + ",\"reported\":" + (w.Done ? "true" : "false") + "}");
                }
            return "{\"enabled\":" + (PartWatch ? "true" : "false") + ",\"flags\":" + PartWatchFlags + ",\"marks\":" + PartWatchMarksSaved + ",\"near\":" + _nearParts.Count
 + ",\"last\":" + Json.Str(PartWatchLast) + ",\"parts\":[" + string.Join(",", rows) + "]}";
        }
    }
}
