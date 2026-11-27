using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEngine;

namespace TLDRevamp
{
    /// ModHost's game-only parts: TLD Loader's loading panel and its Events, raised from the game's own calls.
    public static partial class ModHost
    {
        /// The beta's item database has empty slots (items taken out of the game keep their ids); the public game's had none.
        /// Mods go through every item in DbLoad (one gives every car a nitrous system, another makes every item
        /// attachable) and stopped at the first empty one: no nitrous spawners added, nothing attachable (issue 44).
        /// During DbLoad and OnLoad each empty slot holds an inactive placeholder; afterwards the slots are empty again (also in the
        /// array a mod made by appending to it), so no item id moves. A/B: false = before 2026-11-23.
        private static GameObject _slotPlaceholder;
        private static readonly List<int> _holes = new List<int>();
        static partial void CompatType(ref Type type) { type = typeof(LoaderCompat); }
        /// Again after each mod's DbLoad (a registered item can make the table longer): only the slots still empty; the ones
        /// filled before stay in the list for RestoreHoles.
        static partial void FillHoles()
        {
            var db = itemdatabase.s;
            var holes = _holes;
            if (db == null || db.items == null) return;
            for (int i = 0; i < db.items.Length; i++)
                if (db.items[i] == null)
                {
                    if (_slotPlaceholder == null)
                    {
                        _slotPlaceholder = new GameObject("(empty item slot)") { hideFlags = HideFlags.HideAndDontSave };
                        _slotPlaceholder.SetActive(false);
                    }
                    db.items[i] = _slotPlaceholder; holes.Add(i);
                }
            EmptySlotsFilled = holes.Count;
        }
        static partial void RestoreHoles()
        {
            var db = itemdatabase.s; var holes = _holes;
            if (db != null && db.items != null)
                foreach (int i in holes) if (i < db.items.Length && ReferenceEquals(db.items[i], _slotPlaceholder)) db.items[i] = null;
            if (_slotPlaceholder != null) { UnityEngine.Object.Destroy(_slotPlaceholder); _slotPlaceholder = null; }   // with what mods added to it
            holes.Clear();
        }

        static partial void ItemCount(ref int count) { var db = itemdatabase.s; count = db != null && db.items != null ? db.items.Length : 0; }
        /// TLD Loader's registry places (TLDLoader.ItemSlots.First/Count in the stub; here too for a game with no Loader mod
        /// left, whose saves can still hold their items).
        private const int RegistryFirst = 16384, RegistryEnd = 32768;
        private static Func<string, string, int, int> _assignAppended;
        /// The end of the table as the numbering before 2026-11-27 had it (each mod's items at the end, one mod after the
        /// other), and where each item it placed there is now: a save from then keeps loading. (A mod that also registers
        /// items through TLD Loader in the same DbLoad can make it wrong for the mods after it.)
        private static int _oldEnd = -1;
        private static readonly Dictionary<int, int> _oldPlaces = new Dictionary<int, int>();
        private static readonly HashSet<int> _missingTold = new HashSet<int>();

        static partial void AppendedReset() { _oldPlaces.Clear(); _oldEnd = -1; }

        /// The items a mod's DbLoad appended (places from `from` on). Each moves to its registry place (StableAppendedItems:
        /// TLDLoader.ItemSlots.AssignAppended, by the mod's ID, the item's name and how many of that name came before it) and
        /// that place is its tosaveitemscript.id; the end of the table is as it was before the mod's DbLoad, but for items
        /// TLD Loader's RegisterItem placed there. A game prefab a mod lists again, or one it lists twice, keeps its first
        /// place. No workshop mod remembers where its items went in the table (2026-11-27: they find them by component).
        static partial void NumberAppended(int from, string modId)
        {
            var db = itemdatabase.s;
            if (db == null || db.items == null) return;
            if (_oldEnd < 0) _oldEnd = from;
            int oldFrom = _oldEnd;
            if (from >= db.items.Length) return;
            _oldEnd = oldFrom + (db.items.Length - from);
            var earlier = new HashSet<GameObject>();
            for (int i = 0; i < from; i++) if (db.items[i] != null) earlier.Add(db.items[i]);
            if (!StableAppendedItems || _assignAppended == null)
            {
                for (int i = from; i < db.items.Length; i++)
                {
                    var go = db.items[i];
                    if (go == null || ReferenceEquals(go, _slotPlaceholder) || !earlier.Add(go)) continue;
                    var t = go.GetComponent<tosaveitemscript>();
                    if (t == null || t.id == i) continue;
                    t.id = i; AppendedItemsNumbered++;
                }
                return;
            }
            var names = new Dictionary<string, int>();
            var moved = new List<KeyValuePair<int, GameObject>>();
            int end = from;
            for (int i = from; i < db.items.Length; i++)
            {
                var go = db.items[i];
                if (go == null || ReferenceEquals(go, _slotPlaceholder)) continue;
                if (LoaderCompat.SlotKey != null && LoaderCompat.SlotKey(i, out _, out _)) { end = i + 1; continue; }   // RegisterItem's
                if (!earlier.Add(go)) continue;
                names.TryGetValue(go.name, out int n); names[go.name] = n + 1;
                int slot = _assignAppended(modId, go.name, n);
                moved.Add(new KeyValuePair<int, GameObject>(slot, go));
                _oldPlaces[oldFrom + (i - from)] = slot;
            }
            int length = end;
            foreach (var kv in moved) length = Math.Max(length, kv.Key + 1);
            var items = new GameObject[length];
            Array.Copy(db.items, items, from);
            for (int i = from; i < end; i++) if (LoaderCompat.SlotKey != null && LoaderCompat.SlotKey(i, out _, out _)) items[i] = db.items[i];
            foreach (var kv in moved)
            {
                items[kv.Key] = kv.Value;
                var t = kv.Value.GetComponent<tosaveitemscript>();
                if (t != null) t.id = kv.Key;
                AppendedItemsMoved++;
            }
            db.items = items;
        }

        /// A saved item comes back from its place in the item table (savedatascript.SpawnItem: itemdatabase.Item(prefabID),
        /// the game's error item when the place is empty, and a save then keeps the error item's id: the item is lost for
        /// good). One at a place of the numbering before 2026-11-27 comes from that item's place now. One at an empty registry
        /// place (its mod not loaded) is still the error item but keeps its place, so it saves as it was and comes back when
        /// the mod does. The idea of keeping it is Roadworthy's (TldModItems' placeholders, MIT,
        /// github.com/jaylikesbunda/TheLongDrive-Roadworthy); the code is RevaMP's.
        [HarmonyLib.HarmonyPatch(typeof(savedatascript), nameof(savedatascript.SpawnItem), new[] { typeof(save_item) })]
        private static class SavedPlaces
        {
            private static void Prefix(save_item _value, out int __state)
            {
                __state = -1;
                var db = itemdatabase.s;
                if (!StableAppendedItems || _value == null || db == null || db.CheckType(_value.prefabID)) return;
                if (_oldPlaces.TryGetValue(_value.prefabID, out int now) && db.CheckType(now)) { _value.prefabID = now; OldPlacesMapped++; return; }
                if (_value.prefabID >= RegistryFirst && _value.prefabID < RegistryEnd) __state = _value.prefabID;
            }

            private static void Postfix(GameObject __result, int __state)
            {
                if (__state < 0 || __result == null) return;
                var t = __result.GetComponent<tosaveitemscript>();
                var error = itemdatabase.s?.gerror?.GetComponent<tosaveitemscript>();
                if (t == null || error == null || t.id != error.id) return;   // the map placed one of its own instead
                t.id = __state; MissingModItemsKept++;
                if (_missingTold.Add(__state)) Plugin.Log.LogInfo("ModHost: a saved item at place " + __state + " has no prefab (its mod isn't loaded): the error item stands in and keeps its place");
            }
        }

        /// TLD Loader's loading panel (ModLoader.loading): an inactive object, so a mod's `loading.activeSelf` is false.
        static partial void MakeLoadingPanel()
        {
            var f = Loader?.GetField("loading", BindingFlags.Public | BindingFlags.Static);
            if (f == null || f.GetValue(null) != null) return;
            var go = new GameObject("TLDLoader loading (RevaMP)") { hideFlags = HideFlags.HideAndDontSave };
            go.SetActive(false);
            UnityEngine.Object.DontDestroyOnLoad(go);
            f.SetValue(null, go);
        }

        static partial void BindEvents(Assembly stub) => Events.Bind(stub);

        /// TLD Loader 3.0.3's ModLoader.Instance (its object, coroutines run on it) and item registry; LoaderCompat answers the
        /// mods' tosaveitemscript.id/category from the registry's places (TLDLoader.ItemSlots).
        static partial void MakeLoaderObject(Assembly stub)
        {
            try
            {
                Loader?.GetMethod("HostMakeInstance", BindingFlags.NonPublic | BindingFlags.Static)?.Invoke(null, null);
                var slots = stub.GetType("TLDLoader.ItemSlots");
                var key = slots?.GetMethod("TryKey", BindingFlags.Public | BindingFlags.Static);
                var find = slots?.GetMethod("Find", BindingFlags.Public | BindingFlags.Static);
                if (key != null) LoaderCompat.SlotKey = (LoaderCompat.SlotKeyLookup)Delegate.CreateDelegate(typeof(LoaderCompat.SlotKeyLookup), key);
                if (find != null) LoaderCompat.SlotOf = (Func<int, int, int>)Delegate.CreateDelegate(typeof(Func<int, int, int>), find);
                var appended = slots?.GetMethod("AssignAppended", BindingFlags.Public | BindingFlags.Static);
                if (appended != null) _assignAppended = (Func<string, string, int, int>)Delegate.CreateDelegate(typeof(Func<string, string, int, int>), appended);
                int first = Convert.ToInt32(slots?.GetField("First")?.GetRawConstantValue() ?? RegistryFirst), count = Convert.ToInt32(slots?.GetField("Count")?.GetRawConstantValue() ?? RegistryEnd - RegistryFirst);
                if (first != RegistryFirst || first + count != RegistryEnd) Plugin.Log.LogWarning("ModHost: TLD Loader's registry places are " + first + ".." + (first + count) + ", ModHost's " + RegistryFirst + ".." + RegistryEnd);
            }
            catch (Exception e) { Plugin.Log.LogWarning("ModHost: TLD Loader's object and item registry: " + (e.InnerException ?? e).Message); }
        }

        /// A/B (bridge `set`): false = before 2026-11-11 (mainscript.M.seed/load/napszak exist but stay 0/false/null).
        public static bool GameAliasesOn = true;
        private static bool _fromSave, _loadPending, _aliasWasUp;
        private static FieldInfo _fSeed, _fLoad, _fNapszak, _fLoaded, _fMulti, _fWorld, _fMainWorld;
        private static UnityEngine.Transform _worldAnchor;
        private static visszarako _mainWorld;
        private static bool _aliasLooked;
        private static bool _loadSignal;   // a save was chosen since the last look (the flag may be set and cleared between looks)

        // The two game calls that set DataFromMenuScript.load: the save screen's Load (an existing file) and the menu's
        // Continue. Not SetLoad itself: a one-line setter the JIT inlines into them, so a patch on it never runs (2026-11-11:
        // a reloaded save read load=false with it).
        [HarmonyLib.HarmonyPatch(typeof(newSaveScreenScript), nameof(newSaveScreenScript.Load))]
        private static class LoadChosen { [HarmonyLib.HarmonyPrefix] private static void Prefix(string _name) { if (File.Exists(_name)) _loadSignal = true; } }

        [HarmonyLib.HarmonyPatch(typeof(menuhandler), nameof(menuhandler.AutoContinue))]
        private static class ContinueChosen { [HarmonyLib.HarmonyPrefix] private static void Prefix() => _loadSignal = true; }

        /// The public game's mainscript.seed / load / napszak (the preloader adds them, NotSerialized) set from the beta's
        /// world seed, whether the world came from a save, and its day/night cycle.
        static partial void GameAliases(bool worldUp)
        {
            if (!GameAliasesOn) return;
            if (!_aliasLooked)
            {
                _aliasLooked = true;
                const BindingFlags f = BindingFlags.Public | BindingFlags.Instance;
                _fSeed = GameLookups.ReflectionField(typeof(mainscript), "seed", f); _fLoad = GameLookups.ReflectionField(typeof(mainscript), "load", f); _fNapszak = GameLookups.ReflectionField(typeof(mainscript), "napszak", f);
                _fLoaded = GameLookups.ReflectionField(typeof(mainscript), "loaded", f); _fMulti = GameLookups.ReflectionField(typeof(mainscript), "multi", f); _fWorld = GameLookups.ReflectionField(typeof(mainscript), "world", f);
                _fMainWorld = GameLookups.ReflectionField(typeof(mainscript), "mainWorld", f);
            }
            var dm = DataFromMenuScript.s;
            bool signal = _loadSignal; _loadSignal = false;
            bool load = FromSaveLatch(_aliasWasUp, worldUp, (dm != null && dm.load) || signal, Net.Mp.Client != null, ref _loadPending, ref _fromSave);
            _aliasWasUp = worldUp;
            var m = mainscript.s;
            if (m == null) return;
            try
            {
                if (_fSeed != null && dm != null) _fSeed.SetValue(m, dm.seed);
                if (_fLoad != null) _fLoad.SetValue(m, load);
                if (_fNapszak != null && !ReferenceEquals(_fNapszak.GetValue(m), napszakvaltakozas.s)) _fNapszak.SetValue(m, napszakvaltakozas.s);
                // before this frame's OnLoad: false through it and its frame's Update, true from the next frame
                if (_fLoaded != null) _fLoaded.SetValue(m, worldUp && _gameLoadedCalled);
                if (_fMulti != null) _fMulti.SetValue(m, Net.Entities.InSession);
                if ((_fWorld != null || _fMainWorld != null) && worldUp)
                {
                    if (_worldAnchor == null)
                    {
                        _worldAnchor = new UnityEngine.GameObject("RevaMP mainscript.world (TLD Loader mods)").transform;
                        // the public game's mainWorld: its coord is the world offset. Started already, so the game never
                        // lists it among the objects it moves back (this anchor follows the offset below)
                        _mainWorld = _worldAnchor.gameObject.AddComponent<visszarako>();
                        _mainWorld.started = true; _mainWorld.dont = true; _mainWorld.dontlookUnderMap = true;
                    }
                    if (_fWorld != null && !ReferenceEquals(_fWorld.GetValue(m), _worldAnchor)) _fWorld.SetValue(m, _worldAnchor);
                    if (_fMainWorld != null && !ReferenceEquals(_fMainWorld.GetValue(m), _mainWorld)) _fMainWorld.SetValue(m, _mainWorld);
                    var o = m.visszarakva;   // global = unity - offset: world.InverseTransformPoint(p) = p - offset
                    _worldAnchor.position = new UnityEngine.Vector3((float)o.x, (float)o.y, (float)o.z);
                    _mainWorld.coord = o;
                }
            }
            catch (Exception e) { GameAliasesOn = false; Plugin.Log.LogWarning("ModHost: mainscript.seed/load/napszak/loaded/multi/world/mainWorld: " + e.Message); }
        }

        public static string AliasStatus()
        {
            var aliases = new System.Collections.Generic.List<string>();
            foreach (var a in new[] { "mainscript.M -> mainscript.s (host mirror)", "itemdatabase.d -> itemdatabase.s (host mirror)",
                                     "savedatascript.d -> savedatascript.s (host mirror)", "mainscript.seed -> DataFromMenuScript.s.seed (host snapshot)",
                                     "mainscript.load -> from-save latch (host snapshot)", "mainscript.napszak -> napszakvaltakozas.s (host mirror)",
                                     "mainscript.loaded -> world up and OnLoad done", "mainscript.multi -> RevaMP session", "mainscript.world -> a transform at the world offset",
                                     "mainscript.mainWorld -> a visszarako whose coord is the world offset" })
                aliases.Add(Json.Str(a));
            foreach (var a in LoaderRetarget.AliasDescriptions()) aliases.Add(Json.Str(a));
            string list = "\"aliases\":[" + string.Join(",", aliases.ToArray()) + "],\"rewrites\":" + (Retarget ? "true" : "false") + ",";
            var m = mainscript.s;
            if (m == null) return "{" + list + "\"world\":false}";
            return "{" + list + "\"seed\":" + (_fSeed?.GetValue(m) ?? "null") + ",\"load\":" + ((_fLoad?.GetValue(m) as bool?) == true ? "true" : "false") +
                   ",\"napszak\":" + (_fNapszak?.GetValue(m) != null ? "true" : "false") + ",\"gameSeed\":" + (DataFromMenuScript.s?.seed.ToString() ?? "null") + "}";
        }

        /// UseHarmony, safely: all of a mod's patches or none. TLD Loader runs PatchAll on the mod's assembly; on the beta a
        /// mod written for the public game breaks the game itself that way:
        /// - a patch whose code names a class that can't load here throws inside the game's own method on every call
        ///   (a car battery mod: ~9,600 exceptions in carscript.Electronics/LEDUPD, enginescript.Update,
        ///   headlightscript.RefreshLights in one test run);
        /// - PatchAll stops at the first patch whose target is gone and leaves the ones before it applied; patching the rest
        ///   class by class instead (tried 2026-11-11) applied a set its author never ran together and one mod's set hung
        ///   the game at start-up, every time.
        /// So nothing is applied until every patch class passes: its methods compile (a missing type or member fails here,
        /// not inside the game) and its target, where the attributes name one, exists on the beta. One failure: none applied,
        /// and the log names it. Then all are applied; if one still throws, all are undone.
        static partial void PatchModSafely(Loaded l)
        {
            if (l.BrokenTypes > 0) { Refuse(l, l.BrokenTypes + " of its types can't load on the beta"); return; }
            var asm = l.Mod.GetType().Assembly;
            var classes = new System.Collections.Generic.List<Type>();
            foreach (var t in HarmonyLib.AccessTools.GetTypesFromAssembly(asm))
            {
                if (t == null) continue;
                System.Collections.Generic.List<HarmonyLib.HarmonyMethod> attrs;
                try { attrs = HarmonyLib.HarmonyMethodExtensions.GetFromType(t); } catch (Exception e) { Refuse(l, t.FullName + ": " + e.Message); return; }
                if (attrs == null || attrs.Count == 0) continue;
                string why = Check(t, HarmonyLib.HarmonyMethod.Merge(attrs));
                if (why != null) { Refuse(l, t.FullName + ": " + why); return; }
                classes.Add(t);
            }
            var h = new HarmonyLib.Harmony("com." + l.Id.ToLowerInvariant());
            try { foreach (var t in classes) h.CreateClassProcessor(t).Patch(); }
            catch (Exception e)
            {
                try { h.UnpatchSelf(); } catch (System.Exception quietError) { Diagnostics.QuietErrors.Once("ModHost.RollbackPatches", quietError); }
                Refuse(l, "patching threw, all undone: " + (e.InnerException ?? e).Message);
                return;
            }
            l.Mod.GetType().BaseType?.GetField("HarmonyInstance")?.SetValue(l.Mod, h);
            HarmonyModsPatched++; HarmonyClassesPatched += classes.Count;
            Plugin.Log.LogInfo("ModHost: " + l.Id + ": Harmony (UseHarmony): all " + classes.Count + " patch class(es) applied");
        }

        public static int HarmonyModsPatched, HarmonyClassesPatched, HarmonyModsRefused;

        /// A modding toolkit (the library most workshop item mods use) saves a despawned item in CustomSaveItem.OnDestroy
        /// by looking its name up; the items it builds into the item database have no name (only spawned copies get one). The
        /// public game kept its database until quit; the beta makes a new one per scene load, so entering a world threw
        /// ArgumentNullException once per toolkit item (x8 in one mod). Nameless ones are skipped: it threw there and saved nothing.
        /// A/B: false = before 2026-11-24.
        public static bool AmtTemplatesQuiet = true;
        public static int AmtTemplatesSkipped;
        private static PropertyInfo _amtName;
        private static FieldInfo _amtProperties;
        static partial void KnownModFixes(Assembly asm)
        {
            var t = asm.GetType("Amt.CustomSaveItem", false);
            var destroy = t?.GetMethod("OnDestroy", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            var props = t?.GetField("properties");
            var name = props?.FieldType.GetProperty("Name");
            if (destroy == null || name == null) return;
            _amtProperties = props; _amtName = name;
            try
            {
                new HarmonyLib.Harmony(Plugin.Guid + ".amt").Patch(destroy, prefix: new HarmonyLib.HarmonyMethod(typeof(ModHost).GetMethod(nameof(AmtTemplateDestroyed), BindingFlags.NonPublic | BindingFlags.Static)));
                Plugin.Log.LogInfo("ModHost: " + asm.GetName().Name + ": a modding toolkit's item templates destroyed with the old item database are not saved");
            }
            catch (Exception e) { Plugin.Log.LogWarning("ModHost: " + asm.GetName().Name + ": item template OnDestroy guard: " + e.Message); }
        }
        private static bool AmtTemplateDestroyed(object __instance)
        {
            if (!AmtTemplatesQuiet) return true;
            try
            {
                var p = _amtProperties.GetValue(__instance);
                if (p != null && _amtName.GetValue(p, null) != null) return true;
            }
            catch { return true; }
            AmtTemplatesSkipped++;
            return false;
        }

        private static void Refuse(Loaded l, string why)
        {
            HarmonyModsRefused++;
            Plugin.Log.LogWarning("ModHost: " + l.Id + " asks for Harmony (UseHarmony); none of its patches applied: " + why);
        }

        /// null = fine: the class's methods compile, and the target its attributes name exists here.
        private static string Check(Type t, HarmonyLib.HarmonyMethod m)
        {
            const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly;
            try
            {
                foreach (var me in t.GetMethods(all))
                    if (!me.IsAbstract && !me.ContainsGenericParameters) System.Runtime.CompilerServices.RuntimeHelpers.PrepareMethod(me.MethodHandle);
            }
            catch (Exception e) { return "its code doesn't compile here: " + (e.InnerException ?? e).Message; }
            // a class that finds its target itself (TargetMethod/TargetMethods) is checked by patching it
            if (m == null || m.declaringType == null || HarmonyLib.AccessTools.Method(t, "TargetMethod") != null || HarmonyLib.AccessTools.Method(t, "TargetMethods") != null) return null;
            try
            {
                var dt = m.declaringType; var args = m.argumentTypes;
                object target;
                switch (m.methodType ?? HarmonyLib.MethodType.Normal)
                {
                    case HarmonyLib.MethodType.Getter: target = GameLookups.RuntimeHarmonyGetter(dt, m.methodName); break;
                    case HarmonyLib.MethodType.Setter: target = GameLookups.RuntimeHarmonySetter(dt, m.methodName); break;
                    case HarmonyLib.MethodType.Constructor: target = GameLookups.RuntimeHarmonyConstructor(dt, args); break;
                    case HarmonyLib.MethodType.StaticConstructor: target = dt.TypeInitializer; break;
                    default: target = m.methodName == null ? (object)dt : GameLookups.RuntimeHarmonyMethod(dt, m.methodName, args); break;
                }
                return target == null ? "its target " + dt.Name + "." + m.methodName + " doesn't exist on the beta" : null;
            }
            catch (Exception e) { return "its target can't be found: " + e.Message; }
        }

        /// TLD Loader's Events, raised from the same game calls TLD Loader patches: sitting down / standing up
        /// (seatscript.GetIn/GetOut), saving, loading, deleting a save, and the pause menu (mainscript.SetCursorVisible).
        internal static class Events
        {
            private static MethodInfo _sit, _stand, _save, _load, _delete, _pause, _unpause;
            public static long Raised;
            internal static void Bind(Assembly stub)
            {
                var t = stub.GetType("TLDLoader.Events");
                if (t == null) return;
                const BindingFlags f = BindingFlags.NonPublic | BindingFlags.Static;
                _sit = t.GetMethod("RaiseOnSit", f); _stand = t.GetMethod("RaiseOnStand", f); _save = t.GetMethod("RaiseOnSave", f);
                _load = t.GetMethod("RaiseOnSaveLoad", f); _delete = t.GetMethod("RaiseOnSaveDelete", f);
                _pause = t.GetMethod("RaiseOnPause", f); _unpause = t.GetMethod("RaiseOnUnpause", f);
            }
            private static void Raise(MethodInfo m, params object[] a)
            {
                if (m == null || Mods.Count == 0) return;
                try { m.Invoke(null, a); Raised++; } catch (Exception e) { Plugin.Log.LogWarning("ModHost: TLD Loader event " + m.Name + ": " + (e.InnerException ?? e).Message); }
            }

            [HarmonyLib.HarmonyPatch(typeof(seatscript), nameof(seatscript.GetIn))]
            private static class Sit { [HarmonyLib.HarmonyPrefix] private static void Prefix(seatscript __instance) => Raise(_sit, __instance); }

            [HarmonyLib.HarmonyPatch(typeof(seatscript), nameof(seatscript.GetOut))]
            private static class Stand { [HarmonyLib.HarmonyPrefix] private static void Prefix(seatscript __instance) => Raise(_stand, __instance); }

            [HarmonyLib.HarmonyPatch(typeof(newSaveScreenScript), nameof(newSaveScreenScript.Save))]
            private static class Save { [HarmonyLib.HarmonyPostfix] private static void Postfix(string _text, bool __runOriginal) { if (__runOriginal) Raise(_save, _text); } }

            [HarmonyLib.HarmonyPatch(typeof(newSaveScreenScript), nameof(newSaveScreenScript.Load))]
            private static class Load { [HarmonyLib.HarmonyPostfix] private static void Postfix(string _name) { if (File.Exists(_name)) Raise(_load, _name); } }

            [HarmonyLib.HarmonyPatch(typeof(newSaveScreenScript), nameof(newSaveScreenScript.PressedDeleteYes))]
            private static class Delete
            {
                [HarmonyLib.HarmonyPrefix]
                private static void Prefix(newSaveScreenScript __instance)
                {
                    if (_delete == null || Mods.Count == 0) return;
                    try
                    {
                        // the beta's own choice in PressedDeleteYes: an autosave slot, or the selected save
                        var which = GameLookups.FieldValue<bool>(typeof(newSaveScreenScript), "BautoSaveSelected", __instance)
                            ? __instance.AutoSaveReferences[__instance.selectedAutoSaveIndex]
                            : __instance.LSaveReferences[GameLookups.FieldValue<int>(typeof(newSaveScreenScript), "CurrentSelected", __instance)];
                        if (which != null) Raise(_delete, which.name);
                    }
                    catch (System.Exception quietError) { Diagnostics.QuietErrors.Once("ModHost.DeleteSave", quietError); }
                }
            }

            /// The pause menu opening and closing, once each. mainscript.Update calls SetCursorVisible every frame, so raising
            /// on each call fired OnUnpause ~60 times a second while playing (OnPause while paused); its argument isn't the
            /// pause either (FreeCursorScreens frees the cursor under the join screen). The game's own pauseMenuOpen is read
            /// there instead, and only a change raises. A new world starts from its own state, with nothing raised.
            private static mainscript _pauseWorld;
            private static bool _paused;
            internal static long PauseChecks;

            [HarmonyLib.HarmonyPatch(typeof(mainscript), nameof(mainscript.SetCursorVisible))]
            private static class Pause
            {
                [HarmonyLib.HarmonyPrefix]
                private static void Prefix(mainscript __instance)
                {
                    PauseChecks++;
                    bool now = __instance.pauseMenuOpen;
                    if (!ReferenceEquals(__instance, _pauseWorld)) { _pauseWorld = __instance; _paused = now; return; }
                    if (now == _paused) return;
                    _paused = now;
                    Raise(now ? _pause : _unpause);
                }
            }
        }

    }
}
