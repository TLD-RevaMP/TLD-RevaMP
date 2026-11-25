using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace TLDRevamp.Net
{
    /// Hardcore. In single player the player's own switch (Settings → Revamp → World → Hardcore, `Own`); in a session the
    /// host's (Session settings → Hardcore; SessionSettingsSnapshot.Hardcore, protocol 63), which a hosted session starts
    /// from the host's own switch and writes back to it. While it is on, on every machine in the session:
    ///  - the developer menu (kaposztaleves, F1) does not open, not even for the host, and its "spawn the last item again"
    ///    key spawns nothing. The bridge's own spawns (tests) are not the menu and still work.
    ///  - faucets are empty: a building's tanks (poiScript.tanks: the faucets of some houses, water towers) hold no
    ///    water. The water is only kept aside, not lost: the building's record still carries it (save_poi.Save), and it
    ///    is back in the tank when Hardcore goes off. Other fluids in a building's tank stay. A building's tank has no
    ///    network path (each machine keeps its own level), so each machine empties its own.
    ///  - a part a crash knocks off (attachablescript.FallOFf, on the machine that simulates the car) falls as before,
    ///    can't be mounted again, and is deleted for everyone VanishS seconds later, whoever holds it by then.
    ///  - cleaning tools clean nothing: partconditionscript.Repair (wire brush, sandpaper, sponge, spray) and
    ///    washtriggerscript.Wash (dirt, blood) do nothing. A shot still breaks what a shot breaks (Wash's weapon path).
    public static class Hardcore
    {
        public static float VanishS = 5f;
        public static long MenuClosed, SpawnsRefused, TanksDried, TanksRefilled, PartsDoomed, PartsVanished, MountsRefused, RepairsRefused, WashesRefused;
        public static double WaterHidden;

        /// The player's own switch (RevampSettings "hardcore"): single player, and what a hosted session starts with.
        public static bool Own;

        /// What counts now: the host's setting in a session (a joiner's, as it last heard it from Welcome on), else the own one.
        public static bool On => Mp.Server != null ? Mp.Server.SessionSettings.Hardcore : Mp.Client != null ? DeveloperAccess.ClientHardcore : Own;

        /// The settings row: the own switch, and the session's too while hosting (as its Session settings row would).
        internal static void SetOwn(bool on)
        {
            Own = on;
            var server = Mp.Server;
            if (server != null && server.SessionSettings.Hardcore != on) server.UpdateSessionSettings(x => x.Hardcore = on, false);
        }

        private static bool _was;
        private static float _nextPrune;

        /// Plugin.Update: follows the setting as it changes (the host's click, a new snapshot, the session's end) and
        /// deletes the crashed-off parts whose time is up.
        internal static void Tick()
        {
            bool on = On;
            if (on != _was)
            {
                _was = on;
                Plugin.Log.LogInfo("hardcore: " + (on ? "on" : "off"));
                if (on) DryAll(); else RefillAll();
            }
            if (_doomed.Count > 0) VanishTick();
            if (Time.realtimeSinceStartup >= _nextPrune) { _nextPrune = Time.realtimeSinceStartup + 2f; Prune(); }
        }

        // ---------------------------------------------------------------- the developer menu
        /// Closed in the same frame it would open (the game opens it inside Upd), whatever opened it.
        [HarmonyPatch(typeof(kaposztaleves), nameof(kaposztaleves.Upd))]
        private static class NoDevMenu
        {
            [HarmonyPostfix]
            private static void Postfix(kaposztaleves __instance)
            {
                if (__instance == null || !__instance.isOpen || !On) return;
                __instance.isOpen = false;
                __instance.Closed();
                __instance.SetMenu();
                MenuClosed++;
            }
        }

        [HarmonyPatch(typeof(kaposztaleves), nameof(kaposztaleves.LastSpawn))]
        private static class NoLastSpawn
        {
            [HarmonyPrefix]
            private static bool Prefix() { if (!On) return true; SpawnsRefused++; return false; }
        }

        // ---------------------------------------------------------------- faucets
        /// The water taken out of a building's tank while Hardcore is on, litres per tank.
        private static readonly Dictionary<tankscript, float> _hidden = new Dictionary<tankscript, float>();

        /// A building made anew (poiGenScript: FStart, then its record's Load). FStart runs again on a building that was
        /// only hidden (a fixed one): its tanks are as they were, so they are left alone.
        [HarmonyPatch(typeof(poiScript), nameof(poiScript.FStart))]
        private static class DryNewBuilding
        {
            [HarmonyPrefix] private static void Prefix(poiScript __instance, out bool __state) => __state = __instance != null && __instance.started;
            [HarmonyPostfix] private static void Postfix(poiScript __instance, bool __state) { if (!__state && On) Dry(__instance); }
        }

        [HarmonyPatch(typeof(save_poi), nameof(save_poi.Load), new[] { typeof(poiScript) })]
        private static class DryLoadedBuilding
        {
            [HarmonyPostfix] private static void Postfix(poiScript _poi) { if (On) Dry(_poi); }
        }

        /// The building's record keeps the water it had: the world saved during Hardcore has its faucets back without it.
        [HarmonyPatch(typeof(save_poi), nameof(save_poi.Save), new[] { typeof(poiScript) })]
        private static class KeepWaterInRecord
        {
            [HarmonyPostfix]
            private static void Postfix(save_poi __instance, poiScript _poi)
            {
                if (_hidden.Count == 0 || _poi == null || _poi.tanks == null || __instance.tanks == null) return;
                for (int j = 0; j < _poi.tanks.Count; j++)
                {
                    var t = _poi.tanks[j];
                    if (t == null || !_hidden.TryGetValue(t, out float litres) || litres <= 0f) continue;
                    var rec = __instance.tanks.FindLast(x => x != null && x.ind == j);
                    if (rec != null) AddWater(rec, litres);
                }
            }
        }

        /// Takes the water out of a building's tanks; what was taken replaces what was kept for them before.
        internal static void Dry(poiScript p)
        {
            if (p == null || p.tanks == null) return;
            foreach (var t in p.tanks)
            {
                if (t == null || t.F == null || t.F.fluids == null) continue;
                float litres = 0f;
                for (int i = t.F.fluids.Count - 1; i >= 0; i--)
                {
                    var f = t.F.fluids[i];
                    if (f == null || f.type != mainscript.fluidenum.water) continue;
                    litres += Mathf.Max(0f, f.amount);
                    t.F.fluids.RemoveAt(i);
                }
                if (litres <= 0f) { _hidden.Remove(t); continue; }
                _hidden[t] = litres;
                t.Upd(dontSync: true);
                TanksDried++; WaterHidden += litres;
            }
        }

        private static void DryAll()
        {
            foreach (var p in LoadedBuildings()) Dry(p);
        }

        /// Hardcore off (or the session over): the water back in every tank still here.
        private static void RefillAll()
        {
            foreach (var kv in _hidden)
            {
                var t = kv.Key;
                if (t == null || t.F == null || t.F.fluids == null) continue;
                var water = t.F.fluids.Find(f => f != null && f.type == mainscript.fluidenum.water);
                if (water != null) water.amount += kv.Value;
                else t.F.fluids.Add(new mainscript.fluid(mainscript.fluidenum.water, kv.Value));
                t.Upd(dontSync: true);
                TanksRefilled++;
            }
            _hidden.Clear();
        }

        private static void AddWater(save_tankscript rec, float litres)
        {
            int n = rec.type != null ? rec.type.Length : 0;
            for (int i = 0; i < n; i++)
                if (rec.type[i] == (int)mainscript.fluidenum.water && rec.amount != null && i < rec.amount.Length) { rec.amount[i] += litres; return; }
            var types = new int[n + 1]; var amounts = new float[n + 1];
            for (int i = 0; i < n; i++) { types[i] = rec.type[i]; amounts[i] = rec.amount != null && i < rec.amount.Length ? rec.amount[i] : 0f; }
            types[n] = (int)mainscript.fluidenum.water; amounts[n] = litres;
            rec.type = types; rec.amount = amounts;
        }

        /// Every building the map holds an object for, shown or hidden (a fixed building is only hidden when it is far).
        private static IEnumerable<poiScript> LoadedBuildings()
        {
            var map = menuhandler.s != null ? menuhandler.s.currentMainMap : null;
            if (map == null || map.poiGens == null) yield break;
            foreach (var gen in map.poiGens)
            {
                if (gen == null || gen.chunks == null) continue;
                foreach (var chunk in gen.chunks.Values)
                {
                    if (chunk == null || chunk.pois == null) continue;
                    foreach (var poi in chunk.pois.Values)
                        if (poi != null && poi.pobj != null && poi.pobj.started) yield return poi.pobj;
                }
            }
        }

        // ---------------------------------------------------------------- crashed-off parts
        /// Parts a crash knocked off here, and when they go.
        private static readonly Dictionary<tosaveitemscript, float> _doomed = new Dictionary<tosaveitemscript, float>();
        private static readonly List<tosaveitemscript> _due = new List<tosaveitemscript>();

        /// After the game's own fall-off (it ran: this machine simulates the car; a copy's is skipped).
        [HarmonyPatch(typeof(attachablescript), nameof(attachablescript.FallOFf))]
        private static class Doom
        {
            [HarmonyPostfix]
            private static void Postfix(attachablescript __instance, bool __runOriginal)
            {
                if (!__runOriginal || !On || __instance == null || __instance.tosave == null) return;
                if (_doomed.ContainsKey(__instance.tosave)) return;
                _doomed[__instance.tosave] = Time.realtimeSinceStartup + VanishS;
                PartsDoomed++;
            }
        }

        [HarmonyPatch(typeof(partslotscript), nameof(partslotscript.CanCraft), new[] { typeof(attachablescript) })]
        private static class NoRemount
        {
            [HarmonyPrefix]
            private static bool Prefix(attachablescript PP, ref bool __result)
            {
                if (_doomed.Count == 0 || PP == null || PP.tosave == null || !_doomed.ContainsKey(PP.tosave)) return true;
                __result = false;
                MountsRefused++;
                return false;
            }
        }

        private static void VanishTick()
        {
            float now = Time.realtimeSinceStartup;
            _due.Clear();
            foreach (var kv in _doomed) if (kv.Key == null || now >= kv.Value) _due.Add(kv.Key);
            foreach (var it in _due)
            {
                _doomed.Remove(it);
                if (it == null) continue;   // gone already (a scene change, a deletion)
                // for everyone, whoever holds it by now: a player's deletion, and its far-store record goes too
                EntityLifetimeStreaming.NotePlayerDeleted(it);
                EntityLifetimeStreaming.RemoveRecord(it.idInSave);
                it.JustDestroy();
                PartsVanished++;
            }
            _due.Clear();
        }

        // ---------------------------------------------------------------- cleaning
        [HarmonyPatch(typeof(partconditionscript), nameof(partconditionscript.Repair))]
        private static class NoRepair
        {
            [HarmonyPrefix]
            private static bool Prefix(ref bool __result) { if (!On) return true; __result = false; RepairsRefused++; return false; }
        }

        [HarmonyPatch(typeof(washtriggerscript), nameof(washtriggerscript.Wash), new[] { typeof(bool), typeof(int), typeof(float), typeof(drotkefescript) })]
        private static class NoWash
        {
            [HarmonyPrefix]
            private static bool Prefix(bool _weapon, ref bool __result) { if (_weapon || !On) return true; __result = false; WashesRefused++; return false; }
        }

        // ----------------------------------------------------------------
        private static readonly List<tankscript> _deadTanks = new List<tankscript>();
        private static void Prune()
        {
            if (_hidden.Count == 0) return;
            _deadTanks.Clear();
            foreach (var t in _hidden.Keys) if (t == null) _deadTanks.Add(t);
            foreach (var t in _deadTanks) _hidden.Remove(t);
            _deadTanks.Clear();
        }

        /// Bridge: `mp hardcore`.
        public static string Stats() =>
            "{\"on\":" + (On ? "true" : "false") + ",\"vanishS\":" + VanishS.ToString("F1", System.Globalization.CultureInfo.InvariantCulture) +
            ",\"menuClosed\":" + MenuClosed + ",\"spawnsRefused\":" + SpawnsRefused +
            ",\"tanksHidden\":" + _hidden.Count + ",\"tanksDried\":" + TanksDried + ",\"tanksRefilled\":" + TanksRefilled +
            ",\"waterHidden\":" + WaterHidden.ToString("F1", System.Globalization.CultureInfo.InvariantCulture) +
            ",\"partsDoomed\":" + PartsDoomed + ",\"partsWaiting\":" + _doomed.Count + ",\"partsVanished\":" + PartsVanished + ",\"mountsRefused\":" + MountsRefused +
            ",\"repairsRefused\":" + RepairsRefused + ",\"washesRefused\":" + WashesRefused + "}";
    }
}
