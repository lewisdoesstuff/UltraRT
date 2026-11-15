using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using BepInEx.Logging;
using UnityEngine;
using UnityEngine.Rendering;

namespace UltraRT
{
    /// <summary>Tunables for <see cref="LightShafts"/> (docs/volumetrics.md, "Lamp cones").</summary>
    public sealed class LightShaftSettings
    {
        /// <summary>Replace the game's fake light cones (LightShaft meshes) with volumetric cones of their lights.</summary>
        public bool enabled = true;
        /// <summary>Scattering of a shaft light relative to a light whose haze weight is 1 (replaces the range weight).</summary>
        public float strength = 3f;
        /// <summary>Soft cone edge: the scattering fades in over the outer (1 - edge) of the cone's half angle.</summary>
        public float edge = 0.75f;
        /// <summary>Keep drawing the game's cone meshes on top (A/B and fallback).</summary>
        public bool keepFakes = false;
        /// <summary>Beam meshes parented under a spot light (3-1's eyes: nested `LightPillar 1F NoFog` frusta under the eye's
        /// Spot Light, whose narrow end does not reach the light) are matched to that spot: the cone is the widest of them,
        /// from the light. Off: only meshes whose apex is at a light (the original fit).</summary>
        public bool parentSpots = true;
        /// <summary>Beam channel (docs/volumetrics.md "Eye beams"): beam meshes matched through their parent spot (3-1's eye
        /// beams) are drawn by the volumetric pass instead of the game: each mesh's convex hull is intersected per screen
        /// pixel and adds the game's colour once per face the game would draw (the same additive look, nested layers and
        /// all), times a traced visibility: the lit fraction of the pixel's ray inside the beam, from shadow rays to the
        /// light at low resolution, filtered over time. Whatever stands in the beam cuts shafts through it. Off: the game's
        /// meshes stay.</summary>
        public bool beams = true;
        /// <summary>Beam channel brightness relative to the game's meshes (1 = the same).</summary>
        public float beamStrength = 1f;
        /// <summary>Beam channel shadowing: 1 = traced visibility, 0 = none (the game's look, for A/B), in between = a mix.</summary>
        public float beamShadows = 1f;
        /// <summary>A-trous passes over the beam visibility (0-5; sharper shafts with fewer, smoother with more).</summary>
        public int beamAtrous = 2;
        /// <summary>Shadow rays per beam mesh per low-res pixel and frame.</summary>
        public int beamSamples = 1;
        /// <summary>Most beam meshes drawn at once (nearest first; the rest keep the game's meshes). At most 32.</summary>
        public int maxBeamHulls = 32;
        /// <summary>The beam channel's colour: 1 = the game's own beam meshes drawn with their material into a buffer (against
        /// the game's depth), exactly the game's look; 0 = the analytic hulls (vertex colours fitted per face; misses the
        /// Master shader's distance fade: far faces up to 3x too bright).</summary>
        public int beamMeshes = 1;
        /// <summary>Diagnostics for the analytic colour: which faces it draws (0 = as the material says, 1 = entering only,
        /// 2 = leaving only).</summary>
        public int beamFaces = 0;
        /// <summary>The housing a parent-spot beam's light sits in (3-1's 39 m eyeball and its socket) stops blocking that
        /// light (SceneProxies.AddHousing): the eye lights the room, and the ribs cast shadows on the floor.</summary>
        public bool housings = true;
    }

    /// <summary>
    /// The game fakes lamp light cones with additive transparent meshes (8-2's streetlights: `LightShaft`, material
    /// `LightPillarStreetlight`, a static mesh hanging from a point light of range 30). With ray-traced volumetrics the
    /// light itself can scatter in the air, but a point light scatters a round halo, and the cone mesh stays drawn on top
    /// whatever the light does. Here every such mesh is matched once per scene to the light at its apex and a cone is
    /// fitted to its vertices (axis = mean direction from the light, half angle = the widest of the far vertices). The
    /// volumetric pass then clips that light's scattering to the cone (and gives it <see cref="LightShaftSettings.strength"/>
    /// in place of the haze range weight, which nearly removes 30 m lights), and the fake mesh is hidden while the
    /// volumetric pass runs. Surfaces keep the game's point light. Vertex data comes from a one-off GPU readback (the
    /// meshes are static-batched and not readable).
    /// </summary>
    internal sealed class LightShafts
    {
        public LightShaftSettings Settings = new LightShaftSettings();

        public sealed class Cone
        {
            public Light Light;
            public Vector3 Apex;      // light position when fitted
            public Vector3 Axis;      // away from the light
            public float CosHalf, Sharp, Length;
            /// <summary>Matched through the parent spot: the beams move with the light, so the cone follows it (axis in the
            /// light's space) instead of being dropped when the light moves.</summary>
            public bool Parented;
            public Vector3 LocalAxis;
            public Vector3 WorldAxis => Parented && Light != null ? Light.transform.TransformDirection(LocalAxis) : Axis;
            /// <summary>Parent-spot beams: where the meshes begin along the axis (m from the light: past the light's own
            /// housing, the eyeball), and the colour (display) and per-layer alpha the game draws them with.</summary>
            public float Start = float.MaxValue;
            public Color BeamColor = Color.black;
            public float BeamAlpha;
            public bool InPlace => Light != null && (Parented || (Light.transform.position - Apex).sqrMagnitude <= 0.25f);
            public readonly List<Renderer> Fakes = new List<Renderer>();
            /// <summary>Parent-spot beams: the convex hull of each beam mesh (the beam channel draws these).</summary>
            public readonly List<BeamHull> Hulls = new List<BeamHull>();
        }

        /// <summary>
        /// One beam mesh as the beam channel draws it: the convex hull of its vertices as planes (outward normal n, offset d:
        /// inside where n.x &lt;= d) in its light's local space (it moves with the light) or in world space (static-batched
        /// meshes cannot move), and how the game draws it: colour x alpha (display, additive), which faces (culling),
        /// fog, and its near-camera distance fade.
        /// </summary>
        public sealed class BeamHull
        {
            public Renderer Renderer;
            public Light Light;
            public bool Local;                    // planes in the light's space
            public Vector4[] Planes;              // xyz normal, w d
            public Vector3 Center;                // bounding sphere (same space as the planes)
            public float Radius;
            public float Start;                   // m along the light's beam axis where the mesh begins (shadow rays stop there)
            public Color Add;                     // rgb added per face drawn (display units: colour x alpha x vertex hue)
            public Vector4[] FaceGrad;            // per plane, vertex colour brightness on that face at x (same space as the
                                                  // planes): w + dot(xyz, x); zero: the mesh has no face there
            public bool Entry = true, Exit = true; // faces the game draws: entering (front) and leaving (back) the hull
            public bool Fog;
            public float FadeNear, FadeFar;       // DISTANCE_FADE: invisible within FadeNear m of the camera, full at FadeFar (0, 0 = off)
            public int LastDrawn = -100;          // frame the beam channel last drew it

            /// <summary>Whether the game would draw it now (its object active; its renderer enabled, or hidden by us).</summary>
            public bool Live(bool hiddenByUs) => Renderer != null && Light != null && Light.isActiveAndEnabled &&
                                                 Renderer.gameObject.activeInHierarchy && (Renderer.enabled || hiddenByUs);
        }

        private static readonly PropertyInfo StaticBatchRoot =
            typeof(Renderer).GetProperty("staticBatchRootTransform", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly string[] ShaftNames = { "lightshaft", "lightpillar", "light shaft", "lightray", "light ray", "godray", "shaft" };

        private readonly ManualLogSource _log;
        private readonly Dictionary<Light, Cone> _cones = new Dictionary<Light, Cone>();
        private readonly Dictionary<Mesh, Vector3[]> _meshCache = new Dictionary<Mesh, Vector3[]>();
        private readonly List<Renderer> _hidden = new List<Renderer>();
        private readonly List<Vector3> _tmp = new List<Vector3>(256);
        private object _scene;
        private bool _hide;
        private float _nextStaleCheck;
        private readonly HashSet<Renderer> _hiddenBeams = new HashSet<Renderer>();
        private readonly HashSet<Renderer> _beamKeep = new HashSet<Renderer>();
        private readonly List<Renderer> _tmpShow = new List<Renderer>();

        public int Count => _cones.Count;

        public LightShafts(ManualLogSource log) { _log = log; }

        public void Clear()
        {
            ShowFakes();
            _cones.Clear();
            _meshCache.Clear();
            _scene = null;
        }

        public bool TryGet(Light l, out Cone c) => _cones.TryGetValue(l, out c);
        public IEnumerable<Cone> Cones => _cones.Values;
        /// <summary>Bumped whenever the cone set is fitted again.</summary>
        public int Version { get; private set; }

        /// <summary>Once per RT scene (lazily, the first frame the volumetric pass runs in it).</summary>
        public void EnsureBuilt(object scene, IList<Light> lights)
        {
            if (scene == null) return;
            if (ReferenceEquals(scene, _scene))
            {
                // A matched light or fake was destroyed (a checkpoint respawn replaces its room with a fresh clone): fit again,
                // so the clone's beams are matched too. At most once a second, and only then (the scan takes 10-25 ms).
                if (Time.realtimeSinceStartup < _nextStaleCheck) return;
                _nextStaleCheck = Time.realtimeSinceStartup + 1f;
                bool stale = false;
                foreach (var c in _cones.Values)
                {
                    if (c.Light == null) { stale = true; break; }
                    foreach (var f in c.Fakes) if (f == null) { stale = true; break; }
                    if (stale) break;
                }
                if (!stale) return;
                _log.LogInfo("Light shafts: a matched light or cone mesh is gone; fitting again");
            }
            ShowFakes();
            _cones.Clear();
            _meshCache.Clear();
            _scene = scene;
            var sw = Stopwatch.StartNew();
            int candidates = 0, rejected = 0;
            foreach (var r in UnityEngine.Object.FindObjectsOfType<MeshRenderer>(true))
            {
                if (!IsShaft(r)) continue;
                candidates++;
                try { if (!Match(r, lights)) { rejected++; if (rejected <= 8) LogRejected(r, lights); } }
                catch (Exception e) { rejected++; _log.LogWarning($"Light shafts: '{r.name}': {e.Message}"); }
            }
            _meshCache.Clear();
            Version++;
            _log.LogInfo($"Light shafts: {_cones.Count} lights with a fake cone ({candidates} shaft meshes, {rejected} without a light at their apex) in {sw.Elapsed.TotalMilliseconds:F1} ms");
            int n = 0;
            foreach (var c in _cones.Values)
            {
                if (n++ >= 12) break;
                _log.LogInfo($"Light shafts: '{c.Light.name}' at {c.Apex:F2} axis {c.Axis:F2} half angle {Mathf.Acos(c.CosHalf) * Mathf.Rad2Deg:F0} deg length {c.Length:F1} m, " +
                             $"{(c.Parented ? $"parent spot (angle {c.Light.spotAngle:F0}, beam from {c.Start:F1} m, colour {c.BeamColor} alpha {c.BeamAlpha:F2}, {c.Hulls.Count} hulls) " : "")}range {c.Light.range:F0} intensity {c.Light.intensity:F2} colour {c.Light.color}, " +
                             $"fakes {string.Join(", ", c.Fakes.ConvertAll(f => f.name))}");
            }
        }

        private static bool IsShaft(MeshRenderer r)
        {
            var m = r.sharedMaterial;
            if (m == null || m.shader == null || m.shader.name != "ULTRAKILL/Master" || !m.IsKeywordEnabled("TRANSPARENCY")) return false;
            if (!m.HasProperty("_DstBlend") || Mathf.RoundToInt(m.GetFloat("_DstBlend")) != (int)BlendMode.One) return false; // additive
            string n = (m.name + " " + r.name).ToLowerInvariant();
            if (n.Contains("lava")) return false;
            foreach (var s in ShaftNames) if (n.Contains(s)) return true;
            return false;
        }

        /// <summary>Diagnostics: why a shaft mesh found no light (its parent light, the lights near it, the fit).</summary>
        private void LogRejected(MeshRenderer r, IList<Light> lights)
        {
            var mf = r.GetComponent<MeshFilter>();
            var parent = r.GetComponentInParent<Light>(true);
            var sb = new System.Text.StringBuilder();
            sb.Append($"Light shafts: rejected '{Bench.GameControl.PathOf(r.transform)}' mesh '{mf?.sharedMesh?.name}' verts {mf?.sharedMesh?.vertexCount} " +
                      $"static {r.isPartOfStaticBatch} bounds {r.bounds.center:F1} size {r.bounds.size:F1} scale {r.transform.lossyScale:F1} rot {r.transform.rotation.eulerAngles:F0}");
            if (parent != null) sb.Append($"; parent light '{parent.name}' {parent.type} at {parent.transform.position:F1} fwd {parent.transform.forward:F2} angle {parent.spotAngle:F0} range {parent.range:F0} int {parent.intensity:F2} on {parent.isActiveAndEnabled} in list {lights.Contains(parent)}");
            var verts = WorldVertices(r);
            foreach (var l in new[] { parent })
            {
                if (l == null || verts == null) continue;
                bool ok = Fit(l.transform.position, verts, out var axis, out var cosHalf, out var len, out var apexDist);
                float maxD = 0f;
                foreach (var v in verts) maxD = Mathf.Max(maxD, (v - l.transform.position).magnitude);
                sb.Append($"; fit {ok} axis {axis:F2} half {Mathf.Acos(Mathf.Clamp(cosHalf, -1f, 1f)) * Mathf.Rad2Deg:F0} len {len:F1} apexDist {apexDist:F2} maxD {maxD:F1}");
            }
            _log.LogInfo(sb.ToString());
        }

        /// <summary>Find the light at this mesh's apex and fit the cone. False if none fits.</summary>
        private bool Match(MeshRenderer r, IList<Light> lights)
        {
            if (Settings.parentSpots && MatchParentSpot(r)) return true;
            var b = r.bounds;
            var near = b;
            near.Expand(2f);
            Light best = null;
            float bestApex = float.MaxValue;
            Vector3 bestAxis = default;
            float bestCos = 0f, bestLen = 0f;
            List<Vector3> verts = null;
            foreach (var l in lights)
            {
                if (l == null || (l.type != LightType.Point && l.type != LightType.Spot)) continue;
                var p = l.transform.position;
                if (!near.Contains(p)) continue;
                if (verts == null && (verts = WorldVertices(r)) == null) return false;
                // A lamp's cone: no longer than the light reaches (2 x range, plus margin) and not wider than 60 degrees.
                // 4-3's light pillars (190 m tall boxes around a small light) fit a cone from their base otherwise.
                if (Fit(p, verts, out var axis, out var cosHalf, out var len, out var apexDist) && apexDist < bestApex &&
                    len <= 3f * Mathf.Max(l.range, 1f) && cosHalf >= Mathf.Cos(60f * Mathf.Deg2Rad))
                {
                    best = l; bestApex = apexDist; bestAxis = axis; bestCos = cosHalf; bestLen = len;
                }
            }
            if (best == null) return false;
            if (!_cones.TryGetValue(best, out var c))
            {
                float a = Mathf.Acos(bestCos);
                float inner = Mathf.Cos(a * Mathf.Clamp(Settings.edge, 0.05f, 0.99f));
                c = new Cone { Light = best, Apex = best.transform.position, Axis = bestAxis, CosHalf = bestCos, Sharp = 1f / Mathf.Max(inner - bestCos, 1e-4f), Length = bestLen };
                _cones[best] = c;
            }
            c.Fakes.Add(r);
            return true;
        }

        /// <summary>
        /// A beam mesh under a spot light: the cone from the light over the far half of the mesh (axis = mean direction,
        /// within 20 degrees of the spot's forward; half angle = the widest vertex, at most 15 degrees past the spot's own).
        /// Nested beams of one light (3-1's eyes draw four frusta of growing width) widen one cone to the outermost.
        /// </summary>
        private bool MatchParentSpot(MeshRenderer r)
        {
            var l = r.GetComponentInParent<Light>(true);
            if (l == null || l.type != LightType.Spot) return false;
            var verts = WorldVertices(r);
            if (verts == null) return false;
            var p = l.transform.position;
            var fwd = l.transform.forward;
            float maxAlong = 0f;
            foreach (var v in verts) maxAlong = Mathf.Max(maxAlong, Vector3.Dot(v - p, fwd));
            if (maxAlong < 2f) return false;
            Vector3 sum = Vector3.zero;
            foreach (var v in verts)
            {
                var d = v - p;
                if (Vector3.Dot(d, fwd) > 0.5f * maxAlong) sum += d.normalized;
            }
            if (sum.sqrMagnitude < 1e-6f) return false;
            var axis = sum.normalized;
            if (Vector3.Dot(axis, fwd) < Mathf.Cos(20f * Mathf.Deg2Rad)) return false;
            float cosHalf = 1f, len = 0f, start = float.MaxValue;
            foreach (var v in verts)
            {
                var d = v - p;
                float along = Vector3.Dot(d, axis);
                len = Mathf.Max(len, along);
                start = Mathf.Min(start, along);
                if (along <= 0.5f * maxAlong) continue;
                cosHalf = Mathf.Min(cosHalf, along / Mathf.Max(d.magnitude, 1e-4f));
            }
            float half = Mathf.Acos(Mathf.Clamp(cosHalf, -1f, 1f)) * Mathf.Rad2Deg;
            if (half < 1f || half > 75f || half > 0.5f * l.spotAngle + 15f) return false;
            if (_cones.TryGetValue(l, out var c))
            {
                // Widen to the outermost nested beam; keep the axis of the first.
                c.CosHalf = Mathf.Min(c.CosHalf, cosHalf);
                c.Length = Mathf.Max(c.Length, len);
            }
            else
            {
                c = new Cone { Light = l, Apex = p, Axis = axis, CosHalf = cosHalf, Length = len, Parented = true,
                               LocalAxis = l.transform.InverseTransformDirection(axis) };
                _cones[l] = c;
            }
            float a = Mathf.Acos(c.CosHalf);
            float inner = Mathf.Cos(a * Mathf.Clamp(Settings.edge, 0.05f, 0.99f));
            c.Sharp = 1f / Mathf.Max(inner - c.CosHalf, 1e-4f);
            c.Start = Mathf.Min(c.Start, Mathf.Max(start, 0.35f));
            var m = r.sharedMaterial;
            if (m != null && m.HasProperty("_Color"))
            {
                // The brightest of the nested meshes' colours; alpha per layer (they are drawn two-sided, so a ray through
                // the beam crosses two layers of each).
                var col = m.GetColor("_Color");
                if (col.maxColorComponent >= c.BeamColor.maxColorComponent) c.BeamColor = new Color(col.r, col.g, col.b, 1f);
                c.BeamAlpha = Mathf.Max(c.BeamAlpha, Mathf.Clamp01(col.a));
            }
            c.Fakes.Add(r);
            try
            {
                var hull = FitHull(r, l, verts, axis);
                if (hull != null) c.Hulls.Add(hull);
            }
            catch (Exception e) { _log.LogWarning($"Light shafts: beam hull of '{r.name}': {e.Message}"); }
            return true;
        }

        /// <summary>
        /// The beam channel's description of a beam mesh (<see cref="BeamHull"/>): the convex hull of its vertices, and the
        /// material's colour, culling, fog and distance fade. Null (the game keeps drawing the mesh) when the mesh is not
        /// convex (a vertex inside its hull), has more than 8 hull planes, or more than 64 distinct vertices.
        /// </summary>
        private BeamHull FitHull(MeshRenderer r, Light l, List<Vector3> world, Vector3 axisWorld)
        {
            bool local = !r.isPartOfStaticBatch;
            var toSpace = local ? l.transform.worldToLocalMatrix : Matrix4x4.identity;
            var pts = new List<Vector3>();
            Vector3 lo = Vector3.positiveInfinity, hi = Vector3.negativeInfinity;
            var all = new List<Vector3>(world.Count);
            foreach (var w in world)
            {
                var p = toSpace.MultiplyPoint3x4(w);
                all.Add(p);
                lo = Vector3.Min(lo, p); hi = Vector3.Max(hi, p);
            }
            float extent = (hi - lo).magnitude;
            if (!(extent > 1e-4f)) return null;
            float tol = 2e-3f * extent;
            foreach (var p in all)
            {
                bool dup = false;
                foreach (var q in pts) if ((q - p).sqrMagnitude < 0.25f * tol * tol) { dup = true; break; }
                if (!dup) pts.Add(p);
                if (pts.Count > 64) { _log.LogInfo($"Light shafts: beam '{r.name}' has over 64 distinct vertices: the game draws it"); return null; }
            }
            var planes = new List<Vector4>();
            int n = pts.Count;
            for (int i = 0; i < n; i++)
            for (int j = i + 1; j < n; j++)
            for (int k = j + 1; k < n; k++)
            {
                var e1 = pts[j] - pts[i];
                var e2 = pts[k] - pts[i];
                var nrm = Vector3.Cross(e1, e2);
                float len = nrm.magnitude;
                if (len < 1e-3f * e1.magnitude * e2.magnitude) continue; // (nearly) collinear
                nrm /= len;
                float d = Vector3.Dot(nrm, pts[i]);
                bool allIn = true, allOut = true;
                for (int m = 0; m < n && (allIn || allOut); m++)
                {
                    float s = Vector3.Dot(nrm, pts[m]) - d;
                    if (s > tol) allIn = false;
                    if (s < -tol) allOut = false;
                }
                if (!allIn && !allOut) continue;
                if (!allIn) { nrm = -nrm; d = -d; }
                bool seen = false;
                foreach (var p in planes)
                    if (Vector3.Dot(new Vector3(p.x, p.y, p.z), nrm) > 0.9999f && Mathf.Abs(p.w - d) < tol) { seen = true; break; }
                if (!seen) planes.Add(new Vector4(nrm.x, nrm.y, nrm.z, d));
            }
            if (planes.Count < 4 || planes.Count > 8)
            {
                _log.LogInfo($"Light shafts: beam '{r.name}' hull has {planes.Count} planes: the game draws it");
                return null;
            }
            // Convex: every vertex lies on the hull (a vertex inside it would be a dent the hull fills in).
            foreach (var p in pts)
            {
                bool onHull = false;
                foreach (var pl in planes) if (Mathf.Abs(Vector3.Dot(new Vector3(pl.x, pl.y, pl.z), p) - pl.w) <= tol) { onHull = true; break; }
                if (!onHull) { _log.LogInfo($"Light shafts: beam '{r.name}' is not convex: the game draws it"); return null; }
            }
            Vector3 lo2 = Vector3.positiveInfinity, hi2 = Vector3.negativeInfinity;
            foreach (var p in pts) { lo2 = Vector3.Min(lo2, p); hi2 = Vector3.Max(hi2, p); }
            var center = 0.5f * (lo2 + hi2);
            float radius = 0f;
            foreach (var p in pts) radius = Mathf.Max(radius, (p - center).magnitude);
            float start = float.MaxValue;
            var lp = l.transform.position;
            foreach (var w in world) start = Mathf.Min(start, Vector3.Dot(w - lp, axisWorld));

            var h = new BeamHull { Renderer = r, Light = l, Local = local, Planes = planes.ToArray(), Center = center, Radius = radius,
                                   Start = Mathf.Max(start, 0.35f) };
            var m0 = r.sharedMaterial;
            Color col = m0 != null && m0.HasProperty("_Color") ? m0.GetColor("_Color") : Color.white;
            // SrcBlend SrcAlpha, DstBlend One (IsShaft checked the latter): dst += rgb x a x vertex colour (_VertexColors 1).
            // The vertex colours are gradients (3-1: white at the eye end, black at the far end), interpolated across each
            // face; ProBuilder gives every face its own vertices, so each hull face gets its own linear fit of the vertex
            // brightness over the mesh triangles lying on it (exact for a colour interpolated linearly over the face). A hull
            // face with no triangles on it is one the mesh does not have (an open end): brightness 0. Their mean hue goes
            // into the colour.
            float a = Mathf.Clamp01(col.a);
            if (m0 != null && m0.HasProperty("_SrcBlend") && Mathf.RoundToInt(m0.GetFloat("_SrcBlend")) == (int)BlendMode.One) a = 1f;
            var vcs = VertexColors(r);
            Color vc = Color.white;
            float[] sv = null;
            if (vcs != null && vcs.Count == all.Count)
            {
                sv = new float[all.Count];
                Color sum = Color.clear;
                for (int i = 0; i < all.Count; i++) { sum += vcs[i]; sv[i] = (vcs[i].r + vcs[i].g + vcs[i].b) / 3f * vcs[i].a; }
                vc = sum / all.Count;
                float mean = (vc.r + vc.g + vc.b) / 3f;
                if (mean > 1e-4f) { vc.r /= mean; vc.g /= mean; vc.b /= mean; } else vc = Color.white;
            }
            var tris = Triangles(r, all.Count);
            h.FaceGrad = new Vector4[planes.Count];
            var faceInfo = new System.Text.StringBuilder();
            for (int f = 0; f < planes.Count; f++)
            {
                var pl = planes[f];
                var nrm = new Vector3(pl.x, pl.y, pl.z);
                if (tris == null)
                {
                    h.FaceGrad[f] = sv != null ? FitLinear(all, sv) : new Vector4(0, 0, 0, 1);
                    continue;
                }
                var on = new List<int>();
                int nt = 0;
                for (int t = 0; t + 2 < tris.Count; t += 3)
                {
                    bool inPlane = true;
                    for (int q = 0; q < 3; q++)
                        if (Mathf.Abs(Vector3.Dot(nrm, all[tris[t + q]]) - pl.w) > tol) { inPlane = false; break; }
                    if (!inPlane) continue;
                    nt++;
                    for (int q = 0; q < 3; q++) if (!on.Contains(tris[t + q])) on.Add(tris[t + q]);
                }
                if (nt == 0) { h.FaceGrad[f] = Vector4.zero; faceInfo.Append(" -"); continue; }
                if (sv == null) { h.FaceGrad[f] = new Vector4(0, 0, 0, 1); faceInfo.Append($" {nt}t"); continue; }
                var fp = new List<Vector3>(on.Count);
                var fv = new float[on.Count];
                float vmin = float.MaxValue, vmax = float.MinValue;
                for (int q = 0; q < on.Count; q++) { fp.Add(all[on[q]]); fv[q] = sv[on[q]]; vmin = Mathf.Min(vmin, fv[q]); vmax = Mathf.Max(vmax, fv[q]); }
                h.FaceGrad[f] = FitLinear(fp, fv);
                faceInfo.Append($" {nt}t {vmin:F2}-{vmax:F2}");
            }
            h.Add = new Color(col.r * vc.r * a, col.g * vc.g * a, col.b * vc.b * a, 1f);
            int cull = m0 != null && m0.HasProperty("_Cull") ? Mathf.RoundToInt(m0.GetFloat("_Cull")) : 0;
            h.Entry = cull != (int)CullMode.Front;
            h.Exit = cull != (int)CullMode.Back;
            h.Fog = m0 != null && !m0.IsKeywordEnabled("_FOG_OFF");
            if (m0 != null && m0.IsKeywordEnabled("DISTANCE_FADE") && m0.HasProperty("_CameraFadingEnabled") && m0.GetFloat("_CameraFadingEnabled") > 0.5f)
            {
                h.FadeNear = m0.HasProperty("_CameraNearFadeDistance") ? m0.GetFloat("_CameraNearFadeDistance") : 0f;
                h.FadeFar = m0.HasProperty("_CameraFarFadeDistance") ? m0.GetFloat("_CameraFarFadeDistance") : 0f;
            }
            _log.LogInfo($"Light shafts: beam hull '{Bench.GameControl.PathOf(r.transform)}' material '{m0?.name}': {planes.Count} planes, {pts.Count} vertices, " +
                         $"radius {radius:F1}, from {h.Start:F1} m, adds ({h.Add.r:F3}, {h.Add.g:F3}, {h.Add.b:F3}) per face x vertex brightness (faces:{faceInfo}) (colour {col}, vertex hue {vc}), " +
                         $"cull {cull}, fog {h.Fog}, fade {h.FadeNear:F1}-{h.FadeFar:F1} m, {(local ? "follows its light" : "static")}");
            return h;
        }

        /// <summary>Least squares s = w + dot(xyz, p) over the points (a constant where they span no gradient).</summary>
        private static Vector4 FitLinear(List<Vector3> pts, float[] v)
        {
            int n = pts.Count;
            Vector3 c = Vector3.zero;
            float mv = 0f;
            for (int i = 0; i < n; i++) { c += pts[i]; mv += v[i]; }
            c /= n; mv /= n;
            // Normal equations of the centred problem: (sum d d^T) g = sum d (v - mean)
            var m = new double[3, 4];
            double tr = 0;
            for (int i = 0; i < n; i++)
            {
                var d = pts[i] - c;
                double[] dv = { d.x, d.y, d.z };
                for (int r = 0; r < 3; r++)
                {
                    for (int k = 0; k < 3; k++) m[r, k] += dv[r] * dv[k];
                    m[r, 3] += dv[r] * (v[i] - mv);
                }
                tr += d.sqrMagnitude;
            }
            for (int r = 0; r < 3; r++) m[r, r] += 1e-6 * tr + 1e-12;
            for (int col = 0; col < 3; col++)
            {
                int piv = col;
                for (int r = col + 1; r < 3; r++) if (Math.Abs(m[r, col]) > Math.Abs(m[piv, col])) piv = r;
                for (int k = 0; k < 4; k++) { var t = m[col, k]; m[col, k] = m[piv, k]; m[piv, k] = t; }
                for (int r = 0; r < 3; r++)
                {
                    if (r == col) continue;
                    double f = m[r, col] / m[col, col];
                    for (int k = col; k < 4; k++) m[r, k] -= f * m[col, k];
                }
            }
            var g = new Vector3((float)(m[0, 3] / m[0, 0]), (float)(m[1, 3] / m[1, 1]), (float)(m[2, 3] / m[2, 2]));
            if (float.IsNaN(g.x) || float.IsNaN(g.y) || float.IsNaN(g.z)) g = Vector3.zero;
            return new Vector4(g.x, g.y, g.z, mv - Vector3.Dot(g, c));
        }

        /// <summary>The renderer's triangles as indices into its vertex range (WorldVertices' order), or null if they can't
        /// be read (GPU readback: the meshes are not readable).</summary>
        private static List<int> Triangles(MeshRenderer r, int rangeCount)
        {
            var mf = r.GetComponent<MeshFilter>();
            var mesh = mf != null ? mf.sharedMesh : null;
            if (mesh == null) return null;
            int first = 0, subFrom = 0, subTo = mesh.subMeshCount;
            if (r.isPartOfStaticBatch)
            {
                subFrom = r.subMeshStartIndex;
                subTo = subFrom + Math.Max(1, r.sharedMaterials.Length);
                if (subFrom < 0 || subTo > mesh.subMeshCount) return null;
                first = mesh.GetSubMesh(subFrom).firstVertex;
            }
            int[] idx;
            try
            {
                if (mesh.isReadable)
                {
                    var l = new List<int>();
                    for (int sm = subFrom; sm < subTo; sm++)
                    {
                        if (mesh.GetTopology(sm) != MeshTopology.Triangles) continue;
                        foreach (var i in mesh.GetIndices(sm, true)) l.Add(i);
                    }
                    idx = l.ToArray();
                }
                else
                {
                    using (var ib = mesh.GetIndexBuffer())
                    {
                        int n = ib.count;
                        var all = new int[n];
                        if (mesh.indexFormat == IndexFormat.UInt16)
                        {
                            var u = new ushort[n];
                            ib.GetData(u, 0, 0, n);
                            for (int i = 0; i < n; i++) all[i] = u[i];
                        }
                        else
                        {
                            var u = new uint[n];
                            ib.GetData(u, 0, 0, n);
                            for (int i = 0; i < n; i++) all[i] = (int)u[i];
                        }
                        var l = new List<int>();
                        for (int sm = subFrom; sm < subTo; sm++)
                        {
                            var d = mesh.GetSubMesh(sm);
                            if (d.topology != MeshTopology.Triangles) continue;
                            for (int i = d.indexStart; i < d.indexStart + d.indexCount && i < n; i++) l.Add(all[i] + d.baseVertex);
                        }
                        idx = l.ToArray();
                    }
                }
            }
            catch (Exception) { return null; }
            var res = new List<int>(idx.Length);
            foreach (var i in idx)
            {
                int k = i - first;
                if (k < 0 || k >= rangeCount) return null;
                res.Add(k);
            }
            return res.Count >= 3 ? res : null;
        }

        /// <summary>The vertex colours of the renderer's vertices (its range of a static batch), in WorldVertices' order;
        /// null if the mesh has none or they can't be read.</summary>
        private static List<Color> VertexColors(MeshRenderer r)
        {
            var mf = r.GetComponent<MeshFilter>();
            var mesh = mf != null ? mf.sharedMesh : null;
            if (mesh == null || !mesh.HasVertexAttribute(VertexAttribute.Color)) return null;
            int first = 0, count = mesh.vertexCount;
            if (r.isPartOfStaticBatch)
            {
                int sub = r.subMeshStartIndex;
                if (sub < 0 || sub >= mesh.subMeshCount) return null;
                var d = mesh.GetSubMesh(sub);
                first = d.firstVertex;
                count = d.vertexCount;
            }
            Color[] cols = null;
            if (mesh.isReadable) cols = mesh.colors;
            else
            {
                var fmt = mesh.GetVertexAttributeFormat(VertexAttribute.Color);
                int dim = mesh.GetVertexAttributeDimension(VertexAttribute.Color);
                if (dim != 4 || (fmt != VertexAttributeFormat.UNorm8 && fmt != VertexAttributeFormat.Float32)) return null;
                int s = mesh.GetVertexAttributeStream(VertexAttribute.Color);
                int off = mesh.GetVertexAttributeOffset(VertexAttribute.Color);
                int stride = mesh.GetVertexBufferStride(s);
                int n = mesh.vertexCount;
                using (var vb = mesh.GetVertexBuffer(s))
                {
                    var words = new uint[(n * stride + 3) / 4];
                    vb.GetData(words, 0, 0, words.Length);
                    var bytes = new byte[n * stride];
                    Buffer.BlockCopy(words, 0, bytes, 0, bytes.Length);
                    cols = new Color[n];
                    for (int i = 0; i < n; i++)
                    {
                        int o = i * stride + off;
                        cols[i] = fmt == VertexAttributeFormat.UNorm8
                            ? new Color(bytes[o] / 255f, bytes[o + 1] / 255f, bytes[o + 2] / 255f, bytes[o + 3] / 255f)
                            : new Color(BitConverter.ToSingle(bytes, o), BitConverter.ToSingle(bytes, o + 4), BitConverter.ToSingle(bytes, o + 8), BitConverter.ToSingle(bytes, o + 12));
                    }
                }
            }
            if (cols == null || cols.Length == 0) return null;
            var list = new List<Color>(count);
            for (int i = first; i < first + count && i < cols.Length; i++) list.Add(cols[i]);
            return list;
        }

        /// <summary>
        /// Cone from apex p over the mesh's vertices: the vertices within 1 m of p (or 10% of the length) make it the apex;
        /// the axis is the mean direction to the far half of the vertices, the half angle the widest of them.
        /// </summary>
        private static bool Fit(Vector3 p, List<Vector3> verts, out Vector3 axis, out float cosHalf, out float length, out float apexDist)
        {
            axis = Vector3.down; cosHalf = 0f; length = 0f; apexDist = float.MaxValue;
            float maxD = 0f;
            foreach (var v in verts)
            {
                float d = (v - p).magnitude;
                if (d < apexDist) apexDist = d;
                if (d > maxD) maxD = d;
            }
            if (maxD < 2f || apexDist > Mathf.Max(1f, 0.1f * maxD)) return false;
            Vector3 sum = Vector3.zero;
            foreach (var v in verts)
            {
                var d = v - p;
                float m = d.magnitude;
                if (m > 0.5f * maxD) sum += d / m;
            }
            if (sum.sqrMagnitude < 1e-6f) return false;
            axis = sum.normalized;
            cosHalf = 1f;
            foreach (var v in verts)
            {
                var d = v - p;
                float m = d.magnitude;
                if (m <= 0.5f * maxD) continue;
                cosHalf = Mathf.Min(cosHalf, Vector3.Dot(d / m, axis));
                length = Mathf.Max(length, Vector3.Dot(d, axis));
            }
            // A cone, not a slab or a sphere around the light.
            return cosHalf > Mathf.Cos(75f * Mathf.Deg2Rad) && cosHalf < Mathf.Cos(3f * Mathf.Deg2Rad);
        }

        /// <summary>The renderer's vertices in world space (its submesh of a static batch, or its own mesh).</summary>
        private List<Vector3> WorldVertices(MeshRenderer r)
        {
            var mf = r.GetComponent<MeshFilter>();
            var mesh = mf != null ? mf.sharedMesh : null;
            if (mesh == null) return null;
            var pos = Positions(mesh);
            if (pos == null) return null;
            _tmp.Clear();
            int first = 0, count = pos.Length;
            Matrix4x4 toWorld;
            if (r.isPartOfStaticBatch)
            {
                int sub = r.subMeshStartIndex;
                if (sub < 0 || sub >= mesh.subMeshCount) return null;
                var d = mesh.GetSubMesh(sub);
                first = d.firstVertex;
                count = d.vertexCount;
                var root = StaticBatchRoot?.GetValue(r) as Transform;
                toWorld = root != null ? root.localToWorldMatrix : Matrix4x4.identity;
            }
            else toWorld = r.transform.localToWorldMatrix;
            for (int i = first; i < first + count && i < pos.Length; i++) _tmp.Add(toWorld.MultiplyPoint3x4(pos[i]));
            return _tmp.Count > 0 ? new List<Vector3>(_tmp) : null;
        }

        private Vector3[] Positions(Mesh mesh)
        {
            if (_meshCache.TryGetValue(mesh, out var cached)) return cached;
            Vector3[] pos = null;
            if (mesh.isReadable) pos = mesh.vertices;
            else if (mesh.HasVertexAttribute(VertexAttribute.Position) && mesh.GetVertexAttributeFormat(VertexAttribute.Position) == VertexAttributeFormat.Float32)
            {
                int s = mesh.GetVertexAttributeStream(VertexAttribute.Position);
                int off = mesh.GetVertexAttributeOffset(VertexAttribute.Position);
                int stride = mesh.GetVertexBufferStride(s);
                int n = mesh.vertexCount;
                using (var vb = mesh.GetVertexBuffer(s))
                {
                    var words = new uint[(n * stride + 3) / 4];
                    vb.GetData(words, 0, 0, words.Length);
                    var bytes = new byte[n * stride];
                    Buffer.BlockCopy(words, 0, bytes, 0, bytes.Length);
                    pos = new Vector3[n];
                    for (int i = 0; i < n; i++)
                    {
                        int o = i * stride + off;
                        pos[i] = new Vector3(BitConverter.ToSingle(bytes, o), BitConverter.ToSingle(bytes, o + 4), BitConverter.ToSingle(bytes, o + 8));
                    }
                }
            }
            _meshCache[mesh] = pos;
            return pos;
        }

        /// <summary>
        /// Every frame: hide the fake cones while the volumetric pass draws the real ones (<paramref name="active"/>), show
        /// them otherwise. A cone whose light has moved away from where it was fitted keeps its fake.
        /// </summary>
        public void UpdateFakes(bool active)
        {
            bool hide = active && Settings.enabled && !Settings.keepFakes;
            UpdateBeamFakes(hide && Settings.beams);
            if (hide == _hide) return;
            if (_hide) ShowLampFakes();
            _hide = hide;
            if (!hide) return;
            foreach (var c in _cones.Values)
            {
                if (!c.InPlace || c.Parented) continue; // beam meshes: UpdateBeamFakes
                foreach (var f in c.Fakes)
                    if (f != null && f.enabled) { f.enabled = false; _hidden.Add(f); }
            }
        }

        /// <summary>Beam meshes the beam channel drew in the last frames are hidden (the channel draws them); every other one
        /// is shown again (out of the hull budget, its light off, the channel off).</summary>
        private void UpdateBeamFakes(bool on)
        {
            int frame = Time.frameCount;
            _beamKeep.Clear();
            if (on)
                foreach (var c in _cones.Values)
                {
                    if (!c.Parented) continue;
                    foreach (var h in c.Hulls)
                        if (h.Renderer != null && frame - h.LastDrawn <= 2) _beamKeep.Add(h.Renderer);
                }
            _tmpShow.Clear();
            foreach (var r in _hiddenBeams) if (!_beamKeep.Contains(r)) _tmpShow.Add(r);
            foreach (var r in _tmpShow)
            {
                if (r != null) r.enabled = true;
                _hiddenBeams.Remove(r);
            }
            foreach (var r in _beamKeep)
                if (!_hiddenBeams.Contains(r) && r.enabled) { r.enabled = false; _hiddenBeams.Add(r); }
        }

        /// <summary>Whether the beam channel hid this renderer (the game would draw it otherwise).</summary>
        public bool HiddenByBeams(Renderer r) => _hiddenBeams.Contains(r);

        private void ShowLampFakes()
        {
            foreach (var f in _hidden) if (f != null) f.enabled = true;
            _hidden.Clear();
            _hide = false;
        }

        private void ShowFakes()
        {
            ShowLampFakes();
            foreach (var r in _hiddenBeams) if (r != null) r.enabled = true;
            _hiddenBeams.Clear();
        }
    }
}
