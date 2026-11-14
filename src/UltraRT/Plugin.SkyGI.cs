using System;
using System.Globalization;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace UltraRT
{
    /// <summary>
    /// Relight sky light, sun disk and linear light transport (docs/relight.md, "Sky and GI"), plus the radiance
    /// statistics the bench logs for every RT screenshot. Plugin.cs only has one-line hooks: ParseSkyArgs (Awake),
    /// SkyHotkeys (OnUpdate), BindSkyLight (wherever BindSky runs), SkyGlobals (Trace).
    ///   -ultrart-relight-linear on|off   linear light transport (default on)
    ///   -ultrart-sun-angle DEG           angular radius of the sun disk for shadowed directional lights (default 0.5)
    ///   -ultrart-sky-sampling on|off     sample the sky directly with a visibility ray (default on)
    ///   -ultrart-sky-scale F             sky light multiplier (default 1)
    /// Ctrl+F5: toggle all three (A/B against the old relight).
    /// </summary>
    public partial class Plugin
    {
        private bool _relightLinear = true;
        private float _sunAngle = 0.5f;   // degrees (angular radius)
        private bool _skySampling = true;
        private float _skyScale = 1f;
        private bool _skyGI = true;       // Ctrl+F5: master switch for the three above
        private float _lastLinear = -1f;
        private SkyTable _skyTable;

        private static readonly int UltraRTLinearId = Shader.PropertyToID("_UltraRTLinear");

        private void ParseSkyArgs()
        {
            var a = Environment.GetCommandLineArgs();
            bool On(string v) => v == null || !(v == "0" || v.Equals("off", StringComparison.OrdinalIgnoreCase) || v.Equals("false", StringComparison.OrdinalIgnoreCase));
            for (int i = 0; i < a.Length; i++)
            {
                string Next() => i + 1 < a.Length ? a[++i] : null;
                switch (a[i].ToLowerInvariant())
                {
                    case "-ultrart-relight-linear": _relightLinear = On(Next()); break;
                    case "-ultrart-sky-sampling": _skySampling = On(Next()); break;
                    case "-ultrart-sun-angle": float.TryParse(Next(), NumberStyles.Float, CultureInfo.InvariantCulture, out _sunAngle); break;
                    case "-ultrart-sky-scale": float.TryParse(Next(), NumberStyles.Float, CultureInfo.InvariantCulture, out _skyScale); break;
                }
            }
            Logger.LogInfo($"Relight sky/GI: linear {_relightLinear}, sun {_sunAngle} deg, sky sampling {_skySampling}, sky scale {_skyScale}");
        }

        private void SkyHotkeys()
        {
            if (!Input.GetKeyDown(KeyCode.F5) || !CtrlHeld()) return;
            _skyGI = !_skyGI;
            _denoiser?.Reset(); _lightSampling?.Reset();
            Logger.LogInfo($"Relight sky/GI (linear transport, sky sampling, sun disk): {(_skyGI ? "on" : "off")}");
            Toast($"Relight sky/GI {(_skyGI ? "on" : "off")} (Ctrl+F5)");
        }

        private bool LinearActive => _relightActive && _skyGI && _relightLinear;

        /// <summary>_SkyLight: x linear transport, y sun angular radius (rad), z sky sampling, w sky scale. Relight only.</summary>
        private Vector4 SkyLightParams => _relightActive && _skyGI
            ? new Vector4(_relightLinear ? 1f : 0f, Mathf.Max(0f, _sunAngle) * Mathf.Deg2Rad, _skySampling ? 1f : 0f, Mathf.Max(0f, _skyScale))
            : new Vector4(0f, 0f, 0f, 1f);

        /// <summary>Per frame, before the traces: the composites' conversion flag, and a cache reset when the lighting space changes.</summary>
        private void SkyGlobals()
        {
            float lin = LinearActive ? 1f : 0f;
            _cmd.SetGlobalFloat(UltraRTLinearId, lin);
            if (lin != _lastLinear)
            {
                // Cache and reservoir weights are in the other space. The short cache history (as after a light change)
                // lets the multi-bounce tail re-converge in a few frames instead of ~32.
                if (_lastLinear >= 0f) { _radianceCache?.Reset(); _radianceCache?.NotifyLightsChanged(); _lightSampling?.Reset(); }
                _lastLinear = lin;
            }
        }

        /// <summary>Binds _SkyLight and the sky table (_SkyAlias, _SkyParams.zw) on a shader that includes UltraRTSky.hlsl.
        /// Call right after BindSky (which sets _SkyParams.xy).</summary>
        private void BindSkyLight(RayTracingShader shader, Vector4 skyParams, Texture tex, Color tint, Color flat)
        {
            _skyTable ??= new SkyTable();
            _skyTable.Update(_cmd, tex, tint, flat, _relightActive && _skyGI && _skySampling);
            skyParams.z = _skyTable.Width;
            skyParams.w = _skyTable.Height;
            _cmd.SetRayTracingVectorParam(shader, "_SkyParams", skyParams);
            _cmd.SetRayTracingBufferParam(shader, "_SkyAlias", _skyTable.Buffer);
            _cmd.SetRayTracingVectorParam(shader, "_SkyLight", SkyLightParams);
        }

        /// <summary>
        /// Luminance-weighted alias table over a W x H lat-long grid of the sky (UltraRTSky.hlsl), rebuilt when the sky
        /// texture or tint changes: the texture is blitted into a 512x256 target, mipped down to 64x32 (box average) and
        /// read back asynchronously. Until it arrives the previous table stays bound: any table is unbiased under MIS.
        /// A flat sky (no texture) uses a 1x1 table (uniform sphere).
        /// </summary>
        private sealed class SkyTable
        {
            private const int W = 64, H = 32, Mip = 3;
            private ComputeBuffer _table, _flat;
            private bool _built;
            /// <summary>The buffer to bind as _SkyAlias (the 1-entry uniform table until a texture table is built).</summary>
            public ComputeBuffer Buffer => _built && Width == W ? _table : _flat;
            public int Width { get; private set; }
            public int Height { get; private set; }
            private RenderTexture _rt;
            private Texture _source;
            private Color _tint;
            private bool _pending;
            private int _requestId;
            public string LastBuild = "none";

            public SkyTable()
            {
                _table = new ComputeBuffer(W * H, 16);
                _flat = new ComputeBuffer(1, 16);
                _flat.SetData(new uint[] { Bits(1f), 0u, Bits(1f), Bits(1f) });
            }

            private static uint Bits(float f) => BitConverter.ToUInt32(BitConverter.GetBytes(f), 0);

            public void Update(CommandBuffer cmd, Texture tex, Color tint, Color flat, bool wanted)
            {
                if (!wanted) { Width = Height = 0; return; }
                if (tex == null)
                {
                    // Flat sky colour: uniform sphere, or nothing if it is black.
                    Width = Height = flat.r + flat.g + flat.b > 1e-4f ? 1 : 0;
                    return;
                }
                if ((tex != _source || tint != _tint) && !_pending) Request(cmd, tex, tint);
                // Until the first table arrives: the uniform sphere. A table built for another sky is still unbiased.
                Width = _built ? W : 1;
                Height = _built ? H : 1;
            }

            private void Request(CommandBuffer cmd, Texture tex, Color tint)
            {
                if (_rt == null)
                {
                    _rt = new RenderTexture(W << Mip, H << Mip, 0, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear)
                        { useMipMap = true, autoGenerateMips = false, filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Repeat, name = "UltraRT SkyTable" };
                    _rt.Create();
                }
                _source = tex;
                _tint = tint;
                _pending = true;
                int id = ++_requestId;
                cmd.Blit(tex, _rt);
                cmd.GenerateMips(_rt);
                cmd.RequestAsyncReadback(_rt, Mip, TextureFormat.RGBAFloat, r => OnReadback(r, id, tint, tex.name));
            }

            private void OnReadback(AsyncGPUReadbackRequest r, int id, Color tint, string name)
            {
                _pending = false;
                if (id != _requestId || _table == null) return;
                if (r.hasError || r.width != W || r.height != H) { LastBuild = $"readback failed ({r.width}x{r.height})"; return; }
                var px = r.GetData<Vector4>();
                var w = new double[W * H];
                double sum = 0;
                for (int y = 0; y < H; y++)
                {
                    double omega = 2.0 * Math.PI / W * (Math.Sin(((y + 1.0) / H - 0.5) * Math.PI) - Math.Sin(((double)y / H - 0.5) * Math.PI));
                    for (int x = 0; x < W; x++)
                    {
                        var c = px[y * W + x];
                        // Linear luminance of the sky as a light (tex * tint), times the cell's solid angle.
                        double lum = 0.2126 * Math.Pow(Math.Max(0, c.x * tint.r), 2.2) + 0.7152 * Math.Pow(Math.Max(0, c.y * tint.g), 2.2) + 0.0722 * Math.Pow(Math.Max(0, c.z * tint.b), 2.2);
                        w[y * W + x] = (double.IsNaN(lum) ? 0 : lum) * omega;
                        sum += w[y * W + x];
                    }
                }
                if (!(sum > 0)) { for (int i = 0; i < w.Length; i++) w[i] = 1; sum = w.Length; }
                // A small uniform floor keeps dark parts of the sky reachable (MIS makes any table unbiased; this only helps variance).
                double floor = 0.02 * sum / (4.0 * Math.PI);
                double total = 0;
                for (int y = 0; y < H; y++)
                {
                    double omega = 2.0 * Math.PI / W * (Math.Sin(((y + 1.0) / H - 0.5) * Math.PI) - Math.Sin(((double)y / H - 0.5) * Math.PI));
                    for (int x = 0; x < W; x++) { w[y * W + x] += floor * omega; total += w[y * W + x]; }
                }
                _table.SetData(BuildAlias(w, total));
                _built = true;
                // Where the sky is brightest, for the log: row 0 is straight down.
                int best = 0;
                for (int i = 1; i < w.Length; i++) if (w[i] > w[best]) best = i;
                double upper = 0;
                for (int i = W * H / 2; i < w.Length; i++) upper += w[i];
                LastBuild = string.Format(CultureInfo.InvariantCulture, "'{0}' {1}x{2}, upper hemisphere {3:P0} of the light, brightest cell at elevation {4:F0} deg",
                    name, W, H, upper / total, ((best / W + 0.5) / H - 0.5) * 180.0);
            }

            private static uint[] BuildAlias(double[] w, double total)
            {
                int n = w.Length;
                var prob = new double[n];
                var alias = new int[n];
                var scaled = new double[n];
                var small = new System.Collections.Generic.Stack<int>();
                var large = new System.Collections.Generic.Stack<int>();
                for (int i = 0; i < n; i++)
                {
                    scaled[i] = w[i] / total * n;
                    alias[i] = i;
                    (scaled[i] < 1.0 ? small : large).Push(i);
                }
                while (small.Count > 0 && large.Count > 0)
                {
                    int s = small.Pop(), l = large.Pop();
                    prob[s] = scaled[s];
                    alias[s] = l;
                    scaled[l] = scaled[l] + scaled[s] - 1.0;
                    (scaled[l] < 1.0 ? small : large).Push(l);
                }
                while (large.Count > 0) { int l = large.Pop(); prob[l] = 1.0; alias[l] = l; }
                while (small.Count > 0) { int s = small.Pop(); prob[s] = 1.0; alias[s] = s; }
                var data = new uint[n * 4];
                for (int i = 0; i < n; i++)
                {
                    data[i * 4] = Bits((float)prob[i]);
                    data[i * 4 + 1] = (uint)i | ((uint)alias[i] << 16);
                    data[i * 4 + 2] = Bits((float)(w[i] / total));
                    data[i * 4 + 3] = Bits((float)(w[alias[i]] / total));
                }
                return data;
            }

            public void Dispose()
            {
                _table?.Release(); _table = null;
                _flat?.Release(); _flat = null;
                if (_rt != null) { _rt.Release(); _rt = null; }
            }
        }

        /// <summary>
        /// Mean demodulated lighting of the last traced frame, split by surface orientation (up: n.y > 0.7, down: n.y < -0.7,
        /// side: the rest). Fullbright pixels (lighting exactly 1) and sky are left out. In lighting space (linear with linear transport; then also the
        /// mean after conversion to gamma, "G"). Synchronous readback: bench / debugging only.
        /// </summary>
        private string RadianceStats()
        {
            if (_gRadiance == null || _gNormalDepth == null) return "no g-buffer";
            // Native formats (ARGBHalf, ARGBHalf, ARGBInt): converting readbacks fail on these targets.
            var rq = AsyncGPUReadback.Request(_gRadiance);
            var nq = AsyncGPUReadback.Request(_gNormalDepth);
            rq.WaitForCompletion(); nq.WaitForCompletion();
            if (rq.hasError || nq.hasError) return $"readback failed (radiance {rq.hasError}, normal {nq.hasError})";
            NativeArray<ushort> radH = rq.GetData<ushort>(), ndH = nq.GetData<ushort>();
            int pixels = radH.Length / 4;
            if (ndH.Length / 4 != pixels) return $"readback size mismatch {radH.Length} {ndH.Length}";
            Vector4 H4(NativeArray<ushort> a, int i) => new Vector4(Mathf.HalfToFloat(a[i * 4]), Mathf.HalfToFloat(a[i * 4 + 1]), Mathf.HalfToFloat(a[i * 4 + 2]), Mathf.HalfToFloat(a[i * 4 + 3]));
            var sum = new Vector3[4];
            var cnt = new int[4];
            int sky = 0, full = 0, total = pixels;
            for (int i = 0; i < pixels; i++)
            {
                var n = H4(ndH, i);
                if (n.w < 0f) { sky++; continue; }
                // Fullbright surfaces: PathTrace writes lighting = lerp(1, lighting, 0) = exactly 1.
                if (radH[i * 4] == 0x3C00 && radH[i * 4 + 1] == 0x3C00 && radH[i * 4 + 2] == 0x3C00) { full++; continue; }
                int c = n.y > 0.7f ? 0 : n.y < -0.7f ? 2 : 1;
                var r = H4(radH, i);
                if (float.IsNaN(r.x) || float.IsInfinity(r.x) || float.IsNaN(r.y) || float.IsNaN(r.z)) continue;
                sum[c] += new Vector3(r.x, r.y, r.z);
                cnt[c]++;
            }
            for (int c = 0; c < 3; c++) { sum[3] += sum[c]; cnt[3] += cnt[c]; }
            bool lin = LinearActive;
            string Part(string name, int c)
            {
                if (cnt[c] == 0) return $"{name} -";
                var m = sum[c] / cnt[c];
                float lum = 0.2126f * m.x + 0.7152f * m.y + 0.0722f * m.z;
                string g = lin ? string.Format(CultureInfo.InvariantCulture, " G={0:F3}", Mathf.Pow(Mathf.Max(lum, 0f), 1f / 2.2f)) : "";
                return string.Format(CultureInfo.InvariantCulture, "{0} {1:P0} L={2:F3}{6} ({3:F2},{4:F2},{5:F2})",
                    name, (float)cnt[c] / total, lum, m.x, m.y, m.z, g);
            }
            return $"sky table {_skyTable?.LastBuild ?? "-"}; radiance{(lin ? " (linear)" : "")}: {Part("all", 3)} | {Part("up", 0)} | {Part("side", 1)} | {Part("down", 2)} | sky {(float)sky / total:P0} fullbright {(float)full / total:P0}";
        }
    }
}
