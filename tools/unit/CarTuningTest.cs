using System;
using TLDRevamp.Net;

/// The REAL src/TLDRevamp/Net/CarTuning.cs: a car's tuning survives the wire exactly, float noise is no change, a real
/// change is one, and what a peer could send that is no tuning (another version, too many wheels, NaN, a huge value,
/// bytes cut short or left over) is refused, never half read.
static class CarTuningTest
{
    static int failures;
    static void Check(string name, bool ok) { Console.WriteLine((ok ? "PASS " : "FAIL ") + name); if (!ok) failures++; }

    static CarTuning Sample()
    {
        var t = new CarTuning { Diff = 4.44f, Gears = new[] { -3.2f, 0f, 3.5f, 2.1f, 1.4f, 1f } };
        t.Wheels = new[]
        {
            new CarTuning.Wheel { Name = "RR", X = 0.594f, Y = -0.13f, Z = -1.352f, Sus = 0.4f, CX = 0f, CY = 0.05f, CZ = 0f, Roles = CarTuning.Driven | CarTuning.HandBrake },
            new CarTuning.Wheel { Name = "FR", X = 0.642f, Y = -0.13f, Z = 1.063f, Sus = 0.4f, Roles = CarTuning.Steer },
            new CarTuning.Wheel { Name = "RL", X = -0.594f, Y = -0.13f, Z = -1.352f, Sus = 0.4f, Roles = CarTuning.Driven | CarTuning.HandBrake },
            new CarTuning.Wheel { Name = "FL", X = -0.642f, Y = -0.13f, Z = 1.063f, Sus = 0.4f, Roles = CarTuning.Steer },
        };
        return t;
    }

    static CarTuning Round(CarTuning t) { var b = t.ToBytes(); return CarTuning.FromBytes(b, 0, b.Length); }

    static byte[] Bytes(Action<NetWriter> write) { var w = new NetWriter(); write(w); var b = new byte[w.Len]; Buffer.BlockCopy(w.Buf, 0, b, 0, w.Len); return b; }

    static int Main()
    {
        var a = Sample();
        var b = Round(a);
        Check("round trip reads back", b != null);
        Check("round trip: same key", b != null && b.Key() == a.Key());
        Check("round trip: every value exact", b != null && b.Diff == a.Diff && b.Gears.Length == 6 && b.Gears[2] == 3.5f && b.Wheels.Length == 4 &&
              b.Wheels[0].Name == "RR" && b.Wheels[0].Z == -1.352f && b.Wheels[0].CY == 0.05f && b.Wheels[0].Roles == (CarTuning.Driven | CarTuning.HandBrake) && b.Wheels[3].Roles == CarTuning.Steer);
        Console.WriteLine("  bytes for a 4-wheel car with 6 gears: " + a.ToBytes().Length);

        var noise = Sample(); noise.Wheels[0].Y += 0.0001f; noise.Diff += 0.0001f;
        Check("float noise (0.1 mm, 0.0001 ratio) is no change", noise.Key() == a.Key());
        var lowered = Sample(); foreach (var w in lowered.Wheels) w.Y -= 0.05f;
        Check("lowered 5 cm is a change", lowered.Key() != a.Key());
        var awd = Sample(); foreach (var w in awd.Wheels) w.Roles |= CarTuning.Driven;
        Check("AWD is a change", awd.Key() != a.Key());
        var diff = Sample(); diff.Diff = 4.94f;
        Check("final drive is a change", diff.Key() != a.Key());
        var renamed = Sample(); renamed.Wheels[1].Name = "FR2";
        Check("another wheel name is a change", renamed.Key() != a.Key());

        var nan = Sample(); nan.Wheels[2].Sus = float.NaN;
        Check("NaN refused", Round(nan) == null);
        var huge = Sample(); huge.Diff = 1e9f;
        Check("a huge value refused", Round(huge) == null);
        var full = a.ToBytes();
        Check("cut short refused", CarTuning.FromBytes(full, 0, full.Length - 3) == null);
        var longer = new byte[full.Length + 1]; Buffer.BlockCopy(full, 0, longer, 0, full.Length);
        Check("bytes left over refused", CarTuning.FromBytes(longer, 0, longer.Length) == null);
        var v2 = (byte[])full.Clone(); v2[0] = 2;
        Check("another version refused", CarTuning.FromBytes(v2, 0, v2.Length) == null);
        Check("17 wheels refused", CarTuning.FromBytes(Bytes(w => { w.U8(1); w.F32(1f); w.U8(0); w.U8(17); }), 0, 7) == null);
        Check("33 gears refused", CarTuning.FromBytes(Bytes(w => { w.U8(1); w.F32(1f); w.U8(33); }), 0, 6) == null);
        Check("roles beyond the five refused", CarTuning.FromBytes(Bytes(w => { w.U8(1); w.F32(1f); w.U8(0); w.U8(1); w.Str("W"); for (int i = 0; i < 7; i++) w.F32(0f); w.U8(32); }), 0, 6 + 2 + 28 + 1) == null);
        Check("an empty array is no tuning", CarTuning.FromBytes(new byte[0], 0, 0) == null);
        Check("a range outside the array is refused, not thrown", CarTuning.FromBytes(full, 5, full.Length) == null);

        var many = Sample(); many.Wheels = new CarTuning.Wheel[20]; for (int i = 0; i < 20; i++) many.Wheels[i] = new CarTuning.Wheel { Name = "W" + i };
        var cut = Round(many);
        Check("writing caps the wheels at 16 (reads back)", cut != null && cut.Wheels.Length == CarTuning.MaxWheels);

        Console.WriteLine(failures == 0 ? "ALL PASS" : failures + " FAILED");
        return failures == 0 ? 0 : 1;
    }
}
