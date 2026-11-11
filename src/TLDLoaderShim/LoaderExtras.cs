using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
#if TLD_UNITY
using UnityEngine;
#endif

namespace TLDLoader
{
    public class GameUtil
    {
        /// A private instance field of a game object's script, read by name.
        public static T GetPrivateField<T>(object instance, string name) =>
            (T)instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(instance);
    }

#if TLD_UNITY
    /// TLD Loader's older key bindings (the loader marks them obsolete in favour of SettingAPI.GUIKeyBinding): a key and
    /// an optional modifier, read with Unity's Input.
    public class Keybind
    {
        public static List<Keybind> Keybinds = new List<Keybind>();
        public static List<Keybind> DefaultKeybinds = new List<Keybind>();

        public string ID { get; set; }
        public string Name { get; set; }
        public KeyCode Key { get; set; }
        public KeyCode Modifier { get; set; }
        public Mod Mod { get; set; }
        public object[] Vals { get; set; }

        public Keybind(string id, string name, KeyCode key) : this(id, name, key, KeyCode.None) { }
        public Keybind(string id, string name, KeyCode key, KeyCode modifier) { ID = id; Name = name; Key = key; Modifier = modifier; }

        public static void Add(Mod mod, Keybind key)
        {
            key.Mod = mod;
            Keybinds.Add(key);
            DefaultKeybinds.Add(new Keybind(key.ID, key.Name, key.Key, key.Modifier) { Mod = mod });
        }
        public static void AddHeader(Mod mod, string HeaderTitle) => AddHeader(mod, HeaderTitle, Color.blue, Color.white);
        public static void AddHeader(Mod mod, string HeaderTitle, Color backgroundColor) => AddHeader(mod, HeaderTitle, backgroundColor, Color.white);
        public static void AddHeader(Mod mod, string HeaderTitle, Color backgroundColor, Color textColor) =>
            Keybinds.Add(new Keybind(null, HeaderTitle, KeyCode.None) { Mod = mod, Vals = new object[] { HeaderTitle, backgroundColor, textColor } });
        public static List<Keybind> Get(Mod mod) => Keybinds.FindAll(x => x.Mod == mod);
        public static List<Keybind> GetDefault(Mod mod) => DefaultKeybinds.FindAll(x => x.Mod == mod);

        private bool Mod_() => Modifier == KeyCode.None || Input.GetKey(Modifier);
        public bool GetKeybind() => Mod_() && Input.GetKey(Key);
        public bool GetKeybindDown() => Mod_() && Input.GetKeyDown(Key);
        public bool GetKeybindUp() => Mod_() && Input.GetKeyUp(Key);
    }

    /// TLD Loader's game events. TLDRevamp raises them from the game's own calls (ModHost's patches): sitting down and
    /// standing up (seatscript.GetIn/GetOut), saving, loading and deleting a save, the pause menu (the cursor shown).
    public static class Events
    {
        public static event Action<seatscript> OnSit;
        public static event Action<seatscript> OnStand;
        public static event Action<string> OnGameSave;
        public static event Action<string> OnSaveLoad;
        public static event Action<string> OnSaveDelete;
        public static event Action OnPause;
        public static event Action OnUnpause;

        // one mod's handler that throws must not stop the others, nor the game's call that raised it
        private static void Each(Delegate d, Action<Delegate> call)
        {
            if (d == null) return;
            foreach (var h in d.GetInvocationList())
                try { call(h); } catch (Exception e) { ModLoader.Echo?.Invoke("TLD Loader event handler failed: " + (e.InnerException ?? e).Message, 2); }
        }
        internal static void RaiseOnSit(seatscript seat) => Each(OnSit, h => ((Action<seatscript>)h)(seat));
        internal static void RaiseOnStand(seatscript seat) => Each(OnStand, h => ((Action<seatscript>)h)(seat));
        internal static void RaiseOnSave(string name) => Each(OnGameSave, h => ((Action<string>)h)(name));
        internal static void RaiseOnSaveLoad(string name) => Each(OnSaveLoad, h => ((Action<string>)h)(name));
        internal static void RaiseOnSaveDelete(string name) => Each(OnSaveDelete, h => ((Action<string>)h)(name));
        internal static void RaiseOnPause() => Each(OnPause, h => ((Action)h)());
        internal static void RaiseOnUnpause() => Each(OnUnpause, h => ((Action)h)());
    }

    /// TLD Loader's mod list window. Nothing here is that window; the type exists so a mod that looks for it (and finds
    /// none) still loads.
    public class ModList : MonoBehaviour { }
#endif
}

#if TLD_UNITY
namespace TLDLoader.Extensions
{
    public static class ComponentExtensions
    {
        /// A copy of `other` added to the object: a component of the same type with its fields and writable properties.
        public static T CopyComponent<T>(this GameObject go, T toAdd) where T : Component =>
            go.AddComponent(toAdd.GetType()).GetCopyOf(toAdd);

        /// `comp` takes `other`'s fields and writable properties (same type only; null otherwise). Shared-asset
        /// properties (sharedMaterial...) are left to their non-shared twins' copies, obsolete ones and a rigidbody's
        /// inertia tensor are skipped, as TLD Loader does.
        public static T GetCopyOf<T>(this Component comp, T other) where T : Component
        {
            var type = comp.GetType();
            if (type != other.GetType()) return null;
            var chain = new List<Type>();
            for (var b = type; b != null && b != typeof(MonoBehaviour) && b != typeof(Behaviour) && b != typeof(Component) && b != typeof(UnityEngine.Object); b = b.BaseType) chain.Add(b);
            const BindingFlags all = BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var props = chain.SelectMany(t => t.GetProperties(all))
                .Where(p => p.CanWrite && p.CanRead && p.GetIndexParameters().Length == 0)
                .Where(p => !(type == typeof(Rigidbody) && p.Name == "inertiaTensor"))
                .Where(p => !p.IsDefined(typeof(ObsoleteAttribute), true)).ToList();
            foreach (var p in props)
            {
                if (props.Any(q => q.Name == "shared" + char.ToUpperInvariant(p.Name[0]) + p.Name.Substring(1))) continue;
                try { p.SetValue(comp, p.GetValue(other, null), null); } catch { }
            }
            foreach (var f in chain.SelectMany(t => t.GetFields(all)))
                try { f.SetValue(comp, f.GetValue(other)); } catch { }
            return comp as T;
        }
    }
}
#endif
