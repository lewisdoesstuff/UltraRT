using System;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace UltraRT
{
    /// <summary>Reflection tunables (docs/reflections.md). Read every frame.</summary>
    public sealed class ReflectionSettings
    {
        /// <summary>-ultrart-noreflections / F4. Off: no reflection rays, the resolve only unpacks albedo and emission.</summary>
        public bool enabled = true;
        /// <summary>-ultrart-spec-weight: global weight of the specular classes (per-material weights are relative to it).</summary>
        public float specWeight = 0.3f;
        /// <summary>-ultrart-refl-strength: scales the game's _ReflectionStrength on REFLECTION materials.</summary>
        public float reflStrength = 1f;
        /// <summary>Luminance of the specular term below which a pixel traces no reflection ray.</summary>
        public float threshold = 0.015f;
        /// <summary>Firefly clamp (luminance) on glossy reflection samples.</summary>
        public float maxRadiance = 8f;
        public bool temporal = true;
        public bool spatial = true;
        /// <summary>Minimum temporal blend factor for mirrors (roughness 0) and rough reflections (roughness >= 0.5).</summary>
        public float alphaMirror = 0.3f, alphaRough = 0.08f;
        /// <summary>History clip half-size, in standard deviations of the current 3x3, for mirrors and rough reflections.</summary>
        public float clampMirror = 1.25f, clampRough = 2.5f;
        public int maxHistory = 32;
        public float reprojNormalTolerance = 0.9f;
        public float reprojPlaneTolerance = 0.01f;
        /// <summary>The spatial pass only blurs reflections at least this rough.</summary>
        public float spatialMinRoughness = 0.08f;

        /// <summary>-ultrart-refl-cache off|auto|always: radiance cache at reflection hits. Auto (1) uses it where the
        /// reflection footprint covers <see cref="cacheFootprint"/> of a cache cell, else 1-sample direct + cached indirect.</summary>
        public int cacheShading = 1;
        public float cacheFootprint = 0.5f;
        /// <summary>Hybrid: remove the game's own REFLECTION cubemap term where ours applies (-ultrart-refl-keepcube: off).</summary>
        public bool replaceGameCube = true;
        /// <summary>Hybrid cube removal: cap on the keep factor divided by what the game left of the lit colour.</summary>
        public float maxCubeKeep = 4f;

        /// <summary>The shaders' _ReflParams: x enabled, y spec weight, z refl strength, w threshold.</summary>
        public Vector4 ShaderParams => new Vector4(enabled ? 1f : 0f, specWeight, reflStrength, threshold);
        /// <summary>PathTrace's _ReflCache: x cache mode, y footprint threshold (cells), z replace the game cubemap.</summary>
        public Vector4 CacheParams => new Vector4(Mathf.Clamp(cacheShading, 0, 2), Mathf.Max(0f, cacheFootprint), replaceGameCube ? 1f : 0f, 0f);
    }

    public struct ReflectionInputs
    {
        public RenderTexture surface;     // ARGBInt (uint4): PathTrace _Surface
        public RenderTexture reflection;  // ARGBHalf: PathTrace _Reflection
        public RenderTexture normalDepth;
        public RenderTexture instanceId;
        public Matrix4x4 viewProj, invViewProj;
        public Vector3 camPos;
        // Outputs (trace resolution, enableRandomWrite)
        public RenderTexture albedo;      // ARGB32
        public RenderTexture emission;    // ARGBHalf
        public RenderTexture specular;    // ARGBHalf: rgb added, a keep
        public RenderTexture debug;       // ARGB32, written when debugView != 0
    }

    /// <summary>
    /// Temporal + spatial filter for the one-ray reflection signal, and the resolve pass that unpacks PathTrace's packed
    /// surface into the albedo/emission textures the denoiser and composites read. Reflections.compute; see
    /// docs/reflections.md. Call <see cref="Execute"/> once per path-traced frame, after DispatchRays.
    /// </summary>
    internal sealed class ReflectionFilter : IDisposable
    {
        private readonly ComputeShader _cs;
        private readonly int _kTemporal, _kSpatial, _kResolve;
        private int _width, _height, _cur;
        private bool _historyValid;
        private Matrix4x4 _prevViewProj;
        private readonly RenderTexture[] _hist = new RenderTexture[2];
        private readonly RenderTexture[] _geo = new RenderTexture[2];
        private RenderTexture _spatial;

        public ReflectionFilter(ComputeShader cs)
        {
            _cs = cs ? cs : throw new ArgumentNullException(nameof(cs));
            _kTemporal = cs.FindKernel("Temporal");
            _kSpatial = cs.FindKernel("Spatial");
            _kResolve = cs.FindKernel("Resolve");
        }

        public void Reset() => _historyValid = false;

        /// <param name="hybrid">Hybrid composite weights (dielectric specular is added on top of the game's colour).</param>
        /// <param name="debugView">0 none, 1 raw reflection, 2 filtered reflection, 3 specular contribution, 4 history length.</param>
        public void Execute(CommandBuffer cmd, in ReflectionInputs inp, ReflectionSettings s, bool hybrid, int debugView)
        {
            int w = inp.surface.width, h = inp.surface.height;
            var cs = _cs;
            int gx = Mathf.CeilToInt(w / 8f), gy = Mathf.CeilToInt(h / 8f);
            cmd.SetComputeVectorParam(cs, "_Dim", new Vector4(w, h, 1f / w, 1f / h));
            cmd.SetComputeVectorParam(cs, "_CamPos", inp.camPos);
            cmd.SetComputeMatrixParam(cs, "_InvViewProj", inp.invViewProj);
            cmd.SetComputeFloatParam(cs, "_MaxHistory", Mathf.Clamp(s.maxHistory, 1, 255));

            Texture filtered = inp.reflection;
            if (s.enabled && (s.temporal || s.spatial))
            {
                EnsureTargets(w, h);
                int prev = _cur ^ 1;
                cmd.SetComputeMatrixParam(cs, "_PrevViewProj", _historyValid ? _prevViewProj : inp.viewProj);

                // Temporal (with temporal off it still runs, with no history, to produce the packed geometry).
                cmd.SetComputeIntParam(cs, "_HistoryValid", _historyValid && s.temporal ? 1 : 0);
                cmd.SetComputeVectorParam(cs, "_AlphaMin", new Vector4(Mathf.Clamp01(s.alphaMirror), Mathf.Clamp01(s.alphaRough), 0, 0));
                cmd.SetComputeVectorParam(cs, "_ClampSigma", new Vector4(Mathf.Max(0f, s.clampMirror), Mathf.Max(0f, s.clampRough), 0, 0));
                cmd.SetComputeFloatParam(cs, "_NormalTol", s.reprojNormalTolerance);
                cmd.SetComputeFloatParam(cs, "_PlaneTol", Mathf.Max(0f, s.reprojPlaneTolerance));
                cmd.SetComputeTextureParam(cs, _kTemporal, "_Reflection", inp.reflection);
                cmd.SetComputeTextureParam(cs, _kTemporal, "_Surface", inp.surface);
                cmd.SetComputeTextureParam(cs, _kTemporal, "_NormalDepth", inp.normalDepth);
                cmd.SetComputeTextureParam(cs, _kTemporal, "_InstanceId", inp.instanceId);
                cmd.SetComputeTextureParam(cs, _kTemporal, "_PrevGeo", _geo[prev]);
                cmd.SetComputeTextureParam(cs, _kTemporal, "_HistIn", _hist[prev]);
                cmd.SetComputeTextureParam(cs, _kTemporal, "_HistOut", _hist[_cur]);
                cmd.SetComputeTextureParam(cs, _kTemporal, "_GeoOut", _geo[_cur]);
                cmd.DispatchCompute(cs, _kTemporal, gx, gy, 1);
                filtered = _hist[_cur];

                if (s.spatial)
                {
                    cmd.SetComputeFloatParam(cs, "_PixelAngle", PixelAngle(inp.invViewProj, inp.camPos, w, h));
                    cmd.SetComputeFloatParam(cs, "_PhiDepth", 1f);
                    cmd.SetComputeFloatParam(cs, "_SpatialMinRoughness", s.spatialMinRoughness);
                    cmd.SetComputeTextureParam(cs, _kSpatial, "_SpatialIn", _hist[_cur]);
                    cmd.SetComputeTextureParam(cs, _kSpatial, "_Geo", _geo[_cur]);
                    cmd.SetComputeTextureParam(cs, _kSpatial, "_Surface", inp.surface);
                    cmd.SetComputeTextureParam(cs, _kSpatial, "_SpatialOut", _spatial);
                    cmd.DispatchCompute(cs, _kSpatial, gx, gy, 1);
                    filtered = _spatial;
                }

                _prevViewProj = inp.viewProj;
                _historyValid = s.temporal;
                _cur = prev;
            }
            else _historyValid = false;

            cmd.SetComputeIntParam(cs, "_Enabled", s.enabled ? 1 : 0);
            cmd.SetComputeIntParam(cs, "_Hybrid", hybrid ? 1 : 0);
            cmd.SetComputeIntParam(cs, "_DebugView", debugView);
            cmd.SetComputeVectorParam(cs, "_ReflParams", s.ShaderParams);
            cmd.SetComputeFloatParam(cs, "_MaxCubeKeep", Mathf.Max(1f, s.maxCubeKeep));
            cmd.SetComputeTextureParam(cs, _kResolve, "_Surface", inp.surface);
            cmd.SetComputeTextureParam(cs, _kResolve, "_NormalDepth", inp.normalDepth);
            cmd.SetComputeTextureParam(cs, _kResolve, "_Reflection", inp.reflection);
            cmd.SetComputeTextureParam(cs, _kResolve, "_Filtered", filtered);
            cmd.SetComputeTextureParam(cs, _kResolve, "_AlbedoOut", inp.albedo);
            cmd.SetComputeTextureParam(cs, _kResolve, "_EmissionOut", inp.emission);
            cmd.SetComputeTextureParam(cs, _kResolve, "_SpecularOut", inp.specular);
            cmd.SetComputeTextureParam(cs, _kResolve, "_DebugOut", inp.debug);
            cmd.DispatchCompute(cs, _kResolve, gx, gy, 1);
        }

        /// <summary>World-space width of one trace pixel at distance 1, at the screen centre (as the denoiser).</summary>
        private static float PixelAngle(Matrix4x4 invViewProj, Vector3 camPos, int w, int h)
        {
            Vector3 Dir(float px, float py)
            {
                var ndc = new Vector3((px + 0.5f) / w * 2f - 1f, (py + 0.5f) / h * 2f - 1f, 1f);
                return (invViewProj.MultiplyPoint(ndc) - camPos).normalized;
            }
            float cx = w / 2, cy = h / 2;
            float a = (Dir(cx + 1, cy) - Dir(cx, cy)).magnitude;
            return float.IsNaN(a) || a <= 0f ? 1e-3f : a;
        }

        private void EnsureTargets(int w, int h)
        {
            if (_spatial != null && _width == w && _height == h && _spatial.IsCreated()) return;
            ReleaseTargets();
            _width = w;
            _height = h;
            for (int i = 0; i < 2; i++)
            {
                _hist[i] = Create($"UltraRT Reflection History {i}", RenderTextureFormat.ARGBHalf);
                _geo[i] = Create($"UltraRT Reflection Geo {i}", RenderTextureFormat.ARGBInt);
            }
            _spatial = Create("UltraRT Reflection Filtered", RenderTextureFormat.ARGBHalf);
            _cur = 0;
            _historyValid = false;
        }

        private RenderTexture Create(string name, RenderTextureFormat format)
        {
            var rt = new RenderTexture(_width, _height, 0, format, RenderTextureReadWrite.Linear)
            {
                name = name,
                enableRandomWrite = true,
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
                useMipMap = false,
                autoGenerateMips = false,
                hideFlags = HideFlags.HideAndDontSave,
            };
            rt.Create();
            return rt;
        }

        private static void Release(ref RenderTexture rt)
        {
            if (rt == null) return;
            rt.Release();
            Object.Destroy(rt);
            rt = null;
        }

        private void ReleaseTargets()
        {
            for (int i = 0; i < 2; i++)
            {
                Release(ref _hist[i]);
                Release(ref _geo[i]);
            }
            Release(ref _spatial);
            _historyValid = false;
        }

        public void Dispose() => ReleaseTargets();
    }
}
