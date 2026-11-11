using System;
using System.Collections.Generic;
using System.Reflection.Emit;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using TMPro;
using UnityEngine.UI;

namespace TLDRevamp
{
    /// Settings → Revamp → Keys: every RevaMP key, the game's own F1 developer menu and its other Dev Mode keys
    /// (DevModeKeys, unbound by default), rebound in game. Click a key, then
    /// press the new one: a chord with Ctrl, Shift or Alt held counts (Ctrl+Insert); a modifier pressed and let go alone is
    /// the key itself (Right Shift); Backspace unbinds, Escape cancels. A key used twice says which other action has it.
    /// The values are the BepInEx config's (tldrevamp.core.cfg, [Keys]), saved as soon as they change.
    internal static class KeyBindings
    {
        private sealed class Bind { public string Name, Help; public ConfigEntry<KeyboardShortcut> Entry; }
        private static readonly List<Bind> All = new List<Bind>();
        /// Every bound key by its name in the Keys section (PlayerEdition unbinds the developer ones).
        internal static Dictionary<string, ConfigEntry<KeyboardShortcut>> ByName()
        {
            var d = new Dictionary<string, ConfigEntry<KeyboardShortcut>>();
            foreach (var b in All) d[b.Name] = b.Entry;
            return d;
        }
        /// Every key's entry, in the Keys section's order (Plugin.KeyDown's chord check).
        internal static IEnumerable<ConfigEntry<KeyboardShortcut>> Entries() { foreach (var b in All) yield return b.Entry; }
        private static Bind _capturing;
        private static KeyCode _modifierAlone = KeyCode.None;
        private static int _swallowFrame = -1;
        internal static bool Capturing => _capturing != null;
        /// The frame a key was captured on: no action fires for it (the new key would trigger at once).
        internal static bool Swallowed => _capturing != null || Time.frameCount == _swallowFrame;

        private static readonly KeyCode[] Modifiers = { KeyCode.LeftShift, KeyCode.RightShift, KeyCode.LeftControl, KeyCode.RightControl, KeyCode.LeftAlt, KeyCode.RightAlt };
        private static KeyCode[] _keys;

        /// Plugin, after binding its keys: the Keys section of the Revamp tab.
        private const string HowTo = "Click a key, then press the new one. Hold Ctrl, Shift or Alt for a chord (Ctrl+Insert); a modifier pressed and let go " +
                                     "on its own is the key itself (Right Shift). Backspace unbinds, Escape cancels. A key used twice shows which other action has it. " +
                                     "Saved in BepInEx/config/tldrevamp.core.cfg, [Keys].";
        internal static void Register(params (string Name, ConfigEntry<KeyboardShortcut> Entry)[] binds)
        {
            All.Clear();
            AddSection("Keys", HowTo, binds);
        }

        /// One more section of keys after Register's (Dev Mode keys: the game's own, on a page of their own).
        internal static void AddSection(string header, string help, params (string Name, ConfigEntry<KeyboardShortcut> Entry)[] binds)
        {
            RevampSettings.Entries.Add(new RevampSettings.Entry { Kind = RevampSettings.Kind.Header, Label = header, Help = help });
            foreach (var (name, entry) in binds)
            {
                if (entry == null) continue;
                var b = new Bind { Name = name, Entry = entry, Help = entry.Description?.Description ?? "" };
                All.Add(b);
                RevampSettings.Entries.Add(new RevampSettings.Entry { Kind = RevampSettings.Kind.Button, Label = name, Help = b.Help,
                    Text = () => Show(b), Click = () => { _capturing = _capturing == b ? null : b; _modifierAlone = KeyCode.None; } });
            }
        }

        private static string Show(Bind b)
        {
            if (_capturing == b) return "Press a key…";
            var k = b.Entry.Value;
            string text = k.MainKey == KeyCode.None ? "unbound" : Plugin.KeyName(b.Entry);
            foreach (var o in All)
                if (o != b && k.MainKey != KeyCode.None && o.Entry.Value.MainKey == k.MainKey && SameModifiers(o.Entry.Value, k)) { text += "  (also " + o.Name + ")"; break; }
            return text;
        }
        private static bool SameModifiers(KeyboardShortcut a, KeyboardShortcut b)
        {
            var x = new HashSet<KeyCode>(a.Modifiers); return x.SetEquals(b.Modifiers);
        }

        /// Plugin.Update, before any key action: a key being captured.
        internal static void Tick()
        {
            if (_capturing == null) return;
            if (_keys == null) _keys = (KeyCode[])Enum.GetValues(typeof(KeyCode));
            // a modifier let go with nothing else pressed: that modifier is the key
            if (_modifierAlone != KeyCode.None && Input.GetKeyUp(_modifierAlone)) { Set(new KeyboardShortcut(_modifierAlone)); return; }
            if (!Input.anyKeyDown) return;
            foreach (var k in _keys)
            {
                if (k == KeyCode.None || k >= KeyCode.Mouse0 && k <= KeyCode.Mouse6 || k >= KeyCode.JoystickButton0 || !Input.GetKeyDown(k)) continue;
                if (k == KeyCode.Escape) { _capturing = null; _swallowFrame = Time.frameCount; return; }
                if (k == KeyCode.Backspace) { Set(new KeyboardShortcut(KeyCode.None)); return; }
                if (Array.IndexOf(Modifiers, k) >= 0) { if (_modifierAlone == KeyCode.None) _modifierAlone = k; continue; }
                var held = new List<KeyCode>();
                foreach (var m in Modifiers) if (Input.GetKey(m)) held.Add(m);
                Set(new KeyboardShortcut(k, held.ToArray()));
                return;
            }
        }
        private static void Set(KeyboardShortcut key)
        {
            _capturing.Entry.Value = key;   // BepInEx saves the config file on set
            _capturing = null; _modifierAlone = KeyCode.None; _swallowFrame = Time.frameCount;
            RevampMenu.RefreshOpen();
        }

        // ---------------------------------------------------------------- the game's F1 developer menu
        /// kaposztaleves.Upd opens its developer menu on Input.GetKeyDown(KeyCode.F1): that one check becomes the DevMenu key.
        [HarmonyPatch(typeof(kaposztaleves), nameof(kaposztaleves.Upd))]
        internal static class DevMenuKey
        {
            internal static bool Patched;
            private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> code)
            {
                var getKeyDown = AccessTools.Method(typeof(Input), nameof(Input.GetKeyDown), new[] { typeof(KeyCode) });
                var list = new List<CodeInstruction>(code);
                for (int i = 0; i + 1 < list.Count; i++)
                {
                    if (!list[i].LoadsConstant((int)KeyCode.F1) || !list[i + 1].Calls(getKeyDown)) continue;
                    list[i].opcode = OpCodes.Nop; list[i].operand = null;
                    list[i + 1] = new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(DevMenuKey), nameof(Down))).MoveLabelsFrom(list[i + 1]);
                    Patched = true; break;
                }
                if (!Patched) Plugin.Log.LogWarning("KeyBindings: the F1 check not found in kaposztaleves.Upd; the developer menu stays on F1");
                return list;
            }
            private static bool Down() => Plugin.KeyDevMenu == null ? Input.GetKeyDown(KeyCode.F1) : Plugin.KeyDown(Plugin.KeyDevMenu);
        }

        /// `mp keys`: every key and whether the F1 check was found. `mp keys set <name> <KeyCode|none>`: rebind one (as the
        /// settings row does; saved at once) — the settings restart check uses it.
        internal static string Command(string[] a)
        {
            if (a.Length >= 4 && a[1] == "set")
            {
                string name = string.Join(" ", a, 2, a.Length - 3);
                var bind = All.Find(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
                if (bind == null) return "{\"error\":\"no key named " + name.Replace("\"", "") + "\"}";
                if (!Enum.TryParse(a[a.Length - 1], true, out KeyCode k)) return "{\"error\":\"not a key code\"}";
                bind.Entry.Value = new KeyboardShortcut(k);
                RevampMenu.RefreshOpen();
            }
            var sb = new System.Text.StringBuilder("{\"capturing\":").Append(_capturing != null ? Json.Str(_capturing.Name) : "null").Append(",\"devMenuPatched\":").Append(DevMenuKey.Patched ? "true" : "false").Append(",\"devModeKeys\":").Append(DevModeKeys.Status()).Append(",\"devMenuList\":").Append(Json.Str(DevModeKeys.ListShown ?? "")).Append(",\"keys\":[");
            for (int i = 0; i < All.Count; i++) { if (i > 0) sb.Append(','); sb.Append("{\"name\":").Append(Json.Str(All[i].Name)).Append(",\"key\":").Append(Json.Str(Show(All[i]))).Append('}'); }
            return sb.Append("]}").ToString();
        }
    }
}
