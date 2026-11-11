using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace TLDRevamp
{
    /// One line of an item's health (ItemHealth): ok, a warning or a problem, what is wrong, why, and what happened.
    internal sealed class HealthCheck
    {
        internal int Level;      // 0 ok, 1 warning, 2 problem
        internal string Title, Why, How;
        internal string Key;     // the background watch's fixed name for a problem (HealthWatchRules), or null
    }
}

namespace TLDRevamp.Net
{
    public static partial class Entities
    {
        internal static Ent EntityOf(uint net) => ByNet.TryGetValue(net, out var e) ? e : null;

        /// The checks of ItemHealth for object `net`: this machine's own state (`local`, from ObjectPanels.Capture, with
        /// its last 35 s of events) and every other player's view of it (`partners`, the live comparison through the
        /// host). Each problem says why, and lists the recent events that led there.
        internal static void HealthChecks(uint net, ObjectPanelState local, List<(int Player, ObjectPanelState State, ObjectPanelState Mine, double Age)> partners,
                                          bool comparing, List<HealthCheck> into)
        {
            into.Clear();
            var ic = CultureInfo.InvariantCulture;
            var e = EntityOf(net);
            void Add(int level, string title, string why, string how = null, string key = null) => into.Add(new HealthCheck { Level = level, Title = title, Why = why, How = how, Key = key });

            // 1. loaded here at all
            if (e == null || e.Root == null || !e.Root.gameObject.activeInHierarchy)
            {
                Add(2, "Not loaded here", e == null ? "This machine has no object " + net + " (never shared to it, or removed)." : "Known, but not in the world here (streamed out: far from you, or its group isn't loaded).",
                    HowOf(local, "add-", "remove-", "owner-"));
                return;
            }
            var pl = mainscript.s != null ? mainscript.s.player : null;

            // 2. owner: this machine against every other player's view
            string me = WorldOverlayLogic.Owner(e.OwnerId, MyId) + (e.Proxy ? ", shown here as a copy" : ", simulated here") + " · epoch " + e.Epoch;
            var ownerDiff = new List<string>();
            foreach (var p in partners)
                if (p.State.Has(ObjectPanelState.Bits.Found) && (p.State.Owner != e.OwnerId || p.State.Epoch != e.Epoch))
                    ownerDiff.Add("player " + p.Player + " has owner " + WorldOverlayLogic.Owner(p.State.Owner, MyId) + " epoch " + p.State.Epoch);
            if (ownerDiff.Count > 0)
                Add(2, "Owner differs", "Here: " + me + ". " + string.Join("; ", ownerDiff) + ". The machines disagree on who simulates it, so each moves it differently.",
                    HowOf(local, "owner-", "handover-", "claim-", "lease-", "crash-"));
            else Add(0, "Owner", me + (partners.Count > 0 ? " (same for every player)" : ""));

            // 3. position: a copy against its owner's last pose here, and every other player's view
            CheckPosition(e, local, Add);
            foreach (var p in partners)
            {
                if (!p.State.Has(ObjectPanelState.Bits.Found)) { Add(2, "Missing for player " + p.Player, "Player " + p.Player + " has no object " + net + " loaded: it was never sent to them, or removed there.", HowOf(p.State, "add-", "remove-")); continue; }
                if (p.Mine == null || !p.Mine.Has(ObjectPanelState.Bits.Found)) continue;
                double d = p.State.Distance(p.Mine), a = p.State.Angle(p.Mine);
                int sev = ObjectPanelState.Severity(d, a);
                if (sev > 0) Add(sev == 2 ? 2 : 1, "Player " + p.Player + " sees it elsewhere", "Player " + p.Player + " has it " + N(d) + " m and " + N(a) + "° from here (" + N(p.Age) + " s ago)." +
                                 (e.Proxy ? " Yours is the copy: compare with the owner's view." : " Yours is the owner's: their copy is behind or stuck."),
                                 HowOf(p.State, "proxy-", "owner-", "speed-", "crash-") ?? HowOf(local, "proxy-", "owner-", "crash-"));
            }

            // 4. held, stored, attached, locked: against every other player's view
            var mask = ~(ObjectPanelState.Bits.Proxy | ObjectPanelState.Bits.EventsNotShared | ObjectPanelState.Bits.Live);
            foreach (var p in partners)
            {
                if (p.Mine == null || !p.State.Has(ObjectPanelState.Bits.Found) || !p.Mine.Has(ObjectPanelState.Bits.Found)) continue;
                var diffs = new List<string>();
                var a = p.Mine.Flags & mask; var b = p.State.Flags & mask;
                foreach (ObjectPanelState.Bits bit in new[] { ObjectPanelState.Bits.Held, ObjectPanelState.Bits.Stored, ObjectPanelState.Bits.Attached, ObjectPanelState.Bits.Locked })
                    if ((a & bit) != (b & bit)) diffs.Add(bit.ToString().ToLowerInvariant() + ": " + ((a & bit) != 0 ? "yes" : "no") + " here, " + ((b & bit) != 0 ? "yes" : "no") + " for player " + p.Player);
                if (p.Mine.Parent != p.State.Parent || p.Mine.Part != p.State.Part) diffs.Add("attached to " + p.Mine.Parent + "/" + p.Mine.Part + " here, " + p.State.Parent + "/" + p.State.Part + " for player " + p.Player);
                if (p.Mine.LockedTo != p.State.LockedTo) diffs.Add("locked to " + p.Mine.LockedTo + " here, " + p.State.LockedTo + " for player " + p.Player);
                int slotDiff = 0;
                for (int i = 0; i < Math.Min(p.State.SlotCount, p.Mine.SlotCount); i++)
                {
                    var s = p.State.Slots[i]; var m = p.Mine.Slots[i];
                    if (s.Content != m.Content || s.Attached != m.Attached) { slotDiff++; if (slotDiff <= 3) diffs.Add("slot " + s.Item + "/" + s.Index + ": " + (m.Attached ? "net " + m.Content : "empty") + " here, " + (s.Attached ? "net " + s.Content : "empty") + " for player " + p.Player); }
                }
                if (p.State.SlotCount != p.Mine.SlotCount) diffs.Add(p.Mine.SlotCount + " slots here, " + p.State.SlotCount + " for player " + p.Player);
                if (slotDiff > 3) diffs.Add((slotDiff - 3) + " more slots differ");
                if (diffs.Count > 0)
                    Add(2, "State differs from player " + p.Player, string.Join("; ", diffs) + ". A message about it was lost, refused, or applied on one machine only.",
                        HowOf(local, "attach-", "detach-", "part-", "physlock-", "joint-", "remove-") ?? HowOf(p.State, "attach-", "detach-", "part-", "physlock-"));
            }

            // 5. its body: what the mod keeps on it here; and what can never be right (sanity)
            CheckBody(e, local, Add);
            CheckPickup(e, local, Add);
            CheckSanity(e, Add);

            // 6. its fitted parts: shown but unreachable (PartWatch)
            var broken = new List<string>(); int fitted = 0;
            if (pl != null && e.Items != null)
                foreach (var it in e.Items)
                    if (it != null && it.partslotscripts != null)
                        foreach (var ps in it.partslotscripts)
                        {
                            if (ps == null || !ps.hasPart()) continue;
                            var part = ps.part(); if (part == null || part.tosave == null || part.tosave.P == null) continue;
                            fitted++;
                            var r = Reach(part, pl);
                            if (PartWatchRules.Broken(r.Visible, r.Targetable, r.Expected, r.Held)) broken.Add(PrefabName(part.tosave.id) + " (" + PartWatchRules.Why(r.Live, r.Held) + ", colliders " + r.Live + "/" + r.Cols + ")");
                        }
            if (e.Root.attachable != null && e.Root.attachable.attached && e.Root.attachable.slot != null && pl != null)
            {
                var r = Reach(e.Root.attachable, pl);
                if (PartWatchRules.Broken(r.Visible, r.Targetable, r.Expected, r.Held)) broken.Add("this part itself (" + PartWatchRules.Why(r.Live, r.Held) + ")");
            }
            if (broken.Count > 0) Add(2, "Parts you can't reach", string.Join("; ", broken) + ". You see them but get no prompt: the crosshair's use ray can't hit them.", HowOf(local, "part-", "attach-", "detach-", "owner-", "handover-"));
            else if (fitted > 0) Add(0, "Fitted parts", fitted + " fitted, all reachable");

            // 7. what was refused or corrected lately
            var trouble = new List<string>();
            for (int i = 0; i < local.EventCount; i++)
            {
                var ev = local.Events[i]; string k = ObjectPanelEvents.Kinds[ev.Kind];
                if (k.EndsWith("-refused", StringComparison.Ordinal) || k == "proxy-correction" || k == "proxy-snap" || k == "speed-jump" || k == "speed-anomaly" || k == "census-repair-failed"
                    || k == "health-problem" || k == "item-untargetable" || k == "part-untargetable")
                    trouble.Add(EventText(ev, 0));
            }
            if (trouble.Count > 0) Add(1, "Refused or corrected lately", string.Join("; ", trouble.GetRange(0, Math.Min(4, trouble.Count))) + (trouble.Count > 4 ? "; " + (trouble.Count - 4) + " more" : ""));

            // 8. whether the other players' views are in
            if (!comparing) Add(1, "Other players' views", "Not compared (not in a session, or the comparison is off).");
            else if (partners.Count == 0) Add(1, "Other players' views", "Waiting for their replies through the host.");
        }

        internal delegate void CheckAdd(int level, string title, string why, string how = null, string key = null);

        /// A copy against its owner's last pose here. `local` (its recent events) may be null: the background watch.
        private static void CheckPosition(Ent e, ObjectPanelState local, CheckAdd Add)
        {
            // a part fitted to a car (or bolted to its parent) sends no poses of its own: its last one is from before it was
            // fitted, minutes old and anywhere (a bus tank "3956 m off", a glovebox "884 m off" while drawn right in the car)
            if (HealthSkipFitted && e.Proxy && (OverlayBolted(e) || AttachedCarPart(e)))
            {
                Add(0, "Position", "Rides its car or parent: no poses of its own to compare.");
                return;
            }
            if (e.Proxy && OverlaySample(e, out var pose, out var rot))
            {
                var frame = OverlayFrame(e);
                double err = frame != null ? (mainscript.GlobalFromUnityPos(frame.position) - pose).magnitude : double.NaN;
                double age = e.TelemetryReceived > 0 ? Time.realtimeSinceStartupAsDouble - e.TelemetryReceived : double.NaN;
                bool moving = OverlayMoving(e), stale = WorldOverlayLogic.Stale(age, moving);
                // a copy is drawn its interpolation delay behind the last pose: at speed that is metres (the host's car at
                // 49 m/s, 3.39 m off a pose 0.01 s old, was marked "left behind")
                double speed = e.LastVel.magnitude, tol = 2 + speed * ((e.Ip != null && e.Ip.DelayMs > 0 ? e.Ip.DelayMs / 1000.0 : 0.1) + (double.IsNaN(age) ? 0 : Math.Min(age, 1)));
                if (stale && !(err >= tol)) stale = false;   // quiet but where it should be: an event's worth, not a problem
                int level = stale || err >= tol ? 2 : err >= .2 ? 1 : 0;
                string why = "Shown " + N(err) + " m from its owner's last pose here, which is " + N(age) + " s old" + (moving ? " (moving)" : "") + ".";
                if (stale) why += " No update for over 1.5 s while it moves: its owner stopped sending, or this machine stopped hearing it.";
                else if (err >= tol) why += " Left behind its owner's poses: held back by something here (a collision, a hold, a lost interpolation).";
                Add(level, level == 0 ? "Position" : "Position off", why, level > 0 ? HowOf(local, "proxy-", "speed-", "owner-", "crash-", "tpa-") : null, level == 2 ? HealthWatchRules.Position : null);
            }
        }

        /// What the mod keeps on the object here: colliders, layers, the body.
        private static void CheckBody(Ent e, ObjectPanelState local, CheckAdd Add)
        {
            var pl = mainscript.s != null ? mainscript.s.player : null;
            var rb = e.Root.GetComponent<Rigidbody>();
            _pwCols.Clear(); e.Root.GetComponentsInChildren(true, _pwCols);
            int all = _pwCols.Count, on = 0, heldLayer = 0;
            foreach (var c in _pwCols)
            {
                if (c == null) continue;
                if (c.enabled && c.gameObject.activeInHierarchy) on++;
                // on the held layer because what it is on is being carried (a tyre on a wheel in someone's hands): the game's
                // pickupable.Pickup puts the parts in its slots there too
                if (c.gameObject.layer == mainscript.pickedUpLayer && !HeldAbove(c.transform, pl)) heldLayer++;
            }
            bool inHand = InLocalHands(pl, e);
            // a ragdoll (a corpse, a dead rabbit): the game unparents its bones' colliders, the root keeps none switched on
            bool ragdoll = Ragdoll(e.Root);
            // a fitted part whose slot switches its colliders off on purpose (partslotscript.disableCollidersOnCraft)
            bool offByDesign = e.Root.attachable != null && e.Root.attachable.attached && !CollidersExpected(e.Root.attachable);
            bool worn = HealthSkipWorn && (e.WornBy >= 0 || e.SentWornSlot >= 0 || LocalWornSlot(e.Root) >= 0);   // the game takes a worn item's body away
            bool keptFrozen = HealthSkipFrozen && (e.Root.buried || Fixes.UnfreezeOnGround.Holding(e.Root));       // kinematic on purpose until it has ground under it
            string body = rb == null ? "no rigidbody" : (rb.isKinematic ? "kinematic" : "dynamic") + (rb.detectCollisions ? "" : ", collisions off");
            string flags = (e.Held ? " held" : "") + (e.Stored ? " stored" : "") + (worn ? " worn" : "") + (keptFrozen ? " kept-frozen" : "") + (e.ReleaseGhost ? " release-ghost" : "") + (e.CargoGhost ? " cargo-ghost" : "") + (e.Physical ? " physical-copy" : "") + (e.PartsColOff ? " part-colliders-off" : "");
            string bodyText = "Colliders " + on + "/" + all + " on · " + body + (flags.Length > 0 ? " · mod flags:" + flags : "");
            var bodyProblems = new List<string>();
            if (all > 0 && on == 0 && !e.Stored && !worn && !ragdoll && !offByDesign) bodyProblems.Add("every collider is off, so nothing can touch or target it");
            if (heldLayer > 0 && !inHand && !e.Held) bodyProblems.Add(heldLayer + " colliders on the held-item layer while nobody holds it: the crosshair passes through them");
            if (rb != null && !rb.detectCollisions && !e.Held && !e.Stored && !e.ReleaseGhost && !e.CargoGhost && !inHand && !worn) bodyProblems.Add("collisions are off without a reason the mod knows (not held, stored or a ghost)");
            bool heldFrozen = Fixes.LoadFreeze.Holding || Tpa.Holding(rb) || (e.Root.P != null && e.Root.P.inRightHand);   // frozen on purpose for a moment
            if (rb != null && !e.Proxy && rb.isKinematic && !e.Stored && e.Root.attachable != null && !e.Root.attachable.attached && !inHand && !worn && !keptFrozen && !heldFrozen && e.Root.car == null) bodyProblems.Add("ours but kinematic and loose: it won't fall or be pushed");
            if (rb != null && e.Proxy && !rb.isKinematic && !e.Physical && !e.CrashTaking) bodyProblems.Add("a copy with a dynamic body outside a crash: it will drift from its owner's poses");
            if (bodyProblems.Count > 0) Add(2, "Body", bodyText + ". " + string.Join("; ", bodyProblems) + ".", HowOf(local, "owner-", "physlock-", "attach-", "detach-", "part-", "handover-"), HealthWatchRules.Body);
            else Add(0, "Body", bodyText);

        }

        /// The nearest pickupable above this collider (itself included) is being carried: here, by another player, or by us.
        internal static bool HeldAbove(Transform t, fpscontroller pl)
        {
            var p = t.GetComponentInParent<pickupable>();
            for (int i = 0; p != null && i < 8; i++)
            {
                if (p.pickedUp || (pl != null && (p == pl.pickedUp || p == pl.inHandP))) return true;
                var pe = p.tosave != null ? EntOfRoot(p.tosave.transform.root) : null;
                if (pe != null && pe.Held) return true;
                // what it is fitted to, up the chain (a tyre on a rim on a wheel)
                var at = p.tosave != null ? p.tosave.attachable : null;
                var holder = at != null && at.attached && at.slot != null && at.slot.tosaveitem != null ? at.slot.tosaveitem.P : null;
                if (holder == null || holder == p) return false;
                p = holder;
            }
            return false;
        }

        /// A ragdoll's root (a player's corpse, a rabbit gone limp): its colliders are on bones the game unparents.
        internal static bool Ragdoll(tosaveitemscript t)
        {
            if (t == null) return false;
            if (t.ragdollPos != null) return true;
            var r = t.GetComponentInChildren<ragdollactivatescript>(true);
            return r != null && r.ragdoll;
        }

        /// A loose item: can the game's pickup ray (fpscontroller.pickupLayer) hit it? Colliders all off or on the held layer
        /// are the body check's; here, colliders on but none on a layer the ray hits ("I could see it just fine").
        private static void CheckPickup(Ent e, ObjectPanelState local, CheckAdd Add)
        {
            var pl = mainscript.s != null ? mainscript.s.player : null;
            if (pl == null || !Loose(e, pl)) return;
            _pwCols.Clear(); e.Root.GetComponentsInChildren(true, _pwCols);
            int live = 0, held = 0, pickup = 0, mask = pl.pickupLayer.value;
            var layers = new List<string>();
            foreach (var c in _pwCols)
            {
                if (c == null || !c.enabled || !c.gameObject.activeInHierarchy) continue;
                live++;
                if (c.gameObject.layer == mainscript.pickedUpLayer) held++;
                if ((mask & (1 << c.gameObject.layer)) != 0) pickup++;
                string ln = LayerMask.LayerToName(c.gameObject.layer); if (!layers.Contains(ln)) layers.Add(ln);
            }
            if (live == 0 || held > 0) return;
            bool shows = false;
            foreach (var rd in e.Root.GetComponentsInChildren<Renderer>()) if (rd.enabled && rd.gameObject.activeInHierarchy) { shows = true; break; }
            if (pickup == 0 && shows)
                Add(2, "Can't pick it up", live + " colliders on, none on a layer the game's pickup ray hits (they are on " + string.Join(", ", layers) +
                    "): it shows, but the crosshair passes through it and there is no prompt.", HowOf(local, "owner-", "remove-", "add-", "detach-", "handover-"), HealthWatchRules.ItemNotPickup);
            else Add(0, "Pick up", pickup + " of " + live + " colliders on the pickup layer");
        }

        /// The recent events (35 s) of the kinds given, newest first, as a short timeline — how the state came about.
        private static string HowOf(ObjectPanelState s, params string[] prefixes)
        {
            if (s == null || s.EventCount == 0) return null;
            var lines = new List<string>();
            for (int i = 0; i < s.EventCount && lines.Count < 4; i++)
            {
                string k = ObjectPanelEvents.Kinds[s.Events[i].Kind];
                foreach (var p in prefixes) if (k.StartsWith(p, StringComparison.Ordinal)) { lines.Add(EventText(s.Events[i], 0)); break; }
            }
            return lines.Count == 0 ? null : string.Join("\n", lines);
        }
        internal static string EventText(ObjectPanelState.Recent ev, double age)
        {
            string k = ObjectPanelEvents.Kinds[ev.Kind], reason = ObjectPanelEvents.Reasons[ev.Reason];
            string who = ev.From >= 0 || ev.To >= 0 ? " (" + (ev.From >= 0 ? WorldOverlayLogic.Owner(ev.From, MyId) : "?") + (ev.To >= 0 ? " → " + WorldOverlayLogic.Owner(ev.To, MyId) : "") + ")" : "";
            return "-" + ObjectPanelState.N(ev.Age + age) + " s " + k + who + (ev.Epoch != 0 ? " epoch " + ev.Epoch : "") + (reason != "ok" && reason != "reason unavailable" ? ": " + reason : "");
        }
        private static string N(double n) => double.IsNaN(n) || double.IsInfinity(n) ? "?" : n.ToString(n < 10 ? "F2" : "F0", CultureInfo.InvariantCulture);
    }
}
