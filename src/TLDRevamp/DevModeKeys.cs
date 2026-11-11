using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace TLDRevamp
{
    /// The game's Dev Mode hotkeys (asked for 2026-11-11): every key the game listens for while Dev Mode is on, besides
    /// F1 (KeyBindings.DevMenuKey), is rebindable in Settings > Revamp > Keys and UNBOUND by default, so Dev Mode no
    /// longer turns Caps Lock, X, U, Home, 0 and the F keys into cheats. Each game check `Input.GetKey*(KeyCode.K)` in the
    /// methods below becomes a call that reads the key's [Keys] entry (a chord with Ctrl/Shift/Alt works). The game's
    /// other conditions (Dev Mode on, not typing, looking at a spawned item, …) stay as they are.
    /// Not touched: the fly movement (the game's own move/jump/duck/run keys) and keys outside Dev Mode (the maze's F4,
    /// the free camera's End).
    public static class DevModeKeys
    {
        internal sealed class Key
        {
            public string Name, Cfg, Help;
            public KeyCode[] Game;                       // what the game had (all of them one action: 0 and ` spawn the last item)
            public ConfigEntry<KeyboardShortcut> Entry;
            /// checks replaced in the game's code, per method (0 in all: the game changed, the key does nothing). Per method,
            /// not added up: Harmony runs a method's transpilers again whenever another patch lands on it.
            public readonly Dictionary<string, int> Sites = new Dictionary<string, int>();
            public int SiteCount { get { int n = 0; foreach (var v in Sites.Values) n += v; return n; } }
        }

        internal static readonly Key[] Keys =
        {
            new Key { Name = "Fly on/off", Cfg = "DevFly", Game = new[] { KeyCode.CapsLock },
                Help = "The game's fly (noclip): tap the key. Was Caps Lock." },
            new Key { Name = "Delete spawned item", Cfg = "DevDelete", Game = new[] { KeyCode.X },
                Help = "Deletes the spawned item you look at (hold it to keep deleting). Was X." },
            new Key { Name = "Bury spawned item", Cfg = "DevBury", Game = new[] { KeyCode.U },
                Help = "Buries the spawned item you look at. Was U." },
            new Key { Name = "Spawn the last item again", Cfg = "DevLastSpawn", Game = new[] { KeyCode.Alpha0, KeyCode.BackQuote },
                Help = "Spawns the developer menu's last item again (on foot). Was 0 or `." },
            new Key { Name = "Save the game", Cfg = "DevSave", Game = new[] { KeyCode.Home },
                Help = "Saves the game at once. Was Home (also RevaMP's Item health key)." },
            new Key { Name = "Revive / respawn", Cfg = "DevRevive", Game = new[] { KeyCode.F9 },
                Help = "Brings you back after dying (respawns you in multiplayer). Was F9." },
            new Key { Name = "Time backward (hold)", Cfg = "DevTimeBack", Game = new[] { KeyCode.F2 },
                Help = "Turns the time of day back while held. Was F2." },
            new Key { Name = "Time forward (hold)", Cfg = "DevTimeForward", Game = new[] { KeyCode.F3 },
                Help = "Turns the time of day forward while held. Was F3." },
            new Key { Name = "Next radio station", Cfg = "DevRadio", Game = new[] { KeyCode.F10 },
                Help = "Moves every radio to its next song. Was F10." },
            new Key { Name = "Spawn a test ragdoll", Cfg = "DevRagdoll", Game = new[] { KeyCode.F8 },
                Help = "Spawns a test ragdoll in front of you. Was F8." },
            new Key { Name = "Depth screenshot", Cfg = "DevDepthShot", Game = new[] { KeyCode.End },
                Help = "Takes the game's depth screenshot. Was End." },
            new Key { Name = "Debug item spawner", Cfg = "DevDebugSpawn", Game = new[] { KeyCode.F4 },
                Help = "Fires the game's debug item spawners, where a world has them. Was F4." },
        };

        /// Plugin.Awake: the [Keys] entries, unbound by default. Returns the Keys rows (name, entry).
        internal static (string, ConfigEntry<KeyboardShortcut>)[] Bind(ConfigFile config)
        {
            var rows = new (string, ConfigEntry<KeyboardShortcut>)[Keys.Length];
            for (int i = 0; i < Keys.Length; i++)
            {
                var k = Keys[i];
                k.Entry = config.Bind("Keys", k.Cfg, new KeyboardShortcut(KeyCode.None),
                    "The game's Dev Mode (on in the game's settings): " + k.Help + " Unbound by default.");
                rows[i] = (k.Name, k.Entry);
            }
            return rows;
        }

        // what the patched code calls instead of Input.GetKeyDown/GetKey/GetKeyUp(KeyCode): the slot is the Keys index
        public static bool Down(int slot) => Plugin.KeyDown(Keys[slot].Entry);
        public static bool Held(int slot) => Active(slot, out var s) && Input.GetKey(s.MainKey);
        public static bool Up(int slot) => Active(slot, out var s) && Input.GetKeyUp(s.MainKey);
        private static bool Active(int slot, out KeyboardShortcut s)
        {
            s = Keys[slot].Entry == null ? KeyboardShortcut.Empty : Keys[slot].Entry.Value;
            if (s.MainKey == KeyCode.None || Net.Chat.Typing || MarkPanel.Opened || KeyBindings.Swallowed) return false;
            foreach (var m in s.Modifiers)
                if (!Input.GetKey(m) && !Input.GetKey(Other(m))) return false;   // either Ctrl/Shift/Alt counts, as Plugin.KeyDown
            return true;
        }
        private static KeyCode Other(KeyCode m)
        {
            switch (m)
            {
                case KeyCode.LeftControl: return KeyCode.RightControl; case KeyCode.RightControl: return KeyCode.LeftControl;
                case KeyCode.LeftShift: return KeyCode.RightShift; case KeyCode.RightShift: return KeyCode.LeftShift;
                case KeyCode.LeftAlt: return KeyCode.RightAlt; case KeyCode.RightAlt: return KeyCode.LeftAlt;
                default: return m;
            }
        }

        /// Replaces each `ldc.i4 K; call Input.GetKey*(KeyCode)` whose K is one of `keys` in a game method.
        private static IEnumerable<CodeInstruction> Swap(IEnumerable<CodeInstruction> code, string where, params KeyCode[] keys)
        {
            var calls = new Dictionary<MethodInfo, string>
            {
                { AccessTools.Method(typeof(Input), nameof(Input.GetKeyDown), new[] { typeof(KeyCode) }), nameof(Down) },
                { AccessTools.Method(typeof(Input), nameof(Input.GetKey), new[] { typeof(KeyCode) }), nameof(Held) },
                { AccessTools.Method(typeof(Input), nameof(Input.GetKeyUp), new[] { typeof(KeyCode) }), nameof(Up) },
            };
            var list = new List<CodeInstruction>(code);
            var found = new HashSet<KeyCode>();
            foreach (var k in Keys) k.Sites.Remove(where);
            for (int i = 0; i + 1 < list.Count; i++)
            {
                if (!(list[i + 1].operand is MethodInfo called) || !calls.TryGetValue(called, out var with)) continue;
                foreach (var key in keys)
                {
                    if (!list[i].LoadsConstant((int)key)) continue;
                    int slot = System.Array.FindIndex(Keys, k => System.Array.IndexOf(k.Game, key) >= 0);
                    list[i] = new CodeInstruction(OpCodes.Ldc_I4, slot).MoveLabelsFrom(list[i]);
                    list[i + 1] = new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(DevModeKeys), with)).MoveLabelsFrom(list[i + 1]);
                    Keys[slot].Sites.TryGetValue(where, out int n); Keys[slot].Sites[where] = n + 1; found.Add(key);
                    break;
                }
            }
            foreach (var key in keys)
                if (!found.Contains(key)) Plugin.Log.LogWarning("DevModeKeys: no " + key + " check in " + where + "; that Dev Mode key stays as the game has it");
            return list;
        }

        [HarmonyPatch(typeof(kaposztaleves), nameof(kaposztaleves.Upd))]
        internal static class MenuUpd
        {
            private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> code) =>
                Swap(code, "kaposztaleves.Upd", KeyCode.CapsLock, KeyCode.End, KeyCode.X, KeyCode.F8, KeyCode.Alpha0, KeyCode.BackQuote);
        }

        [HarmonyPatch(typeof(kaposztaleves), nameof(kaposztaleves.ForceAutoSave))]
        internal static class Save
        {
            private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> code) => Swap(code, "kaposztaleves.ForceAutoSave", KeyCode.Home);
        }

        [HarmonyPatch(typeof(fpscontroller), "Update")]
        internal static class Revive
        {
            private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> code) => Swap(code, "fpscontroller.Update", KeyCode.F9);
        }

        [HarmonyPatch(typeof(fpscontroller), nameof(fpscontroller.RayStuff))]
        internal static class Aimed
        {
            private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> code) => Swap(code, "fpscontroller.RayStuff", KeyCode.X, KeyCode.U);
        }

        [HarmonyPatch(typeof(mainscript), nameof(mainscript.RADIO))]
        internal static class Radio
        {
            private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> code) => Swap(code, "mainscript.RADIO", KeyCode.F10);
        }

        [HarmonyPatch(typeof(napszakvaltakozas), "FixedUpdate")]
        internal static class TimeOfDay
        {
            private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> code) => Swap(code, "napszakvaltakozas.FixedUpdate", KeyCode.F2, KeyCode.F3);
        }

        [HarmonyPatch(typeof(newRandomStuffSpawnScript), "Update")]
        internal static class DebugSpawn
        {
            private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> code) => Swap(code, "newRandomStuffSpawnScript.Update", KeyCode.F4);
        }

        // ---------------------------------------------------------------- the developer menu's key list
        /// The developer menu lists its keys in a text of its own ("[Caps]: Fly Toggle", "[0]: Spawn", …), written into the
        /// game. While the menu is open each "[key]" in it shows the key set in Settings > Revamp > Dev Mode keys (or
        /// "unbound"); a line naming a key RevaMP doesn't manage (the fly movement) stays as the game wrote it.
        internal static string ListOriginal, ListShown;
        private static TMPro.TMP_Text _list;
        private static float _listAt;

        /// The game's own labels for its keys (what it writes in the brackets), to the key that is now set.
        internal static ConfigEntry<KeyboardShortcut> ForLabel(string label)
        {
            switch (label.Trim().ToLowerInvariant())
            {
                case "f1": return Plugin.KeyDevMenu;
                case "caps": case "capslock": case "caps lock": return Keys[0].Entry;
                case "x": return Keys[1].Entry;
                case "u": return Keys[2].Entry;
                case "0": case "`": case "alpha0": case "backquote": return Keys[3].Entry;
                case "home": return Keys[4].Entry;
                case "f9": return Keys[5].Entry;
                case "f2": return Keys[6].Entry;
                case "f3": return Keys[7].Entry;
                case "f10": return Keys[8].Entry;
                case "f8": return Keys[9].Entry;
                case "end": return Keys[10].Entry;
                case "f4": return Keys[11].Entry;
                default: return null;
            }
        }

        /// The game's list with each "[key]" replaced by the key set now: `name` gives a label's key name, or null for a
        /// label it doesn't know (left as the game wrote it). Pure, for tests.
        internal static string Rewrite(string original, System.Func<string, string> name)
        {
            return System.Text.RegularExpressions.Regex.Replace(original, @"\[([^\]\r\n]{1,24})\]", m =>
            {
                // "[0/`]", "[F2/F3]": every part known; parts naming one key collapse to it
                var parts = m.Groups[1].Value.Split('/', ',');
                var names = new List<string>();
                foreach (var p in parts)
                {
                    string n = name(p);
                    if (n == null) return m.Value;   // a key nobody manages: as the game wrote it
                    if (!names.Contains(n)) names.Add(n);
                }
                return "[" + string.Join("/", names.ToArray()) + "]";
            });
        }

        /// A list label's key now: a RevaMP-managed key's binding, or (T, "target") the game's own Controls binding.
        private static string Name(string label)
        {
            var e = ForLabel(label);
            if (e != null) return e.Value.MainKey == KeyCode.None ? "unbound" : Plugin.KeyName(e);
            if (label.Trim().ToLowerInvariant() == "t") return GameKey(inputscript.IN.target);
            return null;
        }
        private static string GameKey(inputscript.IN input)
        {
            try
            {
                var keys = settingsscript.s.S.inputs[(int)input].keys;
                foreach (var k in keys) if (k != null && !k.axis && k.key != KeyCode.None) return k.key.ToString();
                return "unbound";
            }
            catch (System.Exception) { return null; }
        }

        [HarmonyPatch(typeof(kaposztaleves), nameof(kaposztaleves.Upd))]
        internal static class KeyList
        {
            private static void Postfix(kaposztaleves __instance)
            {
                try
                {
                    if (!__instance.isOpen || __instance.controlWindow == null) { _listAt = 0f; return; }
                    if (UnityEngine.Time.unscaledTime < _listAt) return;
                    _listAt = UnityEngine.Time.unscaledTime + 0.5f;   // a key changed in Settings shows within half a second
                    if (_list == null) _list = __instance.controlWindow.GetComponentInChildren<TMPro.TMP_Text>(true);
                    if (_list == null) return;
                    if (ListOriginal == null) ListOriginal = _list.text;
                    string shown = Rewrite(ListOriginal, Name);
                    if (shown != _list.text) _list.text = shown;
                    ListShown = shown;
                }
                catch (System.Exception e) { _listAt = float.MaxValue; Plugin.Log.LogWarning("DevModeKeys: the developer menu's key list: " + e.Message); }
            }
        }

        /// `mp keys`: how many game checks each key replaced.
        internal static string Status()
        {
            var sb = new System.Text.StringBuilder("[");
            for (int i = 0; i < Keys.Length; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append("{\"cfg\":").Append(Json.Str(Keys[i].Cfg)).Append(",\"sites\":").Append(Keys[i].SiteCount).Append('}');
            }
            return sb.Append(']').ToString();
        }
    }
}
