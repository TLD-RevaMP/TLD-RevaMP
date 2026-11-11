using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
#if TLD_UNITY
using UnityEngine;
#endif

// Enough of TLDLoader's API for TLDLoader mods to run under BepInEx on the beta (where TLDLoader itself doesn't work):
// the Mod base class and its lifecycle, its logger, the config and assets folders, LoadAssets (asset bundles,
// textures, OBJ meshes), and its helpers (LoaderSettings.cs: SettingAPI; LoaderLogging.cs: Logger; LoaderExtras.cs:
// Keybind, Events, GameUtil, ComponentExtensions). TLDRevamp's ModHost finds the mods, creates them and calls their
// lifecycle: Config once at load, UseHarmony patches, DbLoad/dbLoad once the item database exists (mods with LoadInDB),
// OnMenuLoad in the menu, OnLoad once a game is loaded.
// The API is TLD Loader's, member for member as the workshop's mods call it (2026-11-11: every TLDLoader member the
// 224 workshop DLLs reference, except item registration); the behaviour was checked against TLD Loader's, the code is
// RevaMP's own.
namespace TLDLoader
{
    public interface ILogger
    {
        void Log(string msg, Logger.LogLevel level = Logger.LogLevel.Info);
        void LogDebug(string msg);
        void LogInfo(string msg);
        void LogWarning(string msg);
        void LogError(string msg);
        void LogCritical(string msg);
    }

    /// The host's logger (BepInEx), handed over as three callbacks — TLDRevamp only reaches this assembly by reflection.
    /// Debug goes out as info, critical as an error.
    public sealed class HostLogger : ILogger
    {
        private readonly Action<string> _info, _warn, _error;
        public HostLogger(Action<string> info, Action<string> warn, Action<string> error) { _info = info; _warn = warn; _error = error; }
        public void Log(string msg, Logger.LogLevel level = Logger.LogLevel.Info)
        {
            switch (level)
            {
                case Logger.LogLevel.Warning: _warn?.Invoke(msg); break;
                case Logger.LogLevel.Error: case Logger.LogLevel.Critical: _error?.Invoke(msg); break;
                default: _info?.Invoke(msg); break;
            }
        }
        public void LogDebug(string msg) => _info?.Invoke(msg);
        public void LogInfo(string msg) => _info?.Invoke(msg);
        public void LogWarning(string msg) => _warn?.Invoke(msg);
        public void LogError(string msg) => _error?.Invoke(msg);
        public void LogCritical(string msg) => _error?.Invoke(msg);
    }

    public abstract class Mod
    {
        public virtual string ID => GetType().Name;
        public virtual string Name => ID;
        public virtual string Version => "";
        public virtual string Author => "";
        public virtual bool LoadInMenu => false;
        /// The mod changes or adds items in the item database: DbLoad/dbLoad run once it exists, before a world loads.
        public virtual bool LoadInDB => false;
        /// The mod has files in Assets/<ID> next to the mods (LoadAssets, GetModAssetsFolder).
        public virtual bool UseAssetsFolder => false;
        public virtual float ConfigScrollHeight => 0f;
        /// The host patches the mod's own assembly with Harmony (PatchAll) before its first lifecycle call.
        public virtual bool UseHarmony => false;
        /// The mod gets its own log file (Logs/<ID>/<ID>_<date>.log next to the mods) as its Logger.
        public virtual bool UseLogger => false;
        /// With UseLogger: also write each line to the game's log (BepInEx's here).
        public virtual bool EchoToGameLog => true;
        /// A number from the mod's ID (FNV-1, as TLD Loader computes it), the same on every machine.
        public int Category => ModLoader.Fnv1(ID);

        /// Set by the host before any lifecycle call.
        public ILogger Logger { get; set; }

        /// False during the host's first Config() call at load, where SettingAPI only reads the saved values; true once
        /// Config() draws the settings window.
        internal bool isConfigLoaded;

#if TLD_UNITY
        /// The mod's Harmony instance once HarmonyPatch ran (a HarmonyLib.Harmony; typed object so the base class never
        /// needs Harmony loaded to exist — no workshop mod reads this field).
        public object HarmonyInstance;

        /// Patches the mod's assembly (the host calls it for UseHarmony mods; a mod may call it itself).
        public void HarmonyPatch()
        {
            var h = new HarmonyLib.Harmony("com." + ID.ToLowerInvariant());
            HarmonyInstance = h;
            h.PatchAll(GetType().Assembly);
        }
#endif

        public virtual void DbLoad() { }
        public virtual void dbLoad() { }   // TLDLoader's older name: both are called
        public virtual void OnMenuLoad() { }
        public virtual void OnLoad() { }
        public virtual void OnUnload() { }
        public virtual void Config() { }
        public virtual void Update() { }
        public virtual void FixedUpdate() { }
        public virtual void OnGUI() { }
    }

    public static class ModLoader
    {
        /// Set by the host: BepInEx/config/TLDLoaderMods.
        public static string ConfigRoot = "";
        /// Set by the host: the folder the mods are loaded from (BepInEx/TLDLoaderMods); their assets are in its Assets/<ID>.
        public static string ModsFolder = "";

        public static string GetModConfigFolder(Mod mod)
        {
            string dir = Path.Combine(ConfigRoot, mod.ID);
            Directory.CreateDirectory(dir);
            return dir;
        }

        public static string GetModAssetsFolder(Mod mod) => Path.Combine(Path.Combine(ModsFolder, "Assets"), mod.ID);

        /// Where SettingAPI keeps a mod's settings: its config folder.
        public static string GetModSettingsFolder(Mod mod) => GetModConfigFolder(mod);

        /// A UseLogger mod's log files: Logs/<ID> next to the mods.
        public static string GetModLogFolder(Mod mod) => Path.Combine(Path.Combine(ModsFolder, "Logs"), mod.ID);

        /// Every mod the host loaded, in load order.
        public static List<Mod> LoadedMods { get; } = new List<Mod>();

        /// True while no world is up (the host keeps it current, as TLD Loader does: mainscript doesn't exist then).
        public static bool isOnMainMenu = true;

        public enum CurrentScene { MainMenu, Game }
        internal static CurrentScene CurrentGameScene = CurrentScene.MainMenu;
        public static CurrentScene GetCurrentScene() => CurrentGameScene;

        /// The host: a line for the game's log at a level (0 info, 1 warning, 2 error) — a UseLogger mod's echo.
        public static Action<string, int> Echo;

        // ---- the host's side (TLDRevamp's ModHost calls these by reflection)
        internal static void HostAdd(Mod mod) { if (!LoadedMods.Contains(mod)) LoadedMods.Add(mod); }
        internal static void HostScene(bool inWorld) { isOnMainMenu = !inWorld; CurrentGameScene = inWorld ? CurrentScene.Game : CurrentScene.MainMenu; }
        internal static void HostConfigPass(Mod mod, bool drawing) => mod.isConfigLoaded = drawing;

        internal static int Fnv1(string s)
        {
            unchecked
            {
                int h = -2128831035;
                foreach (char c in s ?? "") { h ^= c; h *= 16777619; }
                return h;
            }
        }

#if TLD_UNITY
        /// TLD Loader's "loading mods" panel: some mods check whether it is showing. Here it is an inactive object the
        /// host makes once Unity runs, so `loading.activeSelf` reads false instead of throwing.
        public static GameObject loading;
#endif
    }

#if TLD_UNITY
    /// A mod's own files from its assets folder (ModLoader.GetModAssetsFolder).
    public static class LoadAssets
    {
        private static string File(Mod mod, string fileName, string what)
        {
            string path = Path.Combine(ModLoader.GetModAssetsFolder(mod), fileName);
            if (!System.IO.File.Exists(path)) throw new FileNotFoundException(what + ": " + mod.ID + " has no file " + fileName + " in " + ModLoader.GetModAssetsFolder(mod), path);
            return path;
        }

        public static AssetBundle LoadBundle(Mod mod, string bundleName) =>
            AssetBundle.LoadFromMemory(System.IO.File.ReadAllBytes(File(mod, bundleName, "LoadBundle")));

        /// .dds (DXT1/DXT5), .tga (uncompressed 24/32-bit), .png and .jpg.
        public static Texture2D LoadTexture(Mod mod, string fileName, bool normalMap = false)
        {
            string path = File(mod, fileName, "LoadTexture");
            byte[] data = System.IO.File.ReadAllBytes(path);
            Texture2D t;
            switch (Path.GetExtension(path).ToLowerInvariant())
            {
                case ".dds": t = Dds(data); break;
                case ".tga": t = Tga(data); break;
                case ".png": case ".jpg": case ".jpeg":
                    t = new Texture2D(2, 2, TextureFormat.RGBA32, true, normalMap);
                    if (!ImageConversion.LoadImage(t, data)) throw new InvalidDataException("LoadTexture: " + fileName + " is not a readable image");
                    break;
                default: throw new NotSupportedException("LoadTexture: " + fileName + ": .dds, .tga, .png or .jpg");
            }
            t.name = Path.GetFileName(path);
            return t;
        }

        private static Texture2D Dds(byte[] d)
        {
            // DDS: "DDS " magic, a 124-byte header; the pixel format's FourCC at byte 84, height at 12, width at 16, data after 128
            if (d.Length < 128 || d[0] != 'D' || d[1] != 'D' || d[2] != 'S' || BitConverter.ToInt32(d, 4) != 124) throw new InvalidDataException("LoadTexture: not a DDS file");
            string fourCc = System.Text.Encoding.ASCII.GetString(d, 84, 4);
            var format = fourCc == "DXT1" ? TextureFormat.DXT1 : fourCc == "DXT5" ? TextureFormat.DXT5 : throw new NotSupportedException("LoadTexture: DDS " + fourCc + " (DXT1 or DXT5 only)");
            int h = BitConverter.ToInt32(d, 12), w = BitConverter.ToInt32(d, 16);
            int mips = Math.Max(1, BitConverter.ToInt32(d, 28));
            var t = new Texture2D(w, h, format, mips > 1);
            var raw = new byte[d.Length - 128];
            Buffer.BlockCopy(d, 128, raw, 0, raw.Length);
            t.LoadRawTextureData(raw);
            t.Apply(false);
            return t;
        }

        private static Texture2D Tga(byte[] d)
        {
            // TGA: an 18-byte header (image type at 2, width at 12, height at 14, bits per pixel at 16, descriptor at 17),
            // then the image id and pixels (BGR or BGRA), bottom-up unless descriptor bit 5 says top-down
            if (d.Length < 18 || d[2] != 2) throw new NotSupportedException("LoadTexture: TGA must be uncompressed true colour");
            int w = BitConverter.ToUInt16(d, 12), h = BitConverter.ToUInt16(d, 14), bpp = d[16] / 8, at = 18 + d[0];
            if (bpp != 3 && bpp != 4) throw new NotSupportedException("LoadTexture: TGA must be 24 or 32 bit");
            bool topDown = (d[17] & 0x20) != 0;
            var px = new Color32[w * h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++, at += bpp)
                    px[(topDown ? h - 1 - y : y) * w + x] = new Color32(d[at + 2], d[at + 1], d[at], bpp == 4 ? d[at + 3] : (byte)255);
            var t = new Texture2D(w, h, TextureFormat.RGBA32, true);
            t.SetPixels32(px);
            t.Apply();
            return t;
        }

        /// A game object with the .obj file's mesh (MeshFilter, MeshRenderer, optionally a collider and a rigidbody).
        public static GameObject LoadObj(Mod mod, string fileName, string name, bool collider = true, bool rigidbody = false)
        {
            var mesh = LoadOBJMesh(mod, fileName);
            var go = new GameObject(name);
            go.AddComponent<MeshFilter>().mesh = mesh;
            go.AddComponent<MeshRenderer>();
            if (rigidbody) go.AddComponent<Rigidbody>();
            if (collider) { var mc = go.AddComponent<MeshCollider>(); mc.sharedMesh = mesh; mc.convex = rigidbody; }
            return go;
        }

        /// A Wavefront .obj's first object: positions, texture coordinates, normals; polygons fanned into triangles.
        public static Mesh LoadOBJMesh(Mod mod, string fileName)
        {
            string path = File(mod, fileName, "LoadOBJ");
            if (Path.GetExtension(path).ToLowerInvariant() != ".obj") throw new NotSupportedException("LoadOBJ: .obj files only");
            var mesh = ObjMesh.Parse(System.IO.File.ReadAllLines(path));
            mesh.name = Path.GetFileNameWithoutExtension(path);
            return mesh;
        }
    }

    /// The .obj reader behind LoadOBJMesh (its own class so the unit test runs it without the game).
    public static class ObjMesh
    {
        public static Mesh Parse(IEnumerable<string> lines)
        {
            Build(lines, out var verts, out var uvs, out var normals, out var tris);
            var m = new Mesh();
            if (verts.Count > 65535) m.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            m.SetVertices(verts);
            if (uvs.Count == verts.Count) m.SetUVs(0, uvs);
            if (normals.Count == verts.Count) m.SetNormals(normals); else m.RecalculateNormals();
            m.SetTriangles(tris, 0);
            m.RecalculateBounds();
            return m;
        }

        /// One output vertex per distinct "v/vt/vn" corner; faces with more than three corners become a fan.
        public static void Build(IEnumerable<string> lines, out List<Vector3> verts, out List<Vector2> uvs, out List<Vector3> normals, out List<int> tris)
        {
            var v = new List<Vector3>(); var vt = new List<Vector2>(); var vn = new List<Vector3>();
            verts = new List<Vector3>(); uvs = new List<Vector2>(); normals = new List<Vector3>(); tris = new List<int>();
            var corners = new Dictionary<string, int>();
            var ci = CultureInfo.InvariantCulture;
            foreach (var raw in lines)
            {
                var line = raw.Trim();
                if (line.Length == 0 || line[0] == '#') continue;
                var p = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                switch (p[0])
                {
                    // .obj is right-handed, Unity left-handed: mirror x (and reverse the winding below)
                    case "v": v.Add(new Vector3(-float.Parse(p[1], ci), float.Parse(p[2], ci), float.Parse(p[3], ci))); break;
                    case "vt": vt.Add(new Vector2(float.Parse(p[1], ci), p.Length > 2 ? float.Parse(p[2], ci) : 0f)); break;
                    case "vn": vn.Add(new Vector3(-float.Parse(p[1], ci), float.Parse(p[2], ci), float.Parse(p[3], ci))); break;
                    case "f":
                        var face = new List<int>();
                        for (int i = 1; i < p.Length; i++)
                        {
                            if (!corners.TryGetValue(p[i], out int idx))
                            {
                                var s = p[i].Split('/');
                                verts.Add(v[Index(s[0], v.Count)]);
                                if (s.Length > 1 && s[1].Length > 0) uvs.Add(vt[Index(s[1], vt.Count)]);
                                if (s.Length > 2 && s[2].Length > 0) normals.Add(vn[Index(s[2], vn.Count)]);
                                idx = verts.Count - 1;
                                corners[p[i]] = idx;
                            }
                            face.Add(idx);
                        }
                        for (int i = 1; i + 1 < face.Count; i++) { tris.Add(face[0]); tris.Add(face[i + 1]); tris.Add(face[i]); }
                        break;
                }
            }
        }

        private static int Index(string s, int count)
        {
            int i = int.Parse(s, CultureInfo.InvariantCulture);
            return i < 0 ? count + i : i - 1;   // 1-based; negative counts back from the latest
        }
    }
#endif
}
