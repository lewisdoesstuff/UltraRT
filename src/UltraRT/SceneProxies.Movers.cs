using System.Collections.Generic;
using UnityEngine;

namespace UltraRT
{
    /// <summary>
    /// Moving occluders (docs/denoiser.md, "Moving shadows"). Every frame, up to <see cref="MaxMovers"/> world AABBs of
    /// dynamic objects that moved recently: the player's third-person model first (its shadow is the one the player looks at
    /// all the time), then the nearest moving enemies and props. Each box is the union of this frame's and last frame's
    /// bounds, so it covers both where a shadow arrives and where it leaves. PathTrace tests its direct-light shadow
    /// segment against them (UltraRTLights.hlsl, MoverCrosses): and
    /// the pixel is flagged for the denoiser's targeted anti-lag.
    /// </summary>
    internal sealed partial class SceneProxies
    {
        /// <summary>Boxes per frame (shader loop length). 0 = off.</summary>
        public static int MaxMovers = 8;
        /// <summary>Objects farther than this from the camera, or larger than <see cref="MoverMaxSize"/>, are not listed.</summary>
        public static float MoverRange = 60f, MoverMaxSize = 16f;
        /// <summary>Frames an object stays listed after it stopped (the last stale history gets cleared too).</summary>
        public static int MoverHoldFrames = 3;
        /// <summary>Movement that counts (m of bounds centre or extent per frame).</summary>
        public static float MoverEpsilon = 0.003f;
        /// <summary>Include objects other than the player (enemies, doors, platforms).</summary>
        public static bool MoverOthers = true;

        private readonly Dictionary<DynamicProxy, (Bounds b, int hold)> _moverState = new Dictionary<DynamicProxy, (Bounds, int)>();
        private Bounds _playerMoverPrev;
        private bool _playerMoverValid;
        private int _playerMoverHold;
        private readonly List<(float d, Bounds b)> _moverCandidates = new List<(float, Bounds)>();
        private readonly List<DynamicProxy> _moverGone = new List<DynamicProxy>();

        /// <summary>V1's model bounds this frame (union of its visible proxies), for the player shadow split; null = none.</summary>
        public Bounds? PlayerModelBounds { get; private set; }

        private static bool BoundsMoved(Bounds a, Bounds b) =>
            (a.center - b.center).sqrMagnitude > MoverEpsilon * MoverEpsilon || (a.extents - b.extents).sqrMagnitude > MoverEpsilon * MoverEpsilon;

        private static Bounds Union(Bounds a, Bounds b) { a.Encapsulate(b); return a; }

        /// <summary>Fills <paramref name="data"/> with min, max pairs (2 float4 per box); returns the box count.</summary>
        public int CollectMovers(Vector3 camPos, Vector4[] data)
        {
            int max = Mathf.Min(MaxMovers, data.Length / 2);
            int n = 0;

            // The player's model: the union of its visible proxies' bounds.
            bool anyPlayer = false;
            var pb = new Bounds();
            foreach (var d in _playerProxies)
            {
                if (!d.Visible || d.Source == null) continue;
                var b = d.Source.bounds;
                if (!anyPlayer) { pb = b; anyPlayer = true; } else pb.Encapsulate(b);
            }
            PlayerModelBounds = anyPlayer ? pb : (Bounds?)null;
            if (anyPlayer)
            {
                if (_playerMoverValid && BoundsMoved(pb, _playerMoverPrev)) _playerMoverHold = MoverHoldFrames;
                if (_playerMoverValid && _playerMoverHold > 0 && n < max)
                {
                    var u = Union(pb, _playerMoverPrev);
                    data[2 * n] = u.min; data[2 * n + 1] = u.max; n++;
                    _playerMoverHold--;
                }
                _playerMoverPrev = pb;
                _playerMoverValid = true;
            }
            else _playerMoverValid = false;

            if (!MoverOthers || n >= max) return n;
            _moverCandidates.Clear();
            float range2 = MoverRange * MoverRange;
            foreach (var d in _active)
            {
                if (d.Source == null || d.Mask == PlayerMask) continue;
                bool moving = d.Moved || (d.SkinnedProxy != null && d.CurM != d.PrevM);
                bool known = _moverState.TryGetValue(d, out var st);
                if (!moving && !known) continue;
                var b = d.Source.bounds;
                if (b.size.x > MoverMaxSize || b.size.y > MoverMaxSize || b.size.z > MoverMaxSize) continue;
                int hold = moving ? MoverHoldFrames : st.hold - 1;
                if (hold <= 0) { _moverGone.Add(d); continue; }
                var u = known ? Union(b, st.b) : b;
                _moverState[d] = (b, hold);
                float d2 = PortalView.SqrDistance(u, camPos); // movers seen through a portal count as near too
                if (d2 <= range2) _moverCandidates.Add((d2, u));
            }
            foreach (var d in _moverGone) _moverState.Remove(d);
            _moverGone.Clear();
            if (_moverState.Count > 512) _moverState.Clear(); // proxies that left the RTAS while listed
            _moverCandidates.Sort((a, b) => a.d.CompareTo(b.d));
            foreach (var c in _moverCandidates)
            {
                if (n >= max) break;
                data[2 * n] = c.b.min; data[2 * n + 1] = c.b.max; n++;
            }
            return n;
        }
    }
}
