using System;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace UltraRT
{
    public enum DenoiserDebugView
    {
        Final = 0,
        FilteredLighting = 1,
        RawLighting = 2,
        Variance = 3,
        HistoryLength = 4,
        Disocclusion = 5,
    }

    /// <summary>Tunables, read every <see cref="Denoiser.Execute"/>. Safe to change at any time.</summary>
    public sealed class DenoiserSettings
    {
        /// <summary>A-trous iterations (steps 1, 2, 4, ...). 0 = temporal accumulation only.</summary>
        public int iterations = 3;
        /// <summary>5x5 B3-spline taps instead of 3x3. Smoother, roughly 2.5x the filter cost.</summary>
        public bool wideKernel = false;
        /// <summary>Lower bound on the temporal blend factor. Higher = less lag, more noise.</summary>
        public float alphaMin = 0.1f;
        /// <summary>History length cap (frames).</summary>
        public int maxHistory = 32;
        /// <summary>A-trous normal weight exponent: pow(max(0, dot(n, nq)), phiNormal).</summary>
        public float phiNormal = 128f;
        /// <summary>A-trous plane-distance tolerance, in pixel footprints per pixel of offset.</summary>
        public float phiDepth = 1f;
        /// <summary>A-trous luminance tolerance, in standard deviations.</summary>
        public float phiLuminance = 4f;
        /// <summary>Reprojection: minimum cosine between current and history normal.</summary>
        public float reprojNormalTolerance = 0.9f;
        /// <summary>Reprojection: maximum distance from the current surface's plane, relative to hit distance.</summary>
        public float reprojPlaneTolerance = 0.01f;
        /// <summary>Clip reprojected history to the current neighbourhood (fights ghosting from changing lights).</summary>
        public bool clampHistory = true;
        /// <summary>Clip box half-size in standard deviations.</summary>
        public float clampSigma = 2f;
        public DenoiserDebugView debugView = DenoiserDebugView.Final;
    }

    /// <summary>
    /// All textures at trace resolution, point filtered.
    /// prevViewProj / prevCamPos are accepted for API symmetry but not used: the denoiser remembers the
    /// matrix of its own previous <see cref="Denoiser.Execute"/>, which is what its history was written with.
    /// </summary>
    public struct DenoiserInputs
    {
        public RenderTexture radiance;    // ARGBHalf, demodulated lighting
        public RenderTexture albedo;      // ARGB32
        public RenderTexture emission;    // ARGBHalf
        public RenderTexture normalDepth; // ARGBHalf, xyz world normal, w ray t (< 0 = sky)
        public RenderTexture instanceId;  // RInt, -1 = sky
        public RenderTexture specular;    // optional ARGBHalf (reflections): rgb added, a = keep factor of the diffuse part
        public Matrix4x4 viewProj, invViewProj, prevViewProj;
        public Vector3 camPos, prevCamPos;
    }

    /// <summary>
    /// SVGF-style denoiser for the demodulated path-traced lighting. Owns its history; see docs/denoiser.md.
    /// Call <see cref="Execute"/> exactly once per traced frame and execute the command buffer once.
    /// </summary>
    internal sealed class Denoiser : IDisposable
    {
        public DenoiserSettings Settings = new DenoiserSettings();

        private readonly ComputeShader _shader;
        private readonly int _kTemporal, _kAtrous3, _kAtrous5, _kComposite;

        private int _width, _height;
        private RenderTexture _hist;                       // first a-trous output, fed back as next frame's history
        private RenderTexture _t0, _t1;                   // integrated lighting / a-trous ping-pong
        private readonly RenderTexture[] _moments = new RenderTexture[2];
        private readonly RenderTexture[] _geo = new RenderTexture[2];
        private int _cur;                                 // index written this frame
        private bool _historyValid;
        private Matrix4x4 _prevViewProj;

        private static readonly int
            IdDim = Shader.PropertyToID("_Dim"),
            IdCamPos = Shader.PropertyToID("_CamPos"),
            IdInvViewProj = Shader.PropertyToID("_InvViewProj"),
            IdPrevViewProj = Shader.PropertyToID("_PrevViewProj"),
            IdRadiance = Shader.PropertyToID("_Radiance"),
            IdNormalDepth = Shader.PropertyToID("_NormalDepth"),
            IdInstanceId = Shader.PropertyToID("_InstanceId"),
            IdPrevGeo = Shader.PropertyToID("_PrevGeo"),
            IdHistColor = Shader.PropertyToID("_HistColor"),
            IdPrevMoments = Shader.PropertyToID("_PrevMoments"),
            IdOutIntegrated = Shader.PropertyToID("_OutIntegrated"),
            IdOutMoments = Shader.PropertyToID("_OutMoments"),
            IdOutGeo = Shader.PropertyToID("_OutGeo"),
            IdHistoryValid = Shader.PropertyToID("_HistoryValid"),
            IdAlphaMin = Shader.PropertyToID("_AlphaMin"),
            IdMaxHistory = Shader.PropertyToID("_MaxHistory"),
            IdNormalTol = Shader.PropertyToID("_NormalTol"),
            IdPlaneTol = Shader.PropertyToID("_PlaneTol"),
            IdClampHistory = Shader.PropertyToID("_ClampHistory"),
            IdClampSigma = Shader.PropertyToID("_ClampSigma"),
            IdAtrousIn = Shader.PropertyToID("_AtrousIn"),
            IdGeo = Shader.PropertyToID("_Geo"),
            IdAtrousOut = Shader.PropertyToID("_AtrousOut"),
            IdStep = Shader.PropertyToID("_Step"),
            IdPrefilterVariance = Shader.PropertyToID("_PrefilterVariance"),
            IdPhiNormal = Shader.PropertyToID("_PhiNormal"),
            IdPhiDepth = Shader.PropertyToID("_PhiDepth"),
            IdPhiLuminance = Shader.PropertyToID("_PhiLuminance"),
            IdPixelAngle = Shader.PropertyToID("_PixelAngle"),
            IdFiltered = Shader.PropertyToID("_Filtered"),
            IdAlbedo = Shader.PropertyToID("_Albedo"),
            IdEmission = Shader.PropertyToID("_Emission"),
            IdMoments = Shader.PropertyToID("_Moments"),
            IdOutput = Shader.PropertyToID("_Output"),
            IdOutDim = Shader.PropertyToID("_OutDim"),
            IdDebugView = Shader.PropertyToID("_DebugView"),
            IdSpecular = Shader.PropertyToID("_Specular"),
            IdHasSpecular = Shader.PropertyToID("_HasSpecular");

        public Denoiser(ComputeShader shader)
        {
            _shader = shader ? shader : throw new ArgumentNullException(nameof(shader));
            _kTemporal = shader.FindKernel("Temporal");
            _kAtrous3 = shader.FindKernel("Atrous3");
            _kAtrous5 = shader.FindKernel("Atrous5");
            _kComposite = shader.FindKernel("Composite");
        }

        /// <summary>Forget all history (scene load, teleport, respawn, camera cut, shader reload).</summary>
        public void Reset() => _historyValid = false;

        /// <summary>The filtered, demodulated lighting (ARGBHalf, trace resolution) from the last Execute. For the ratio composite.</summary>
        public RenderTexture FilteredLighting { get; private set; }

        /// <summary>
        /// Records the denoiser into <paramref name="cmd"/> and writes albedo * lighting * spec.a + emission + spec.rgb (or the
        /// selected debug view) to <paramref name="output"/> (ARGB32, enableRandomWrite; any size, nearest upscale).
        /// </summary>
        public void Execute(CommandBuffer cmd, in DenoiserInputs inputs, RenderTexture output)
        {
            if (cmd == null) throw new ArgumentNullException(nameof(cmd));
            if (output == null) throw new ArgumentNullException(nameof(output));
            if (inputs.radiance == null || inputs.albedo == null || inputs.emission == null ||
                inputs.normalDepth == null || inputs.instanceId == null)
                throw new ArgumentException("Denoiser input texture missing", nameof(inputs));

            int w = inputs.radiance.width, h = inputs.radiance.height;
            EnsureTargets(w, h);
            var s = Settings;
            int prev = _cur ^ 1;
            var cs = _shader;

            // Shared parameters.
            cmd.SetComputeVectorParam(cs, IdDim, new Vector4(w, h, 1f / w, 1f / h));
            cmd.SetComputeVectorParam(cs, IdCamPos, inputs.camPos);
            cmd.SetComputeMatrixParam(cs, IdInvViewProj, inputs.invViewProj);
            cmd.SetComputeMatrixParam(cs, IdPrevViewProj, _historyValid ? _prevViewProj : inputs.viewProj);
            cmd.SetComputeFloatParam(cs, IdMaxHistory, Mathf.Clamp(s.maxHistory, 1, 255));

            // 1. Temporal accumulation.
            cmd.SetComputeIntParam(cs, IdHistoryValid, _historyValid ? 1 : 0);
            cmd.SetComputeFloatParam(cs, IdAlphaMin, Mathf.Clamp01(s.alphaMin));
            cmd.SetComputeFloatParam(cs, IdNormalTol, s.reprojNormalTolerance);
            cmd.SetComputeFloatParam(cs, IdPlaneTol, Mathf.Max(0f, s.reprojPlaneTolerance));
            cmd.SetComputeIntParam(cs, IdClampHistory, s.clampHistory ? 1 : 0);
            cmd.SetComputeFloatParam(cs, IdClampSigma, Mathf.Max(0f, s.clampSigma));
            cmd.SetComputeTextureParam(cs, _kTemporal, IdRadiance, inputs.radiance);
            cmd.SetComputeTextureParam(cs, _kTemporal, IdNormalDepth, inputs.normalDepth);
            cmd.SetComputeTextureParam(cs, _kTemporal, IdInstanceId, inputs.instanceId);
            cmd.SetComputeTextureParam(cs, _kTemporal, IdPrevGeo, _geo[prev]);
            cmd.SetComputeTextureParam(cs, _kTemporal, IdHistColor, _hist);
            cmd.SetComputeTextureParam(cs, _kTemporal, IdPrevMoments, _moments[prev]);
            cmd.SetComputeTextureParam(cs, _kTemporal, IdOutIntegrated, _t0);
            cmd.SetComputeTextureParam(cs, _kTemporal, IdOutMoments, _moments[_cur]);
            cmd.SetComputeTextureParam(cs, _kTemporal, IdOutGeo, _geo[_cur]);
            int gx = Mathf.CeilToInt(w / 8f), gy = Mathf.CeilToInt(h / 8f);
            cmd.DispatchCompute(cs, _kTemporal, gx, gy, 1);

            // 2. A-trous. Iteration 0 writes the feedback history; later ones ping-pong t0/t1.
            //    t0 -> hist -> t0 -> t1 -> t0 -> ...
            RenderTexture filtered = _t0;
            int iterations = Mathf.Clamp(s.iterations, 0, 5);
            if (iterations == 0)
            {
                cmd.CopyTexture(_t0, _hist);
            }
            else
            {
                int kAtrous = s.wideKernel ? _kAtrous5 : _kAtrous3;
                cmd.SetComputeFloatParam(cs, IdPhiNormal, s.phiNormal);
                cmd.SetComputeFloatParam(cs, IdPhiDepth, Mathf.Max(1e-3f, s.phiDepth));
                cmd.SetComputeFloatParam(cs, IdPhiLuminance, Mathf.Max(1e-3f, s.phiLuminance));
                cmd.SetComputeFloatParam(cs, IdPixelAngle, PixelAngle(inputs.invViewProj, inputs.camPos, w, h));
                cmd.SetComputeTextureParam(cs, kAtrous, IdGeo, _geo[_cur]);
                RenderTexture src = _t0;
                for (int i = 0; i < iterations; i++)
                {
                    RenderTexture dst = i == 0 ? _hist : (src == _t0 ? _t1 : _t0);
                    cmd.SetComputeIntParam(cs, IdStep, 1 << i);
                    cmd.SetComputeIntParam(cs, IdPrefilterVariance, i == 0 ? 1 : 0);
                    cmd.SetComputeTextureParam(cs, kAtrous, IdAtrousIn, src);
                    cmd.SetComputeTextureParam(cs, kAtrous, IdAtrousOut, dst);
                    cmd.DispatchCompute(cs, kAtrous, gx, gy, 1);
                    src = dst;
                }
                filtered = src;
            }

            FilteredLighting = filtered;

            // 3. Composite.
            cmd.SetComputeVectorParam(cs, IdOutDim, new Vector4(output.width, output.height, 0, 0));
            cmd.SetComputeIntParam(cs, IdDebugView, (int)s.debugView);
            cmd.SetComputeTextureParam(cs, _kComposite, IdFiltered, filtered);
            cmd.SetComputeTextureParam(cs, _kComposite, IdAlbedo, inputs.albedo);
            cmd.SetComputeTextureParam(cs, _kComposite, IdEmission, inputs.emission);
            cmd.SetComputeTextureParam(cs, _kComposite, IdRadiance, inputs.radiance);
            cmd.SetComputeTextureParam(cs, _kComposite, IdMoments, _moments[_cur]);
            cmd.SetComputeTextureParam(cs, _kComposite, IdSpecular, inputs.specular != null ? (Texture)inputs.specular : Texture2D.blackTexture);
            cmd.SetComputeIntParam(cs, IdHasSpecular, inputs.specular != null ? 1 : 0);
            cmd.SetComputeTextureParam(cs, _kComposite, IdOutput, output);
            cmd.DispatchCompute(cs, _kComposite, Mathf.CeilToInt(output.width / 8f), Mathf.CeilToInt(output.height / 8f), 1);

            // History for the next frame.
            _prevViewProj = inputs.viewProj;
            _historyValid = true;
            _cur = prev;
        }

        /// <summary>World-space width of one trace pixel at distance 1, measured at the screen centre.</summary>
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
            if (_hist != null && _width == w && _height == h && _hist.IsCreated()) return;
            ReleaseTargets();
            _width = w;
            _height = h;
            _hist = Create("UltraRT Denoise History", RenderTextureFormat.ARGBHalf);
            _t0 = Create("UltraRT Denoise T0", RenderTextureFormat.ARGBHalf);
            _t1 = Create("UltraRT Denoise T1", RenderTextureFormat.ARGBHalf);
            for (int i = 0; i < 2; i++)
            {
                _moments[i] = Create($"UltraRT Denoise Moments {i}", RenderTextureFormat.ARGBHalf);
                _geo[i] = Create($"UltraRT Denoise Geo {i}", RenderTextureFormat.ARGBInt);
            }
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
            Release(ref _hist);
            Release(ref _t0);
            Release(ref _t1);
            for (int i = 0; i < 2; i++)
            {
                Release(ref _moments[i]);
                Release(ref _geo[i]);
            }
            _historyValid = false;
        }

        public void Dispose() => ReleaseTargets();
    }
}
