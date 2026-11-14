using System;
using System.Globalization;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
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
        /// <summary>Stability features: red pre-blur strength, green cache seed, blue 3x3 history search, white firefly clamp.</summary>
        SpatialBoost = 6,
        /// <summary>Motion vectors: object motion as a flow colour (hue direction, saturation speed), camera motion faint.</summary>
        Motion = 7,
    }

    /// <summary>Which radiance cache value matches the denoiser's input (set per frame by the plugin).</summary>
    public enum DenoiserCacheChannel
    {
        /// <summary>No cache seed (inputs the cache has no equivalent for, e.g. hybrid's direct luminance pair).</summary>
        None = 0,
        /// <summary>Direct + indirect at the surface (PathTrace _Mode 0: full lighting).</summary>
        Total = 1,
        /// <summary>Indirect only (PathTrace _Mode 3: hybrid's bounce channel).</summary>
        Indirect = 2,
    }

    /// <summary>
    /// Ctrl+F3 cycles these. Full: the defaults (docs/denoiser.md, "Temporal stability" and "Static noise").
    /// Previous: the build before the static-noise work (alphaMin 0.1, 32 frames, no spatial variance floor, no output
    /// stabilisation, no guard band). Legacy: Previous without the four stability features (plain SVGF).
    /// </summary>
    public enum DenoiserPreset { Full, Previous, Legacy, Custom }

    /// <summary>Tunables, read every <see cref="Denoiser.Execute"/>. Safe to change at any time.</summary>
    public sealed class DenoiserSettings
    {
        /// <summary>A-trous iterations (steps 1, 2, 4, ...). 0 = temporal accumulation only.</summary>
        public int iterations = 3;
        /// <summary>5x5 B3-spline taps instead of 3x3. Smoother, roughly 2.5x the filter cost.</summary>
        public bool wideKernel = false;
        /// <summary>Lower bound on the temporal blend factor at <see cref="referenceHz"/>. Higher = less lag, more noise.</summary>
        public float alphaMin = 0.04f;
        /// <summary>History length cap (frames).</summary>
        public int maxHistory = 64;
        /// <summary>Frame rate the blend factors (alphaMin, stabilizeAlphaMin) are meant for. At other frame rates they are
        /// rescaled to the same time constant: 1 - (1 - alpha)^(referenceHz * dt). 0 = per frame, whatever the frame rate.</summary>
        public float referenceHz = 360f;
        /// <summary>The a-trous variance never drops below this x the input's 3x3 spatial variance (0 = plain SVGF). Needed
        /// for ReSTIR's temporally correlated direct-light noise, which the temporal moments don't see.</summary>
        public float spatialVarianceFloor = 2f;
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

        // ---- Temporal stability (docs/denoiser.md). `stability = false` is the previous behaviour, for A/B.
        /// <summary>Master switch for the four features below (Ctrl+F3, -ultrart-dn-legacy).</summary>
        public bool stability = true;
        /// <summary>Pre-blur: pixels with less history than this (frames) accumulate a geometry-aware blur of the input
        /// instead of their own sample; the strength falls linearly to 0 at this length. 0 = off.</summary>
        public float boostFrames = 8f;
        /// <summary>Pre-blur radius (trace pixels) at history 0. Clamped to 4 (the Temporal tile apron).</summary>
        public float boostRadius = 4f;
        /// <summary>Firefly clamp for pixels with 4+ frames of history: a sample is limited to
        /// max(2 ref, ref + fireflySigma * spread), ref and spread from the history and the 8 neighbours. 0 = off.</summary>
        public float fireflySigma = 8f;
        /// <summary>When all 4 bilinear history taps fail, search the 3x3 around the reprojected point for a matching texel.</summary>
        public bool historySearch = true;
        /// <summary>Radiance cache seed weight (virtual samples at history 0, fading like the pre-blur). 0 = off.
        /// Needs <see cref="Denoiser.CacheSource"/> and a channel other than None.</summary>
        public float cacheSeed = 1f;
        /// <summary>Cache seed consistency test: relative tolerance added to the pre-blur's own noise.</summary>
        public float cacheSeedTolerance = 0.5f;

        // ---- Temporal stabilisation of the filtered output (docs/denoiser.md, "Stabilisation"). Ctrl+F3 cycles
        //      stabilize + stability -> stability only -> neither.
        /// <summary>Blend the final filtered lighting with its own reprojected history, clamped to its temporal spread.</summary>
        public bool stabilize = true;
        /// <summary>Clamp half-size in temporal standard deviations of the filtered signal.</summary>
        public float stabilizeSigma = 2f;
        /// <summary>Lower bound on the stabilisation blend factor (0.05 = about 20 frames of averaging).</summary>
        public float stabilizeAlphaMin = 0.05f;
        /// <summary>Clamp tolerance relative to the current luminance (keeps a little slack where the spread is ~0).</summary>
        public float stabilizeTolerance = 0.02f;
        /// <summary>History length cap of the stabilisation (frames), on top of the history the input already has.</summary>
        public float stabilizeMaxLength = 32f;

        /// <summary>
        /// Defaults overridden by command-line flags (both denoiser instances):
        /// -ultrart-dn-legacy (stability off), -ultrart-dn-boost N (frames, 0 = off), -ultrart-dn-boost-radius R,
        /// -ultrart-dn-firefly K (0 = off), -ultrart-dn-nosearch, -ultrart-dn-cache W (0 = off), -ultrart-dn-noclamp,
        /// -ultrart-dn-iterations N.
        /// </summary>
        public DenoiserSettings Clone() => (DenoiserSettings)MemberwiseClone();

        /// <summary>Ctrl+F3 presets (the temporal/stability fields only; other fields are kept).</summary>
        public void ApplyPreset(DenoiserPreset p)
        {
            var d = new DenoiserSettings();
            bool full = p == DenoiserPreset.Full;
            alphaMin = full ? d.alphaMin : 0.1f;
            maxHistory = full ? d.maxHistory : 32;
            referenceHz = full ? d.referenceHz : 0f;
            spatialVarianceFloor = full ? d.spatialVarianceFloor : 0f;
            stabilize = full;
            stability = p != DenoiserPreset.Legacy;
        }

        /// <summary>Which preset these settings match (Custom when they match none).</summary>
        public DenoiserPreset CurrentPreset()
        {
            foreach (DenoiserPreset p in new[] { DenoiserPreset.Full, DenoiserPreset.Previous, DenoiserPreset.Legacy })
            {
                var t = Clone();
                t.ApplyPreset(p);
                if (t.alphaMin == alphaMin && t.maxHistory == maxHistory && t.referenceHz == referenceHz &&
                    t.spatialVarianceFloor == spatialVarianceFloor && t.stabilize == stabilize && t.stability == stability) return p;
            }
            return DenoiserPreset.Custom;
        }

        public static DenoiserSettings FromCommandLine()
        {
            var s = new DenoiserSettings();
            var a = Environment.GetCommandLineArgs();
            for (int i = 0; i < a.Length; i++)
            {
                string Next() => i + 1 < a.Length ? a[++i] : null;
                float F(float fallback) => float.TryParse(Next(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;
                switch (a[i].ToLowerInvariant())
                {
                    case "-ultrart-dn-legacy": s.ApplyPreset(DenoiserPreset.Legacy); break;
                    case "-ultrart-dn-previous": s.ApplyPreset(DenoiserPreset.Previous); break;
                    case "-ultrart-dn-alpha": s.alphaMin = F(s.alphaMin); break;
                    case "-ultrart-dn-spatialvar": s.spatialVarianceFloor = F(s.spatialVarianceFloor); break;
                    case "-ultrart-dn-nostabilize": s.stabilize = false; break;
                    case "-ultrart-dn-stabilize-alpha": s.stabilizeAlphaMin = F(s.stabilizeAlphaMin); break;
                    case "-ultrart-dn-stabilize-sigma": s.stabilizeSigma = F(s.stabilizeSigma); break;
                    case "-ultrart-dn-boost": s.boostFrames = F(s.boostFrames); break;
                    case "-ultrart-dn-boost-radius": s.boostRadius = F(s.boostRadius); break;
                    case "-ultrart-dn-firefly": s.fireflySigma = F(s.fireflySigma); break;
                    case "-ultrart-dn-nosearch": s.historySearch = false; break;
                    case "-ultrart-dn-cache": s.cacheSeed = F(s.cacheSeed); break;
                    case "-ultrart-dn-noclamp": s.clampHistory = false; break;
                    case "-ultrart-dn-iterations": s.iterations = (int)F(s.iterations); break;
                }
            }
            return s;
        }
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
        public RenderTexture instanceId;  // RGInt: x instance (-1 = sky), y packed world-space motion (0 = static)
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
        public DenoiserSettings Settings = DenoiserSettings.FromCommandLine();

        /// <summary>Radiance cache for the disocclusion seed (null = none). Read by the Temporal pass, after the cache's
        /// resolve in the same frame. Set per frame by the plugin (Plugin.Cache.cs), with the two fields below.</summary>
        internal RadianceCache CacheSource;
        /// <summary>The cache value that matches this instance's input this frame.</summary>
        public DenoiserCacheChannel CacheChannel = DenoiserCacheChannel.None;
        /// <summary>Added to the cached value: lighting terms PathTrace adds that the cache leaves out (ambient).</summary>
        public Vector3 CacheOffset;
        /// <summary>Seconds since the previous frame (set per frame by the plugin): blend factors are kept at the same time
        /// constant across frame rates (<see cref="DenoiserSettings.referenceHz"/>). 0 = assume the reference rate.</summary>
        public float FrameTime;
        /// <summary>Bench: when > 0, overrides <see cref="FrameTime"/> for every instance (captures run slower than real time).</summary>
        public static float FrameTimeOverride;

        private float RateAlpha(float alpha)
        {
            alpha = Mathf.Clamp01(alpha);
            float dt = FrameTimeOverride > 0f ? FrameTimeOverride : FrameTime;
            float hz = Settings.referenceHz;
            if (hz <= 0f || dt <= 0f) return alpha;
            float frames = Mathf.Clamp(dt * hz, 0.1f, 50f);
            return 1f - Mathf.Pow(1f - alpha, frames);
        }

        private readonly ComputeShader _shader;
        private readonly int _kTemporal, _kAtrous3, _kAtrous5, _kStabilize, _kComposite;

        private int _width, _height;
        private RenderTexture _hist;                       // first a-trous output, fed back as next frame's history
        private RenderTexture _t0, _t1;                   // integrated lighting / a-trous ping-pong
        private readonly RenderTexture[] _moments = new RenderTexture[2];
        private readonly RenderTexture[] _geo = new RenderTexture[2];
        private RenderTexture _boostInfo;                 // Temporal's debug channels (DenoiserDebugView.SpatialBoost)
        private RenderTexture _reproj;                    // Temporal's history footprint (R32G32_UInt), reused by Stabilize
        private readonly RenderTexture[] _stable = new RenderTexture[2]; // stabilised lighting (ping-pong)
        private readonly ComputeBuffer _dummyKeys, _dummyResolved; // bound when there is no cache (Temporal declares them)
        private uint _frame;
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
            IdHasSpecular = Shader.PropertyToID("_HasSpecular"),
            IdOutBoost = Shader.PropertyToID("_OutBoost"),
            IdBoostInfo = Shader.PropertyToID("_BoostInfo"),
            IdBoost = Shader.PropertyToID("_Boost"),
            IdCacheSeed = Shader.PropertyToID("_CacheSeed"),
            IdCacheSeedOffset = Shader.PropertyToID("_CacheSeedOffset"),
            IdDnFrame = Shader.PropertyToID("_DnFrame"),
            IdCacheKeys = Shader.PropertyToID("_CacheKeys"),
            IdCacheResolved = Shader.PropertyToID("_CacheResolved"),
            IdCacheMode = Shader.PropertyToID("_CacheMode"),
            IdOutReproj = Shader.PropertyToID("_OutReproj"),
            IdReproj = Shader.PropertyToID("_Reproj"),
            IdStabSrc = Shader.PropertyToID("_StabSrc"),
            IdStabMoments = Shader.PropertyToID("_StabMoments"),
            IdStableIn = Shader.PropertyToID("_StableIn"),
            IdStableOut = Shader.PropertyToID("_StableOut"),
            IdStabilize = Shader.PropertyToID("_Stabilize"),
            IdSpatialVarFloor = Shader.PropertyToID("_SpatialVarFloor");

        public Denoiser(ComputeShader shader)
        {
            _shader = shader ? shader : throw new ArgumentNullException(nameof(shader));
            _kTemporal = shader.FindKernel("Temporal");
            _kAtrous3 = shader.FindKernel("Atrous3");
            _kAtrous5 = shader.FindKernel("Atrous5");
            _kStabilize = shader.FindKernel("Stabilize");
            _kComposite = shader.FindKernel("Composite");
            _dummyKeys = new ComputeBuffer(8, 4) { name = "UltraRT Denoise cache keys (unused)" };
            _dummyKeys.SetData(new uint[8]);
            _dummyResolved = new ComputeBuffer(8, 16) { name = "UltraRT Denoise cache resolved (unused)" };
            _dummyResolved.SetData(new uint[32]);
        }

        /// <summary>Forget all history (scene load, teleport, respawn, camera cut, shader reload).</summary>
        public void Reset() { _historyValid = false; Generation++; }

        /// <summary>Counts <see cref="Reset"/> calls (the temporal upsampler drops its history along with the denoiser's).</summary>
        public int Generation { get; private set; }

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
            // Geometry weights: used by the a-trous passes and by Temporal's pre-blur.
            cmd.SetComputeFloatParam(cs, IdPhiNormal, s.phiNormal);
            cmd.SetComputeFloatParam(cs, IdPhiDepth, Mathf.Max(1e-3f, s.phiDepth));
            cmd.SetComputeFloatParam(cs, IdPixelAngle, PixelAngle(inputs.invViewProj, inputs.camPos, w, h));

            // 1. Temporal accumulation.
            cmd.SetComputeIntParam(cs, IdHistoryValid, _historyValid ? 1 : 0);
            cmd.SetComputeFloatParam(cs, IdAlphaMin, RateAlpha(s.alphaMin));
            cmd.SetComputeFloatParam(cs, IdSpatialVarFloor, Mathf.Max(0f, s.spatialVarianceFloor));
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
            cmd.SetComputeTextureParam(cs, _kTemporal, IdOutBoost, _boostInfo);
            cmd.SetComputeTextureParam(cs, _kTemporal, IdOutReproj, _reproj);
            bool st = s.stability;
            cmd.SetComputeVectorParam(cs, IdBoost, new Vector4(
                st ? Mathf.Max(0f, s.boostFrames) : 0f,
                st ? Mathf.Clamp(s.boostRadius, 0f, 4f) : 0f,
                st ? Mathf.Max(0f, s.fireflySigma) : 0f,
                st && s.historySearch ? 1f : 0f));
            bool seed = st && s.cacheSeed > 0f && CacheChannel != DenoiserCacheChannel.None && CacheSource != null
                        && CacheSource.BindComputeLookup(cmd, cs, _kTemporal);
            if (!seed)
            {
                cmd.SetComputeBufferParam(cs, _kTemporal, IdCacheKeys, _dummyKeys);
                cmd.SetComputeBufferParam(cs, _kTemporal, IdCacheResolved, _dummyResolved);
                cmd.SetComputeIntParam(cs, IdCacheMode, 0);
            }
            cmd.SetComputeVectorParam(cs, IdCacheSeed, new Vector4(seed ? s.cacheSeed : 0f, (int)CacheChannel, Mathf.Max(0f, s.cacheSeedTolerance), 0));
            cmd.SetComputeVectorParam(cs, IdCacheSeedOffset, CacheOffset);
            cmd.SetComputeIntParam(cs, IdDnFrame, unchecked((int)_frame++));
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
                cmd.SetComputeFloatParam(cs, IdPhiLuminance, Mathf.Max(1e-3f, s.phiLuminance));
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

            // 3. Temporal stabilisation of the filtered lighting (not fed back into the history above).
            if (s.stabilize)
            {
                cmd.SetComputeVectorParam(cs, IdStabilize, new Vector4(Mathf.Max(0f, s.stabilizeSigma), RateAlpha(s.stabilizeAlphaMin),
                    Mathf.Max(0f, s.stabilizeTolerance), Mathf.Max(1f, s.stabilizeMaxLength)));
                cmd.SetComputeTextureParam(cs, _kStabilize, IdStabSrc, filtered);
                cmd.SetComputeTextureParam(cs, _kStabilize, IdStabMoments, _moments[_cur]);
                cmd.SetComputeTextureParam(cs, _kStabilize, IdReproj, _reproj);
                cmd.SetComputeTextureParam(cs, _kStabilize, IdStableIn, _stable[prev]);
                cmd.SetComputeTextureParam(cs, _kStabilize, IdStableOut, _stable[_cur]);
                cmd.DispatchCompute(cs, _kStabilize, gx, gy, 1);
                filtered = _stable[_cur];
            }

            FilteredLighting = filtered;

            // 4. Composite.
            cmd.SetComputeVectorParam(cs, IdOutDim, new Vector4(output.width, output.height, 0, 0));
            cmd.SetComputeIntParam(cs, IdDebugView, (int)s.debugView);
            cmd.SetComputeTextureParam(cs, _kComposite, IdFiltered, filtered);
            cmd.SetComputeTextureParam(cs, _kComposite, IdAlbedo, inputs.albedo);
            cmd.SetComputeTextureParam(cs, _kComposite, IdEmission, inputs.emission);
            cmd.SetComputeTextureParam(cs, _kComposite, IdRadiance, inputs.radiance);
            cmd.SetComputeTextureParam(cs, _kComposite, IdMoments, _moments[_cur]);
            cmd.SetComputeTextureParam(cs, _kComposite, IdSpecular, inputs.specular != null ? (Texture)inputs.specular : Texture2D.blackTexture);
            cmd.SetComputeIntParam(cs, IdHasSpecular, inputs.specular != null ? 1 : 0);
            cmd.SetComputeTextureParam(cs, _kComposite, IdBoostInfo, _boostInfo);
            cmd.SetComputeTextureParam(cs, _kComposite, IdNormalDepth, inputs.normalDepth); // motion debug view
            cmd.SetComputeTextureParam(cs, _kComposite, IdInstanceId, inputs.instanceId);
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
            _boostInfo = Create("UltraRT Denoise Boost", RenderTextureFormat.ARGB32);
            _reproj = Create("UltraRT Denoise Reprojection", GraphicsFormat.R32G32_UInt);
            for (int i = 0; i < 2; i++) _stable[i] = Create($"UltraRT Denoise Stable {i}", RenderTextureFormat.ARGBHalf);
            _cur = 0;
            _historyValid = false;
        }

        private RenderTexture Create(string name, RenderTextureFormat format) =>
            Init(new RenderTexture(_width, _height, 0, format, RenderTextureReadWrite.Linear), name);

        private RenderTexture Create(string name, GraphicsFormat format) =>
            Init(new RenderTexture(_width, _height, 0, format), name);

        private static RenderTexture Init(RenderTexture rt, string name)
        {
            rt.name = name;
            rt.enableRandomWrite = true;
            rt.filterMode = FilterMode.Point;
            rt.wrapMode = TextureWrapMode.Clamp;
            rt.useMipMap = false;
            rt.autoGenerateMips = false;
            rt.hideFlags = HideFlags.HideAndDontSave;
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
            Release(ref _boostInfo);
            Release(ref _reproj);
            for (int i = 0; i < 2; i++) Release(ref _stable[i]);
            _historyValid = false;
        }

        public void Dispose()
        {
            ReleaseTargets();
            _dummyKeys?.Release();
            _dummyResolved?.Release();
        }
    }
}
