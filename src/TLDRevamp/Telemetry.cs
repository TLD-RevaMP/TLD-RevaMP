using System;
using System.Linq;
using System.Text;
using UnityEngine;

namespace TLDRevamp
{
    /// Frame timing + GC + scene counters. Allocation-free per frame.
    public class Telemetry
    {
        private const int Window = 600; // ~10s at 60fps
        private const float HitchMs = 50f;

        private readonly float[] _frameMs = new float[Window];
        private readonly float[] _sorted = new float[Window];
        private int _head, _count;
        private int _gcBaseline;

        private const int Buckets = 10000; // 0.1ms buckets up to 1000ms
        private readonly int[] _hist = new int[Buckets];

        public int Hitches { get; private set; }
        public long Frames { get; private set; }

        public readonly FrameBreakdown Breakdown = new FrameBreakdown();

        public Telemetry() => _gcBaseline = GC.CollectionCount(0);

        public void Tick()
        {
            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            long allocated = GC.GetAllocatedBytesForCurrentThread();
            try { Sample(); }
            catch (Exception e) { SessionLog.Fail("frame", e); }
            finally { SessionLog.Cost(System.Diagnostics.Stopwatch.GetTimestamp() - started, GC.GetAllocatedBytesForCurrentThread() - allocated); }
        }

        private long _lastTick;
        private void Sample()
        {
            // Wall time between frames (Runner.Update to Runner.Update). Not Time.unscaledDeltaTime: with VSync on, Unity
            // reports the display's refresh interval there (measured: a steady 2.77 ms on a 360 Hz display while frames
            // took 12.8 ms), which hid every slow frame.
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            float ms = _lastTick != 0 ? (float)((now - _lastTick) * 1000.0 / System.Diagnostics.Stopwatch.Frequency) : Time.unscaledDeltaTime * 1000f;
            _lastTick = now;
            _frameMs[_head] = ms;
            _head = (_head + 1) % Window;
            if (_count < Window) _count++;
            if (ms > HitchMs) Hitches++;
            _hist[Mathf.Clamp((int)(ms * 10f), 0, Buckets - 1)]++;
            if (ms > _maxMs) _maxMs = ms;
            Breakdown.Tick(ms);
            Frames++;
            Minute(ms);
            SessionLog.Frame(ms, Breakdown);
        }

        // The overlay's rates go by time, not by frame count: a 600-frame median took 2 s to follow a change at 300 fps
        // and 15 s at 40. Fps is the last second of frames. The average and the 1% low cover the last minute, one slot per
        // second: its frames and time, plus its frame times in log buckets (4% wide, 0.25 ms to ~1 s) with their summed ms.
        private const int MinuteSlots = 60, SlowBuckets = 192;
        private readonly int[] _slotFrames = new int[MinuteSlots];
        private readonly float[] _slotMs = new float[MinuteSlots];
        private readonly int[] _slowCount = new int[MinuteSlots * SlowBuckets];
        private readonly float[] _slowMs = new float[MinuteSlots * SlowBuckets];
        private readonly int[] _sumCount = new int[SlowBuckets];
        private readonly float[] _sumMs = new float[SlowBuckets];
        private int _slot, _slots;

        private void Minute(float ms)
        {
            if (_slots == 0 || _slotMs[_slot] >= 1000f)
            {
                if (_slots > 0) _slot = (_slot + 1) % MinuteSlots;
                if (_slots < MinuteSlots) _slots++;
                _slotFrames[_slot] = 0; _slotMs[_slot] = 0;
                Array.Clear(_slowCount, _slot * SlowBuckets, SlowBuckets);
                Array.Clear(_slowMs, _slot * SlowBuckets, SlowBuckets);
            }
            _slotFrames[_slot]++; _slotMs[_slot] += ms;
            int b = _slot * SlowBuckets + Mathf.Clamp((int)(Mathf.Log(Mathf.Max(ms, .25f) / .25f, 2f) * 16f), 0, SlowBuckets - 1);
            _slowCount[b]++; _slowMs[b] += ms;
        }

        /// Frames per second over the last second (the whole ring when it holds less).
        public float Fps
        {
            get
            {
                float ms = 0; int n = 0;
                while (n < _count && ms < 1000f) ms += _frameMs[(_head - 1 - n++ + Window) % Window];
                return ms > 0 ? n * 1000f / ms : 0f;
            }
        }

        /// Frames per second over the last minute, the second in progress included.
        public float AvgFps
        {
            get
            {
                float ms = 0; long n = 0;
                for (int s = 0; s < _slots; s++) { ms += _slotMs[s]; n += _slotFrames[s]; }
                return ms > 0 ? n * 1000f / ms : 0f;
            }
        }

        /// The 1% and 0.1% lows over the last minute: the average rate of its slowest 1% (0.1%) of frames.
        public void Lows(out float onePercent, out float tenthPercent)
        {
            Array.Clear(_sumCount, 0, SlowBuckets); Array.Clear(_sumMs, 0, SlowBuckets);
            long n = 0;
            for (int s = 0; s < _slots; s++)
            {
                n += _slotFrames[s];
                for (int b = 0, o = s * SlowBuckets; b < SlowBuckets; b++) { _sumCount[b] += _slowCount[o + b]; _sumMs[b] += _slowMs[o + b]; }
            }
            onePercent = Low(Math.Max(1, n / 100));
            tenthPercent = Low(Math.Max(1, n / 1000));
        }

        private float Low(long want)
        {
            long got = 0; float ms = 0;
            for (int b = SlowBuckets - 1; b >= 0 && got < want; b--)
            {
                int c = _sumCount[b]; if (c == 0) continue;
                long take = Math.Min(c, want - got);
                ms += _sumMs[b] * take / c; got += take;
            }
            return ms > 0 ? got * 1000f / ms : 0f;
        }

        public void Reset()
        {
            _head = _count = 0;
            _slot = _slots = 0;
            Hitches = 0;
            _maxMs = 0;
            Array.Clear(_hist, 0, Buckets);
            Frames = 0;
            Breakdown.Reset();
            DebugBridge.TotalPumpMs = 0;
            DebugBridge.MaxPumpMs = 0;
            _gcBaseline = GC.CollectionCount(0);
        }

        public float Percentile(float p)
        {
            if (_count == 0) return 0f;
            Array.Copy(_frameMs, _sorted, _count);
            Array.Sort(_sorted, 0, _count);
            return _sorted[Mathf.Clamp((int)(p * (_count - 1)), 0, _count - 1)];
        }

        private float _maxMs;

        /// Percentile over every frame since the last Reset (not just the ring window).
        public float RunPercentile(float p)
        {
            if (Frames == 0) return 0f;
            if (p >= 1f) return _maxMs;
            long target = (long)(p * Frames), seen = 0;
            for (int i = 0; i < Buckets; i++)
            {
                seen += _hist[i];
                if (seen > target) return (i + 0.5f) / 10f;
            }
            return _maxMs;
        }

        public float Last => _count == 0 ? 0f : _frameMs[(_head - 1 + Window) % Window];
        public int GcCollections => GC.CollectionCount(0) - _gcBaseline;
        public long ManagedMB => GC.GetTotalMemory(false) / (1024 * 1024);

        /// Samples the frame history into a small fixed buffer for graphing.
        public int CopyHistory(float[] dst)
        {
            int n = Math.Min(dst.Length, _count);
            for (int i = 0; i < n; i++)
                dst[i] = _frameMs[(_head - n + i + Window) % Window];
            return n;
        }

        private string _sceneJson;
        private double _sceneAt = -100;

        /// Expensive: walks the scene. Call on demand, not per frame.
        public string SceneStatsJson()
        {
            double now = Time.realtimeSinceStartupAsDouble;
            if (_sceneJson != null && now - _sceneAt < 60) return _sceneJson;
            long scanStart = System.Diagnostics.Stopwatch.GetTimestamp();
            var bodies = UnityEngine.Object.FindObjectsOfType<Rigidbody>();
            int awake = bodies.Count(b => !b.IsSleeping());
            int renderers = UnityEngine.Object.FindObjectsOfType<Renderer>().Length;
            int behaviours = UnityEngine.Object.FindObjectsOfType<MonoBehaviour>().Length;
            var player = mainscript.s != null ? mainscript.s.player : null;
            Vector3 pos = player != null ? player.transform.position : Vector3.zero;
            double scanMs = (System.Diagnostics.Stopwatch.GetTimestamp() - scanStart) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            _sceneAt = now;
            _sceneJson = new StringBuilder()
                .Append("{\"scene\":").Append(TelemetryPrivacy.Str(TelemetryPrivacy.FreeText(UnityEngine.SceneManagement.SceneManager.GetActiveScene().name)))
                .Append(",\"focused\":").Append(Application.isFocused ? "true" : "false")
                .Append(",\"rigidbodies\":").Append(bodies.Length)
                .Append(",\"rigidbodiesAwake\":").Append(awake)
                .Append(",\"renderers\":").Append(renderers)
                .Append(",\"monobehaviours\":").Append(behaviours)
                .Append(",\"scanMs\":").Append(Json.Num(scanMs)).Append(",\"sampleSt\":").Append(Json.Num(SessionLog.St))
                .Append(",\"playerPos\":[").Append(pos.x.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)).Append(',').Append(pos.y.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)).Append(',').Append(pos.z.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)).Append(']')
                .Append('}').ToString();
            return _sceneJson;
        }

        public string FrameStatsJson() =>
            "{\"frames\":" + Frames +
            ",\"fps\":" + (RunPercentile(0.5f) > 0 ? (1000f / RunPercentile(0.5f)).ToString("F1", System.Globalization.CultureInfo.InvariantCulture) : "0") +
            ",\"p50ms\":" + RunPercentile(0.5f).ToString("F2", System.Globalization.CultureInfo.InvariantCulture) +
            ",\"p99ms\":" + RunPercentile(0.99f).ToString("F2", System.Globalization.CultureInfo.InvariantCulture) +
            ",\"p999ms\":" + RunPercentile(0.999f).ToString("F2", System.Globalization.CultureInfo.InvariantCulture) +
            ",\"maxms\":" + RunPercentile(1f).ToString("F2", System.Globalization.CultureInfo.InvariantCulture) +
            ",\"hitches50ms\":" + Hitches +
            ",\"gc0\":" + GcCollections +
            ",\"managedMB\":" + ManagedMB +
            ",\"bridgeMsTotal\":" + DebugBridge.TotalPumpMs.ToString("F1", System.Globalization.CultureInfo.InvariantCulture) +
            ",\"bridgeMaxMs\":" + DebugBridge.MaxPumpMs.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) +
            ",\"time\":" + Time.realtimeSinceStartup.ToString("F1", System.Globalization.CultureInfo.InvariantCulture) + "}";
    }

    /// What was in the scene around slow frames: one scan of the scene's live objects (FindObjectsByType, unsorted),
    /// at most every MinIntervalS, triggered by a hitch line. Between scans the hitch line repeats the last counts with
    /// their age. The scan's own cost (scanMs) is logged with it; it allocates the object arrays it walks.
    internal static class SceneCounts
    {
        public static double MinIntervalS = 30;
        /// What can pile up over a long session (",\"meshes\":…"): set by the plugin (Diagnostics.SceneGrowth).
        internal static Func<string> Growth;
        private static double _at = double.NegativeInfinity;
        private static string _body;

        public static string OnHitch(double now)
        {
            if (now - _at >= MinIntervalS) Scan(now);
            return _body == null ? "null" : "{" + _body + ",\"ageS\":" + Json.Num(now - _at, "F1") + "}";
        }

        public static string Scan(double now)
        {
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            int bodies = 0, dynamic = 0, awake = 0, renderers = 0, visible = 0, lights = 0, shadowLights = 0, particles = 0, playing = 0;
            foreach (var b in UnityEngine.Object.FindObjectsByType<Rigidbody>(FindObjectsSortMode.None))
            { bodies++; if (!b.isKinematic) { dynamic++; if (!b.IsSleeping()) awake++; } }
            foreach (var r in UnityEngine.Object.FindObjectsByType<Renderer>(FindObjectsSortMode.None))
            { renderers++; if (r.isVisible) visible++; }
            foreach (var l in UnityEngine.Object.FindObjectsByType<Light>(FindObjectsSortMode.None))
                if (l.enabled) { lights++; if (l.shadows != LightShadows.None) shadowLights++; }
            foreach (var ps in UnityEngine.Object.FindObjectsByType<ParticleSystem>(FindObjectsSortMode.None))
            { particles++; if (ps.isPlaying) playing++; }
            int cars = mainscript.s?.Cars != null ? mainscript.s.Cars.Count : -1;
            int copies = Net.Mp.Server != null || Net.Mp.Client != null ? Net.Entities.CopyCarCount() : 0;
            double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            _at = now;
            _body = "\"rigidbodies\":" + bodies + ",\"rigidbodiesDynamic\":" + dynamic + ",\"rigidbodiesAwake\":" + awake +
                ",\"renderers\":" + renderers + ",\"renderersVisible\":" + visible + ",\"lights\":" + lights + ",\"shadowLights\":" + shadowLights +
                ",\"particleSystems\":" + particles + ",\"particleSystemsPlaying\":" + playing + ",\"cars\":" + cars + ",\"carCopies\":" + copies +
                (Growth != null ? Growth() : "") +
                ",\"scanMs\":" + Json.Num(ms) + ",\"sampleSt\":" + Json.Num(SessionLog.St);
            return "{" + _body + "}";
        }
    }
}
