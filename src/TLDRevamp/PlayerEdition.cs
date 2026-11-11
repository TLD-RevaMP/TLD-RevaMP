using System.Collections.Generic;
using BepInEx.Configuration;
using UnityEngine;

namespace TLDRevamp
{
    /// The player edition: the same code, built with -p:RevampEdition=player (tools/package.sh, previewrelease.py), set
    /// up for players instead of testers and developers (asked for 2026-11-11). It hides and switches off what only
    /// developers use and keeps everything players do (multiplayer, the mod browser, the fixes, VR, filmmaking):
    ///  - Settings > Revamp: no Debugging and no Branding section; the WIP mark off;
    ///  - Keys: the developer keys hidden and ignored (debug report, item health, debug layout, item traffic, the legacy
    ///    debug views, object comparison and events), their bindings left as they are in the cfg so a dev build later
    ///    still has them; the game's own F1 developer menu (players have it with Dev Mode on), the bug report, marks,
    ///    multiplayer screen, performance overlay (an FPS counter players use), cinematic camera and replays stay;
    ///  - every debug overlay off (the performance overlay until its key is pressed);
    ///  - no debug bridge, and no background health, item and part watches with their automatic recordings (a bug
    ///    report by hand works as before).
    /// A dev or test build (no RevampEdition) is unchanged.
    public static class PlayerEdition
    {
        public static bool On => Plugin.IsPlayerEdition;

        /// Settings > Revamp sections a player edition leaves out.
        internal static readonly HashSet<string> HiddenSections = new HashSet<string> { "Debugging", "Branding" };

        /// Keys (KeyBindings names, Plugin.Awake) that are only for developers and testers.
        internal static readonly HashSet<string> DeveloperKeys = new HashSet<string>
        {
            "Debug report", "Item health", "Debug layout (V2)", "Item traffic",
            "Debug view (Legacy)", "Ownership labels (Legacy)", "Owner ghost (Legacy)", "Links (Legacy)",
            "Object comparison", "Object events",
        };

        /// The developer keys' entries: Plugin.KeyDown never fires them (filled by Apply).
        internal static readonly HashSet<ConfigEntry<KeyboardShortcut>> Blocked = new HashSet<ConfigEntry<KeyboardShortcut>>();

        /// Debugging settings switched off (Settings keys; Choice settings take their first option, "Off").
        internal static readonly string[] DebugSettingsOff =
        {
            "debugV2", "debugView", "overlay", "netPackets", "itemTraffic", "ownershipOverlay", "ownerGhost", "ownerGhostAll",
            "linkView", "objectComparison", "objectEvents",
        };

        /// Plugin.Awake, after the settings and keys are loaded.
        internal static void Apply(IDictionary<string, ConfigEntry<KeyboardShortcut>> keys)
        {
            if (!On) return;
            Net.Entities.HealthWatch = false; Net.Entities.AutoMarks = false;
            Net.Entities.ItemWatch = false; Net.Entities.PartWatch = false; Net.Entities.PartWatchMarks = false;
            Plugin.SetWipMark(false);
            var entries = RevampSettings.Entries;
            foreach (var e in entries)
                if (e.Key != null && System.Array.IndexOf(DebugSettingsOff, e.Key) >= 0 && e.Set != null && e.Get != null && e.Get() != 0) e.Set(0);
            // ignored, not unbound: setting the value would save "None" to the cfg and a dev build would lose the key
            foreach (var kv in keys)
                if (DeveloperKeys.Contains(kv.Key) && kv.Value != null) Blocked.Add(kv.Value);
            // the rows: hidden sections whole, developer keys one by one
            var keep = new List<RevampSettings.Entry>();
            bool hiding = false;
            foreach (var e in entries)
            {
                if (e.Kind == RevampSettings.Kind.Header) hiding = HiddenSections.Contains(e.Label);
                if (hiding) continue;
                if (e.Kind == RevampSettings.Kind.Button && DeveloperKeys.Contains(e.Label)) continue;
                keep.Add(e);
            }
            entries.Clear(); entries.AddRange(keep);
            Plugin.Log.LogInfo("Player edition: developer settings, keys, overlays, bridge and background watches off");
        }
    }
}
