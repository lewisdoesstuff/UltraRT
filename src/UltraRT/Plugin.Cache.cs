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
    ///   -ultrart-cache-bounces N         update path bounces (1-4, default 2)
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
                    case "-ultrart-cache-bounces": s.bounces = I(s.bounces); break;
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

        // ---- Denoiser stability (docs/denoiser.md). Lives here so Plugin.cs needs no further hooks.

        /// <summary>
        /// Tell the denoisers which cache value matches their input this frame (the disocclusion seed). Hybrid's main
        /// denoiser filters the bounce only (PathTrace _Mode 3: cache indirect); the other denoised modes filter full
        /// lighting (_Mode 0: cache total + the primary ambient). Both add the bounce-hit ambient the cache leaves out,
        /// approximated with albedo 0.5. Hybrid's direct denoiser filters a luminance pair the cache has no equivalent for.
        /// </summary>
        private void ConfigureDenoiserCacheSeed(Color ambient)
        {
            if (_denoiser == null) return;
            bool hybrid = Modes[_mode].kind == Kind.Hybrid;
            float ambientScale = 2f * (_relightActive ? 1f - Mathf.Clamp01(RelightParams.w) : 1f); // PathTrace's _Ambient * RelightAmbientScale()
            var amb = new Vector3(ambient.r, ambient.g, ambient.b) * ambientScale;
            _denoiser.CacheSource = _radianceCache;
            _denoiser.CacheChannel = hybrid ? DenoiserCacheChannel.Indirect : DenoiserCacheChannel.Total;
            _denoiser.CacheOffset = amb * ((hybrid ? 0f : _primaryAmbient) + 0.5f);
            if (_denoiserDirect != null) _denoiserDirect.CacheChannel = DenoiserCacheChannel.None;
        }

        /// <summary>Ctrl+F3: cycle the denoiser presets (DenoiserPreset): Full -> Static (the build before the motion-noise work)
        /// -> Previous (the build before the static-noise work, also without the guard band and hybrid's ReSTIR spatial reuse)
        /// -> Legacy (plain SVGF) -> Full.</summary>
        private bool DenoiserStabilityHotkey()
        {
            if (!Input.GetKeyDown(KeyCode.F3) || !(Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl))) return false;
            if (_denoiser == null) return true;
            var next = _denoiser.Settings.CurrentPreset() switch
            {
                DenoiserPreset.Full => DenoiserPreset.Static,
                DenoiserPreset.Static => DenoiserPreset.Previous,
                DenoiserPreset.Previous => DenoiserPreset.Legacy,
                _ => DenoiserPreset.Full,
            };
            ApplyStabilityPreset(next);
            string name = next == DenoiserPreset.Full ? "full" : next == DenoiserPreset.Static ? "before the motion work"
                : next == DenoiserPreset.Previous ? "previous build" : "off (legacy SVGF)";
            Logger.LogInfo($"Denoiser stability: {name}");
            Toast($"Denoiser stability: {name} (Ctrl+F3)");
            return true;
        }

        /// <summary>A DenoiserPreset on both denoisers plus the parts that live elsewhere: the guard band, hybrid's ReSTIR
        /// spatial reuse (static-noise work) and LightSampling's motion features (per-pixel light-change history, extra
        /// candidates for fresh pixels).</summary>
        private void ApplyStabilityPreset(DenoiserPreset p)
        {
            _denoiser?.Settings.ApplyPreset(p);
            _denoiserDirect?.Settings.ApplyPreset(p);
            bool staticWork = p == DenoiserPreset.Full || p == DenoiserPreset.Static;
            bool motion = p == DenoiserPreset.Full;
            var d = new LightSamplingSettings();
            _guardActive = staticWork;
            _lightSamplingSettings.spatialSamplesHybrid = staticWork ? d.spatialSamplesHybrid : 0;
            _lightSamplingSettings.perPixelLightChange = motion && d.perPixelLightChange;
            _lightSamplingSettings.freshCandidates = motion ? d.freshCandidates : 0;
        }

        private void CacheHotkeys()
        {
            if (DenoiserStabilityHotkey()) return;
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
        /// Record the cache update + resolve and bind the lookups on PathTrace. Call inside Trace()'s PathTrace bind block,
        /// after LightSampling.Bind and EmissiveLights.Bind for PathTrace (EmissiveLights' history flag is consumed by its
        /// first Bind of the frame, which must be PathTrace's), before PathTrace's DispatchRays.
        /// </summary>
        private void TraceRadianceCache(RayTracingShader pathTrace, int w, int h, Matrix4x4 invViewProj, Color ambient)
        {
            ConfigureDenoiserCacheSeed(ambient);
            if (_radianceCache == null) return;
            if (_radianceCache.Active)
            {
                var u = _radianceCache.UpdateShader;
                _cmd.SetRayTracingAccelerationStructure(u, "_Scene", _proxies.Rtas);
                _cmd.SetRayTracingBufferParam(u, "_Lights", _lightBuffer);
                _cmd.SetRayTracingBufferParam(u, "_LightMasks", _lightMaskBuffer);
                _cmd.SetRayTracingIntParam(u, "_LightCount", _lightCount);
            _cmd.SetRayTracingFloatParam(u, "_LightRadius", _lightRadius);
            _cmd.SetRayTracingVectorParam(u, "_Relight", RelightParams);
            _cmd.SetRayTracingFloatParam(u, "_FixtureShadows", _fixtureShadows ? 1f : 0f);
                _lightSampling.BindTables(_cmd, u);
                _emissive.Bind(_cmd, u, _lightSamplingSettings.mode);
                _cmd.SetRayTracingVectorParam(u, "_SkyColor", new Vector4(ambient.r, ambient.g, ambient.b, 1));
                BindSky(u);
                if (_lightSampling.LightSetChanged || _emissive.SetChanged) _radianceCache.NotifyLightsChanged();
            }
            _gpu?.Begin(_cmd, SlotCache);
            _radianceCache.Execute(_cmd, pathTrace, w, h, invViewProj, _camera.transform.position, _frameIndex, 1f, 1f);
            _gpu?.End(_cmd, SlotCache);
        }

        /// <summary>The "cache" view mode: overwrite the PathTrace output with the cache debug view.</summary>
        private void TraceCacheDebug(int mode)
        {
            if (mode < CacheDebugModeBase || _radianceCache == null) return;
            var view = _cacheSettings.debugView != RadianceCacheDebug.None ? _cacheSettings.debugView : (RadianceCacheDebug)(mode - CacheDebugModeBase);
            _radianceCache.DispatchDebug(_cmd, _output, view);
        }
    }
}
