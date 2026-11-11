using System;

namespace TLDRevamp.Net
{
    /// A car's tuning: the values of carscript and its WheelColliders that the game sets once from the prefab and never
    /// writes again, so only a mod (or a player through one) changes them while the game runs: the final drive, the gear
    /// ratios, where each wheel sits (its collider's local position and center), how far its suspension travels, and
    /// which wheels drive, steer, steer the other way, only brake, or take the handbrake. Of the TLD Workshop's DLLs
    /// (2026-11-11, metadata only) 23 write some of these: car mods once, the tuners (Runden's Suspension Tuner,
    /// EngineTuner, M-ultiTool) while a player drives.
    ///
    /// Not here, because the game derives them itself: a wheel's radius, mass and grip (wheelscript.WheelSize, from its tyre
    /// part, which the save records carry) and its spring (set while the car is picked up).
    ///
    /// Plain data and its wire form, free of Unity, so tools/unit/CarTuningTest checks it without the game.
    public sealed class CarTuning
    {
        public const int MaxGears = 32, MaxWheels = 16, MaxName = 64;
        public const float MaxAbs = 1000f;   // any value beyond is not a tuning, it is garbage
        public const byte Driven = 1, Steer = 2, InvSteer = 4, BrakeOnly = 8, HandBrake = 16;

        public sealed class Wheel
        {
            public string Name = "";
            public float X, Y, Z;        // the collider's local position
            public float Sus;            // suspensionDistance
            public float CX, CY, CZ;     // the collider's center
            public byte Roles;           // Driven | Steer | InvSteer | BrakeOnly | HandBrake
        }

        public float Diff;
        public float[] Gears = new float[0];
        public Wheel[] Wheels = new Wheel[0];

        /// u8 version, f32 diff, u8 n gears + f32 each, u8 n wheels + (str name, 7 × f32, u8 roles) each.
        public const byte WireVersion = 1;

        public void Write(NetWriter w)
        {
            w.U8(WireVersion);
            w.F32(Diff);
            int ng = Math.Min(Gears.Length, MaxGears);
            w.U8((byte)ng);
            for (int i = 0; i < ng; i++) w.F32(Gears[i]);
            int nw = Math.Min(Wheels.Length, MaxWheels);
            w.U8((byte)nw);
            for (int i = 0; i < nw; i++)
            {
                var x = Wheels[i];
                w.Str(x.Name.Length > MaxName ? x.Name.Substring(0, MaxName) : x.Name);
                w.F32(x.X); w.F32(x.Y); w.F32(x.Z); w.F32(x.Sus); w.F32(x.CX); w.F32(x.CY); w.F32(x.CZ);
                w.U8(x.Roles);
            }
        }

        /// null: unreadable, another version, too many gears or wheels, or a value that is no tuning (not finite, huge).
        public static CarTuning Read(NetReader r)
        {
            if (r.U8() != WireVersion) return null;
            var t = new CarTuning { Diff = r.F32() };
            int ng = r.U8();
            if (ng > MaxGears) return null;
            t.Gears = new float[ng];
            for (int i = 0; i < ng; i++) t.Gears[i] = r.F32();
            int nw = r.U8();
            if (nw > MaxWheels) return null;
            t.Wheels = new Wheel[nw];
            for (int i = 0; i < nw && !r.Bad; i++)
            {
                var x = new Wheel { Name = r.Str() ?? "" };
                x.X = r.F32(); x.Y = r.F32(); x.Z = r.F32(); x.Sus = r.F32(); x.CX = r.F32(); x.CY = r.F32(); x.CZ = r.F32();
                x.Roles = r.U8();
                if (x.Name.Length > MaxName) return null;
                t.Wheels[i] = x;
            }
            if (r.Bad || !Ok(t.Diff)) return null;
            foreach (var g in t.Gears) if (!Ok(g)) return null;
            foreach (var x in t.Wheels)
                if (!Ok(x.X) || !Ok(x.Y) || !Ok(x.Z) || !Ok(x.Sus) || !Ok(x.CX) || !Ok(x.CY) || !Ok(x.CZ) || x.Roles > 31) return null;
            return t;
        }

        private static bool Ok(float v) => !float.IsNaN(v) && !float.IsInfinity(v) && Math.Abs(v) <= MaxAbs;

        /// Equal keys: the same tuning to a millimetre and a thousandth of a ratio (float noise never counts as a change).
        public int Key()
        {
            unchecked
            {
                uint h = 2166136261;
                void Mix(int v) { for (int b = 0; b < 4; b++) { h ^= (byte)(v >> (8 * b)); h *= 16777619; } }
                int Q(float v) => (int)Math.Round(v * 1000.0);
                Mix(Q(Diff)); Mix(Gears.Length);
                foreach (var g in Gears) Mix(Q(g));
                Mix(Wheels.Length);
                foreach (var x in Wheels)
                {
                    foreach (char ch in x.Name) Mix(ch);
                    Mix(Q(x.X)); Mix(Q(x.Y)); Mix(Q(x.Z)); Mix(Q(x.Sus)); Mix(Q(x.CX)); Mix(Q(x.CY)); Mix(Q(x.CZ)); Mix(x.Roles);
                }
                return (int)h;
            }
        }

        public byte[] ToBytes()
        {
            var w = new NetWriter(); Write(w);
            var b = new byte[w.Len]; Buffer.BlockCopy(w.Buf, 0, b, 0, w.Len); return b;
        }

        public static CarTuning FromBytes(byte[] b, int off, int len)
        {
            if (b == null || len < 0 || off < 0 || off + len > b.Length) return null;
            var r = new NetReader(); r.Load(len); Buffer.BlockCopy(b, off, r.Buf, 0, len);
            var t = Read(r);
            return t != null && r.Remaining == 0 ? t : null;
        }
    }
}
