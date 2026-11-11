using UnityEngine;

namespace TLDRevamp.Diagnostics
{
    /// What can pile up over a long session, for SceneCounts (bridge `scenecount`, tools/longhost.py): a host's FPS fell
    /// from ~100 to ~70 over a few hundred km (Volkan, 2026-11-11). Meshes alive (leaked ones too), objects, the buildings
    /// the game ever placed (poiGenScript.Upd walks them every frame), the managed heap, the shared objects.
    internal static class SceneGrowth
    {
        internal static string Json()
        {
            int meshes = Resources.FindObjectsOfTypeAll<Mesh>().Length, transforms = Object.FindObjectsByType<Transform>(FindObjectsSortMode.None).Length;
            int pois = 0, poiChunks = 0;
            try
            {
                var map = menuhandler.s != null ? menuhandler.s.currentMainMap : null;
                if (map != null && map.poiGens != null)
                    foreach (var g in map.poiGens) if (g != null && g.chunks != null) foreach (var c in g.chunks.Values) { poiChunks++; if (c?.pois != null) pois += c.pois.Count; }
            }
            catch { }
            return ",\"meshes\":" + meshes + ",\"transforms\":" + transforms + ",\"pois\":" + pois + ",\"poiChunks\":" + poiChunks +
                   ",\"heapMB\":" + (System.GC.GetTotalMemory(false) / 1048576.0).ToString("F0", System.Globalization.CultureInfo.InvariantCulture) +
                   ",\"shared\":" + Net.Entities.SharedCount;
        }
    }
}
