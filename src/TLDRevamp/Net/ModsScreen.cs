using System;
using System.Collections.Generic;
using System.IO;
using HarmonyLib;
using TLDRevamp.Multiplayer;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace TLDRevamp.Net
{
    /// The Mods screens: the list for browsing (ModsScreen.List.cs), and the required-mods screen (ModsScreen.Needed.cs)
    /// on the game's Save/Load tapes, paper card, buttons and sounds, reused like MpScreen does.
    [HarmonyPatch]
    public static partial class ModsScreen
    {
        private static GameObject _root, _settings;
        private static menuhandler _builtFor;
        private static TextMeshProUGUI _title, _count, _name, _version, _message;
        private static readonly List<TextMeshProUGUI> Lines = new List<TextMeshProUGUI>();
        /// A tape on the required-mods screen: the mod's name (bold) and version in its strip, the host's requirement over
        /// the picture, and Install / Enable buttons.
        private sealed class Tile { internal GameObject Go; internal TextMeshProUGUI Name, Version; internal Button Action, Enable; internal GameObject Required; internal TextMeshProUGUI RequiredText; }
        private static readonly List<Tile> Tiles = new List<Tile>();
        private static Button _source, _refresh, _up, _down;
        private static int _page, _selected;
        private static float _refreshAt;
        private static Action _yes, _no;            // what the list's question's Yes and No do (remove a mod; update or not)
        private static bool _checked, _offer;       // the start-up update check: started, and its answer still to show
        public static bool IsOpen => _root != null && _root.activeSelf || ListOpen;
        internal static bool IsVisible => _root != null && _root.activeInHierarchy || _list != null && _list.activeInHierarchy;

        /// settings: the Revamp settings page to hide while open, or null over the main menu (the game shows it again).
        /// Browsing mods opens the list (ModsScreen.List.cs); the tapes are kept for the required-mods screen.
        internal static void Open(GameObject settings, bool fetch = true)
        {
            try
            {
                if (menuhandler.s == null || menuhandler.s.SaveLoadObject == null) return;
                _needed = null;
                MpScreen.Close();
                OpenList(settings, fetch);
            }
            catch (Exception e) { Plugin.Log.LogError("ModsScreen: " + e); Close(); }
        }

        /// The tapes: the required-mods screen (OpenNeeded sets _needed and refreshes). The tapes once browsed the mods
        /// too; the list does that now, and their browse code went (it ran once here before OpenNeeded set _needed: a
        /// wasted pass over every mod's files, and an error in it fell back to the bare refusal).
        private static void OpenTapes()
        {
            try
            {
                if (menuhandler.s == null || menuhandler.s.SaveLoadObject == null) return;
                MpScreen.Close();
                if (_list != null) _list.SetActive(false);
                if (_root == null || _builtFor != menuhandler.s) Build(menuhandler.s);
                _settings = null;
                _root.transform.SetAsLastSibling(); _root.SetActive(true);
                _page = 0; _selected = 0;
            }
            catch (Exception e) { Plugin.Log.LogError("ModsScreen: " + e); Close(); }
        }

        /// The start-up check (like MSCLoader's): once the main menu shows, fetch the catalog in the background; if a mod
        /// installed from the browser has a newer catalog version, open this screen and ask to update. Mods installed by
        /// hand are named, to update by hand. Once per game run; the test machines (bridge on) only check when told to.
        private static void StartupCheck(menuhandler mh)
        {
            if (!_checked)
            {
                _checked = true;
                if (!ModCatalog.CheckAtStart || DevHooks.Testing || DedicatedServer.Enabled || ModCatalog.Busy) return;
                ModCatalog.Refresh(true); _offer = ModCatalog.Busy;
                return;
            }
            if (!_offer || ModCatalog.Busy || IsOpen || MpScreen.IsOpen || mh.Menu == null || !mh.Menu.activeSelf) return;
            _offer = false;
            if (ModCatalog.Current == null) return;
            var byHand = new List<string>();
            var updates = Updates(byHand);
            if (updates.Count == 0 && byHand.Count == 0) return;
            Plugin.Log.LogInfo("Mod update check: " + updates.Count + " to update, " + byHand.Count + " installed by hand");
            Open(null, false);
            var names = new List<string>();
            foreach (var u in updates) names.Add(u.Key.Name + " " + (Installed(u.Key, out _) ?? "") + " to " + u.Value.Version);
            string hand = byHand.Count == 0 ? "" : " Installed by hand, update yourself: " + string.Join(", ", byHand) + ".";
            if (updates.Count > 0)
                Ask("Mod updates: " + string.Join(", ", names) + "." + hand + " Update now?", () => { ModCatalog.UpdateAll(updates); Refresh(); }, Close);
            else
                Ask("Newer mod versions." + hand + " Open the mod browser?", Refresh, Close);
        }

        /// For the bridge (mods check): run the start-up check again now, as at start, even with the bridge on.
        internal static void CheckNow()
        {
            _checked = true;
            ModCatalog.Refresh(true); _offer = ModCatalog.Busy;
        }
        internal static void Answer(bool yes)
        {
            if (Question == null) return;
            _dialog.SetActive(false);
            (yes ? _yes : _no)?.Invoke();
        }
        internal static string Question => ListOpen && _dialog != null && _dialog.activeSelf ? _dialogText.text : null;

        /// Every catalog mod with a newer catalog version than the installed one; byHand gets the ones installed by hand.
        internal static List<KeyValuePair<ModCatalog.Mod, ModCatalog.Release>> Updates(List<string> byHand)
        {
            var list = new List<KeyValuePair<ModCatalog.Mod, ModCatalog.Release>>();
            if (ModCatalog.Current == null) return list;
            foreach (var m in ModCatalog.Current.Mods)
            {
                try
                {
                    if (ModActivation.Core(m.Id)) continue;
                    string installed = Installed(m, out bool manual);
                    var v = ModCatalog.UpdateTo(m, installed, ModInstall.MaxBytes, MpApi.ValidId, Plugin.BaseVersion);
                    if (v == null) continue;
                    if (manual) byHand.Add(m.Name + " " + v.Version);
                    else list.Add(new KeyValuePair<ModCatalog.Mod, ModCatalog.Release>(m, v));
                }
                catch (Exception e) { Plugin.Log.LogWarning("Mod update check: " + m.Id + ": " + e.Message); }
            }
            return list;
        }

        /// A question on the list (StartupCheck opens it first; nothing is asked if it could not open).
        private static void Ask(string question, Action yes, Action no = null)
        {
            if (ListOpen) AskList(question, yes, no);
        }

        public static void Close()
        {
            _needed = null; _installQueue.Clear();
            ModCatalog.Cancel();
            if (_root != null) _root.SetActive(false);
            if (_list != null) _list.SetActive(false);
            if (_settings != null) _settings.SetActive(true);
            _settings = null;
        }

        [HarmonyPatch(typeof(menuhandler), "Update")]
        [HarmonyPostfix]
        private static void MenuUpdate(menuhandler __instance)
        {
            if (__instance == menuhandler.s && DataFromMenuScript.s != null && DataFromMenuScript.s.mainmenu && Time.timeSinceLevelLoad > 1f)
                StartupCheck(__instance);
            if (!IsOpen || __instance != (ListOpen ? _listFor : _builtFor)) return;
            if (DataFromMenuScript.s != null && !DataFromMenuScript.s.mainmenu && mainscript.s != null && !mainscript.s.pauseMenuOpen)
            { Close(); return; }
            if (__instance.Menu != null) __instance.Menu.SetActive(false);
            if (Time.unscaledTime >= _refreshAt) Refresh();
        }

        private static TextMeshProUGUI Text(Transform p, string n) => p.Find(n).GetComponent<TextMeshProUGUI>();
        private static string Esc(string s) => (s ?? "").Replace("<", "‹").Replace(">", "›");
        private static void Label(Button b, string s) => b.GetComponentInChildren<TextMeshProUGUI>(true).text = s;
        private static Button Button(Transform p, string n, Action action, bool icon = false)
        {
            var b = p.Find(n).GetComponent<Button>();
            b.onClick.AddListener(() => action());
            var image = b.transform.Find("Image");
            if (!icon && image != null) image.gameObject.SetActive(false);
            return b;
        }

        private static void Build(menuhandler mh)
        {
            if (_root != null) UnityEngine.Object.Destroy(_root);
            _builtFor = mh; Tiles.Clear(); Lines.Clear();
            var src = mh.SaveLoadObject;
            var save = src.GetComponent<newSaveScreenScript>();
            _root = UnityEngine.Object.Instantiate(src, src.transform.parent);
            _root.name = "TLDRevampMods"; _root.SetActive(false);
            var script = _root.GetComponent<newSaveScreenScript>();
            if (script != null) UnityEngine.Object.DestroyImmediate(script);
            foreach (var c in _root.GetComponentsInChildren<languagetext>(true)) UnityEngine.Object.DestroyImmediate(c);
            foreach (var b in _root.GetComponentsInChildren<Button>(true)) b.onClick = new Button.ButtonClickedEvent();
            var t = _root.transform;
            foreach (var n in new[] { "TextLoad", "TextSaved", "TextSaveOverwrite", "TextLoadLast", "TextLoad (1)", "TMP_IF_ScreenShotPath",
                "TMP_IF_SaveName", "ButtonFastUp", "ButtonFastDown", "ButtonJumpUp", "ButtonJumpDown", "ButtonSetActive", "ButtonSave", "ButtonDelete", "TextDelete" })
            { var o = t.Find(n); if (o != null) o.gameObject.SetActive(false); }
            _title = Text(t, "PageText"); _count = Text(t, "SaveCountText");
            var card = t.Find("InfoBackGround");
            _name = Text(card, "Text (1)"); _version = Text(card, "TextMapname");
            _name.enableAutoSizing = true; _name.fontSizeMax = 50; _name.fontSizeMin = 20;
            _name.overflowMode = TextOverflowModes.Ellipsis;
            var info = card.Find("InfoObj"); info.gameObject.SetActive(true);
            foreach (var n in new[] { "TMP_IF_Name", "TMP_IF_Date", "TMP_IF_Seed", "TMP_IF_Driven", "TMP_IF_Version" })
            {
                var f = info.Find(n).GetComponent<TMP_InputField>(); f.enabled = false;
                if (f.placeholder != null) f.placeholder.gameObject.SetActive(false);
                var txt = (TextMeshProUGUI)f.textComponent;
                txt.enableAutoSizing = true; txt.fontSizeMax = 34; txt.fontSizeMin = 16;
                txt.richText = false; txt.overflowMode = TextOverflowModes.Ellipsis; Lines.Add(txt);
            }
            _message = Text(t, "TextCantSave");
            var mr = (RectTransform)_message.transform; mr.anchoredPosition = new Vector2(439, 603); mr.sizeDelta = new Vector2(501, 140);   // up to the card: room for an update summary
            _message.enableAutoSizing = true; _message.fontSizeMax = 32; _message.fontSizeMin = 16;
            _message.alignment = TextAlignmentOptions.Center; _message.richText = false; _message.enableWordWrapping = true;
            var action = Button(t, "ButtonSave", () => { });   // hidden: only the template of the tapes' Install / Enable buttons
            _source = Button(t, "ButtonLoad", CopyNeeded);            // "Copy list" (RefreshNeeded labels it)
            _refresh = Button(t, "ButtonLoadLast", InstallNeededAll); // "Install all"
            Label(Button(t, "ButtonBack", Close), "Close");
            _up = Button(t, "ButtonUp", () => { _page--; Refresh(); }, true);
            _down = Button(t, "ButtonDown", () => { _page++; Refresh(); }, true);
            var tapes = new List<Transform>();
            for (int i = 0; i < t.childCount; i++) if (t.GetChild(i).name.StartsWith("ButtonFile")) tapes.Add(t.GetChild(i));
            tapes.Sort((a, b) =>
            {
                var pa = ((RectTransform)a).anchoredPosition; var pb = ((RectTransform)b).anchoredPosition;
                return Mathf.Abs(pa.y - pb.y) > 1 ? pb.y.CompareTo(pa.y) : pa.x.CompareTo(pb.x);
            });
            foreach (var tp in tapes)
            {
                int i = Tiles.Count;
                var tile = new Tile { Go = tp.gameObject, Name = Text(tp, "ButtonNameTextBackground/ButtonNameText") };
                var image = tp.Find("Mask2/ButtonImage").GetComponent<Image>(); image.sprite = save != null ? save.spriteNoImage : null;
                tile.Name.enableAutoSizing = true; tile.Name.fontSizeMin = 14; tile.Name.fontSizeMax = tile.Name.fontSize * 1.6f;
                tile.Name.fontStyle = FontStyles.Bold; tile.Name.overflowMode = TextOverflowModes.Ellipsis;
                // the version at the strip's right end, the name kept clear of it
                tile.Version = UnityEngine.Object.Instantiate(tile.Name.gameObject, tile.Name.transform.parent, false).GetComponent<TextMeshProUGUI>();
                tile.Version.name = "RevampModVersion"; tile.Version.fontStyle = FontStyles.Normal; tile.Version.raycastTarget = false;
                tile.Version.alignment = TextAlignmentOptions.Right; tile.Version.overflowMode = TextOverflowModes.Overflow;
                tile.Version.margin = new Vector4(tile.Version.margin.x, tile.Version.margin.y, tile.Version.margin.z + 10f, tile.Version.margin.w);
                tile.Name.margin = new Vector4(tile.Name.margin.x, tile.Name.margin.y, 120f, tile.Name.margin.w);
                tp.GetComponent<Button>().onClick.AddListener(() => { _selected = _page * Tiles.Count + i; Refresh(); });
                tile.Action = UnityEngine.Object.Instantiate(action, tp);
                tile.Action.name = "NeededModAction";
                var actionText = tile.Action.GetComponentInChildren<TextMeshProUGUI>(true);
                actionText.enableAutoSizing = true; actionText.fontSizeMax = 24; actionText.fontSizeMin = 14;
                tile.Action.onClick = new Button.ButtonClickedEvent();
                tile.Action.onClick.AddListener(() => NeededAction(_page * Tiles.Count + i));
                var ar = (RectTransform)tile.Action.transform;
                ar.anchorMin = ar.anchorMax = ar.pivot = new Vector2(1, 0);
                ar.anchoredPosition = new Vector2(-8, 8); ar.sizeDelta = new Vector2(130, 45);
                tile.Action.gameObject.SetActive(false);
                tile.Enable = UnityEngine.Object.Instantiate(tile.Action, tp);
                tile.Enable.name = "EnableRequiredMod";
                tile.Enable.onClick = new Button.ButtonClickedEvent();
                tile.Enable.onClick.AddListener(() => NeededEnable(_page * Tiles.Count + i));
                var er = (RectTransform)tile.Enable.transform;
                er.anchorMin = er.anchorMax = er.pivot = new Vector2(0, 0);
                er.anchoredPosition = new Vector2(8, 8);
                Label(tile.Enable, "Enable");
                tile.Required = new GameObject("RequiredModDetails", typeof(RectTransform), typeof(Image));
                tile.Required.transform.SetParent(tp.Find("Mask2"), false);
                var rr = (RectTransform)tile.Required.transform;
                rr.anchorMin = Vector2.zero; rr.anchorMax = Vector2.one;
                rr.offsetMin = new Vector2(8, 65); rr.offsetMax = new Vector2(-8, -8);
                tile.Required.GetComponent<Image>().color = new Color(0.06f, 0.05f, 0.04f, .9f);
                tile.Required.GetComponent<Image>().raycastTarget = false;
                tile.RequiredText = UnityEngine.Object.Instantiate(tile.Name, rr);
                var tr = (RectTransform)tile.RequiredText.transform;
                tr.anchorMin = Vector2.zero; tr.anchorMax = Vector2.one; tr.offsetMin = new Vector2(8, 8); tr.offsetMax = new Vector2(-8, -8);
                tile.RequiredText.margin = Vector4.zero; tile.RequiredText.richText = false; tile.RequiredText.raycastTarget = false;
                tile.RequiredText.fontSizeMax = 26; tile.RequiredText.fontSizeMin = 14; tile.RequiredText.enableWordWrapping = true;
                tile.RequiredText.alignment = TextAlignmentOptions.TopLeft;
                tile.Required.SetActive(false);
                Tiles.Add(tile);
            }
        }

        internal static string Installed(ModCatalog.Mod m, out bool manual)
        {
            manual = false; string newest = null;
            // Include BepInEx plugins which have not registered with the multiplayer API.
            foreach (var p in BepInEx.Bootstrap.Chainloader.PluginInfos.Values)
                if (p.Metadata.GUID == m.Id && File.Exists(p.Location))
                {
                    string v = p.Metadata.Version.ToString();
                    if (!ModFiles.Managed(ModInstall.InstallDir, p.Location, m.Id)) { manual = true; return v; }
                    if (newest == null || Newer(v, newest)) newest = v;
                }
            foreach (var p in MpApi.Mods)
                if (p.Id == m.Id && File.Exists(p.File))
                {
                    if (!ModFiles.Managed(ModInstall.InstallDir, p.File, m.Id)) { manual = true; return p.Version; }
                    if (newest == null || Newer(p.Version, newest)) newest = p.Version;
                }
            // Pending installs/removals are reflected immediately, before BepInEx runs again.
            foreach (var f in ModInstall.ManagedFiles(m.Id))
            {
                string stem = Path.GetFileNameWithoutExtension(f);
                if (!stem.StartsWith(m.Id + "-", StringComparison.Ordinal)) continue;
                string v = stem.Substring(m.Id.Length + 1);
                if (newest == null || Newer(v, newest)) newest = v;
            }
            return newest;
        }

        private static bool Newer(string offered, string installed) => ModCatalog.Newer(offered, installed);

        private static void Refresh()
        {
            if (ListOpen) { RefreshList(); return; }
            if (_needed != null) RefreshNeeded();
        }

        // the state colours (TileState), drawn darker on the list's paper (OnPaper)
        static readonly Color On = new Color32(0x7C, 0xFF, 0x7C, 0xFF), Off = new Color32(0xFF, 0xB3, 0x47, 0xFF),
            Update = new Color32(0xFF, 0xE1, 0x4D, 0xFF), Absent = new Color32(0xE6, 0xE6, 0xE6, 0xFF), Unavailable = new Color32(0x9A, 0x9A, 0x9A, 0xFF),
            Removing = new Color32(0xFF, 0x70, 0x70, 0xFF);

        /// One mod's state line, version line and colour (the list's rows).
        private static void TileState(ModCatalog.Mod m, out string state, out string version, out Color colour)
        {
            var offered = ModCatalog.Offered(m);
            string available = offered?.Version;
            bool availableRelease = offered != null && ModCatalog.Reason(m, offered) == null;
            string installed = null; bool manual = false; string status = "";
            try { installed = Installed(m, out manual); status = ModBrowser.State(m.Id) ?? ""; } catch (System.Exception quietError) { Diagnostics.QuietErrors.Once("ModsScreen.TileState", quietError); }
            bool update = installed != null && available != null && Newer(available, installed);
            bool pending = status.EndsWith("at next start") || status.Contains("takes effect at next start");
            version = installed == null ? (available != null ? "v" + available : "") : "v" + installed;
            if (ModActivation.Core(m.Id)) { state = "Built in"; colour = On; return; }
            if (manual) { state = "On · installed by hand"; colour = On; return; }
            // a removal already carried out reads "Not installed" (ModActivation.Status), so this one is still to come
            if (status.StartsWith("Removed")) { state = "Removed at next start"; colour = Removing; return; }
            if (installed == null)
            {
                state = availableRelease ? "Not installed" : "Not available"; colour = availableRelease ? Absent : Unavailable; return;
            }
            bool off = status.StartsWith("Disabled");
            state = (off ? "Off" : "On") + (pending ? " at next start" : "") + (update ? " · update to v" + available : "");
            colour = update ? Update : off ? Off : On;
        }
    }
}
