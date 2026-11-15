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
        /// <summary>Beams matched through their parent spot are drawn by the volumetric pass as the game draws them (a
        /// constant-density glow of the meshes' colour inside the cone, not the spot's light falling off with distance)
        /// with traced visibility to the light, so whatever stands in the beam shadows it (3-1's eye beams through the
        /// ribs). Brightness relative to the game's layered meshes; 0 = off: the game's meshes stay (default, see
        /// docs/volumetrics.md "Eye beams": an additive haze under the composite's soft cap can't reach their orange).</summary>
        public float beamStrength = 0f;
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
        private bool _hideBeams;

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
                             $"{(c.Parented ? $"parent spot (angle {c.Light.spotAngle:F0}, beam from {c.Start:F1} m, colour {c.BeamColor} alpha {c.BeamAlpha:F2}) " : "")}range {c.Light.range:F0} intensity {c.Light.intensity:F2} colour {c.Light.color}, " +
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
            return true;
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
            bool beams = Settings.beamStrength > 0f;
            if (hide == _hide && beams == _hideBeams) return;
            if (_hide) ShowFakes();
            _hide = hide;
            _hideBeams = beams;
            if (!hide) return;
            foreach (var c in _cones.Values)
            {
                if (!c.InPlace) continue;
                if (c.Parented && Settings.beamStrength <= 0f) continue; // the game's beam meshes stay (not drawn by the volumetric pass)
                foreach (var f in c.Fakes)
                    if (f != null && f.enabled) { f.enabled = false; _hidden.Add(f); }
            }
        }

        private void ShowFakes()
        {
            foreach (var f in _hidden) if (f != null) f.enabled = true;
            _hidden.Clear();
            _hide = false;
        }
    }
}
