namespace TLDRevamp.Net
{
    /// When a fitted part the player can see is a bug (Entities.PartWatch): the game reaches a fitted part with its use ray
    /// (fpscontroller, useLayer) — through the part's own colliders or its slot's. A part that shows but has neither on a
    /// layer the ray hits can't be opened, unbolted or taken off, and nothing tells the player why: bug report 2026-11-06
    /// 10:57, a hood the joiner could see but got no prompt on from any side, until the host took it off and put it back.
    /// Some fitted parts are not reachable by design: the game switches their colliders off when they are crafted into a
    /// slot with disableCollidersOnCraft, or onto something that is (a tyre on a rim on a car: partslotscript.Craft) —
    /// those don't count. A fitted part on the held-item layer that nobody holds is always wrong.
    internal static class PartWatchRules
    {
        /// The reasons are fixed phrases: they go into bug reports, which keep only known words (TelemetryPrivacy.Results).
        internal const string Off = "part colliders off", HeldLayer = "part on held layer", NotUseLayer = "part not on use layer",
                              Blocked = "hidden object in front";
        internal static float NearbyAfterS = 3f;   // the nearby scan: broken this long (not a part being fitted or taken off)
        internal static float LookAfterS = 1f;     // looked at, broken, this long

        /// `collidersExpected`: no slot up the part's chain switches colliders off on craft. `held`: colliders on the
        /// held-item layer while fitted and not in a hand.
        /// `held` colliders on the held layer count only when some collider is on (all off and on the held layer: the layer
        /// doesn't matter, and the slot decides whether they should be on).
        internal static bool Broken(bool visible, bool targetable, bool collidersExpected, int held, int live = 1) =>
            visible && !targetable && (collidersExpected || (held > 0 && live > 0));

        /// Why a part has no collider the use ray hits: `live` colliders switched on, `held` on the held-item layer.
        internal static string Why(int live, int held) => held > 0 ? HeldLayer : live == 0 ? Off : NotUseLayer;
    }
}
