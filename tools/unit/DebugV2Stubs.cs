// Stubs for the parts of Debugging V2 and the item traffic table that WorldOverlays draws from (IdleTrafficTest).
using System.Collections.Generic;
using UnityEngine;

namespace TLDRevamp
{
    internal static class DebugV2
    {
        internal static bool Hidden, Active;
        internal static bool Line(string id, string line) => true;
        internal static int TabBorder => 3;
        internal static Color? TabText => null;
    }
}
namespace TLDRevamp.Net
{
    public static class ItemTraffic
    {
        public struct Row { public uint Net; public bool Mod; public float UpBps, DownBps, UpMsgs, DownMsgs; }
        public static bool RateOf(uint net, out Row row) { row = default; return false; }
        internal static string ObjectName(string name) => name;
    }
    public static partial class Entities
    {
        internal static string OverlayColliders(Ent e, bool aimed = false) => "";
        internal static string HealthWatchLine(uint net) => null;
        private static List<Ent> LookupSet() => new List<Ent>(ByNet.Values);   // Entities.Placed: every object here is placed
        internal static void SeatCollisionIgnore(TestPlayer pl, TestSeat seat) { }   // Entities.Seat (SetProxy around a seated player)
    }
}
