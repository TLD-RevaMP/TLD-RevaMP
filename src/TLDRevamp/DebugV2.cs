using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace TLDRevamp
{
    /// Debugging V2 (Settings → Revamp → Debugging → Version: V2). One switch turns the whole debug overlay on — the
    /// performance graph, the network packets panel, the focus card, owner ghosts and links, and one tab directly above each
    /// nearby shared object with everything about it — instead of a dozen switches and separate labels stacked over an
    /// object. Right Shift (Keys → DebugLayout) edits it: every panel and every line of it on or off, the panels dragged,
    /// each panel's title, border (off, plain, coloured) and colours, and custom panels made of fields picked from a list.
    /// Saved as one JSON line in the settings file (debugV2Layout).
    ///
    /// Legacy keeps the old switches, untouched: V2 takes a snapshot of them when it turns on and puts them back when it
    /// turns off, and the settings file always keeps the Legacy values (Save writes the snapshot while V2 runs).
    internal static class DebugV2
    {
        internal static bool Style, On;   // settings: debugStyle (0 Legacy, 1 V2) and debugV2
        private static bool _hidden;
        /// No debug overlay is drawn, Legacy or V2, while every setting stays as it is: Settings → Debugging → Show debug
        /// overlays off, or (V2, the editor's Options) the game's F1 developer menu open — its windows fill the screen and
        /// the panels covered them.
        internal static bool Hidden { get => _hidden || Active && _cfg.hideInDevMenu && DevMenuOpen; set => _hidden = value; }
        internal static bool HiddenByHand => _hidden;
        private static bool DevMenuOpen => kaposztaleves.s != null && kaposztaleves.s.isOpen && settingsscript.s != null && settingsscript.s.S.BDevMode;
        internal static bool Active => Style && On;
        internal static bool Editing { get; private set; }
        internal const string LayoutKey = "debugV2Layout";

        // ---------------------------------------------------------------- the layout
        [Serializable]
        internal sealed class PanelCfg
        {
            public string id, name;
            public bool shown = true, title = true, customText;
            public int border = 1;                           // 0 off, 1 plain, 2 coloured (tabs: 2 = their status colour)
            public float x, y;                               // dragged by, pixels at 1080p
            public Color borderColour = new Color(.43f, .8f, .61f), titleColour = new Color(.35f, .76f, .9f);
            public List<string> off = new List<string>();    // lines switched off
            public List<string> fields = new List<string>(); // a custom panel's fields
        }
        [Serializable] internal sealed class LayoutCfg { public int version = 2; public bool hideInDevMenu = true, snap = true; public int grid = 10;
            // JsonUtility (in a mod assembly) drops a list of nested custom objects: each panel is saved as its own JSON text
            public List<string> panelJson = new List<string>();
            [NonSerialized] public List<PanelCfg> panels = new List<PanelCfg>(); }
        private static LayoutCfg _cfg = new LayoutCfg();

        private struct Kind { public string Id, Name; public string[] Lines, LineNames; public bool Title, Colours; }
        private static readonly Kind[] Builtins =
        {
            new Kind { Id = "perf", Name = "Performance", Title = true, Colours = true,
                Lines = new[] { "graph", "fps", "times", "stats", "keys", "mp", "net" },
                LineNames = new[] { "Frame time graph", "FPS and lows", "p50 / p99", "Hitches, GC, heap", "Key hints (hide · report)", "Multiplayer and ping", "Network down / up" } },
            new Kind { Id = "packets", Name = "Network packets", Title = true, Colours = true,
                Lines = new[] { "summary", "table" }, LineNames = new[] { "Summary line", "Message table" } },
            new Kind { Id = "card", Name = "Focus card", Title = true, Colours = true, Lines = new string[0], LineNames = new string[0] },
            new Kind { Id = "tabs", Name = "Tabs above objects", Colours = true,
                Lines = new[] { "name", "owner", "sync", "net", "cols" },
                LineNames = new[] { "Name and net id", "Owner, simulated or copy, epoch", "Sync (a copy's error) or send state", "Network use", "Colliders and parts" } },
            new Kind { Id = "ghosts", Name = "Owner ghosts", Lines = new string[0], LineNames = new string[0] },
            new Kind { Id = "links", Name = "Links", Lines = new string[0], LineNames = new string[0] },
            new Kind { Id = "health", Name = "Item health", Lines = new string[0], LineNames = new string[0] },
            new Kind { Id = "attach", Name = "Attach boxes", Lines = new string[0], LineNames = new string[0] },
            new Kind { Id = "options", Name = "Options", Lines = new string[0], LineNames = new string[0] },
        };

        internal static PanelCfg Get(string id)
        {
            foreach (var p in _cfg.panels) if (p.id == id) return p;
            var n = new PanelCfg { id = id };
            if (id == "tabs") n.border = 2;
            if (id == "attach") n.shown = false;   // a tool for one job, not part of the everyday overlay
            _cfg.panels.Add(n); return n;
        }
        internal static bool Has(string id) => Active && Get(id).shown;
        /// A line of a panel: always on in Legacy, else unless switched off.
        internal static bool Line(string id, string line) => !Active || !Get(id).off.Contains(line);

        /// OverlayStyle.Panel's look for a panel (title, border, colours) while V2 runs.
        internal static OverlayStyle.PanelLook? Look(OverlayTool tool)
        {
            if (!Active) return null;
            string id = tool == OverlayTool.Performance ? "perf" : tool == OverlayTool.Packets ? "packets"
                      : tool == OverlayTool.Focus || tool == OverlayTool.Problems || tool == OverlayTool.Everything || tool == OverlayTool.Compare || tool == OverlayTool.Events ? "card" : null;
            return id == null ? (OverlayStyle.PanelLook?)null : LookOf(Get(id));
        }
        private static OverlayStyle.PanelLook LookOf(PanelCfg p) =>
            new OverlayStyle.PanelLook { Title = p.title, Border = p.border, BorderColour = p.borderColour, TitleColour = p.titleColour };

        /// A tab over an object: its border (0 off, 1 plain, 2 its status colour) and text colour (null: its status colour).
        internal static int TabBorder => Get("tabs").border;
        internal static Color? TabText => Get("tabs").customText ? Get("tabs").titleColour : (Color?)null;

        // ---------------------------------------------------------------- legacy state while V2 runs
        private static bool _applied;
        private static DebugViewMode _mode; private static bool _own, _ghost, _ghostAll, _links, _perf, _packets, _traffic;

        /// After a setting changed (or the settings loaded): V2's state on, or the Legacy state back.
        internal static void Apply()
        {
            OverlayStyle.Looks = Look;
            if (!Active)
            {
                if (_applied) { Restore(); _applied = false; }
                if (Editing) StopEditing();
                return;
            }
            if (!_applied) { Snapshot(); _applied = true; }
            if (DebugView.Mode != DebugViewMode.Everything) DebugView.Mode = DebugViewMode.Everything;
            DebugView.Ownership = Get("tabs").shown;
            DebugView.Ghost = DebugView.GhostAll = Get("ghosts").shown;
            DebugView.Links = Get("links").shown;
            if (!ReferenceEquals(Plugin.Instance, null) && Plugin.Instance.Overlay != null) Plugin.Instance.Overlay.Visible = Get("perf").shown;
            Net.NetMeter.Show = Get("packets").shown;
            Net.ItemTraffic.Show = false;   // in the tabs now
        }
        private static void Snapshot()
        {
            _mode = DebugView.Mode; _own = DebugView.Ownership; _ghost = DebugView.Ghost; _ghostAll = DebugView.GhostAll; _links = DebugView.Links;
            _perf = !ReferenceEquals(Plugin.Instance, null) && Plugin.Instance.Overlay != null && Plugin.Instance.Overlay.Visible;
            _packets = Net.NetMeter.Show; _traffic = Net.ItemTraffic.Show;
        }
        private static void Restore()
        {
            DebugView.Mode = _mode; DebugView.Ownership = _own; DebugView.Ghost = _ghost; DebugView.GhostAll = _ghostAll; DebugView.Links = _links;
            if (!ReferenceEquals(Plugin.Instance, null) && Plugin.Instance.Overlay != null) Plugin.Instance.Overlay.Visible = _perf;
            Net.NetMeter.Show = _packets; Net.ItemTraffic.Show = _traffic;
        }
        /// RevampSettings.Save: the Legacy switches as they were before V2, so the file never keeps V2's.
        internal static void BeforeSave() { if (_applied) Restore(); }
        internal static void AfterSave() { if (_applied) Apply(); }

        internal static string LayoutText()
        {
            _cfg.panelJson = new List<string>();
            foreach (var p in _cfg.panels) _cfg.panelJson.Add(JsonUtility.ToJson(p));
            return JsonUtility.ToJson(_cfg);
        }
        internal static void LoadLayout(string text)
        {
            _cfg = new LayoutCfg();
            if (string.IsNullOrEmpty(text) || text[0] != '{') return;
            try
            {
                _cfg = JsonUtility.FromJson<LayoutCfg>(text) ?? new LayoutCfg();
                _cfg.panels = new List<PanelCfg>();
                if (_cfg.panelJson != null) foreach (var j in _cfg.panelJson) { var p = JsonUtility.FromJson<PanelCfg>(j); if (p != null && !string.IsNullOrEmpty(p.id)) _cfg.panels.Add(p); }
            }
            catch (Exception e) { Plugin.Log.LogWarning("debug layout unreadable, defaults used: " + e.Message); _cfg = new LayoutCfg(); }
            foreach (var p in _cfg.panels) { if (p.off == null) p.off = new List<string>(); if (p.fields == null) p.fields = new List<string>(); }
        }
        private static void SaveLayout() { RevampSettings.SetText(LayoutKey, LayoutText()); RevampSettings.Save(); }

        // ---------------------------------------------------------------- placing the panels
        /// Overlay.OnGUI, right after DebugLayout.Arrange: the dragged panels moved (kept on screen) and the label layout redone.
        internal static void AfterArrange()
        {
            if (!Active) return;
            float s = DebugLayout.Scale(Screen.height);
            Move(ref DebugLayout.Performance, Off("perf") * s);
            Move(ref DebugLayout.Packets, Off("packets") * s);
            var card = Off("card") * s; Move(ref DebugLayout.Card, card); Move(ref DebugLayout.Compare, card);
            DebugLayout.Begin();
            foreach (var b in _customBoxes) DebugLayout.Labels.Reserve(b);
        }
        private static Vector2 Off(string id) { var p = Get(id); return new Vector2(p.x, p.y); }
        /// A panel's dragged offset in screen pixels (ItemHealth places its panel with it).
        internal static Vector2 Offset(string id) => Off(id) * DebugLayout.Scale(Screen.height);
        private static void Move(ref DebugBox b, Vector2 by)
        {
            if (b.W <= 0 || b.H <= 0 || by == Vector2.zero) return;
            b.X = Mathf.Clamp(b.X + by.x, 0, Mathf.Max(0, Screen.width - b.W)); b.Y = Mathf.Clamp(b.Y + by.y, 0, Mathf.Max(0, Screen.height - b.H));
        }

        // ---------------------------------------------------------------- custom panels
        private struct Field { public string Key, Name; public Func<string> Text; }
        private static readonly Field[] Fields =
        {
            new Field { Key = "fps", Name = "FPS", Text = () => T == null ? "" : "fps  " + T.Fps.ToString("F0", IC) },
            new Field { Key = "avg", Name = "Average FPS", Text = () => T == null ? "" : "avg  " + T.AvgFps.ToString("F0", IC) },
            new Field { Key = "lows", Name = "1% and 0.1% lows", Text = () => { if (T == null) return ""; T.Lows(out float a, out float b); return "1% low  " + a.ToString("F0", IC) + "   0.1% low  " + b.ToString("F0", IC); } },
            new Field { Key = "p50", Name = "Frame time p50", Text = () => T == null ? "" : "p50  " + T.Percentile(.5f).ToString("F1", IC) + " ms" },
            new Field { Key = "p99", Name = "Frame time p99", Text = () => T == null ? "" : "p99  " + T.Percentile(.99f).ToString("F1", IC) + " ms" },
            new Field { Key = "hitches", Name = "Hitches", Text = () => T == null ? "" : "hitches  " + T.Hitches },
            new Field { Key = "gc", Name = "GC collections", Text = () => T == null ? "" : "GC  " + T.GcCollections },
            new Field { Key = "heap", Name = "Managed heap", Text = () => T == null ? "" : "heap  " + T.ManagedMB + " MB" },
            new Field { Key = "mp", Name = "Multiplayer and ping", Text = () => NetLine(0) },
            new Field { Key = "updown", Name = "Network down / up", Text = () => NetLine(1) },
            new Field { Key = "speed", Name = "Speed", Text = Speed },
            new Field { Key = "pos", Name = "Position (global)", Text = Position },
            new Field { Key = "aimed", Name = "Aimed object (name, owner)", Text = () => Aimed(false) },
            new Field { Key = "aimedcols", Name = "Aimed object's colliders", Text = () => Aimed(true) },
            new Field { Key = "objects", Name = "Shared objects here", Text = () => Net.Entities.InSession ? "objects  " + Net.Entities.SharedCount : "" },
            new Field { Key = "version", Name = "RevaMP version", Text = () => "RevaMP " + Plugin.Version },
        };
        private static readonly CultureInfo IC = CultureInfo.InvariantCulture;
        private static Telemetry T => !ReferenceEquals(Plugin.Instance, null) ? Plugin.Instance.Telemetry : null;
        private static string NetLine(int i)
        {
            string net = null; try { net = Net.Mp.NetStatsLine(); } catch { }
            if (net == null) return "";
            var parts = net.Replace("<b>", "").Replace("</b>", "").Replace("  down", "\nDown").Split('\n');
            return i < parts.Length ? parts[i] : "";
        }
        private static string Speed()
        {
            var pl = mainscript.s != null ? mainscript.s.player : null;
            if (pl == null) return "";
            if (pl.Car != null) return "speed  " + Mathf.Abs(pl.Car.speed).ToString("F0", IC) + " km/h";
            var rb = pl.GetComponent<Rigidbody>();
            return "speed  " + (rb != null ? rb.velocity.magnitude * 3.6f : 0f).ToString("F0", IC) + " km/h";
        }
        private static string Position()
        {
            var pl = mainscript.s != null ? mainscript.s.player : null;
            if (pl == null) return "";
            var g = mainscript.GlobalFromUnityPos(pl.transform.position);
            return "at  " + g.x.ToString("F0", IC) + ", " + g.z.ToString("F0", IC);
        }
        private static string Aimed(bool cols)
        {
            var e = Net.Entities.InSession ? Net.Entities.OverlayLook() : null;
            if (e == null || e.Root == null) return cols ? "" : "aimed  nothing shared";
            if (cols) return Net.Entities.OverlayColliders(e, true);
            return "aimed  " + Net.ItemTraffic.ObjectName(e.Root.name) + " · " + WorldOverlayLogic.Owner(e.OwnerId, Net.Entities.MyId) + (e.Proxy ? " · copy" : " · simulated here");
        }

        private sealed class Custom { public PanelCfg Cfg; public readonly GUIContent Text = new GUIContent(); public float Next, Height; }
        private static readonly List<Custom> _custom = new List<Custom>();
        private static readonly List<DebugBox> _customBoxes = new List<DebugBox>();
        private static bool IsCustom(string id) => id != null && id.StartsWith("c", StringComparison.Ordinal) && Array.FindIndex(Builtins, k => k.Id == id) < 0;
        private static void SyncCustom()
        {
            _custom.RemoveAll(c => !_cfg.panels.Contains(c.Cfg));
            foreach (var p in _cfg.panels) if (IsCustom(p.id) && _custom.Find(c => c.Cfg == p) == null) _custom.Add(new Custom { Cfg = p });
        }
        /// Overlay.OnGUI: the custom panels, stacked down the right side where nothing was dragged, 4 text refreshes a second.
        internal static void DrawCustom()
        {
            _customBoxes.Clear();
            if (!Active || Hidden) return;
            SyncCustom();
            float s = DebugLayout.Scale(Screen.height), w = 300 * s, top = 12 * s, now = Time.realtimeSinceStartup;
            foreach (var c in _custom)
            {
                var p = c.Cfg;
                if (!p.shown) { _customBoxes.Add(default); continue; }
                if (now >= c.Next)
                {
                    c.Next = now + .25f;
                    var lines = new List<string>();
                    foreach (var f in Fields) if (p.fields.Contains(f.Key)) { string t = ""; try { t = f.Text(); } catch { } if (!string.IsNullOrEmpty(t)) lines.Add(t); }
                    if (lines.Count == 0) lines.Add("Right Shift: pick fields for this panel");
                    c.Text.text = OverlayStyle.Truncate(string.Join("\n", lines), w - 2 * OverlayStyle.Padding);
                    c.Height = OverlayStyle.Height(c.Text, w - 2 * OverlayStyle.Padding);
                }
                var look = LookOf(p);
                float h = (look.Title ? OverlayStyle.Header : OverlayStyle.Padding) + c.Height + OverlayStyle.Padding;
                var box = new DebugBox(Screen.width - w - 12 * s, top, w, h); top += h + 8 * s;
                Move(ref box, new Vector2(p.x, p.y) * s);
                _customBoxes.Add(box);
                if (Event.current.type != EventType.Repaint) continue;
                var r = OverlayStyle.Panel(OverlayStyle.Rect(box), look, string.IsNullOrEmpty(p.name) ? "Custom" : p.name);
                OverlayStyle.Label(r, c.Text);
            }
        }

        // ---------------------------------------------------------------- Right Shift: the layout editor
        private static bool _cursorVisible; private static CursorLockMode _cursorLock;
        private static string _drag; private static Vector2 _dragFrom, _dragStart, _dragBox;
        private static string _selected = "perf"; private static bool _editTitle;
        internal static void Tick()
        {
            if (!Active) { if (Editing) StopEditing(); return; }
            if (Plugin.KeyDown(Plugin.KeyDebugLayout)) { if (Editing) StopEditing(); else StartEditing(); }
            if (!Editing) return;
            if (mainscript.s == null || mainscript.s.player == null || DataFromMenuScript.s == null || DataFromMenuScript.s.mainmenu) { StopEditing(); return; }
            Cursor.lockState = CursorLockMode.None; Cursor.visible = true;
        }
        private static void StartEditing() { _cursorVisible = Cursor.visible; _cursorLock = Cursor.lockState; Editing = true; _drag = null; }
        private static void StopEditing() { Editing = false; _drag = null; Cursor.lockState = _cursorLock; Cursor.visible = _cursorVisible; SaveLayout(); }

        private static readonly Color[] Palette =
        {
            new Color(.43f, .8f, .61f), new Color(.35f, .76f, .9f), new Color(.96f, .73f, .32f), new Color(.94f, .39f, .4f),
            new Color(.73f, .55f, .95f), new Color(.98f, .55f, .82f), new Color(.98f, .9f, .4f), new Color(.55f, .9f, .9f),
            new Color(.95f, .95f, .95f), new Color(.57f, .64f, .71f), new Color(.3f, .35f, .4f), new Color(.1f, .12f, .15f),
        };
        private static readonly Color Selected = new Color(.19f, .37f, .44f);

        /// Overlay.OnGUI, last: the editor window and the panels' drag frames.
        internal static void DrawEditor()
        {
            if (!Editing) return;
            OverlayStyle.Ensure();
            float s = DebugLayout.Scale(Screen.height);
            var e = Event.current;
            float w = 760 * s, line = 26 * s, h = 600 * s;
            var win = new Rect(Screen.width - w - 20 * s, Mathf.Max(10 * s, (Screen.height - h) / 2), w, h);
            bool overWindow = win.Contains(e.mousePosition);
            // drag frames: the built-in panels and the custom ones (not under the window: a click there is the window's)
            var frames = new List<KeyValuePair<string, DebugBox>>
            {
                new KeyValuePair<string, DebugBox>("perf", DebugLayout.Performance), new KeyValuePair<string, DebugBox>("packets", DebugLayout.Packets),
                new KeyValuePair<string, DebugBox>("card", DebugLayout.Card),
            };
            var hb = ItemHealth.Frame(); frames.Add(new KeyValuePair<string, DebugBox>("health", new DebugBox(hb.x, hb.y, hb.width, hb.height)));
            if (!ItemHealth.Open && e.type == EventType.Repaint)
            {
                OverlayStyle.Fill(hb, new Color(.055f, .07f, .09f, .6f));
                OverlayStyle.Label(new Rect(hb.x + 10 * s, hb.y + 8 * s, hb.width - 20 * s, line), Tmp("Item health (opens with " + Plugin.KeyName(Plugin.KeyItemHealth) + ") — drag to place it"), OverlayKind.Muted);
            }
            for (int i = 0; i < _custom.Count && i < _customBoxes.Count; i++) frames.Add(new KeyValuePair<string, DebugBox>(_custom[i].Cfg.id, _customBoxes[i]));
            foreach (var kv in frames)
            {
                var b = kv.Value; if (b.W <= 0 || b.H <= 0) continue;
                var r = new Rect(b.X, b.Y, b.W, b.H);
                if (e.type == EventType.Repaint)
                {
                    var c = kv.Key == _drag ? OverlayStyle.Warning : kv.Key == _selected ? OverlayStyle.Accent : OverlayStyle.Ok; c.a = .9f;
                    OverlayStyle.Outline(r, c, 2);
                }
                if (!overWindow && e.type == EventType.MouseDown && e.button == 0 && r.Contains(e.mousePosition))
                { _drag = kv.Key; _selected = kv.Key; _dragFrom = e.mousePosition; _dragStart = Off(kv.Key); _dragBox = new Vector2(b.X, b.Y); e.Use(); }
            }
            float grid = Mathf.Max(2, _cfg.grid) * s;
            if (_drag != null && _cfg.snap && e.type == EventType.Repaint)
            {
                var gc = new Color(.9f, .93f, .96f, .28f);
                for (float gx = 0; gx < Screen.width; gx += grid) OverlayStyle.Fill(new Rect(gx, 0, 1, Screen.height), gc);
                for (float gy = 0; gy < Screen.height; gy += grid) OverlayStyle.Fill(new Rect(0, gy, Screen.width, 1), gc);
            }
            if (_drag != null && e.type == EventType.MouseDrag)
            {
                // the panel's top-left corner where the mouse takes it, on the grid; the offset is what moves it there
                var at = _dragBox + (e.mousePosition - _dragFrom);
                if (_cfg.snap) at = new Vector2(Mathf.Round(at.x / grid) * grid, Mathf.Round(at.y / grid) * grid);
                var o = _dragStart + (at - _dragBox) / s; var p = Get(_drag); p.x = o.x; p.y = o.y; e.Use();
            }
            if (_drag != null && e.type == EventType.MouseUp) { _drag = null; SaveLayout(); e.Use(); }

            if (e.type == EventType.Repaint) { OverlayStyle.Back(win); OverlayStyle.Outline(win, OverlayStyle.Muted, 1); }
            var body = OverlayStyle.Body;
            OverlayStyle.Label(new Rect(win.x + 10 * s, win.y + 6 * s, w - 20 * s, line), Tmp("RevaMP · Debug layout   (drag the framed panels · " + Plugin.KeyName(Plugin.KeyDebugLayout) + " closes)"), OverlayKind.Accent);
            // left: the panels, each with its on/off box
            float lx = win.x + 10 * s, ly = win.y + 1.4f * line, lw = 230 * s;
            var ids = new List<KeyValuePair<string, string>>();
            foreach (var k in Builtins) ids.Add(new KeyValuePair<string, string>(k.Id, k.Name));
            SyncCustom();
            foreach (var c in _custom) ids.Add(new KeyValuePair<string, string>(c.Cfg.id, "· " + (string.IsNullOrEmpty(c.Cfg.name) ? "Custom" : c.Cfg.name)));
            foreach (var kv in ids)
            {
                var r = new Rect(lx, ly, lw, line - 2 * s);
                if (kv.Key == _selected && e.type == EventType.Repaint) OverlayStyle.Fill(r, Selected);
                if (kv.Key != "options" && kv.Key != "health")
                {
                    bool shown = Get(kv.Key).shown;
                    bool now = Box(new Rect(lx + 4 * s, ly, 22 * s, line - 2 * s), shown, "");
                    if (now != shown) { Get(kv.Key).shown = now; Apply(); SaveLayout(); }
                }
                if (GUI.Button(new Rect(lx + 28 * s, ly, lw - 28 * s, line - 2 * s), kv.Value, body)) _selected = kv.Key;
                ly += line;
            }
            if (OverlayStyle.Click(new Rect(lx, ly + 4 * s, lw, line), "+ New panel"))
            {
                int n = 1; while (_cfg.panels.Exists(p => p.id == "c" + n)) n++;
                var cfg = Get("c" + n); cfg.name = "Panel " + n; cfg.border = 2; cfg.fields.AddRange(new[] { "fps", "p99", "mp" });
                _selected = cfg.id; SyncCustom(); SaveLayout();
            }
            if (OverlayStyle.Click(new Rect(lx, win.yMax - line - 10 * s, lw, line), "Reset layout")) { LoadLayout(""); Apply(); SaveLayout(); _selected = "perf"; }
            // right: the selected panel's options
            float rx = lx + lw + 16 * s, rw = win.xMax - rx - 10 * s, ry = win.y + 1.4f * line;
            int bi = Array.FindIndex(Builtins, k => k.Id == _selected);
            var sel = Get(_selected); bool custom = bi < 0;
            var kind = custom ? new Kind { Id = sel.id, Name = sel.name, Title = true, Colours = true, Lines = new string[0], LineNames = new string[0] } : Builtins[bi];
            bool tabs = _selected == "tabs";
            OverlayStyle.Label(new Rect(rx, ry, rw, line), Tmp(custom ? "Custom panel" : kind.Name), OverlayKind.Accent); ry += line;
            if (custom)
            {
                GUI.Label(new Rect(rx, ry, 60 * s, line), "Name", body);
                string name = GUI.TextField(new Rect(rx + 60 * s, ry, rw - 170 * s, line - 2 * s), sel.name ?? "", 40);
                if (name != sel.name) { sel.name = name; SaveLayout(); }
                if (OverlayStyle.Click(new Rect(rx + rw - 100 * s, ry, 100 * s, line - 2 * s), "Delete")) { _cfg.panels.Remove(sel); _selected = "perf"; SyncCustom(); SaveLayout(); return; }
                ry += line * 1.2f;
            }
            if (_selected == "options")
            {
                bool on = _cfg.hideInDevMenu;
                bool now = Box(new Rect(rx, ry, rw, line), on, "Hide all overlays while the game's F1 menu is open");
                if (now != on) { _cfg.hideInDevMenu = now; SaveLayout(); }
                ry += line * 1.4f;
                bool snap = Box(new Rect(rx, ry, rw, line), _cfg.snap, "Snap panels to a grid while dragging");
                if (snap != _cfg.snap) { _cfg.snap = snap; SaveLayout(); }
                ry += line;
                GUI.Label(new Rect(rx + 26 * s, ry, 90 * s, line), "Grid size", body);
                int[] sizes = { 5, 10, 20, 40 };
                for (int i = 0; i < sizes.Length; i++)
                {
                    var r = new Rect(rx + 120 * s + i * 80 * s, ry, 74 * s, line - 2 * s);
                    if (_cfg.grid == sizes[i] && e.type == EventType.Repaint) OverlayStyle.Fill(r, Selected);
                    if (GUI.Button(r, sizes[i] + " px", body) && _cfg.grid != sizes[i]) { _cfg.grid = sizes[i]; SaveLayout(); }
                }
                ry += line * 1.2f;
                GUI.Label(new Rect(rx + 26 * s, ry, rw - 26 * s, line * 2), "Smaller is finer placement; sizes are at 1080p and scale with the screen.", body);
                return;
            }
            if (_selected == "attach")
            {
                GUI.Label(new Rect(rx, ry, rw, line * 6), "Wire boxes on every part slot near you (blue: empty). With a part in hand: its scan sphere (a bolted part attaches when a slot box that takes it is inside the sphere around the part's own origin, wherever you look), green = the box it attaches to now, yellow = takes it but out of reach, with the gap in metres.", OverlayStyle.Wrap);
                return;
            }
            if (_selected == "health")
            {
                GUI.Label(new Rect(rx, ry, rw, line * 3), "Opens on an object with " + Plugin.KeyName(Plugin.KeyItemHealth) + ". Drag its frame to place it; its border shows the worst check (green, amber, red).", OverlayStyle.Wrap);
                return;
            }
            if (!kind.Title && !kind.Colours && kind.Lines.Length == 0)
            { GUI.Label(new Rect(rx, ry, rw, line), "On or off only (the box on the left).", body); return; }
            if (kind.Title) ry = Check(rx, ry, rw, line, "Title", ref sel.title);
            if (kind.Colours)
            {
                GUI.Label(new Rect(rx, ry, 70 * s, line), "Border", body);
                string[] modes = tabs ? new[] { "Off", "Plain", "Status colour" } : new[] { "Off", "Plain", "Coloured" };
                for (int i = 0; i < 3; i++)
                {
                    var r = new Rect(rx + 70 * s + i * 120 * s, ry, 114 * s, line - 2 * s);
                    if (sel.border == i && e.type == EventType.Repaint) OverlayStyle.Fill(r, Selected);
                    if (GUI.Button(r, modes[i], body) && sel.border != i) { sel.border = i; SaveLayout(); }
                }
                ry += line * 1.2f;
                if (tabs) ry = Check(rx, ry, rw, line, "Own text colour for healthy tabs (problems stay amber/red, with ! or ✗)", ref sel.customText);
                bool showBorder = !tabs && sel.border == 2, showTitle = tabs ? sel.customText : sel.title;
                if (showBorder || showTitle)
                {
                    if (showBorder && showTitle)
                    {
                        if (GUI.Button(new Rect(rx, ry, 150 * s, line - 2 * s), (_editTitle ? "○ " : "● ") + "Border colour", body)) _editTitle = false;
                        if (GUI.Button(new Rect(rx + 160 * s, ry, 160 * s, line - 2 * s), (_editTitle ? "● " : "○ ") + "Title colour", body)) _editTitle = true;
                        ry += line;
                    }
                    bool title = showTitle && (!showBorder || _editTitle);
                    var colour = title ? sel.titleColour : sel.borderColour;
                    var picked = ColourPicker(rx, ref ry, rw, line, s, colour);
                    if (picked != colour) { if (title) sel.titleColour = picked; else sel.borderColour = picked; SaveLayout(); }
                }
            }
            if (kind.Lines.Length > 0)
            {
                GUI.Label(new Rect(rx, ry, rw, line), "Lines", body); ry += line;
                for (int i = 0; i < kind.Lines.Length; i++)
                {
                    bool on = !sel.off.Contains(kind.Lines[i]);
                    bool now = Box(new Rect(rx + (i % 2) * rw / 2, ry, rw / 2, line), on, kind.LineNames[i]);
                    if (now != on) { if (now) sel.off.Remove(kind.Lines[i]); else sel.off.Add(kind.Lines[i]); SaveLayout(); }
                    if (i % 2 == 1 || i == kind.Lines.Length - 1) ry += line;
                }
            }
            if (custom)
            {
                GUI.Label(new Rect(rx, ry, rw, line), "Fields", body); ry += line;
                for (int i = 0; i < Fields.Length; i++)
                {
                    bool on = sel.fields.Contains(Fields[i].Key);
                    bool now = Box(new Rect(rx + (i % 2) * rw / 2, ry, rw / 2, line), on, Fields[i].Name);
                    if (now != on) { if (now) sel.fields.Add(Fields[i].Key); else sel.fields.Remove(Fields[i].Key); SaveLayout(); foreach (var c in _custom) c.Next = 0; }
                    if (i % 2 == 1 || i == Fields.Length - 1) ry += line;
                }
            }
        }
        /// A checkbox that reads at a glance: an outlined square, filled green when on, and its label; the whole row clicks.
        private static bool Box(Rect r, bool value, string label)
        {
            float s = DebugLayout.Scale(Screen.height), size = 16 * s;
            var box = new Rect(r.x + 2 * s, r.y + (r.height - size) / 2, size, size);
            if (Event.current.type == EventType.Repaint)
            {
                OverlayStyle.Outline(box, value ? OverlayStyle.Ok : OverlayStyle.Muted, 1.5f * s);
                if (value) OverlayStyle.Fill(new Rect(box.x + 4 * s, box.y + 4 * s, box.width - 8 * s, box.height - 8 * s), OverlayStyle.Ok);
                if (!string.IsNullOrEmpty(label)) OverlayStyle.Label(new Rect(box.xMax + 8 * s, r.y + (r.height - OverlayStyle.Line) / 2, r.width - size - 10 * s, OverlayStyle.Line), Tmp(label), value ? OverlayKind.Text : OverlayKind.Muted);
            }
            if (GUI.Button(r, GUIContent.none, GUIStyle.none)) value = !value;
            return value;
        }
        private static readonly GUIContent _tmp = new GUIContent();
        private static GUIContent Tmp(string text) { _tmp.text = text; return _tmp; }
        private static float Check(float x, float y, float w, float line, string label, ref bool value)
        {
            bool now = Box(new Rect(x, y, w, line), value, label);
            if (now != value) { value = now; SaveLayout(); }
            return y + line;
        }
        /// Twelve swatches and R/G/B sliders, with a preview.
        private static Color ColourPicker(float x, ref float y, float w, float line, float s, Color colour)
        {
            float sw = 30 * s; bool paint = Event.current.type == EventType.Repaint;
            for (int i = 0; i < Palette.Length; i++)
            {
                var r = new Rect(x + i * (sw + 4 * s), y, sw, sw);
                if (paint) { OverlayStyle.Fill(r, Palette[i]); if (Palette[i] == colour) OverlayStyle.Outline(r, Color.white, 2); }
                if (GUI.Button(r, GUIContent.none, GUIStyle.none)) colour = Palette[i];
            }
            y += sw + 6 * s;
            if (paint) OverlayStyle.Fill(new Rect(x, y, 40 * s, 3 * line - 6 * s), colour);
            string[] names = { "R", "G", "B" };
            for (int i = 0; i < 3; i++)
            {
                GUI.Label(new Rect(x + 50 * s, y + i * line, 20 * s, line), names[i], OverlayStyle.Body);
                float v = GUI.HorizontalSlider(new Rect(x + 72 * s, y + i * line + 6 * s, w - 130 * s, line), colour[i], 0f, 1f);
                GUI.Label(new Rect(x + w - 50 * s, y + i * line, 50 * s, line), Mathf.RoundToInt(v * 255).ToString(IC), OverlayStyle.Body);
                if (Math.Abs(v - colour[i]) > 1e-4f) colour[i] = v;
            }
            y += 3 * line + 4 * s;
            colour.a = 1f; return colour;
        }

        /// `mp debugv2 [style v2|legacy | on|off | edit | show|hide <panel> | line <panel> <line> on|off | move <panel> <x/y> |
        /// border <panel> 0|1|2 <#rrggbb> | title <panel> on|off [#rrggbb] | text tabs on|off [#rrggbb] | new <name> <field,field> | reset]`
        /// (panels: perf packets card tabs ghosts links c1 c2 …).
        internal static string Command(string[] a)
        {
            if (a.Length >= 3 && a[1] == "style") { RevampSettings.SetChoice("debugStyle", a[2] == "v2" ? 1 : 0); RevampSettings.Save(); }
            else if (a.Length >= 2 && (a[1] == "on" || a[1] == "off")) { var t = RevampSettings.Entries.Find(x => x.Key == "debugV2"); t?.Set(a[1] == "on" ? 1 : 0); RevampSettings.Save(); }
            else if (a.Length >= 2 && a[1] == "edit") { if (Active && !Editing) StartEditing(); else if (Editing) StopEditing(); }
            else if (a.Length >= 3 && (a[1] == "show" || a[1] == "hide")) { Get(a[2]).shown = a[1] == "show"; Apply(); SaveLayout(); }
            else if (a.Length >= 5 && a[1] == "line") { var p = Get(a[2]); p.off.Remove(a[3]); if (a[4] == "off") p.off.Add(a[3]); SaveLayout(); }
            else if (a.Length >= 4 && a[1] == "move") { var xy = a[3].Split('/'); var p = Get(a[2]); p.x = float.Parse(xy[0], IC); p.y = float.Parse(xy[1], IC); SaveLayout(); }
            else if (a.Length >= 4 && a[1] == "border") { var p = Get(a[2]); p.border = int.Parse(a[3], IC); if (a.Length > 4 && ColorUtility.TryParseHtmlString(a[4], out var c)) p.borderColour = c; SaveLayout(); }
            else if (a.Length >= 4 && a[1] == "title") { var p = Get(a[2]); p.title = a[3] != "off"; if (a.Length > 4 && ColorUtility.TryParseHtmlString(a[4], out var c)) p.titleColour = c; SaveLayout(); }
            else if (a.Length >= 3 && a[1] == "grid") { if (a[2] == "off") _cfg.snap = false; else { _cfg.snap = true; _cfg.grid = int.Parse(a[2], IC); } SaveLayout(); }
            else if (a.Length >= 3 && a[1] == "devmenu") { _cfg.hideInDevMenu = a[2] != "off"; SaveLayout(); }
            else if (a.Length >= 4 && a[1] == "text") { var p = Get(a[2]); p.customText = a[3] != "off"; if (a.Length > 4 && ColorUtility.TryParseHtmlString(a[4], out var c)) p.titleColour = c; SaveLayout(); }
            else if (a.Length >= 4 && a[1] == "new")
            {
                int n = 1; while (_cfg.panels.Exists(p => p.id == "c" + n)) n++;
                var cfg = Get("c" + n); cfg.name = a[2].Replace('_', ' '); cfg.border = 2; cfg.fields.AddRange(a[3].Split(',')); SyncCustom(); SaveLayout();
            }
            else if (a.Length >= 2 && a[1] == "reset") { LoadLayout(""); Apply(); SaveLayout(); }
            return "{\"style\":" + Json.Str(Style ? "V2" : "Legacy") + ",\"on\":" + (On ? "true" : "false") + ",\"active\":" + (Active ? "true" : "false")
                   + ",\"editing\":" + (Editing ? "true" : "false") + ",\"layout\":" + LayoutText() + "}";
        }
    }
}
