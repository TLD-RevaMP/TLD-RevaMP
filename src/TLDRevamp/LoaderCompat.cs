using System.Collections.Generic;
using UnityEngine;

namespace TLDRevamp
{
    /// What TLD Loader mods use of the public game's save and attach code that the beta changed or dropped. LoaderRetarget
    /// points the mod's own instructions here (by name: it stays free of the game's types); nothing in the game changes.
    ///
    /// The spawner many workshop mods share (issue 44: a pet mod's cat, a nitrous mod's bottle): pick the first
    /// free save id with `!savedatascript.d.toSaveStuff.ContainsKey(i) && !data.farStuff.ContainsKey(i) &&
    /// !data.nearStuff.ContainsKey(i)`, instantiate, `tosaveitemscript.FStart(i)`. On the beta those fields and the type
    /// save_prefab are gone: the whole method stopped at its first call, so nothing was made, and the spawn menu's holder
    /// (the pet mod's cat model with its collider switched off) stayed in the air where it was spawned.
    public static class LoaderCompat
    {
        public static long Calls;

        // ------------------------------------------------------------------ the old save ids

        private static Dictionary<int, tosaveitemscript> _toSave;
        private static int _toSaveFrame = -1;
        /// savedatascript.toSaveStuff: the placed items by id (the beta keys them by uint). One copy per frame: the spawner
        /// loop reads it once per id it tries.
        public static Dictionary<int, tosaveitemscript> ToSaveStuff(savedatascript s)
        {
            Calls++;
            if (_toSave != null && _toSaveFrame == Time.frameCount) return _toSave;
            _toSave = new Dictionary<int, tosaveitemscript>();
            var items = s != null ? s.items : null;
            if (items != null) foreach (var kv in items) _toSave[unchecked((int)kv.Key)] = kv.Value;
            _toSaveFrame = Time.frameCount;
            return _toSave;
        }

        /// save_prefab, the public game's record of a far item, as the mods use it (LoaderRetarget points the type here): many
        /// workshop mods keep their settings and progress in one, a string in a fake plate record,
        /// `farStuff.Add(Mathf.Abs(ID.GetHashCode()), new save_prefab(plate, ...) { rendszam = new save_rendszam { S = json } })`.
        public class SaveRecord
        {
            public int categoryID, id;
            public save_rendszam rendszam;
            public save_meterscript meter;   // one author's save helper (in a car mod) keeps its numbers in one
            public SaveRecord(int category, int id, double x, double y, double z, float rx, float ry, float rz) { categoryID = category; this.id = id; }
        }

        /// The beta's save has its plate strings by item id (itemData.rendszam); a mod's record is one of them under this
        /// base + its key. Item ids count up from 0, and the beta renumbers only the ids it knows (a construction's).
        public const uint RecordIdBase = 0x80000000u;
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<savedata, Dictionary<int, SaveRecord>> _records =
            new System.Runtime.CompilerServices.ConditionalWeakTable<savedata, Dictionary<int, SaveRecord>>();

        /// savedata.nearStuff / farStuff: the mods' own records of this save (both: a mod reads either, then adds to far).
        /// The spawner's ContainsKey on them comes to RecordIdUsed.
        public static Dictionary<int, SaveRecord> SavedRecords(savedata d)
        {
            Calls++;
            if (d == null) return new Dictionary<int, SaveRecord>();
            if (_records.TryGetValue(d, out var map)) return map;
            map = ReadRecords(d.itemData?.rendszam);
            _records.Add(d, map);
            return map;
        }

        public static Dictionary<int, SaveRecord> ReadRecords(List<save_rendszam> plates)
        {
            var map = new Dictionary<int, SaveRecord>();
            if (plates != null)
                foreach (var plate in plates)
                    if (plate != null && plate.id >= RecordIdBase)
                    {
                        var r = new SaveRecord(0, 0, 0, 0, 0, 0, 0, 0) { rendszam = plate };
                        int cut = plate.S != null ? plate.S.IndexOf(MeterMark) : -1;
                        if (cut >= 0)
                        {
                            r.meter = ReadMeter(plate.S.Substring(cut + 1));
                            r.rendszam = new save_rendszam { id = plate.id, S = plate.S.Substring(0, cut) };
                        }
                        map[(int)(plate.id - RecordIdBase)] = r;
                    }
            return map;
        }

        /// A record's meter goes into its plate string after this mark: its readings (mutatok), then its dials
        /// (szamlapok), each "name\tvalue" on a line, the two lists apart by an empty line.
        private const char MeterMark = '\u0001';
        private static string WriteMeter(save_meterscript m)
        {
            var b = new System.Text.StringBuilder();
            void List(save_meterscript.save_meter[] ms)
            {
                if (ms != null) foreach (var x in ms) if (x != null) b.Append(x.tipus).Append('\t').Append(x.value.ToString("R", System.Globalization.CultureInfo.InvariantCulture)).Append('\n');
            }
            List(m.mutatok); b.Append('\n'); List(m.szamlapok);
            return b.ToString();
        }
        private static save_meterscript ReadMeter(string text)
        {
            var lists = new[] { new List<save_meterscript.save_meter>(), new List<save_meterscript.save_meter>() };
            int which = 0;
            foreach (var line in text.Split('\n'))
            {
                if (line.Length == 0) { if (which == 0) which = 1; continue; }
                int tab = line.LastIndexOf('\t');
                if (tab < 0) continue;
                float.TryParse(line.Substring(tab + 1), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float v);
                lists[which].Add(new save_meterscript.save_meter { tipus = line.Substring(0, tab), value = v });
            }
            return new save_meterscript { mutatok = lists[0].ToArray(), szamlapok = lists[1].ToArray() };
        }

        /// The records as they are now, one plate string each, in place of the ones there.
        public static void WriteRecords(Dictionary<int, SaveRecord> map, List<save_rendszam> plates)
        {
            plates.RemoveAll(p => p != null && p.id >= RecordIdBase);
            foreach (var kv in map)
                if (kv.Key >= 0 && (kv.Value?.rendszam != null || kv.Value?.meter != null))
                    plates.Add(new save_rendszam
                    {
                        id = RecordIdBase + (uint)kv.Key,
                        S = (kv.Value.rendszam?.S ?? "") + (kv.Value.meter != null ? MeterMark + WriteMeter(kv.Value.meter) : "")
                    });
        }

        /// A new save_rendszam's string is empty, as the public game's was; the beta's is null. A wheel position mod
        /// makes its record's and reads it back as its saved XML: "" meant none yet, null threw (StringReader).
        [HarmonyLib.HarmonyPatch(typeof(save_rendszam), HarmonyLib.MethodType.Constructor, new System.Type[0])]
        private static class EmptyPlateString
        {
            [HarmonyLib.HarmonyPostfix]
            private static void Postfix(save_rendszam __instance) { if (__instance.S == null) __instance.S = ""; }
        }

        /// Into the save being written (a copy of savedatascript.s.data, made before the mods' last changes reach it).
        [HarmonyLib.HarmonyPatch(typeof(savedatascript), nameof(savedatascript.SaveToData))]
        private static class KeepRecords
        {
            [HarmonyLib.HarmonyPrefix]
            private static void Prefix(savedata _data)
            {
                var from = savedatascript.s != null ? savedatascript.s.data : null;
                if (from == null || _data?.itemData?.rendszam == null || !_records.TryGetValue(from, out var map) || map.Count == 0) return;
                try { WriteRecords(map, _data.itemData.rendszam); }
                catch (System.Exception e) { Diagnostics.QuietErrors.Once("LoaderCompat.KeepRecords", e); }
            }
        }

        /// Any record or placed item with this id: the beta keeps far items as records in data.itemData.items.
        public static bool RecordIdUsed(object records, int id)
        {
            Calls++;
            if (records is Dictionary<int, SaveRecord> own && own.ContainsKey(id)) return true;
            var s = savedatascript.s;
            if (s == null) return false;
            uint key = unchecked((uint)id);
            if (s.items != null && s.items.ContainsKey(key)) return true;
            var data = s.data;
            return data != null && data.itemData != null && savedatascript.IndexOfID(data.itemData.items, key, out _);
        }

        /// tosaveitemscript.FStart(int): the beta's takes a uint.
        public static void FStart(tosaveitemscript t, int id) { Calls++; if (t != null) t.FStart(unchecked((uint)id)); }

        /// savedatascript.properties: the save's (its date, seed, the distance driven) are the data's on the beta.
        public static saveproperties SaveProperties(savedatascript s) => s != null && s.data != null ? s.data.properties : null;

        /// tosaveitemscript.removeFromMemory: the public game's "not an item of its own" for display copies of a game prefab
        /// (a nitrous mod's gauge face is a compass inside the bottle). One never started isn't saved on the beta either, but the
        /// beta takes every item out of its parent the frame after it starts (NextFrameUnparent): the compass fell out and
        /// the gauge, its own colliders already removed, fell through the world. Kept where the mod put it.
        public static bool GetRemoveFromMemory(tosaveitemscript t) => t != null && t.dontUnparent;
        public static void SetRemoveFromMemory(tosaveitemscript t, bool value) { Calls++; if (t != null && value) t.dontUnparent = true; }

        /// GetComponent&lt;T&gt;() of a type the beta doesn't have (partscript): there is none.
        public static Component NoComponent(object owner) { Calls++; return null; }

        // ------------------------------------------------------------------ the main menu

        /// mainmenuscript.mainmenu's screens (menuhandler.s here), active while open: mods hide their own menu buttons then
        /// (three of the workshop's mods). The beta's settings and save screens switch their own object on.
        public static GameObject MenuSettingsScreen(menuhandler m) { Calls++; return settingsscript.s != null ? settingsscript.s.gameObject : null; }
        public static GameObject MenuSaveScreen(menuhandler m)
        {
            Calls++;
            var save = m != null && m.saveScreen != null ? m.saveScreen : savedatascript.s != null ? savedatascript.s.saveScreen : null;
            return save != null ? save.gameObject : null;
        }
        public static Transform MenuCanvas(menuhandler m) { Calls++; return m != null && m.canv != null ? m.canv.transform : null; }

        // ------------------------------------------------------------------ attaching

        private static tosaveitemscript SaveOf(pickupable p) => p == null ? null : p.tosave != null ? p.tosave : p.GetComponent<tosaveitemscript>();

        /// pickupable.attach: the beta keeps the attach script on the item's tosaveitemscript only.
        public static attachablescript GetAttach(pickupable p) { var t = SaveOf(p); return t != null ? t.attachable : null; }
        public static void SetAttach(pickupable p, attachablescript a) { Calls++; var t = SaveOf(p); if (t != null) t.attachable = a; }

        /// attachablescript.p: the item, through its tosaveitemscript, which the beta sets in the attach script's FStart; a
        /// part a mod just made (AddComponent, then its item set) has none yet, so it is found as FStart would.
        private static tosaveitemscript SaveOf(attachablescript a) => a == null ? null : a.tosave != null ? a.tosave : a.GetComponent<tosaveitemscript>();
        public static pickupable AttachableItem(attachablescript a) { var t = SaveOf(a); return t != null ? t.P : null; }
        public static void SetAttachableItem(attachablescript a, pickupable p) { Calls++; var t = SaveOf(a); if (t != null) t.P = p; }

        /// AddComponent<mountStuff>() in a mod: the public game's mount called its own FStart in Start; the beta's is started
        /// by its item's SetID only, which a mount a mod adds after the item started never gets (a basket mod's mount held
        /// nothing). Its starter calls FStart for the first frames (a no-op once started; a mod that copies a vanilla
        /// mount's fields onto it resets that), then goes.
        public static mountStuff AddMount(GameObject go)
        {
            Calls++;
            var m = go.AddComponent<mountStuff>();
            if (m != null) go.AddComponent<MountStarter>();
            return m;
        }
        public class MountStarter : MonoBehaviour
        {
            private int _frames;
            private void Update()
            {
                var m = GetComponent<mountStuff>();
                if (m != null && m.col != null) { m.FStart(); if (StartedMounts.Count > 256) StartedMounts.RemoveWhere(x => x == null); StartedMounts.Add(m); }
                if (m == null || ++_frames >= 3) Destroy(this);
            }
        }
        /// The mounts a starter started. One a mod left half set up (no item to lock to: an aircraft mod's) threw in its
        /// Update every second; it goes back to unstarted, as it was without the starter, and the error is dropped.
        private static readonly HashSet<mountStuff> StartedMounts = new HashSet<mountStuff>();
        public static int MountsStoppedAgain;
        [HarmonyLib.HarmonyPatch(typeof(mountStuff), "Update")]
        private static class MountStartedUpdate
        {
            [HarmonyLib.HarmonyFinalizer]
            private static System.Exception Finalizer(mountStuff __instance, System.Exception __exception, ref bool ___started)
            {
                if (__exception == null || __instance == null || !StartedMounts.Remove(__instance)) return __exception;
                ___started = false;
                MountsStoppedAgain++;
                return null;
            }
        }

        /// pickupable.cols / cols2: arrays there, lists here. A layerScript a mod added itself (AddComponent) is started as
        /// the game starts its own (layerScript.FStart: its item, its collider, its default layer): the game reads them for
        /// the item's colliders, and its Pickup threw on one that never started (a mod's nitrous bottle, its model's
        /// colliders: it could never be picked up, issue 44, 2026-11-27). A/B: StartModLayerScripts = false is before.
        public static bool StartModLayerScripts = true;
        public static int ModLayerScriptsStarted;
        public static layerScript[] GetCols(pickupable p) => p != null && p.cols != null ? p.cols.ToArray() : null;
        public static void SetCols(pickupable p, layerScript[] v) { Calls++; if (p != null) p.cols = v != null ? new List<layerScript>(StartLayerScripts(p, v)) : null; }
        public static layerScript[] GetCols2(pickupable p) => p != null && p.cols2 != null ? p.cols2.ToArray() : null;
        public static void SetCols2(pickupable p, layerScript[] v) { Calls++; if (p != null) p.cols2 = v != null ? new List<layerScript>(StartLayerScripts(p, v)) : null; }
        private static layerScript[] StartLayerScripts(pickupable p, layerScript[] v)
        {
            if (!StartModLayerScripts) return v;
            for (int i = 0; i < v.Length; i++)
            {
                var ls = v[i];
                if (ls == null || ls.started) continue;
                try { ls.FStart(p, i); ModLayerScriptsStarted++; }
                catch (System.Exception e) { Diagnostics.QuietErrors.Once("LoaderCompat.StartLayerScripts", e); }
            }
            return v;
        }

        /// attachablescript.targetTosave: what it is attached to.
        public static tosaveitemscript TargetTosave(attachablescript a)
        {
            if (a == null) return null;
            var t = a.AttachedToTosave();
            return t != null ? t : _loadTargets.TryGetValue(a, out var box) ? box.T : null;
        }
        /// attachablescript.targetTosave = t: what its next Load(pos) attaches it to (a tool mod's swap of an item for another).
        private sealed class TargetBox { public tosaveitemscript T; }
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<attachablescript, TargetBox> _loadTargets =
            new System.Runtime.CompilerServices.ConditionalWeakTable<attachablescript, TargetBox>();
        public static void SetTargetTosave(attachablescript a, tosaveitemscript t)
        {
            Calls++;
            if (a == null) return;
            _loadTargets.Remove(a);
            _loadTargets.Add(a, new TargetBox { T = t });
        }

        /// attachablescript.pointLocalPos: where it is attached, in its target's space. Detach destroys the point at the end
        /// of the frame, so it is still there for the read right after one.
        public static Vector3 PointLocalPos(attachablescript a)
        {
            if (a == null || a.point == null) return Vector3.zero;
            var t = a.point.GetComponentInParent<tosaveitemscript>();
            return t != null ? t.transform.InverseTransformPoint(a.point.position) : a.point.localPosition;
        }

        /// attachablescript.Load(localPos): attached to its target (targetTosave) at that place, as a save load did.
        public static void AttachableLoad(attachablescript a, Vector3 localPos)
        {
            Calls++;
            var t = TargetTosave(a);
            if (t == null) return;
            a.Attach(a.NewPoint(t.transform, t.transform.TransformPoint(localPos), a.transform.rotation));
        }

        /// tosaveitemscript.Claim(b): the public game's multiplayer claim on an item; the beta has none (a tool mod claims
        /// the part it swapped in). Nothing to do.
        public static void Claim(tosaveitemscript t, bool b) { Calls++; }

        /// mountStuff.Lock(item, ignoreRigidbody): the beta's Store (a modding toolkit's saved items put back in their mounts).
        public static void MountLock(mountStuff m, pickupable p, bool ignoreRigidbody) { Calls++; if (m != null) m.Store(p, ignoreRigidbody); }

        /// attachablescript.C: the item's own colliders, a public list there, a private array here.
        private static readonly System.Reflection.FieldInfo _c = GameLookups.Field(typeof(attachablescript), "C");
        public static List<Collider> GetC(attachablescript a)
        {
            if (a == null) return null;
            var arr = _c != null && _c.FieldType == typeof(Collider[]) ? (Collider[])_c.GetValue(a) : null;
            return new List<Collider>(arr ?? a.GetComponentsInChildren<Collider>());
        }
        public static void SetC(attachablescript a, List<Collider> v) { Calls++; if (a != null && _c != null && _c.FieldType == typeof(Collider[])) _c.SetValue(a, v != null ? v.ToArray() : null); }

        /// partslotscript.part = a: the public game's slot held one part; the beta's keeps a list (part() is its first).
        public static void SetSlotPart(partslotscript s, attachablescript a)
        {
            Calls++;
            if (s == null) return;
            if (s.parts == null) s.parts = new List<attachablescript>(); else s.parts.Clear();
            if (a != null) s.AddPart(a);
        }

        // ------------------------------------------------------------------ fields the beta dropped

        /// A field of the public game the beta has no counterpart for (LoaderRetarget's last resort): what the mod stored,
        /// per object (static fields: owner null), so the methods that name it run instead of stopping at their first line.
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<object, Dictionary<string, object>> _kept =
            new System.Runtime.CompilerServices.ConditionalWeakTable<object, Dictionary<string, object>>();
        private static readonly Dictionary<string, object> _keptStatic = new Dictionary<string, object>();
        public static T Kept<T>(object owner, string field)
        {
            var d = owner == null ? _keptStatic : _kept.TryGetValue(owner, out var x) ? x : null;
            return d != null && d.TryGetValue(field, out var v) && v is T t ? t : default(T);
        }
        public static void Keep<T>(object owner, string field, T value)
        {
            Calls++;
            (owner == null ? _keptStatic : _kept.GetValue(owner, _ => new Dictionary<string, object>()))[field] = value;
        }

        /// attachablescript.Refresh(): the public game refreshed the attach point's save data; the beta keeps none there.
        public static void RefreshAttachable(attachablescript a) { Calls++; }

        /// usablescript.Refresh(slide, xRot, yRot): the beta's Refresh also takes the turn state; the usable keeps its own.
        public static void RefreshUsable(usablescript u, float slide, float xRot, float yRot) { Calls++; u.Refresh(slide, xRot, yRot, u.currentTurnState); }

        /// layerScript.FStart(): the beta's takes the item and the collider's place in its list (nothing reads that index).
        public static void LayerFStart(layerScript l) { Calls++; if (l != null) l.FStart(l.p != null ? l.p : l.GetComponentInParent<pickupable>(), 0); }

        /// Harmony.PatchAll() in the mod's own code: as the mod asked, but a patch that fails on the beta undoes the ones
        /// applied before it (all or none, as ModHost's UseHarmony), and the mod's code goes on after the call.
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        public static void PatchAll(HarmonyLib.Harmony h) => PatchAllFrom(h, new System.Diagnostics.StackTrace().GetFrame(1).GetMethod().ReflectedType.Assembly);
        public static void PatchAllFrom(HarmonyLib.Harmony h, System.Reflection.Assembly a)
        {
            Calls++;
            try { h.PatchAll(a); }
            catch (System.Exception e)
            {
                try { h.UnpatchSelf(); } catch (System.Exception quietError) { Diagnostics.QuietErrors.Once("LoaderCompat.PatchAll", quietError); }
                PatchAllRefused++;
                Plugin.Log.LogWarning("ModHost: " + a.GetName().Name + ": its Harmony patches can't all apply on the beta, none applied: " + (e.InnerException ?? e).Message);
            }
        }
        public static int PatchAllRefused;

        /// GameObject.Find("Main"): the public game's object with mainscript on it. The beta's has another name, and an
        /// object named Main is something else; in the menu (no mainscript) the name is looked up as the mod asked.
        public static GameObject FindMain(string name) { Calls++; return mainscript.s != null ? mainscript.s.gameObject : GameObject.Find(name); }

        /// material.mainTexture.name: "" for a material without a texture (some of the beta's have none) instead of a throw.
        public static string TextureName(UnityEngine.Object texture) { Calls++; return texture != null ? texture.name : ""; }

        /// Assembly.Location: for a rewritten mod (loaded from bytes, no location) the DLL it came from.
        public static string AssemblyLocation(System.Reflection.Assembly a)
        {
            if (a == null) return null;
            lock (LoaderRetarget.Origins) if (LoaderRetarget.Origins.TryGetValue(a, out var p)) return p;
            return a.Location;
        }

        // ------------------------------------------------------------------ fuel caps

        /// tankcapscript.TCap/TOpen/TClosed and UpdCap(bool): the public game moved the cap between two transforms; the
        /// beta's cap turns by its usable between closedRot and openRot. Engine mods (ten in the workshop) set
        /// these in the middle of the method that builds the whole engine, which stopped at them: kept here for the mod to
        /// read back, the beta's own cap left as it is.
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<tankcapscript, Transform[]> _caps =
            new System.Runtime.CompilerServices.ConditionalWeakTable<tankcapscript, Transform[]>();
        private static Transform CapGet(tankcapscript c, int i) => c != null && _caps.TryGetValue(c, out var t) ? t[i] : null;
        private static void CapSet(tankcapscript c, int i, Transform v) { Calls++; if (c != null) _caps.GetValue(c, _ => new Transform[3])[i] = v; }
        public static Transform GetCapT(tankcapscript c) => CapGet(c, 0) ?? (c != null ? c.transform : null);
        public static void SetCapT(tankcapscript c, Transform v) => CapSet(c, 0, v);
        public static Transform GetCapOpen(tankcapscript c) => CapGet(c, 1);
        public static void SetCapOpen(tankcapscript c, Transform v) => CapSet(c, 1, v);
        public static Transform GetCapClosed(tankcapscript c) => CapGet(c, 2);
        public static void SetCapClosed(tankcapscript c, Transform v) => CapSet(c, 2, v);
        public static void UpdCap(tankcapscript c, bool sound) { Calls++; }
        // GOpen/GClosed: what the public game showed open or closed (lists of objects), kept the same way
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<tankcapscript, List<GameObject>[]> _capObjs =
            new System.Runtime.CompilerServices.ConditionalWeakTable<tankcapscript, List<GameObject>[]>();
        private static List<GameObject> CapObjs(tankcapscript c, int i) => c == null ? new List<GameObject>() : _capObjs.GetValue(c, _ => new[] { new List<GameObject>(), new List<GameObject>() })[i];
        public static List<GameObject> GetCapGOpen(tankcapscript c) => CapObjs(c, 0);
        public static void SetCapGOpen(tankcapscript c, List<GameObject> v) { Calls++; if (c != null) _capObjs.GetValue(c, _ => new[] { new List<GameObject>(), new List<GameObject>() })[0] = v ?? new List<GameObject>(); }
        public static List<GameObject> GetCapGClosed(tankcapscript c) => CapObjs(c, 1);
        public static void SetCapGClosed(tankcapscript c, List<GameObject> v) { Calls++; if (c != null) _capObjs.GetValue(c, _ => new[] { new List<GameObject>(), new List<GameObject>() })[1] = v ?? new List<GameObject>(); }

        /// pickupable.slots: the item's part slots, the beta's tosaveitemscript list (the same list).
        public static List<partslotscript> PickupSlots(pickupable p) { var t = SaveOf(p); return t != null ? t.partslotscripts : null; }

        // ------------------------------------------------------------------ cars

        /// carscript.driverSeat: the seat a mod set as the driver's, else the one the beta marks itself (driverSeat0): from
        /// the car's save list, or its children when that list is empty yet (a car a mod just spawned). Before 2026-11-25
        /// a seat a mod set was kept but never read back (an AI traffic mod's cars stopped in their setup).
        public static seatscript DriverSeat(carscript c)
        {
            Calls++;
            if (c == null) return null;
            seatscript set = Kept<seatscript>(c, "carscript.driverSeat");
            if (set != null) return set;
            var seats = c.tosave != null ? c.tosave.seats : null;
            if (seats != null) foreach (var s in seats) if (s != null && s.driverSeat0) return s;
            foreach (var s in c.GetComponentsInChildren<seatscript>(true)) if (s.driverSeat0) return s;
            return null;
        }

        /// carscript.ISwitches: the switches a mod set (the same array, so its element writes stick), else the usables the
        /// beta lists on the car's tosaveitemscript (a copy: the beta's save indexes them). Before 2026-11-26 a mod's own
        /// list was kept but never read back (a gearbox mod's gear handle was some other switch).
        public static usablescript[] Switches(carscript c)
        {
            if (c == null) return null;
            var set = Kept<usablescript[]>(c, "carscript.ISwitches");
            if (set != null) return set;
            return c.tosave != null && c.tosave.usables != null ? c.tosave.usables.ToArray() : null;
        }

        /// settingsscript.TAutoShift: the public game's auto-shift toggle, which mods copy as the template of a toggle of
        /// their own. The beta's is the controls tab's second; a copy of it would carry the game's togglesetting, whose
        /// references still point at the real toggles. Mods get one copy without it, its translated label or the toggle's listeners, kept
        /// inactive under the real toggle's parent (its copies scale as the menu's), made again when the menu is.
        public static UnityEngine.UI.Toggle AutoShiftToggle(settingsscript s)
        {
            Calls++;
            var set = Kept<UnityEngine.UI.Toggle>(s, "settingsscript.TAutoShift");
            if (set != null) return set;
            if (_toggleTemplate != null) return _toggleTemplate;
            var toggles = s != null && s.tabControls != null ? s.tabControls.toggles : null;
            var real = toggles != null && toggles.Length > 1 && toggles[1] != null ? toggles[1].toggle : null;
            if (real == null) return null;
            var holder = new GameObject("(toggle template)");
            holder.SetActive(false);
            holder.transform.SetParent(real.transform.parent, false);
            var copy = Object.Instantiate(real.gameObject, holder.transform, false);
            foreach (var t in copy.GetComponentsInChildren<togglesetting>(true)) Object.DestroyImmediate(t);
            foreach (var t in copy.GetComponentsInChildren<languagetext>(true)) Object.DestroyImmediate(t);   // the label is the mod's
            _toggleTemplate = copy.GetComponent<UnityEngine.UI.Toggle>();
            _toggleTemplate.onValueChanged = new UnityEngine.UI.Toggle.ToggleEvent();
            // the public game's label was a UI Text, which mods set; the beta's is TextMeshPro: a hidden Text passes it on
            var tmp = copy.GetComponentInChildren<TMPro.TMP_Text>(true);
            if (tmp != null && copy.GetComponentInChildren<UnityEngine.UI.Text>(true) == null)
            {
                var label = new GameObject("(label text)", typeof(RectTransform));
                label.transform.SetParent(tmp.transform, false);
                var text = label.AddComponent<UnityEngine.UI.Text>();
                text.text = tmp.text; text.color = Color.clear; text.raycastTarget = false;
                label.AddComponent<TextToTmp>().Target = tmp;
            }
            return _toggleTemplate;
        }
        private static UnityEngine.UI.Toggle _toggleTemplate;
        /// A mod's UI Text on a beta label: what the mod writes shows on the TextMeshPro beside it.
        public class TextToTmp : MonoBehaviour
        {
            public TMPro.TMP_Text Target;
            private UnityEngine.UI.Text _text;
            private void LateUpdate()
            {
                if (_text == null) _text = GetComponent<UnityEngine.UI.Text>();
                if (_text != null && Target != null && Target.text != _text.text) Target.text = _text.text;
            }
        }

        // ------------------------------------------------------------------ the world generator

        /// TerrainGenerator.TG.manager.settings.objGeneration: on the beta each step is the current map.
        public static mapSettingScript Map(mapSettingScript m) => m;

        /// ObjectGenerationScript.objTypes: every prefab the current map's object generators scatter, in their order (the
        /// same prefab objects: a mod's change to one is the game's).
        public static objGenScript.prefabClass[] ObjTypes(mapSettingScript m)
        {
            Calls++;
            var all = new List<objGenScript.prefabClass>();
            if (m != null && m.objGens != null)
                foreach (var g in m.objGens)
                    if (g != null && g.categories != null)
                        foreach (var c in g.categories)
                            if (c != null && c.prefabs != null) foreach (var p in c.prefabs) if (p != null && p.prefab != null) all.Add(p);
            return all.ToArray();
        }

        // ------------------------------------------------------------------ the road

        /// roadGenScript.roadList, the public game's main road as one array in driving order (the beta has none): the
        /// road pieces the beta has placed, in order (each one's next, as the beta finds it: GetNextRoad), the longest run.
        /// Each entry is a hidden marker on its piece (inactive, so nothing of roadscript runs on it), at the piece's start
        /// and facing the next one: an AI traffic mod drives from entry to entry and checks it passed one by its forward,
        /// and the beta's pieces keep their prefab's rotation. A marker stays with its piece (world shifts, despawn), so
        /// an entry a mod holds is still in the next list. Rebuilt at most once a second. A/B: false = before 2026-11-25
        /// (no list at all: the mod threw every frame; an empty one gives its own steering without roads).
        public static bool RoadListStandIn = true;
        public static int RoadListsBuilt;
        private static readonly Dictionary<roadGenScript, KeyValuePair<float, roadscript[]>> _roadLists = new Dictionary<roadGenScript, KeyValuePair<float, roadscript[]>>();
        private static readonly Dictionary<roadscript, roadscript> _roadMarkers = new Dictionary<roadscript, roadscript>();
        public static roadscript[] RoadList(roadGenScript g)
        {
            Calls++;
            if (g == null) throw new System.NullReferenceException();
            if (!RoadListStandIn || g.parent == null) return new roadscript[0];
            if (_roadLists.TryGetValue(g, out var cached) && Time.time - cached.Key < 1f && Time.time >= cached.Key) return cached.Value;
            var pieces = new List<roadscript>();
            for (int i = 0; i < g.parent.childCount; i++)
            {
                var r = g.parent.GetChild(i).GetComponent<roadscript>();
                if (r != null && r.gameObject.activeInHierarchy) pieces.Add(r);
            }
            var all = new HashSet<roadscript>(pieces);
            var next = new Dictionary<roadscript, roadscript>(); var hasPrevious = new HashSet<roadscript>();
            foreach (var r in pieces)
            {
                roadscript n = null;
                try { n = r.GetNextRoad(); } catch (System.Exception quietError) { Diagnostics.QuietErrors.Once("LoaderCompat.RoadList", quietError); }
                if (n != null && n != r && all.Contains(n)) { next[r] = n; hasPrevious.Add(n); }
            }
            List<roadscript> best = null;
            foreach (var r in pieces)
            {
                if (hasPrevious.Contains(r) || !next.ContainsKey(r)) continue;
                var run = new List<roadscript>(); var seen = new HashSet<roadscript>();
                for (var at = r; at != null && seen.Add(at); next.TryGetValue(at, out at)) run.Add(at);
                if (best == null || run.Count > best.Count) best = run;
            }
            var list = new roadscript[best != null ? best.Count : 0];
            for (int i = 0; i < list.Length; i++)
            {
                var piece = best[i];
                if (!_roadMarkers.TryGetValue(piece, out var marker) || marker == null)
                {
                    var go = new GameObject("(road list marker)");
                    go.SetActive(false);
                    go.transform.SetParent(piece.transform, false);
                    marker = go.AddComponent<roadscript>();
                    _roadMarkers[piece] = marker;
                }
                Vector3 from = piece.transform.position;
                Vector3 to = i + 1 < list.Length ? best[i + 1].transform.position : from + (i > 0 ? from - best[i - 1].transform.position : piece.transform.forward);
                marker.transform.position = from;
                if (to - from != Vector3.zero) marker.transform.rotation = Quaternion.LookRotation(to - from);
                list[i] = marker;
            }
            if (_roadMarkers.Count > 4 * list.Length + 64)   // pieces gone with their markers
            {
                var gone = new List<roadscript>();
                foreach (var kv in _roadMarkers) if (kv.Key == null || kv.Value == null) gone.Add(kv.Key);
                foreach (var k in gone) _roadMarkers.Remove(k);
            }
            _roadLists[g] = new KeyValuePair<float, roadscript[]>(Time.time, list);
            RoadListsBuilt++;
            return list;
        }

        // ------------------------------------------------------------------ the buildings along the road

        /// roadBuildingScript, the public game's generator of the buildings along the road
        /// (TerrainGenerationSettings.roadBuildingGeneration): the beta places them as points of interest, each a root of its
        /// own. Made without its native half, it equals null in Unity's sense, so mods that test it first skip it (two
        /// driving mods); parent is null (a tool mod places a building at the root, as the beta does, and an AI traffic
        /// mod finds no building under it); placedBuildings is the current map's placed points of interest (a sandstorm
        /// mod starts its storms at one).
        public class RoadBuildings : MonoBehaviour
        {
            public Transform parent;
            public Dictionary<int, GameObject> placedBuildings;
        }

        private static RoadBuildings _roadBuildings;
        private static float _roadBuildingsAt = float.MinValue;
        public static RoadBuildings RoadBuildingGeneration(mapSettingScript m)
        {
            Calls++;
            if ((object)_roadBuildings == null)
            {
                _roadBuildings = (RoadBuildings)System.Runtime.Serialization.FormatterServices.GetUninitializedObject(typeof(RoadBuildings));
                _roadBuildings.placedBuildings = new Dictionary<int, GameObject>();
            }
            // an AI traffic mod asks per car while it drives: the list is made again at most once a second
            if (Time.unscaledTime - _roadBuildingsAt < 1f) return _roadBuildings;
            _roadBuildingsAt = Time.unscaledTime;
            var placed = _roadBuildings.placedBuildings;
            placed.Clear();
            if (m != null && m.poiGens != null)
                foreach (var g in m.poiGens)
                    if (g != null && g.chunks != null)
                        foreach (var c in g.chunks.Values)
                            if (c != null && c.pois != null)
                                foreach (var poi in c.pois.Values)
                                    if (poi != null && poi.pobj != null && poi.pobj.gameObject.activeInHierarchy) placed[placed.Count] = poi.pobj.gameObject;
            return _roadBuildings;
        }

        // ------------------------------------------------------------------ what the beta dropped

        /// A game type the beta has no counterpart for (digholescript2, flashlightscript): nothing in the world has
        /// one, so finding one finds none.
        public class Gone : MonoBehaviour
        {
            /// the dropped flashlightscript's switch: a mod's method that calls it loads (there is no flashlight to call it on)
            public void TurnOff(bool off) { }
        }

        /// TerrainGenerator.GetTerrainHeight(at, ...): the ground's height under a world x/z (at.x, at.y), from the terrain
        /// tile there; 0 where none is loaded.
        public static float TerrainHeight(mapSettingScript m, Vector2 at, bool a, bool b, bool c)
        {
            Calls++;
            foreach (var t in Terrain.activeTerrains)
            {
                if (t == null || t.terrainData == null) continue;
                Vector3 o = t.transform.position, size = t.terrainData.size;
                if (at.x < o.x || at.y < o.z || at.x > o.x + size.x || at.y > o.z + size.z) continue;
                return t.SampleHeight(new Vector3(at.x, 0f, at.y)) + o.y;
            }
            return 0f;
        }

        /// inputscript.forward: a float on the beta, a bool on the public game.
        public static bool InputForward(inputscript i) => i != null && i.forward > 0f;

        /// Type.GetMethod(name) and (name, flags) where the beta's overloads make the name ambiguous (LoaderRetarget.LooseMethod).
        public static System.Reflection.MethodInfo GetMethod(System.Type t, string name) =>
            LoaderRetarget.LooseMethod(t, name, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static);
        public static System.Reflection.MethodInfo GetMethodFlags(System.Type t, string name, System.Reflection.BindingFlags flags) =>
            LoaderRetarget.LooseMethod(t, name, flags);
        /// MethodBase.Invoke(obj, args), with the optional parameters the args leave out at their defaults.
        public static object Invoke(System.Reflection.MethodBase m, object obj, object[] args)
        {
            if (m != null && obj != null && m.Name == "PatchAll" && m.DeclaringType?.Name == "Harmony" && args?.Length == 1 && args[0] is System.Reflection.Assembly a &&
                PatchEachClass(obj, a)) return null;
            return LoaderRetarget.InvokeLoose(m, obj, args);
        }

        /// HarmonyLib.AccessTools.TypeByName in a mod: the same answer, remembered; a miss until another assembly loads. Road
        /// Save Guard looks for seven other mods every frame: each miss searched every loaded type and HarmonyX logged a
        /// warning for it, and the game slowed until the test car stood still.
        public static System.Type TypeByName(string name)
        {
            if (name == null) return HarmonyLib.AccessTools.TypeByName(name);
            lock (_typesByName)
            {
                if (!_typesWatched) { _typesWatched = true; System.AppDomain.CurrentDomain.AssemblyLoad += (s, e) => { lock (_typesByName) _typesByName.Clear(); }; }
                if (_typesByName.TryGetValue(name, out var known)) return known;
            }
            var t = HarmonyLib.AccessTools.TypeByName(name);
            lock (_typesByName) _typesByName[name] = t;
            return t;
        }
        private static readonly Dictionary<string, System.Type> _typesByName = new Dictionary<string, System.Type>();
        private static bool _typesWatched;

        /// Harmony's PatchAll(assembly) called by reflection (a mod with its own copy of Harmony): each patch class on
        /// its own, as PatchAll does, but one whose target the beta dropped (mainscript.PauseRBs, setcenterofmass.Pause)
        /// is left out. PatchAll stopped there: the classes after it (that mod's wheels, sandstorm, pouring) never applied.
        public static int PatchClassesSkipped;
        private static bool PatchEachClass(object harmony, System.Reflection.Assembly a)
        {
            var create = harmony.GetType().GetMethod("CreateClassProcessor", new[] { typeof(System.Type) });
            if (create == null) return false;
            Calls++;
            System.Type[] types;
            try { types = a.GetTypes(); }
            catch (System.Reflection.ReflectionTypeLoadException e) { types = System.Array.FindAll(e.Types, t => t != null); }
            var skipped = new List<string>();
            foreach (var t in types)
            {
                try
                {
                    var processor = create.Invoke(harmony, new object[] { t });
                    processor?.GetType().GetMethod("Patch", System.Type.EmptyTypes)?.Invoke(processor, null);
                }
                catch (System.Exception e) { skipped.Add(t.Name + " (" + ((e.InnerException ?? e).InnerException ?? e.InnerException ?? e).Message + ")"); }
            }
            if (skipped.Count > 0)
            {
                PatchClassesSkipped += skipped.Count;
                Plugin.Log.LogWarning("ModHost: " + a.GetName().Name + ": " + skipped.Count + " of its Harmony patch classes left out, their targets not on the beta: " + string.Join("; ", skipped));
            }
            return true;
        }

        /// A tumbleweed mod destroys the tumbleweed spawners (thumbleweedMain) it finds, as on the public game, where nothing
        /// else held them. The beta's map keeps them in a list and updates each every frame: the destroyed one threw there,
        /// the rest of the map's Update never ran and the world never finished loading. Destroyed ones leave the list.
        [HarmonyLib.HarmonyPatch(typeof(mapSettingScript), "Update")]
        private static class DropDestroyedTumbleweeds
        {
            [HarmonyLib.HarmonyPrefix]
            internal static void Prefix(mapSettingScript __instance)
            {
                var list = __instance.thumbleweeds;
                if (list == null) return;
                for (int i = list.Count - 1; i >= 0; i--) if (list[i] == null) list.RemoveAt(i);
            }
        }
        /// RestartMap numbers them first, at world load: the destroyed one threw there, before the sky was set up.
        [HarmonyLib.HarmonyPatch(typeof(mapSettingScript), nameof(mapSettingScript.RestartMap))]
        private static class DropDestroyedTumbleweedsAtRestart
        {
            [HarmonyLib.HarmonyPrefix]
            private static void Prefix(mapSettingScript __instance) => DropDestroyedTumbleweeds.Prefix(__instance);
        }

        /// A light a mod destroyed under a headlight (an AI traffic mod puts a jack-o'-lantern in each driver's seat and
        /// destroys its Light; the beta's lantern has a headlightscript, the public game's had none): RefreshLights threw on
        /// it every frame (x8198 in one run). When it throws, the destroyed lights leave its lists (with their colour and
        /// dither at the same place) and the error is dropped; nothing runs when it doesn't throw. A/B: false = before 2026-11-25.
        public static bool DropDestroyedHeadlights = true;
        public static int HeadlightsDropped;
        [HarmonyLib.HarmonyPatch(typeof(headlightscript), nameof(headlightscript.RefreshLights))]
        private static class DropDestroyedHeadlight
        {
            [HarmonyLib.HarmonyFinalizer]
            private static System.Exception Finalizer(headlightscript __instance, System.Exception __exception)
            {
                if (!DropDestroyedHeadlights || !(__exception is System.NullReferenceException) || __instance == null || __instance.states == null) return __exception;
                int dropped = 0;
                foreach (var state in __instance.states)
                {
                    if (state == null || state.lights == null) continue;
                    foreach (var l in state.lights)
                    {
                        if (l == null || l.L == null) continue;
                        for (int i = l.L.Count - 1; i >= 0; i--)
                        {
                            if (l.L[i] != null) continue;
                            l.L.RemoveAt(i); dropped++;
                            if (l.defLColors != null && i < l.defLColors.Count) l.defLColors.RemoveAt(i);
                            if (l.dithers != null && i < l.dithers.Count) l.dithers.RemoveAt(i);
                        }
                    }
                }
                HeadlightsDropped += dropped;
                return dropped > 0 ? null : __exception;
            }
        }

        /// A car's wheel locks (the beta's: a box under each wheel collider while the car is parked) need each collider's
        /// wheelgraphicsscript. A mod that puts more colliders in the car's wheel lists (a handbrake mod: every rear one,
        /// a wheel's second collider too) left a null there: the car's Start threw before it let the locks go, the boxes
        /// held the car, and the lock size threw every frame. Such a collider gets the wheel it belongs to (W or W2), or
        /// loses its lock. Nothing changes where every collider has its wheel. A/B: false = before 2026-11-26.
        public static bool MendWheelLocks = true;
        public static int WheelLocksMended;
        [HarmonyLib.HarmonyPatch(typeof(carscript), nameof(carscript.UpdWheelLockSize))]
        private static class MendWheelLock
        {
            private static void Prefix(carscript __instance)
            {
                if (!MendWheelLocks || __instance == null || __instance.allWG == null || __instance.allWH == null || __instance.whColliders == null) return;
                if (!__instance.allWG.Contains(null) && __instance.allWG.Count == __instance.allWH.Count) return;
                wheelgraphicsscript[] wheels = null;
                for (int i = __instance.allWH.Count - 1; i >= 0; i--)
                {
                    if (i < __instance.allWG.Count && __instance.allWG[i] != null) continue;
                    var wh = __instance.allWH[i];
                    wheelgraphicsscript found = null;
                    if (wh != null)
                    {
                        if (wheels == null) wheels = __instance.GetComponentsInChildren<wheelgraphicsscript>(true);
                        foreach (var w in wheels) if (w != null && (w.W == wh || w.W2 == wh)) { found = w; break; }
                    }
                    if (found != null && i < __instance.allWG.Count) __instance.allWG[i] = found;
                    else
                    {
                        __instance.allWH.RemoveAt(i);
                        if (i < __instance.allWG.Count) __instance.allWG.RemoveAt(i);
                        if (i < __instance.whColliders.Count)
                        {
                            if (__instance.whColliders[i] != null) Object.Destroy(__instance.whColliders[i].gameObject);
                            __instance.whColliders.RemoveAt(i);
                        }
                    }
                    WheelLocksMended++;
                }
            }
        }

        /// tosaveitemscript.started = true from a mod, on an item the game never started: the mod's way to keep the item out
        /// of the save (AI traffic, a mod's own cars). The public game's car prefabs came with their parts in their slots;
        /// the beta's put them in when the car starts (FStart: the save entry, then each slot crafts the part under it), so
        /// such a car had no engine and stood. Each slot's part is crafted here as the slot would, without a save entry for
        /// the car or its parts, then the flag is set. Nothing changes for an item that started or a mod that clears it.
        /// A/B: false = before 2026-11-26.
        public static bool CraftUnstartedParts = true;
        public static int UnstartedPartsCrafted;
        public static void SetStarted(tosaveitemscript t, bool value)
        {
            Calls++;
            if (t == null) return;
            if (value && !t.started && CraftUnstartedParts) CraftParts(t, 0);
            t.started = value;
        }
        private static void CraftParts(tosaveitemscript t, int depth)
        {
            if (depth > 6 || t.partslotscripts == null) return;
            t.FInit();
            foreach (var slot in t.partslotscripts)
            {
                if (slot == null || slot.hasPart()) continue;
                try
                {
                    if (slot.T == null) slot.T = slot.transform;
                    var part = slot.T.GetComponentInChildren<attachablescript>(true);
                    if (part == null || part.slot == slot || !part.gameObject.activeSelf) continue;
                    var save = part.tosave != null ? part.tosave : part.GetComponent<tosaveitemscript>();
                    if (save == null) continue;
                    save.FInit();
                    if (save.P == null) continue;
                    if (save.P.tosave == null) save.P.tosave = save;
                    if (slot.pick != null && slot.pick.tosave == null) slot.pick.tosave = slot.pick.GetComponent<tosaveitemscript>();
                    part.FStart();
                    slot.Craft(part);
                    save.started = true;   // its own Start would put it in the save
                    UnstartedPartsCrafted++;
                    CraftParts(save, depth + 1);
                }
                catch (System.Exception e) { Diagnostics.QuietErrors.Once("LoaderCompat.CraftParts", e); }
            }
        }

        /// A part a mod made itself (AddComponent, its item set) and crafts into a slot before its own FStart ran: the slot's
        /// smooth snap moves the part's T, which FStart sets, and threw (a battery mod's battery never sat in its slot, the
        /// car never started). The part's T is its own transform, as FStart makes it. Every started part has it.
        [HarmonyLib.HarmonyPatch(typeof(partslotscript), nameof(partslotscript.Craft))]
        private static class CraftUnstartedPart
        {
            private static void Prefix(attachablescript p) { if (p != null && p.T == null) p.T = p.transform; }
        }

        /// A mount component (attachablescript) a mod gave an item after the item started: the beta sets one up only in the
        /// item's start (FStart: its transform, its save, its body), so one added later stayed empty - a mount-everything
        /// mod copies another item's component field by field from its prefab, which never started either. Held, the game
        /// sets its collision every frame through its save (SetCollider: tosave.P), which threw: the player's Update
        /// stopped every frame, the hand and the look froze (issue 44, 2026-11-27: items another mod had changed, the ones
        /// without a mount component of their own). Such a component is set up as the beta sets up its own, as RevaMP's
        /// mount mod does for an item it makes glueable. Nothing changes for one that started. A/B: false = before 2026-11-27.
        public static bool StartLateAttachables = true;
        public static int LateAttachablesStarted;
        [HarmonyLib.HarmonyPatch(typeof(fpscontroller), "Update")]
        private static class StartHeldAttachable
        {
            private static void Prefix(fpscontroller __instance)
            {
                if (!StartLateAttachables || __instance == null) return;
                var p = __instance.pickedUp;
                if (p == null) return;
                StartLatePickup(p);
                if (p.tosave == null) return;
                var a = p.tosave.attachable;
                if (a == null || a.started) return;
                StartLate(a, p.tosave);
            }
        }
        /// The frame an item is picked up (fpscontroller.Pickup, in the middle of Update) the prefix above had already run:
        /// the rest of that Update threw once. Started right after the pickup too.
        [HarmonyLib.HarmonyPatch]
        private static class StartPickedAttachable
        {
            private static IEnumerable<System.Reflection.MethodBase> TargetMethods()
            {
                foreach (var m in typeof(fpscontroller).GetMethods(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic))
                    if (m.Name == "Pickup" && m.GetParameters().Length >= 1 && m.GetParameters()[0].ParameterType == typeof(pickupable)) yield return m;
            }
            private static void Postfix(pickupable __0)
            {
                if (!StartLateAttachables || __0 == null) return;
                StartLatePickup(__0);
                if (__0.tosave == null) return;
                var a = __0.tosave.attachable;
                if (a != null && !a.started) StartLate(a, __0.tosave);
            }
        }
        /// The same for the item's pickup component (pickupable) a mod put in place of the item's own, a copy of another
        /// item's: it never started (its save, its body, what it hides when stored), and the item's save still named the
        /// destroyed one (tosave.P), through which the game sets a held item's collision: a mod's nitrous gauge, held, threw
        /// every frame. The item's save names this one, and it starts as the game starts its own (FStart).
        public static int LatePickupsStarted;
        internal static void StartLatePickup(pickupable p)
        {
            try
            {
                var save = p.tosave != null ? p.tosave : p.GetComponent<tosaveitemscript>();
                if (save != null && save.P == null) save.P = p;   // destroyed counts as null: the one it named is gone
                if (!p.started && save != null) { p.FStart(save); LatePickupsStarted++; }
            }
            catch (System.Exception e) { Diagnostics.QuietErrors.Once("LoaderCompat.StartLatePickup", e); }
        }

        /// The game asks CanPickup when the player looks at an item: a mod's late pickup component starts there, so the
        /// gates of the pickup itself read a started one.
        [HarmonyLib.HarmonyPatch(typeof(pickupable), nameof(pickupable.CanPickup))]
        private static class StartLookedPickup
        {
            private static void Prefix(pickupable __instance) { if (StartLateAttachables && __instance != null && !__instance.started) StartLatePickup(__instance); }
        }

        internal static void StartLate(attachablescript a, tosaveitemscript save)
        {
            try
            {
                if (a.T == null) a.T = a.transform;
                if (a.tosave == null) a.tosave = save;
                if (a.RB == null) a.RB = a.GetComponent<Rigidbody>();
                if (a.tipus == null) a.tipus = new string[0];
                if (a.maintipus == null) a.maintipus = new string[0];
                a.FStart();
                LateAttachablesStarted++;
            }
            catch (System.Exception e) { Diagnostics.QuietErrors.Once("LoaderCompat.StartLate", e); }
        }

        /// A mount whose collider's parent is unset (a mod copied an unstarted mount's private fields onto a started one):
        /// its freeze scan threw. The parent is the collider's, as FStart sets it. Every started mount has it already.
        [HarmonyLib.HarmonyPatch(typeof(mountStuff), nameof(mountStuff.Scan))]
        private static class MountScanParent
        {
            private static void Prefix(mountStuff __instance, ref Transform ___colParentTransform)
            {
                if (___colParentTransform == null && __instance != null && __instance.col != null) ___colParentTransform = __instance.col.transform.parent;
            }
        }

        /// A copy of a tank cap that had started (a helicopter mod clones the car06 cap it made in DbLoad) has the beta's
        /// started flag set, its particle module not (Unity copies the one, not the other): FStart skipped, the cap's Update
        /// threw every frame ("Do not create your own module instances", x2807 in one run). Such a copy starts as its own.
        /// A/B: false = before 2026-11-24.
        public static bool RestartCopiedTankCaps = true;
        public static int TankCapsRestarted;
        [HarmonyLib.HarmonyPatch(typeof(tankcapscript), "Start")]
        private static class RestartCopiedTankCap
        {
            private static readonly System.Reflection.FieldInfo Owner = HarmonyLib.AccessTools.Field(typeof(ParticleSystem.CollisionModule), "m_ParticleSystem");
            [HarmonyLib.HarmonyPrefix]
            private static void Prefix(tankcapscript __instance)
            {
                if (!RestartCopiedTankCaps || !__instance.started || __instance.ps == null || Owner == null) return;
                if (ReferenceEquals(Owner.GetValue(__instance.psc), __instance.ps)) return;   // started here (FStart before Start)
                __instance.started = false;
                TankCapsRestarted++;
            }
        }

        // ------------------------------------------------------------------ buildings

        /// buildingscript.itemsSpawned (buildingscript is the beta's poiScript): the place has started, which is when the
        /// beta spawns its items. A modding toolkit puts its mods' items into a building when this turns true.
        public static bool ItemsSpawned(poiScript p) => p != null && p.started;
        /// buildingscript.FStart(kind) (a building a mod placed itself): the beta's start with that generator id and the
        /// place id the building has (the mod writes its pos, which is the beta's posID, first).
        public static void PoiFStart(poiScript p, int kind) { Calls++; if (p == null) throw new System.NullReferenceException(); p.FStart(kind, p.posID); }

        /// attachablescript.Attach(Transform, RaycastHit): attached where the ray hit, as the beta's Attach(RaycastHit).
        public static void Attach(attachablescript a, Transform target, RaycastHit hit) { Calls++; if (a != null && hit.collider != null) a.Attach(hit); }

        // ------------------------------------------------------------------ TLD Loader 3.0.3's item registry

        /// A registered item (TLDLoader.ItemSlots in the stub, bound by ModHost): the category and the mod's own id at a
        /// place of the item table, and back. Unbound (no stub): no registered items.
        public delegate bool SlotKeyLookup(int slot, out int category, out int id);
        public static SlotKeyLookup SlotKey;
        public static System.Func<int, int, int> SlotOf;

        /// tosaveitemscript.id as a mod reads it: a registered item's own number in its mod (the beta's id is its place in
        /// the item table, which the save and the game use); every other item's id as it is.
        public static int ItemId(tosaveitemscript t)
        {
            int v = t.id;
            return SlotKey != null && SlotKey(v, out _, out int id) ? id : v;
        }
        /// A mod's id written to an item of a registered category: that item's place; anything else as it is.
        public static void SetItemId(tosaveitemscript t, int id)
        {
            Calls++;
            int category = ItemCategory(t);
            int slot = category != 0 && SlotOf != null ? SlotOf(category, id) : -1;
            t.id = slot >= 0 ? slot : id;
        }
        /// tosaveitemscript.category (the beta has none): a registered item's mod's; any other item's what a mod stored on it.
        public static int ItemCategory(tosaveitemscript t)
        {
            int v = t.id;
            return SlotKey != null && SlotKey(v, out int category, out _) ? category : Kept<int>(t, CategoryKey);
        }
        public static void SetItemCategory(tosaveitemscript t, int category)
        {
            if (t == null) throw new System.NullReferenceException();
            Keep(t, CategoryKey, category);
            int slot = SlotOf != null ? SlotOf(category, ItemId(t)) : -1;
            if (slot >= 0) t.id = slot;
        }
        private const string CategoryKey = "tosaveitemscript.category";   // KeptAdapter's name for it
        /// The field's address (`t.category.ToString()`): a copy's; a write through it changes nothing.
        public static ref int ItemIdRef(tosaveitemscript t) { _refCopy = ItemId(t); return ref _refCopy; }
        public static ref int ItemCategoryRef(tosaveitemscript t) { _refCopy = ItemCategory(t); return ref _refCopy; }
        [System.ThreadStatic] private static int _refCopy;
    }
}
