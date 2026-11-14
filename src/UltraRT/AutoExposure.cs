using System;
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
        public float minEV = -4f, maxEV = 0f;
        /// <summary>Adaptation rates in 1/s (exponential, in EV): up = exposure rising (walking into the dark),
        /// down = exposure falling (walking into the light). -ultrart-ae-speed UP[,DOWN].</summary>
        public float speedUp = 1.5f, speedDown = 3f;
        /// <summary>Percentile band of the histogram that is averaged: -ultrart-ae-band LOW,HIGH (fractions).</summary>
        public float lowFraction = 0.4f, highFraction = 0.95f;
        /// <summary>Exposure compensation in EV, added to the target: -ultrart-ae-comp.</summary>
        public float compensation;

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
                    case "-ultrart-ae-min": minEV = F(Next(), minEV); break;
                    case "-ultrart-ae-max": maxEV = F(Next(), maxEV); break;
                    case "-ultrart-ae-comp": compensation = F(Next(), compensation); break;
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
            FormattableString.Invariant($"auto exposure {(enabled ? "on" : "off")}{(ptDn ? " (+pt-dn)" : "")}: key {key}, EV [{minEV}, {maxEV}], speed up {speedUp}/s down {speedDown}/s, band {lowFraction}-{highFraction}, comp {compensation}");
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
        private float _nextReadback;
        private bool _readbackPending;

        /// <summary>Last read-back state (a few frames old): exposure, EV, mean log2 luminance, target EV.</summary>
        public Vector4 Last { get; private set; } = new Vector4(1f, 0f, 0f, 0f);
        /// <summary>Pixels counted last read-back (0 = nothing measurable).</summary>
        public float LastCount { get; private set; }
        public ComputeBuffer ExposureBuffer => _exposure;

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
            IdCompositeExposure = Shader.PropertyToID("_UltraRTExposure"),
            IdCompositeLitWeight = Shader.PropertyToID("_UltraRTLitWeight");

        public AutoExposure(ComputeShader shader)
        {
            _shader = shader ? shader : throw new ArgumentNullException(nameof(shader));
            _kHistogram = shader.FindKernel("Histogram");
            _kAdapt = shader.FindKernel("Adapt");
            _hist = new ComputeBuffer(Bins, 4) { name = "UltraRT Exposure Histogram" };
            _hist.SetData(new uint[Bins]);
            _exposure = new ComputeBuffer(2, 16) { name = "UltraRT Exposure" };
            _exposure.SetData(new[] { new Vector4(1f, 0f, 0f, 0f), Vector4.zero });
        }

        /// <summary>Jump straight to the measured exposure next frame (scene load, teleport, mode switch).</summary>
        public void Reset() => _snap = true;

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
            // Every second pixel in x and y: 16x16 threads cover 32x32 pixels.
            cmd.DispatchCompute(cs, _kHistogram, Mathf.CeilToInt(w / 32f), Mathf.CeilToInt(h / 32f), 1);

            float minEV = Mathf.Min(s.minEV, s.maxEV);
            cmd.SetComputeVectorParam(cs, IdParams0, new Vector4(Mathf.Log(Mathf.Max(s.key, 1e-4f), 2f), minEV, s.maxEV, Mathf.Clamp(dt, 0f, 0.25f)));
            cmd.SetComputeVectorParam(cs, IdParams1, new Vector4(Mathf.Max(0f, s.speedUp), Mathf.Max(0f, s.speedDown),
                Mathf.Clamp01(s.lowFraction), Mathf.Clamp(s.highFraction, Mathf.Clamp01(s.lowFraction) + 0.01f, 1f)));
            // A measurement needs at least 2% of the sampled pixels (the rest is sky or fullbright).
            cmd.SetComputeVectorParam(cs, IdParams2, new Vector4(_snap ? 1f : 0f, Mathf.Max(16f, w * h / 4f * 0.02f), s.compensation, 0));
            cmd.SetComputeBufferParam(cs, _kAdapt, IdHist, _hist);
            cmd.SetComputeBufferParam(cs, _kAdapt, IdExposure, _exposure);
            cmd.DispatchCompute(cs, _kAdapt, 1, 1, 1);
            _snap = false;

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
