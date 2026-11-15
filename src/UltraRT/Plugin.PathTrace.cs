using System;
using System.Globalization;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace UltraRT
{
    public enum PathTraceDenoiser
    {
        /// <summary>DLSS Ray Reconstruction when it is available (nvngx_dlssd.dll, an RTX GPU), else UltraRT's denoiser.</summary>
        Auto = 0,
        /// <summary>UltraRT's denoiser, reflection filter and temporal upsampler.</summary>
        UltraRT = 1,
        /// <summary>DLSS Ray Reconstruction; falls back to UltraRT's when it can't run.</summary>
        RayReconstruction = 2,
    }

    /// <summary>The <c>pathtrace</c> view mode (docs/path-tracing.md). Read every frame; bench prefix <c>ptx.</c>.</summary>
    public sealed class PathTraceSettings
    {
        /// <summary>Diffuse (and, on glossy pixels, specular) paths per pixel, 1-4.</summary>
        public int paths = 1;
        /// <summary>Bounce rays per path, 1-4. Every vertex but the last takes one light sample and one sky sample.</summary>
        public int bounces = 2;
        /// <summary>The last vertex reads the radiance cache (direct + all further bounces) instead of one light sample.</summary>
        public bool cacheTail = true;
        /// <summary>Russian roulette: paths whose throughput (largest channel) is below this survive in proportion. 0 = off.</summary>
        public float roulette = 0.2f;
        /// <summary>Firefly clamp on one diffuse path's radiance (luminance, lighting units). 0 = off.</summary>
        public float maxRadiance = 12f;
        /// <summary>Path vertices follow the surface's specular lobe (metal, mirrors, REFLECTION materials) by its weight.</summary>
        public bool specularLobes = true;
        /// <summary>Path vertices past the primary hit take a direct sky sample (one more ray each, outdoors). Off: their
        /// cosine bounce finds the sky by itself, at more variance in the deeper bounces only.</summary>
        public bool skyAtVertices = false;
        /// <summary>Path rays see water, glass and liquid blood: a Fresnel mirror reflection or a tinted pass-through. Off: they
        /// pass through untouched, as every ray but the overlay's does in relight.</summary>
        public bool transparents = true;
        /// <summary>How much of a transparent surface's colour tints the light that passes through it (blood: in full, water and
        /// glass: 0.6 of this).</summary>
        public float transparentTint = 0.8f;
        public PathTraceDenoiser denoiser = PathTraceDenoiser.Auto;
        /// <summary>Trace scale (per axis) when DLSS RR denoises and upscales: 0.3333 = 853x480 at 1440p.</summary>
        public float scaleRR = 0.3333f;
        /// <summary>Trace scale with UltraRT's denoiser. 1/3 with <see cref="remodulate"/> keeps the texels (the exact 3x3 grid, as
        /// RR); without remodulation use 0.5, where the upsampler's own jitter is exact.</summary>
        public float scale = 0.3333f;
        /// <summary>Paths per pixel with UltraRT's denoiser (0 = <see cref="paths"/>): at scale 0.5 it traces 2.25x the pixels.</summary>
        public int pathsOwn = 1;
        /// <summary>UltraRT's denoiser remodulates like RR: the upsampler accumulates lighting only, the texels come back from exact
        /// hits at screen resolution (Plugin.Remod.cs). Needed below trace scale 0.5, where the upsampler blurs textures.</summary>
        public bool remodulate = true;
        /// <summary>With <see cref="remodulate"/> at trace scale 1/3 (or 1/5): the exact N x N jitter grid, as RR uses.</summary>
        public bool remodExactGrid = true;
        /// <summary>The lighting upsampler's sample weight exp(-x d^2), d in screen px, and its history cap (exact hits). Masked
        /// burst sigma on 0-1 metal at 1/3: 1.90 with 0.5 / 4, 1.38 with 4 / 3 (the plain upsampler's), 1.20 with 1 / 6.</summary>
        public float remodSharpness = 1f;
        public float remodMaxWeight = 6f;
        /// <summary>Light sampling at the primary hit while RR denoises: -1 = as configured (F6), 1 = RIS without reuse,
        /// 2 = ReSTIR. RR wants uncorrelated samples (docs/dlss.md).</summary>
        public int lightsRR = 1;
        /// <summary>RIS candidates at the primary hit while RR denoises and <see cref="lightsRR"/> is RIS.</summary>
        public int candidatesRR = 16;
        /// <summary>Light samples (one shadow ray each) at the primary hit while RR denoises with RIS, 1-4.</summary>
        public int directSamplesRR = 1;
        /// <summary>Compile RayGenPT's ray tracing pipeline on the session's first trace (a 1x1 dispatch, usually at the main
        /// menu): its first use after a new shader bundle took 16-33 s (the driver's shader cache misses; about 0.2 s on a hit),
        /// which froze the first switch to pathtrace. Flag -ultrart-pt-noprewarm.</summary>
        public bool prewarm = true;
    }

    public partial class Plugin
    {
        private const string PathTraceModeName = "pathtrace";
        private readonly PathTraceSettings _ptSettings = new PathTraceSettings();
        private bool _ptLoggedDenoiser;

        /// <summary>The mode with relight's light model and look: relight itself and the path tracing mode.</summary>
        private static bool RelightLook(string mode) => mode == "relight" || mode == PathTraceModeName;

        private bool PathTraceActive => Modes[_mode].name == PathTraceModeName;

        private void ParsePathTraceArgs()
        {
            var s = _ptSettings;
            var a = Environment.GetCommandLineArgs();
            for (int i = 0; i < a.Length; i++)
            {
                string Next() => i + 1 < a.Length ? a[++i] : null;
                float F(float fallback) => float.TryParse(Next(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;
                int I(int fallback) => int.TryParse(Next(), out var v) ? v : fallback;
                switch (a[i].ToLowerInvariant())
                {
                    case "-ultrart-pt-paths": s.paths = I(s.paths); break;
                    case "-ultrart-pt-bounces": s.bounces = I(s.bounces); break;
                    case "-ultrart-pt-nocache": s.cacheTail = false; break;
                    case "-ultrart-pt-noprewarm": s.prewarm = false; break;
                    case "-ultrart-pt-roulette": s.roulette = F(s.roulette); break;
                    case "-ultrart-pt-clamp": s.maxRadiance = F(s.maxRadiance); break;
                    case "-ultrart-pt-scale": s.scaleRR = s.scale = F(s.scale); break;
                    case "-ultrart-pt-denoiser":
                        switch ((Next() ?? "").ToLowerInvariant())
                        {
                            case "rr": case "dlss": s.denoiser = PathTraceDenoiser.RayReconstruction; break;
                            case "ultrart": case "own": case "svgf": s.denoiser = PathTraceDenoiser.UltraRT; break;
                            default: s.denoiser = PathTraceDenoiser.Auto; break;
                        }
                        break;
                }
            }
        }

        /// <summary>Whether the path tracing mode wants RR this frame (Plugin.Dlss.cs decides whether it can run).</summary>
        private bool PathTraceWantsRR => _ptSettings.denoiser != PathTraceDenoiser.UltraRT;

        /// <summary>Trace scale of this frame: the mode's own in pathtrace (by denoiser), else the global one.</summary>
        private float TraceScaleFor(bool full, bool rr) =>
            full ? Mathf.Clamp(rr ? _ptSettings.scaleRR : _ptSettings.scale, 0.2f, 1f) : _scale;

        // Light sampling as the path tracing mode wants it under RR; restored after the binds (the settings are shared).
        private LightSamplingMode _ptSavedLights;
        private int _ptSavedCandidates, _ptSavedRisCandidates;
        private bool _ptLightsOverridden;

        private void PathTraceLightsBegin(bool full, bool rr)
        {
            _ptLightsOverridden = full && rr && _ptSettings.lightsRR >= 0;
            if (!_ptLightsOverridden) return;
            var ls = _lightSamplingSettings;
            _ptSavedLights = ls.mode;
            _ptSavedCandidates = ls.candidates;
            _ptSavedRisCandidates = ls.risCandidates;
            ls.mode = (LightSamplingMode)Mathf.Clamp(_ptSettings.lightsRR, 0, 2);
            if (ls.mode == LightSamplingMode.Ris) ls.candidates = ls.risCandidates = Mathf.Clamp(_ptSettings.candidatesRR, 1, 64);
        }

        private void PathTraceLightsEnd()
        {
            if (!_ptLightsOverridden) return;
            _lightSamplingSettings.mode = _ptSavedLights;
            _lightSamplingSettings.candidates = _ptSavedCandidates;
            _lightSamplingSettings.risCandidates = _ptSavedRisCandidates;
            _ptLightsOverridden = false;
        }

        /// <summary>PathTrace bind block: the parameters RayGenPT reads (harmless for RayGen).</summary>
        private void BindPathTrace(RayTracingShader shader, bool full, bool rr)
        {
            var s = _ptSettings;
            int paths = !rr && s.pathsOwn > 0 ? s.pathsOwn : s.paths;
            _cmd.SetRayTracingVectorParam(shader, "_PtParams", new Vector4(Mathf.Clamp(paths, 1, 4), Mathf.Clamp(s.bounces, 1, 4),
                s.cacheTail && _radianceCache != null && _radianceCache.Active ? 1f : 0f, Mathf.Max(0f, s.roulette)));
            _cmd.SetRayTracingVectorParam(shader, "_PtParams2", new Vector4(Mathf.Max(0f, s.maxRadiance), s.specularLobes ? 1f : 0f, s.skyAtVertices ? 1f : 0f,
                rr ? Mathf.Clamp(s.directSamplesRR, 1, 4) : 1f));
            bool trans = s.transparents && SceneProxies.Transparents.enabled && _proxies != null && _proxies.ActiveTransparent > 0;
            _cmd.SetRayTracingVectorParam(shader, "_PtParams3", new Vector4(trans ? 1f : 0f, Mathf.Clamp01(s.transparentTint), 0f, 0f));
            if (full && !_ptLoggedDenoiser)
            {
                _ptLoggedDenoiser = true;
                Logger.LogInfo($"Path tracing: {paths} path(s) x {s.bounces} bounce(s), cache tail {(s.cacheTail ? "on" : "off")}, " +
                               $"{(rr ? $"DLSS RR at scale {s.scaleRR:F3}" : $"UltraRT denoiser at scale {s.scale:F3}")}");
            }
        }
    }
}
