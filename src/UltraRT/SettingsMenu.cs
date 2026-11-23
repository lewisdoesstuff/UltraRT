using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using BepInEx.Logging;
using UnityEngine;

namespace UltraRT
{
    /// <summary>
    /// In-game settings (docs/settings.md). Every setting is a Get/Set pair over the plugin's own fields, the same fields the
    /// hotkeys flip and the command-line flags fill, so there is one source of truth and nothing to keep in sync by hand:
    ///  - startup: the BepInEx config entry (BepInEx/config/ooo.lew.ultrart.cfg) is applied, unless a command-line flag for that
    ///    setting is present (flags still win, for benchmarks);
    ///  - every frame <see cref="SettingsMenu.Update"/> compares the live values with what the menu shows, so a hotkey (or a
    ///    bench variant) moves the menu entry;
    ///  - a change made in the menu calls Set (the same side effects as the hotkey) and writes the config entry. Hotkeys and
    ///    flags are session-only: they never write the config.
    /// The menu itself is PluginConfigurator's options-menu page (a soft dependency): it is only created when that plugin is
    /// loaded, and everything that touches its types lives in <see cref="SettingsMenuUi"/>, which nothing else references.
    /// Plugin.cs has three hooks: InitSettings + AttachSettings (Awake) and _menu.Update (OnUpdate).
    /// </summary>
    public partial class Plugin
    {
        internal const string PluginConfiguratorGuid = "com.eternalUnion.pluginConfigurator";

        private SettingsMenu _menu;

        // Mirrors Plugin.Modes by name (PascalCase of the mode name: "pt-dn" -> PtDn). A mode without a member is left out of the menu.
        internal enum ViewModeChoice
        {
            PtDn, Relight, Pathtrace, DnLighting, DnVariance, DnHistory, Pt, PtDirect, PtIndirect, Lit, Albedo, Normals, Depth, Instance, Uv,
            Emission, Primitive, Refl, ReflRaw, ReflSpec, ReflHistory, Materials, SpecParams, Cache, CacheCells, DnBoost, Motion,
        }

        internal enum TraceQuality { Performance, Balanced, Quality }
        internal enum PathTraceScale { UltraPerformance, Performance, Balanced }

        /// <summary>First thing in Awake, before any config entry or flag changes a field: registers the settings and remembers
        /// the code defaults.</summary>
        private void InitSettings()
        {
            var m = _menu = new SettingsMenu(Logger, Config);

            // View mode <-> Modes index, by name.
            var modeOf = new Dictionary<ViewModeChoice, int>();
            for (int i = 0; i < Modes.Length; i++)
                if (Enum.TryParse<ViewModeChoice>(string.Concat(Modes[i].name.Split('-').Select(p => char.ToUpperInvariant(p[0]) + p.Substring(1))), out var c)) modeOf[c] = i;
            if (modeOf.Count != Modes.Length || modeOf.Count != Enum.GetValues(typeof(ViewModeChoice)).Length)
                Logger.LogWarning($"Settings menu: ViewModeChoice and Plugin.Modes differ ({Enum.GetValues(typeof(ViewModeChoice)).Length} vs {Modes.Length}); modes without a match are not in the menu");

            m.Group = "View";
            m.Bool("View", "Enabled", "Ray tracing  [F7]", () => _viewEnabled, v =>
            {
                _viewEnabled = v;
                _denoiser?.Reset(); _lightSampling?.Reset(); _reflFilter?.Reset();
            }, "-ultrart-off").SessionOnly().Basic();
            m.Choice("View", "DefaultMode", "View mode  [PgUp/PgDn]",
                () => { foreach (var kv in modeOf) if (kv.Value == _mode) return kv.Key; return ViewModeChoice.Relight; },
                v => { if (modeOf.TryGetValue(v, out int i)) _mode = i; },
                v => modeOf.TryGetValue(v, out int i) ? Modes[i].name : v.ToString(),
                v => v switch
                {
                    ViewModeChoice.Relight => "relight (full RT)",
                    ViewModeChoice.Pathtrace => "pathtrace (multi-bounce)",
                    ViewModeChoice.PtDn => "pt-dn (path traced)",
                    _ => modeOf.TryGetValue(v, out int i) ? Modes[i].name : v.ToString(),
                }, "-ultrart-view").Legacy().Basic();
            m.Bool("View", "GameDither", "The game's screen dither while ray traced", () => _gameDither, v => _gameDither = v, "-ultrart-game-dither").Basic();
            // Player-safe hotkeys (Plugin.Hotkeys.cs, docs/settings.md "Hotkeys for players and developers"). Both are always on in
            // a bench launch, whatever these say.
            m.Bool("View", "DebugModes", "Debug views in the Page Up / Page Down cycle", () => _debugModes, v => _debugModes = v, "-ultrart-debug-modes");
            m.Bool("View", "DevHotkeys", "Developer hotkeys (F2, F3, F6, F9, F10, F11) and shader hot reload", () => _devHotkeys, v => _devHotkeys = v, "-ultrart-dev-hotkeys");

            m.Group = "Quality and performance";
            m.Choice("Quality", "Preset", "Quality preset",
                DetectQualityPreset,
                v => { if (v != QualityPreset.Custom) ApplyQualityPreset(v); },
                v => v.ToString(),
                v => v == QualityPreset.Ultra ? "Ultra (0.67x, 3 bounces)"
                    : v == QualityPreset.High ? "High (default)"
                    : v == QualityPreset.Performance ? "Performance (1 bounce, single-lobe)"
                    : v == QualityPreset.Competitive ? "Competitive (max FPS)"
                    : "Custom",
                "-ultrart-preset").SessionOnly().Basic();
            m.Choice("Quality", "TraceScale", "Trace resolution",
                () => _scale < 0.585f ? TraceQuality.Performance : _scale < 0.835f ? TraceQuality.Balanced : TraceQuality.Quality,
                v =>
                {
                    _scale = v == TraceQuality.Performance ? 0.5f : v == TraceQuality.Balanced ? 0.67f : 1f;
                    _denoiser?.Reset(); _lightSampling?.Reset(); _reflFilter?.Reset();
                },
                v => v.ToString(),
                v => v == TraceQuality.Performance ? "Performance (0.5x)" : v == TraceQuality.Balanced ? "Balanced (0.67x)" : "Quality (1x)", "-ultrart-scale").Basic();
            m.Choice("Quality", "RadianceCache", "Radiance cache  [F3]",
                () => _cacheSettings.enabled ? _cacheSettings.mode : RadianceCacheMode.Off,
                v => { _cacheSettings.enabled = true; _cacheSettings.mode = v; _denoiser?.Reset(); },
                v => v.ToString(), v => v.ToString(), "-ultrart-nocache", "-ultrart-cache-mode");
            // Key renamed from "LightSampling" when the default went from ReSTIR to RIS (docs/light-sampling.md, "Bias and
            // noise, 2026-11-15"): config files keep the default they were first written with, so the old key pinned ReSTIR.
            m.Choice("Quality", "LightSampler", "Light sampling  [F6]",
                () => _lightSamplingSettings.mode,
                v => { _lightSamplingSettings.mode = v; _lightSampling?.Reset(); },
                v => v.ToString(),
                v => v == LightSamplingMode.Reference ? "Reference (every light, slow)" : "RIS", "-ultrart-lights");

            m.Group = "Path tracing (view mode pathtrace)";
            m.Choice("PathTrace", "Denoiser", "Denoiser",
                () => _ptSettings.denoiser,
                v => { _ptSettings.denoiser = v; _denoiser?.Reset(); _lightSampling?.Reset(); _reflFilter?.Reset(); },
                v => v.ToString(),
                v => v == PathTraceDenoiser.Auto ? "Auto (DLSS RR if available)" : v == PathTraceDenoiser.UltraRT ? "UltraRT denoiser" : "DLSS Ray Reconstruction",
                "-ultrart-pt-denoiser").Basic();
            m.Choice("PathTrace", "ScaleRR", "Render resolution with DLSS RR",
                () => _ptSettings.scaleRR < 0.42f ? PathTraceScale.UltraPerformance : _ptSettings.scaleRR < 0.585f ? PathTraceScale.Performance : PathTraceScale.Balanced,
                v => _ptSettings.scaleRR = v == PathTraceScale.UltraPerformance ? 1f / 3f : v == PathTraceScale.Performance ? 0.5f : 0.67f,
                v => v.ToString(),
                v => v == PathTraceScale.UltraPerformance ? "Ultra performance (1/3)" : v == PathTraceScale.Performance ? "Performance (1/2)" : "Balanced (0.67x)",
                "-ultrart-pt-scale").Basic();
            m.Choice("PathTrace", "Scale", "Render resolution with UltraRT's denoiser",
                () => _ptSettings.scale < 0.42f ? PathTraceScale.UltraPerformance : _ptSettings.scale < 0.585f ? PathTraceScale.Performance : PathTraceScale.Balanced,
                v => { _ptSettings.scale = v == PathTraceScale.UltraPerformance ? 1f / 3f : v == PathTraceScale.Performance ? 0.5f : 0.67f; _denoiser?.Reset(); _lightSampling?.Reset(); },
                v => v.ToString(),
                v => v == PathTraceScale.UltraPerformance ? "1/3 (with exact texels)" : v == PathTraceScale.Performance ? "1/2" : "0.67x").Basic();
            m.Bool("PathTrace", "Remodulate", "UltraRT's denoiser: exact texels (remodulation)", () => _ptSettings.remodulate, v => _ptSettings.remodulate = v);
            m.Float("PathTrace", "Paths", "Paths per pixel", 1f, 4f, 0, () => _ptSettings.paths, v => _ptSettings.paths = Mathf.Clamp(Mathf.RoundToInt(v), 1, 4), "-ultrart-pt-paths");
            m.Float("PathTrace", "Bounces", "Bounces per path", 1f, 4f, 0, () => _ptSettings.bounces, v => _ptSettings.bounces = Mathf.Clamp(Mathf.RoundToInt(v), 1, 4), "-ultrart-pt-bounces");
            m.Float("PathTrace", "DirectSamples", "Light samples per pixel (DLSS RR)", 1f, 4f, 0, () => _ptSettings.directSamplesRR, v => _ptSettings.directSamplesRR = Mathf.Clamp(Mathf.RoundToInt(v), 1, 4));
            m.Bool("PathTrace", "CacheTail", "Radiance cache after the last bounce", () => _ptSettings.cacheTail, v => _ptSettings.cacheTail = v, "-ultrart-pt-nocache");
            m.Float("PathTrace", "Footprint", "End wide paths in the cache (cells; 0 = off)", 0f, 8f, 2, () => _ptSettings.footprint, v => _ptSettings.footprint = Mathf.Max(0f, v), "-ultrart-pt-footprint");
            m.Bool("PathTrace", "SkyAtVertices", "Sky sample at every bounce", () => _ptSettings.skyAtVertices, v => _ptSettings.skyAtVertices = v);
            m.Bool("DLSS", "RayReconstruction", "DLSS RR in relight / pt-dn  [Ctrl+F8]", () => _rrEnabled, v => _rrEnabled = v, "-ultrart-rr", "-ultrart-norr").Legacy().Basic();
            m.Bool("DLSS", "Remodulate", "DLSS RR: exact texels (remodulation)", () => _rrRemodulate, v => _rrRemodulate = v, "-ultrart-rr-noremod");

            m.Group = "Lighting";
            m.Float("Relight", "LightReference", "Light reference", 0.05f, 1f, 2, () => _relightRef, v => { _relightRef = v; _radianceCache?.NotifyLightsChanged(); }, "-ultrart-relight-ref");
            m.Float("Relight", "Exposure", "Light exposure", 0.25f, 4f, 2, () => _relightExposure, v => _relightExposure = v, "-ultrart-relight-exposure");
            m.Float("Relight", "FillCut", "Fill light cut", 0f, 1f, 2, () => _relightFillCut, v => _relightFillCut = v, "-ultrart-relight-fill");
            m.Bool("Relight", "SkyGI", "Sky + linear light  [Ctrl+F5]", () => _skyGI, v => { _skyGI = v; _denoiser?.Reset(); _lightSampling?.Reset(); });
            m.Bool("Relight", "SkySampling", "Sample the sky directly", () => _skySampling, v => { _skySampling = v; _denoiser?.Reset(); _lightSampling?.Reset(); }, "-ultrart-sky-sampling");
            m.Float("Relight", "SkyScale", "Sky light", 0f, 3f, 2, () => _skyScale, v => _skyScale = v, "-ultrart-sky-scale");
            m.Float("Relight", "HighlightHue", "Hot reds turn orange, not pink (0 = off)", 0f, 1f, 2, () => _tonemapHue, v => _tonemapHue = Mathf.Clamp01(v), "-ultrart-relight-hue");
            m.Bool("Lighting", "EmissiveLights", "Emissive lighting  [F5]", () => _emissiveSettings.enabled, v =>
            {
                _emissiveSettings.enabled = v;
                _lightSampling?.Reset(); _denoiser?.Reset();
            }, "-ultrart-noemissive");
            m.Float("Lighting", "EmissiveScale", "Emissive brightness", 0f, 8f, 2, () => _emissiveSettings.lightScale, v => _emissiveSettings.lightScale = v, "-ultrart-emissive-scale");
            m.Bool("Lighting", "PortalLights", "Lights shine through portals (layer 8)", () => _portalLights, v => _portalLights = v, "-ultrart-portal-lights");

            m.Group = "Auto exposure";
            m.Bool("Relight", "AutoExposure", "Auto exposure  [Ctrl+F4]", () => _aeSettings.enabled, v => { _aeSettings.enabled = v; _autoExposure?.Reset(); }, "-ultrart-relight-autoexposure").Legacy();
            m.Float("Relight", "RtBrightness", "RT brightness (0 = as the game)", 0f, 1f, 2, () => _aeSettings.rtBrightness, v => _aeSettings.rtBrightness = Mathf.Clamp01(v), "-ultrart-rt-brightness").Basic();
            m.Float("Relight", "AutoExposureKey", "Target brightness", 0.1f, 1f, 2, () => _aeSettings.key, v => _aeSettings.key = v, "-ultrart-ae-key");
            m.Float("Relight", "AutoExposureMin", "Darkest exposure (EV)", -6f, 0f, 1, () => _aeSettings.minEV, v => _aeSettings.minEV = v, "-ultrart-ae-min");
            m.Float("Relight", "AutoExposureMax", "Brightest exposure (EV)", 0f, 3f, 1, () => _aeSettings.maxEV, v => _aeSettings.maxEV = v, "-ultrart-ae-max");

            m.Group = "Effects";
            m.Bool("Volumetrics", "Enabled", "Volumetric light and fog  [Ctrl+F6]", () => _volSettings.enabled, v => { _volSettings.enabled = v; _volumetrics?.Reset(); }, "-ultrart-volumetrics").Legacy().Basic();
            m.Float("Volumetrics", "Density", "Fog density", 0f, 4f, 2, () => _volSettings.densityScale, v => _volSettings.densityScale = v, "-ultrart-vol-density");
            // Key renamed from "Strength" when the default went from 1 to 0.65 (docs/volumetrics.md, "Balance"): config files
            // keep the default they were first written with, so the old key would have pinned 1.
            m.Float("Volumetrics", "ShaftStrength", "Light shaft strength", 0f, 4f, 2, () => _volSettings.strength, v => _volSettings.strength = v, "-ultrart-vol-strength");
            m.Bool("Materials", "Enabled", "Surface detail maps  [Ctrl+F1]", () => MaterialMaps.Settings.enabled, v => MaterialMaps.Settings.enabled = v, "-ultrart-materials").Legacy();
            m.Float("Materials", "NormalStrength", "Detail strength (relight, pt-dn)", 0f, 4f, 2, () => MaterialMaps.Settings.normalStrength, v => MaterialMaps.Settings.normalStrength = v, "-ultrart-materials-normal").Legacy();
            m.Float("Materials", "Roughness", "Roughness variation", 0f, 2f, 2, () => MaterialMaps.Settings.roughness, v => MaterialMaps.Settings.roughness = v, "-ultrart-materials-roughness").Legacy();
            m.Float("Effects", "StainedGlassProjection", "Stained-glass light on floors and walls (strength in daylight, 0 = off)", 0f, 8f, 2, () => _sgSettings.strength, v => _sgSettings.strength = Mathf.Max(0f, v), "-ultrart-stained-glass");
            m.Float("Materials", "FleshSubsurface", "Flesh subsurface glow (strength, 0 = off)", 0f, 2f, 2, () => MaterialMaps.Settings.fleshSss, v => MaterialMaps.Settings.fleshSss = Mathf.Max(v, 0f), "-ultrart-flesh-sss");
            m.Bool("Reflections", "Enabled", "Reflections  [F4]", () => _reflSettings.enabled, v => { _reflSettings.enabled = v; _reflFilter?.Reset(); }, "-ultrart-noreflections").Basic();
            m.Float("Reflections", "Strength", "Reflection strength", 0f, 2f, 2, () => _reflSettings.reflStrength, v => _reflSettings.reflStrength = v, "-ultrart-refl-strength");
            m.Bool("Reflections", "PlayerInReflections", "V1 in reflections", () => _playerRays != 0, v => _playerRays = v ? 15 : 0, "-ultrart-player-rays", "-ultrart-noplayer").Basic();
            m.Bool("Reflections", "RtMirrors", "Ray-traced mirrors", () => _rtMirrors, v => _rtMirrors = v, "-ultrart-rtmirrors");
            // Bound by ParseParticleArgs / ParseViewmodelArgs (hence Legacy); the setters do what Shift+F7 / Ctrl+F7 do.
            m.Bool("Particles", "Lit", "Lit particles: smoke, dust, blood spray  [Shift+F7]", () => _particleSettings.enabled,
                v => { _particleSettings.enabled = v; _particleLight?.Reset(); }, "-ultrart-particles").Legacy().Basic();
            m.Bool("Viewmodel", "Enabled", "Ray-traced lighting on the gun and arms  [Ctrl+F7]", () => _vmEnabled,
                v => { _vmEnabled = v; _vmHistValid = false; }, "-ultrart-viewmodel").Legacy().Basic();
            m.Bool("Effects", "EffectLights", "Effect lights",
                () => _effectSettings.spawnedLights || _effectSettings.virtualLights || _effectSettings.flicker,
                v => _effectSettings.spawnedLights = _effectSettings.virtualLights = _effectSettings.flicker = v);
            m.Float("Effects", "BeamLights", "Combat beam lights (strength, 0 = off)", 0f, 3f, 2, () => _effectSettings.beamLights, v => _effectSettings.beamLights = Mathf.Max(v, 0f), "-ultrart-beam-lights");
            m.Float("Effects", "ParryLight", "Parry light burst (strength, 0 = off)", 0f, 3f, 2, () => _effectSettings.parryLight, v => _effectSettings.parryLight = Mathf.Max(v, 0f), "-ultrart-parry-light");
            m.Float("Effects", "LightningFlash", "Lightning flash (strength, 0 = off)", 0f, 3f, 2, () => _effectSettings.lightningFlash, v => _effectSettings.lightningFlash = Mathf.Max(v, 0f), "-ultrart-lightning-flash");
            m.Bool("Effects", "EffectReflections", "Fire and explosions in reflections", () => _fxgSettings.enabled, v => _fxgSettings.enabled = v, "-ultrart-noeffectgeometry");
            m.Bool("Effects", "Lava", "Ray-traced lava and acid", () => LavaCfg.active, v => LavaCfg.active = v, "-ultrart-nolava");
            m.Float("Effects", "Caustics", "Water caustics (strength, 0 = off)", 0f, 3f, 2, () => SceneProxies.Caustics.strength, v => SceneProxies.Caustics.strength = Mathf.Max(v, 0f), "-ultrart-caustics");
            m.Bool("Effects", "CausticShadows", "Water caustics: shadows", () => SceneProxies.Caustics.shadows, v => SceneProxies.Caustics.shadows = v, "-ultrart-caustics-noshadow");
            m.Float("Effects", "RippleCaustics", "Ripple caustics under water (strength, 0 = off)", 0f, 2f, 2, () => SceneProxies.Caustics.ripple, v => SceneProxies.Caustics.ripple = Mathf.Max(v, 0f), "-ultrart-ripple-caustics");
            m.Float("Effects", "WaterAbsorption", "Deep water absorption (per metre, 0 = off)", 0f, 1.5f, 2, () => SceneProxies.Transparents.waterAbsorb, v => SceneProxies.Transparents.waterAbsorb = Mathf.Max(v, 0f), "-ultrart-water-absorb");
            m.Float("Effects", "InteractiveWaterRipples", "Water ripples from splashes, wakes and explosions (strength, 0 = off)", 0f, 2f, 2, () => _waterImpulseSettings.impulses, v => _waterImpulseSettings.impulses = Mathf.Max(v, 0f), "-ultrart-water-impulses");

            // Screen-space effects (docs/vfx.md)
            m.Group = "Screen effects";
            m.Bool("ScreenEffects", "Enabled", "Screen effects  [Ctrl+F2]", () => _vfxSettings.enabled, v => _vfxSettings.enabled = v, "-ultrart-vfx");
            m.Bool("ScreenEffects", "Bloom", "Bloom  [Shift+F2]", () => _vfxSettings.bloom, v => _vfxSettings.bloom = v, "-ultrart-bloom");
            m.Float("ScreenEffects", "BloomStrength", "Bloom strength", 0f, 2f, 2, () => _vfxSettings.bloomStrength, v => _vfxSettings.bloomStrength = v, "-ultrart-bloom-strength");
            m.Bool("ScreenEffects", "SoftParticles", "Soft particles", () => _particleSettings.soft, v => _particleSettings.soft = v, "-ultrart-particles-soft");
            m.Bool("ScreenEffects", "HeatHaze", "Heat haze over lava and fire", () => _vfxSettings.heatHaze, v => _vfxSettings.heatHaze = v, "-ultrart-heathaze");
            m.Float("ScreenEffects", "HeatHazeStrength", "Heat haze strength", 0f, 3f, 2, () => _vfxSettings.heatAmplitude / VfxSettings.HeatAmplitudeDefault, v => _vfxSettings.heatAmplitude = v * VfxSettings.HeatAmplitudeDefault, "-ultrart-heathaze-strength");
            m.Bool("ScreenEffects", "Shockwaves", "Explosion shockwaves", () => _vfxSettings.shockwaves, v => _vfxSettings.shockwaves = v, "-ultrart-shockwaves");
            m.Float("ScreenEffects", "ShockwaveStrength", "Shockwave strength", 0f, 3f, 2, () => _vfxSettings.shockAmplitude / VfxSettings.ShockAmplitudeDefault, v => _vfxSettings.shockAmplitude = v * VfxSettings.ShockAmplitudeDefault, "-ultrart-shockwave-strength");
            m.Bool("ScreenEffects", "DebrisGlow", "Glowing explosion debris", () => _vfxSettings.debrisGlow, v => _vfxSettings.debrisGlow = v, "-ultrart-debris-glow");
            m.Bool("ScreenEffects", "Underwater", "Underwater shafts, fog and distortion", () => _vfxSettings.underwater, v => _vfxSettings.underwater = v, "-ultrart-underwater");

            m.Bool("Wet", "Rain", "Rain wetness and puddles", () => _wetSettings.rain, v => _wetSettings.rain = v, "-ultrart-wet-rain");
            m.Float("Wet", "RainWetness", "Rain wetness", 0f, 1f, 2, () => _wetSettings.rainWetness, v => _wetSettings.rainWetness = v, "-ultrart-wet-rain-strength");
            m.Bool("Wet", "Blood", "Wet blood (fresh stains are glossy)", () => _wetSettings.blood, v => _wetSettings.blood = v, "-ultrart-wet-blood");
            m.Float("Wet", "BloodDryTime", "Blood drying time (s)", 5f, 180f, 0, () => _wetSettings.bloodDryTime, v => _wetSettings.bloodDryTime = v, "-ultrart-wet-blood-dry");
            m.Bool("Wet", "StainReflections", "Blood stains in reflections", () => _wetSettings.stainReflections, v => _wetSettings.stainReflections = v, "-ultrart-stain-reflections");
            m.Float("Wet", "Puddles", "Puddles", 0f, 1f, 2, () => _wetSettings.puddles, v => _wetSettings.puddles = v, "-ultrart-wet-puddles");

            // HDR display output (Plugin.Hdr.cs, docs/hdr.md). The switch is read when the game starts (the preloader patcher).
            m.Group = "HDR display";
            m.Bool("Display", "HDR", "HDR output (applies after a restart)", () => _hdrRequested, v => _hdrRequested = v, "-ultrart-hdr").Legacy().Basic();
            m.Float("Display", "HDRPaperWhiteNits", "Paper white (nits; 0 = as Windows shows SDR)", 0f, 500f, 0, () => _hdrPaperWhite, v => _hdrPaperWhite = v, "-ultrart-hdr-paperwhite").Legacy().Basic();
            m.Float("Display", "HDRPeak", "Peak brightness (nits; 0 = the display's)", 0f, 4000f, 0, () => _hdrPeak, v => _hdrPeak = v, "-ultrart-hdr-peak").Legacy().Basic();
            m.Float("Display", "HDREmissive", "Glowing surfaces (x their SDR brightness)", 1f, 4f, 2, () => _hdrEmissive, v => _hdrEmissive = v, "-ultrart-hdr-emissive").Legacy();
            m.Float("Display", "HDRLava", "Lava and acid (x their SDR brightness)", 1f, 4f, 2, () => _hdrGameSurfaces, v => _hdrGameSurfaces = v, "-ultrart-hdr-lava").Legacy();
            m.Float("Display", "HDRSky", "Sky (x its SDR brightness)", 1f, 4f, 2, () => _hdrSky, v => _hdrSky = v, "-ultrart-hdr-sky").Legacy();
            m.Float("Display", "HDREffects", "Effects: fire, explosions, beams (x their SDR brightness)", 1f, 4f, 2, () => _hdrEffects, v => _hdrEffects = v, "-ultrart-hdr-effects").Legacy();
            m.Float("Display", "HDRBloom", "Bloom above white (room, display units)", 0f, 4f, 2, () => _hdrBloom, v => _hdrBloom = v, "-ultrart-hdr-bloom").Legacy();
            m.Bool("Display", "HDRHighlights", "Ray-traced highlights above white", () => _hdrHighlights, v => _hdrHighlights = v, "-ultrart-hdr-highlights").Legacy();
            // Native float HDR (docs/hdr.md, "Native float HDR") has no menu row since 2026-11-23: it is always wanted, and the
            // gain maps stay the automatic fallback while the game's target is not patched (-ultrart-hdr-native off for tests).

            LightEditorSettings(m); // per-level light overrides and the light editor (Plugin.LightOverrides.cs)
            CineSettingsMenu(m); // cinematic camera and offline render (Plugin.Cinematics.cs)

            m.Action("Reload shaders  [F9]", ReloadShaders);
            // One row per key (combination), matching the code: Plugin.cs PlayerAndDevHotkeys and the *Hotkeys / *Update methods.
            m.Keybinds = new[]
            {
                "Page Up / Page Down|previous / next view mode (pathtrace, relight, pt-dn; the debug views too with Advanced > Debug views)",
                "F7|ray tracing on / off", "F4|reflections", "Ctrl+F4|auto exposure",
                "F5|glowing surfaces as lights", "Ctrl+F5|sky light and linear transport", "Ctrl+F6|volumetrics",
                "Ctrl+F1|surface detail maps", "Ctrl+F2|screen effects (bloom, heat haze, shockwaves, underwater)", "Shift+F2|bloom",
                "Ctrl+F7|viewmodel pass (the gun and arms traced)", "Shift+F7|lit particles", "Ctrl+F8|DLSS Ray Reconstruction (relight, pt-dn)",
                "Insert|light editor (then numpad: 5 aim-select, 4/6 cycle, 8/2 parameter, +/- change, Enter save; docs/lighting-overrides.md)",
                "Ctrl+Insert|light classifier (docs/lighting-overrides.md)",
                "Developer keys|only with Advanced > Developer hotkeys (always in bench launches):",
                "F2|temporal upsampler", "F3|radiance cache mode (Shift+F3: its debug view)",
                "F6|light sampling (RIS / reference)", "F9|reload shaders (the Reload shaders button works without the developer keys)",
                "F10|record a camera path (bench)", "F11|save the current pose (bench)",
            }.Concat(CineKeybinds).ToArray();
            m.Capture();
        }

        /// <summary>After the shaders are loaded and every Parse* has run: apply the config, then create the menu page.</summary>
        private void AttachSettings()
        {
            // [Particles] Lit is bound by ParseParticleArgs, which otherwise first runs in the first update: bind it before
            // the menu does, or the menu's Legacy lookup binds it first with its label as the config description.
            if (!_particleArgsParsed) ParseParticleArgs();
            _menu?.Attach();
        }
    }

    internal interface IMenuBuilder
    {
        void Bool(BoolSetting s);
        void Float(FloatSetting s);
        void Choice<E>(ChoiceSetting<E> s) where E : struct, Enum;
    }

    internal abstract class Setting
    {
        public string Group, Section, Key, Label;
        /// <summary>Command-line flags that set this setting; a trailing * matches a prefix. One present: the config is not applied.</summary>
        public string[] Flags = Array.Empty<string>();
        /// <summary>The config entry is bound by the plugin's own Parse* code (older entries): reuse it, never rebind it.</summary>
        public bool IsLegacy;
        /// <summary>No config entry: changes last for the session.</summary>
        public bool IsSessionOnly;
        /// <summary>On the main page; everything else is on the "Advanced settings" sub-page (docs/settings.md, "Layout").</summary>
        public bool IsBasic;
        public SettingsMenu Menu;
        protected ConfigEntryBase Entry;

        public Setting Legacy() { IsLegacy = true; return this; }
        public Setting SessionOnly() { IsSessionOnly = true; return this; }
        public Setting Basic() { IsBasic = true; return this; }

        public abstract void Capture();
        public abstract void BindEntry(ConfigFile cfg, bool apply);
        /// <summary>Per frame: show a changed live value in the menu. Returns whether it changed.</summary>
        public abstract bool Refresh();
        public abstract void ResetToDefault();
        public abstract void Build(IMenuBuilder b);

        protected bool FlagPresent()
        {
            foreach (var arg in Environment.GetCommandLineArgs())
                foreach (var f in Flags)
                    if (f.EndsWith("*") ? arg.StartsWith(f.Substring(0, f.Length - 1), StringComparison.OrdinalIgnoreCase) : arg.Equals(f, StringComparison.OrdinalIgnoreCase))
                        return true;
            return false;
        }
    }

    internal abstract class Setting<T> : Setting
    {
        public Func<T> Get;
        public Action<T> Set;
        public T Default;
        /// <summary>The value the menu shows now.</summary>
        protected T shown;
        /// <summary>Set by the menu builder: show a value without raising a change event.</summary>
        public Action<T> Show = null;

        protected abstract bool Same(T a, T b);
        protected abstract ConfigEntryBase Bind(ConfigFile cfg);
        protected abstract object ToEntry(T v);
        protected abstract T FromEntry(object o);

        public override void Capture() { Default = Get(); shown = Default; }

        public override bool Refresh()
        {
            var v = Get();
            if (Same(v, shown)) return false;
            shown = v;
            Show?.Invoke(v);
            Menu.Log($"Settings: {Section}.{Key} = {ToEntry(v)} (changed outside the menu)");
            return true;
        }

        public override void BindEntry(ConfigFile cfg, bool apply)
        {
            if (IsSessionOnly) return;
            if (IsLegacy)
            {
                // Bound (and applied, flags included) by Parse*; Capture ran before that, so Default is the code default.
                Entry = cfg.Keys.Where(k => k.Section == Section && k.Key == Key).Select(k => cfg[k]).FirstOrDefault();
                if (Entry == null) Entry = Bind(cfg);
                return;
            }
            Entry = Bind(cfg);
            if (!apply) return;
            if (FlagPresent()) { Menu.Log($"Settings: {Section}.{Key} follows the command line"); return; }
            var v = FromEntry(Entry.BoxedValue);
            if (!Same(v, Get())) Set(v);
            shown = Get();
        }

        /// <summary>A change made in the menu: apply it (the hotkey's side effects included) and save it.</summary>
        public void FromMenu(T v)
        {
            if (Same(v, shown)) return;
            Set(v);
            shown = Get();
            if (!Same(shown, v)) Show?.Invoke(shown); // the plugin adjusted it
            Menu.Log($"Settings: {Section}.{Key} = {ToEntry(shown)} (menu)");
            Store();
        }

        public override void ResetToDefault()
        {
            Set(Default);
            shown = Get();
            Show?.Invoke(shown);
            Store();
        }

        private void Store()
        {
            if (Entry == null) return;
            Entry.BoxedValue = ToEntry(shown);
            Menu.MarkDirty();
        }
    }

    internal sealed class BoolSetting : Setting<bool>
    {
        protected override bool Same(bool a, bool b) => a == b;
        protected override ConfigEntryBase Bind(ConfigFile cfg) => cfg.Bind(Section, Key, Default, Label);
        protected override object ToEntry(bool v) => v;
        protected override bool FromEntry(object o) => (bool)o;
        public override void Build(IMenuBuilder b) => b.Bool(this);
    }

    internal sealed class FloatSetting : Setting<float>
    {
        public float Min, Max;
        public int Decimals;
        protected override bool Same(float a, float b) => Mathf.Abs(a - b) < Mathf.Pow(10f, -Decimals) * 0.5f;
        protected override ConfigEntryBase Bind(ConfigFile cfg) =>
            cfg.Bind(Section, Key, Default, new ConfigDescription(Label, new AcceptableValueRange<float>(Min, Max)));
        protected override object ToEntry(float v) => v;
        protected override float FromEntry(object o) => (float)o;
        public override void Build(IMenuBuilder b) => b.Float(this);
    }

    /// <summary>An enum setting. The config entry is the key string (ConfigEntry&lt;string&gt;), so the file stays readable.</summary>
    internal sealed class ChoiceSetting<E> : Setting<E> where E : struct, Enum
    {
        public Func<E, string> KeyOf, LabelOf;
        public readonly E[] Values = (E[])Enum.GetValues(typeof(E));
        protected override bool Same(E a, E b) => EqualityComparer<E>.Default.Equals(a, b);
        protected override ConfigEntryBase Bind(ConfigFile cfg) =>
            IsLegacy ? cfg.Bind(Section, Key, KeyOf(Default), Label)
                     : cfg.Bind(Section, Key, KeyOf(Default), new ConfigDescription(Label, new AcceptableValueList<string>(
                         // Default first: BepInEx clamps a value that is not in the list (a removed choice, e.g. an old
                         // LightSampler = ReSTIR) to the list's first entry, which should be the default, not the enum's first.
                         new[] { KeyOf(Default) }.Concat(Values.Select(KeyOf).Where(k => k != KeyOf(Default))).ToArray())));
        protected override object ToEntry(E v) => KeyOf(v);
        protected override E FromEntry(object o)
        {
            foreach (var v in Values)
                if (string.Equals(KeyOf(v), o as string, StringComparison.OrdinalIgnoreCase)) return v;
            return Default;
        }
        public override void Build(IMenuBuilder b) => b.Choice(this);
    }

    internal sealed class SettingsMenu
    {
        private readonly ManualLogSource _log;
        private readonly ConfigFile _cfg;
        public readonly List<Setting> All = new List<Setting>();
        public readonly List<(string label, Action run)> Actions = new List<(string, Action)>();
        /// <summary>"keys|what it does", shown read-only.</summary>
        public string[] Keybinds = Array.Empty<string>();
        public string Group;
        private bool _dirty;
        private float _saveAt, _nextRefresh;

        public SettingsMenu(ManualLogSource log, ConfigFile cfg) { _log = log; _cfg = cfg; }

        private T Add<T>(T s, string section, string key, string label, string[] flags) where T : Setting
        {
            s.Group = Group; s.Section = section; s.Key = key; s.Label = label; s.Flags = flags; s.Menu = this;
            All.Add(s);
            return s;
        }

        public BoolSetting Bool(string section, string key, string label, Func<bool> get, Action<bool> set, params string[] flags) =>
            Add(new BoolSetting { Get = get, Set = set }, section, key, label, flags);

        public FloatSetting Float(string section, string key, string label, float min, float max, int decimals, Func<float> get, Action<float> set, params string[] flags) =>
            Add(new FloatSetting { Get = get, Set = set, Min = min, Max = max, Decimals = decimals }, section, key, label, flags);

        public ChoiceSetting<E> Choice<E>(string section, string key, string label, Func<E> get, Action<E> set, Func<E, string> keyOf, Func<E, string> labelOf, params string[] flags) where E : struct, Enum =>
            Add(new ChoiceSetting<E> { Get = get, Set = set, KeyOf = keyOf, LabelOf = labelOf }, section, key, label, flags);

        public void Action(string label, Action run) => Actions.Add((label, run));

        public void Capture() { foreach (var s in All) s.Capture(); }

        public void Attach()
        {
            // New entries are written once at the end, not once per Bind; later saves are debounced (a slider drag is many changes).
            _cfg.SaveOnConfigSet = false;
            foreach (var s in All)
            {
                try { s.BindEntry(_cfg, apply: true); }
                catch (Exception e) { _log.LogWarning($"Settings: {s.Section}.{s.Key}: {e.Message}"); }
            }
            _cfg.Save();
            LogPages();
            TryCreateMenu();
        }

        /// <summary>Which setting is on which page, logged with or without PluginConfigurator (a check of the split that needs no menu).</summary>
        private void LogPages()
        {
            string List(bool basic) => string.Join(", ", All.Where(s => s.IsBasic == basic).Select(s => $"{s.Section}.{s.Key}"));
            _log.LogInfo($"Settings menu: main page ({All.Count(s => s.IsBasic)}): {List(true)}");
            _log.LogInfo($"Settings menu: advanced page ({All.Count(s => !s.IsBasic)}): {List(false)}");
        }

        public void Log(string line) => _log.LogInfo(line);

        public void MarkDirty() { _dirty = true; _saveAt = Time.realtimeSinceStartup + 0.75f; }

        /// <summary>Every frame: the menu follows hotkeys, and the config is saved shortly after the last change. The full
        /// refresh (every setting's getter) runs every frame while a menu can be open (the cursor is free or the game is
        /// paused), and at 4 Hz in play, where nobody sees the page (since 2026-11-20; it was every frame).</summary>
        public void Update()
        {
            float now = Time.realtimeSinceStartup;
            if (MenuMayBeOpen() || now >= _nextRefresh)
            {
                _nextRefresh = now + 0.25f;
                foreach (var s in All) s.Refresh();
            }
            if (_dirty && now >= _saveAt)
            {
                _dirty = false;
                try { _cfg.Save(); }
                catch (Exception e) { _log.LogWarning($"Settings: could not save the config: {e.Message}"); }
            }
        }

        private static bool MenuMayBeOpen()
        {
            try { return Cursor.visible || Cursor.lockState != CursorLockMode.Locked || Time.timeScale == 0f; }
            catch (Exception) { return true; }
        }

        public void ResetAll()
        {
            // The light editor's "selected light" fields edit override data, not settings: a reset leaves them alone.
            foreach (var s in All) if (!(s.Section == "Lights" && s.Key.StartsWith("Sel", StringComparison.Ordinal))) s.ResetToDefault();
        }

        private void TryCreateMenu()
        {
            if (!Chainloader.PluginInfos.ContainsKey(Plugin.PluginConfiguratorGuid))
            {
                _log.LogInfo("Settings menu: PluginConfigurator is not installed, so there is no in-game menu (settings are in BepInEx/config/ooo.lew.ultrart.cfg)");
                return;
            }
#if ULTRART_PLUGINCONFIGURATOR
            try { CreateUi(); }
            catch (Exception e) { _log.LogWarning($"Settings menu: could not create the PluginConfigurator page: {e}"); }
#else
            _log.LogInfo("Settings menu: this build has no PluginConfigurator support (built without lib/PluginConfigurator.dll)");
#endif
        }

#if ULTRART_PLUGINCONFIGURATOR
        // NoInlining: the JIT must not pull PluginConfigurator's types into TryCreateMenu, which runs without the plugin too.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private void CreateUi()
        {
            SettingsMenuUi.Create(this);
            _log.LogInfo($"Settings menu: PluginConfigurator page created ({All.Count} settings: {All.Count(s => s.IsBasic)} main, {All.Count(s => !s.IsBasic)} advanced)");
        }
#endif
    }

#if ULTRART_PLUGINCONFIGURATOR
    /// <summary>The PluginConfigurator page. The only code that references PluginConfig.API.</summary>
    internal sealed class SettingsMenuUi : IMenuBuilder
    {
        private readonly PluginConfig.API.ConfigPanel _panel;

        private SettingsMenuUi(PluginConfig.API.ConfigPanel panel) { _panel = panel; }

        public static void Create(SettingsMenu menu)
        {
            var pc = PluginConfig.API.PluginConfigurator.Create("UltraRT", "ooo.lew.ultrart");
            pc.saveToFile = false; // the values live in the BepInEx config; the page only shows and edits them
            new PluginConfig.API.Decorators.ConfigHeader(pc.rootPanel, $"UltraRT {PluginVersion.Number}", 24); // Version.props
            new PluginConfig.API.Decorators.ConfigHeader(pc.rootPanel, "Changes apply and save at once. Hotkeys: session only.", 14);
            AddPage(pc.rootPanel, menu.All.Where(s => s.IsBasic));

            // Everything not tagged Basic(): tuning values, sampler and denoiser internals, per-effect gains, debug and A/B switches.
            var advanced = new PluginConfig.API.ConfigPanel(pc.rootPanel, "Advanced settings", "advanced");
            new PluginConfig.API.Decorators.ConfigHeader(advanced, "Tuning, internals and comparison switches. Saved like the main page.", 14);
            AddPage(advanced, menu.All.Where(s => !s.IsBasic));

            new PluginConfig.API.Decorators.ConfigHeader(pc.rootPanel, "Tools", 22);
            foreach (var (label, run) in menu.Actions)
                new PluginConfig.API.Functionals.ButtonField(pc.rootPanel, label, "action." + label).onClick += () => run();
            new PluginConfig.API.Functionals.ButtonField(pc.rootPanel, "Reset all UltraRT settings", "action.reset").onClick += menu.ResetAll;

            var keys = new PluginConfig.API.ConfigPanel(pc.rootPanel, "Keybinds", "keybinds");
            new PluginConfig.API.Decorators.ConfigHeader(keys, "Read only. These switch things for the session; save a choice in this menu.", 14);
            foreach (var line in menu.Keybinds)
            {
                var p = line.Split('|');
                new PluginConfig.API.Decorators.ConfigHeader(keys, $"{p[0]}  -  {p[1]}", 16);
            }

            // Credits: the NVIDIA DLSS SDK license asks for attribution in an about box or credits (docs/release.md).
            new PluginConfig.API.Decorators.ConfigHeader(pc.rootPanel, "Credits", 22);
            new PluginConfig.API.Decorators.ConfigHeader(pc.rootPanel,
                "Denoising and upscaling in pathtrace: NVIDIA DLSS Ray Reconstruction (NVIDIA DLSS SDK). NVIDIA and DLSS are trademarks of NVIDIA Corporation.", 14);
            new PluginConfig.API.Decorators.ConfigHeader(pc.rootPanel,
                "Surface detail maps (optional package): made with PBRify Remix (CC0).", 14);
        }

        /// <summary>The settings in registration order, with a header wherever the group changes.</summary>
        private static void AddPage(PluginConfig.API.ConfigPanel panel, IEnumerable<Setting> settings)
        {
            var ui = new SettingsMenuUi(panel);
            string group = null;
            foreach (var s in settings)
            {
                if (s.Group != group)
                {
                    group = s.Group;
                    new PluginConfig.API.Decorators.ConfigHeader(panel, group, 22);
                }
                s.Build(ui);
            }
        }

        private static string Guid(Setting s) => $"{s.Section}.{s.Key}";

        public void Bool(BoolSetting s)
        {
            var f = new PluginConfig.API.Fields.BoolField(_panel, s.Label, Guid(s), s.Default, false);
            f.value = s.Get();
            f.postValueChangeEvent += v => s.FromMenu(v);
            s.Show = v => f.value = v;
        }

        public void Float(FloatSetting s)
        {
            var f = new PluginConfig.API.Fields.FloatSliderField(_panel, s.Label, Guid(s), new Tuple<float, float>(s.Min, s.Max), s.Default, s.Decimals, false);
            f.value = s.Get();
            f.postValueChangeEvent += (v, _) => s.FromMenu(v);
            s.Show = v => f.value = v;
        }

        public void Choice<E>(ChoiceSetting<E> s) where E : struct, Enum
        {
            var f = new PluginConfig.API.Fields.EnumField<E>(_panel, s.Label, Guid(s), s.Default, false);
            foreach (var v in s.Values) f.SetEnumDisplayName(v, s.LabelOf(v));
            f.value = s.Get();
            f.postValueChangeEvent += v => s.FromMenu(v);
            s.Show = v => f.value = v;
        }
    }
#endif
}
