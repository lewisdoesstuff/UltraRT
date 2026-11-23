using System;
using System.Globalization;
using System.Linq;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace UltraRT
{
    /// <summary>
    /// Radiance cache wiring (docs/radiance-cache.md). Kept in its own file so Plugin.cs only has one-line hooks:
    /// ParseCacheArgs (Awake), LoadCacheShaders (LoadShaders), _radianceCache.Reset (ResetScene), CacheHotkeys (OnUpdate),
    /// TraceRadianceCache (Trace, end of the PathTrace bind block), TraceCacheDebug (Trace, after DispatchRays).
    ///   -ultrart-nocache                 cache off (bounce hits take one RIS light sample, as before)
    ///   -ultrart-cache-cell N            base cell size in metres (default 0.5)
    ///   -ultrart-cache-mode bounce|primary|off
    ///   -ultrart-cache-lod N             LOD distance in metres (default 10)
    ///   -ultrart-cache-stride N          one update path per NxN trace pixels (default 4)
    ///   -ultrart-cache-history N         history cap in frames (default 32)
    ///   -ultrart-cache-feedback F        damping of the cached tail (default 0.8)
    ///   -ultrart-cache-size N            log2 of the hash table slots (default 20 = 52 MB)
    ///   -ultrart-cache-debug N           force the debug view of the cache-* view modes (1-5, RadianceCacheDebug)
    /// F3: cycle cache mode (bounce, primary, off). Shift+F3: cycle the forced debug view.
    /// View modes (F8): "cache" (cached radiance at primary hits) and "cache-cells" (cell hash colours).
    /// </summary>
    public partial class Plugin
    {
        // `mode` values of the cache debug entries in Modes: CacheDebugModeBase + RadianceCacheDebug.
        private const int CacheDebugModeBase = 100;

        private readonly RadianceCacheSettings _cacheSettings = new RadianceCacheSettings();
        private RadianceCache _radianceCache;

        private void ParseCacheArgs()
        {
            var a = Environment.GetCommandLineArgs();
            for (int i = 0; i < a.Length; i++)
            {
                string Next() => i + 1 < a.Length ? a[++i] : null;
                float F(float fallback) => float.TryParse(Next(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;
                int I(int fallback) => int.TryParse(Next(), out var v) ? v : fallback;
                var s = _cacheSettings;
                switch (a[i].ToLowerInvariant())
                {
                    case "-ultrart-nocache": s.enabled = false; break;
                    case "-ultrart-cache-cell": s.cellSize = F(s.cellSize); break;
                    case "-ultrart-cache-lod": s.lodDistance = F(s.lodDistance); break;
                    case "-ultrart-cache-stride": s.updateStride = I(s.updateStride); break;
                    case "-ultrart-cache-history": s.historyFrames = F(s.historyFrames); break;
                    case "-ultrart-cache-feedback": s.feedback = F(s.feedback); break;
                    case "-ultrart-cache-size": s.capacityLog2 = I(s.capacityLog2); break;
                    case "-ultrart-cache-debug": s.debugView = (RadianceCacheDebug)Mathf.Clamp(I(1), 1, 5); break;
                    case "-ultrart-cache-mode":
                        switch ((Next() ?? "").ToLowerInvariant())
                        {
                            case "off": s.mode = RadianceCacheMode.Off; break;
                            case "primary": s.mode = RadianceCacheMode.Primary; break;
                            default: s.mode = RadianceCacheMode.Bounce; break;
                        }
                        break;
                }
            }
        }

        private void LoadCacheShaders(AssetBundle bundle)
        {
            var update = bundle.LoadAllAssets<RayTracingShader>().FirstOrDefault(s => s.name == "CacheUpdate");
            var resolve = bundle.LoadAllAssets<ComputeShader>().FirstOrDefault(s => s.name == "CacheResolve");
            if (update == null || resolve == null)
                Logger.LogWarning($"Radiance cache shaders missing (CacheUpdate={(update ? "ok" : "MISSING")} CacheResolve={(resolve ? "ok" : "MISSING")}): cache off");
            if (_radianceCache == null) _radianceCache = new RadianceCache { Settings = _cacheSettings };
            _radianceCache.SetShaders(update, resolve);
        }

        // ---- Denoiser cache seed (docs/denoiser.md). Lives here so Plugin.cs needs no further hooks.

        /// <summary>
        /// Tell the denoiser which cache value matches its input this frame (the disocclusion seed): the denoised modes filter
        /// full lighting (_Mode 0: cache total + the primary ambient), plus the bounce-hit ambient the cache leaves out,
        /// approximated with albedo 0.5.
        /// </summary>
        private void ConfigureDenoiserCacheSeed(Color ambient)
        {
            if (_denoiser == null) return;
            float ambientScale = 2f * (_relightActive ? 1f - Mathf.Clamp01(RelightParams.w) : 1f); // PathTrace's _Ambient * RelightAmbientScale()
            var amb = new Vector3(ambient.r, ambient.g, ambient.b) * ambientScale;
            _denoiser.CacheSource = _radianceCache;
            _denoiser.CacheChannel = DenoiserCacheChannel.Total;
            _denoiser.CacheOffset = amb * (_primaryAmbient + 0.5f);
        }

        /// <summary>F3, Shift+F3: developer keys ([View] DevHotkeys or a bench launch; Plugin.Hotkeys.cs).</summary>
        private void CacheHotkeys()
        {
            if (!DevKey(Input.GetKeyDown(KeyCode.F3), "F3")) return;
            if (!Input.GetKeyDown(KeyCode.F3) || _radianceCache == null) return;
            var s = _cacheSettings;
            if (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift))
            {
                s.debugView = (RadianceCacheDebug)((int)s.debugView % 5 + 1);
                Logger.LogInfo($"Radiance cache debug view: {s.debugView}");
                return;
            }
            // bounce -> primary -> off -> bounce
            s.enabled = true;
            s.mode = s.mode == RadianceCacheMode.Bounce ? RadianceCacheMode.Primary
                   : s.mode == RadianceCacheMode.Primary ? RadianceCacheMode.Off : RadianceCacheMode.Bounce;
            _denoiser?.Reset();
            Logger.LogInfo($"Radiance cache: {s.mode}");
        }

        /// <summary>
        /// Bind the cache on PathTrace (its paths update it, folded). Call inside Trace()'s PathTrace bind block, before
        /// PathTrace's DispatchRays; <see cref="ResolveRadianceCache"/> runs after it.
        /// </summary>
        private bool _cacheDue;

        private void TraceRadianceCache(RayTracingShader pathTrace, Color ambient)
        {
            ConfigureDenoiserCacheSeed(ambient);
            _cacheDue = false;
            if (_radianceCache == null) return;
            if (_radianceCache.Active && (_lightSampling.LightSetChanged || _emissive.SetChanged)) _radianceCache.NotifyLightsChanged();
            bool due = _radianceCache.Due(_frameIndex, _auxEvery);
            _radianceCache.Execute(_cmd, pathTrace, due);
            _cacheDue = due && _radianceCache.Active;
        }

        /// <summary>Run CacheResolve.compute right after PathTrace's DispatchRays.</summary>
        private void ResolveRadianceCache()
        {
            if (!_cacheDue || _radianceCache == null) return;
            _gpu?.Begin(_cmd, SlotCache);
            _radianceCache.Resolve(_cmd);
            _gpu?.End(_cmd, SlotCache);
            _cacheDue = false;
        }

        /// <summary>The "cache" view mode: overwrite the PathTrace output with the cache debug view.</summary>
        private void TraceCacheDebug(int mode, Matrix4x4 invViewProj)
        {
            if (mode < CacheDebugModeBase || _radianceCache == null) return;
            var view = _cacheSettings.debugView != RadianceCacheDebug.None ? _cacheSettings.debugView : (RadianceCacheDebug)(mode - CacheDebugModeBase);
            if (_radianceCache.Active && _radianceCache.UpdateShader != null)
            {
                var u = _radianceCache.UpdateShader;
                _cmd.SetRayTracingAccelerationStructure(u, "_Scene", _proxies.Rtas);
                _cmd.SetRayTracingVectorParam(u, "_Relight", RelightParams);
            }
            _radianceCache.DispatchDebug(_cmd, _output, view, invViewProj, _camera.transform.position);
        }
    }
}
