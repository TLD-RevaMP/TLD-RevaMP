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
    /// Like MpScreen, reuse the game's tapes, paper card, buttons and sounds from Save/Load.
    [HarmonyPatch]
    public static class ModsScreen
    {
        private static GameObject _root, _settings, _confirm;
        private static menuhandler _builtFor;
        private static TextMeshProUGUI _title, _count, _name, _version, _message, _question;
        private static readonly List<TextMeshProUGUI> Lines = new List<TextMeshProUGUI>();
        /// A tape: the mod's name in its strip (bold, as large as it fits), the picture, and over the picture's bottom a dark
        /// band with the version and the state (on, off, not installed, update) in the state's colour. Asked for
        /// 2026-11-11: the name was small and nothing said the version or whether a mod was installed or on.
        private sealed class Tile { internal GameObject Go; internal TextMeshProUGUI Name, Version, Info; internal Image Body; }
        private static readonly List<Tile> Tiles = new List<Tile>();
        private static Button _install, _remove, _source, _refresh, _up, _down, _toggle;
        private static List<ModCatalog.Mod> _items = new List<ModCatalog.Mod>();
        private static int _page, _selected;
        private static float _refreshAt;
        private static Action _yes, _no;            // what the question's Yes and No do (remove the selected mod; update or not)
        private static bool _checked, _offer;       // the start-up update check: started, and its answer still to show
        public static bool IsOpen => _root != null && _root.activeSelf;
        internal static bool IsVisible => _root != null && _root.activeInHierarchy;

        /// settings: the Revamp settings page to hide while open, or null over the main menu (the game shows it again).
        internal static void Open(GameObject settings, bool fetch = true)
        {
            try
            {
                if (menuhandler.s == null || menuhandler.s.SaveLoadObject == null) return;
                MpScreen.Close();
                if (_root == null || _builtFor != menuhandler.s) Build(menuhandler.s);
                _settings = settings; if (_settings != null) _settings.SetActive(false);
                _root.transform.SetAsLastSibling(); _root.SetActive(true);
                _page = 0; _selected = 0; _confirm.SetActive(false);
                if (fetch) { ModCatalog.Refresh(); _offer = false; }   // opened by hand: the start-up question is not needed any more
                Refresh();
            }
            catch (Exception e) { Plugin.Log.LogError("ModsScreen: " + e); Close(); }
        }

        /// The start-up check (like MSCLoader's): once the main menu shows, fetch the catalog in the background; if a mod
        /// installed from the browser has a newer approved version, open this screen and ask to update. Mods installed by
        /// hand are named, to update by hand. Once per game run; the test machines (bridge on) only check when told to.
        private static void StartupCheck(menuhandler mh)
        {
            if (!_checked)
            {
                _checked = true;
                if (!ModCatalog.CheckAtStart || Plugin.BridgeEnabled || DedicatedServer.Enabled || ModCatalog.Busy) return;
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
            // select the first mod the question names
            string first = updates.Count > 0 ? updates[0].Key.Id : null;
            int at = first == null ? -1 : _items.FindIndex(x => x.Id == first);
            if (at >= 0) { _selected = at; _page = at / Math.Max(1, Tiles.Count); Refresh(); }
        }

        /// For the bridge (mods check): run the start-up check again now, as at start, even with the bridge on.
        internal static void CheckNow()
        {
            _checked = true;
            ModCatalog.Refresh(true); _offer = ModCatalog.Busy;
        }
        internal static void Answer(bool yes) { if (Question != null) { _confirm.SetActive(false); (yes ? _yes : _no)?.Invoke(); } }
        internal static string Question => _confirm != null && _confirm.activeInHierarchy ? _question.text : null;

        /// Every catalog mod with a newer approved version than the installed one; byHand gets the ones installed by hand.
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
                    var v = ModCatalog.UpdateTo(m, installed, ModTransfer.Approved(), ModTransfer.MaxBytes, MpApi.ValidId);
                    if (v == null) continue;
                    if (manual) byHand.Add(m.Name + " " + v.Version);
                    else list.Add(new KeyValuePair<ModCatalog.Mod, ModCatalog.Release>(m, v));
                }
                catch (Exception e) { Plugin.Log.LogWarning("Mod update check: " + m.Id + ": " + e.Message); }
            }
            return list;
        }

        private static void Ask(string question, Action yes, Action no = null)
        {
            _question.text = Esc(question); _yes = yes; _no = no ?? Refresh; _confirm.SetActive(true); Refresh();
        }

        public static void Close()
        {
            ModCatalog.Cancel();
            if (_root != null) _root.SetActive(false);
            if (_settings != null) _settings.SetActive(true);
            _settings = null;
        }

        [HarmonyPatch(typeof(menuhandler), "Update")]
        [HarmonyPostfix]
        private static void MenuUpdate(menuhandler __instance)
        {
            if (__instance == menuhandler.s && DataFromMenuScript.s != null && DataFromMenuScript.s.mainmenu && Time.timeSinceLevelLoad > 1f)
                StartupCheck(__instance);
            if (!IsOpen || __instance != _builtFor) return;
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
                "TMP_IF_SaveName", "ButtonFastUp", "ButtonFastDown", "ButtonJumpUp", "ButtonJumpDown", "ButtonSetActive" })
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
            _install = Button(t, "ButtonSave", Install);
            _remove = Button(t, "ButtonDelete", AskRemove, true); Label(_remove, "Remove");
            _source = Button(t, "ButtonLoad", Source); Label(_source, "Source");
            _refresh = Button(t, "ButtonLoadLast", () => { _confirm.SetActive(false); ModCatalog.Refresh(); Refresh(); }); Label(_refresh, "Refresh");
            _toggle = Button(t, "ButtonSetActive", Toggle); Label(_toggle, "Disable");
            Button(t, "ButtonBack", Close);
            _up = Button(t, "ButtonUp", () => { _page--; Refresh(); }, true);
            _down = Button(t, "ButtonDown", () => { _page++; Refresh(); }, true);
            _confirm = t.Find("TextDelete").gameObject; _question = _confirm.GetComponent<TextMeshProUGUI>();
            _question.enableAutoSizing = true; _question.fontSizeMax = _question.fontSize; _question.fontSizeMin = 16; _question.enableWordWrapping = true;
            // room for the update question (StartupCheck): the box grows left and up over the free space above Yes/No,
            // which stay where they are; the text sits on the buttons
            var qr = (RectTransform)_question.transform; float grow = Math.Max(0f, 500f - qr.sizeDelta.x);
            var kids = new List<KeyValuePair<Transform, Vector3>>();
            foreach (Transform c in qr) kids.Add(new KeyValuePair<Transform, Vector3>(c, c.localPosition));
            qr.anchoredPosition -= new Vector2(grow, 0f); qr.sizeDelta = new Vector2(qr.sizeDelta.x + grow, Math.Max(qr.sizeDelta.y, 150f));
            foreach (var k in kids) k.Key.localPosition = k.Value + new Vector3(grow, 0f, 0f);
            _question.alignment = TextAlignmentOptions.BottomRight;
            Button(_confirm.transform, "ButtonDeleteYes", () => { _confirm.SetActive(false); _yes?.Invoke(); });
            Button(_confirm.transform, "ButtonDeleteNo", () => { _confirm.SetActive(false); _no?.Invoke(); });
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
                // a dark band over the bottom of the picture (the visible window, Mask2: the picture is bigger): version and state
                var band = new GameObject("RevampModBand", typeof(RectTransform)); band.transform.SetParent(tp.Find("Mask2"), false);
                var brt = (RectTransform)band.transform; brt.anchorMin = Vector2.zero; brt.anchorMax = new Vector2(1f, 0.26f);
                brt.offsetMin = Vector2.zero; brt.offsetMax = Vector2.zero;
                tile.Body = band.AddComponent<Image>(); tile.Body.raycastTarget = false; tile.Body.color = new Color(0.06f, 0.05f, 0.04f, 0.9f);
                var go = new GameObject("RevampModInfo", typeof(RectTransform)); go.transform.SetParent(band.transform, false);
                var irt = (RectTransform)go.transform; irt.anchorMin = Vector2.zero; irt.anchorMax = Vector2.one;
                irt.offsetMin = new Vector2(10f, 4f); irt.offsetMax = new Vector2(-10f, -4f);
                tile.Info = go.AddComponent<TextMeshProUGUI>(); tile.Info.font = tile.Name.font; tile.Info.raycastTarget = false;
                tile.Info.alignment = TextAlignmentOptions.Center; tile.Info.enableWordWrapping = false; tile.Info.richText = true;
                tile.Info.enableAutoSizing = true; tile.Info.fontSizeMin = 14; tile.Info.fontSizeMax = 44; tile.Info.color = Color.white;
                tile.Info.overflowMode = TextOverflowModes.Ellipsis;
                tp.GetComponent<Button>().onClick.AddListener(() => { _selected = _page * Tiles.Count + i; _confirm.SetActive(false); Refresh(); });
                Tiles.Add(tile);
            }
        }

        private static ModCatalog.Mod Selected() => _selected >= 0 && _selected < _items.Count ? _items[_selected] : null;

        private static string Installed(ModCatalog.Mod m, out bool manual)
        {
            manual = false; string newest = null;
            // Include BepInEx plugins which have not registered with the multiplayer API.
            foreach (var p in BepInEx.Bootstrap.Chainloader.PluginInfos.Values)
                if (p.Metadata.GUID == m.Id && File.Exists(p.Location))
                {
                    string v = p.Metadata.Version.ToString();
                    if (!ModFiles.Managed(ModTransfer.InstallDir, p.Location, m.Id)) { manual = true; return v; }
                    if (newest == null || Newer(v, newest)) newest = v;
                }
            foreach (var p in MpApi.Mods)
                if (p.Id == m.Id && File.Exists(p.File))
                {
                    if (!ModFiles.Managed(ModTransfer.InstallDir, p.File, m.Id)) { manual = true; return p.Version; }
                    if (newest == null || Newer(p.Version, newest)) newest = p.Version;
                }
            // Pending installs/removals are reflected immediately, before BepInEx runs again.
            foreach (var f in ModTransfer.ManagedFiles(m.Id))
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
            _refreshAt = Time.unscaledTime + 0.25f;
            _items = ModBrowser.Items();
            var mods = _items;
            int count = mods?.Count ?? 0, pages = Math.Max(1, (count + Tiles.Count - 1) / Math.Max(1, Tiles.Count));
            _page = Math.Max(0, Math.Min(_page, pages - 1));
            _title.text = "Mods  " + (_page + 1) + "/" + pages; _count.text = count + " mods";
            for (int i = 0; i < Tiles.Count; i++)
            {
                int index = _page * Tiles.Count + i; var tile = Tiles[i]; tile.Go.SetActive(index < count);
                if (index >= count) continue;
                var item = mods[index];
                TileState(item, out string state, out string version, out Color colour);
                bool selected = index == _selected;
                tile.Name.text = Esc(item.Name ?? item.Id);
                tile.Name.color = selected ? new Color32(0xC8, 0xFF, 0xC8, 0xFF) : Color.white;
                tile.Version.text = Esc(version); tile.Version.color = tile.Name.color;
                tile.Info.text = "<b><color=#" + ColorUtility.ToHtmlStringRGB(colour) + ">" + Esc(state) + "</color></b>";
            }
            _up.gameObject.SetActive(_page > 0); _down.gameObject.SetActive(_page < pages - 1);
            var m = Selected();
            _install.gameObject.SetActive(m != null || ModCatalog.Busy); _remove.gameObject.SetActive(false); _source.gameObject.SetActive(m != null && ModCatalog.SourceUrl(m.Source));
            _toggle.gameObject.SetActive(false);
            _refresh.interactable = !ModCatalog.Busy;
            Label(_install, ModCatalog.Busy ? "Cancel" : "Install"); _install.interactable = ModCatalog.Busy;
            _name.text = m != null ? Esc(m.Name ?? m.Id) : "Approved mods"; _version.text = "";
            foreach (var line in Lines) line.text = "";
            if (m != null)
            {
                var v = ModCatalog.Offered(m); string reason = ModCatalog.Reason(m, v);
                string installed = Installed(m, out bool manual);
                bool update = installed != null && Newer(v.Version, installed);
                _version.text = Esc(v.Version);
                Lines[0].text = "By " + (m.Author ?? "unknown") + "  |  " + ModTransfer.Size(v.Size);
                Lines[1].text = m.Description ?? "Single-file mod.";
                Lines[2].text = m.Source ?? "No source link provided.";
                Lines[3].text = "Installed version: " + (installed ?? "none");
                bool core = ModActivation.Core(m.Id);
                string state = core ? "Protected core" : ModBrowser.State(m.Id);
                Lines[4].text = core ? state : manual ? "Enabled (installed by hand; manage files manually)." : state + (update ? "; update available." : "");
                if (!ModCatalog.Busy)
                {
                    Label(_install, update ? "Update" : "Install");
                    _install.interactable = !core && !manual && reason == null && (installed == null || update);
                }
                _remove.gameObject.SetActive(!core && !manual && !_confirm.activeSelf && ModTransfer.ManagedFiles(m.Id).Count > 0);
                _remove.interactable = !ModCatalog.Busy;
                _toggle.gameObject.SetActive(!core && !manual && !_confirm.activeSelf && installed != null);
                _toggle.interactable = !ModCatalog.Busy;
                if (!core) Label(_toggle, ModActivation.Desired(ModTransfer.InstallDir, m.Id) == "disabled" ? "Enable" : "Disable");
                if (reason != null && !manual) Lines[1].text += " " + reason;
            }
            _message.text = ModCatalog.Progress + "\nEnable, disable and remove take effect at next start. Updates keep disabled mods disabled.";
            _message.gameObject.SetActive(_message.text.Length > 0);
        }

        // the state's text colour on the tape's dark band
        static readonly Color On = new Color32(0x7C, 0xFF, 0x7C, 0xFF), Off = new Color32(0xFF, 0xB3, 0x47, 0xFF),
            Update = new Color32(0xFF, 0xE1, 0x4D, 0xFF), Absent = new Color32(0xE6, 0xE6, 0xE6, 0xFF), Unapproved = new Color32(0x9A, 0x9A, 0x9A, 0xFF),
            Removing = new Color32(0xFF, 0x70, 0x70, 0xFF);

        /// One tape's state line, version line and colour.
        private static void TileState(ModCatalog.Mod m, out string state, out string version, out Color colour)
        {
            var offered = ModCatalog.Offered(m);
            string available = offered?.Version;
            bool approved = offered != null && ModCatalog.Reason(m, offered) == null;
            string installed = null; bool manual = false; string status = "";
            try { installed = Installed(m, out manual); status = ModBrowser.State(m.Id) ?? ""; } catch (Exception) { }
            bool update = installed != null && available != null && Newer(available, installed);
            bool pending = status.EndsWith("at next start") || status.Contains("takes effect at next start");
            version = installed == null ? (available != null ? "v" + available : "") : "v" + installed;
            if (ModActivation.Core(m.Id)) { state = "Built in"; colour = On; return; }
            if (manual) { state = "On · installed by hand"; colour = On; return; }
            if (status.StartsWith("Removed")) { state = pending ? "Removed at next start" : "Removed"; colour = Removing; return; }
            if (installed == null)
            {
                state = approved ? "Not installed" : "Not available"; colour = approved ? Absent : Unapproved; return;
            }
            bool off = status.StartsWith("Disabled");
            state = (off ? "Off" : "On") + (pending ? " at next start" : "") + (update ? " · update to v" + available : "");
            colour = update ? Update : off ? Off : On;
        }

        private static void Install()
        {
            if (ModCatalog.Busy) ModCatalog.Cancel();
            else
            {
                var m = Selected();
                if (m != null)
                {
                    string installed = Installed(m, out bool manual);
                    var v = ModCatalog.Offered(m);
                    if (ModActivation.Core(m.Id) || manual || installed != null && !Newer(v.Version, installed)) return;
                    ModCatalog.Download(m, v);
                }
            }
            _confirm.SetActive(false); Refresh();
        }
        private static void Source() { var m = Selected(); if (m != null && ModCatalog.SourceUrl(m.Source)) Application.OpenURL(m.Source); }
        private static void Toggle()
        {
            var m = Selected(); if (ModCatalog.Busy || m == null) return;
            try
            {
                ModBrowser.Action(m.Id, ModActivation.Desired(ModTransfer.InstallDir, m.Id) == "disabled" ? "enabled" : "disabled");
                ModCatalog.Message = ModBrowser.State(m.Id) + ".";
            }
            catch (Exception e) { ModCatalog.Message = "Could not change mod: " + e.Message; }
            Refresh();
        }
        private static void AskRemove()
        {
            if (ModCatalog.Busy || Selected() == null) return;
            Ask("Remove " + Selected().Name + " at the next start?", Remove);
        }
        private static void Remove()
        {
            var m = Selected(); if (ModCatalog.Busy || m == null) return;
            Installed(m, out bool manual); if (manual) return;
            try
            {
                ModBrowser.Action(m.Id, "removed");
                ModCatalog.Message = "Remove " + m.Name + " at next start.";
            }
            catch (Exception e) { ModCatalog.Message = "Could not remove: " + e.Message; }
            Refresh();
        }
    }
}
