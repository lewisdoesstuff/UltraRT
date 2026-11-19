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
            _cmd.SetRayTracingVectorParam(shader, "_StainParams", new Vector4(on ? (s.stainDebug ? 2f : 1f) : 0f, s.blood ? Mathf.Clamp01(s.bloodDarken) : 0f, fog.x, fog.y));
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
            var cp = _camera.transform.position;
            _cmd.SetComputeVectorParam(cs, "_StainCam", new Vector4(cp.x, cp.y, cp.z, StainMeshSide(bsm.optimizedBloodMesh)));
            _cmd.DispatchCompute(cs, k, (cap + 63) / 64, 1, 1);
            if (s.stainDebug && Time.realtimeSinceStartup >= _stainNextProbe) { _stainNextProbe = Time.realtimeSinceStartup + 2f; ProbeStains(bsm); }
            if (Time.realtimeSinceStartup >= _stainNextLog)
            {
                _stainNextLog = Time.realtimeSinceStartup + 30f;
                var fog = StainFade();
                Logger.LogInfo($"Blood stains in reflections: {cap} slots, mesh bounds {bsm.optimizedBloodMesh.bounds}, fade {fog.x:F0}-{fog.y:F0} m, wet {(haveBirth ? "on" : "off")}");
            }
        }

        private float _stainNextProbe;
        private Mesh _stainSideMesh;
        private float _stainSide = -1f;

        /// <summary>The sign of the stain quad's normal z: the game's clip test uses the quad's normal (the stored stain normal
        /// points into the surface). -1 when the mesh isn't readable: a floor stain stores (0, -1, 0) and the game shows it on the floor.</summary>
        private float StainMeshSide(Mesh m)
        {
            if (m == _stainSideMesh) return _stainSide;
            _stainSideMesh = m;
            _stainSide = -1f;
            try { if (m.isReadable) { var n = m.normals; if (n.Length > 0 && n[0].z > 0f) _stainSide = 1f; } } catch (Exception) { }
            Logger.LogInfo($"Blood stains in reflections: mesh '{m.name}' readable {m.isReadable}, side {_stainSide}");
            return _stainSide;
        }

        /// <summary>Debug (wet.stainDebug=1): reads the game's stain slots, our records and the grid back and logs a few.</summary>
        private void ProbeStains(BloodsplatterManager bsm)
        {
            var cam = _camera.transform.position;
            var atlas = bsm.stainMat.HasProperty("_SplatterAtlas") ? bsm.stainMat.GetTexture("_SplatterAtlas") : null;
            var inst = bsm.instanceBuffer;
            var recs = _stainRecords;
            var grid = _stainGrid;
            var parents = bsm.parentBuffer;
            _cmd.RequestAsyncReadback(inst, ri =>
            {
                if (ri.hasError) { Logger.LogWarning("Stain probe: instance readback failed"); return; }
                var d = ri.GetData<float>();
                var used = new System.Collections.Generic.List<int>();
                for (int i = 0; i < inst.count; i++)
                    if (d[i * 8 + 4] != 0f || d[i * 8 + 5] != 0f || d[i * 8 + 6] != 0f) used.Add(i);
                var sb = new System.Text.StringBuilder($"Stain probe: camera {cam:F2}, {used.Count} stains in {inst.count} slots, parents {parents.count}, atlas {(atlas != null ? $"{atlas.name} {atlas.width}x{atlas.height} {atlas.graphicsFormat}" : "none")}");
                var di = d.ToArray();
                if (recs != null && grid != null)
                    ProbeStainGrid(recs, grid, used, di, sb);
                else Logger.LogInfo(sb.ToString());
            });
        }

        private void ProbeStainGrid(ComputeBuffer recs, ComputeBuffer grid, System.Collections.Generic.List<int> used, float[] inst, System.Text.StringBuilder sb)
        {
            UnityEngine.Rendering.AsyncGPUReadback.Request(grid, rg =>
            {
                if (rg.hasError) { Logger.LogInfo(sb + " (grid readback failed)"); return; }
                var g = rg.GetData<uint>();
                long total = 0; int full = 0, nonEmpty = 0;
                for (int b = 0; b < StainBuckets; b++) { uint c = g[b * (StainBucket + 1)]; total += c; if (c > 0) nonEmpty++; if (c >= StainBucket) full++; }
                sb.Append($"; grid {total} entries in {nonEmpty} buckets ({full} full)");
                UnityEngine.Rendering.AsyncGPUReadback.Request(recs, rr =>
                {
                    if (rr.hasError) { Logger.LogInfo(sb + " (record readback failed)"); return; }
                    var r = rr.GetData<float>();
                    int shown = 0;
                    for (int k = used.Count - 1; k >= 0 && shown < 4; k--, shown++)
                    {
                        int i = used[k];
                        int o = i * 16;
                        sb.Append($"\n  slot {i}: pos ({inst[i * 8]:F2}, {inst[i * 8 + 1]:F2}, {inst[i * 8 + 2]:F2}) parent {BitConverter.ToInt32(BitConverter.GetBytes(inst[i * 8 + 3]), 0)} " +
                                  $"norm ({inst[i * 8 + 4]:F2}, {inst[i * 8 + 5]:F2}, {inst[i * 8 + 6]:F2}) clip {BitConverter.ToInt32(BitConverter.GetBytes(inst[i * 8 + 7]), 0)} -> " +
                                  $"centre ({r[o]:F2}, {r[o + 1]:F2}, {r[o + 2]:F2}) halfZ {r[o + 3]:F2} ax ({r[o + 4]:F2}, {r[o + 5]:F2}, {r[o + 6]:F2}) uvScale {r[o + 7]:F3} " +
                                  $"az ({r[o + 12]:F2}, {r[o + 13]:F2}, {r[o + 14]:F2}) cell {r[o + 11]:F1} flags {r[o + 15]:F2}");
                    }
                    Logger.LogInfo(sb.ToString());
                });
            });
        }
    }
}
