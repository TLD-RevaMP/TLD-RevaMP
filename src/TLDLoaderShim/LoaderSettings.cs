using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
#if TLD_UNITY
using UnityEngine;
#endif

namespace TLDLoader
{
    /// A mod's saved settings: one value per setting id (the title, lower case, spaces as '_', as TLD Loader names
    /// them), in Settings.txt in the mod's config folder, one "id<TAB>kind<TAB>value" line each. Free of Unity, so the
    /// unit test runs it.
    public sealed class SettingStore
    {
        private readonly string _path;
        private readonly Dictionary<string, object> _values = new Dictionary<string, object>();
        public string LastError = "";

        public SettingStore(string path) { _path = path; Load(); }

        public static string IdOf(string title) => (title ?? "").Replace(' ', '_').ToLowerInvariant();

        public bool TryGet(string title, out object value) => _values.TryGetValue(IdOf(title), out value);

        /// Stores the value and writes the file, only when it changed.
        public void Set(string title, object value)
        {
            string id = IdOf(title);
            if (_values.TryGetValue(id, out var old) && Equals(old, value)) return;
            _values[id] = value;
            Save();
        }

        private void Load()
        {
            if (!File.Exists(_path)) return;
            try
            {
                foreach (var line in File.ReadAllLines(_path, Encoding.UTF8))
                {
                    var p = line.Split('\t');
                    if (p.Length != 3) continue;
                    object v = Parse(p[1], Unescape(p[2]));
                    if (v != null) _values[p[0]] = v;
                }
            }
            catch (Exception e) { LastError = "reading " + _path + ": " + e.Message; }
        }

        private void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path));
                var sb = new StringBuilder();
                foreach (var kv in _values) sb.Append(kv.Key).Append('\t').Append(Kind(kv.Value)).Append('\t').Append(Escape(Text(kv.Value))).Append('\n');
                File.WriteAllText(_path + ".tmp", sb.ToString(), Encoding.UTF8);
                if (File.Exists(_path)) File.Delete(_path);
                File.Move(_path + ".tmp", _path);
            }
            catch (Exception e) { LastError = "writing " + _path + ": " + e.Message; }
        }

        private static string Kind(object v) => v is bool ? "bool" : v is int ? "int" : v is float ? "float" : v is Enum ? "key" : "string";
        private static string Text(object v) => v is float f ? f.ToString("R", CultureInfo.InvariantCulture) : v is Enum e ? Convert.ToInt32(e).ToString(CultureInfo.InvariantCulture) : Convert.ToString(v, CultureInfo.InvariantCulture);
        private static object Parse(string kind, string s)
        {
            var ci = CultureInfo.InvariantCulture;
            switch (kind)
            {
                case "bool": return bool.TryParse(s, out var b) ? (object)b : null;
                case "int": case "key": return int.TryParse(s, NumberStyles.Integer, ci, out var i) ? (object)i : null;
                case "float": return float.TryParse(s, NumberStyles.Float, ci, out var f) ? (object)f : null;
                default: return s;
            }
        }
        private static string Escape(string s) => (s ?? "").Replace("\\", "\\\\").Replace("\t", "\\t").Replace("\n", "\\n").Replace("\r", "\\r");
        private static string Unescape(string s)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] != '\\' || i + 1 >= s.Length) { sb.Append(s[i]); continue; }
                char c = s[++i];
                sb.Append(c == 't' ? '\t' : c == 'n' ? '\n' : c == 'r' ? '\r' : c);
            }
            return sb.ToString();
        }
    }

#if TLD_UNITY
    /// TLD Loader's settings helpers. A mod calls them inside Config(): the host calls Config() once at load, where each
    /// one only returns the saved value (or the default), and then while the settings window is open, where it draws its
    /// control at (posx, posy) and saves a change. Same signatures and behaviour as TLD Loader's; RevaMP's own code.
    public class SettingAPI
    {
        private readonly Mod _mod;
        private readonly SettingStore _store;

        public SettingAPI(Mod mod)
        {
            _mod = mod;
            _store = new SettingStore(Path.Combine(ModLoader.GetModSettingsFolder(mod), "Settings.txt"));
        }

        private bool Drawing => _mod.isConfigLoaded;

        public bool GUICheckbox(bool toggle, string text, int posx, int posy)
        {
            if (_store.TryGet(text, out var v) && v is bool b) toggle = b;
            if (!Drawing) return toggle;
            toggle = GUI.Toggle(new Rect(posx, posy, 430f, 20f), toggle, text);
            _store.Set(text, toggle);
            return toggle;
        }

        public bool GUIButtonToggle(bool toggle, string text, int posx, int posy)
        {
            if (_store.TryGet(text, out var v) && v is bool b) toggle = b;
            if (!Drawing) return toggle;
            if (GUI.Button(new Rect(posx, posy, 430f, 20f), toggle ? "<color=#0F0>" + text + "</color>" : text)) toggle = !toggle;
            _store.Set(text, toggle);
            return toggle;
        }

        public float GUISlider(string title, float val, float valmin, float valmax, int posx, int posy) => GUISlider(title, val, valmin, valmax, posx, posy, 2);

        public float GUISlider(string title, float val, float valmin, float valmax, int posx, int posy, int precision = 2)
        {
            if (_store.TryGet(title, out var v)) val = Convert.ToSingle(v, CultureInfo.InvariantCulture);
            if (!Drawing) return val;
            GUI.Box(new Rect(posx - 3, posy - 10, 430f, 80f), title);
            GUI.Label(new Rect(posx + 15, posy + 40, 250f, 30f), valmin.ToString(CultureInfo.InvariantCulture));
            GUI.Label(new Rect(posx + 380, posy + 40, 250f, 30f), valmax.ToString(CultureInfo.InvariantCulture));
            if (float.TryParse(GUI.TextField(new Rect(posx + 160, posy + 43, 100f, 20f), val.ToString(CultureInfo.InvariantCulture)), NumberStyles.Float, CultureInfo.InvariantCulture, out var typed)) val = typed;
            val = Mathf.Clamp(val, valmin, valmax);
            float s = (float)Math.Round(GUI.HorizontalSlider(new Rect(posx + 15, posy + 30, 400f, 30f), val, valmin, valmax), Math.Max(0, precision));
            _store.Set(title, s);
            return s;
        }

        public int GUISliderInt(string title, int val, int valmin, int valmax, int posx, int posy) =>
            Mathf.RoundToInt(GUISlider(title, val, valmin, valmax, posx, posy, 0));

        public KeyCode GUIKeyBinding(KeyCode key, string name, int posx, int posy)
        {
            if (_store.TryGet(name, out var v) && v is int k) key = (KeyCode)k;
            if (!Drawing) return key;
            GUI.Box(new Rect(posx - 3, posy - 10, 430f, 80f), "");
            GUI.Label(new Rect(posx + 120, posy + 25, 300f, 40f), "Hold a key, then click this button");
            if (GUI.Button(new Rect(posx, posy + 30, 100f, 30f), "Change key"))
                foreach (KeyCode c in Enum.GetValues(typeof(KeyCode)))
                    if (c != KeyCode.None && c != KeyCode.Mouse0 && Input.GetKey(c)) { key = c; break; }
            GUI.Label(new Rect(posx, posy, 430f, 20f), "Key: " + name + ": " + key);
            _store.Set(name, (int)key);   // kept as its number: a read-back int then equals it, no rewrite every frame
            return key;
        }

        public void GUIDescriptionText(string text, int height, int posx, int posy)
        {
            if (Drawing) GUI.TextArea(new Rect(posx - 3, posy - 10, 430f, height), text);
        }

        public void GUILabel(string text, int height, int posx, int posy)
        {
            if (Drawing) GUI.Label(new Rect(posx - 3, posy - 10, 430f, height), text);
        }

        public string GUITextArea(string title, string text, int height, int posx, int posy)
        {
            if (_store.TryGet(title, out var v) && v is string s) text = s;
            if (!Drawing) return text;
            GUI.Box(new Rect(posx - 3, posy - 10, 430f, 20f), title);
            text = GUI.TextArea(new Rect(posx - 3, posy + 10, 430f, height), text ?? "");
            _store.Set(title, text);
            return text;
        }

        public int GUIDropdown(string title, List<string> options, int selected, int posx, int posy)
        {
            if (_store.TryGet(title, out var v) && v is int i) selected = i;
            if (!Drawing || options == null) return selected;
            GUI.Box(new Rect(posx - 3, posy - 10, 430f, 20f + options.Count * 25), title);
            for (int k = 0; k < options.Count; k++)
                if (GUI.Button(new Rect(posx - 3, posy + 15 + k * 25, 430f, 25f), selected == k ? "<color=#0F0>" + options[k] + "</color>" : options[k])) selected = k;
            _store.Set(title, selected);
            return selected;
        }
    }
#endif
}
