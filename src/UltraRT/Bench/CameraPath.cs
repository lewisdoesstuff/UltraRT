using System;
using System.Collections.Generic;
using UnityEngine;

namespace UltraRT.Bench
{
    /// <summary>Player position (feet, as NewMovement) plus CameraController yaw (rotationY) and pitch (rotationX, + = up).</summary>
    public struct CamPose
    {
        public Vector3 Position;
        public float Yaw, Pitch;

        public CamPose(Vector3 position, float yaw, float pitch) { Position = position; Yaw = yaw; Pitch = pitch; }
    }

    public struct PathKey
    {
        public float Time;
        public CamPose Pose;
    }

    /// <summary>Timed keyframes: Catmull-Rom on position, linear on (unwrapped) angles.</summary>
    public sealed class CameraPath
    {
        private readonly List<PathKey> _keys;
        public float Duration => _keys.Count == 0 ? 0f : _keys[_keys.Count - 1].Time;
        public CamPose Start => _keys[0].Pose;

        public CameraPath(List<PathKey> keys)
        {
            if (keys == null || keys.Count == 0) throw new ArgumentException("path needs at least one key");
            _keys = keys;
            // Unwrap yaw so interpolation never takes the long way round.
            for (int i = 1; i < _keys.Count; i++)
            {
                var k = _keys[i];
                k.Pose.Yaw = _keys[i - 1].Pose.Yaw + Mathf.DeltaAngle(_keys[i - 1].Pose.Yaw, k.Pose.Yaw);
                _keys[i] = k;
            }
        }

        public static CameraPath Static(CamPose pose, float duration) =>
            new CameraPath(new List<PathKey> { new PathKey { Time = 0, Pose = pose }, new PathKey { Time = duration, Pose = pose } });

        /// <summary>Stand still and look around: full yaw turns plus a pitch wobble. Stresses reprojection.</summary>
        public static CameraPath Sweep(CamPose origin, float duration, float yawDegPerSec, float pitchAmplitude)
        {
            var keys = new List<PathKey>();
            for (float t = 0; t <= duration + 1e-3f; t += 1f / 30f)
            {
                var p = origin;
                p.Yaw += yawDegPerSec * t;
                p.Pitch = Mathf.Clamp(origin.Pitch + pitchAmplitude * Mathf.Sin(t * 1.7f), -89f, 89f);
                keys.Add(new PathKey { Time = t, Pose = p });
            }
            return new CameraPath(keys);
        }

        public CamPose Sample(float t)
        {
            if (_keys.Count == 1 || t <= 0) return _keys[0].Pose;
            if (t >= Duration) return _keys[_keys.Count - 1].Pose;

            int i = FindSegment(t);
            var k1 = _keys[i];
            var k2 = _keys[i + 1];
            float u = (t - k1.Time) / Mathf.Max(k2.Time - k1.Time, 1e-6f);
            var k0 = _keys[Math.Max(i - 1, 0)];
            var k3 = _keys[Math.Min(i + 2, _keys.Count - 1)];
            return new CamPose(
                CatmullRom(k0.Pose.Position, k1.Pose.Position, k2.Pose.Position, k3.Pose.Position, u),
                Mathf.Lerp(k1.Pose.Yaw, k2.Pose.Yaw, u),
                Mathf.Lerp(k1.Pose.Pitch, k2.Pose.Pitch, u));
        }

        private int _lastSegment;

        private int FindSegment(float t)
        {
            int i = Mathf.Clamp(_lastSegment, 0, _keys.Count - 2);
            if (_keys[i].Time > t) i = 0;
            while (i < _keys.Count - 2 && _keys[i + 1].Time <= t) i++;
            _lastSegment = i;
            return i;
        }

        private static Vector3 CatmullRom(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, float t)
        {
            float t2 = t * t, t3 = t2 * t;
            return 0.5f * (2f * p1 + (p2 - p0) * t + (2f * p0 - 5f * p1 + 4f * p2 - p3) * t2 + (3f * p1 - p0 - 3f * p2 + p3) * t3);
        }
    }
}
