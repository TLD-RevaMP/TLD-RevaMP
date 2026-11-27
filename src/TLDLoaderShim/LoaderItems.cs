using System;
using System.Collections.Generic;
#if TLD_UNITY
using System.Collections;
using System.IO;
using System.Reflection;
using TLDLoader.Extensions;
using UnityEngine;
#endif

// TLD Loader 3.0.3's item registry: a mod registers its own items in DbLoad (Mod.RegisterItem(prefab, id).Register()) and
// gets them back by id (Mod.GetItem). The public game knew an item by a category and an id, and 3.0.3 filed a mod's items
// under its Category (the FNV-1 of its ID). The beta knows an item by its place in itemdatabase.items alone (a save keeps
// tosaveitemscript.id, and the item comes back from itemdatabase.Item(id)): each registered item gets a place there,
// ItemSlots below, and its tosaveitemscript.id is that place. Mods still read their own numbers: LoaderRetarget points a
// mod's tosaveitemscript.id and .category at LoaderCompat, which answers from ItemSlots.
namespace TLDLoader
{
    /// Where registered items go in the beta's item table: from First, by a hash of (category, id), the next free place on a
    /// collision. The same mods registering the same items get the same places on every machine and in every session (the
    /// host runs DbLoad in the mods' ID order), so saves and multiplayer agree. A mod set where two items' hashes collide
    /// changes which of the two moves on when one of them comes or goes.
    public static class ItemSlots
    {
        /// The beta's own items end below 10000 (9400, 2026-11): far enough above that a game update doesn't reach them.
        public const int First = 16384, Count = 16384;

        private static readonly Dictionary<long, int> _slots = new Dictionary<long, int>();
        private static readonly Dictionary<int, long> _keys = new Dictionary<int, long>();
        private static readonly HashSet<int> _categories = new HashSet<int>();

        private static long Key(int category, int id) => ((long)category << 32) | (uint)id;

        /// FNV-1a over the two numbers' bytes, into [0, Count).
        public static int Hash(int category, int id)
        {
            unchecked
            {
                uint h = 2166136261;
                for (int i = 0; i < 4; i++) { h ^= (byte)(category >> (8 * i)); h *= 16777619; }
                for (int i = 0; i < 4; i++) { h ^= (byte)(id >> (8 * i)); h *= 16777619; }
                return (int)(h % Count);
            }
        }

        /// The item's place, given it now if it has none.
        public static int Assign(int category, int id) => Assign(category, id, true);

        private static int Assign(int category, int id, bool registered)
        {
            long key = Key(category, id);
            if (_slots.TryGetValue(key, out int slot)) return slot;
            if (_keys.Count >= Count) throw new InvalidOperationException("TLD Loader item registry: all " + Count + " places are taken");
            int h = Hash(category, id);
            while (_keys.ContainsKey(First + h)) h = (h + 1) % Count;
            slot = First + h;
            _slots[key] = slot; _keys[slot] = key;
            if (registered) _categories.Add(category); else _appended.Add(slot);
            return slot;
        }

        /// Items a mod puts in the item table itself (`items = items.Append(x).ToArray()`), not through RegisterItem: ModHost
        /// moves each to a place here after the mod's DbLoad, by the mod's ID, the item's name and how many of that name the
        /// mod added before it. At the end of the table their places moved whenever another mod came or went, and a save
        /// keeps an item by its place (a snowman mod's parts came back as another mod's items). Not a registered item's place
        /// (TryKey says no), so a mod reads the item's id there as it is. The idea of a hashed, mod-keyed id is Roadworthy's
        /// (TldLoaderCompatPolicy.ModItemId, MIT, github.com/jaylikesbunda/TheLongDrive-Roadworthy); the code is RevaMP's.
        private const int AppendedCategory = 0x41505044;   // "APPD": no mod's (an FNV-1 of its ID) in practice
        private static readonly HashSet<int> _appended = new HashSet<int>();

        public static int AssignAppended(string modId, string name, int ordinal)
        {
            unchecked
            {
                uint h = 2166136261;
                foreach (char c in (modId ?? "") + "\n" + (name ?? "") + "#" + ordinal) { h ^= (byte)c; h *= 16777619; h ^= (byte)(c >> 8); h *= 16777619; }
                return Assign(AppendedCategory, (int)(h | 0x80000000u), false);
            }
        }

        /// The item's place, or -1 when it has none.
        public static int Find(int category, int id) => _slots.TryGetValue(Key(category, id), out int slot) ? slot : -1;

        /// The registered item at this place of the item table, if any.
        public static bool TryKey(int slot, out int category, out int id)
        {
            category = 0; id = 0;
            if (slot < First || slot >= First + Count || _appended.Contains(slot) || !_keys.TryGetValue(slot, out long key)) return false;
            category = (int)(key >> 32); id = (int)(key & 0xffffffffL);
            return true;
        }

        public static bool HasCategory(int category) => _categories.Contains(category);
        public static int Assigned => _slots.Count;

        /// A new item database: the mods register again, in the same order, and get the same places.
        public static void Clear() { _slots.Clear(); _keys.Clear(); _categories.Clear(); _appended.Clear(); }
    }

    /// TLD Loader 3.0.3's FNV-1 (Mod.Category is this of the mod's ID).
    public static class Hashing
    {
        public static int FNV1(string str) => ModLoader.Fnv1(str);
    }

#if TLD_UNITY
    public class Item
    {
        public int Id { get; set; }
        public GameObject Prefab { get; set; }
        public Item(int id, GameObject prefab) { Id = id; Prefab = prefab; }
    }

    /// ModLoader.Database: what the mods registered, by mod. A registered prefab also stands in the beta's item table at its
    /// ItemSlots place, so the game spawns, saves and loads it as one of its own.
    public class ItemDatabase
    {
        private readonly Dictionary<Mod, List<Item>> _items = new Dictionary<Mod, List<Item>>();
        /// Every loaded mod's category (3.0.3: the categories its patches answer for).
        internal List<int> Categories = new List<int>();
        /// The registered prefabs' parent: inactive, kept across scenes.
        internal Transform PrefabHolder;

        public ItemDatabase()
        {
            var go = new GameObject("Database");
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.SetActive(false);
            PrefabHolder = go.transform;
        }

        internal void RegisterItem(Mod mod, GameObject prefab, int id)
        {
            if (GetItemById(mod.Category, id) != null) throw new InvalidOperationException($"[{mod.ID}] [{id}] ID already registered.");
            if (!_items.TryGetValue(mod, out var list)) _items.Add(mod, list = new List<Item>());
            list.Add(new Item(id, prefab));
            Place(mod.Category, id, prefab);
        }

        /// Into the item table at the item's place (the table grows to it), its tosaveitemscript.id that place.
        private static void Place(int category, int id, GameObject prefab)
        {
            int slot = ItemSlots.Assign(category, id);
            var t = prefab.GetComponent<tosaveitemscript>();
            if (t != null) t.id = slot;
            var db = itemdatabase.s;
            if (db == null || db.items == null) return;
            if (db.items.Length <= slot) Array.Resize(ref db.items, slot + 1);
            db.items[slot] = prefab;
        }

        public Item GetItemById(int category, int id)
        {
            foreach (var kv in _items)
                if (kv.Key.Category == category)
                    foreach (var item in kv.Value) if (item.Id == id) return item;
            return null;
        }

        public GameObject GetById(int category, int id) => GetItemById(category, id)?.Prefab;

        public List<GameObject> GetAllItems()
        {
            var all = new List<GameObject>();
            foreach (var list in _items.Values) foreach (var item in list) all.Add(item.Prefab);
            return all;
        }

        /// A new tosaveitemscript on the prefab (null if it has one): its collections empty, its id the item's place. The
        /// beta's parts point back at it (pickupable.tosave, attachablescript.tosave): they get it too.
        public tosaveitemscript MakeSaveable(Mod mod, int id, GameObject prefab)
        {
            if (prefab.GetComponent<tosaveitemscript>() != null) return null;
            var t = prefab.AddComponent<tosaveitemscript>();
            foreach (var f in typeof(tosaveitemscript).GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                if (f.GetValue(t) != null || f.IsInitOnly) continue;
                if (f.FieldType.IsArray) f.SetValue(t, Array.CreateInstance(f.FieldType.GetElementType(), 0));
                else if (f.FieldType.IsGenericType && f.FieldType.GetGenericTypeDefinition() == typeof(List<>)) f.SetValue(t, Activator.CreateInstance(f.FieldType));
            }
            t.id = ItemSlots.Assign(mod.Category, id);
            t.RB = prefab.GetComponent<Rigidbody>();
            var p = prefab.GetComponent<pickupable>();
            if (p != null) { t.P = p; p.tosave = t; }
            var a = prefab.GetComponent<attachablescript>();
            if (a != null) { t.attachable = a; a.tosave = t; }
            return t;
        }

        internal void ResetDatabase() => _items.Clear();
    }

    public class PickupableOptions
    {
        public bool Attachable { get; set; } = true;
        public bool CanInventory { get; set; } = true;
        public bool CanEquip { get; set; }
        public Sprite InventoryIcon { get; set; }
        public Vector3 RightHandPosition { get; set; } = Vector3.zero;
        public Vector3 RightHandRotation { get; set; } = Vector3.zero;
    }

    /// Mod.RegisterItem's builder, as 3.0.3's: a copy of the prefab under the database's holder, made saveable; the With/As
    /// calls give it a rigidbody, the compass's pickupable and attach parts, a tank; Register() files it. The parts the
    /// public game had and the beta folded elsewhere: pickupable.attach is tosaveitemscript.attachable, attachablescript.C
    /// a private array, a tank's id SetID. Its conditions, part slots and world spawns (WithCondition, AsPart, SpawnAt,
    /// SpawnInBox) are not here: no known mod calls them.
    public class ItemRegistrationBuilder
    {
        private readonly Mod _mod;
        private readonly GameObject _prefab;
        private readonly int _id;
        private List<Collider> _colliders = new List<Collider>();
        private readonly tosaveitemscript _toSave;
        private massScript _mass;
        private Rigidbody _rb;
        private pickupable _pickup;
        private attachablescript _attachable;
        private tankscript _tank;
        private bool _generateInventoryIcon = true;

        internal ItemRegistrationBuilder(Mod mod, GameObject prefab, int id, string name = null)
        {
            if (prefab == null) throw new ArgumentNullException($"[{mod.ID}] [Id {id}] Prefab cannot be null.");
            _mod = mod;
            _prefab = UnityEngine.Object.Instantiate(prefab, ModLoader.Database.PrefabHolder);
            _id = id;
            _prefab.name = !string.IsNullOrEmpty(name) ? name : prefab.name;
            var old = _prefab.GetComponent<tosaveitemscript>();
            if (old != null) UnityEngine.Object.DestroyImmediate(old);
            _toSave = ModLoader.Database.MakeSaveable(mod, id, _prefab);
            var place = Compass.GetComponent<visszarako>();
            if (_prefab.GetComponent<visszarako>() == null && place != null) _prefab.CopyComponent(place);
        }

        private static GameObject Compass => itemdatabase.s.gww2compass;

        /// The compass's copied lists are the compass prefab's own list objects (the public game's were arrays, which the game
        /// didn't change): each its own copy, so what the game does to the item's leaves the compass's alone.
        private static T Unshared<T>(T c) where T : Component
        {
            foreach (var f in c.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                if (f.FieldType.IsGenericType && f.FieldType.GetGenericTypeDefinition() == typeof(List<>) && f.GetValue(c) is IEnumerable items)
                    f.SetValue(c, Activator.CreateInstance(f.FieldType, items));
            return c;
        }

        public ItemRegistrationBuilder WithRigidbody(float mass, float drag = 0f, float angularDrag = 0.05f)
        {
            _rb = _prefab.GetComponent<Rigidbody>();
            if (_rb == null) _rb = _prefab.AddComponent<Rigidbody>();
            _rb.mass = mass; _rb.drag = drag; _rb.angularDrag = angularDrag;
            _mass = _prefab.GetComponent<massScript>();
            if (_mass == null) _mass = _prefab.AddComponent<massScript>();
            _mass.SetMass(mass);
            _toSave.RB = _rb;
            return this;
        }

        public ItemRegistrationBuilder AsPickupable(PickupableOptions options = null)
        {
            if (options == null) options = new PickupableOptions();
            if (_rb == null) throw new MissingComponentException($"[{_mod.ID}] [Id {_id}] Prefab must have a Rigidbody to be pickupable.");
            if (_tank != null) throw new InvalidOperationException($"[{_mod.ID}] [Id {_id}] AsPickupable() must be called before AsTank() or AsExplodable().");
            _pickup = Unshared(_prefab.CopyComponent(Compass.GetComponent<pickupable>()));
            _pickup.mass = _mass;
            _mass.P = _pickup;
            _pickup.RB = _rb;
            _pickup.tosave = _toSave;
            _toSave.P = _pickup;
            _pickup.canInventory = options.CanInventory;
            _pickup.canHoldRightHand = options.CanEquip;
            _pickup.RHP = options.RightHandPosition;
            _pickup.RHR = options.RightHandRotation;
            if (options.InventoryIcon != null) { _pickup.invImg = options.InventoryIcon; _generateInventoryIcon = false; }
            if (_prefab.GetComponentsInChildren<layerScript>().Length == 0)
            {
                var layers = new List<layerScript>();
                _colliders = new List<Collider>(_prefab.GetComponentsInChildren<Collider>());
                foreach (var c in _colliders)
                {
                    var l = c.gameObject.AddComponent<layerScript>();
                    l.col = c; l.p = _pickup;
                    layers.Add(l);
                }
                _pickup.cols = layers;
            }
            if (options.Attachable)
            {
                var from = Compass.GetComponent<attachablescript>();
                _attachable = from != null ? Unshared(_prefab.CopyComponent(from)) : _prefab.AddComponent<attachablescript>();
                AttachColliders?.SetValue(_attachable, _colliders.ToArray());
                _attachable.tosave = _toSave;
                _toSave.attachable = _attachable;
            }
            return this;
        }

        // attachablescript.C: a private Collider[] on the beta (a public List in the public game)
        private static readonly FieldInfo AttachColliders = typeof(attachablescript).GetField("C", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

        public ItemRegistrationBuilder AsTank(mainscript.fluidcontainer fluids = null, bool allowSpawnRandomFluids = true, bool canDrinkFrom = true)
        {
            if (_tank != null) throw new InvalidOperationException($"[{_mod.ID}] [Id {_id}] Object already has a tank. Please call either AsTank() or AsExplodable(), not both .");
            _tank = _prefab.AddComponent<tankscript>();
            _tank.alwaysDefaultFluid = !allowSpawnRandomFluids;
            _tank.noDrink = !canDrinkFrom;
            _tank.F = fluids;
            _tank.SetID(_toSave.idInSave, 0);
            if (_pickup != null) _pickup.tank = _tank;
            _toSave.tanks = new List<tankscript> { _tank };
            return this;
        }

        public ItemRegistrationBuilder AsExplodable()
        {
            if (_tank != null) throw new InvalidOperationException($"[{_mod.ID}] [Id {_id}] Object already has a tank. Please call either AsTank() or AsExplodable(), not both.");
            var f = new mainscript.fluidcontainer { maxC = 1f };
            f.ChangeOne(1f, 0);
            return AsTank(f, false, false);
        }

        /// The beta's breakablescript has no noGib.
        public ItemRegistrationBuilder AsBreakable(float health, float shootHealth, float explosionHealth = 3f)
        {
            var b = _prefab.AddComponent<breakablescript>();
            b.health = health; b.shootHealth = shootHealth; b.explosionHealth = explosionHealth; b.noTrigger = true;
            return this;
        }

        public ItemRegistrationBuilder AddComponent<T>() where T : Component
        {
            _prefab.AddComponent<T>();
            return this;
        }

        public void Register()
        {
            if (ModLoader.Instance != null && ModLoader.Instance.dbmodsloaded)
                throw new InvalidOperationException($"[{_mod.ID}] [Id {_id}] RegisterItem must be called from within DbLoad().");
            ModLoader.Database.RegisterItem(_mod, _prefab, _id);
            if (_pickup != null && _generateInventoryIcon && ModLoader.Instance != null)
            {
                var p = _pickup;
                ModLoader.Instance.StartCoroutine(IconGenerator.GetOrGenerate(_prefab, _mod.Category + "_" + _id, sprite => p.invImg = sprite));
            }
        }
    }

    /// A registered pickupable's inventory picture, as 3.0.3 draws it: the item from the side, 180x70, kept in the loader's
    /// Cache folder (<category>_<id>.png; mods look there). The item drawn is a copy with only its meshes: the game's
    /// scripts on a whole copy would make it a world item (save, multiplayer) for the frame it lives.
    internal static class IconGenerator
    {
        private const int Width = 180, Height = 70;
        private static string CachePath => Path.Combine(ModLoader.LoaderFolder, "Cache");

        public static IEnumerator GetOrGenerate(GameObject prefab, string cacheKey, Action<Sprite> onComplete)
        {
            string file = Path.Combine(CachePath, cacheKey + ".png");
            if (File.Exists(file))
            {
                var cached = Load(file);
                if (cached != null) { onComplete(cached); yield break; }
            }
            var holder = new GameObject("IconHolder");
            holder.SetActive(false);
            var copy = UnityEngine.Object.Instantiate(prefab, holder.transform);
            // scripts first, then joints, then what they need (a component another requires refuses to go before it)
            for (int pass = 0; pass < 3; pass++)
                foreach (var c in copy.GetComponentsInChildren<Component>(true))
                    if (c != null && !(c is Transform) && !(c is MeshFilter) && !(c is Renderer) && (pass > 0 || c is Behaviour) && (pass > 1 || !(c is Rigidbody)))
                        UnityEngine.Object.DestroyImmediate(c);
            copy.transform.SetParent(null, false);
            copy.transform.position = new Vector3(UnityEngine.Random.Range(-100f, 100f), -9999f, UnityEngine.Random.Range(-100f, 100f));
            UnityEngine.Object.Destroy(holder);
            copy.SetActive(true);
            var rt = new RenderTexture(Width, Height, 16) { antiAliasing = 4 };
            var camObj = new GameObject("IconCamera");
            var cam = camObj.AddComponent<Camera>();
            cam.backgroundColor = Color.clear;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.targetTexture = rt;
            cam.fieldOfView = 30f;
            Frame(cam, copy);
            yield return null;
            Sprite sprite = null;
            try
            {
                cam.Render();
                RenderTexture.active = rt;
                var tex = new Texture2D(Width, Height, TextureFormat.ARGB32, false);
                tex.ReadPixels(new Rect(0f, 0f, Width, Height), 0, 0);
                tex.Apply();
                RenderTexture.active = null;
                sprite = Sprite.Create(tex, new Rect(0f, 0f, Width, Height), new Vector2(0.5f, 0.5f));
                Save(tex, file);
            }
            finally
            {
                UnityEngine.Object.Destroy(camObj);
                UnityEngine.Object.Destroy(copy);
                rt.Release();
            }
            onComplete(sprite);
        }

        private static void Frame(Camera cam, GameObject item)
        {
            var renderers = item.GetComponentsInChildren<Renderer>();
            if (renderers.Length == 0)
            {
                cam.transform.position = item.transform.position + new Vector3(0f, 0f, -1f);
                cam.transform.LookAt(item.transform.position);
                return;
            }
            var bounds = renderers[0].bounds;
            foreach (var r in renderers) bounds.Encapsulate(r.bounds);
            float aspect = cam.aspect = (float)Width / Height;
            var dir = new Vector3(1f, -0.3f, -1f).normalized;
            float half = cam.fieldOfView * 0.5f * Mathf.Deg2Rad;
            float dist = Mathf.Max(bounds.extents.magnitude / Mathf.Tan(half), bounds.extents.magnitude / Mathf.Tan(half * aspect));
            cam.transform.position = bounds.center - dir * dist;
            cam.transform.LookAt(bounds.center);
            cam.nearClipPlane = 0.01f;
            cam.farClipPlane = dist * 3f;
        }

        private static Sprite Load(string file)
        {
            try
            {
                var tex = new Texture2D(Width, Height, TextureFormat.ARGB32, false);
                if (!ImageConversion.LoadImage(tex, File.ReadAllBytes(file))) return null;
                return Sprite.Create(tex, new Rect(0f, 0f, tex.width, tex.height), new Vector2(0.5f, 0.5f));
            }
            catch { return null; }
        }

        private static void Save(Texture2D tex, string file)
        {
            try { Directory.CreateDirectory(Path.GetDirectoryName(file)); File.WriteAllBytes(file, ImageConversion.EncodeToPNG(tex)); }
            catch (Exception e) { ModLoader.Echo?.Invoke("TLD Loader: an inventory picture was not kept: " + e.Message, 1); }
        }
    }
#endif
}
