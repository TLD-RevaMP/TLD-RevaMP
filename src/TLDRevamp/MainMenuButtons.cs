using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace TLDRevamp
{
    /// Mods and Revamp on the game's main menu (asked for 2026-11-11): one row of two half-width buttons right under
    /// Settings, cloned from the game's own Settings button (same sprite, font and sound); Exit moves down a row to keep
    /// the column's spacing. Mods opens the mod browser over the menu; Revamp opens the settings on the Revamp tab.
    /// A menu laid out otherwise (no Settings or Exit button where the game has them) is left as it is.
    [HarmonyPatch]
    public static class MainMenuButtons
    {
        public static bool Enabled = true;   // A/B (bridge `set`, next menu): false = the game's menu as it was
        public const string ModsName = "ButtonRevampMods", RevampName = "ButtonRevampTab";
        public const float Gap = 10f, FontSize = 44f;
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
                var exit = settings != null && settings.parent != null ? settings.parent.Find("ButtonExit") as RectTransform : null;
                if (settings == null || exit == null) { Last = "the main menu has no Settings or Exit button: left as it is"; return; }
                var parent = settings.parent;
                if (parent.Find(ModsName) != null) { Last = "built"; return; }

                // the row under Settings, as far below it as Settings is below Load (the game's spacing)
                var load = Find(menu.transform, "ButtonLoad");
                float step = load != null ? Mathf.Abs(WorldY(load) - WorldY(settings)) / Mathf.Max(0.0001f, settings.lossyScale.y) : 130f;
                if (step < 100f || step > 200f) step = 130f;
                float half = (settings.sizeDelta.x - Gap) / 2f;
                var row = settings.anchoredPosition - new Vector2(0f, step);
                Clone(settings, ModsName, "Mods", row, half, () => Net.ModsScreen.Open(null));
                Clone(settings, RevampName, "Revamp", row + new Vector2(half + Gap, 0f), half, () => OpenRevampTab(menu));
                // Exit keeps the gap it had under Settings, one row lower
                exit.anchoredPosition -= new Vector2(0f, step);
                Last = "built: Mods and Revamp under Settings, Exit one row lower";
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

        /// The game's settings, opened as its Settings button does, then on the Revamp tab.
        private static void OpenRevampTab(menuhandler menu)
        {
            menu.PressedSettings();
            var s = settingsscript.s != null ? settingsscript.s : Object.FindObjectOfType<settingsscript>();
            if (s != null) s.PressedTab(RevampMenu.TabIndex);
        }

        private static RectTransform Find(Transform root, string name)
        {
            foreach (var t in root.GetComponentsInChildren<RectTransform>(true)) if (t.name == name) return t;
            return null;
        }

        private static float WorldY(RectTransform t) => t.position.y;
    }
}
