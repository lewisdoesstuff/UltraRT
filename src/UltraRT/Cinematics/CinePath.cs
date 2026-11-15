using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Newtonsoft.Json;
using UnityEngine;

namespace UltraRT.Cinematics
{
    /// <summary>How a segment's time maps to its progress (docs/cinematics.md, "Keys and segments").</summary>
    public enum CineEase
    {
        /// <summary>Constant speed along the segment (arc length).</summary>
        Linear = 0,
        /// <summary>Starts and stops gently (smoothstep).</summary>
        Smooth = 1,
        /// <summary>Starts slowly (ease in), full speed at the end.</summary>
        In = 2,
        /// <summary>Full speed at the start, stops gently (ease out).</summary>
        Out = 3,
        /// <summary>Holds this key for the segment's duration, then cuts to the next key.</summary>
        Cut = 4,
    }

    /// <summary>One camera key. Position + Rotation is what the editor writes; hand-written files may give Player (feet, as
    /// a pose's Position) instead of Position, and Yaw / Pitch / Roll instead of Rotation.</summary>
    public sealed class CineKey
    {
        public float[] Position;
        /// <summary>Quaternion x, y, z, w (world).</summary>
        public float[] Rotation;
        /// <summary>Player feet position (a pose's Position): the camera goes to it plus the eye offset (CinePath.EyeOffset).</summary>
        public float[] Player;
        public float Yaw, Pitch, Roll;
        /// <summary>Vertical field of view (degrees), as Camera.fieldOfView. 0 = the game's.</summary>
        public float Fov;
        /// <summary>Seconds from this key to the next (unused on the last key).</summary>
        public float Duration = 2f;
        public string Ease = "linear";

        [JsonIgnore] public Vector3 Pos;
        [JsonIgnore] public Quaternion Rot = Quaternion.identity;
        [JsonIgnore] public CineEase EaseMode;

        public static CineKey From(Vector3 pos, Quaternion rot, float fov, float duration, CineEase ease)
        {
            var k = new CineKey { Pos = pos, Rot = rot, Fov = fov, Duration = duration, EaseMode = ease };
            k.Store();
            return k;
        }

        /// <summary>Runtime fields -> the serialised arrays.</summary>
        public void Store()
        {
            Position = new[] { R(Pos.x), R(Pos.y), R(Pos.z) };
            Rotation = new[] { R5(Rot.x), R5(Rot.y), R5(Rot.z), R5(Rot.w) };
            Player = null;
            var e = Rot.eulerAngles;
            Yaw = R(e.y); Pitch = R(-Mathf.DeltaAngle(0f, e.x)); Roll = R(Mathf.DeltaAngle(0f, e.z));
            Ease = EaseMode.ToString().ToLowerInvariant();
            Fov = R(Fov);
            Duration = (float)Math.Round(Duration, 3);
        }

        private static float R(float v) => (float)Math.Round(v, 3);
        private static float R5(float v) => (float)Math.Round(v, 6);

        /// <summary>Serialised fields -> runtime fields. <paramref name="eye"/>: camera minus player position (Player keys).</summary>
        public void Resolve(Vector3 eye)
        {
            if (Position != null && Position.Length >= 3) Pos = new Vector3(Position[0], Position[1], Position[2]);
            else if (Player != null && Player.Length >= 3) Pos = new Vector3(Player[0], Player[1], Player[2]) + eye;
            Rot = Rotation != null && Rotation.Length >= 4
                ? Normalize(new Quaternion(Rotation[0], Rotation[1], Rotation[2], Rotation[3]))
                : Quaternion.Euler(-Pitch, Yaw, Roll);
            EaseMode = Enum.TryParse(Ease ?? "linear", true, out CineEase m) ? m : CineEase.Linear;
            Duration = Mathf.Max(0.05f, Duration);
        }

        public CineKey Clone()
        {
            var k = (CineKey)MemberwiseClone();
            k.Store();
            return k;
        }

        internal static Quaternion Normalize(Quaternion q)
        {
            float n = Mathf.Sqrt(q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w);
            return n < 1e-6f ? Quaternion.identity : new Quaternion(q.x / n, q.y / n, q.z / n, q.w / n);
        }
    }

    /// <summary>A saved camera path: BepInEx\ultrart\cinematics\&lt;level&gt;\&lt;name&gt;.json (docs/cinematics.md).</summary>
    public sealed class CinePathFile
    {
        public int Version = 1;
        public string Name;
        public string Scene;
        public string Created;
        /// <summary>The level state when it was saved, as F11 saves it with a pose: the route walked since the level loaded
        /// (approach_*.json) and what area triggers had switched (areas_*.json), both in BepInEx\ultrart\recordings. The
        /// bench's cinematic scenarios load the level with them (bench/README.md, "Cinematics").</summary>
        public string Approach, Areas, Checkpoint;
        /// <summary>Camera minus player position when it was saved (Player keys use it when the game is not running).</summary>
        public float[] EyeOffset;
        public List<CineKey> Keys = new List<CineKey>();

        public static CinePathFile Load(string path) =>
            JsonConvert.DeserializeObject<CinePathFile>(File.ReadAllText(path), new JsonSerializerSettings { ObjectCreationHandling = ObjectCreationHandling.Replace });

        public void Save(string path)
        {
            foreach (var k in Keys) k.Store();
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
            File.WriteAllText(path, JsonConvert.SerializeObject(this, Formatting.Indented, new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore }));
        }
    }

    /// <summary>A camera sample: position, rotation, vertical FOV.</summary>
    public struct CineSample
    {
        public Vector3 Position;
        public Quaternion Rotation;
        public float Fov;
    }

    /// <summary>
    /// Evaluates a key list as a smooth camera path: centripetal Catmull-Rom on position (re-parameterised by arc length,
    /// so "linear" segments move at constant speed), SQUAD on rotation, Catmull-Rom on FOV, each segment with its own
    /// duration and easing. A Cut segment holds its first key and starts a new spline at the next one. Everything is a pure
    /// function of time: the same t always gives the same sample (no state, no frame-rate dependence).
    /// </summary>
    public sealed class CineSpline
    {
        private readonly List<CineKey> _k;
        private readonly float[] _start;            // segment start times
        private readonly Quaternion[] _q;           // hemisphere-aligned rotations
        private readonly Quaternion[] _a;           // SQUAD inner control points
        private readonly float[][] _arc;            // per segment: cumulative length at u = i / ArcSteps
        private const int ArcSteps = 48;
        public float Duration { get; }
        public int Count => _k.Count;
        public float DefaultFov = 105f;

        public CineSpline(List<CineKey> keys, float defaultFov)
        {
            _k = keys ?? throw new ArgumentNullException(nameof(keys));
            DefaultFov = defaultFov;
            int n = _k.Count;
            _start = new float[Math.Max(1, n)];
            float t = 0f;
            for (int i = 0; i < n; i++)
            {
                _start[i] = t;
                if (i < n - 1) t += Mathf.Max(0.05f, _k[i].Duration);
            }
            Duration = t;
            _q = new Quaternion[n];
            for (int i = 0; i < n; i++)
            {
                var q = CineKey.Normalize(_k[i].Rot);
                if (i > 0 && !IsCut(i - 1) && Dot(_q[i - 1], q) < 0f) q = Neg(q);
                _q[i] = q;
            }
            _a = new Quaternion[n];
            for (int i = 0; i < n; i++)
            {
                int p = Prev(i), nx = Next(i);
                if (p == i || nx == i) { _a[i] = _q[i]; continue; }
                var inv = Quaternion.Inverse(_q[i]);
                var qp = _q[p];
                var qn = _q[nx];
                var l = Add(Log(inv * qp), Log(inv * qn));
                _a[i] = CineKey.Normalize(_q[i] * Exp(Scale(l, -0.25f)));
            }
            _arc = new float[Math.Max(0, n - 1)][];
            for (int s = 0; s < n - 1; s++)
            {
                var arc = new float[ArcSteps + 1];
                var last = SegmentPosition(s, 0f);
                for (int j = 1; j <= ArcSteps; j++)
                {
                    var p = SegmentPosition(s, j / (float)ArcSteps);
                    arc[j] = arc[j - 1] + Vector3.Distance(last, p);
                    last = p;
                }
                _arc[s] = arc;
            }
        }

        public float KeyTime(int i) => _k.Count == 0 ? 0f : _start[Mathf.Clamp(i, 0, _k.Count - 1)];
        public float SegmentLength(int s) => s >= 0 && s < _arc.Length ? _arc[s][ArcSteps] : 0f;

        private bool IsCut(int seg) => seg >= 0 && seg < _k.Count - 1 && _k[seg].EaseMode == CineEase.Cut;
        /// <summary>The spline neighbour before key i (itself at a path start or after a cut).</summary>
        private int Prev(int i) => i > 0 && !IsCut(i - 1) ? i - 1 : i;
        private int Next(int i) => i < _k.Count - 1 && !IsCut(i) ? i + 1 : i;

        public CineSample Sample(float t)
        {
            int n = _k.Count;
            if (n == 0) return new CineSample { Rotation = Quaternion.identity, Fov = DefaultFov };
            if (n == 1 || t <= 0f) return KeySample(0);
            if (t >= Duration) return KeySample(n - 1);
            int s = Segment(t);
            float d = Mathf.Max(0.05f, _k[s].Duration);
            float f = Mathf.Clamp01((t - _start[s]) / d);
            var ease = _k[s].EaseMode;
            if (ease == CineEase.Cut) return KeySample(s);
            float e = Ease(ease, f);
            float u = ArcToParam(s, e);
            return new CineSample
            {
                Position = SegmentPosition(s, u),
                Rotation = Squad(s, e),
                Fov = SegmentFov(s, e),
            };
        }

        public CineSample KeySample(int i)
        {
            var k = _k[Mathf.Clamp(i, 0, _k.Count - 1)];
            return new CineSample { Position = k.Pos, Rotation = CineKey.Normalize(k.Rot), Fov = k.Fov > 1f ? k.Fov : DefaultFov };
        }

        /// <summary>The segment containing t (the last one at the end).</summary>
        public int Segment(float t)
        {
            int n = _k.Count;
            if (n < 2) return 0;
            int lo = 0, hi = n - 2;
            while (lo < hi)
            {
                int mid = (lo + hi + 1) / 2;
                if (_start[mid] <= t) lo = mid; else hi = mid - 1;
            }
            return lo;
        }

        public static float Ease(CineEase e, float f)
        {
            switch (e)
            {
                case CineEase.Smooth: return f * f * (3f - 2f * f);
                case CineEase.In: return f * f;
                case CineEase.Out: return 1f - (1f - f) * (1f - f);
                case CineEase.Cut: return 0f;
                default: return f;
            }
        }

        private float ArcToParam(int s, float frac)
        {
            var arc = _arc[s];
            float total = arc[ArcSteps];
            if (total < 1e-4f) return frac; // the keys share a position: rotation / FOV only
            float target = frac * total;
            int lo = 0, hi = ArcSteps;
            while (hi - lo > 1)
            {
                int mid = (lo + hi) / 2;
                if (arc[mid] < target) lo = mid; else hi = mid;
            }
            float seg = arc[hi] - arc[lo];
            float w = seg > 1e-6f ? (target - arc[lo]) / seg : 0f;
            return (lo + w) / ArcSteps;
        }

        /// <summary>Centripetal Catmull-Rom (alpha 0.5, Barry-Goldman) between keys s and s + 1, with mirrored end points.</summary>
        private Vector3 SegmentPosition(int s, float u)
        {
            var p1 = _k[s].Pos;
            var p2 = _k[s + 1].Pos;
            int ip = Prev(s), inx = Next(s + 1);
            var p0 = ip != s ? _k[ip].Pos : p1 + (p1 - p2);
            var p3 = inx != s + 1 ? _k[inx].Pos : p2 + (p2 - p1);
            if ((p2 - p1).sqrMagnitude < 1e-8f) return p1;
            float t0 = 0f;
            float t1 = t0 + Knot(p0, p1);
            float t2 = t1 + Knot(p1, p2);
            float t3 = t2 + Knot(p2, p3);
            float t = Mathf.Lerp(t1, t2, u);
            var a1 = Lerp(p0, p1, t0, t1, t);
            var a2 = Lerp(p1, p2, t1, t2, t);
            var a3 = Lerp(p2, p3, t2, t3, t);
            var b1 = Lerp(a1, a2, t0, t2, t);
            var b2 = Lerp(a2, a3, t1, t3, t);
            return Lerp(b1, b2, t1, t2, t);
        }

        private static float Knot(Vector3 a, Vector3 b) => Mathf.Max(1e-3f, Mathf.Sqrt(Vector3.Distance(a, b)));

        private static Vector3 Lerp(Vector3 a, Vector3 b, float ta, float tb, float t) =>
            tb - ta < 1e-6f ? a : a * ((tb - t) / (tb - ta)) + b * ((t - ta) / (tb - ta));

        private float SegmentFov(int s, float u)
        {
            float F(int i) => _k[i].Fov > 1f ? _k[i].Fov : DefaultFov;
            float f1 = F(s), f2 = F(s + 1);
            int ip = Prev(s), inx = Next(s + 1);
            float f0 = ip != s ? F(ip) : f1, f3 = inx != s + 1 ? F(inx) : f2;
            float u2 = u * u, u3 = u2 * u;
            float v = 0.5f * (2f * f1 + (-f0 + f2) * u + (2f * f0 - 5f * f1 + 4f * f2 - f3) * u2 + (-f0 + 3f * f1 - 3f * f2 + f3) * u3);
            return Mathf.Clamp(v, Mathf.Min(f1, f2) - 5f, Mathf.Max(f1, f2) + 5f);
        }

        private Quaternion Squad(int s, float h)
        {
            var q1 = _q[s];
            var q2 = _q[s + 1];
            if (Dot(q1, q2) < 0f) q2 = Neg(q2); // only after a cut, where the alignment above was skipped
            var outer = SlerpRaw(q1, q2, h);
            var inner = SlerpRaw(_a[s], _a[s + 1], h);
            return CineKey.Normalize(SlerpRaw(outer, inner, 2f * h * (1f - h)));
        }

        // ---- quaternion helpers (no shortest-path flips inside SQUAD: the inputs are aligned once, above)

        private static float Dot(Quaternion a, Quaternion b) => a.x * b.x + a.y * b.y + a.z * b.z + a.w * b.w;
        private static Quaternion Neg(Quaternion q) => new Quaternion(-q.x, -q.y, -q.z, -q.w);
        private static Quaternion Scale(Quaternion q, float s) => new Quaternion(q.x * s, q.y * s, q.z * s, q.w * s);
        private static Quaternion Add(Quaternion a, Quaternion b) => new Quaternion(a.x + b.x, a.y + b.y, a.z + b.z, a.w + b.w);

        private static Quaternion SlerpRaw(Quaternion a, Quaternion b, float t)
        {
            float d = Mathf.Clamp(Dot(a, b), -1f, 1f);
            if (d > 0.9995f) return CineKey.Normalize(Add(Scale(a, 1f - t), Scale(b, t)));
            float th = Mathf.Acos(d);
            float sn = Mathf.Sin(th);
            return Add(Scale(a, Mathf.Sin((1f - t) * th) / sn), Scale(b, Mathf.Sin(t * th) / sn));
        }

        /// <summary>Log of a unit quaternion (pure: w = 0).</summary>
        private static Quaternion Log(Quaternion q)
        {
            if (q.w < 0f) q = Neg(q);
            float v = Mathf.Sqrt(q.x * q.x + q.y * q.y + q.z * q.z);
            if (v < 1e-6f) return new Quaternion(0, 0, 0, 0);
            float a = Mathf.Atan2(v, q.w) / v;
            return new Quaternion(q.x * a, q.y * a, q.z * a, 0f);
        }

        private static Quaternion Exp(Quaternion q)
        {
            float a = Mathf.Sqrt(q.x * q.x + q.y * q.y + q.z * q.z);
            if (a < 1e-6f) return Quaternion.identity;
            float s = Mathf.Sin(a) / a;
            return new Quaternion(q.x * s, q.y * s, q.z * s, Mathf.Cos(a));
        }
    }

    internal static class CineFormat
    {
        public static string Time(float s) => s < 60f ? s.ToString("F1", CultureInfo.InvariantCulture) + "s"
            : $"{(int)(s / 60f)}m{(int)(s % 60f):00}s";
    }
}
