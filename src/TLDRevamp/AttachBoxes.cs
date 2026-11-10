using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace TLDRevamp
{
    /// Debugging V2 → Attach boxes: every part slot's box near the player, drawn as a wire box, so a part that is hard to
    /// put back shows why. The game attaches a held bolted part when a slot box that takes it lies within the scan radius
    /// of the part's own origin (attachablescript.Scan: an OverlapSphere on mainscript.scanLayer) - not where you look -
    /// and a loose item through the attach ray from the camera. So with a part in hand its scan sphere is drawn too, with
    /// a line to the nearest box that takes it and the gap in metres:
    ///   green  the box the part attaches to now          yellow  takes the held part, out of reach
    ///   blue   an empty slot (nothing held)              grey    full, or doesn't take the held part
    /// Local only; nothing is sent.
    internal static class AttachBoxes
    {
        private const float Range = 12f;
        private const int MaxLabels = 12;
        private static readonly CultureInfo IC = CultureInfo.InvariantCulture;
        private static readonly Color Now = new Color(.3f, 1f, .45f), Takes = new Color(1f, .85f, .2f), Empty = new Color(.35f, .76f, .95f),
            Other = new Color(.75f, .75f, .75f, .35f), Miss = new Color(1f, .35f, .3f);
        private static readonly List<Collider> _slots = new List<Collider>();
        private static readonly Collider[] _found = new Collider[256];
        private static double _next;
        private static Material _lines;
        private static readonly GUIContent _text = new GUIContent();

        internal static bool Show => DebugV2.Has("attach") && !DebugV2.Hidden;

        internal static void Draw()
        {
            if (!Show || Event.current.type != EventType.Repaint) return;
            var pl = mainscript.s != null ? mainscript.s.player : null;
            var cam = pl != null ? (pl.Cam != null ? pl.Cam : pl.mainCam) : null;
            if (cam == null || !cam.isActiveAndEnabled) return;
            OverlayStyle.Ensure(); Init();
            if (_lines == null) return;

            double now = Time.realtimeSinceStartupAsDouble;
            if (now >= _next)
            {
                _next = now + .25;
                _slots.Clear();
                int mask = mainscript.s.scanLayer.value | pl.attachLayer.value;
                int n = Physics.OverlapSphereNonAlloc(cam.transform.position, Range, _found, mask, QueryTriggerInteraction.Collide);
                for (int i = 0; i < n; i++) if (_found[i] != null && _found[i].GetComponent<partslotscript>() != null) _slots.Add(_found[i]);
            }

            // the held part: what it would attach to now, and its scan sphere
            attachablescript held = pl.pickedUp != null && pl.pickedUp.tosave != null ? pl.pickedUp.tosave.attachable : null;
            partslotscript chosen = held != null && held.isPart ? held.Scan() : null;
            float radius = held != null ? (held.scanRadius == 0 ? mainscript.s.defScanRadius : mainscript.s.defScanRadius * held.scanRadius) : 0;
            Collider nearest = null; float nearestGap = float.MaxValue; Vector3 nearestPoint = Vector3.zero;

            int labels = 0;
            _lines.SetPass(0);
            GL.PushMatrix();
            try
            {
                GL.LoadPixelMatrix(0, Screen.width, Screen.height, 0); GL.Begin(GL.LINES);
                try
                {
                    foreach (var c in _slots)
                    {
                        if (c == null || !c.enabled || !c.gameObject.activeInHierarchy) continue;
                        var ps = c.GetComponent<partslotscript>();
                        if (ps == null) continue;
                        bool takes = held != null && ps.CanCraft(held);
                        Color colour = held != null ? (ps == chosen ? Now : takes ? Takes : Other) : (ps.hasPart() ? Other : Empty);
                        Wire(cam, c, colour);
                        if (takes && held.isPart)
                        {
                            var p = c.ClosestPoint(held.transform.position); float gap = (p - held.transform.position).magnitude;
                            if (gap < nearestGap) { nearestGap = gap; nearest = c; nearestPoint = p; }
                        }
                    }
                    if (held != null && held.isPart)
                    {
                        var o = held.transform.position;
                        var sc = chosen != null ? Now : Miss;
                        Circle(cam, o, Vector3.up, radius, sc); Circle(cam, o, Vector3.right, radius, sc); Circle(cam, o, Vector3.forward, radius, sc);
                        if (nearest != null && chosen == null) Segment(cam, o, nearestPoint, Takes);
                    }
                }
                finally { GL.End(); }
            }
            finally { GL.PopMatrix(); }

            // labels: the slot names (nearest first), then the held part's gap
            var colourBefore = GUI.color;
            try
            {
                _slots.Sort((a, b) => (a.bounds.center - cam.transform.position).sqrMagnitude.CompareTo((b.bounds.center - cam.transform.position).sqrMagnitude));
                foreach (var c in _slots)
                {
                    if (labels >= MaxLabels) break;
                    if (c == null || !c.enabled || !c.gameObject.activeInHierarchy) continue;
                    var ps = c.GetComponent<partslotscript>();
                    if (ps == null) continue;
                    bool takes = held != null && ps.CanCraft(held);
                    if (held != null ? !takes : ps.hasPart()) continue;   // only what matters now
                    Label(cam, c.bounds.center, Name(c), held != null ? (ps == chosen ? Now : Takes) : Empty);
                    labels++;
                }
                if (held != null && held.isPart)
                {
                    string s = chosen != null ? "attaches to " + Name(chosen.GetComponent<Collider>() != null ? chosen.GetComponent<Collider>() : nearest)
                        : nearest != null ? Name(nearest) + " " + nearestGap.ToString("0.00", IC) + " m away, needs " + radius.ToString("0.00", IC) + " m"
                        : "no slot nearby takes this part";
                    Label(cam, held.transform.position, s, chosen != null ? Now : Miss);
                }
            }
            finally { GUI.color = colourBefore; }
        }

        private static string Name(Collider c)
        {
            if (c == null) return "?";
            var t = c.transform;
            // "FrontBumperSlot/FrontBumperSlotCollider" reads as the slot's own name
            return t.parent != null && t.name.StartsWith(t.parent.name, System.StringComparison.Ordinal) ? t.parent.name : t.name;
        }

        private static void Init()
        {
            if (_lines != null) return;
            var shader = Shader.Find("Hidden/Internal-Colored");
            if (shader == null) return;
            _lines = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            _lines.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            _lines.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            _lines.SetInt("_Cull", (int)UnityEngine.Rendering.CullMode.Off);
            _lines.SetInt("_ZWrite", 0); _lines.SetInt("_ZTest", (int)UnityEngine.Rendering.CompareFunction.Always);
        }

        private static void Wire(Camera cam, Collider c, Color colour)
        {
            Vector3 centre, half; Transform t;
            if (c is BoxCollider b) { centre = b.center; half = b.size * .5f; t = c.transform; }
            else { var w = c.bounds; centre = w.center; half = w.extents; t = null; }
            Vector3 Corner(int i)
            {
                var local = centre + new Vector3((i & 1) == 0 ? -half.x : half.x, (i & 2) == 0 ? -half.y : half.y, (i & 4) == 0 ? -half.z : half.z);
                return t != null ? t.TransformPoint(local) : local;
            }
            for (int i = 0; i < 8; i++) for (int bit = 1; bit <= 4; bit *= 2)
                if ((i & bit) == 0) Segment(cam, Corner(i), Corner(i | bit), colour);
        }

        private static void Circle(Camera cam, Vector3 at, Vector3 axis, float r, Color colour)
        {
            var u = Vector3.Cross(axis, Mathf.Abs(axis.y) < .9f ? Vector3.up : Vector3.right).normalized; var v = Vector3.Cross(axis, u);
            const int n = 32;
            for (int i = 0; i < n; i++)
            {
                float a = i * Mathf.PI * 2 / n, b = (i + 1) * Mathf.PI * 2 / n;
                Segment(cam, at + (u * Mathf.Cos(a) + v * Mathf.Sin(a)) * r, at + (u * Mathf.Cos(b) + v * Mathf.Sin(b)) * r, colour);
            }
        }

        private static void Segment(Camera cam, Vector3 a, Vector3 b, Color colour)
        {
            Vector3 ca = cam.WorldToScreenPoint(a), cb = cam.WorldToScreenPoint(b);
            if (ca.z <= cam.nearClipPlane || cb.z <= cam.nearClipPlane) return;
            GL.Color(colour); GL.Vertex3(ca.x, Screen.height - ca.y, 0); GL.Vertex3(cb.x, Screen.height - cb.y, 0);
        }

        private static void Label(Camera cam, Vector3 world, string text, Color colour)
        {
            var p = cam.WorldToScreenPoint(world);
            if (p.z <= cam.nearClipPlane) return;
            _text.text = text;
            var size = OverlayStyle.Small.CalcSize(_text);
            var rect = new Rect(p.x - size.x * .5f - 4, Screen.height - p.y - size.y - 6, size.x + 8, size.y + 4);
            var box = new DebugBox(rect.x, rect.y, rect.width, rect.height);
            if (!DebugLayout.Labels.Place(ref box, Screen.width, Screen.height)) return;
            rect.x = box.X; rect.y = box.Y;
            OverlayStyle.Back(rect);
            OverlayStyle.Outline(rect, colour, 1);
            GUI.color = colour;
            GUI.Label(new Rect(rect.x + 4, rect.y + 2, size.x, size.y), _text, OverlayStyle.Small);
        }
    }
}
