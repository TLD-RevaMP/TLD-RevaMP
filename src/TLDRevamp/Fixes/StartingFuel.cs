using System;
using System.Collections.Generic;
using HarmonyLib;

namespace TLDRevamp.Fixes
{
    /// Settings → Revamp → World → Starting fuel (on: as the game). Off: a new world's start house comes without its petrol
    /// and diesel barrels (asked 2026-11-27). Only when the world is made: the start house's fixed contents are made once
    /// per world (poiScript.FixedSpawn, guarded by its building record's spawnedFixed), on the host or in single player,
    /// and a joiner gets the world from the host; a saved world keeps what it has. The start house is the building whose
    /// fixed contents pick the start car (fixedSpawnScript.useStartCar). The car's own tank and anything not a barrel stay.
    [HarmonyPatch(typeof(fixedSpawnScript), nameof(fixedSpawnScript.FStart))]
    public static class StartingFuel
    {
        public static bool On = true;
        public static long Removed;
        /// The last start house's items with fuel in them, and what became of them (bridge: `get TLDRevamp.Fixes.StartingFuel.Last`).
        public static string Last = "";

        [HarmonyPostfix]
        private static void Postfix(fixedSpawnScript __instance)
        {
            if (__instance == null || !__instance.useStartCar) return;
            var seen = new List<string>();
            foreach (var it in __instance.GetComponentsInChildren<tosaveitemscript>(true))
            {
                if (it == null || it.tanks == null || it.GetComponentInParent<carscript>(true) != null) continue;
                string fuel = Fuel(it);
                if (fuel == null) continue;
                bool barrel = it.name.IndexOf("barrel", StringComparison.OrdinalIgnoreCase) >= 0;
                bool remove = !On && barrel;
                seen.Add(it.name + " (" + fuel + ")" + (remove ? " removed" : ""));
                if (!remove) continue;
                it.JustDestroy();
                Removed++;
            }
            Last = string.Join("; ", seen);
            Plugin.Log.LogInfo("starting fuel " + (On ? "on" : "off") + ": " + (seen.Count == 0 ? "no fuel in the start house" : Last));
        }

        /// The petrol and diesel in the item's tanks ("gas 60 L"), or null when it holds neither.
        private static string Fuel(tosaveitemscript it)
        {
            var parts = new List<string>();
            foreach (var t in it.tanks)
            {
                if (t == null) continue;
                t.FStart();
                if (t.F == null || t.F.fluids == null) continue;
                foreach (var f in t.F.fluids)
                    if (f != null && f.amount > 0f && (f.type == mainscript.fluidenum.gas || f.type == mainscript.fluidenum.diesel))
                        parts.Add(f.type + " " + f.amount.ToString("F0", System.Globalization.CultureInfo.InvariantCulture) + " L");
            }
            return parts.Count == 0 ? null : string.Join(", ", parts);
        }
    }
}
