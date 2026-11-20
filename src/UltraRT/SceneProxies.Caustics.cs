using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Object = UnityEngine.Object;

namespace UltraRT
{
    /// <summary>Water caustics (docs/caustics.md, "Implementation").</summary>
    internal sealed class CausticSettings
    {
        /// <summary>-ultrart-caustics X: strength of the game's caustic pattern in the traced modes (1 = the game's amount, default 2, 0 = off).
        /// Bench variant caustics.strength=0.</summary>
        public float strength = 2f; // 2 since 2026-11-19: our walls are lit brighter than the game's, 1 read weak
        /// <summary>-ultrart-caustics-noshadow: camera hits trace one ray toward the dominant CausticVolume, so V1, enemies
        /// and rocks shadow the pattern (caustics.shadows=0: unshadowed, as the game draws it).</summary>
        public bool shadows = true;
        /// <summary>-ultrart-ripple-caustics X / bench caustics.ripple: caustics of our own water ripples on what lies under
        /// a water plane (docs/caustics.md, section 6), as a factor on the camera hit's albedo; 1 = as tuned, 0 = off.</summary>
        public float ripple = 1f;
        /// <summary>How much more the light focuses than the reflection's ripple slopes alone would give (those focus only
        /// 7 m down). Depth x gain is soft-limited at 8 in the shader.</summary>
        public float rippleGain = 4f;
        /// <summary>Deepest receiver (m below the surface); faded out over the last 40 %.</summary>
        public float rippleDepth = 6f;
        /// <summary>Most water planes per frame (the nearest; the ray generation shader loops over them).</summary>
        public int ripplePlanes = 16;
    }

    /// <summary>
    /// ULTRAKILL/Master CAUSTICS on traced surfaces (docs/caustics.md). The game adds an unlit, world-projected, scrolling
    /// Voronoi pattern after lighting (like emission) inside designer-placed CausticVolume spheres; relight and pathtrace
    /// replace those pixels with RT albedo x lighting, so the pattern was lost. Proxy.shader evaluates the same formula at
    /// every hit on a CAUSTICS material and adds it as emission; on camera hits the ray generation shader shadows it with one
    /// ray toward the volume that contributes most (PathTrace.raytrace, CausticShadow).
    ///  - material: _CausticMode, _Scale, _Speed, _Falloff, _CausticColor, _CausticsDirection, _Cells, _Perlin (proxy: _Caustics
    ///    = mode + 1, _CausticColor, _CausticDir, _CausticParams, _CausticCells, _CausticPerlin);
    ///  - mode 2 needs the renderer's ObjectBoundsToShader bounds centre (its property block _BoundsCenter_VolumeCount): each
    ///    such renderer gets its own copy of the proxy material (<see cref="CausticVariant"/>), refreshed from the block;
    ///    without the block the game shows nothing, and neither do we;
    ///  - volumes: CausticVolumeManager.causticVolumes, packed like the game's _CausticVolumeData into the global
    ///    _UltraRTCausticVolumes every frame (<see cref="UpdateCaustics"/>).
    /// </summary>
    internal sealed partial class SceneProxies
    {
        public static readonly CausticSettings Caustics = new CausticSettings();

        private const int MaxCausticVolumes = 64;
        private const int CausticRefreshFrames = 30;

        private static readonly int IdCaustics = Shader.PropertyToID("_Caustics");
        private static readonly int IdCausticColor = Shader.PropertyToID("_CausticColor");
        private static readonly int IdCausticDir = Shader.PropertyToID("_CausticDir");
        private static readonly int IdCausticParams = Shader.PropertyToID("_CausticParams");
        private static readonly int IdCausticBounds = Shader.PropertyToID("_CausticBounds");
        private static readonly int IdCausticCells = Shader.PropertyToID("_CausticCells");
        private static readonly int IdCausticPerlin = Shader.PropertyToID("_CausticPerlin");
        private static readonly int IdGameBounds = Shader.PropertyToID("_BoundsCenter_VolumeCount");
        private static readonly int IdCausticVolumes = Shader.PropertyToID("_UltraRTCausticVolumes");
        private static readonly int IdCausticGlobal = Shader.PropertyToID("_UltraRTCaustics");

        private sealed class CausticRenderer
        {
            public Renderer Source;
            public Material Variant;
            public Vector4 Bounds;
        }

        private readonly Dictionary<(Material, Renderer), Material> _causticVariants = new Dictionary<(Material, Renderer), Material>();
        private readonly List<CausticRenderer> _causticRenderers = new List<CausticRenderer>();
        private readonly List<string> _causticLog = new List<string>();
        private readonly MaterialPropertyBlock _causticBlock = new MaterialPropertyBlock();
        private readonly Vector4[] _causticData = new Vector4[MaxCausticVolumes * 2];
        private readonly Vector4[] _causticUploaded = new Vector4[MaxCausticVolumes * 2];
        // Static: the global stays bound to a live buffer across scene rebuilds (a released buffer must never be bound).
        private static ComputeBuffer _causticBuffer;
        private int _causticCount = -1, _causticRefresh, _causticLogFrame = -100000;

        /// <summary>Traced CAUSTICS materials / per-renderer variants / volumes now (diagnostics).</summary>
        public int CausticMaterials { get; private set; }
        public int CausticVolumesActive => Mathf.Max(_causticCount, 0);

        private static bool HasCaustics(Material src) =>
            src != null && src.shader != null && src.shader.name == "ULTRAKILL/Master" && src.HasProperty(IdCausticColor) && src.HasProperty("_Cells")
            && (src.IsKeywordEnabled("CAUSTICS") || src.shaderKeywords.Contains("CAUSTICS")); // IsKeywordEnabled can miss Master keywords (_VERTEXCOLORS_ON)

        /// <summary>Proxy(): the caustic parameters of a CAUSTICS source material.</summary>
        private void ApplyCaustics(Material src, Material proxy)
        {
            if (!HasCaustics(src)) return;
            float mode = src.HasProperty("_CausticMode") ? Mathf.Round(src.GetFloat("_CausticMode")) : 0f;
            var cells = src.GetTexture("_Cells");
            var perlin = src.HasProperty("_Perlin") ? src.GetTexture("_Perlin") : null;
            if (cells == null || perlin == null || cells.dimension != UnityEngine.Rendering.TextureDimension.Tex2D || perlin.dimension != UnityEngine.Rendering.TextureDimension.Tex2D)
            {
                _causticLog.Add($"'{src.name}' skipped: cells {cells?.name ?? "-"} perlin {perlin?.name ?? "-"}");
                return;
            }
            Vector3 dir = src.HasProperty("_CausticsDirection") ? (Vector3)src.GetVector("_CausticsDirection") : Vector3.up;
            float scale = src.HasProperty("_Scale") ? src.GetFloat("_Scale") : 1f;
            float speed = src.HasProperty("_Speed") ? src.GetFloat("_Speed") : 1f;
            float falloff = src.HasProperty("_Falloff") ? src.GetFloat("_Falloff") : 1f;
            var color = src.GetColor(IdCausticColor);
            proxy.SetFloat(IdCaustics, mode + 1f);
            proxy.SetColor(IdCausticColor, color);
            proxy.SetVector(IdCausticDir, new Vector4(dir.x, dir.y, dir.z, 0f));
            proxy.SetVector(IdCausticParams, new Vector4(scale, speed, falloff, 0f));
            proxy.SetVector(IdCausticBounds, Vector4.zero); // mode 2: per renderer (CausticVariant)
            proxy.SetTexture(IdCausticCells, cells);
            proxy.SetTexture(IdCausticPerlin, perlin);
            CausticMaterials++;
            if (_causticLog.Count < 24)
                _causticLog.Add(System.FormattableString.Invariant($"'{src.name}' mode {mode:0} scale {scale:G3} speed {speed:G3} falloff {falloff:G3} colour ({color.r:F2}, {color.g:F2}, {color.b:F2}) dir ({dir.x:F2}, {dir.y:F2}, {dir.z:F2}) cells {cells.name} {cells.filterMode}"));
        }


        private static bool ProxyCausticMode2(Material proxy) =>
            proxy != null && proxy.HasProperty(IdCaustics) && Mathf.Abs(proxy.GetFloat(IdCaustics) - 3f) < 0.5f;

        /// <summary>
        /// Mode-2 caustics project along (renderer bounds centre - volume), so each renderer with ObjectBoundsToShader gets
        /// its own copy of the proxy material, which <see cref="UpdateCaustics"/> keeps on the game's bounds centre. Every
        /// other material comes back unchanged.
        /// </summary>
        private Material CausticVariant(Material proxy, Renderer source)
        {
            if (source == null || !ProxyCausticMode2(proxy) || source.GetComponent<ObjectBoundsToShader>() == null) return proxy;
            if (_causticVariants.TryGetValue((proxy, source), out var v)) return v;
            v = new Material(proxy) { name = proxy.name + " / caustics " + source.name };
            if (IsTwoSided(proxy)) _twoSidedProxies.Add(v);
            if (IsSkyWall(proxy)) _skyWallProxies.Add(v);
            RegisterRainVariant(proxy, v);
            _causticVariants[(proxy, source)] = v;
            var e = new CausticRenderer { Source = source, Variant = v, Bounds = Vector4.zero };
            RefreshCausticBounds(e);
            _causticRenderers.Add(e);
            return v;
        }

        private void CausticVariants(Renderer source, Material[] proxyMats)
        {
            if (CausticMaterials == 0 || source == null) return;
            for (int i = 0; i < proxyMats.Length; i++) proxyMats[i] = CausticVariant(proxyMats[i], source);
        }

        /// <summary>The game's bounds centre for this renderer (its property block), w = 1; zero while it has none.</summary>
        private void RefreshCausticBounds(CausticRenderer e)
        {
            if (e.Source == null || e.Variant == null) return;
            e.Source.GetPropertyBlock(_causticBlock);
            var b = !_causticBlock.isEmpty && _causticBlock.HasVector(IdGameBounds) ? _causticBlock.GetVector(IdGameBounds) : Vector4.zero;
            var v = new Vector4(b.x, b.y, b.z, _causticBlock.HasVector(IdGameBounds) ? 1f : 0f);
            if (v == e.Bounds) return;
            e.Bounds = v;
            e.Variant.SetVector(IdCausticBounds, v);
        }

        /// <summary>
        /// Per traced frame, before the dispatches: the active CausticVolumes into the global buffer (as the game packs
        /// _CausticVolumeData: position + nearRadius, colour x intensity + farRadius), the settings, and a slice of the
        /// renderers' bounds centres. The buffer is always bound (a 1-entry dummy without volumes).
        /// </summary>
        public void UpdateCaustics()
        {
            if (_causticBuffer == null)
            {
                _causticBuffer = new ComputeBuffer(MaxCausticVolumes * 2, 16);
                _causticBuffer.SetData(_causticUploaded);
            }
            int n = 0;
            if (CausticMaterials > 0 && Caustics.strength > 0f)
            {
                var mgr = MonoSingleton<CausticVolumeManager>.Instance;
                if (mgr != null && mgr.causticVolumes != null)
                    foreach (var cv in mgr.causticVolumes)
                    {
                        if (cv == null || !cv.isActiveAndEnabled || n >= MaxCausticVolumes) continue;
                        var p = cv.transform.position;
                        var c = cv.color * cv.intensity;
                        _causticData[2 * n] = new Vector4(p.x, p.y, p.z, cv.nearRadius);
                        _causticData[2 * n + 1] = new Vector4(c.r, c.g, c.b, cv.farRadius);
                        n++;
                    }
                bool changed = n != _causticCount;
                for (int i = 0; !changed && i < 2 * n; i++) changed = _causticData[i] != _causticUploaded[i];
                if (changed)
                {
                    System.Array.Copy(_causticData, _causticUploaded, 2 * n);
                    _causticBuffer.SetData(_causticUploaded, 0, 0, Mathf.Max(2 * n, 2));
                    if (Time.frameCount - _causticLogFrame > 60)
                    {
                        _causticLogFrame = Time.frameCount;
                        _log.LogInfo($"Caustics: {n} active volume(s), {CausticMaterials} CAUSTICS proxy material(s), {_causticRenderers.Count} mode-2 renderer(s) " +
                                     $"({_causticRenderers.Count(r => r.Bounds.w > 0f)} with the game's bounds centre)");
                    }
                }
                if (++_causticRefresh >= CausticRefreshFrames)
                {
                    _causticRefresh = 0;
                    foreach (var e in _causticRenderers) RefreshCausticBounds(e);
                }
            }
            _causticCount = n;
            Shader.SetGlobalBuffer(IdCausticVolumes, _causticBuffer);
            Shader.SetGlobalVector(IdCausticGlobal, new Vector4(CausticMaterials > 0 ? Mathf.Max(Caustics.strength, 0f) : 0f, n, Caustics.shadows ? 1f : 0f, 0f));
        }

        // ---------------------------------------------------------------- ripple caustics (docs/caustics.md, section 6)

        private const int MaxRipplePlanes = 32;
        private static readonly int IdWaterPlanes = Shader.PropertyToID("_UltraRTWaterPlanes");
        private static readonly int IdRipple = Shader.PropertyToID("_UltraRTRipple");
        private static ComputeBuffer _rippleBuffer; // static and never released, like _causticBuffer
        private readonly List<Renderer> _rippleWater = new List<Renderer>();
        private readonly Vector4[] _rippleData = new Vector4[MaxRipplePlanes * 2];
        private readonly Vector4[] _rippleUploaded = new Vector4[MaxRipplePlanes * 2];
        private readonly float[] _rippleDist = new float[MaxRipplePlanes];
        private int _rippleCount = -1, _rippleLogs;

        /// <summary>A water renderer (dynamic or static-batched) whose surface throws ripple caustics. Red "water" (6-2's
        /// blood pools, Water 5) is left out.</summary>
        private void NoteRippleWater(Renderer r)
        {
            if (r == null || _rippleWater.Contains(r)) return;
            var m = r.sharedMaterials.LastOrDefault(x => TransparentClassifier.Of(x) == TransparentKind.Water);
            var c = m != null && m.HasProperty("_Color") ? m.GetColor("_Color") : Color.white;
            if (c.r > 2f * Mathf.Max(c.g, c.b)) return; // 6-2: (1, 0, 0); 1-3's Water 2 is (1, 0.73, 0.73) and stays
            _rippleWater.Add(r);
        }

        /// <summary>
        /// Per traced frame: the nearest active water surfaces (up to <see cref="CausticSettings.ripplePlanes"/>) into the
        /// global _UltraRTWaterPlanes, as horizontal planes from their renderer bounds: the top is the surface; a thin plane
        /// lights receivers down to the deepest receiver depth, a water box (taller than 1 m) only inside itself. None while
        /// the camera is in water: the underwater screen effect draws its own caustics there (docs/vfx.md).
        /// </summary>
        public void UpdateRippleCaustics(Vector3 cam, float fadeDistance)
        {
            if (_rippleBuffer == null)
            {
                _rippleBuffer = new ComputeBuffer(MaxRipplePlanes * 2, 16);
                _rippleBuffer.SetData(_rippleUploaded);
            }
            var s = Caustics;
            int n = 0, cap = Mathf.Clamp(s.ripplePlanes, 0, MaxRipplePlanes);
            if (s.ripple > 0f && cap > 0 && _rippleWater.Count > 0 && !InsideWater(cam))
            {
                float reach = fadeDistance + 5f;
                foreach (var r in _rippleWater)
                {
                    if (r == null || !r.enabled || !r.gameObject.activeInHierarchy) continue;
                    var b = r.bounds;
                    float d = Mathf.Sqrt(b.SqrDistance(cam));
                    if (d > reach) continue;
                    // Insertion into the nearest `cap`.
                    int at = n;
                    while (at > 0 && _rippleDist[at - 1] > d) at--;
                    if (at >= cap) continue;
                    int last = Mathf.Min(n, cap - 1);
                    for (int k = last; k > at; k--)
                    {
                        _rippleDist[k] = _rippleDist[k - 1];
                        _rippleData[2 * k] = _rippleData[2 * k - 2];
                        _rippleData[2 * k + 1] = _rippleData[2 * k - 1];
                    }
                    var mn = b.min; var mx = b.max;
                    float lowest = b.size.y > 1f ? mn.y - 0.25f : mx.y - s.rippleDepth;
                    _rippleDist[at] = d;
                    _rippleData[2 * at] = new Vector4(mn.x, mn.z, mx.x, mx.z);
                    _rippleData[2 * at + 1] = new Vector4(mx.y, lowest, 0f, 0f);
                    if (n < cap) n++;
                }
            }
            bool changed = n != _rippleCount;
            for (int i = 0; !changed && i < 2 * n; i++) changed = _rippleData[i] != _rippleUploaded[i];
            if (changed)
            {
                System.Array.Copy(_rippleData, _rippleUploaded, 2 * n);
                if (n > 0) _rippleBuffer.SetData(_rippleUploaded, 0, 0, 2 * n);
                if (n != _rippleCount && _rippleLogs < 40)
                {
                    _rippleLogs++;
                    string near = n > 0 ? System.FormattableString.Invariant($", nearest surface y {_rippleUploaded[1].x:F2} over x {_rippleUploaded[0].x:F1}..{_rippleUploaded[0].z:F1} z {_rippleUploaded[0].y:F1}..{_rippleUploaded[0].w:F1}, receivers down to y {_rippleUploaded[1].y:F2}") : "";
                    _log.LogInfo($"Ripple caustics: {n} water plane(s) of {_rippleWater.Count}{near}");
                }
            }
            _rippleCount = n;
            Shader.SetGlobalBuffer(IdWaterPlanes, _rippleBuffer);
            Shader.SetGlobalVector(IdRipple, new Vector4(n > 0 ? s.ripple : 0f, n, Mathf.Max(s.rippleGain, 0f), Mathf.Max(s.rippleDepth, 0.5f)));
        }

        // ---------------------------------------------------------------- interactive ripples (Plugin.WaterRipples.cs)

        /// <summary>The surface (top of the renderer bounds, as the ripple caustics take it) of the active ripple water whose
        /// rectangle, grown by <paramref name="margin"/>, holds p and whose surface is nearest in height, within maxDy.</summary>
        public bool WaterSurfaceAt(Vector3 p, float maxDy, float margin, out float surfaceY)
        {
            surfaceY = 0f;
            float best = float.MaxValue;
            foreach (var r in _rippleWater)
            {
                if (r == null || !r.enabled || !r.gameObject.activeInHierarchy) continue;
                var b = r.bounds;
                if (p.x < b.min.x - margin || p.x > b.max.x + margin || p.z < b.min.z - margin || p.z > b.max.z + margin) continue;
                float dy = Mathf.Abs(p.y - b.max.y);
                if (dy > maxDy || dy >= best) continue;
                best = dy;
                surfaceY = b.max.y;
            }
            return best < float.MaxValue;
        }

        /// <summary>The first ripple water surface a ray from o along d (normalised) meets between minT and maxT, from above.</summary>
        public bool WaterAlongRay(Vector3 o, Vector3 d, float minT, float maxT, out Vector3 hit)
        {
            hit = Vector3.zero;
            float best = maxT;
            if (d.y > -1e-3f) return false;
            foreach (var r in _rippleWater)
            {
                if (r == null || !r.enabled || !r.gameObject.activeInHierarchy) continue;
                var b = r.bounds;
                if (o.y < b.max.y) continue;
                float t = (b.max.y - o.y) / d.y;
                if (t < minT || t >= best) continue;
                var q = o + d * t;
                if (q.x < b.min.x || q.x > b.max.x || q.z < b.min.z || q.z > b.max.z) continue;
                best = t;
                hit = q;
            }
            return best < maxT;
        }

        private void LogCaustics()
        {
            if (_causticLog.Count == 0) return;
            _log.LogInfo($"Caustics: {CausticMaterials} traced CAUSTICS material(s), {_causticRenderers.Count} mode-2 renderer variant(s): {string.Join("; ", _causticLog)}");
            _causticLog.Clear();
        }

        private void DisposeCaustics()
        {
            foreach (var m in _causticVariants.Values) if (m != null) Object.Destroy(m);
            _causticVariants.Clear();
            _causticRenderers.Clear();
            _causticCount = -1;
            CausticMaterials = 0;
            Shader.SetGlobalVector(IdCausticGlobal, Vector4.zero);
            _rippleWater.Clear();
            _rippleCount = -1;
            Shader.SetGlobalVector(IdRipple, Vector4.zero);
        }
    }
}
