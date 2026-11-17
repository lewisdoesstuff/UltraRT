using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using UnityEngine.Rendering;

namespace UltraRT
{
    /// <summary>Relight auto exposure knobs (docs/relight.md, "Auto exposure"). Read every frame.</summary>
    public sealed class AutoExposureSettings
    {
        /// <summary>-ultrart-relight-autoexposure on|off (config [Relight] AutoExposure). Ctrl+F4 toggles in game.</summary>
        public bool enabled = true;
        /// <summary>Also expose pt-dn (the game's light model): -ultrart-ae-ptdn.</summary>
        public bool ptDn;
        /// <summary>Mean scene luminance (of the exposed part) that maps to itself: -ultrart-ae-key.</summary>
        public float key = 0.45f;
        /// <summary>Exposure clamp in EV relative to the fixed relight look (0 = no change): -ultrart-ae-min / -ultrart-ae-max.
        /// max 0 means auto exposure only ever darkens, so dim rooms keep exactly the old look.</summary>
        public float minEV = -4f, maxEV = 1f;
        /// <summary>Adaptation rates in 1/s (exponential, in EV): up = exposure rising (walking into the dark),
        /// down = exposure falling (walking into the light). -ultrart-ae-speed UP[,DOWN].</summary>
        public float speedUp = 1.5f, speedDown = 3f;
        /// <summary>Percentile band of the histogram that is averaged: -ultrart-ae-band LOW,HIGH (fractions).</summary>
        public float lowFraction = 0.4f, highFraction = 0.95f;
        /// <summary>Exposure compensation in EV, added to the target: -ultrart-ae-comp.</summary>
        public float compensation;
        /// <summary>Dark key: scenes whose mean luminance is between this and the key are left alone; darker ones are
        /// brightened towards it (by upStrength), so a dark room stays dark but readable. -ultrart-ae-key-low K.</summary>
        public float keyLow = 0.1f;
        /// <summary>Fraction of the brightening towards the dark key a darker scene gets (1 = all the way, 0 = never
        /// brightens). Darkening above the key is always full. -ultrart-ae-up F.</summary>
        public float upStrength = 0.5f;
        /// <summary>Dead band in EV: the exposure stops this far short of the target, so a target that wanders by less
        /// than this (noise, flickering torches) never moves it. -ultrart-ae-deadband EV.</summary>
        public float deadBand = 0.05f;
        /// <summary>Smoothing of the metered luminance before the target is derived, 1/s (0 = none). -ultrart-ae-meter F.</summary>
        public float meterSpeed = 4f;
        /// <summary>After a reset (scene load, mode switch): adapt at settleSpeed (1/s, no dead band) for settleTime seconds,
        /// so the exposure follows the denoiser and radiance cache while they converge. -ultrart-ae-settle SECONDS[,SPEED].</summary>
        public float settleTime = 0.5f, settleSpeed = 8f;
        /// <summary>
        /// Game match (docs/relight.md, "Game match"): the game's own frame (opaques and sky, before the RT composite) is
        /// metered at the same pixels as the RT image, near ones only (game fog under <see cref="gameMatchFog"/>), and the
        /// target moves this fraction of the way towards the game's brightness, beyond a dead band of
        /// <see cref="gameMatchBand"/> EV. Up = the RT image is darker than the game (8-2's alley, lit in game by lights
        /// shining through walls), down = brighter (torch rooms, 4-1). 0 = off. -ultrart-ae-match UP[,DOWN].
        /// </summary>
        public float gameMatchUp = 0.75f, gameMatchDown = 0.4f;
        public float gameMatchBand = 0.35f;
        /// <summary>Pixels whose game fog factor is above this are not compared (the game's fog replaces their colour).</summary>
        public float gameMatchFog = 0.3f;
        /// <summary>
        /// Colour match (docs/relight.md, "Area colour"): the same near pixels' mean colour, game against RT, each
        /// normalised to its luminance. Per channel, beyond <see cref="colorMatchBand"/> (log2), this share of the
        /// difference becomes a gain on the lit part (clamped to +-<see cref="colorMatchMax"/> log2, channel average kept), so
        /// relight keeps an area's tint (2-2's purple, 6-1's and 7-3's red) where its own light model would wash it out.
        /// 0 = off. -ultrart-ae-color STRENGTH[,BAND[,MAX]].
        /// </summary>
        public float colorMatch = 1f, colorMatchBand = 0.2f, colorMatchMax = 2f;

        public void ParseArgs()
        {
            var a = Environment.GetCommandLineArgs();
            for (int i = 0; i < a.Length; i++)
            {
                string Next() => i + 1 < a.Length ? a[++i] : null;
                float F(string s, float fallback) => float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;
                switch (a[i].ToLowerInvariant())
                {
                    case "-ultrart-relight-autoexposure":
                    {
                        var v = (Next() ?? "on").ToLowerInvariant();
                        enabled = v != "off" && v != "0" && v != "false";
                        break;
                    }
                    case "-ultrart-ae-ptdn": ptDn = true; break;
                    case "-ultrart-ae-key": key = F(Next(), key); break;
                    case "-ultrart-ae-match":
                    {
                        var parts = (Next() ?? "").Split(',');
                        gameMatchUp = F(parts[0], gameMatchUp);
                        gameMatchDown = parts.Length > 1 ? F(parts[1], gameMatchDown) : gameMatchUp;
                        break;
                    }
                    case "-ultrart-ae-color":
                    {
                        var p = (Next() ?? "").Split(',');
                        colorMatch = F(p[0], colorMatch);
                        if (p.Length > 1) colorMatchBand = F(p[1], colorMatchBand);
                        if (p.Length > 2) colorMatchMax = F(p[2], colorMatchMax);
                        break;
                    }
                    case "-ultrart-ae-min": minEV = F(Next(), minEV); break;
                    case "-ultrart-ae-max": maxEV = F(Next(), maxEV); break;
                    case "-ultrart-ae-comp": compensation = F(Next(), compensation); break;
                    case "-ultrart-ae-up": upStrength = F(Next(), upStrength); break;
                    case "-ultrart-ae-key-low": keyLow = F(Next(), keyLow); break;
                    case "-ultrart-ae-deadband": deadBand = F(Next(), deadBand); break;
                    case "-ultrart-ae-meter": meterSpeed = F(Next(), meterSpeed); break;
                    case "-ultrart-ae-settle":
                    {
                        var p = (Next() ?? "").Split(',');
                        settleTime = F(p[0], settleTime);
                        if (p.Length > 1) settleSpeed = F(p[1], settleSpeed);
                        break;
                    }
                    case "-ultrart-ae-speed":
                    {
                        var p = (Next() ?? "").Split(',');
                        speedUp = F(p[0], speedUp);
                        speedDown = p.Length > 1 ? F(p[1], speedDown) : speedUp * 2f;
                        break;
                    }
                    case "-ultrart-ae-band":
                    {
                        var p = (Next() ?? "").Split(',');
                        lowFraction = F(p[0], lowFraction);
                        if (p.Length > 1) highFraction = F(p[1], highFraction);
                        break;
                    }
                }
            }
        }

        public override string ToString() =>
            FormattableString.Invariant($"auto exposure {(enabled ? "on" : "off")}{(ptDn ? " (+pt-dn)" : "")}: key {key} (dark key {keyLow}), EV [{minEV}, {maxEV}], speed up {speedUp}/s down {speedDown}/s, band {lowFraction}-{highFraction}, comp {compensation}, up {upStrength}, dead band {deadBand} EV, meter {meterSpeed}/s, settle {settleTime}s@{settleSpeed}/s, game match {gameMatchUp}/{gameMatchDown}, colour match {colorMatch} (band {colorMatchBand}, max {colorMatchMax})");
    }

    /// <summary>
    /// Histogram-based auto exposure on the GPU (Exposure.compute). Runs after the denoiser; its result is read by the
    /// denoiser's Composite on the next frame, so nothing waits on the CPU. A small async readback feeds the log/bench only.
    /// </summary>
    internal sealed class AutoExposure : IDisposable
    {
        public const int Bins = 64;
        private const float MinLog2 = -12f, MaxLog2 = 4f; // luminance 2^-12 .. 16

        private readonly ComputeShader _shader;
        private readonly int _kHistogram, _kAdapt;
        private readonly ComputeBuffer _hist, _exposure;
        private bool _snap = true;
        private float _settleLeft = -1f; // < 0: start a settle period on the next Execute
        private float _nextReadback;
        private bool _readbackPending;

        /// <summary>Last read-back state (a few frames old): exposure, EV, mean log2 luminance, target EV.</summary>
        public Vector4 Last { get; private set; } = new Vector4(1f, 0f, 0f, 0f);
        /// <summary>Pixels counted last read-back (0 = nothing measurable).</summary>
        public float LastCount { get; private set; }
        public ComputeBuffer ExposureBuffer => _exposure;
        /// <summary>Volumetric haze metered with the image (last frame's low-res in-scattering; null = none) and its params
        /// (x, y low-res size, z scale, w 1 = on). Set by the volumetrics pass every frame (Plugin.Volumetrics.cs).</summary>
        public Texture Haze;
        public Vector4 HazeParams;
        /// <summary>Game match: a copy of the game's frame before the RT composite (null = no match this frame), the map
        /// from trace uv to its uv (xy scale around the centre = the guard band, z 1 = flip y) and the game's fog
        /// (x 1 = on, y mode 1 linear / 2 exp / 3 exp2, z start or density, w end). Set by the plugin every frame.</summary>
        public Texture GameColor;
        public Vector4 GameMap, GameFog;
        /// <summary>Last read-back game match state: game mean log2, RT mean log2 (same pixels), correction EV, pixels.</summary>
        public Vector4 LastMatch { get; private set; }
        /// <summary>Last read-back colour match gain (rgb; w 1 = valid).</summary>
        public Vector4 LastBalance { get; private set; }

        private static readonly int
            IdFiltered = Shader.PropertyToID("_Filtered"),
            IdAlbedo = Shader.PropertyToID("_Albedo"),
            IdSpecular = Shader.PropertyToID("_Specular"),
            IdHasSpecular = Shader.PropertyToID("_HasSpecular"),
            IdNormalDepth = Shader.PropertyToID("_NormalDepth"),
            IdLitWeight = Shader.PropertyToID("_LitWeight"),
            IdDim = Shader.PropertyToID("_Dim"),
            IdRange = Shader.PropertyToID("_Range"),
            IdHist = Shader.PropertyToID("_Hist"),
            IdExposure = Shader.PropertyToID("_Exposure"),
            IdParams0 = Shader.PropertyToID("_Params0"),
            IdParams1 = Shader.PropertyToID("_Params1"),
            IdParams2 = Shader.PropertyToID("_Params2"),
            IdParams3 = Shader.PropertyToID("_Params3"),
            IdParams4 = Shader.PropertyToID("_Params4"),
            IdVolHaze = Shader.PropertyToID("_VolHaze"),
            IdVolHazeParams = Shader.PropertyToID("_VolHazeParams"),
            IdGameColor = Shader.PropertyToID("_GameColor"),
            IdGameMap = Shader.PropertyToID("_GameMap"),
            IdGameFog = Shader.PropertyToID("_GameFog"),
            IdParams5 = Shader.PropertyToID("_Params5"),
            IdParams6 = Shader.PropertyToID("_Params6"),
            IdCompositeExposure = Shader.PropertyToID("_UltraRTExposure"),
            IdCompositeLitWeight = Shader.PropertyToID("_UltraRTLitWeight");

        public AutoExposure(ComputeShader shader)
        {
            _shader = shader ? shader : throw new ArgumentNullException(nameof(shader));
            _kHistogram = shader.FindKernel("Histogram");
            _kAdapt = shader.FindKernel("Adapt");
            // Three histograms: the whole exposed image (the meter), and for the game match the near RT pixels and the
            // game's own colour at the same pixels.
            // + 6 colour-match sums + 6 light-fit sums (linear RT and game colour; docs/lighting-overrides.md, "Fit").
            _hist = new ComputeBuffer(3 * Bins + 12, 4) { name = "UltraRT Exposure Histogram" };
            _hist.SetData(new uint[3 * Bins + 12]);
            _exposure = new ComputeBuffer(8, 16) { name = "UltraRT Exposure" };
            _exposure.SetData(new[] { new Vector4(1f, 0f, 0f, 0f), Vector4.zero, Vector4.zero, Vector4.zero, Vector4.zero, Vector4.zero, Vector4.zero, Vector4.zero });
        }

        /// <summary>Jump straight to the measured exposure next frame (scene load, teleport, mode switch).</summary>
        public void Reset() { _snap = true; _settleLeft = -1f; }

        /// <summary>Bench: when non-null, every frame's state is read back and appended here (exposure state, info).</summary>
        public List<(Vector4 state, Vector4 info)> Trace;

        /// <summary>Light fit (Plugin.LightFit.cs): when non-null, the linear mean colour of RT and game at the near pixels
        /// is measured and every frame's pair is read back and appended here (rgb, w = near pixels).</summary>
        public List<Vector4[]> FitTrace; // per frame: linear RT, linear game, display RT, display game (rgb, w = near pixels)

        /// <summary>
        /// Binds the exposure buffer and the lit-weight texture on the denoiser's Composite kernel. Needed every frame the
        /// denoiser runs (any mode: the kernel declares them); <c>_UltraRTAutoExposure</c> decides whether they are used.
        /// </summary>
        public static void BindComposite(CommandBuffer cmd, ComputeShader denoise, int compositeKernel, ComputeBuffer exposure, Texture litWeight)
        {
            cmd.SetComputeBufferParam(denoise, compositeKernel, IdCompositeExposure, exposure);
            cmd.SetComputeTextureParam(denoise, compositeKernel, IdCompositeLitWeight, litWeight);
        }

        /// <summary>Measures this frame (after the denoiser) and adapts the exposure used from the next frame on.</summary>
        public void Execute(CommandBuffer cmd, AutoExposureSettings s, Texture filtered, Texture albedo, Texture specular,
                            Texture normalDepth, Texture litWeight, int w, int h, float dt)
        {
            var cs = _shader;
            cmd.SetComputeVectorParam(cs, IdDim, new Vector4(w, h, 0, 0));
            cmd.SetComputeVectorParam(cs, IdRange, new Vector4(MinLog2, Bins / (MaxLog2 - MinLog2), 0, 0));
            cmd.SetComputeTextureParam(cs, _kHistogram, IdFiltered, filtered);
            cmd.SetComputeTextureParam(cs, _kHistogram, IdAlbedo, albedo);
            cmd.SetComputeTextureParam(cs, _kHistogram, IdSpecular, specular != null ? specular : Texture2D.blackTexture);
            cmd.SetComputeIntParam(cs, IdHasSpecular, specular != null ? 1 : 0);
            cmd.SetComputeTextureParam(cs, _kHistogram, IdNormalDepth, normalDepth);
            cmd.SetComputeTextureParam(cs, _kHistogram, IdLitWeight, litWeight);
            cmd.SetComputeBufferParam(cs, _kHistogram, IdHist, _hist);
            bool haze = Haze != null && HazeParams.w > 0.5f;
            cmd.SetComputeTextureParam(cs, _kHistogram, IdVolHaze, haze ? Haze : Texture2D.blackTexture);
            cmd.SetComputeVectorParam(cs, IdVolHazeParams, haze ? HazeParams : Vector4.zero);
            cmd.SetComputeBufferParam(cs, _kHistogram, IdExposure, _exposure);
            bool match = GameColor != null && (s.gameMatchUp > 0f || s.gameMatchDown > 0f || s.colorMatch > 0f);
            cmd.SetComputeTextureParam(cs, _kHistogram, IdGameColor, match ? GameColor : Texture2D.blackTexture);
            cmd.SetComputeVectorParam(cs, IdGameMap, match ? GameMap : Vector4.zero);
            cmd.SetComputeVectorParam(cs, IdGameFog, GameFog);
            cmd.SetComputeVectorParam(cs, IdParams5, new Vector4(match ? Mathf.Max(0f, s.gameMatchUp) : 0f, match ? Mathf.Max(0f, s.gameMatchDown) : 0f,
                Mathf.Max(0f, s.gameMatchBand), Mathf.Clamp01(s.gameMatchFog)));
            cmd.SetComputeVectorParam(cs, IdParams6, new Vector4(match ? Mathf.Max(0f, s.colorMatch) : 0f, Mathf.Max(0f, s.colorMatchBand), Mathf.Max(0f, s.colorMatchMax), FitTrace != null ? 1f : 0f));
            // Every second pixel in x and y: 16x16 threads cover 32x32 pixels.
            cmd.DispatchCompute(cs, _kHistogram, Mathf.CeilToInt(w / 32f), Mathf.CeilToInt(h / 32f), 1);

            float minEV = Mathf.Min(s.minEV, s.maxEV);
            cmd.SetComputeVectorParam(cs, IdParams0, new Vector4(Mathf.Log(Mathf.Max(s.key, 1e-4f), 2f), minEV, s.maxEV, Mathf.Clamp(dt, 0f, 0.25f)));
            cmd.SetComputeVectorParam(cs, IdParams1, new Vector4(Mathf.Max(0f, s.speedUp), Mathf.Max(0f, s.speedDown),
                Mathf.Clamp01(s.lowFraction), Mathf.Clamp(s.highFraction, Mathf.Clamp01(s.lowFraction) + 0.01f, 1f)));
            // A measurement needs at least 2% of the sampled pixels (the rest is sky or fullbright).
            cmd.SetComputeVectorParam(cs, IdParams2, new Vector4(_snap ? 1f : 0f, Mathf.Max(16f, w * h / 4f * 0.02f), s.compensation, 0));
            if (_settleLeft < 0f) _settleLeft = s.settleTime; // first frame after a reset
            bool settling = _settleLeft > 0f;
            _settleLeft = Mathf.Max(0f, _settleLeft - dt);
            cmd.SetComputeVectorParam(cs, IdParams3, new Vector4(Mathf.Clamp01(s.upStrength), Mathf.Max(0f, s.deadBand),
                Mathf.Max(0f, s.meterSpeed), settling ? Mathf.Max(0f, s.settleSpeed) : 0f));
            cmd.SetComputeVectorParam(cs, IdParams4, new Vector4(Mathf.Log(Mathf.Clamp(s.keyLow, 1e-4f, Mathf.Max(s.key, 1e-4f)), 2f), 0, 0, 0));
            cmd.SetComputeVectorParam(cs, IdParams5, new Vector4(match ? Mathf.Max(0f, s.gameMatchUp) : 0f, match ? Mathf.Max(0f, s.gameMatchDown) : 0f,
                Mathf.Max(0f, s.gameMatchBand), Mathf.Clamp01(s.gameMatchFog)));
            cmd.SetComputeVectorParam(cs, IdParams6, new Vector4(match ? Mathf.Max(0f, s.colorMatch) : 0f, Mathf.Max(0f, s.colorMatchBand), Mathf.Max(0f, s.colorMatchMax), FitTrace != null ? 1f : 0f));
            cmd.SetComputeBufferParam(cs, _kAdapt, IdHist, _hist);
            cmd.SetComputeBufferParam(cs, _kAdapt, IdExposure, _exposure);
            cmd.DispatchCompute(cs, _kAdapt, 1, 1, 1);
            _snap = false;

            if (Trace != null)
            {
                var trace = Trace;
                cmd.RequestAsyncReadback(_exposure, r =>
                {
                    if (r.hasError) return;
                    var d = r.GetData<Vector4>();
                    trace.Add((d[0], d[1]));
                });
            }
            if (FitTrace != null)
            {
                var fit = FitTrace;
                cmd.RequestAsyncReadback(_exposure, r =>
                {
                    if (r.hasError) return;
                    var d = r.GetData<Vector4>();
                    if (d.Length > 7 && d[4].w > 0f) fit.Add(new[] { d[4], d[5], d[6], d[7] });
                });
            }
            if (!_readbackPending && Time.realtimeSinceStartup >= _nextReadback)
            {
                _readbackPending = true;
                _nextReadback = Time.realtimeSinceStartup + 0.25f;
                cmd.RequestAsyncReadback(_exposure, r =>
                {
                    _readbackPending = false;
                    if (r.hasError) return;
                    var d = r.GetData<Vector4>();
                    Last = d[0];
                    LastCount = d[1].x;
                    if (d.Length > 2) LastMatch = d[2];
                    if (d.Length > 3) LastBalance = d[3];
                });
            }
        }

        public void Dispose()
        {
            _hist?.Release();
            _exposure?.Release();
        }
    }
}
