using System.Text;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace UltraRT
{
    /// <summary>
    /// Ray counters (docs/performance.md, "Ray counts"; plan 5.1 step 5). Bench plugin._rayCounts=1 swaps the main ray
    /// tracing shader for PathTraceCount.raytrace: the same ray generation, counting every TraceRay by kind per pixel
    /// and writing the counts over _Output (camera, shadow, V1 shadow, bounce) and _Radiance (reflection, transparent,
    /// effects, portal segments). Every 10th frame both are read back and summed; every 2 s the log gets the mean rays per
    /// trace pixel by kind. The image is garbage while it runs (a diagnostic, not a view).
    /// </summary>
    public partial class Plugin
    {
        private bool _rayCounts;
        private RayTracingShader _ptCountShader;
        private readonly double[] _rayCountSum = new double[8];
        private int _rayCountPending, _rayCountReads, _rayCountLogs;
        private long _rayCountPixels;
        private float _rayCountNextLog;
        private string _rayCountLabel = "";
        private static readonly string[] RayCountKinds = { "camera", "shadow", "V1 shadow", "bounce", "reflection", "transparent", "effects", "portal segments" };

        /// <summary>The shader the main dispatch uses: the counting variant while counting.</summary>
        private RayTracingShader MainTraceShader(bool pt) => pt ? (_rayCounts && _ptCountShader != null ? _ptCountShader : _ptShader) : _debugShader;

        /// <summary>After the main DispatchRays (recorded in _cmd): every 10th frame, read the counts of a w x h dispatch back.</summary>
        private void RequestRayCounts(bool pt, int w, int h, string label)
        {
            if (!_rayCounts || !pt || _ptCountShader == null || _rayCountPending > 0 || Time.frameCount % 10 != 0) return;
            if (_output == null || _gRadiance == null || _output.width < w || _output.height < h || _gRadiance.width < w || _gRadiance.height < h) return;
            _rayCountPending = 2;
            if (label != _rayCountLabel) { System.Array.Clear(_rayCountSum, 0, _rayCountSum.Length); _rayCountPixels = 0; _rayCountReads = 0; }
            _rayCountLabel = label;
            _cmd.RequestAsyncReadback(_output, r => RayCountsArrived(r, 0, w, h));
            _cmd.RequestAsyncReadback(_gRadiance, r => RayCountsArrived(r, 4, w, h));
        }

        private void RayCountsArrived(AsyncGPUReadbackRequest r, int first, int w, int h)
        {
            _rayCountPending = Mathf.Max(0, _rayCountPending - 1);
            if (r.hasError || r.layerCount < 1) return;
            var data = r.GetData<ushort>();
            int stride = r.width;
            if (data.Length < stride * h * 4) return;
            var sum = new double[4];
            for (int y = 0; y < h; y++)
            {
                int row = y * stride * 4;
                for (int x = 0; x < w; x++)
                {
                    int i = row + x * 4;
                    sum[0] += Mathf.HalfToFloat(data[i]);
                    sum[1] += Mathf.HalfToFloat(data[i + 1]);
                    sum[2] += Mathf.HalfToFloat(data[i + 2]);
                    sum[3] += Mathf.HalfToFloat(data[i + 3]);
                }
            }
            for (int k = 0; k < 4; k++) _rayCountSum[first + k] += sum[k];
            if (first == 0) { _rayCountPixels += (long)w * h; _rayCountReads++; }
            if (_rayCountPending == 0 && Time.realtimeSinceStartup >= _rayCountNextLog && _rayCountPixels > 0 && _rayCountLogs < 400)
            {
                _rayCountNextLog = Time.realtimeSinceStartup + 2f;
                _rayCountLogs++;
                var sb = new StringBuilder();
                double total = 0;
                for (int k = 0; k < 7; k++) total += _rayCountSum[k];
                sb.Append($"Ray counts ({_rayCountLabel}, {w}x{h}, {_rayCountReads} frames): {total / _rayCountPixels:F2} rays per trace pixel =");
                for (int k = 0; k < 7; k++) sb.Append($" {RayCountKinds[k]} {_rayCountSum[k] / _rayCountPixels:F3}");
                sb.Append($"; {RayCountKinds[7]} {_rayCountSum[7] / _rayCountPixels:F3} (included above)");
                Logger.LogInfo(sb.ToString());
                System.Array.Clear(_rayCountSum, 0, _rayCountSum.Length);
                _rayCountPixels = 0;
                _rayCountReads = 0;
            }
        }
    }
}
