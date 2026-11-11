using System.Collections.Generic;
using System.Text;
using HarmonyLib;
using UnityEngine;

namespace TLDRevamp.Fixes
{
    /// What the vehicles do in the first WatchS seconds after a world loads: every car, trailer and trailer grip (the
    /// grip is its own body between a trailer and the car it is hitched to), its top speed and when, when it was let go
    /// (kinematic off) and how far it is from where it was loaded. A save whose hitched pickup and car trailer blew apart
    /// at load (Volkan, 2026-11-11: Δv 109-176 m/s, the trailer into a parked car at 199 m/s) showed nothing of the order
    /// in the log. Read with the bridge's `loadwatch` (`loadtrace`: the first hitched trailer, its grip and its car frame by
    /// frame); the worst are logged when the watch ends. tools/loadfreeze.py reads both.
    [HarmonyPatch(typeof(fpscontroller), "Update")]
    public static class LoadWatch
    {
        public static float WatchS = 30f;
        private class Body { public string Name; public uint Save; public Rigidbody RB; public Vector3 Start; public float Max, MaxAt = -1, FreeAt = -1, Gap0 = -1, GapMax, Ball0 = -1, BallMax; public attachgripscript Grip; public tosaveitemscript Item; }
        private static readonly List<Body> _bodies = new List<Body>();
        private static readonly HashSet<Rigidbody> _seen = new HashSet<Rigidbody>();
        private static mapSettingScript _map;
        private static float _t0 = -1, _scanAt;
        private static bool _logged;

        [HarmonyPostfix]
        private static void Postfix(fpscontroller __instance)
        {
            var map = menuhandler.s != null ? menuhandler.s.currentMainMap : null;
            if (map != _map) { _map = map; _bodies.Clear(); _seen.Clear(); _t0 = -1; _logged = false; _trace.Clear(); _tt = _tg = _tc = null; _traceFrom = -1; }
            if (map == null || !map.firstRun || mainscript.s == null || __instance != mainscript.s.player || savedatascript.s == null) return;
            float now = Time.realtimeSinceStartup;
            if (_t0 < 0) { _t0 = now; _scanAt = 0; }
            float t = now - _t0;
            if (t > WatchS) { if (!_logged) { _logged = true; Plugin.Log.LogInfo("load watch: " + Summary(5)); } return; }
            if (now >= _scanAt) { _scanAt = now + 0.5f; Scan(t); }
            Trace(t);
            foreach (var b in _bodies)
            {
                if (b.RB == null) continue;
                if (b.FreeAt < 0 && !b.RB.isKinematic) b.FreeAt = t;
                float v = b.RB.isKinematic ? 0f : b.RB.velocity.magnitude;
                if (v > b.Max) { b.Max = v; b.MaxAt = t; }
                if (b.Grip != null && b.Grip.Grip != null && b.Grip.GripPos != null)
                {
                    // the trailer's coupler to its grip, and the grip to the point it hangs on (the car's ball)
                    float gap = (b.Grip.GripPos.position - b.Grip.Grip.transform.position).magnitude;
                    if (b.Gap0 < 0) b.Gap0 = gap; if (gap > b.GapMax) b.GapMax = gap;
                    var at = b.Grip.Grip.GetComponent<attachablescript>();
                    if (at != null && at.attached && at.point != null)
                    {
                        float ball = (at.point.position - b.Grip.Grip.transform.position).magnitude;
                        if (b.Ball0 < 0) b.Ball0 = ball; if (ball > b.BallMax) b.BallMax = ball;
                    }
                }
            }
        }

        /// Frame by frame, from the first hitched trailer's grip found until TraceS later: the trailer, its grip and the car
        /// it hangs on (speed, distance from where they were loaded, kinematic), the grip's two joint gaps.
        public static float TraceS = 4f;
        public static string TraceName = "";   // bridge `set`: trace this vehicle (its name) instead
        private static readonly StringBuilder _trace = new StringBuilder();
        private static float _traceFrom = -1;
        private static Body _tt, _tg, _tc;
        private static void Trace(float t)
        {
            if (_tt == null)
            {
                foreach (var b in _bodies) if (TraceName.Length > 0 ? b.Name == TraceName : b.Grip != null && b.Grip.Grip != null) { _tt = b; break; }
                if (_tt == null) return;
                var gr = _tt.Grip != null && _tt.Grip.Grip != null ? _tt.Grip.Grip.GetComponent<Rigidbody>() : null;
                foreach (var b in _bodies) if (gr != null && b.RB == gr) _tg = b;
                _traceFrom = t;
            }
            if (t - _traceFrom > TraceS) return;
            if (_tc == null && _tt.Grip != null)
            {
                var at = _tt.Grip.Grip != null ? _tt.Grip.Grip.GetComponent<attachablescript>() : null;
                var cr = at != null && at.attached && at.point != null ? at.point.root.GetComponent<Rigidbody>() : null;
                foreach (var b in _bodies) if (cr != null && b.RB == cr) _tc = b;
            }
            _trace.Append(t.ToString("F3")).Append(" f").Append(Time.frameCount);
            foreach (var b in new[] { _tt, _tg, _tc })
            {
                if (b == null || b.RB == null) { _trace.Append(" | -"); continue; }
                _trace.Append(" | ").Append(b.Name).Append(b.RB.isKinematic ? " K" : " D").Append(" v").Append(b.RB.velocity.magnitude.ToString("F2"))
                      .Append(" d").Append((b.RB.position - b.Start).magnitude.ToString("F3")).Append(" dy").Append((b.RB.position.y - b.Start.y).ToString("F3"));
            }
            var g = _tt.Grip;
            if (g != null && g.Grip != null && g.GripPos != null)
            {
                _trace.Append(" | gap ").Append((g.GripPos.position - g.Grip.transform.position).magnitude.ToString("F3"));
                var at2 = g.Grip.GetComponent<attachablescript>();
                _trace.Append(" ball ").Append(at2 != null && at2.attached && at2.point != null ? (at2.point.position - g.Grip.transform.position).magnitude.ToString("F3") : "-");
            }
            _trace.Append('\n');
        }
        public static string TraceText => _trace.ToString();

        private static void Scan(float t)
        {
            foreach (var it in savedatascript.s.items.Values)
            {
                if (it == null || !UnfreezeOnGround.Hitchable(it) || !it.gameObject.activeInHierarchy) continue;
                var grip = it.car == null ? it.GetComponentInChildren<attachgripscript>() : null;
                Add(it.name, it.idInSave, it.RB != null ? it.RB : it.GetComponent<Rigidbody>(), grip, it);
                if (grip != null && grip.Grip != null) Add(it.name + "/grip", it.idInSave, grip.Grip.GetComponent<Rigidbody>());
            }
        }

        private static void Add(string name, uint save, Rigidbody rb, attachgripscript grip = null, tosaveitemscript item = null)
        {
            if (rb == null || !_seen.Add(rb)) return;
            _bodies.Add(new Body { Name = name.Replace("(Clone)", ""), Save = save, RB = rb, Start = rb.position, Grip = grip, Item = item });
        }

        private static string Summary(int top)
        {
            var sb = new StringBuilder();
            _bodies.Sort((a, b) => b.Max.CompareTo(a.Max));
            for (int i = 0; i < _bodies.Count && i < top; i++)
            {
                var b = _bodies[i];
                sb.Append(i == 0 ? "" : "; ").Append(b.Name).Append(' ').Append(b.Save).Append(" top ").Append(b.Max.ToString("F1")).Append(" m/s at ").Append(b.MaxAt.ToString("F1"))
                  .Append(" s, let go ").Append(b.FreeAt < 0 ? "never" : b.FreeAt.ToString("F1") + " s");
            }
            return sb.Length == 0 ? "no vehicles" : sb.ToString();
        }

        public static string Stats()
        {
            var ic = System.Globalization.CultureInfo.InvariantCulture;
            var sb = new StringBuilder("{\"t\":").Append(_t0 < 0 ? "-1" : (Time.realtimeSinceStartup - _t0).ToString("F1", ic)).Append(",\"bodies\":[");
            for (int i = 0; i < _bodies.Count; i++)
            {
                var b = _bodies[i];
                float moved = b.RB != null ? (b.RB.position - b.Start).magnitude : -1f;
                var g = b.RB != null ? mainscript.GlobalFromUnityPos(b.RB.position) : default;
                if (i > 0) sb.Append(',');
                sb.Append("{\"name\":").Append(Json.Str(b.Name)).Append(",\"save\":").Append(b.Save)
                  .Append(",\"top\":").Append(b.Max.ToString("F2", ic)).Append(",\"topAt\":").Append(b.MaxAt.ToString("F2", ic))
                  .Append(",\"freeAt\":").Append(b.FreeAt.ToString("F2", ic)).Append(",\"moved\":").Append(moved.ToString("F2", ic)).Append(",\"dy\":").Append((b.RB != null ? b.RB.position.y - b.Start.y : 0f).ToString("F2", ic))
                  .Append(",\"gap0\":").Append(b.Gap0.ToString("F2", ic)).Append(",\"gapMax\":").Append(b.GapMax.ToString("F2", ic))
                  .Append(",\"ball0\":").Append(b.Ball0.ToString("F2", ic)).Append(",\"ballMax\":").Append(b.BallMax.ToString("F2", ic))
                  .Append(",\"hitched\":").Append(b.Grip != null && b.Grip.Grip != null && b.Grip.Grip.GetComponent<attachablescript>() is attachablescript aa && aa.attached ? Json.Str(aa.point != null ? aa.point.root.name : "?") : "null")
                  .Append(",\"item\":").Append(ReferenceEquals(b.Item, null) ? "\"none\"" : b.Item != null ? (b.Item.RB == b.RB ? "\"same body\"" : b.Item.GetComponent<Rigidbody>() != null ? "\"new body\"" : "\"no body\"") : "\"destroyed\"")
                  .Append(",\"itemPos\":").Append(b.Item != null ? Json.Str(mainscript.GlobalFromUnityPos(b.Item.transform.position).ToString()) : "null")
                  .Append(",\"kinematic\":").Append(b.RB != null && b.RB.isKinematic ? "true" : "false")
                  .Append(",\"pos\":[").Append(g.x.ToString("F2", ic)).Append(',').Append(g.y.ToString("F2", ic)).Append(',').Append(g.z.ToString("F2", ic)).Append("]}");
            }
            return sb.Append("]}").ToString();
        }
    }
}
