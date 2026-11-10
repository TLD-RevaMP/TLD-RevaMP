using System.Collections.Generic;
using TMPro;
using UnityEngine;

namespace TLDRevamp.WorldGen
{
    /// The new-game screen's layout once RevaMP and mods have added their rows. The game puts the biome dropdown at the
    /// top right with its two check boxes (Peaceful; Hunger, Thirst) indented on the left beside the empty space under
    /// it, and the map dropdown below; rows added later each found their own place (TerrainChoice's terrain copy into
    /// that empty space beside the check boxes, Road end and RevaMP's terrain row 85 px under the map, on top of each
    /// other and of the image-map settings). The user, 2026-11-10: biome and terrain belong below map and road end, as
    /// "Biome:" and "Terrain:", and the check boxes line up with the rows' labels.
    ///
    /// Now, top to bottom: the seed (the game's), the check boxes at the labels' left edge, then one column of rows
    /// 85 px apart: Map, Road end, Biome, Terrain, then any other row a mod added; the image-map settings (shown for a
    /// picture map) below the last row. Rows without a label get one copied from the map row's. Every position is set
    /// from the game's own, kept from the first pass, so it can run again whenever a row is added (checked twice a
    /// second while the menu is up). A layout it does not recognise (no biome or map row) is left as it is.
    public static class NewGameLayout
    {
        public static bool Enabled = true;   // A/B (bridge `set`): false = each row where its own code put it (before)
        public const float RowStep = 85f, ToggleStep = 64f, Gap = 20f, MapSettingsGap = 60f;
        public static string Last = "not run";

        private static Transform _content;
        private static int _signature;
        private static float _nextCheck;
        private static readonly Dictionary<RectTransform, Vector2> _original = new Dictionary<RectTransform, Vector2>();
        /// Labels the game translates (languagetext rewrites them on a language change): the colon the other rows have.
        private static readonly List<TextMeshProUGUI> _colon = new List<TextMeshProUGUI>();

        /// Twice a second while there is no world (the menu): lay the screen out again when its rows changed.
        public static void Tick()
        {
            if (!Enabled || Time.unscaledTime < _nextCheck) return;
            _nextCheck = Time.unscaledTime + 0.5f;
            if (mainscript.s != null && mainscript.s.player != null) return;
            try
            {
                if (_content == null) { _content = FindContent(); _signature = 0; _original.Clear(); _colon.Clear(); }
                if (_content == null || !_content.gameObject.activeInHierarchy) return;
                foreach (var t in _colon) if (t != null && t.text.Length > 0 && !t.text.EndsWith(":")) t.text += ":";
                int sig = Signature(_content);
                if (sig == _signature) return;
                Apply(_content);
                _signature = Signature(_content);
            }
            catch (System.Exception e) { Last = "failed: " + e.Message; }
        }

        private static Transform FindContent()
        {
            foreach (var g in Object.FindObjectsOfType<gamemodesettingsscript>())
                if (g.dropdrops != null && g.dropdrops.Count > 1 && g.dropdrops[1] != null) return g.dropdrops[1].transform.parent;
            return null;
        }

        /// What can change the layout: the rows there and which of them show.
        private static int Signature(Transform content)
        {
            int h = content.childCount;
            for (int i = 0; i < content.childCount; i++)
            {
                var c = content.GetChild(i);
                h = h * 31 + c.name.GetHashCode() * (c.gameObject.activeSelf ? 1 : 7);
                var rt = c as RectTransform;
                if (rt != null) h = h * 17 + Mathf.RoundToInt(rt.anchoredPosition.y);
            }
            return h;
        }

        private static Vector2 Original(RectTransform rt)
        {
            if (!_original.TryGetValue(rt, out var p)) _original[rt] = p = rt.anchoredPosition;
            return p;
        }

        public static void Apply(Transform content)
        {
            RectTransform biome = null, map = null, terrain = null, roadEnd = null, mapSettings = null;
            var toggles = new List<RectTransform>();
            var others = new List<RectTransform>();
            for (int i = 0; i < content.childCount; i++)
            {
                var rt = content.GetChild(i) as RectTransform;
                if (rt == null) continue;
                switch (rt.name)
                {
                    case "DropdownBiome": biome = rt; break;
                    case "DropdownMap": map = rt; break;
                    case "DropdownRoadEnd": roadEnd = rt; break;
                    case "TerrainChoiceRow": case TerrainNewGameUi.DropdownName: terrain = rt; break;
                    case "ImageMapSetting": mapSettings = rt; break;
                    case "TogglePeaceful": case "ToggleSurvival": case "ToggleHomesick": toggles.Add(rt); break;
                    default:
                        // another mod's row in the right-hand column
                        if (rt.GetComponent<TMP_Dropdown>() != null && rt.anchorMin == new Vector2(1f, 1f)) others.Add(rt);
                        break;
                }
            }
            if (biome == null || map == null) { Last = "not the screen we know (no biome or map row): left as it is"; return; }
            var label = MapLabel(map);

            // the check boxes: from where the biome row starts, one under the other, at the map label's left edge
            float y = Original(biome).y;
            float x = label != null ? LeftIn(content, label) : Original(toggles.Count > 0 ? toggles[0] : biome).x;
            foreach (var t in new[] { "TogglePeaceful", "ToggleSurvival", "ToggleHomesick" })
            {
                var rt = toggles.Find(r => r.name == t);
                if (rt == null) continue;
                Original(rt);
                if (!rt.gameObject.activeSelf) { rt.anchoredPosition = new Vector2(x, y); continue; }   // hidden: kept in step, takes no row
                rt.anchoredPosition = new Vector2(x, y);
                y -= ToggleStep;
            }
            y -= (60f - ToggleStep) + Gap;   // the last box is 60 high

            // the rows: map, road end, biome, terrain, then other mods' rows in their own order
            var rows = new List<RectTransform> { map };
            if (roadEnd != null) rows.Add(roadEnd);
            rows.Add(biome);
            if (terrain != null) rows.Add(terrain);
            others.Sort((a, b) => Original(b).y.CompareTo(Original(a).y));
            rows.AddRange(others);
            foreach (var r in rows)
            {
                Original(r);
                r.anchoredPosition = new Vector2(r.anchoredPosition.x, y);
                if (r.gameObject.activeSelf) y -= RowStep;
            }
            if (label != null)
            {
                EnsureLabel(biome, label, "Biome:");
                if (terrain != null) EnsureLabel(terrain, label, "Terrain:");
            }

            // the image-map settings (top-anchored like the rows, their own children placed inside): their topmost child
            // goes MapSettingsGap under the last row
            if (mapSettings != null && mapSettings.childCount > 0)
            {
                var o = Original(mapSettings);
                float top = float.MinValue;
                for (int i = 0; i < mapSettings.childCount; i++)
                    if (mapSettings.GetChild(i) is RectTransform c) top = Mathf.Max(top, c.anchoredPosition.y);
                float lastRowBottom = y + RowStep - 70f;
                mapSettings.anchoredPosition = new Vector2(o.x, o.y + (lastRowBottom - MapSettingsGap) - (o.y + top));
            }
            Last = "laid out: " + toggles.Count + " check box(es), " + rows.Count + " row(s)" + (mapSettings != null ? ", map settings below" : "");
        }

        /// The map row's label (its "Map:" text: a TMP text of its own, not the dropdown's caption or list).
        private static TextMeshProUGUI MapLabel(RectTransform map)
        {
            var dd = map.GetComponent<TMP_Dropdown>();
            foreach (var t in map.GetComponentsInChildren<TextMeshProUGUI>(true))
            {
                if (dd != null && (t == dd.captionText || t == dd.itemText || (dd.template != null && t.transform.IsChildOf(dd.template)))) continue;
                if (t.transform.parent == map) return t;
            }
            return null;
        }

        /// The x a left-anchored row in `content` needs for its left edge to be at the label's text.
        private static float LeftIn(Transform content, TextMeshProUGUI label)
        {
            var corners = new Vector3[4];
            label.rectTransform.GetWorldCorners(corners);
            float left = ((RectTransform)content).InverseTransformPoint(corners[0]).x;
            return left - ((RectTransform)content).rect.xMin;
        }

        /// The row's label laid out like the map row's: the game's biome row has one ("Biome") but 500 wide, so its text
        /// starts outside the panel and is clipped; TerrainChoice's copy of that row carries it too. A label that the
        /// game translates (languagetext) keeps its words; one without gets `text`. A row without a label gets a copy.
        private static void EnsureLabel(RectTransform row, TextMeshProUGUI template, string text)
        {
            var dd = row.GetComponent<TMP_Dropdown>();
            TextMeshProUGUI own = null;
            foreach (var t in row.GetComponentsInChildren<TextMeshProUGUI>(true))
            {
                if (dd != null && (t == dd.captionText || t == dd.itemText || (dd.template != null && t.transform.IsChildOf(dd.template)))) continue;
                if (t.transform.parent == row) { own = t; break; }
            }
            if (own == null)
            {
                var go = Object.Instantiate(template.gameObject, row, false);
                go.name = "RevampRowLabel";
                foreach (var c in go.GetComponentsInChildren<languagetext>(true))
                {
                    if (ls.l != null) { ls.l.texts.Remove(c); ls.l.tmps.Remove(c); }
                    Object.DestroyImmediate(c);
                }
                own = go.GetComponent<TextMeshProUGUI>();
                go.transform.SetAsFirstSibling();
            }
            if (own.GetComponent<languagetext>() == null) own.text = text;
            else { if (!_colon.Contains(own)) _colon.Add(own); if (own.text.Length > 0 && !own.text.EndsWith(":")) own.text += ":"; }
            own.gameObject.SetActive(true);
            var src = template.rectTransform; var dst = own.rectTransform;
            dst.anchorMin = src.anchorMin; dst.anchorMax = src.anchorMax; dst.pivot = src.pivot;
            dst.anchoredPosition = src.anchoredPosition; dst.sizeDelta = src.sizeDelta;
            own.alignment = template.alignment;
        }
    }
}
