using System;
using System.Globalization;
using UnityEngine;

namespace UltraRT
{
    /// <summary>
    /// Effect lights wiring (EffectLights.cs, docs/emissive-lights.md "Effect lights"); kept out of Plugin.cs, which only
    /// calls <see cref="UpdateEffects"/> from OnUpdate and <see cref="AppendEffectLights"/> at the end of GatherLights.
    /// Flags: -ultrart-noeffectlights, -ultrart-nospawnedlights, -ultrart-novirtuallights, -ultrart-effect-scale x,
    /// -ultrart-noflicker, -ultrart-effect-max n, -ultrart-effectsurvey (diagnostic; costs ms); combat lights
    /// (CombatLights.cs): -ultrart-beam-lights x, -ultrart-parry-light x, -ultrart-lightning-flash x (strengths, 0 = off).
    /// </summary>
    public partial class Plugin
    {
        private EffectLights _effectLights;
        private CombatLights _combatLights;
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
                    case "-ultrart-beam-lights": _effectSettings.beamLights = Mathf.Max(0f, F(_effectSettings.beamLights)); break;
                    case "-ultrart-parry-light": _effectSettings.parryLight = Mathf.Max(0f, F(_effectSettings.parryLight)); break;
                    case "-ultrart-lightning-flash": _effectSettings.lightningFlash = Mathf.Max(0f, F(_effectSettings.lightningFlash)); break;
                }
            }
            if (!_effectLightsEnabled)
            {
                _effectSettings.spawnedLights = false; _effectSettings.virtualLights = false; _effectSettings.flicker = false;
                _effectSettings.beamLights = 0f; _effectSettings.parryLight = 0f; _effectSettings.lightningFlash = 0f;
            }
            Logger.LogInfo($"Effect lights: spawned {(_effectSettings.spawnedLights ? "on" : "off")}, virtual {(_effectSettings.virtualLights ? "on" : "off")}, flicker {(_effectSettings.flicker ? "on" : "off")} " +
                           $"(max {_effectSettings.maxVirtual}, scale {_effectSettings.intensityScale}){(_effectSurveyEnabled ? ", survey on" : "")}; " +
                           $"combat: beams {_effectSettings.beamLights:F2}, parry {_effectSettings.parryLight:F2}, lightning {_effectSettings.lightningFlash:F2} " +
                           $"(hooks: {(CombatHooks.Hooked.Length > 0 ? CombatHooks.Hooked : "none")}{(CombatHooks.Errors.Length > 0 ? "; errors:" + CombatHooks.Errors : "")})");
        }

        /// <summary>Every frame, from OnUpdate: drains the spawn queue (and runs the survey, if enabled).</summary>
        private void UpdateEffects()
        {
            if (!_effectArgsParsed) ParseEffectArgs();
            if (_effectLights == null)
            {
                _combatLights = new CombatLights(Logger, _effectSettings);
                _effectLights = new EffectLights(Logger) { Settings = _effectSettings, Spawned = OnEffectSpawned };
                _effectGeometry = new EffectGeometry(Logger) { Settings = _fxgSettings };
                _effectGeometry.SetShader(_effectProxyShader);
                _effectLights.GlowSink = _effectGeometry.Track;
                if (_lightSampling != null) _lightSampling.VirtualIds = _lightIds;
            }
            if (_effectSurveyEnabled && _effectSurvey == null) _effectSurvey = new EffectSurvey(Logger);
            CombatHooks.Parry = _effectSettings.parryLight > 0f;
            CombatHooks.Lightning = _effectSettings.lightningFlash > 0f;
            EffectSpawns.Enabled = _effectSettings.spawnedLights || _effectSettings.virtualLights || _effectSettings.flicker || _effectSurvey != null ||
                                   _effectSettings.beamLights > 0f ||
                                   (_vfx != null && _vfxSettings.enabled && (_vfxSettings.shockwaves || _vfxSettings.debrisGlow));
            // Without a traced frame (view off, menus) nothing updates the effect instances: tell the shaders there are none.
            if (!_viewEnabled && !_cinePairedLive && _effectGeometry != null && _effectGeometry.Active > 0) _effectGeometry.Update(null, null);
            _effectLights.ProcessSpawns(_effectSurvey);
            try { _combatLights.Update(); }
            catch (Exception e) { Logger.LogWarning($"Combat lights: {e.Message}"); }
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

        /// <summary>Every spawned hierarchy, as the queue drains: screen effects (Plugin.Vfx.cs) and combat beams.</summary>
        private void OnEffectSpawned(GameObject go)
        {
            VfxOnSpawn(go);
            WaterImpulseOnSpawn(go);
            _combatLights?.OnSpawn(go);
        }

        /// <summary>Slots kept free after the combat lights for the enemy glow and portal images appended after them.</summary>
        private const int CombatLightReserve = 8;

        /// <summary>End of GatherLights, before the upload: effect lights go after the scene lights, combat lights
        /// (CombatLights.cs) after those, so beams and flashes never take an explosion's or a muzzle flash's slot.</summary>
        private void AppendEffectLights()
        {
            if (_effectLights == null || _camera == null) return;
            bool fx = _effectSettings.spawnedLights || _effectSettings.virtualLights || _effectSettings.flicker;
            bool combat = _effectSettings.beamLights > 0f || _effectSettings.parryLight > 0f || _effectSettings.lightningFlash > 0f;
            if (!fx && !combat) return;
            _effectLights.ProcessSpawns(_effectSurvey); // spawned since OnUpdate (coroutines, LateUpdate): no frame of delay
            if (fx) _lightCount = _effectLights.Append(_proxies, _activeLights, _activeLightsVersion, _lightData, _lightMasks, _packedLights, _lightIds, _lightCount, MaxLights, _camera.transform.position);
            if (combat && _combatLights != null)
                _lightCount = _combatLights.Append(_lightData, _lightMasks, _packedLights, _lightIds, _lightMedium, _lightCount, MaxLights, CombatLightReserve, _camera.transform.position);
        }

        /// <summary>Auto exposure (Plugin.Exposure.cs, Plugin.Dlss.cs): a brief combat flash is lighting the frame, so the
        /// exposure holds still instead of darkening for it and creeping back (fx.flashHoldsExposure).</summary>
        private bool CombatFlashHoldsExposure() => _combatLights != null && _combatLights.HoldExposure;
    }
}
