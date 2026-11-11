using System;
using System.Collections.Generic;
using System.IO;
using HarmonyLib;
using UnityEngine;

namespace TLDRevamp.Net
{
    /// Cars' tunings (CarTuning) kept with the world: the game's save has no place for them, so a tuned car came back
    /// stock after every load - also in single player, where the tuners' own saving (Runden's Suspension Tuner) needs the
    /// public game's old save classes and does nothing on the beta. Next to the save, as `.players` and `.leases` are:
    /// `<save>.tune`, written with every save of the world by the machine whose world it is (single player or the host,
    /// never a joiner), rotated with the autosaves, read once per world.
    ///
    /// Saved: every loaded car that differs from its prefab, and every shared car the session knows a tuning for (also
    /// when it is far and not loaded here); a loaded car back to its prefab's values is dropped. Applied: once a second
    /// to a saved car the game has placed anew, unless the session keeps that car's tuning (Entities.CarTune does then).
    /// A/B: Enabled false = before (nothing written or applied).
    public static class CarTuneSave
    {
        public static bool Enabled = true;
        public static long FilesRead, FilesWritten, Applied;
        public static int Count => _saved.Count;
        public const int MaxCars = 4096;
        private const uint Magic = 0x314E5554;   // "TUN1"

        private static readonly Dictionary<uint, byte[]> _saved = new Dictionary<uint, byte[]>();
        private static readonly Dictionary<uint, Transform> _done = new Dictionary<uint, Transform>();
        private static readonly HashSet<uint> _session = new HashSet<uint>();
        private static int _world;
        private static float _at;

        private static bool OwnsWorld => Mp.Client == null;   // single player or the host

        private static void EnsureWorld()
        {
            int id = mainscript.s != null ? mainscript.s.GetInstanceID() : 0;
            if (id == 0 || id == _world) return;
            _world = id;
            _saved.Clear(); _done.Clear();
            if (!Enabled || !OwnsWorld) return;
            var save = Entities.WorldSaveFile;
            if (save == null) return;
            var f = ReadFile(save + ".tune");
            if (f == null) return;
            foreach (var kv in f) _saved[kv.Key] = kv.Value;
            FilesRead++;
            Plugin.Log.LogInfo($"car tuning: {_saved.Count} tuned car(s) loaded with {Path.GetFileName(save)}");
        }

        /// Once a second (Runner): saved tunings onto cars the game has placed anew.
        public static void Tick()
        {
            if (!Enabled || !OwnsWorld || Time.realtimeSinceStartup < _at) return;
            _at = Time.realtimeSinceStartup + 1f;
            if (mainscript.s == null || savedatascript.s == null) return;
            EnsureWorld();
            if (_saved.Count == 0) return;
            _session.Clear(); Entities.TunedRootIds(_session);
            foreach (var kv in _saved)
            {
                if (_session.Contains(kv.Key)) continue;
                if (!savedatascript.s.items.TryGetValue(kv.Key, out var it) || it == null || it.car == null) continue;
                if (_done.TryGetValue(kv.Key, out var d) && d == it.transform) continue;
                _done[kv.Key] = it.transform;
                var t = CarTuning.FromBytes(kv.Value, 0, kv.Value.Length);
                if (t == null) continue;
                Entities.ApplyTuning(it.car, t);
                Applied++;
            }
        }

        /// What the world's cars have now, into the table.
        private static void Refresh()
        {
            EnsureWorld();
            _session.Clear(); Entities.TunedRootIds(_session);
            Entities.KnownTunings(_saved);   // shared cars: what the session keeps, loaded here or not
            if (savedatascript.s == null) return;
            foreach (var kv in savedatascript.s.items)
            {
                var it = kv.Value;
                if (it == null || it.car == null || _session.Contains(kv.Key)) continue;
                var t = Entities.CaptureTuning(it.car);
                if (t.Key() == Entities.PrefabTuneKey(it)) { _saved.Remove(kv.Key); continue; }
                if (_saved.Count < MaxCars || _saved.ContainsKey(kv.Key)) _saved[kv.Key] = t.ToBytes();
                _done[kv.Key] = it.transform;
            }
        }

        [HarmonyPatch(typeof(newSaveScreenScript), nameof(newSaveScreenScript.Save))]
        private static class WithSave
        {
            [HarmonyPostfix]
            private static void Postfix(string _text, bool __runOriginal)
            {
                if (!Enabled || !__runOriginal || !OwnsWorld) return;
                try
                {
                    Refresh();
                    var path = pathscript.SaveDataName(pathscript.UnDuckStringBecauseItIsDuckedByDefault(_text)) + ".tune";
                    if (_saved.Count > 0) WriteFile(path);
                    else if (File.Exists(path)) File.Delete(path);   // another world's, from before
                }
                catch (Exception e) { Plugin.Log.LogWarning("car tuning: not saved: " + e.Message); }
            }
        }

        private static Dictionary<uint, byte[]> ReadFile(string path)
        {
            if (!File.Exists(path)) return null;
            try
            {
                var bytes = File.ReadAllBytes(path);
                var r = new NetReader(); r.Load(bytes.Length); Buffer.BlockCopy(bytes, 0, r.Buf, 0, bytes.Length);
                if (r.U32() != Magic) throw new Exception("not a tuning file");
                var outp = new Dictionary<uint, byte[]>();
                int n = (int)r.VarU32();
                for (int i = 0; i < n && !r.Bad && i < MaxCars; i++)
                {
                    uint id = r.U32(); int len = (int)r.VarU32();
                    if (len < 0 || len > r.Remaining || len > Entities.MaxTuneBytes) { r.Bad = true; break; }
                    var b = new byte[len]; Buffer.BlockCopy(r.Buf, r.Pos, b, 0, len); r.Pos += len;
                    if (CarTuning.FromBytes(b, 0, len) != null) outp[id] = b;
                }
                if (r.Bad) throw new Exception("cut short");
                return outp;
            }
            catch (Exception e) { Plugin.Log.LogWarning("car tuning: " + Path.GetFileName(path) + " not readable: " + e.Message); return null; }
        }

        private static void WriteFile(string path)
        {
            var w = new NetWriter();
            w.U32(Magic); w.VarU32((uint)_saved.Count);
            foreach (var kv in _saved) { w.U32(kv.Key); w.VarU32((uint)kv.Value.Length); w.Bytes(kv.Value, 0, kv.Value.Length); }
            using (var fs = new FileStream(path + ".tmp", FileMode.Create, FileAccess.Write)) fs.Write(w.Buf, 0, w.Len);
            if (File.Exists(path)) File.Replace(path + ".tmp", path, null);
            else File.Move(path + ".tmp", path);
            FilesWritten++;
        }

        public static string Stats() => "{\"enabled\":" + (Enabled ? "true" : "false") + ",\"cars\":" + _saved.Count + ",\"read\":" + FilesRead +
                                        ",\"written\":" + FilesWritten + ",\"applied\":" + Applied + "}";
    }
}
