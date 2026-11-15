using UnityEngine;

namespace UltraRT
{
    /// <summary>
    /// Remodulation for UltraRT's own denoiser in the pathtrace mode (docs/path-tracing.md, "UltraRT's denoiser at 1/3
    /// scale"): what DLSS RR's path does (docs/dlss.md, "Remodulation"), for the fallback. The denoiser's composite is divided
    /// by each surface's demodulation colour D (what it shows under white light), the temporal upsampler accumulates that
    /// lighting-only image, a second upsampler accumulates the noise-free D from exact texel hits, and RemodFinish
    /// multiplies them at screen resolution and applies the tone curve. With the exact 3x3 jitter grid at trace scale 1/3,
    /// textures converge to the game's texel grid instead of the temporal upsampler's blur at that scale.
    /// </summary>
    public partial class Plugin
    {
        private RenderTexture _remodDemod, _remodX, _remodFinal;
        private TemporalUpsampler _remodUpX, _remodUpD;
        private int _kRemodDemod = -1, _kRemodFinish = -1;
        private ComputeShader _remodShaderBound;
        private uint _remodJitterIndex;
        private bool _remodLogged;

        /// <summary>Whether this frame's pathtrace fallback (UltraRT's denoiser) remodulates.</summary>
        private bool RemodFor(bool full, bool rr, bool taau) =>
            full && !rr && taau && _ptSettings.remodulate && _rrShader != null && _rrUpsampleShader != null && EnsureRemodKernels();

        private bool EnsureRemodKernels()
        {
            if (_remodShaderBound == _rrShader && _kRemodDemod >= 0) return _kRemodFinish >= 0;
            _remodShaderBound = _rrShader;
            _kRemodDemod = _rrShader.HasKernel("Demod") ? _rrShader.FindKernel("Demod") : -1;
            _kRemodFinish = _rrShader.HasKernel("RemodFinish") ? _rrShader.FindKernel("RemodFinish") : -1;
            _remodUpX?.Dispose(); _remodUpD?.Dispose();
            _remodUpX = _remodUpD = null;
            if (_kRemodDemod < 0 || _kRemodFinish < 0) return false;
            _remodUpX = new TemporalUpsampler(_rrUpsampleShader) { Unclamped = true, Settings = new UpsamplerSettings() };
            _remodUpD = new TemporalUpsampler(_rrUpsampleShader) { Unclamped = true, ExactReplace = 0.08f, Settings = new UpsamplerSettings() };
            return true;
        }

        /// <summary>The exact jitter grid N for the fallback at this trace scale (3 at 1/3, 5 at 1/5; 0 = the upsampler's own jitter).</summary>
        private int RemodGrid(float scale)
        {
            if (!_ptSettings.remodExactGrid) return 0;
            float up = 1f / Mathf.Max(0.05f, scale);
            int n = Mathf.RoundToInt(up);
            return (n == 3 || n == 5) && Mathf.Abs(up - n) < 0.02f ? n : 0;
        }

        /// <summary>Before the denoiser: D at trace resolution; the composite then also writes composite / D.</summary>
        private void RemodPrepare(int w, int h, Matrix4x4 invViewProj)
        {
            EnsureGBuffer(ref _remodDemod, w, h, RenderTextureFormat.ARGBHalf, "UltraRT Remod Demod");
            EnsureGBuffer(ref _remodX, w, h, RenderTextureFormat.ARGBHalf, "UltraRT Remod Lighting");
            var cs = _rrShader;
            int k = _kRemodDemod;
            _cmd.SetComputeVectorParam(cs, "_Dim", new Vector4(w, h, 1f / w, 1f / h));
            _cmd.SetComputeVectorParam(cs, "_CamPos", _camera.transform.position);
            _cmd.SetComputeMatrixParam(cs, "_InvViewProj", invViewProj);
            _cmd.SetComputeVectorParam(cs, "_ReflParams", _reflSettings.ShaderParams);
            _cmd.SetComputeTextureParam(cs, k, "_Albedo", _gAlbedo);
            _cmd.SetComputeTextureParam(cs, k, "_Emission", _gEmission);
            _cmd.SetComputeTextureParam(cs, k, "_Specular", _gSpecular);
            _cmd.SetComputeTextureParam(cs, k, "_NormalDepth", _gNormalDepth);
            _cmd.SetComputeTextureParam(cs, k, "_Surface", _gSurface);
            _cmd.SetComputeTextureParam(cs, k, "_DemodOut", _remodDemod);
            _cmd.DispatchCompute(cs, k, Mathf.CeilToInt(w / 8f), Mathf.CeilToInt(h / 8f), 1);
            _denoiser.RemodDemod = _remodDemod;
            _denoiser.RemodTarget = _remodX;
            if (!_remodLogged)
            {
                _remodLogged = true;
                Logger.LogInfo($"Path tracing: UltraRT denoiser with remodulation at {w}x{h} (exact grid {_remodGridNow})");
            }
        }

        private int _remodGridNow;

        /// <summary>After the denoiser: both upsamplers and the finish pass. Returns the screen-resolution image (rgb, a = weight).</summary>
        private RenderTexture RemodUpsample(Vector2Int guardPx, bool reset)
        {
            var depth = _depthCopy;
            int Ws = depth.width, Hs = depth.height;
            int tw = _remodX.width, th = _remodX.height;
            int n = _remodGridNow;
            Vector4? traceMap = null;
            if (n > 2)
            {
                // The trace was sized for the exact grid (ExactGridTrace): one trace pixel = n screen pixels, centred on the screen.
                int rw = tw - 2 * guardPx.x, rh = th - 2 * guardPx.y;
                var off = new Vector2((rw * n - Ws) / 2, (rh * n - Hs) / 2);
                traceMap = new Vector4(1f / n, 1f / n, guardPx.x + off.x / n, guardPx.y + off.y / n);
            }
            var s = _ptSettings;
            var ds = _remodUpD.Settings;
            ds.sharpness = 30f; ds.maxWeight = 4f; ds.spatialWeight = 1e-3f;
            var xs = _remodUpX.Settings;
            xs.sharpness = Mathf.Max(0.05f, s.remodSharpness);
            xs.maxWeight = Mathf.Max(0.5f, s.remodMaxWeight);
            _remodUpD.Execute(_cmd, _camera, _remodDemod, _gNormalDepth, _gInstanceId, depth, guardPx, _guardScale, _jitter, reset, traceMap);
            _remodUpX.Execute(_cmd, _camera, _remodX, _gNormalDepth, _gInstanceId, depth, guardPx, _guardScale, _jitter, reset, traceMap);

            if (_remodFinal == null || _remodFinal.width != Ws || _remodFinal.height != Hs)
            {
                if (_remodFinal != null) _remodFinal.Release();
                _remodFinal = new RenderTexture(Ws, Hs, 0, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear)
                    { enableRandomWrite = true, filterMode = FilterMode.Point, name = "UltraRT Remod Final" };
                _remodFinal.Create();
            }
            var cs = _rrShader;
            int k = _kRemodFinish;
            var tm = traceMap ?? new Vector4((float)(tw - 2 * guardPx.x) / Ws, (float)(th - 2 * guardPx.y) / Hs, guardPx.x, guardPx.y);
            _cmd.SetComputeVectorParam(cs, "_Dim", new Vector4(tw, th, 1f / tw, 1f / th));
            _cmd.SetComputeVectorParam(cs, "_OutDim", new Vector4(Ws, Hs, 1f / Ws, 1f / Hs));
            _cmd.SetComputeVectorParam(cs, "_TraceMap", tm);
            _cmd.SetComputeVectorParam(cs, "_Jitter", new Vector4(_jitter.x, _jitter.y, 0f, 0f));
            _cmd.SetComputeTextureParam(cs, k, "_RemodX", _remodUpX.Output);
            _cmd.SetComputeTextureParam(cs, k, "_DemodHi", _remodUpD.Output);
            _cmd.SetComputeTextureParam(cs, k, "_DemodTex", _remodDemod);
            _cmd.SetComputeTextureParam(cs, k, "_Final", _remodFinal);
            _cmd.DispatchCompute(cs, k, Mathf.CeilToInt(Ws / 8f), Mathf.CeilToInt(Hs / 8f), 1);
            return _remodFinal;
        }
    }
}
