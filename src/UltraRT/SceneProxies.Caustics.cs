using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Object = UnityEngine.Object;

namespace UltraRT
{
    /// <summary>Water caustics (docs/caustics.md, "Implementation").</summary>
    internal sealed class CausticSettings
    {
        /// <summary>-ultrart-caustics X: strength of the game's caustic pattern in the traced modes (1 = the game's, 0 = off).
        /// Bench variant caustics.strength=0.</summary>
        public float strength = 1f;
        /// <summary>-ultrart-caustics-noshadow: camera hits trace one ray toward the dominant CausticVolume, so V1, enemies
        /// and rocks shadow the pattern (caustics.shadows=0: unshadowed, as the game draws it).</summary>
        public bool shadows = true;
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
        }
    }
}
