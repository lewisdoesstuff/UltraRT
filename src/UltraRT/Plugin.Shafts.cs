using System;
using System.Globalization;
using UnityEngine;

namespace UltraRT
{
    /// <summary>
    /// Lamp cones (LightShafts.cs, docs/volumetrics.md "Lamp cones"): wiring. The volumetric pass gets one float4 pair per
    /// light in <c>_VolLightCone</c>: [0] cone axis + cos(half angle) (w &lt; -0.5: the light's own shape), [1] x weight of the
    /// light's scattering (multiplies the haze range weight), y cone edge sharpness.
    ///   -ultrart-shafts on|off, -ultrart-shaft-strength F, -ultrart-shaft-keepfakes, -ultrart-beams on|off (the beam channel,
    ///   docs/volumetrics.md "Eye beams"); bench variant shaft.&lt;field&gt;.
    /// </summary>
    public partial class Plugin
    {
        private LightShafts _lightShafts;
        private readonly LightShaftSettings _shaftSettings = new LightShaftSettings();
        private ComputeBuffer _volConeBuffer;
        private readonly Vector4[] _volCone = new Vector4[MaxLights * 2];
        private bool _shaftArgsParsed;
        private int _shaftsFrame = -100;
        private ComputeBuffer _volBeamBuffer;
        private int _housingVersion = -1;
        private object _housingScene;
        private const int MaxBeamHulls = 32, BeamFloat4 = 21; // keep in step with Volumetrics.raytrace / VolumetricsComposite.shader
        private readonly Vector4[] _volBeams = new Vector4[MaxBeamHulls * BeamFloat4];
        private readonly System.Collections.Generic.List<(float dist, LightShafts.BeamHull h)> _beamPick =
            new System.Collections.Generic.List<(float, LightShafts.BeamHull)>();
        public int VolBeamCount { get; private set; }
        private readonly System.Collections.Generic.List<LightShafts.BeamHull> _beamDrawn = new System.Collections.Generic.List<LightShafts.BeamHull>();
        private readonly System.Collections.Generic.Dictionary<Shader, int> _beamPass = new System.Collections.Generic.Dictionary<Shader, int>();
        private MaterialPropertyBlock _beamMpb;
        private readonly UnityEngine.Rendering.RenderTargetIdentifier[] _beamCamTargets = new UnityEngine.Rendering.RenderTargetIdentifier[3];

        private void ParseShaftArgs()
        {
            _shaftArgsParsed = true;
            var a = Environment.GetCommandLineArgs();
            for (int i = 0; i < a.Length; i++)
            {
                string Next() => i + 1 < a.Length ? a[++i] : null;
                switch (a[i].ToLowerInvariant())
                {
                    case "-ultrart-shafts": _shaftSettings.enabled = !string.Equals(Next(), "off", StringComparison.OrdinalIgnoreCase); break;
                    case "-ultrart-shaft-strength":
                        if (float.TryParse(Next(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) _shaftSettings.strength = v;
                        break;
                    case "-ultrart-shaft-keepfakes": _shaftSettings.keepFakes = true; break;
                    case "-ultrart-beams": _shaftSettings.beams = !string.Equals(Next(), "off", StringComparison.OrdinalIgnoreCase); break;
                    case "-ultrart-window-beams": _shaftSettings.windowBeams = string.Equals(Next(), "on", StringComparison.OrdinalIgnoreCase); break;
                }
            }
            Logger.LogInfo($"Lamp cones: {(_shaftSettings.enabled ? "on" : "off")}, strength {_shaftSettings.strength}{(_shaftSettings.keepFakes ? ", fakes kept" : "")}, beam channel {(_shaftSettings.beams ? "on" : "off")}");
        }

        /// <summary>Every frame (OnUpdate): the game's fake cones are hidden only while the volumetric pass draws ours.</summary>
        private void UpdateShaftFakes()
        {
            if (_lightShafts == null) return;
            bool active = Time.frameCount - _shaftsFrame <= 2 && _viewEnabled;
            try { _lightShafts.UpdateFakes(active); }
            catch (Exception e) { Logger.LogWarning($"Lamp cones: {e.Message}"); }
        }

        /// <summary>
        /// The beam channel's hulls this frame (LightShafts.BeamHull; Volumetrics.raytrace BeamVisibility,
        /// VolumetricsComposite pass 2). BeamFloat4 float4 per hull, in world space:
        ///   [0] rgb added per face drawn (display units, x beamStrength), w flags: 1 entry face, 2 exit face, 4 fogged
        ///   [1] bounding sphere centre + radius
        ///   [2] light position + distance fade near (m), [3] beam axis + where the mesh begins (m along it)
        ///   [4] x distance fade far (m), [5..12] planes (xyz outward normal, w offset; unused: 0, 0, 0, -1),
        ///   [13..20] per plane, the vertex colour brightness on that face: w + dot(xyz, x) (0: no face there)
        /// Nearest first, at most maxBeamHulls, within 300 m.
        /// </summary>
        private ComputeBuffer VolumetricsBeamBuffer(out int count)
        {
            if (_volBeamBuffer == null) _volBeamBuffer = new ComputeBuffer(MaxBeamHulls * BeamFloat4, 16) { name = "UltraRT Volumetrics Beams" };
            count = 0;
            _beamPick.Clear();
            _beamDrawn.Clear();
            if (_shaftSettings.enabled && _shaftSettings.beams && _lightShafts != null && _camera != null)
            {
                var cam = _camera.transform.position;
                foreach (var c in _lightShafts.Cones)
                {
                    if (!c.Parented) continue;
                    foreach (var h in c.Hulls)
                    {
                        if (!h.Live(_lightShafts.HiddenByBeams(h.Renderer))) continue;
                        var centre = h.Local ? h.Space.TransformPoint(h.Center) : h.Center;
                        float radius = h.Local ? h.Radius * MaxScale(h.Space.lossyScale) : h.Radius;
                        float dist = Mathf.Max(0f, Vector3.Distance(cam, centre) - radius);
                        if (dist > 300f) continue;
                        _beamPick.Add((dist, h));
                    }
                }
                if (_shaftSettings.windowBeams)
                    foreach (var h in _lightShafts.FreeHulls)
                    {
                        if (!h.Live(_lightShafts.HiddenByBeams(h.Renderer))) continue;
                        var centre = h.Local ? h.Space.TransformPoint(h.Center) : h.Center;
                        float radius = h.Local ? h.Radius * MaxScale(h.Space.lossyScale) : h.Radius;
                        float dist = Mathf.Max(0f, Vector3.Distance(cam, centre) - radius);
                        if (dist > 300f) continue;
                        _beamPick.Add((dist, h));
                    }
                _beamPick.Sort((a, b) => a.dist.CompareTo(b.dist));
                int max = Mathf.Clamp(_shaftSettings.maxBeamHulls, 0, MaxBeamHulls);
                float k = Mathf.Max(0f, _shaftSettings.beamStrength);
                foreach (var (dist, h) in _beamPick)
                {
                    if (count >= max) break;
                    var lt = h.Local ? h.Space : (h.Light != null ? h.Light.transform : null);
                    var m = h.Local ? lt.localToWorldMatrix : Matrix4x4.identity;
                    // Planes: n_w = (M^-1)^T n_l (normalised), through the transformed point n_l d_l.
                    var inv = h.Local ? lt.worldToLocalMatrix : Matrix4x4.identity;
                    var centre = m.MultiplyPoint3x4(h.Center);
                    float radius = h.Local ? h.Radius * MaxScale(lt.lossyScale) : h.Radius;
                    Vector3 axis, lp;
                    float start = h.Start;
                    if (h.Free)
                    {
                        // A window beam: the shadow rays go to a point far behind the source end and stop at its plane.
                        axis = m.MultiplyVector(h.FreeAxis).normalized;
                        lp = m.MultiplyPoint3x4(h.Source);
                        start = Mathf.Max(Vector3.Dot(m.MultiplyPoint3x4(h.SourceEnd) - lp, axis), 0.35f);
                    }
                    else
                    {
                        axis = h.Light.transform.forward;
                        var cone = _lightShafts.TryGet(h.Light, out var c) ? c : null;
                        if (cone != null) axis = cone.WorldAxis;
                        lp = h.Light.transform.position;
                    }
                    int o = count * BeamFloat4;
                    int faces = _shaftSettings.beamFaces == 1 ? 1 : _shaftSettings.beamFaces == 2 ? 2 : (h.Entry ? 1 : 0) + (h.Exit ? 2 : 0);
                    float flags = faces + (h.Fog ? 4f : 0f);
                    _volBeams[o] = new Vector4(h.Add.r * k, h.Add.g * k, h.Add.b * k, flags);
                    _volBeams[o + 1] = new Vector4(centre.x, centre.y, centre.z, radius);
                    _volBeams[o + 2] = new Vector4(lp.x, lp.y, lp.z, h.FadeNear);
                    _volBeams[o + 3] = new Vector4(axis.x, axis.y, axis.z, start);
                    _volBeams[o + 4] = new Vector4(h.FadeFar, 0, 0, 0);
                    var tr = (Vector3)inv.GetColumn(3);
                    for (int p = 0; p < 8; p++)
                    {
                        if (p >= h.Planes.Length) { _volBeams[o + 5 + p] = new Vector4(0, 0, 0, -1f); _volBeams[o + 13 + p] = Vector4.zero; continue; }
                        var pl = h.Planes[p];
                        var nl = new Vector3(pl.x, pl.y, pl.z);
                        var nw = inv.transpose.MultiplyVector(nl).normalized;
                        var pw = m.MultiplyPoint3x4(nl * pl.w);
                        _volBeams[o + 5 + p] = new Vector4(nw.x, nw.y, nw.z, Vector3.Dot(nw, pw));
                        // the face's brightness: x_l = inv x_w, so w + g.x_l = (w + g.t) + (R^T g).x_w
                        var g = h.FaceGrad[p];
                        var gl = new Vector3(g.x, g.y, g.z);
                        var gw = inv.transpose.MultiplyVector(gl);
                        _volBeams[o + 13 + p] = new Vector4(gw.x, gw.y, gw.z, g.w + Vector3.Dot(gl, tr));
                    }
                    h.LastDrawn = Time.frameCount;
                    _beamDrawn.Add(h);
                    count++;
                }
            }
            VolBeamCount = count;
            _cmd.SetBufferData(_volBeamBuffer, _volBeams, 0, 0, Math.Max(1, count) * BeamFloat4);
            return _volBeamBuffer;
        }

        /// <summary>
        /// The beam channel's colour (LightShaftSettings.beamMeshes): this frame's beam meshes drawn as the game draws them
        /// (their mesh, material, property block and forward pass) into the bound target, for Volumetrics to multiply by the
        /// traced visibility. Their renderers are disabled (UpdateFakes), which DrawMesh doesn't look at.
        /// </summary>
        private void DrawBeamMeshes(UnityEngine.Rendering.CommandBuffer cmd)
        {
            if (_beamMpb == null) _beamMpb = new MaterialPropertyBlock();
            foreach (var h in _beamDrawn)
            {
                var r = h.Renderer as MeshRenderer;
                var mf = r != null ? r.GetComponent<MeshFilter>() : null;
                var mesh = mf != null ? mf.sharedMesh : null;
                if (mesh == null) continue;
                var mats = r.sharedMaterials;
                _beamMpb.Clear();
                if (r.HasPropertyBlock()) r.GetPropertyBlock(_beamMpb);
                int sub0 = 0;
                Matrix4x4 m;
                if (r.isPartOfStaticBatch)
                {
                    sub0 = r.subMeshStartIndex;
                    var root = StaticBatchRootOf(r);
                    m = root != null ? root.localToWorldMatrix : Matrix4x4.identity;
                }
                else m = r.localToWorldMatrix;
                for (int i = 0; i < mats.Length && sub0 + i < mesh.subMeshCount; i++)
                {
                    var mat = mats[i];
                    if (mat == null) continue;
                    cmd.DrawMesh(mesh, m, mat, sub0 + i, ForwardPass(mat.shader), _beamMpb);
                }
            }
        }

        private static readonly System.Reflection.PropertyInfo StaticBatchRootProp =
            typeof(Renderer).GetProperty("staticBatchRootTransform", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        private static Transform StaticBatchRootOf(Renderer r) => StaticBatchRootProp?.GetValue(r) as Transform;

        /// <summary>The shader's ForwardBase pass (the one the game's forward rendering draws a transparent with); 0 if none.</summary>
        private int ForwardPass(Shader sh)
        {
            if (sh == null) return 0;
            if (_beamPass.TryGetValue(sh, out int p)) return p;
            p = 0;
            var tag = new UnityEngine.Rendering.ShaderTagId("LightMode");
            for (int i = 0; i < sh.passCount; i++)
            {
                var v = sh.FindPassTagValue(i, tag).name;
                if (v == "ForwardBase" || v == "Always") { p = i; break; }
            }
            _beamPass[sh] = p;
            Logger.LogInfo($"Light shafts: beam meshes drawn with pass {p} of '{sh.name}' ({sh.passCount} passes, LightMode '{sh.FindPassTagValue(p, tag).name}')");
            return p;
        }

        private static float MaxScale(Vector3 s) => Mathf.Max(Mathf.Abs(s.x), Mathf.Max(Mathf.Abs(s.y), Mathf.Abs(s.z)));

        /// <summary>The volumetric pass's per-light cone and weight buffer for this frame (TraceVolumetrics).</summary>
        private ComputeBuffer VolumetricsConeBuffer()
        {
            if (!_shaftArgsParsed) ParseShaftArgs();
            if (_lightShafts == null) _lightShafts = new LightShafts(Logger) { Settings = _shaftSettings };
            if (_volConeBuffer == null) _volConeBuffer = new ComputeBuffer(_volCone.Length, 16) { name = "UltraRT Volumetrics Cones" };
            if (_shaftSettings.enabled && _proxies != null)
            {
                try
                {
                    _lightShafts.EnsureBuilt(_proxies, _proxies.Lights);
                    if (_lightShafts.Version != _housingVersion || !ReferenceEquals(_housingScene, _proxies))
                    {
                        _housingVersion = _lightShafts.Version;
                        _housingScene = _proxies;
                        if (_shaftSettings.housings)
                            foreach (var c in _lightShafts.Cones)
                                if (c.Parented && c.Light != null) _proxies.AddHousing(c.Light, c.Fakes);
                    }
                }
                catch (Exception e) { Logger.LogWarning($"Lamp cones: build failed: {e.Message}"); }
            }
            _shaftsFrame = Time.frameCount;
            float hazeRange = Mathf.Max(0f, _volSettings.hazeRange);
            int n = Math.Min(_lightCount, MaxLights);
            for (int i = 0; i < n; i++)
            {
                _volCone[i * 2] = new Vector4(0, 0, 0, -1f);
                _volCone[i * 2 + 1] = new Vector4(_lightMedium[i], 0, 0, 0); // x: the light's weight in the air (LightPolicy.Medium)
                var l = _packedLights[i];
                if (!_shaftSettings.enabled || ReferenceEquals(l, null) || l == null || !_lightShafts.TryGet(l, out var c)) continue;
                if (!c.InPlace) continue; // moved away from its mesh (parent-spot cones move with their light)
                if (c.Parented && _shaftSettings.beams && c.Hulls.Count > 0) continue; // drawn by the beam channel (VolumetricsBeamBuffer); the spot keeps its own haze
                // The cone gets `strength` in place of the haze range weight (Volumetrics.raytrace HazeWeight): the art says
                // this lamp's light is meant to be seen in the air, whatever its range.
                float r = _lightData[i * 4].w, haze = 1f;
                if (hazeRange > 0f) { float x = r / hazeRange; haze = 1f / (1f + x * x * x); }
                var axis = c.WorldAxis;
                _volCone[i * 2] = new Vector4(axis.x, axis.y, axis.z, c.CosHalf);
                _volCone[i * 2 + 1] = new Vector4(_lightMedium[i] * Mathf.Max(0f, _shaftSettings.strength) / Mathf.Max(haze, 1e-3f), c.Sharp, 0, 0);
            }
            _cmd.SetBufferData(_volConeBuffer, _volCone, 0, 0, Math.Max(1, n) * 2);
            return _volConeBuffer;
        }
    }
}
