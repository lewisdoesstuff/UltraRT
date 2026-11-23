using System;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace UltraRT
{
    public enum RadianceCacheMode
    {
        /// <summary>No cache: the bounce hit takes one RIS light sample, as before.</summary>
        Off = 0,
        /// <summary>The bounce hit reads its lighting (direct + further bounces) from the cache; fallback on a miss.</summary>
        Bounce = 1,
        /// <summary>No bounce ray: the primary hit reads its indirect light from the cache (jittered); fallback on a miss.</summary>
        Primary = 2,
    }

    public enum RadianceCacheDebug
    {
        None = 0,
        /// <summary>albedo x cached total lighting + emission at the primary hit (magenta checker = no valid cell).</summary>
        Radiance = 1,
        /// <summary>Cached total lighting (demodulated).</summary>
        Lighting = 2,
        /// <summary>Cached indirect lighting (demodulated).</summary>
        Indirect = 3,
        /// <summary>Cells: a hash colour per cell, darker on odd LOD levels; grey = not in the table.</summary>
        Cells = 4,
        /// <summary>Samples in the cell's history, log heat map (blue 1 .. red ~1000).</summary>
        Samples = 5,
    }

    /// <summary>Tunables, read every <see cref="RadianceCache.Execute"/>. Safe to change at any time; changing
    /// <see cref="capacityLog2"/>, <see cref="cellSize"/>, <see cref="lodDistance"/> or <see cref="normalOffset"/> clears the cache.</summary>
    public sealed class RadianceCacheSettings
    {
        public bool enabled = true;
        public RadianceCacheMode mode = RadianceCacheMode.Bounce;
        /// <summary>Cell edge (m) within <see cref="lodDistance"/> of the camera.</summary>
        public float cellSize = 0.5f;
        /// <summary>Beyond this distance (m) the cell size doubles with every doubling of the distance.</summary>
        public float lodDistance = 10f;
        /// <summary>Points are pushed this fraction of a cell along their normal before quantising (keeps grid-aligned walls in one cell).</summary>
        public float normalOffset = 0.5f;
        /// <summary>Update paths: one per stride x stride block of trace pixels (4 = 1/16 of the pixels).</summary>
        public int updateStride = 4;
        /// <summary>History cap in frames' worth of samples (at each cell's own sampling rate).</summary>
        public float historyFrames = 32f;
        /// <summary>History cap for a few frames after lights or emitters appear or disappear (room toggles).</summary>
        public float changeHistoryFrames = 4f;
        /// <summary>Frames the short history stays in effect after a change.</summary>
        public int changeFrames = 4;
        /// <summary>Cells not sampled for this many frames are evicted.</summary>
        public int staleFrames = 120;
        /// <summary>Damping of the cached radiance used for the path tail (infinite-bounce feedback). 0 = no feedback.</summary>
        public float feedback = 0.8f;
        /// <summary>Albedo clamp on update paths, so white surfaces can't make the feedback loop diverge.</summary>
        public float albedoMax = 0.95f;
        /// <summary>A sample's lighting is clamped to this before it is added (fireflies, fixed-point range).</summary>
        public float maxSample = 64f;
        /// <summary>Cells with fewer history samples count as a miss.</summary>
        public float minSamples = 2f;
        /// <summary>Primary mode: query position jitter, as a fraction of the cell (hides the grid; the denoiser filters it).</summary>
        public float queryJitter = 1f;
        /// <summary>On a miss, also try the neighbouring LOD level (cells change level as the camera moves).</summary>
        public bool levelFallback = true;
        /// <summary>Slots in the hash table = 2^capacityLog2 (52 bytes each: 2^20 = 52 MB).</summary>
        public int capacityLog2 = 20;
        public RadianceCacheDebug debugView = RadianceCacheDebug.None;
    }

    /// <summary>
    /// World-space radiance cache for multi-bounce diffuse GI (SHaRC-style spatial hash grid). See docs/radiance-cache.md.
    /// Per traced frame: <see cref="Execute"/> binds the cache on PathTrace, whose paths update it (folded), and
    /// <see cref="Resolve"/> runs CacheResolve.compute after PathTrace's DispatchRays. <see cref="UpdateShader"/>
    /// (CacheUpdate.raytrace) is only the debug views' shader since its UpdateRayGen went (2026-11-23).
    /// </summary>
    internal sealed class RadianceCache : IDisposable
    {
        public RadianceCacheSettings Settings = new RadianceCacheSettings();

        public RayTracingShader UpdateShader { get; private set; }
        /// <summary>The update and resolve will run this frame (shaders loaded, enabled, mode not Off).</summary>
        public bool Active => Settings.enabled && Settings.mode != RadianceCacheMode.Off && UpdateShader != null && _resolve != null;
        public int Capacity => _capacity;
        public long GpuBytes => _keys == null ? 0 : (long)_capacity * BytesPerSlot;
        internal ComputeBuffer KeysBuffer => _keys ?? _dummyKeys;
        internal ComputeBuffer ResolvedBuffer => _resolved ?? _dummyResolved;
        internal int ActiveModeValue => Active ? (int)Settings.mode : 0;
        internal Vector4 GridParams => new Vector4(Mathf.Max(0.05f, Settings.cellSize), 1f / Mathf.Max(0.5f, Settings.lodDistance),
            Settings.normalOffset, Settings.levelFallback ? 1f : 0f);
        internal Vector4 QueryParams => new Vector4(Mathf.Max(1f, Settings.minSamples), Mathf.Max(0f, Settings.queryJitter), 0f, 0f);

        private const int BytesPerSlot = 4 + 32 + 16; // key, accumulators, resolved
        private const float FixedScale = 2048f; // ULTRART_CACHE_SCALE in UltraRTCache.hlsl

        private ComputeShader _resolve;
        private int _kResolve = -1, _kClear = -1;
        private ComputeBuffer _keys, _accum, _resolved;
        private ComputeBuffer _dummyKeys, _dummyAccum, _dummyResolved;
        private RenderTexture _dummyTex;
        private int _capacity;
        private bool _needsClear = true;
        private uint _frame = 1;
        private int _fastFrames;
        private float _keyCell, _keyLod, _keyOffset;

        private static readonly int
            IdKeys = Shader.PropertyToID("_CacheKeys"),
            IdAccum = Shader.PropertyToID("_CacheAccum"),
            IdResolved = Shader.PropertyToID("_CacheResolved"),
            IdMode = Shader.PropertyToID("_CacheMode"),
            IdCapacity = Shader.PropertyToID("_CacheCapacity"),
            IdGrid = Shader.PropertyToID("_CacheGrid"),
            IdQuery = Shader.PropertyToID("_CacheQuery"),
            IdFold = Shader.PropertyToID("_CacheFold"),
            IdDebugOut = Shader.PropertyToID("_CacheDebugOut"),
            IdDebug = Shader.PropertyToID("_CacheDebug"),
            IdFrame = Shader.PropertyToID("_CacheFrame"),
            IdResolve = Shader.PropertyToID("_CacheResolve"),
            IdInvViewProj = Shader.PropertyToID("_InvViewProj"),
            IdCamPos = Shader.PropertyToID("_CamPos"),
            IdEmissionScale = Shader.PropertyToID("_EmissionScale");

        public RadianceCache()
        {
            _dummyKeys = new ComputeBuffer(8, 4) { name = "UltraRT Cache keys (unused)" };
            _dummyKeys.SetData(new uint[8]);
            _dummyAccum = new ComputeBuffer(64, 4) { name = "UltraRT Cache accum (unused)" };
            _dummyAccum.SetData(new uint[64]);
            _dummyResolved = new ComputeBuffer(8, 16) { name = "UltraRT Cache resolved (unused)" };
            _dummyResolved.SetData(new uint[32]);
            _dummyTex = new RenderTexture(1, 1, 0, RenderTextureFormat.ARGBHalf) { enableRandomWrite = true, name = "UltraRT Cache debug (unused)" };
            _dummyTex.Create();
        }

        /// <summary>Shaders from the bundle (null if missing: the cache stays off). Call again after a bundle reload.</summary>
        public void SetShaders(RayTracingShader update, ComputeShader resolve)
        {
            UpdateShader = update;
            _resolve = resolve;
            _kResolve = resolve != null ? resolve.FindKernel("Resolve") : -1;
            _kClear = resolve != null ? resolve.FindKernel("Clear") : -1;
            if (update != null) update.SetShaderPass("UltraRTHit");
            _needsClear = true;
        }

        /// <summary>Forget all cells (scene change, shader reload, teleport).</summary>
        public void Reset() => _needsClear = true;

        /// <summary>Shorten the history for a few frames (lights or emitters appeared/disappeared).</summary>
        public void NotifyLightsChanged() => _fastFrames = Math.Max(_fastFrames, Settings.changeFrames);

        /// <summary>Whether the update runs this frame when the auxiliary passes take turns (docs/performance.md, "Pass
        /// schedule"): every <paramref name="every"/>-th frame, and every frame while the cache is new or the lights
        /// have just changed. The cache is in world space, so a frame without an update reads it unchanged.</summary>
        public bool Due(uint frameIndex, int every)
        {
            Every = Mathf.Max(1, every);
            return DueNow(frameIndex, every);
        }

        /// <summary>Frames per update, as last asked of <see cref="Due"/>: the history and stale lengths are divided by
        /// it, so the cache follows a changing light as fast in seconds as when it updates every frame.</summary>
        public int Every { get; private set; } = 1;

        private bool DueNow(uint frameIndex, int every) => every <= 1 || _needsClear || _fastFrames > 0 || _keys == null || frameIndex % (uint)every == 0;

        /// <summary>
        /// Bind the cache on PathTrace, which updates it (the folded update: PathTrace's own paths write into the cells, every
        /// <see cref="RadianceCacheSettings.updateStride"/>-th warp) and reads it. Must run on every PathTrace dispatch (PathTrace
        /// declares the cache inputs), before its DispatchRays; <see cref="Resolve"/> runs after it. (The separate update pass,
        /// CacheUpdate.raytrace's UpdateRayGen, was removed on 2026-11-23: docs/radiance-cache.md.)
        /// </summary>
        /// <param name="update">Whether PathTrace adds samples this frame (<see cref="Due"/>).</param>
        public void Execute(CommandBuffer cmd, RayTracingShader pathTrace, bool update = true)
        {
            var s = Settings;
            if (Active)
            {
                EnsureBuffers();
                if (_needsClear)
                {
                    BindResolve(cmd);
                    cmd.SetComputeIntParam(_resolve, IdCapacity, _capacity);
                    cmd.DispatchCompute(_resolve, _kClear, _capacity / 256, 1, 1);
                    _needsClear = false;
                }
                BindLookup(cmd, pathTrace, true);
                cmd.SetRayTracingBufferParam(pathTrace, IdAccum, _accum);
                int stride = Mathf.Clamp(s.updateStride, 1, 16);
                cmd.SetRayTracingVectorParam(pathTrace, IdFold, new Vector4(update ? stride : 0f, Mathf.Max(0f, s.feedback), Mathf.Clamp01(s.albedoMax), Mathf.Clamp(s.maxSample, 1f, 1000f)));
                return;
            }
            if (!s.enabled && _keys != null) ReleaseBuffers(); // switched off: give the memory back
            BindLookup(cmd, pathTrace, false);
            cmd.SetRayTracingBufferParam(pathTrace, IdAccum, _dummyAccum);
            cmd.SetRayTracingVectorParam(pathTrace, IdFold, Vector4.zero);
        }

        /// <summary>Resolve step: run CacheResolve.compute right after PathTrace's DispatchRays.</summary>
        public void Resolve(CommandBuffer cmd)
        {
            if (!Active || _keys == null) return;
            RunResolve(cmd);
        }

        private void RunResolve(CommandBuffer cmd)
        {
            var s = Settings;
            float history = _fastFrames > 0 ? Mathf.Min(s.changeHistoryFrames, s.historyFrames) : s.historyFrames;
            if (_fastFrames > 0) _fastFrames--;
            BindResolve(cmd);
            cmd.SetComputeIntParam(_resolve, IdCapacity, _capacity);
            cmd.SetComputeIntParam(_resolve, IdFrame, unchecked((int)_frame));
            cmd.SetComputeVectorParam(_resolve, IdResolve, new Vector4(Mathf.Max(1f, history / Every), Mathf.Max(1, s.staleFrames / Every), 1f / FixedScale, 0));
            cmd.DispatchCompute(_resolve, _kResolve, _capacity / 256, 1, 1);
            _frame++;
        }

        /// <summary>
        /// Debug view: overwrite <paramref name="target"/> (PathTrace's output, same size) with a view of the cache.
        /// Call after PathTrace's DispatchRays, in a frame where <see cref="Execute"/> ran with the cache active.
        /// </summary>
        public void DispatchDebug(CommandBuffer cmd, RenderTexture target, RadianceCacheDebug view, Matrix4x4 invViewProj, Vector3 camPos)
        {
            if (!Active || _keys == null || target == null || view == RadianceCacheDebug.None) return;
            var u = UpdateShader;
            BindLookup(cmd, u, true);
            cmd.SetRayTracingMatrixParam(u, IdInvViewProj, invViewProj);
            cmd.SetRayTracingVectorParam(u, IdCamPos, camPos);
            cmd.SetRayTracingFloatParam(u, IdEmissionScale, 1f);
            cmd.SetRayTracingTextureParam(u, IdDebugOut, target);
            cmd.SetRayTracingIntParam(u, IdDebug, (int)view);
            cmd.DispatchRays(u, "DebugRayGen", (uint)target.width, (uint)target.height, 1);
            cmd.SetRayTracingTextureParam(u, IdDebugOut, _dummyTex);
        }

        /// <summary>Bind the read-only lookups on another ray tracing shader (ParticleLight.raytrace), after <see cref="Execute"/>
        /// ran this frame.</summary>
        public void BindRayTracingLookup(CommandBuffer cmd, RayTracingShader shader) => BindLookup(cmd, shader, Active && _keys != null);

        private void BindLookup(CommandBuffer cmd, RayTracingShader shader, bool active)
        {
            var s = Settings;
            cmd.SetRayTracingBufferParam(shader, IdKeys, active ? _keys : _dummyKeys);
            cmd.SetRayTracingBufferParam(shader, IdResolved, active ? _resolved : _dummyResolved);
            cmd.SetRayTracingIntParam(shader, IdMode, active ? (int)s.mode : 0);
            cmd.SetRayTracingIntParam(shader, IdCapacity, active ? _capacity : 8);
            cmd.SetRayTracingVectorParam(shader, IdGrid, new Vector4(_keyCell > 0 ? _keyCell : 0.5f, 1f / Mathf.Max(0.1f, _keyLod > 0 ? _keyLod : 10f),
                                                                      _keyOffset, s.levelFallback ? 1f : 0f));
            cmd.SetRayTracingVectorParam(shader, IdQuery, new Vector4(Mathf.Max(1f, s.minSamples), Mathf.Max(0f, s.queryJitter), 0, 0));
        }

        /// <summary>Bind the read-only lookup inputs on another ray tracing shader that includes UltraRTCache.hlsl (the viewmodel pass,
        /// docs/viewmodel.md). Call after <see cref="Execute"/> in the same frame.</summary>
        public void BindLookupOn(CommandBuffer cmd, RayTracingShader shader) => BindLookup(cmd, shader, Active && _keys != null);

        /// <summary>
        /// Bind the read-only lookup inputs (UltraRTCache.hlsl) on a compute kernel: the denoiser's disocclusion seed
        /// (docs/denoiser.md). Binds nothing and returns false when the cache did not run this frame.
        /// </summary>
        public bool BindComputeLookup(CommandBuffer cmd, ComputeShader cs, int kernel)
        {
            if (!Active || _keys == null) return false;
            var s = Settings;
            cmd.SetComputeBufferParam(cs, kernel, IdKeys, _keys);
            cmd.SetComputeBufferParam(cs, kernel, IdResolved, _resolved);
            cmd.SetComputeIntParam(cs, IdMode, 1); // CacheLookup only checks > 0
            cmd.SetComputeIntParam(cs, IdCapacity, _capacity);
            cmd.SetComputeVectorParam(cs, IdGrid, new Vector4(_keyCell, 1f / Mathf.Max(0.1f, _keyLod), _keyOffset, s.levelFallback ? 1f : 0f));
            cmd.SetComputeVectorParam(cs, IdQuery, new Vector4(Mathf.Max(1f, s.minSamples), Mathf.Max(0f, s.queryJitter), 0, 0));
            return true;
        }

        private void BindResolve(CommandBuffer cmd)
        {
            foreach (int k in new[] { _kResolve, _kClear })
            {
                cmd.SetComputeBufferParam(_resolve, k, IdKeys, _keys);
                cmd.SetComputeBufferParam(_resolve, k, IdAccum, _accum);
                cmd.SetComputeBufferParam(_resolve, k, IdResolved, _resolved);
            }
        }

        private void EnsureBuffers()
        {
            var s = Settings;
            int capacity = 1 << Mathf.Clamp(s.capacityLog2, 12, 22);
            float cell = Mathf.Clamp(s.cellSize, 0.05f, 16f);
            float lod = Mathf.Clamp(s.lodDistance, 0.5f, 1000f);
            float offset = Mathf.Clamp01(s.normalOffset);
            if (cell != _keyCell || lod != _keyLod || offset != _keyOffset)
            {
                // Keys depend on these: every cell would be orphaned.
                _keyCell = cell; _keyLod = lod; _keyOffset = offset;
                _needsClear = true;
            }
            if (_keys != null && capacity == _capacity) return;
            ReleaseBuffers();
            _capacity = capacity;
            _keys = new ComputeBuffer(capacity, 4) { name = "UltraRT Cache keys" };
            _accum = new ComputeBuffer(capacity * 8, 4) { name = "UltraRT Cache accum" };
            _resolved = new ComputeBuffer(capacity, 16) { name = "UltraRT Cache resolved" };
            _needsClear = true;
        }

        private void ReleaseBuffers()
        {
            _keys?.Release(); _keys = null;
            _accum?.Release(); _accum = null;
            _resolved?.Release(); _resolved = null;
        }

        public void Dispose()
        {
            ReleaseBuffers();
            _dummyKeys?.Release();
            _dummyAccum?.Release();
            _dummyResolved?.Release();
            if (_dummyTex != null) _dummyTex.Release();
        }
    }
}
