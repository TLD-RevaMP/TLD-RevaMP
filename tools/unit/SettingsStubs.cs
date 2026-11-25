using System;
namespace TLDRevamp.Fixes
{
    static class MirrorThrottle { internal static bool Enabled; internal static int EveryN; internal static bool NoShadows; }
    static class TinyRendererCull { internal static bool Enabled; internal static float ThresholdPx; }
    static class GrassCameraThrottle { internal static bool Enabled; internal static int EveryN; }
    static class RenderOptions { internal static float ProbeInterval; internal static int ShadowCascades; internal static bool SmallItemShadows; }
    static class BinocularLensCamera { internal static bool Enabled; }
    static class DetailedTerrain { public static float DistanceM = 750f; internal static readonly float[] Choices = { 0f, 500f, 750f, 1000f }; internal static void Apply() { } }
    static class RoadPlaceCache { internal static bool Enabled; }
    static class ObjGenFast { internal static bool Enabled; }
    static class SceneryTileSync { internal static bool Enabled; }
    static class TerrainSync { internal static bool Enabled; }
    static class RoadConnectMemo { internal static bool Enabled; }
    static class DotChunkMemo { internal static bool Enabled; }
    static class AsyncTerrain { internal static bool Enabled; }
    // The Revamp settings file names the terrain switches; this test does not exercise them, so it only needs the
    // the flags and the restore call to exist (the production class lives in Fixes/TerrainHorizon.cs).
    static class TerrainHorizon
    {
        internal static bool CorrectFar = true;
        internal static bool DistantRoads = true;
        internal static void RestoreFarMeshes() { }
    }
    static class ParallelRoadGen { internal static bool Enabled; }
    static class ChunksNeededInPlace { internal static bool Enabled; }
    static class ObjPlaceInPlace { internal static bool Enabled; }
    static class ItemSpawnSpread { internal static bool Enabled; }
    static class FarItemLookup { internal static bool Spread; }
    static class PartConditionIdle { internal static bool Enabled; }
    static class SleepingWheelSkip { internal static bool Enabled; }
    static class ImpactSoundLimiter { internal static int Mode; }
    static class RebaseParticles { internal static bool Enabled; }
    static class AlignNearestRoad { internal static bool Enabled; }
    static class AiRaycastNoAlloc { internal static bool Enabled; }
    static class OutfitDistanceNoAlloc { internal static bool Enabled; }
    static class IdleTextureStreaming { internal static bool Enabled; }
    static class IdlePourPause { internal static bool Enabled; }
    static class IdleCarParticles { internal static bool Enabled; }
    static class TrailerBrakes { internal static bool Enabled; }
    static class DrivingAssists { internal static bool Roll; internal static bool Sway; internal static bool Traction; }
    static class AntiAliasingChoice
    {
        internal static readonly string[] MsaaOptions = { "Off", "2x", "4x", "8x" };
        internal static readonly string[] FilterOptions = { "None", "FXAA", "SMAA", "TAA (tuned)", "TAA (game's tuning)", "FSR 3.1 native AA" };
        internal const int DefaultMsaa = 1, DefaultFilter = 2;
        internal static int Msaa = DefaultMsaa, Filter = DefaultFilter;
        internal static void SetMsaa(int v) { Msaa = v; }
        internal static void SetFilter(int v) { Filter = v; }
        internal static bool FromOldMode(int mode, bool tunedTaa, out int msaa, out int filter)
        {
            msaa = 0; filter = 0;
            switch (mode)
            {
                case 1: return true;
                case 2: filter = 1; return true;
                case 3: filter = 2; return true;
                case 4: filter = tunedTaa ? 3 : 4; return true;
                case 5: msaa = 1; return true;
                case 6: msaa = 2; return true;
                case 7: msaa = 3; return true;
                case 8 when tunedTaa: msaa = 1; filter = 3; return true;
                default: return false;
            }
        }
    }
}
namespace TLDRevamp.WorldGen
{
    // The terrain mode (issue #32). The real model is linked into this test project, so the setting below uses the
    // production code; only the two Unity-facing entry points are stubbed here.
    public static class TerrainChoice { public static TerrainMode Selected = TerrainModes.Default; }
    public static class TerrainHooks { public static void Select(TerrainMode m) { if (TerrainModes.IsSelectable(m)) TerrainChoice.Selected = m; } }
}
namespace TLDRevamp.Cinematic
{
    // The settings tab reads the cinematic camera's mode, speed and field of view. Only the Unity camera is
    // stubbed: the real core (the modes, the bounds and the policy) is compiled in, so the settings test and the
    // camera test agree on the same numbers.
    static class CinematicCamera
    {
        internal static CinematicMode Mode = CinematicMode.Free;
        internal static CinematicMode DefaultMode = CinematicMode.Free;
        internal static float Speed = CinematicLimits.DefaultSpeed;
        internal static float Fov = CinematicLimits.DefaultFov;
        internal static bool ShowStatus = true;
    }
}
namespace BepInEx { static class Paths { internal static string ConfigPath; } }
namespace TLDRevamp
{
    static class Json { internal static string Str(string s) => System.Text.Json.JsonSerializer.Serialize(s); }
    class Plugin { internal static Plugin Instance = new Plugin(); internal static Logger Log = new Logger(); internal Overlay Overlay = new Overlay(); internal static bool WipMarkEnabled = true; internal static void SetWipMark(bool value) { WipMarkEnabled = value; } }
    class Logger { internal void LogWarning(string s) { throw new Exception(s); } }
    class Overlay { internal bool Visible; }
    static class WorldOverlays { internal static bool Ownership, Ghost, GhostAll, Links; }
    static class ObjectPanels { internal static bool Compare, Events; }
    // Debugging V2 (DebugV2.cs): the settings rows only switch it and keep its layout text
    static class DebugV2 { internal static bool Style, On, Hidden, HiddenByHand; internal const string LayoutKey = "debugV2Layout"; internal static void Apply() { } internal static void LoadLayout(string s) { } internal static void BeforeSave() { } internal static void AfterSave() { } }
    static class Sandstorms { internal static int Frequency; internal static void SetFrequency(int n) { Frequency = n; } }
}
namespace TLDRevamp.Net
{
    static class SessionSettingsRules { internal static string CleanPassword(string s) => (s ?? "").Trim(); }
    static class JoinLoading { internal static bool Enabled = true; }
    static class Voice { internal enum Mode { Push, Open, Off } internal static Mode Setting; internal static float Volume;
        internal static readonly string[] EveryoneKeyNames = { "None", "O" }, EveryoneKeys = EveryoneKeyNames; internal static int EveryoneKey; }
    static class Tpa { internal static int[] Choices = { -1, 0, 60, 300, 900 }; internal static bool Enabled; internal static int CooldownS; }
    static class Hardcore { internal static bool Own; internal static void SetOwn(bool on) { Own = on; } }
    static class NetMeterTools { internal static bool Show; }
    static class ItemTraffic { internal static bool Show; internal static void SetVisible(bool value) { Show = value; } }
    static class Reports { internal static bool Share, AskPlayers; }
    static class WorldCheck { internal static void SetScenery(bool on) { } internal static void SetObjGen(bool on) { } }
    static class ModCatalog { internal static bool CheckAtStart; }
}
namespace TLDRevamp.VR.Native
{
    // The VR rows of the settings tab read and write these switches; the VR code itself needs the game and OpenXR.
    static class VrNative { internal static bool Comfort = true, Hands = true; }
    static class VrHeight { internal static bool Enabled = true; }
    static class VrPerf { internal static bool DesktopMirror, SharedMirrors = true, SinglePass = true, NoTaa = true; internal static float RenderScale = 1f; internal static int MaxMsaa = 4; }
}
namespace TLDRevamp.Replay
{
    // Settings exposes replay controls too; the network test only needs their entry points.
    static class ReplayRecorder
    {
        internal static bool Recording, ShowBanner = true;
        internal static string Start() { Recording = true; return ""; }
        internal static string Stop() { Recording = false; return ""; }
    }
    static class ReplayViewer
    {
        internal static bool Active;
        internal static string PlayLatest() { Active = true; return ""; }
        internal static string End(string reason) { Active = false; return ""; }
    }
}
