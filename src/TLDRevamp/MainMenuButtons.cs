using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace TLDRevamp
{
    /// Mods on the game's main menu (asked for 2026-11-11): a button against Settings' right edge, the way the gear
    /// sits against START, cloned from the game's own Settings button (same sprite, font and sound). It opens the mod
    /// browser over the menu. Nothing else moves: a row under Settings pushed Exit below the bottom of a 16:9 screen.
    /// (The Revamp settings are in Settings, so there is no Revamp button here.) A menu without a Settings button is
    /// left as it is.
    [HarmonyPatch]
    public static class MainMenuButtons
    {
        public static bool Enabled = true;   // A/B (bridge `set`, next menu): false = the game's menu as it was
        public const string ModsName = "ButtonRevampMods";
        public const float Width = 160f, FontSize = 44f;
        public static string Last = "not built";

        [HarmonyPatch(typeof(menuhandler), "Start")]
        [HarmonyPostfix]
        private static void AfterStart(menuhandler __instance) => TryBuild(__instance);

        [HarmonyPatch(typeof(menuhandler), nameof(menuhandler.LoadMainMenu))]
        [HarmonyPostfix]
        private static void AfterLoadMainMenu(menuhandler __instance) => TryBuild(__instance);

        public static void TryBuild(menuhandler menu)
        {
            if (!Enabled || menu == null) return;
            try
            {
                var settings = Find(menu.transform, "ButtonSettings");
                if (settings == null || settings.parent == null) { Last = "the main menu has no Settings button: left as it is"; return; }
                Tighten(menu.transform, settings);
                // against Settings' right edge, its height: as the gear against START
                var at = settings.anchoredPosition + new Vector2(settings.sizeDelta.x * (1f - settings.pivot.x), 0f);
                if (settings.parent.Find(ModsName) is RectTransform built) { built.anchoredPosition = at; Last = "built"; return; }
                Clone(settings, ModsName, "Mods", at, Width, () => Net.ModsScreen.Open(null));
                Last = "built: Mods next to Settings";
                Plugin.Log.LogInfo("MainMenuButtons: " + Last);
            }
            catch (System.Exception e) { Last = "failed: " + e.Message; Plugin.Log.LogWarning("MainMenuButtons: " + e.Message); }
        }

        private static void Clone(RectTransform template, string name, string text, Vector2 pos, float width, System.Action click)
        {
            var go = Object.Instantiate(template.gameObject, template.parent, false);
            go.name = name;
            foreach (var c in go.GetComponentsInChildren<languagetext>(true))
            {
                if (ls.l != null) { ls.l.texts.Remove(c); ls.l.tmps.Remove(c); }
                Object.DestroyImmediate(c);
            }
            var rt = (RectTransform)go.transform;
            rt.anchoredPosition = pos;
            rt.sizeDelta = new Vector2(width, template.sizeDelta.y);
            // one size for both (auto-sizing made "Revamp" smaller than "Mods"): what the longer word fits in half a button
            foreach (var t in go.GetComponentsInChildren<TextMeshProUGUI>(true)) { t.text = text; t.enableAutoSizing = false; t.fontSize = FontSize; }
            foreach (var t in go.GetComponentsInChildren<Text>(true)) t.text = text;
            // the game sizes a button's click area to its rect (colliderResizeToRect); the clone's box follows its new size
            var box = go.GetComponent<BoxCollider>();
            if (box != null) { box.size = new Vector3(width, template.sizeDelta.y, box.size.z); box.center = new Vector3(width * (0.5f - rt.pivot.x), template.sizeDelta.y * (0.5f - rt.pivot.y), box.center.z); }
            var b = go.GetComponent<Button>();
            b.onClick = new Button.ButtonClickedEvent();   // the Settings button's own listener goes with the clone
            b.onClick.AddListener(() => { try { click(); } catch (System.Exception e) { Plugin.Log.LogWarning("MainMenuButtons: " + e.Message); } });
        }

        /// The menu's column one even step apart (asked for 2026-11-11: "spaced out too far"; the game had 130 to 150
        /// between buttons 90 high). The column keeps its middle, so it stays where it was on the screen, and running
        /// it again changes nothing. The buttons sit under two parents, so this works in world space; the column is the
        /// buttons with Settings' left edge (the save screen has a Load button of its own elsewhere).
        public static float Step = 115f;
        public static readonly string[] Column = { "ButtonContinue", "ButtonMultiplayer", "ButtonStart", "ButtonLoad", "ButtonSettings", "ButtonExit" };
        internal static void Tighten(Transform root, RectTransform settings)
        {
            if (Step <= 0f) return;
            var found = new System.Collections.Generic.List<RectTransform>();
            float x = settings.position.x, scale = settings.lossyScale.y;
            foreach (var name in Column)
                foreach (var t in root.GetComponentsInChildren<RectTransform>(true))
                    if (t.name == name && Mathf.Abs(t.position.x - x) < 2f * Mathf.Max(scale, 0.0001f)) { found.Add(t); break; }
            if (found.Count < 3) return;
            float top = float.MinValue, bottom = float.MaxValue;
            foreach (var t in found) { top = Mathf.Max(top, Bottom(t) + t.rect.height * scale); bottom = Mathf.Min(bottom, Bottom(t)); }
            float h = settings.rect.height * scale, step = Step * scale;
            float span = (found.Count - 1) * step + h, first = (top + bottom) * 0.5f + span * 0.5f - h;   // the top button's bottom edge
            for (int i = 0; i < found.Count; i++)
            {
                var t = found[i];
                t.position += new Vector3(0f, first - i * step - Bottom(t), 0f);
            }
        }
        private static float Bottom(RectTransform t)
        {
            var c = new Vector3[4]; t.GetWorldCorners(c); return c[0].y;
        }

        private static RectTransform Find(Transform root, string name)
        {
            foreach (var t in root.GetComponentsInChildren<RectTransform>(true)) if (t.name == name) return t;
            return null;
        }

    }
}
