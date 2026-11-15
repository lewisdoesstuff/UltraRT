using System;
using System.Linq;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

namespace UltraRT
{
    /// <summary>
    /// Blood stains in reflections (docs/path-tracing.md, "Blood stains in reflections"). The game draws its stains as
    /// screen-space decals over the finished frame, so a reflection never showed them. Each frame StainGrid.compute hashes the
    /// stains near the camera (read straight from the game's GPU stain buffers) into a world-space grid, and PathTrace's
    /// reflection hits (relight's reflection ray, pathtrace's specular path vertices) apply the game's blood composite there
    /// (UltraRTStains.hlsl). Wet stains (BloodWet.cs's birth times) are darker, as at primary hits.
    /// Bench: wet.stainReflections=0. Flag: -ultrart-stain-reflections on|off.
    /// </summary>
    public partial class Plugin
    {
        private const int StainBuckets = 16384, StainBucket = 32, StainRecordBytes = 64; // as UltraRTStainGrid.hlsl
        private ComputeShader _stainShader;
        private int _kStainClear = -1, _kStainInsert = -1;
        private ComputeBuffer _stainGrid, _stainRecords, _stainDummyGrid, _stainDummyRecords, _stainDummyBirth;
        private float _stainNextLog;

        private void LoadStainShader(AssetBundle bundle)
        {
            _stainShader = bundle.LoadAllAssets<ComputeShader>().FirstOrDefault(s => s.name == "StainGrid");
            _kStainClear = _stainShader != null ? _stainShader.FindKernel("Clear") : -1;
            _kStainInsert = _stainShader != null ? _stainShader.FindKernel("Insert") : -1;
            if (_stainShader == null) Logger.LogWarning("StainGrid compute shader missing from bundle: no blood stains in reflections");
        }

        /// <summary>Trace, before the dispatch: builds the stain grid (when there are stains and reflections can see them) and binds it.</summary>
        private void BindStains(RayTracingShader shader, bool wanted)
        {
            var s = _wetSettings;
            bool on = false;
            BloodsplatterManager bsm = null;
            if (wanted && s.stainReflections && _stainShader != null)
            {
                try
                {
                    bsm = MonoSingleton<BloodsplatterManager>.Instance;
                    on = bsm != null && bsm.usedComputeShadersAtStart && bsm.instanceBuffer != null && bsm.parentBuffer != null && bsm.stainMat != null &&
                         bsm.optimizedBloodMesh != null && bsm.instanceBuffer.count > 0;
                }
                catch (Exception) { on = false; }
            }
            if (on)
            {
                try { BuildStainGrid(bsm, s); }
                catch (Exception e)
                {
                    Logger.LogWarning($"Blood stains in reflections failed, switching them off: {e}");
                    s.stainReflections = false;
                    on = false;
                }
            }
            if (_stainDummyGrid == null)
            {
                _stainDummyGrid = new ComputeBuffer(StainBucket + 1, 4) { name = "UltraRT Stain Grid (none)" };
                _stainDummyGrid.SetData(new uint[StainBucket + 1]);
                _stainDummyRecords = new ComputeBuffer(1, StainRecordBytes) { name = "UltraRT Stain Records (none)" };
            }
            _cmd.SetRayTracingBufferParam(shader, "_StainGrid", on ? _stainGrid : _stainDummyGrid);
            _cmd.SetRayTracingBufferParam(shader, "_StainRecords", on ? _stainRecords : _stainDummyRecords);
            var atlas = on && bsm.stainMat.HasProperty("_SplatterAtlas") ? bsm.stainMat.GetTexture("_SplatterAtlas") : null;
            _cmd.SetRayTracingTextureParam(shader, "_StainAtlas", atlas != null ? atlas : Texture2D.whiteTexture);
            Vector2 fog = StainFade();
            _cmd.SetRayTracingVectorParam(shader, "_StainParams", new Vector4(on ? 1f : 0f, s.blood ? Mathf.Clamp01(s.bloodDarken) : 0f, fog.x, fog.y));
            _cmd.SetRayTracingVectorParam(shader, "_StainCamPos", _camera.transform.position);
        }

        /// <summary>The distances over which the game's stains fade (its composite: the fog's start and end).</summary>
        private static Vector2 StainFade() =>
            RenderSettings.fog ? new Vector2(RenderSettings.fogStartDistance, Mathf.Max(RenderSettings.fogStartDistance + 1f, RenderSettings.fogEndDistance)) : new Vector2(0f, 1e6f);

        private void BuildStainGrid(BloodsplatterManager bsm, WetSettings s)
        {
            int cap = bsm.instanceBuffer.count;
            if (_stainGrid == null) _stainGrid = new ComputeBuffer(StainBuckets * (StainBucket + 1), 4) { name = "UltraRT Stain Grid" };
            if (_stainRecords == null || _stainRecords.count != cap)
            {
                _stainRecords?.Release();
                _stainRecords = new ComputeBuffer(cap, StainRecordBytes) { name = "UltraRT Stain Records" };
            }
            var cs = _stainShader;
            var birth = _bloodWet?.BirthBuffer;
            bool haveBirth = birth != null && birth.count == cap && s.blood;
            if (_stainDummyBirth == null) { _stainDummyBirth = new ComputeBuffer(1, 4) { name = "UltraRT Stain Birth (none)" }; _stainDummyBirth.SetData(new float[] { -1e9f }); }
            float half = Mathf.Max(0.01f, bsm.optimizedBloodMesh.bounds.extents.x);
            _cmd.SetComputeBufferParam(cs, _kStainClear, "_StainGridOut", _stainGrid);
            _cmd.DispatchCompute(cs, _kStainClear, (StainBuckets + 63) / 64, 1, 1);
            int k = _kStainInsert;
            _cmd.SetComputeBufferParam(cs, k, "instanceBuffer", bsm.instanceBuffer);
            _cmd.SetComputeBufferParam(cs, k, "parentBuffer", bsm.parentBuffer);
            _cmd.SetComputeBufferParam(cs, k, "_BloodBirth", haveBirth ? birth : _stainDummyBirth);
            _cmd.SetComputeBufferParam(cs, k, "_StainGridOut", _stainGrid);
            _cmd.SetComputeBufferParam(cs, k, "_StainRecordsOut", _stainRecords);
            _cmd.SetComputeVectorParam(cs, "_StainBuild", new Vector4(cap, Mathf.Max(1f, s.stainDistance), Shader.GetGlobalFloat("_StainWarping"), Time.time));
            _cmd.SetComputeVectorParam(cs, "_StainBuild2", new Vector4(Mathf.Max(0f, s.bloodWetTime), Mathf.Max(0.1f, s.bloodDryTime), haveBirth ? 1f : 0f, half));
            _cmd.SetComputeVectorParam(cs, "_StainCam", _camera.transform.position);
            _cmd.DispatchCompute(cs, k, (cap + 63) / 64, 1, 1);
            if (Time.realtimeSinceStartup >= _stainNextLog)
            {
                _stainNextLog = Time.realtimeSinceStartup + 30f;
                var fog = StainFade();
                Logger.LogInfo($"Blood stains in reflections: {cap} slots, mesh half extent {half:F2}, fade {fog.x:F0}-{fog.y:F0} m, wet {(haveBirth ? "on" : "off")}");
            }
        }

        private void ReleaseStains()
        {
            _stainGrid?.Release(); _stainRecords?.Release(); _stainDummyGrid?.Release(); _stainDummyRecords?.Release(); _stainDummyBirth?.Release();
            _stainGrid = _stainRecords = _stainDummyGrid = _stainDummyRecords = _stainDummyBirth = null;
        }
    }
}
