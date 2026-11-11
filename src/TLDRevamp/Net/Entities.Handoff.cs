using System.Collections.Generic;
using UnityEngine;

namespace TLDRevamp.Net
{
    /// Objects are simulated where players are. An object is simulated by its owner's machine only while that machine has
    /// it loaded — around its own player. When the owner travels away (or is the dedicated server's phantom, which never
    /// moves), the object goes dormant there, and every other player's copy is a kinematic stand-in that nothing drives:
    /// a creature in a building stood frozen while another player walked through it, a car or a can stayed unpushable
    /// until someone happened to claim it. The game's own MP has no such case (one host simulates everything around it).
    /// So the server hands an object to the nearest player who has it once its owner's player is beyond the game's item
    /// FREEZE distance (itemPlaceRemoveScript.itemFreezeDist, 200 m: the owner's game has frozen it — and beyond 300 m
    /// removed it into the far store) and that player is within the UNFREEZE distance (itemUnFreezeDist, 100 m: where
    /// the game runs items' physics around a player). The 100 m between the two is the hysteresis: an object never
    /// bounces between two players. (v0.63.0 used 1 km / 250 m — invented, not the game's: an owner 300 m away had it
    /// stored and kept it; handoff run 3.)
    public static partial class Entities
    {
        public static bool HandoffEnabled = true;
        public static double HandFarM, HandNearM;     // 0: the game's own (freeze / unfreeze distance)
        public static int HandPerTick = 16;
        public static long Handoffs, HandoffKeptCarried, HandoffLogged;
        public static bool HandoffKeepCarried = true;   // A/B: false = before v0.65.55 (invsync.py --carry)
        /// A/B: an object mounted on another shared object (a rim bolted on a car, a thing glued on it) goes with what it's
        /// on: CascadeOwner gives it the car's new owner. Handed off on its own, by where it was last seen, it went to
        /// another player than the car's (playtest 2026-11-06: the client teleported with its car, the car was in a crash
        /// lease and stayed theirs, and four rims and two things on it went to the host, whose game then stored the
        /// client's wheels away and destroyed them). False = before.
        public static bool HandoffKeepsMounted = true;
        public static long HandoffKeptMounted;
        private static float _handAcc;
        private static readonly List<KeyValuePair<int, Vector3d>> _handPlayers = new List<KeyValuePair<int, Vector3d>>();

        /// On another shared object: by the server's record of its attach, or (no record: the playtest's rims had none, the
        /// lease that passed the car on didn't take them along) by the host's own copy, attached to another item.
        private static bool Mounted(SEnt se)
        {
            if (se.AttachParent != 0 && Server.ContainsKey(se.AttachParent)) return true;
            return ByNet.TryGetValue(se.NetId, out var e) && e.Root != null && e.Root.attachable != null && e.Root.attachable.attached && AttachedToItem(e.Root);
        }

        /// A member that came off its car and is on nothing now (not bolted back, not glued to something).
        private static bool LooseSplit(SEnt se) =>
            se.ParentNet != 0 && se.AttachParent == 0 && se.CarrierNet == 0 && Server.TryGetValue(se.ParentNet, out var car) && car.SplitOff != null && car.SplitOff.Contains(se.PartIndex);

        private static void ServerHandoffTick(float dt)
        {
            if (Mp.Server == null || !HandoffEnabled) return;
            _handAcc += dt;
            if (_handAcc < 1f) return;
            _handAcc = 0f;
            Mp.Server.ReadyPeers(_handPlayers);
            if (!DedicatedServer.Enabled && mainscript.s != null && mainscript.s.player != null)
                _handPlayers.Add(new KeyValuePair<int, Vector3d>(0, mainscript.GlobalFromUnityPos(mainscript.s.player.transform.position)));
            if (_handPlayers.Count < 1) return;
            var ipr = itemPlaceRemoveScript.s;
            double farM = HandFarM > 0 ? HandFarM : ipr != null && ipr.itemFreezeDist > 0 ? ipr.itemFreezeDist : 200;
            double nearM = HandNearM > 0 ? HandNearM : ipr != null && ipr.itemUnFreezeDist > 0 ? ipr.itemUnFreezeDist : 100;
            if (nearM >= farM) nearM = farM * 0.5;
            double far2 = farM * farM;
            int n = 0;
            foreach (var se in Server.Values)
            {
                // parts go with their car; one that came off and lies loose is an object of its own: left with an owner far
                // off, the player next to it saw a frozen copy nobody simulated (a wheel thrown off, the host driven away)
                if (se.HitchCar != 0 || se.CrashReturnTo >= 0 || se.PartIndex >= 0 && !LooseSplit(se)) continue;
                if (HandoffKeepsMounted && Mounted(se)) { HandoffKeptMounted++; continue; }   // goes with what it's on
                if (se.Driven && Time.realtimeSinceStartup - se.DrivenAt < 1f) continue;        // its driver keeps it
                if (HandoffKeepCarried && (se.Held || se.Stored)) { HandoffKeptCarried++; continue; }   // in someone's hands or inventory: theirs, wherever it was last seen
                if (WornSync && ServerWornNow(se)) { HandoffKeptCarried++; continue; }   // on its owner's head (Entities.Worn)
                double ownerD2 = double.MaxValue, bestD2 = nearM * nearM;              // owner not playing: as far as it gets
                int best = -1;
                foreach (var p in _handPlayers)
                {
                    double dx = p.Value.x - se.Where.x, dz = p.Value.z - se.Where.z, d2 = dx * dx + dz * dz;
                    if (p.Key == se.OwnerId) ownerD2 = d2;
                    else if (d2 < bestD2 && (p.Key == 0 || KnownBy(p.Key).Contains(se.NetId))) { bestD2 = d2; best = p.Key; }
                }
                if (best < 0 || ownerD2 <= far2 || bestD2 >= ownerD2) continue;
                OwnerLog(se, best, "hand-off (nearer player)");
                if (HandoffLogged++ < 40) Plugin.Log.LogInfo($"hand-off: net {se.NetId} {se.OwnerId} -> {best}, owner's player {System.Math.Sqrt(ownerD2):F0} m from it, new {System.Math.Sqrt(bestD2):F0} m (where {se.Where}, at rest {se.AtRest}, locked {se.Locked} in {se.LockFrame}, driven {se.Driven})");
                se.OwnerId = best; se.Epoch++; Handoffs++;
                W.Reset(); W.U8(Owner); W.U32(se.NetId); W.VarU32((uint)best); W.U32(se.Epoch);
                ServerSendAll(W, true, -1);
                CascadeOwner(se);
                if (++n >= HandPerTick) break;
            }
        }
    }
}
