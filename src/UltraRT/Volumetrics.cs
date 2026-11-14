using System;
using System.Globalization;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace UltraRT
{
    /// <summary>Volumetric lighting knobs (docs/volumetrics.md). Public fields are bench variants: vol.&lt;field&gt;=value.</summary>
    public sealed class VolumetricsSettings
    {
        public bool enabled = true;
        /// <summary>Also in pt-dn and hybrid (relight always, when enabled). Hybrid gets the in-scattering only: the game's
        /// colour already carries its fog.</summary>
        public bool ptDn = true, hybrid = false;
        /// <summary>Screen pixels per low-res pixel along each axis (4 = quarter resolution).</summary>
        public int block = 4;
        /// <summary>Sun shadow rays per low-res pixel and frame.</summary>
        public int steps = 8;
        /// <summary>Rays end at the visible surface or this far (m).</summary>
        public float maxDistance = 100f;
        /// <summary>Medium density from the game's fog (extinction per metre, ln 2 over the middle of a linear fog), times this.</summary>
        public float densityScale = 1f;
        /// <summary>Density where the game's fog is off or thinner (per metre): sun shafts and light halos without haze.</summary>
        public float minDensity = 0.002f;
        public float maxDensity = 0.008f;
        /// <summary>In-scattering multiplier.</summary>
        public float strength = 1f;
        /// <summary>Henyey-Greenstein anisotropy: forward scattering makes shafts brighter looking toward the sun.</summary>
        public float anisotropy = 0.4f;
        /// <summary>How much of the fog colour's hue the medium takes (0 = white scattering, 1 = the fog colour's hue).</summary>
        public float tint = 0.5f;
        /// <summary>Point/spot light scattering multiplier (0 = sun only) and RIS candidates per pixel.</summary>
        public float lights = 1f;
        public int lightCandidates = 8;
        /// <summary>Point/spot light samples (shadow rays) per low-res pixel and frame, stratified over one light CDF.</summary>
        public int lightSamples = 1;
        /// <summary>Emissive surface samples (each RIS over 6 points, one shadow ray) per low-res pixel and frame.</summary>
        public int emissiveSamples = 1;
        /// <summary>Let lights attached to the player or the camera light the medium (off: the player's own light sits at
        /// the eye and lit the air over the whole screen).</summary>
        public bool playerLights = false;
        /// <summary>Range (m) at which a point/spot light's haze is halved (1 / (1 + (range / hazeRange)^3); 0 = off): lights
        /// that reach across a whole room are mood/fill lighting, not lamps the air glows around.</summary>
        public float hazeRange = 20f;
        /// <summary>Soft core of a light in the medium, as a fraction of its range (inverse square -> 1 / (d^2 + (core range)^2)).</summary>
        public float core = 0f;
        /// <summary>Sun scattering multiplier (0 = no sun shafts; for checking which term a look or noise comes from).</summary>
        public float sun = 1f;
        /// <summary>Emissive surface scattering multiplier (lava, glowing panels; one sample per pixel, 0 = off).</summary>
        public float emissive = 0.5f;
        /// <summary>Firefly clamp: max luminance (linear) of a pixel's one-sample light + emissive estimate.</summary>
        public float maxSample = 2f;
        /// <summary>Apply the game's own distance fog to ray-traced pixels (the replace modes lost it).</summary>
        public bool gameFog = true;
        public float alphaMin = 0.03f;
        public float maxHistory = 64f;
        /// <summary>Spatial Gaussian of this frame's samples (low-res px; 5x5 taps).</summary>
        public float spatialSigma = 1.5f;
        public float clipSigma = 3f;
        public float depthTolerance = 0.1f;
        public float spatialDepthSigma = 0.05f;
        /// <summary>Variance-guided a-trous passes over the accumulated in-scattering (step 1, 2, 4, ...; 0 = none).</summary>
        public int atrous = 3;
        /// <summary>A-trous luminance edge stop, in standard deviations of the estimate (larger = blurrier).</summary>
        public float atrousLumSigma = 4f;
        /// <summary>A-trous depth edge stop (relative ray length).</summary>
        public float atrousDepthSigma = 0.1f;
        /// <summary>Samples the temporal pass's 5x5 prefilter is credited with in the variance estimate.</summary>
        public float prefilterSamples = 8f;
        /// <summary>Fastest blend of the luminance moments (their variance must follow lighting changes).</summary>
        public float momentsAlpha = 0.1f;
        /// <summary>How much of the haze auto exposure meters with the image (0 = none: a hazy dark scene is lifted as dark).</summary>
        public float meterHaze = 0f;
        /// <summary>1 = show the exposed in-scattering only, 2 = the same without the temporal filter (raw samples).</summary>
        public int debugView;

        public void ParseArgs()
        {
            var a = Environment.GetCommandLineArgs();
            for (int i = 0; i < a.Length; i++)
            {
                string Next() => i + 1 < a.Length ? a[++i] : null;
                float F(float fallback) => float.TryParse(Next(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;
                int I(int fallback) => int.TryParse(Next(), out var v) ? v : fallback;
                switch (a[i].ToLowerInvariant())
                {
                    case "-ultrart-volumetrics":
                    {
                        var v = (Next() ?? "").ToLowerInvariant();
                        enabled = v != "off" && v != "0" && v != "false";
                        break;
                    }
                    case "-ultrart-vol-density": densityScale = F(densityScale); break;
                    case "-ultrart-vol-min-density": minDensity = F(minDensity); break;
                    case "-ultrart-vol-strength": strength = F(strength); break;
                    case "-ultrart-vol-steps": steps = I(steps); break;
                    case "-ultrart-vol-block": block = I(block); break;
                    case "-ultrart-vol-g": anisotropy = F(anisotropy); break;
                    case "-ultrart-vol-lights": lights = F(lights); break;
                    case "-ultrart-vol-emissive": emissive = F(emissive); break;
                    case "-ultrart-vol-distance": maxDistance = F(maxDistance); break;
                    case "-ultrart-vol-nofog": gameFog = false; break;
                    case "-ultrart-vol-hybrid": hybrid = true; break;
                }
            }
        }

        public override string ToString() =>
            FormattableString.Invariant($"{(enabled ? "on" : "off")}, 1/{block} res, {steps} steps, {maxDistance} m, density x{densityScale} (min {minDensity}/m), ") +
            FormattableString.Invariant($"strength {strength}, g {anisotropy}, lights {lights} ({lightSamples} spp, {lightCandidates} candidates), emissive {emissive} ({emissiveSamples} spp), ") +
            FormattableString.Invariant($"a-trous {atrous}, game fog {(gameFog ? "on" : "off")}");
    }

    /// <summary>What the plugin knows about the frame for the volumetric pass.</summary>
    internal struct VolumetricsFrame
    {
        public Camera camera;
        public RenderTexture gameDepth;    // copy of the game's depth (raw, screen resolution)
        public Texture target;              // the camera's colour target (PostProcessV2_Handler.mainTex)
        public RayTracingAccelerationStructure rtas;
        public ComputeBuffer lights;
        public ComputeBuffer skip;         // one bit per light: skipped by the medium (attached to the player / camera)
        public int lightCount;
        public Vector4 sunDir;             // xyz towards the sun, w cos(angular radius); w = 0: no sun
        public Color sunColor;             // linear irradiance
        public Vector4 falloff;            // _VolFalloff
        public float localLightCount;
        public bool applyGameFog;          // replaced pixels are marked in _UltraRTUpsampled
        public bool autoExposure;
        public ComputeBuffer exposure;
        public Material composite;
        public uint frameIndex;
    }

    /// <summary>
    /// Ray-traced single scattering (docs/volumetrics.md): Volumetrics.raytrace at 1/block resolution, temporal accumulation
    /// in Volumetrics.compute, and VolumetricsComposite.shader, which upsamples it with depth weights, restores the game's
    /// fog on ray-traced pixels and adds the in-scattered light.
    /// </summary>
    internal sealed class Volumetrics : IDisposable
    {
        public VolumetricsSettings Settings;
        public Vector4 Medium { get; private set; }
        /// <summary>The ray tracing shader, for binding shared scene inputs (emissive triangles) before <see cref="Execute"/>.</summary>
        public RayTracingShader RtShader => _rt;
        public string MediumSource { get; private set; } = "-";
        /// <summary>The last frame's filtered low-res in-scattering (linear, unexposed) and its size, for auto exposure.</summary>
        public Texture Result { get; private set; }
        public Vector2 ResultSize { get; private set; }

        private readonly RayTracingShader _rt;
        private readonly ComputeShader _cs;
        private readonly int _kDepth, _kTemporal, _kAtrous;
        private RenderTexture _raw, _depth;
        private readonly RenderTexture[] _hist = new RenderTexture[2];
        private readonly RenderTexture[] _histDepth = new RenderTexture[2];
        private readonly RenderTexture[] _mom = new RenderTexture[2];
        private readonly RenderTexture[] _filt = new RenderTexture[2];
        private RenderTexture _sceneCopy;
        private int _cur;
        private bool _historyValid;
        private Matrix4x4 _prevVP;
        private Vector3 _prevCamPos;
        private ComputeBuffer _dummyExposure;

        private static readonly int
            IdGameDepth = Shader.PropertyToID("_GameDepth"), IdDepthOut = Shader.PropertyToID("_DepthOut"),
            IdVolScreen = Shader.PropertyToID("_VolScreen"), IdVolLowDim = Shader.PropertyToID("_VolLowDim"),
            IdZParams = Shader.PropertyToID("_ZParams"), IdViewRay = Shader.PropertyToID("_ViewRay"), IdVolSky = Shader.PropertyToID("_VolSky"),
            IdVolIn = Shader.PropertyToID("_VolIn"), IdVolDepth = Shader.PropertyToID("_VolDepth"),
            IdHistIn = Shader.PropertyToID("_HistIn"), IdHistDepthIn = Shader.PropertyToID("_HistDepthIn"),
            IdHistOut = Shader.PropertyToID("_HistOut"), IdHistDepthOut = Shader.PropertyToID("_HistDepthOut"),
            IdInvViewProj = Shader.PropertyToID("_InvViewProj"), IdPrevViewProj = Shader.PropertyToID("_PrevViewProj"),
            IdCamPos = Shader.PropertyToID("_CamPos"), IdPrevCamPos = Shader.PropertyToID("_PrevCamPos"),
            IdTemporalParams = Shader.PropertyToID("_TemporalParams"), IdTemporalParams2 = Shader.PropertyToID("_TemporalParams2"),
            IdVolOut = Shader.PropertyToID("_VolOut"), IdVolInvViewProj = Shader.PropertyToID("_VolInvViewProj"),
            IdVolCamPos = Shader.PropertyToID("_VolCamPos"), IdVolDim = Shader.PropertyToID("_VolDim"),
            IdVolMedium = Shader.PropertyToID("_VolMedium"), IdVolParams = Shader.PropertyToID("_VolParams"),
            IdVolSunDir = Shader.PropertyToID("_VolSunDir"), IdVolSunColor = Shader.PropertyToID("_VolSunColor"),
            IdVolLightParams = Shader.PropertyToID("_VolLightParams"), IdVolFalloff = Shader.PropertyToID("_VolFalloff"),
            IdLights = Shader.PropertyToID("_Lights"), IdScene = Shader.PropertyToID("_Scene"),
            IdSceneColor = Shader.PropertyToID("_VolSceneColor"), IdVolHist = Shader.PropertyToID("_VolHist"),
            IdVolDepthTex = Shader.PropertyToID("_VolDepthTex"), IdVolExposure = Shader.PropertyToID("_VolExposure"),
            IdVolViewRay = Shader.PropertyToID("_VolViewRay"), IdVolFog = Shader.PropertyToID("_VolFog"),
            IdVolFogColor = Shader.PropertyToID("_VolFogColor"), IdVolComposite = Shader.PropertyToID("_VolComposite"),
            IdVolEmissive = Shader.PropertyToID("_VolEmissive"), IdVolClamp = Shader.PropertyToID("_VolClamp"),
            IdVolSamples = Shader.PropertyToID("_VolSamples"), IdVolLightShape = Shader.PropertyToID("_VolLightShape"),
            IdVolSkip = Shader.PropertyToID("_VolSkip"), IdMomIn = Shader.PropertyToID("_MomIn"), IdMomOut = Shader.PropertyToID("_MomOut"),
            IdTemporalParams3 = Shader.PropertyToID("_TemporalParams3"), IdAtrousIn = Shader.PropertyToID("_AtrousIn"),
            IdAtrousMom = Shader.PropertyToID("_AtrousMom"), IdAtrousOut = Shader.PropertyToID("_AtrousOut"),
            IdAtrousParams = Shader.PropertyToID("_AtrousParams"), IdAtrousParams2 = Shader.PropertyToID("_AtrousParams2");

        private const float SkyDistance = 1e4f;

        public Volumetrics(RayTracingShader rt, ComputeShader cs, VolumetricsSettings settings)
        {
            _rt = rt ? rt : throw new ArgumentNullException(nameof(rt));
            _cs = cs ? cs : throw new ArgumentNullException(nameof(cs));
            _kDepth = cs.FindKernel("Depth");
            _kTemporal = cs.FindKernel("Temporal");
            _kAtrous = cs.FindKernel("Atrous");
            Settings = settings;
            rt.SetShaderPass("UltraRTHit");
        }

        public void Reset() => _historyValid = false;

        /// <summary>
        /// The medium for the level's current fog (RenderSettings, which the game changes per area). x: extinction per metre.
        /// Linear fog maps to the density whose transmittance is 50% in the middle of the fog range; exponential fog uses
        /// its own density. Clamped to [minDensity, maxDensity]. yzw: scattering tint (the fog colour's hue, scaled to max 1).
        /// </summary>
        public static Vector4 MediumFromFog(VolumetricsSettings s, out string source)
        {
            float sigma = 0f;
            source = "none";
            if (RenderSettings.fog)
            {
                switch (RenderSettings.fogMode)
                {
                    case FogMode.Linear:
                    {
                        float mid = 0.5f * (RenderSettings.fogStartDistance + RenderSettings.fogEndDistance);
                        sigma = mid > 0.01f ? 0.6931f / mid : 0f;
                        source = FormattableString.Invariant($"linear {RenderSettings.fogStartDistance:F0}-{RenderSettings.fogEndDistance:F0} m");
                        break;
                    }
                    case FogMode.Exponential:
                        sigma = RenderSettings.fogDensity;
                        source = FormattableString.Invariant($"exp {RenderSettings.fogDensity:F4}");
                        break;
                    default:
                        sigma = RenderSettings.fogDensity;
                        source = FormattableString.Invariant($"exp2 {RenderSettings.fogDensity:F4}");
                        break;
                }
            }
            sigma = Mathf.Clamp(sigma * Mathf.Max(0f, s.densityScale), Mathf.Max(0f, s.minDensity), Mathf.Max(s.minDensity, s.maxDensity));
            var fc = RenderSettings.fogColor;
            float m = Mathf.Max(fc.r, Mathf.Max(fc.g, fc.b));
            var tint = Vector3.one;
            if (RenderSettings.fog && m > 0.02f)
                tint = Vector3.Lerp(Vector3.one, new Vector3(fc.r / m, fc.g / m, fc.b / m), Mathf.Clamp01(s.tint));
            return new Vector4(sigma, tint.x, tint.y, tint.z);
        }

        /// <param name="lateCmd">Runs after the game's transparents (CameraEvent.AfterForwardAlpha); null = all in cmd.</param>
        public void Execute(CommandBuffer cmd, CommandBuffer lateCmd, in VolumetricsFrame f)
        {
            var s = Settings;
            var cam = f.camera;
            int W = f.gameDepth.width, H = f.gameDepth.height;
            int blk = Mathf.Clamp(s.block, 1, 8);
            int w = (W + blk - 1) / blk, h = (H + blk - 1) / blk;
            EnsureTargets(w, h, f.target);

            var proj = cam.projectionMatrix;
            var vp = proj * cam.worldToCameraMatrix;
            var invVP = vp.inverse;
            float n = cam.nearClipPlane, far = cam.farClipPlane;
            Vector4 z = SystemInfo.usesReversedZBuffer
                ? new Vector4(-1f + far / n, 1f, (-1f + far / n) / far, 1f / far)
                : new Vector4(1f - far / n, far / n, (1f - far / n) / far, far / n / far);
            var screen = new Vector4(W, H, blk, 0);
            var low = new Vector4(w, h, 1f / w, 1f / h);
            var viewRay = new Vector4(1f / proj.m00, 1f / proj.m11, 0, 0);
            float skyFrom = far * 0.999f;

            // 1. ray lengths
            cmd.SetComputeTextureParam(_cs, _kDepth, IdGameDepth, f.gameDepth, 0, RenderTextureSubElement.Depth);
            cmd.SetComputeTextureParam(_cs, _kDepth, IdDepthOut, _depth);
            cmd.SetComputeVectorParam(_cs, IdVolScreen, screen);
            cmd.SetComputeVectorParam(_cs, IdVolLowDim, low);
            cmd.SetComputeVectorParam(_cs, IdZParams, z);
            cmd.SetComputeVectorParam(_cs, IdViewRay, viewRay);
            cmd.SetComputeVectorParam(_cs, IdVolSky, new Vector4(SkyDistance, skyFrom, 0, 0));
            cmd.DispatchCompute(_cs, _kDepth, (w + 7) / 8, (h + 7) / 8, 1);

            // 2. in-scattering
            var medium = MediumFromFog(s, out var source);
            Medium = medium;
            MediumSource = source;
            float sigma = medium.x;
            cmd.SetRayTracingAccelerationStructure(_rt, IdScene, f.rtas);
            cmd.SetRayTracingTextureParam(_rt, IdVolOut, _raw);
            cmd.SetRayTracingTextureParam(_rt, IdVolDepth, _depth);
            // The ray of low-res pixel i goes through screen pixel i * blk + blk / 2 (as in the Depth kernel): express that
            // as a low-res uv by shrinking the viewProj's NDC accordingly.
            cmd.SetRayTracingMatrixParam(_rt, IdVolInvViewProj, invVP * LowResToScreen(W, H, w, h, blk));
            cmd.SetRayTracingVectorParam(_rt, IdVolCamPos, cam.transform.position);
            cmd.SetRayTracingVectorParam(_rt, IdVolDim, low);
            cmd.SetRayTracingVectorParam(_rt, IdVolMedium, new Vector4(sigma * medium.y, sigma * medium.z, sigma * medium.w, sigma));
            cmd.SetRayTracingVectorParam(_rt, IdVolParams, new Vector4(Mathf.Clamp(s.steps, 1, 64), Mathf.Max(1f, s.maxDistance),
                Mathf.Clamp(s.anisotropy, -0.95f, 0.95f), f.frameIndex & 0xFFFFF));
            cmd.SetRayTracingVectorParam(_rt, IdVolSunDir, f.sunDir.w > 0f ? f.sunDir : new Vector4(0, 1, 0, 1));
            float sunScale = Mathf.Max(0f, s.sun);
            cmd.SetRayTracingVectorParam(_rt, IdVolSunColor, new Vector4(f.sunColor.r * sunScale, f.sunColor.g * sunScale, f.sunColor.b * sunScale,
                f.sunDir.w > 0f && sunScale > 0f ? 1f : 0f));
            cmd.SetRayTracingVectorParam(_rt, IdVolLightShape, new Vector4(Mathf.Max(0f, s.hazeRange), Mathf.Max(0f, s.core), 0, 0));
            cmd.SetRayTracingVectorParam(_rt, IdVolSamples, new Vector4(Mathf.Clamp(s.lightSamples, 1, 8), Mathf.Clamp(s.emissiveSamples, 1, 8), 0, 0));
            cmd.SetRayTracingVectorParam(_rt, IdVolLightParams, new Vector4(f.localLightCount, Mathf.Clamp(s.lightCandidates, 1, 32), Mathf.Max(0f, s.lights), 0.01f));
            cmd.SetRayTracingVectorParam(_rt, IdVolFalloff, f.falloff);
            cmd.SetRayTracingBufferParam(_rt, IdLights, f.lights);
            cmd.SetRayTracingBufferParam(_rt, IdVolSkip, f.skip);
            cmd.SetRayTracingFloatParam(_rt, IdVolEmissive, Mathf.Max(0f, s.emissive));
            cmd.SetRayTracingFloatParam(_rt, IdVolClamp, Mathf.Max(0f, s.maxSample));
            cmd.DispatchRays(_rt, "VolumeRayGen", (uint)w, (uint)h, 1);

            // 3. temporal accumulation
            int prev = _cur ^ 1;
            cmd.SetComputeTextureParam(_cs, _kTemporal, IdVolIn, _raw);
            cmd.SetComputeTextureParam(_cs, _kTemporal, IdVolDepth, _depth);
            cmd.SetComputeTextureParam(_cs, _kTemporal, IdHistIn, _hist[prev]);
            cmd.SetComputeTextureParam(_cs, _kTemporal, IdHistDepthIn, _histDepth[prev]);
            cmd.SetComputeTextureParam(_cs, _kTemporal, IdHistOut, _hist[_cur]);
            cmd.SetComputeTextureParam(_cs, _kTemporal, IdHistDepthOut, _histDepth[_cur]);
            cmd.SetComputeTextureParam(_cs, _kTemporal, IdMomIn, _mom[prev]);
            cmd.SetComputeTextureParam(_cs, _kTemporal, IdMomOut, _mom[_cur]);
            cmd.SetComputeVectorParam(_cs, IdTemporalParams3, new Vector4(Mathf.Clamp01(s.momentsAlpha), 0, 0, 0));
            cmd.SetComputeMatrixParam(_cs, IdInvViewProj, invVP * LowResToScreen(W, H, w, h, blk));
            cmd.SetComputeMatrixParam(_cs, IdPrevViewProj, ScreenToLowRes(W, H, w, h, blk) * (_historyValid ? _prevVP : vp));
            cmd.SetComputeVectorParam(_cs, IdCamPos, cam.transform.position);
            cmd.SetComputeVectorParam(_cs, IdPrevCamPos, _historyValid ? _prevCamPos : cam.transform.position);
            cmd.SetComputeVectorParam(_cs, IdTemporalParams, new Vector4(Mathf.Clamp01(s.alphaMin), Mathf.Max(1f, s.maxHistory), Mathf.Max(0.5f, s.clipSigma), _historyValid ? 1f : 0f));
            cmd.SetComputeVectorParam(_cs, IdTemporalParams2, new Vector4(Mathf.Max(0.01f, s.depthTolerance), Mathf.Max(0.001f, s.spatialDepthSigma), 0.5f / Mathf.Max(0.1f, s.spatialSigma * s.spatialSigma), s.debugView == 2 ? 1f : 0f));
            cmd.DispatchCompute(_cs, _kTemporal, (w + 7) / 8, (h + 7) / 8, 1);
            Texture result = _hist[_cur];

            // 4. variance-guided a-trous passes (output only: the history keeps the unfiltered accumulation)
            int passes = s.debugView == 2 ? 0 : Mathf.Clamp(s.atrous, 0, 5);
            for (int i = 0; i < passes; i++)
            {
                var dst = _filt[i & 1];
                cmd.SetComputeTextureParam(_cs, _kAtrous, IdAtrousIn, result);
                cmd.SetComputeTextureParam(_cs, _kAtrous, IdAtrousMom, _mom[_cur]);
                cmd.SetComputeTextureParam(_cs, _kAtrous, IdAtrousOut, dst);
                cmd.SetComputeTextureParam(_cs, _kAtrous, IdVolDepth, _depth);
                cmd.SetComputeVectorParam(_cs, IdAtrousParams, new Vector4(1 << i, i == 0 ? 1f : 0f, Mathf.Max(0.1f, s.atrousLumSigma), Mathf.Max(0.01f, s.atrousDepthSigma)));
                cmd.SetComputeVectorParam(_cs, IdAtrousParams2, new Vector4(Mathf.Max(1f, s.prefilterSamples), i == passes - 1 ? 1f : 0f, 0, 0));
                cmd.DispatchCompute(_cs, _kAtrous, (w + 7) / 8, (h + 7) / 8, 1);
                result = dst;
            }
            _cur = prev;
            _prevVP = vp;
            _prevCamPos = cam.transform.position;
            _historyValid = true;

            Result = result;
            ResultSize = new Vector2(w, h);

            // 5. composite into the camera target: the game's fog now (blended, before the game's transparents), the
            // in-scattering after the transparents (late, from a copy of the target; in this buffer when there is none)
            var m = f.composite;
            m.SetTexture(IdSceneColor, _sceneCopy);
            m.SetTexture(IdVolHist, result);
            m.SetTexture(IdVolDepthTex, _depth);
            m.SetBuffer(IdVolExposure, f.exposure != null ? f.exposure : DummyExposure());
            m.SetVector(IdVolScreen, screen);
            m.SetVector(IdVolLowDim, low);
            m.SetVector(IdVolViewRay, new Vector4(viewRay.x, viewRay.y, SkyDistance, skyFrom));
            float fogMode = !RenderSettings.fog ? 0f : RenderSettings.fogMode == FogMode.Linear ? 1f : RenderSettings.fogMode == FogMode.Exponential ? 2f : 3f;
            m.SetVector(IdVolFog, new Vector4(fogMode, RenderSettings.fogStartDistance, RenderSettings.fogEndDistance, RenderSettings.fogDensity));
            var fc = RenderSettings.fogColor;
            m.SetVector(IdVolFogColor, new Vector4(fc.r, fc.g, fc.b, f.applyGameFog && s.gameFog && fogMode > 0f ? 1f : 0f));
            m.SetVector(IdVolComposite, new Vector4(Mathf.Max(0f, s.strength), f.autoExposure && f.exposure != null ? 1f : 0f, 1f, s.debugView));
            if (f.applyGameFog && s.gameFog && fogMode > 0f && s.debugView == 0)
                cmd.DrawProcedural(Matrix4x4.identity, m, 0, MeshTopology.Triangles, 3);
            var late = lateCmd ?? cmd;
            late.CopyTexture(f.target, _sceneCopy);
            late.DrawProcedural(Matrix4x4.identity, m, 1, MeshTopology.Triangles, 3);
        }

        /// <summary>Maps a low-res NDC position to the screen NDC of the pixel it was traced through (i * blk + blk / 2).</summary>
        private static Matrix4x4 LowResToScreen(int W, int H, int w, int h, int blk)
        {
            // low uv u = (i + 0.5) / w  ->  screen px = i * blk + blk/2 + 0.5 = (u * w - 0.5) * blk + floor(blk/2) + 0.5
            // screen ndc = 2 * px / W - 1. Both affine in u (and ndc_low = 2u - 1).
            float sx = (float)w * blk / W, sy = (float)h * blk / H;
            float ox = (2f * (Mathf.Floor(blk * 0.5f) + 0.5f - 0.5f * blk)) / W;
            float oy = (2f * (Mathf.Floor(blk * 0.5f) + 0.5f - 0.5f * blk)) / H;
            // ndc_s = ((ndc_l + 1) / 2 * w * blk + (floor(blk/2) + 0.5 - 0.5 blk)) * 2 / W - 1 = ndc_l * sx + (sx - 1 + ox)
            var m = Matrix4x4.identity;
            m.m00 = sx; m.m03 = sx - 1f + ox;
            m.m11 = sy; m.m13 = sy - 1f + oy;
            return m;
        }

        private static Matrix4x4 ScreenToLowRes(int W, int H, int w, int h, int blk) => LowResToScreen(W, H, w, h, blk).inverse;

        private ComputeBuffer DummyExposure()
        {
            if (_dummyExposure == null)
            {
                _dummyExposure = new ComputeBuffer(1, 16);
                _dummyExposure.SetData(new[] { new Vector4(1, 0, 0, 0) });
            }
            return _dummyExposure;
        }

        private void EnsureTargets(int w, int h, Texture target)
        {
            if (_raw == null || _raw.width != w || _raw.height != h || !_raw.IsCreated())
            {
                ReleaseLowRes();
                _raw = Create(w, h, RenderTextureFormat.ARGBHalf, "UltraRT Volumetrics Raw");
                _depth = Create(w, h, RenderTextureFormat.RFloat, "UltraRT Volumetrics Depth");
                for (int i = 0; i < 2; i++)
                {
                    _hist[i] = Create(w, h, RenderTextureFormat.ARGBHalf, $"UltraRT Volumetrics History {i}");
                    _histDepth[i] = Create(w, h, RenderTextureFormat.RFloat, $"UltraRT Volumetrics History Depth {i}");
                    _mom[i] = Create(w, h, RenderTextureFormat.RGFloat, $"UltraRT Volumetrics Moments {i}");
                    _filt[i] = Create(w, h, RenderTextureFormat.ARGBHalf, $"UltraRT Volumetrics Filtered {i}");
                }
                _cur = 0;
                _historyValid = false;
            }
            var rtTarget = target as RenderTexture;
            if (_sceneCopy == null || _sceneCopy.width != target.width || _sceneCopy.height != target.height ||
                (rtTarget != null && _sceneCopy.format != rtTarget.format))
            {
                if (_sceneCopy != null) { _sceneCopy.Release(); Object.Destroy(_sceneCopy); }
                RenderTextureDescriptor desc = rtTarget != null ? rtTarget.descriptor : new RenderTextureDescriptor(target.width, target.height, RenderTextureFormat.ARGB32, 0);
                desc.depthBufferBits = 0;
                desc.msaaSamples = 1;
                desc.enableRandomWrite = false;
                desc.useMipMap = false;
                desc.autoGenerateMips = false;
                _sceneCopy = new RenderTexture(desc) { name = "UltraRT Volumetrics SceneColor", filterMode = FilterMode.Point, hideFlags = HideFlags.HideAndDontSave };
                _sceneCopy.Create();
            }
        }

        private static RenderTexture Create(int w, int h, RenderTextureFormat format, string name)
        {
            var rt = new RenderTexture(w, h, 0, format, RenderTextureReadWrite.Linear)
            {
                name = name, enableRandomWrite = true, filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp,
                useMipMap = false, autoGenerateMips = false, hideFlags = HideFlags.HideAndDontSave,
            };
            rt.Create();
            return rt;
        }

        private void ReleaseLowRes()
        {
            void Rel(ref RenderTexture t) { if (t != null) { t.Release(); Object.Destroy(t); t = null; } }
            Rel(ref _raw);
            Rel(ref _depth);
            for (int i = 0; i < 2; i++) { Rel(ref _hist[i]); Rel(ref _histDepth[i]); Rel(ref _mom[i]); Rel(ref _filt[i]); }
        }

        public void Dispose()
        {
            ReleaseLowRes();
            if (_sceneCopy != null) { _sceneCopy.Release(); Object.Destroy(_sceneCopy); _sceneCopy = null; }
            _dummyExposure?.Release();
            _dummyExposure = null;
        }
    }
}
