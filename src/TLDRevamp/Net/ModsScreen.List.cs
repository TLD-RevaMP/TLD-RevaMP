using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using HarmonyLib;
using TLDRevamp.Multiplayer;
using TMPro;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.UI;

namespace TLDRevamp.Net
{
    /// The Mods list (asked for 2026-11-20: "a similar thing to the RevaMP launcher", in the game's own style): the
    /// launcher's Mods page layout - tabs (All mods / Installed), search, sort, one row per mod with its tile, name,
    /// author, state, description, numbers and buttons - drawn with the game's own pieces: the Save/Load screen's dimmed
    /// backdrop, paper card and menu buttons, its name field for the search and the main menu's language dropdown for the
    /// sort. The required-mods screen (joining a game that needs mods) still uses the tapes.
    public static partial class ModsScreen
    {
        private static GameObject _list, _dialog;
        private static menuhandler _listFor;
        private static RectTransform _rows;
        private static ScrollRect _scroll;
        private static TMP_InputField _search;
        private static TMP_Dropdown _sort;
        private static Button _tabAll, _tabInstalled, _listRefresh;
        private static TextMeshProUGUI _listCount, _listMessage, _dialogText;
        private static bool _installedTab;
        private static string _shown;               // what the rows show now; they are made again only when it changes
        // the game's pieces the list is made of
        private static Button _buttonTemplate;
        private static Sprite _paper, _buttonSprite;
        private static TextMeshProUGUI _paperStyle, _whiteStyle, _orangeStyle;

        internal static bool ListOpen => _list != null && _list.activeSelf;

        /// Typing in the search box: the game takes no input (its own check for its text fields, as for the chat). The
        /// copied Save-screen field is not one the game knows, so its T (the Multiplayer screen) closed the list and
        /// cancelled a download in the middle of typing "mount", and the voice key talked.
        [HarmonyPatch(typeof(mainscript), nameof(mainscript.SomeInputSelected)), HarmonyPostfix]
        private static void SearchTyping(ref bool __result) { if (_search != null && ListOpen && _search.isFocused) __result = true; }

        static readonly Color Paper = new Color32(0xFF, 0xF3, 0xD0, 0xFF), Ink = new Color32(0x28, 0x28, 0x28, 0xFF),
            Faded = new Color32(0x6E, 0x66, 0x50, 0xFF), Body = new Color32(0x3C, 0x3C, 0x3C, 0xFF),
            ButtonGrey = new Color32(0x82, 0x82, 0x82, 0xFF), ButtonGreen = new Color32(0x4F, 0xB8, 0x1E, 0xFF),
            ButtonOrange = new Color32(0xFF, 0x64, 0x00, 0xFF), ButtonRed = new Color32(0xC8, 0x50, 0x50, 0xFF),
            ButtonFace = new Color32(0xE7, 0xDC, 0xAD, 0xFF), ButtonFaceOn = new Color32(0xC9, 0xBC, 0x86, 0xFF);
        // tiles: muted colours that sit on the paper
        static readonly Color32[] Tiles8 = {
            new Color32(0x5F, 0x8F, 0xB4, 0xFF), new Color32(0x8E, 0x7D, 0xBE, 0xFF), new Color32(0xB5, 0x65, 0x4D, 0xFF), new Color32(0x6F, 0x9E, 0x6B, 0xFF),
            new Color32(0xC0, 0x84, 0x57, 0xFF), new Color32(0x4F, 0x8F, 0x8F, 0xFF), new Color32(0xA3, 0x77, 0x9A, 0xFF), new Color32(0x8A, 0x8F, 0x4B, 0xFF) };

        // ---------------------------------------------------------------- opening, closing, questions

        private static void OpenList(GameObject settings, bool fetch)
        {
            if (_root != null) _root.SetActive(false);
            if (_list == null || _listFor != menuhandler.s) BuildList(menuhandler.s);
            _settings = settings; if (_settings != null) _settings.SetActive(false);
            _list.transform.SetAsLastSibling(); _list.SetActive(true); _dialog.SetActive(false);
            if (fetch) { ModCatalog.Refresh(); _offer = false; }   // opened by hand: the start-up question is not needed any more
            _shown = null;
            RefreshList();
        }

        private static void AskList(string question, Action yes, Action no)
        {
            _dialogText.text = Esc(question); _yes = yes; _no = no ?? Refresh;
            _dialog.transform.SetAsLastSibling(); _dialog.SetActive(true);
        }

        // ---------------------------------------------------------------- building

        private static void BuildList(menuhandler mh)
        {
            if (_list != null) UnityEngine.Object.Destroy(_list);
            _listFor = mh;
            var save = mh.SaveLoadObject.transform;
            _buttonTemplate = save.Find("ButtonBack").GetComponent<Button>();
            _buttonSprite = _buttonTemplate.GetComponent<Image>().sprite;
            _paper = CardTop(save.Find("InfoBackGround").GetComponent<Image>().sprite);
            _paperStyle = Text(save, "InfoBackGround/Text (1)");
            _whiteStyle = Text(save, "PageText");
            _orangeStyle = Text(save, "TextCantSave");

            _list = new GameObject("TLDRevampModList", typeof(RectTransform));
            _list.SetActive(false);
            _list.transform.SetParent(save.parent, false);
            Stretch(_list.transform);   // full height (its width is the parent's: none; the frame and backdrops set their own)
            Backdrop(_list.transform, new Color32(0x32, 0x32, 0x32, 0xC8));   // the Save/Load backdrop

            // a 1920-wide frame in the middle, as tall as the screen (the Save/Load screen's own layout)
            var frame = (RectTransform)new GameObject("Frame", typeof(RectTransform)).transform;
            frame.SetParent(_list.transform, false);
            frame.anchorMin = new Vector2(.5f, 0f); frame.anchorMax = new Vector2(.5f, 1f); frame.pivot = new Vector2(.5f, .5f);
            frame.sizeDelta = new Vector2(1920f, 0f); frame.anchoredPosition = Vector2.zero;

            // the title where the Save/Load screen has its page line (clear of the clock top left and the version top right)
            var title = NewText(frame, _whiteStyle, 64f, Color.white, TextAlignmentOptions.Left, "Title");
            TopLeft(title.rectTransform, 540f, 18f, 220f, 90f); title.text = "Mods"; title.fontStyle = FontStyles.Bold;
            _listCount = NewText(frame, _whiteStyle, 38f, new Color(1f, 1f, 1f, .8f), TextAlignmentOptions.Left, "Count");
            TopLeft(_listCount.rectTransform, 760f, 34f, 600f, 70f); _listCount.fontStyle = FontStyles.Bold;

            // the toolbar: tabs, search, sort, refresh
            _tabAll = NewButton(frame, "All mods", ButtonGrey, 40f, () => { _installedTab = false; _shown = null; RefreshList(); });
            TopLeft((RectTransform)_tabAll.transform, 80f, 120f, 270f, 84f);   // the row: 20 px between every piece
            _tabInstalled = NewButton(frame, "Installed", ButtonGrey, 40f, () => { _installedTab = true; _shown = null; RefreshList(); });
            TopLeft((RectTransform)_tabInstalled.transform, 370f, 120f, 300f, 84f);

            _search = UnityEngine.Object.Instantiate(save.Find("TMP_IF_SaveName").gameObject, frame, false).GetComponent<TMP_InputField>();
            _search.name = "Search"; Quiet(_search.gameObject);
            _search.onValueChanged = new TMP_InputField.OnChangeEvent(); _search.onEndEdit = new TMP_InputField.SubmitEvent();
            _search.onSubmit = new TMP_InputField.SubmitEvent(); _search.onSelect = new TMP_InputField.SelectionEvent();
            _search.onDeselect = new TMP_InputField.SelectionEvent();
            _search.characterLimit = 40; _search.text = "";
            if (_search.placeholder is TMP_Text ph) { ph.text = "Search mods..."; ph.fontStyle = FontStyles.Italic; }
            _search.onValueChanged.AddListener(_ => { _shown = null; RefreshList(); });
            TopLeft((RectTransform)_search.transform, 690f, 122f, 480f, 80f);

            var menuDropdown = mh.Menu != null ? mh.Menu.transform.Find("Language/Dropdown") : null;
            if (menuDropdown != null)
            {
                _sort = UnityEngine.Object.Instantiate(menuDropdown.gameObject, frame, false).GetComponent<TMP_Dropdown>();
                _sort.name = "Sort"; Quiet(_sort.gameObject);
                _sort.onValueChanged = new TMP_Dropdown.DropdownEvent();
                _sort.ClearOptions(); _sort.AddOptions(new List<string>(ModList.SortNames)); _sort.SetValueWithoutNotify(0);
                _sort.onValueChanged.AddListener(_ => { _shown = null; RefreshList(); });
                TopLeft((RectTransform)_sort.transform, 1190f, 127f, 420f, 70f);
            }
            _listRefresh = NewButton(frame, "Refresh", ButtonGreen, 40f, () => { ModCatalog.Refresh(); ModStatsFetch.Begin(true); _shown = null; RefreshList(); });
            TopRight((RectTransform)_listRefresh.transform, 80f, 120f, 210f, 84f);

            // the list: rows on a scrolling column, a scrollbar on its right
            var listGo = new GameObject("List", typeof(RectTransform));
            var listRt = (RectTransform)listGo.transform; listRt.SetParent(frame, false);
            listRt.anchorMin = new Vector2(0f, 0f); listRt.anchorMax = new Vector2(1f, 1f);
            listRt.offsetMin = new Vector2(80f, 140f); listRt.offsetMax = new Vector2(-80f, -228f);
            var viewport = (RectTransform)new GameObject("Viewport", typeof(RectTransform), typeof(Image), typeof(RectMask2D)).transform;
            viewport.SetParent(listRt, false); viewport.anchorMin = Vector2.zero; viewport.anchorMax = Vector2.one;
            viewport.offsetMin = Vector2.zero; viewport.offsetMax = new Vector2(-36f, 0f);
            viewport.GetComponent<Image>().color = new Color(0f, 0f, 0f, 0.001f);   // catches the wheel between rows
            _rows = (RectTransform)new GameObject("Rows", typeof(RectTransform)).transform;
            _rows.SetParent(viewport, false);
            _rows.anchorMin = new Vector2(0f, 1f); _rows.anchorMax = new Vector2(1f, 1f); _rows.pivot = new Vector2(.5f, 1f);
            _rows.offsetMin = Vector2.zero; _rows.offsetMax = Vector2.zero;
            var column = _rows.gameObject.AddComponent<VerticalLayoutGroup>(); column.spacing = 14f;
            column.childControlWidth = true; column.childControlHeight = true; column.childForceExpandWidth = true; column.childForceExpandHeight = false;
            var fit = _rows.gameObject.AddComponent<ContentSizeFitter>(); fit.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            _scroll = listGo.AddComponent<ScrollRect>();
            _scroll.viewport = viewport; _scroll.content = _rows; _scroll.horizontal = false; _scroll.vertical = true;
            _scroll.movementType = ScrollRect.MovementType.Clamped; _scroll.scrollSensitivity = 60f; _scroll.inertia = false;

            var bar = (RectTransform)new GameObject("Scrollbar", typeof(RectTransform), typeof(Image), typeof(Scrollbar)).transform;
            bar.SetParent(listRt, false); bar.anchorMin = new Vector2(1f, 0f); bar.anchorMax = new Vector2(1f, 1f); bar.pivot = new Vector2(1f, .5f);
            bar.sizeDelta = new Vector2(24f, 0f); bar.anchoredPosition = Vector2.zero;
            bar.GetComponent<Image>().color = new Color(0f, 0f, 0f, .35f);
            var area = (RectTransform)new GameObject("Sliding Area", typeof(RectTransform)).transform;
            area.SetParent(bar, false); area.anchorMin = Vector2.zero; area.anchorMax = Vector2.one; area.offsetMin = Vector2.zero; area.offsetMax = Vector2.zero;
            var handle = (RectTransform)new GameObject("Handle", typeof(RectTransform), typeof(Image)).transform;
            handle.SetParent(area, false); handle.offsetMin = Vector2.zero; handle.offsetMax = Vector2.zero;
            var hImg = handle.GetComponent<Image>(); hImg.sprite = _buttonSprite; hImg.type = Image.Type.Sliced; hImg.color = ButtonFace;
            var sb = bar.GetComponent<Scrollbar>(); sb.handleRect = handle; sb.targetGraphic = hImg; sb.direction = Scrollbar.Direction.BottomToTop;
            _scroll.verticalScrollbar = sb; _scroll.verticalScrollbarVisibility = ScrollRect.ScrollbarVisibility.AutoHide;

            // the footer: Close where the game has Back, the message beside it in the game's orange
            var close = NewButton(frame, "Close", ButtonGrey, 55f, Close);
            var cr = (RectTransform)close.transform; cr.anchorMin = cr.anchorMax = cr.pivot = Vector2.zero;
            cr.anchoredPosition = new Vector2(80f, 28f); cr.sizeDelta = new Vector2(340f, 88f);
            _listMessage = NewText(frame, _orangeStyle, 32f, ButtonOrange, TextAlignmentOptions.MidlineLeft, "Message");
            var mr = _listMessage.rectTransform; mr.anchorMin = mr.anchorMax = mr.pivot = Vector2.zero;
            mr.anchoredPosition = new Vector2(460f, 22f); mr.sizeDelta = new Vector2(1380f, 100f);
            _listMessage.enableWordWrapping = true; _listMessage.fontStyle = FontStyles.Bold;
            _listMessage.enableAutoSizing = true; _listMessage.fontSizeMax = 32f; _listMessage.fontSizeMin = 18f;

            // the question (remove, start-up updates): a paper card over a darker backdrop
            _dialog = new GameObject("Question", typeof(RectTransform));
            _dialog.transform.SetParent(_list.transform, false); Stretch(_dialog.transform);
            Backdrop(_dialog.transform, new Color(0f, 0f, 0f, .55f));
            var card = (RectTransform)new GameObject("Card", typeof(RectTransform), typeof(Image)).transform;
            card.SetParent(_dialog.transform, false); card.anchorMin = card.anchorMax = card.pivot = new Vector2(.5f, .5f);
            card.sizeDelta = new Vector2(1040f, 420f);
            var cImg = card.GetComponent<Image>(); cImg.sprite = _paper; cImg.type = Image.Type.Sliced; cImg.color = Paper;
            _dialogText = NewText(card, _paperStyle, 40f, Ink, TextAlignmentOptions.Center, "Text");
            var dr = _dialogText.rectTransform; dr.anchorMin = new Vector2(0f, 0f); dr.anchorMax = new Vector2(1f, 1f);
            dr.offsetMin = new Vector2(50f, 140f); dr.offsetMax = new Vector2(-50f, -70f);
            _dialogText.enableWordWrapping = true; _dialogText.enableAutoSizing = true; _dialogText.fontSizeMax = 40f; _dialogText.fontSizeMin = 20f;
            _dialogText.fontStyle = FontStyles.Bold;
            var yes = NewButton(card, "Yes", ButtonGreen, 55f, () => { _dialog.SetActive(false); _yes?.Invoke(); });
            var no = NewButton(card, "No", ButtonRed, 55f, () => { _dialog.SetActive(false); _no?.Invoke(); });
            foreach (var (b, x) in new[] { (yes, -170f), (no, 170f) })
            {
                var r = (RectTransform)b.transform; r.anchorMin = r.anchorMax = r.pivot = new Vector2(.5f, 0f);
                r.anchoredPosition = new Vector2(x, 36f); r.sizeDelta = new Vector2(280f, 88f);
            }
            _dialog.SetActive(false);
        }

        /// The Save/Load screen's index card cut above its blue rule: its rounded top and plain paper (the whole card has
        /// a blue rule, three dotted rules and a blue band at the bottom, which crossed the rows' text).
        private static Sprite CardTop(Sprite card)
        {
            if (card == null || card.texture == null) return card;
            var r = card.rect;
            float h = Mathf.Min(r.height, 84f * r.height / 512f);
            var cut = Sprite.Create(card.texture, new Rect(r.x, r.yMax - h, r.width, h), new Vector2(.5f, .5f), card.pixelsPerUnit, 0,
                SpriteMeshType.FullRect, new Vector4(20f, 0f, 19f, 20f * r.height / 512f));
            cut.name = "TLDRevampModCard";
            return cut;
        }

        /// A backdrop over the whole screen in the given colour (it also stops clicks reaching the menu behind). The menu's
        /// objects sit in a parent with no width (the Save/Load screen's backdrop is 1920 wide, its loading backdrop 3840),
        /// so stretching to it gives nothing: a fixed width, wide enough for any screen, centred and full height.
        private static void Backdrop(Transform parent, Color colour)
        {
            var r = (RectTransform)new GameObject("Backdrop", typeof(RectTransform), typeof(Image)).transform;
            r.SetParent(parent, false); r.SetAsFirstSibling();
            r.anchorMin = new Vector2(.5f, 0f); r.anchorMax = new Vector2(.5f, 1f); r.pivot = new Vector2(.5f, .5f);
            r.sizeDelta = new Vector2(16000f, 0f); r.anchoredPosition = Vector2.zero;
            var img = r.GetComponent<Image>(); img.color = colour; img.raycastTarget = true;
        }

        private static void Stretch(Transform t)
        {
            var r = (RectTransform)t; r.anchorMin = Vector2.zero; r.anchorMax = Vector2.one; r.offsetMin = Vector2.zero; r.offsetMax = Vector2.zero;
        }
        private static void TopLeft(RectTransform r, float left, float top, float w, float h)
        { r.anchorMin = r.anchorMax = r.pivot = new Vector2(0f, 1f); r.anchoredPosition = new Vector2(left, -top); r.sizeDelta = new Vector2(w, h); }
        private static void TopRight(RectTransform r, float right, float top, float w, float h)
        { r.anchorMin = r.anchorMax = r.pivot = new Vector2(1f, 1f); r.anchoredPosition = new Vector2(-right, -top); r.sizeDelta = new Vector2(w, h); }
        /// A band across its parent: left and right margins, from `top` down `height`.
        private static void Band(RectTransform r, float left, float right, float top, float height)
        {
            r.anchorMin = new Vector2(0f, 1f); r.anchorMax = new Vector2(1f, 1f); r.pivot = new Vector2(0f, 1f);
            r.offsetMin = new Vector2(left, -top - height); r.offsetMax = new Vector2(-right, -top);
        }

        /// The game's own scripts off a copied piece (its language text would put "Back" on every button, the language
        /// dropdown's would change the language); the UI's own components stay.
        private static void Quiet(GameObject go)
        {
            foreach (var c in go.GetComponentsInChildren<languagetext>(true)) UnityEngine.Object.DestroyImmediate(c);
            foreach (var c in go.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (c == null) continue;
                string ns = c.GetType().Namespace ?? "";
                if (!ns.StartsWith("UnityEngine", StringComparison.Ordinal) && !ns.StartsWith("TMPro", StringComparison.Ordinal))
                    UnityEngine.Object.DestroyImmediate(c);
            }
        }

        private static Button NewButton(Transform parent, string label, Color colour, float size, Action onClick)
        {
            var b = UnityEngine.Object.Instantiate(_buttonTemplate, parent, false);
            b.name = "Button " + label;
            foreach (var c in b.GetComponentsInChildren<languagetext>(true)) UnityEngine.Object.DestroyImmediate(c);
            b.onClick = new Button.ButtonClickedEvent(); b.onClick.AddListener(() => onClick());
            var icon = b.transform.Find("Image"); if (icon != null) icon.gameObject.SetActive(false);
            var t = b.GetComponentInChildren<TextMeshProUGUI>(true);
            t.text = label; t.color = colour; t.enableAutoSizing = true; t.fontSizeMax = size; t.fontSizeMin = 16f; t.enableWordWrapping = false;
            var colours = b.colors; colours.disabledColor = new Color(.86f, .84f, .78f, 1f); b.colors = colours;
            return b;
        }

        private static TextMeshProUGUI NewText(Transform parent, TextMeshProUGUI style, float size, Color colour, TextAlignmentOptions align, string name)
        {
            var go = new GameObject(name, typeof(RectTransform)); go.transform.SetParent(parent, false);
            var t = go.AddComponent<TextMeshProUGUI>();
            t.font = style.font; t.fontSharedMaterial = style.fontSharedMaterial;
            t.fontSize = size; t.color = colour; t.alignment = align; t.raycastTarget = false;
            t.enableWordWrapping = false; t.overflowMode = TextOverflowModes.Ellipsis; t.richText = true;
            return t;
        }

        private static void SetButton(Button b, string label, Color colour, bool on)
        {
            var t = b.GetComponentInChildren<TextMeshProUGUI>(true); t.text = label; t.color = on ? colour : new Color(colour.r, colour.g, colour.b, .45f);
            b.interactable = on;
        }

        // ---------------------------------------------------------------- the rows

        private struct RowState
        {
            internal string Head, Meta, Primary, Note; internal Color PrimaryColour; internal bool PrimaryOn;
            internal Action PrimaryDo; internal string Small; internal Color SmallColour; internal Action SmallDo;
            internal bool Remove;
        }

        /// Everything one row says and offers, from the same checks as the tapes had.
        private static RowState Describe(ModCatalog.Mod m, ModList.Stat st)
        {
            var r = new RowState();
            TileState(m, out string state, out string version, out Color colour);
            var v = ModCatalog.Offered(m); string reason = ModCatalog.Reason(m, v);
            string installed = null; bool manual = false; string status = "";
            try { installed = Installed(m, out manual); status = ModBrowser.State(m.Id) ?? ""; }
            catch (Exception e) { Diagnostics.QuietErrors.Once("ModsScreen.Describe", e); }
            bool core = ModActivation.Core(m.Id), busy = ModCatalog.Busy, mine = ModCatalog.DownloadingId == m.Id;
            bool update = installed != null && v != null && Newer(v.Version, installed);
            bool removed = status.StartsWith("Removed", StringComparison.Ordinal);
            bool disabled = !core && ModActivation.Desired(ModInstall.InstallDir, m.Id) == "disabled";

            var head = new StringBuilder();
            head.Append("<b>").Append(Esc(m.Name ?? m.Id)).Append("</b>");
            if (!string.IsNullOrEmpty(m.Author)) head.Append("  <size=28><color=#").Append(ColorUtility.ToHtmlStringRGB(Faded)).Append(">by ").Append(Esc(m.Author)).Append("</color></size>");
            head.Append("    <size=27><b><color=#").Append(ColorUtility.ToHtmlStringRGB(OnPaper(colour))).Append(">").Append(Esc(state)).Append("</color></b></size>");
            r.Head = head.ToString();

            var meta = new List<string>();
            if (m.Tags != null && m.Tags.Count > 0) meta.Add(Esc(string.Join(", ", m.Tags)));
            if (st.Downloads > 0) meta.Add(ModList.Short(st.Downloads) + " downloads");
            if (st.Published != default) meta.Add("updated " + ModList.Ago(st.Published, DateTime.UtcNow));
            if (v != null && v.Size > 0) meta.Add(ModInstall.Size(v.Size));
            if (!string.IsNullOrEmpty(version)) meta.Add(Esc(version));
            r.Meta = string.Join("     ·     ", meta);

            if (mine) { r.Primary = "Cancel"; r.PrimaryColour = ButtonRed; r.PrimaryOn = true; r.PrimaryDo = ModCatalog.Cancel; return r; }
            if (core) { r.Primary = "Built in"; r.PrimaryColour = ButtonGrey; return r; }
            if (manual) { r.Primary = "By hand"; r.PrimaryColour = ButtonGrey; r.Note = "Installed by hand: manage its files yourself."; return r; }
            if (removed)
            {
                // Until the next start its files are still in place, so Install only takes the removal back.
                bool files = ModInstall.ManagedFiles(m.Id).Count > 0;
                r.Primary = files ? "Install" : "Removed"; r.PrimaryColour = files ? ButtonGreen : ButtonGrey; r.PrimaryOn = files && !busy;
                if (files) r.PrimaryDo = () => KeepMod(m);
                r.Note = "Gone at the next start."; return r;
            }
            if (installed == null)
            {
                r.Primary = "Install"; r.PrimaryColour = ButtonGreen; r.PrimaryOn = !busy && reason == null;
                r.PrimaryDo = () => InstallMod(m);
                if (reason != null) r.Note = "Not available: " + reason;
                return r;
            }
            if (update)
            {
                r.Primary = "Update"; r.PrimaryColour = ButtonOrange; r.PrimaryOn = !busy && reason == null; r.PrimaryDo = () => InstallMod(m);
                r.Small = disabled ? "Enable" : "Disable"; r.SmallColour = disabled ? ButtonGreen : ButtonGrey; r.SmallDo = () => ToggleMod(m);
            }
            else
            {
                r.Primary = disabled ? "Enable" : "Disable"; r.PrimaryColour = disabled ? ButtonGreen : ButtonGrey; r.PrimaryOn = !busy;
                r.PrimaryDo = () => ToggleMod(m);
            }
            r.Remove = ModInstall.ManagedFiles(m.Id).Count > 0;
            return r;
        }

        /// The tapes' state colours were for a dark band; on the paper the same colours, darker.
        private static Color OnPaper(Color c)
        {
            if (c == On) return new Color32(0x2F, 0x7D, 0x32, 0xFF);
            if (c == Off) return new Color32(0xB8, 0x6A, 0x00, 0xFF);
            if (c == Update) return new Color32(0xB0, 0x6E, 0x00, 0xFF);
            if (c == Removing) return new Color32(0xB0, 0x3A, 0x2E, 0xFF);
            if (c == Unavailable) return new Color32(0x9A, 0x92, 0x80, 0xFF);
            return new Color32(0x6E, 0x6E, 0x6E, 0xFF);
        }

        private static void Row(ModCatalog.Mod m, RowState s)
        {
            var row = new GameObject("Mod " + m.Id, typeof(RectTransform), typeof(Image), typeof(LayoutElement));
            row.transform.SetParent(_rows, false);
            var img = row.GetComponent<Image>(); img.sprite = _paper; img.type = Image.Type.Sliced; img.color = Paper; img.raycastTarget = true;
            
            row.GetComponent<LayoutElement>().preferredHeight = 178f;
            var rt = (RectTransform)row.transform;

            // the tile: the mod's initials on a menu button face in a colour from its id
            var tile = (RectTransform)new GameObject("Tile", typeof(RectTransform), typeof(Image)).transform;
            tile.SetParent(rt, false); TopLeft(tile, 22f, 24f, 130f, 130f);
            var timg = tile.GetComponent<Image>(); timg.sprite = _buttonSprite; timg.type = Image.Type.Sliced;
            timg.color = Tiles8[ModList.Hue(m.Id) % Tiles8.Length]; timg.raycastTarget = false;
            var initials = NewText(tile, _paperStyle, 56f, Color.white, TextAlignmentOptions.Center, "Initials");
            Stretch(initials.transform); initials.fontStyle = FontStyles.Bold; initials.text = Esc(ModList.Initials(m.Name ?? m.Id));

            const float textLeft = 178f, textRight = 360f;
            var head = NewText(rt, _paperStyle, 42f, Ink, TextAlignmentOptions.MidlineLeft, "Head");
            Band(head.rectTransform, textLeft, textRight, 6f, 58f); head.text = s.Head;
            var desc = NewText(rt, _paperStyle, 27f, Body, TextAlignmentOptions.TopLeft, "Description");
            Band(desc.rectTransform, textLeft, textRight, 74f, 66f); desc.enableWordWrapping = true; desc.maxVisibleLines = 2;
            desc.text = Esc(string.IsNullOrEmpty(s.Note) ? m.Description : m.Description + "  " + s.Note);
            var meta = NewText(rt, _paperStyle, 24f, Faded, TextAlignmentOptions.MidlineLeft, "Numbers");
            Band(meta.rectTransform, textLeft, textRight, 141f, 30f); meta.text = s.Meta;

            var primary = NewButton(rt, s.Primary, s.PrimaryColour, 44f, () => { if (s.PrimaryDo != null) { s.PrimaryDo(); _shown = null; RefreshList(); } });
            TopRight((RectTransform)primary.transform, 24f, 18f, 310f, 84f);
            SetButton(primary, s.Primary, s.PrimaryColour, s.PrimaryOn);
            // the small buttons under it, right to left: Remove, Enable/Disable (no Source: asked 2026-11-20)
            float right = 24f;
            void Small(string label, Color colour, bool on, Action act)
            {
                var b = NewButton(rt, label, colour, 30f, () => { act(); _shown = null; RefreshList(); });
                TopRight((RectTransform)b.transform, right, 110f, 150f, 56f); SetButton(b, label, colour, on);
                right += 158f;
            }
            if (s.Remove) Small("Remove", ButtonRed, !ModCatalog.Busy, () => AskList("Remove " + (m.Name ?? m.Id) + " at the next start?", () => RemoveMod(m), null));
            if (s.Small != null) Small(s.Small, s.SmallColour, !ModCatalog.Busy, s.SmallDo);
        }

        // ---------------------------------------------------------------- what the buttons do

        private static void InstallMod(ModCatalog.Mod m)
        {
            if (ModCatalog.Busy) return;
            string installed = Installed(m, out bool manual);
            var v = ModCatalog.Offered(m);
            if (ModActivation.Core(m.Id) || manual || v == null || installed != null && !Newer(v.Version, installed)) return;
            ModCatalog.Download(m, v);
        }

        private static void ToggleMod(ModCatalog.Mod m)
        {
            if (ModCatalog.Busy) return;
            try
            {
                ModBrowser.Action(m.Id, ModActivation.Desired(ModInstall.InstallDir, m.Id) == "disabled" ? "enabled" : "disabled");
                ModCatalog.Message = (m.Name ?? m.Id) + ": " + ModBrowser.State(m.Id) + ".";
            }
            catch (Exception e) { ModCatalog.Message = "Could not change mod: " + e.Message; }
        }

        private static void KeepMod(ModCatalog.Mod m)
        {
            if (ModCatalog.Busy) return;
            try { ModBrowser.Action(m.Id, "enabled"); ModCatalog.Message = (m.Name ?? m.Id) + ": " + ModBrowser.State(m.Id) + "."; }
            catch (Exception e) { ModCatalog.Message = "Could not install: " + e.Message; }
        }

        private static void RemoveMod(ModCatalog.Mod m)
        {
            if (ModCatalog.Busy) return;
            Installed(m, out bool manual); if (manual) return;
            try { ModBrowser.Action(m.Id, "removed"); ModCatalog.Message = "Remove " + (m.Name ?? m.Id) + " at next start."; }
            catch (Exception e) { ModCatalog.Message = "Could not remove: " + e.Message; }
            _shown = null; RefreshList();
        }

        // ---------------------------------------------------------------- refreshing

        private static bool IsInstalled(ModCatalog.Mod m)
        {
            try { return ModActivation.Core(m.Id) || Installed(m, out _) != null; }
            catch (Exception e) { Diagnostics.QuietErrors.Once("ModsScreen.IsInstalled", e); return false; }
        }

        private static string _quick;          // what the rows depend on besides the disk (RefreshList)
        private static object _quickCatalog;
        private static float _diskAt;

        private static void RefreshList()
        {
            _refreshAt = Time.unscaledTime + 0.25f;
            if (_list == null) return;
            ModStatsFetch.Begin(false); ModStatsFetch.Poll();
            var sort = _sort != null ? (ModList.Sort)Mathf.Clamp(_sort.value, 0, 2) : ModList.Sort.Downloads;
            // Each row reads its mod's files (installed, enabled, pending): some twenty directory listings a mod, which
            // four times a second could stutter the pause menu in a session (review, 2026-11-20). The rows are read
            // again only when something changed - a button (it clears _shown), the search, tab or sort, a download
            // starting or ending, the catalog or the download numbers arriving - or every 2 s for changes made outside
            // the list. The message under the list (the download's progress) stays live.
            string quick = new StringBuilder().Append(_installedTab).Append('|').Append(_search?.text).Append('|').Append(sort).Append('|')
                .Append(ModStatsFetch.Version).Append('|').Append(ModCatalog.Busy).Append('|').Append(ModCatalog.DownloadingId).Append('|')
                .Append(ModCatalog.CatalogUnavailable).ToString();
            if (_shown != null && quick == _quick && ReferenceEquals(_quickCatalog, ModCatalog.Current) && Time.unscaledTime < _diskAt)
            { ListMessage(); return; }
            _quick = quick; _quickCatalog = ModCatalog.Current; _diskAt = Time.unscaledTime + 2f;
            var all = ModBrowser.Items();
            var known = new Dictionary<ModCatalog.Mod, bool>();   // each mod's files read once per refresh, not per use
            bool Has(ModCatalog.Mod m) { if (!known.TryGetValue(m, out bool on)) known[m] = on = IsInstalled(m); return on; }
            int installedCount = 0;
            foreach (var m in all) if (Has(m)) installedCount++;
            var shown = ModList.View(all, _search != null ? _search.text : "", _installedTab, Has, sort, ModStatsFetch.Stats);

            var states = new List<KeyValuePair<ModCatalog.Mod, RowState>>();
            var key = new StringBuilder().Append(_installedTab).Append('|').Append(_search?.text).Append('|').Append(sort).Append('|').Append(ModStatsFetch.Version).Append('|').Append(ModCatalog.Busy);
            foreach (var m in shown)
            {
                var s = Describe(m, ModList.ForMod(m, ModStatsFetch.Stats));
                states.Add(new KeyValuePair<ModCatalog.Mod, RowState>(m, s));
                key.Append('|').Append(m.Id).Append(s.Head).Append(s.Primary).Append(s.PrimaryOn).Append(s.Small).Append(s.Remove).Append(s.Note).Append(s.Meta);
            }
            string k = key.ToString();
            if (k != _shown)
            {
                _shown = k;
                for (int i = _rows.childCount - 1; i >= 0; i--) UnityEngine.Object.Destroy(_rows.GetChild(i).gameObject);
                _rows.DetachChildren();
                foreach (var s in states) Row(s.Key, s.Value);
                if (states.Count == 0)
                {
                    var empty = NewText(_rows, _whiteStyle, 36f, Color.white, TextAlignmentOptions.Center, "Empty");
                    empty.gameObject.AddComponent<LayoutElement>().preferredHeight = 160f;
                    empty.text = _installedTab && string.IsNullOrWhiteSpace(_search?.text) ? "No mods installed yet: they are under All mods." : "No mods match.";
                }
            }
            _listCount.text = shown.Count + " of " + all.Count + " mods";
            Tab(_tabAll, "All mods", !_installedTab);
            Tab(_tabInstalled, "Installed (" + installedCount + ")", _installedTab);
            ListMessage();
        }

        private static void ListMessage()
        {
            _listRefresh.interactable = !ModCatalog.Busy;
            string catalog = ModCatalog.CatalogUnavailable ? "The mod list could not be reached. " : "";
            // the reminder only when the message doesn't already say it ("Disabled (takes effect at next start)")
            string progress = ModCatalog.Progress ?? "";
            bool says = progress.IndexOf("next start", StringComparison.OrdinalIgnoreCase) >= 0 || progress.IndexOf("restart", StringComparison.OrdinalIgnoreCase) >= 0;
            _listMessage.text = catalog + progress + (says ? "" : (progress.Length == 0 ? "" : "\n") + "Changes take effect at the next start.");
        }

        private static void Tab(Button b, string label, bool on)
        {
            var t = b.GetComponentInChildren<TextMeshProUGUI>(true); t.text = label; t.color = on ? Ink : ButtonGrey;
            b.GetComponent<Image>().color = on ? ButtonFaceOn : ButtonFace;
        }

        /// For the bridge (mods view ...): set the search, tab and sort as a player would, and read back the rows.
        internal static string ListView(string search, string tab, int sort, float scroll = -1f, string ask = null, string press = null)
        {
            if (!ListOpen) return null;
            if (search != null) _search.text = search == "-" ? "" : search;
            if (tab != null) _installedTab = tab == "installed";
            if (sort >= 0 && _sort != null) _sort.value = sort;
            if (ask != null) AskList(ask, null, null);
            if (press != null)
            {
                // press=<mod id>:<button label>: that row's button, clicked as a player would
                int at = press.LastIndexOf(':');
                var row = at > 0 ? _rows.Find("Mod " + press.Substring(0, at)) : null;
                Button hit = null;
                if (row != null)
                    foreach (var b in row.GetComponentsInChildren<Button>(true))
                        if (b.GetComponentInChildren<TextMeshProUGUI>(true).text == press.Substring(at + 1)) hit = b;
                if (hit == null) return "no button " + press;
                if (!hit.interactable) return "button " + press + " can't be pressed now";
                hit.onClick.Invoke();
            }
            _shown = null; RefreshList();
            if (scroll >= 0f) { Canvas.ForceUpdateCanvases(); _scroll.verticalNormalizedPosition = Mathf.Clamp01(scroll); }
            var rows = new List<string>();
            for (int i = 0; i < _rows.childCount; i++)
            {
                var r = _rows.GetChild(i);
                var texts = r.GetComponentsInChildren<TextMeshProUGUI>(true);
                var parts = new List<string>();
                foreach (var t in texts) if (t.gameObject.activeInHierarchy && !string.IsNullOrEmpty(t.text)) parts.Add(t.name + "=" + t.text);
                rows.Add(r.name + " :: " + string.Join(" | ", parts));
            }
            return _listCount.text + "\n" + string.Join("\n", rows) + "\nmessage: " + _listMessage.text + "\nnumbers: " + ModStatsFetch.Stats.Count + " assets";
        }

        // ---------------------------------------------------------------- download numbers

        /// Downloads and release dates from GitHub's release lists of the repositories the catalog's mods come from, as
        /// the launcher shows them; each list is kept for an hour (BepInEx/cache/tldrevamp-modstats), and a list that
        /// can't be fetched just leaves its mods without numbers.
        internal static class ModStatsFetch
        {
            internal static readonly Dictionary<string, ModList.Stat> Stats = new Dictionary<string, ModList.Stat>(StringComparer.Ordinal);
            internal static int Version;
            private static readonly Queue<string> Pending = new Queue<string>();
            private static UnityWebRequest _req;
            private static string _repo;
            private static ModCatalog.Catalog _for;
            private const int MaxBytes = 4 << 20;
            private static readonly TimeSpan Keep = TimeSpan.FromHours(1);

            private static string CacheFile(string repo) => Path.Combine(BepInEx.Paths.CachePath, "tldrevamp-modstats", repo.Replace('/', '_') + ".json");

            /// once per catalog (or again for Refresh): load the kept lists, queue the old or missing ones
            internal static void Begin(bool force)
            {
                var c = ModCatalog.Current;
                if (c == null || (c == _for && !force) || DedicatedServer.Enabled) return;
                _for = c;
                foreach (string repo in ModList.Repos(c.Mods))
                {
                    string file = CacheFile(repo);
                    bool fresh = false;
                    try
                    {
                        if (File.Exists(file))
                        {
                            Merge(File.ReadAllBytes(file));
                            fresh = DateTime.UtcNow - File.GetLastWriteTimeUtc(file) < Keep;
                        }
                    }
                    catch (Exception e) { Plugin.Log.LogWarning("Mod numbers: kept list for " + repo + ": " + e.Message); }
                    if ((force || !fresh) && !Pending.Contains(repo)) Pending.Enqueue(repo);
                }
            }

            internal static void Poll()
            {
                if (_req == null)
                {
                    if (Pending.Count == 0) return;
                    _repo = Pending.Dequeue();
                    _req = UnityWebRequest.Get(ModList.ReleasesUrl(_repo));
                    _req.SetRequestHeader("Accept", "application/vnd.github+json");
                    _req.SetRequestHeader("User-Agent", "TLD-RevaMP");
                    _req.timeout = 20;
                    _req.SendWebRequest();
                    return;
                }
                if (!_req.isDone) return;
                try
                {
                    var data = _req.downloadHandler?.data;
                    if (_req.result == UnityWebRequest.Result.Success && data != null && data.Length <= MaxBytes)
                    {
                        Merge(data);
                        string file = CacheFile(_repo);
                        Directory.CreateDirectory(Path.GetDirectoryName(file));
                        File.WriteAllBytes(file, data);
                    }
                    else Plugin.Log.LogInfo("Mod numbers: " + _repo + ": " + (_req.error ?? "too large"));
                }
                catch (Exception e) { Plugin.Log.LogWarning("Mod numbers: " + _repo + ": " + e.Message); }
                finally { _req.Dispose(); _req = null; }
            }

            private static void Merge(byte[] json)
            {
                foreach (var kv in ModList.Parse(json)) Stats[kv.Key] = kv.Value;
                Version++;
            }
        }
    }
}
