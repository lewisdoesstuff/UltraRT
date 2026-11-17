using System;
using System.Globalization;
using UnityEngine;

namespace UltraRT
{
    /// <summary>
    /// Effect lights wiring (EffectLights.cs, docs/emissive-lights.md "Effect lights"); kept out of Plugin.cs, which only
    /// calls <see cref="UpdateEffects"/> from OnUpdate and <see cref="AppendEffectLights"/> at the end of GatherLights.
    /// Flags: -ultrart-noeffectlights, -ultrart-nospawnedlights, -ultrart-novirtuallights, -ultrart-effect-scale x,
    /// -ultrart-noflicker, -ultrart-effect-max n, -ultrart-effect-hybrid x, -ultrart-effectsurvey (diagnostic; costs ms).
    /// </summary>
    public partial class Plugin
    {
        private EffectLights _effectLights;
        // Effects in reflections (EffectGeometry.cs, docs/path-tracing.md): bench prefix fxg., -ultrart-noeffectgeometry.
        private EffectGeometry _effectGeometry;
        private readonly EffectGeometrySettings _fxgSettings = new EffectGeometrySettings();
        private Shader _effectProxyShader;
        private readonly EffectLightSettings _effectSettings = new EffectLightSettings();
        private readonly int[] _lightIds = new int[MaxLights];
        private EffectSurvey _effectSurvey;
        private bool _effectArgsParsed, _effectSurveyEnabled, _effectLightsEnabled = true;

        private void ParseEffectArgs()
        {
            _effectArgsParsed = true;
            var a = Environment.GetCommandLineArgs();
            for (int i = 0; i < a.Length; i++)
            {
                string Next() => i + 1 < a.Length ? a[++i] : null;
                float F(float def) => float.TryParse(Next(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : def;
                switch (a[i].ToLowerInvariant())
                {
                    case "-ultrart-effectsurvey": _effectSurveyEnabled = true; break;
                    case "-ultrart-noeffectlights": _effectLightsEnabled = false; break;
                    case "-ultrart-noeffectgeometry": _fxgSettings.enabled = false; break;
                    case "-ultrart-nospawnedlights": _effectSettings.spawnedLights = false; break;
                    case "-ultrart-novirtuallights": _effectSettings.virtualLights = false; break;
                    case "-ultrart-noflicker": _effectSettings.flicker = false; break;
                    case "-ultrart-effect-scale": _effectSettings.intensityScale = F(_effectSettings.intensityScale); break;
                    case "-ultrart-effect-max": _effectSettings.maxVirtual = (int)F(_effectSettings.maxVirtual); break;
                    case "-ultrart-effect-hybrid": _effectSettings.hybridWeight = F(_effectSettings.hybridWeight); break;
                }
            }
            if (!_effectLightsEnabled) { _effectSettings.spawnedLights = false; _effectSettings.virtualLights = false; _effectSettings.flicker = false; }
            Logger.LogInfo($"Effect lights: spawned {(_effectSettings.spawnedLights ? "on" : "off")}, virtual {(_effectSettings.virtualLights ? "on" : "off")}, flicker {(_effectSettings.flicker ? "on" : "off")} " +
                           $"(max {_effectSettings.maxVirtual}, scale {_effectSettings.intensityScale}, hybrid {_effectSettings.hybridWeight}){(_effectSurveyEnabled ? ", survey on" : "")}");
        }

        /// <summary>Every frame, from OnUpdate: drains the spawn queue (and runs the survey, if enabled).</summary>
        private void UpdateEffects()
        {
            if (!_effectArgsParsed) ParseEffectArgs();
            if (_effectLights == null)
            {
                _effectLights = new EffectLights(Logger) { Settings = _effectSettings, Spawned = VfxOnSpawn };
                _effectGeometry = new EffectGeometry(Logger) { Settings = _fxgSettings };
                _effectGeometry.SetShader(_effectProxyShader);
                _effectLights.GlowSink = _effectGeometry.Track;
                if (_lightSampling != null) _lightSampling.VirtualIds = _lightIds;
            }
            if (_effectSurveyEnabled && _effectSurvey == null) _effectSurvey = new EffectSurvey(Logger);
            EffectSpawns.Enabled = _effectSettings.spawnedLights || _effectSettings.virtualLights || _effectSettings.flicker || _effectSurvey != null ||
                                   (_vfx != null && _vfxSettings.enabled && (_vfxSettings.shockwaves || _vfxSettings.debrisGlow));
            // Without a traced frame (view off, menus) nothing updates the effect instances: tell the shaders there are none.
            if (!_viewEnabled && !_cinePairedLive && _effectGeometry != null && _effectGeometry.Active > 0) _effectGeometry.Update(null, null);
            _effectLights.ProcessSpawns(_effectSurvey);
            if (_effectSurvey != null)
            {
                try { _effectSurvey.Update(_camera); }
                catch (Exception e) { Logger.LogWarning($"effect survey: {e.Message}"); }
            }
        }

        /// <summary>LoadShaders: the billboards' hit shader (optional: without it effects stay out of reflections).</summary>
        private void LoadEffectShaders(Shader[] shaders)
        {
            _effectProxyShader = System.Linq.Enumerable.FirstOrDefault(shaders, s => s.name == "UltraRT/EffectProxy");
            if (_effectProxyShader == null) Logger.LogWarning("UltraRT/EffectProxy missing from the bundle: effects are not traced");
            _effectGeometry?.SetShader(_effectProxyShader);
        }

        /// <summary>Trace(), before the RTAS build: this frame's effect billboards (EffectGeometry.cs).</summary>
        private void UpdateEffectGeometry(bool pt)
        {
            if (_effectGeometry == null) return;
            long t0 = Perf.Now;
            _effectGeometry.Update(pt ? _proxies.Rtas : null, _camera);
            Perf.Add(Perf.Sec.Emissive, t0);
        }

        /// <summary>End of GatherLights, before the upload: effect lights go after the scene lights.</summary>
        private void AppendEffectLights()
        {
            if (_effectLights == null || _camera == null || (!_effectSettings.spawnedLights && !_effectSettings.virtualLights && !_effectSettings.flicker)) return;
            _effectLights.ProcessSpawns(_effectSurvey); // spawned since OnUpdate (coroutines, LateUpdate): no frame of delay
            _lightCount = _effectLights.Append(_proxies, _activeLights, _activeLightsVersion, _lightData, _lightMasks, _packedLights, _lightIds, _lightCount, MaxLights, _camera.transform.position);
        }
    }
}
