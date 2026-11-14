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
            Hybrid, PtDn, Relight, DnLighting, DnVariance, DnHistory, Pt, PtDirect, PtIndirect, Lit, Albedo, Normals, Depth, Instance, Uv,
            Emission, Primitive, Refl, ReflRaw, ReflSpec, ReflHistory, Materials, SpecParams, Cache, CacheCells, DnBoost, Motion,
        }

        internal enum TraceQuality { Performance, Balanced, Quality }
        internal enum DenoiserQuality { Full, Previous, Legacy, Custom }

        private static readonly UpsamplerSettings UpsamplerDefaults = new UpsamplerSettings();

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
                _denoiser?.Reset(); _denoiserDirect?.Reset(); _lightSampling?.Reset(); _reflFilter?.Reset();
            }, "-ultrart-off").SessionOnly();
            m.Choice("View", "DefaultMode", "View mode  [PgUp/PgDn]",
                () => { foreach (var kv in modeOf) if (kv.Value == _mode) return kv.Key; return ViewModeChoice.Hybrid; },
                v => { if (modeOf.TryGetValue(v, out int i)) _mode = i; },
                v => modeOf.TryGetValue(v, out int i) ? Modes[i].name : v.ToString(),
                v => v switch
                {
                    ViewModeChoice.Hybrid => "hybrid (game + RT)",
                    ViewModeChoice.Relight => "relight (full RT)",
                    ViewModeChoice.PtDn => "pt-dn (path traced)",
                    _ => modeOf.TryGetValue(v, out int i) ? Modes[i].name : v.ToString(),
                }, "-ultrart-view").Legacy();

            m.Group = "Quality and performance";
            m.Choice("Quality", "TraceScale", "Trace resolution",
                () => _scale < 0.585f ? TraceQuality.Performance : _scale < 0.835f ? TraceQuality.Balanced : TraceQuality.Quality,
                v =>
                {
                    _scale = v == TraceQuality.Performance ? 0.5f : v == TraceQuality.Balanced ? 0.67f : 1f;
                    _denoiser?.Reset(); _denoiserDirect?.Reset(); _lightSampling?.Reset(); _reflFilter?.Reset();
                },
                v => v.ToString(),
                v => v == TraceQuality.Performance ? "Performance (0.5x)" : v == TraceQuality.Balanced ? "Balanced (0.67x)" : "Quality (1x)", "-ultrart-scale");
            m.Bool("Quality", "TemporalUpsampler", "Temporal upsampler  [F2]", () => _upsampler?.Settings.enabled ?? UpsamplerDefaults.enabled, v =>
            {
                if (_upsampler != null) _upsampler.Settings.enabled = v;
            }, "-ultrart-notaau");
            m.Choice("Quality", "Denoiser", "Denoiser  [Ctrl+F3]",
                () => _denoiser == null ? DenoiserQuality.Full : (DenoiserQuality)(int)_denoiser.Settings.CurrentPreset(),
                v => { if (v != DenoiserQuality.Custom) ApplyDenoiserPreset((DenoiserPreset)(int)v); },
                v => v.ToString(),
                v => v == DenoiserQuality.Full ? "Full" : v == DenoiserQuality.Previous ? "Previous build" : v == DenoiserQuality.Legacy ? "Legacy (plain SVGF)" : "Custom (command line)",
                "-ultrart-dn-*");
            m.Choice("Quality", "RadianceCache", "Radiance cache  [F3]",
                () => _cacheSettings.enabled ? _cacheSettings.mode : RadianceCacheMode.Off,
                v => { _cacheSettings.enabled = true; _cacheSettings.mode = v; _denoiser?.Reset(); },
                v => v.ToString(), v => v.ToString(), "-ultrart-nocache", "-ultrart-cache-mode");
            m.Choice("Quality", "LightSampling", "Light sampling  [F6]",
                () => _lightSamplingSettings.mode,
                v => { _lightSamplingSettings.mode = v; _lightSampling?.Reset(); },
                v => v.ToString(),
                v => v == LightSamplingMode.Reference ? "Reference (every light, slow)" : v == LightSamplingMode.Ris ? "RIS" : "ReSTIR", "-ultrart-lights");

            m.Group = "Lighting";
            m.Float("Relight", "LightReference", "Light reference", 0.05f, 1f, 2, () => _relightRef, v => { _relightRef = v; _radianceCache?.NotifyLightsChanged(); }, "-ultrart-relight-ref");
            m.Float("Relight", "Exposure", "Light exposure", 0.25f, 4f, 2, () => _relightExposure, v => _relightExposure = v, "-ultrart-relight-exposure");
            m.Float("Relight", "FillCut", "Fill light cut", 0f, 1f, 2, () => _relightFillCut, v => _relightFillCut = v, "-ultrart-relight-fill");
            m.Bool("Relight", "SkyGI", "Sky + linear light  [Ctrl+F5]", () => _skyGI, v => { _skyGI = v; _denoiser?.Reset(); _lightSampling?.Reset(); });
            m.Bool("Relight", "SkySampling", "Sample the sky directly", () => _skySampling, v => { _skySampling = v; _denoiser?.Reset(); _lightSampling?.Reset(); }, "-ultrart-sky-sampling");
            m.Float("Relight", "SkyScale", "Sky light", 0f, 3f, 2, () => _skyScale, v => _skyScale = v, "-ultrart-sky-scale");
            m.Bool("Lighting", "EmissiveLights", "Emissive lighting  [F5]", () => _emissiveSettings.enabled, v =>
            {
                _emissiveSettings.enabled = v;
                _lightSampling?.Reset(); _denoiser?.Reset(); _denoiserDirect?.Reset();
            }, "-ultrart-noemissive");
            m.Float("Lighting", "EmissiveScale", "Emissive brightness", 0f, 8f, 2, () => _emissiveSettings.lightScale, v => _emissiveSettings.lightScale = v, "-ultrart-emissive-scale");

            m.Group = "Auto exposure";
            m.Bool("Relight", "AutoExposure", "Auto exposure  [Ctrl+F4]", () => _aeSettings.enabled, v => { _aeSettings.enabled = v; _autoExposure?.Reset(); }, "-ultrart-relight-autoexposure").Legacy();
            m.Float("Relight", "AutoExposureKey", "Target brightness", 0.1f, 1f, 2, () => _aeSettings.key, v => _aeSettings.key = v, "-ultrart-ae-key");
            m.Float("Relight", "AutoExposureMin", "Darkest exposure (EV)", -6f, 0f, 1, () => _aeSettings.minEV, v => _aeSettings.minEV = v, "-ultrart-ae-min");
            m.Float("Relight", "AutoExposureMax", "Brightest exposure (EV)", 0f, 3f, 1, () => _aeSettings.maxEV, v => _aeSettings.maxEV = v, "-ultrart-ae-max");

            m.Group = "Effects";
            m.Bool("Volumetrics", "Enabled", "Volumetric light and fog  [Ctrl+F6]", () => _volSettings.enabled, v => { _volSettings.enabled = v; _volumetrics?.Reset(); }, "-ultrart-volumetrics").Legacy();
            m.Float("Volumetrics", "Density", "Fog density", 0f, 4f, 2, () => _volSettings.densityScale, v => _volSettings.densityScale = v, "-ultrart-vol-density");
            m.Float("Volumetrics", "Strength", "Light shaft strength", 0f, 4f, 2, () => _volSettings.strength, v => _volSettings.strength = v, "-ultrart-vol-strength");
            m.Bool("Materials", "Enabled", "Surface detail maps  [Ctrl+F1]", () => MaterialMaps.Settings.enabled, v => MaterialMaps.Settings.enabled = v, "-ultrart-materials").Legacy();
            m.Float("Materials", "NormalStrength", "Detail strength (relight, pt-dn)", 0f, 4f, 2, () => MaterialMaps.Settings.normalStrength, v => MaterialMaps.Settings.normalStrength = v, "-ultrart-materials-normal").Legacy();
            m.Float("Materials", "HybridNormalStrength", "Detail strength (hybrid)", 0f, 4f, 2, () => MaterialMaps.Settings.hybridNormalStrength, v => MaterialMaps.Settings.hybridNormalStrength = v, "-ultrart-materials-hybrid-normal").Legacy();
            m.Float("Materials", "Roughness", "Roughness variation", 0f, 2f, 2, () => MaterialMaps.Settings.roughness, v => MaterialMaps.Settings.roughness = v, "-ultrart-materials-roughness").Legacy();
            m.Bool("Reflections", "Enabled", "Reflections  [F4]", () => _reflSettings.enabled, v => { _reflSettings.enabled = v; _reflFilter?.Reset(); }, "-ultrart-noreflections");
            m.Float("Reflections", "Strength", "Reflection strength", 0f, 2f, 2, () => _reflSettings.reflStrength, v => _reflSettings.reflStrength = v, "-ultrart-refl-strength");
            m.Bool("Reflections", "PlayerInReflections", "V1 in reflections", () => _playerRays != 0, v => _playerRays = v ? 15 : 0, "-ultrart-player-rays", "-ultrart-noplayer");
            m.Bool("Reflections", "RtMirrors", "Ray-traced mirrors", () => _rtMirrors, v => _rtMirrors = v, "-ultrart-rtmirrors");
            m.Bool("Effects", "EffectLights", "Effect lights",
                () => _effectSettings.spawnedLights || _effectSettings.virtualLights || _effectSettings.flicker,
                v => _effectSettings.spawnedLights = _effectSettings.virtualLights = _effectSettings.flicker = v);
            m.Bool("Effects", "Lava", "Ray-traced lava and acid", () => LavaCfg.active, v => LavaCfg.active = v, "-ultrart-nolava");

            m.Action("Reload shaders  [F9]", ReloadShaders);
            m.Keybinds = new[]
            {
                "Page Up / Page Down|previous / next view mode", "F7|ray tracing on / off", "F2|temporal upsampler",
                "F3|radiance cache mode (Shift: debug view)", "Ctrl+F3|denoiser preset", "F4|reflections", "Ctrl+F4|auto exposure",
                "F5|glowing surfaces as lights", "Ctrl+F5|sky light and linear transport", "F6|light sampling", "Ctrl+F6|volumetrics",
                "Ctrl+F1|surface detail maps", "F9|reload shaders", "F10|record", "F11|save pose",
            };
            m.Capture();
        }

        /// <summary>After the shaders are loaded and every Parse* has run: apply the config, then create the menu page.</summary>
        private void AttachSettings() => _menu?.Attach();
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
        public SettingsMenu Menu;
        protected ConfigEntryBase Entry;

        public Setting Legacy() { IsLegacy = true; return this; }
        public Setting SessionOnly() { IsSessionOnly = true; return this; }

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
                     : cfg.Bind(Section, Key, KeyOf(Default), new ConfigDescription(Label, new AcceptableValueList<string>(Values.Select(KeyOf).ToArray())));
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
        private float _saveAt;

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
            TryCreateMenu();
        }

        public void Log(string line) => _log.LogInfo(line);

        public void MarkDirty() { _dirty = true; _saveAt = Time.realtimeSinceStartup + 0.75f; }

        /// <summary>Every frame: the menu follows hotkeys, and the config is saved shortly after the last change.</summary>
        public void Update()
        {
            foreach (var s in All) s.Refresh();
            if (_dirty && Time.realtimeSinceStartup >= _saveAt)
            {
                _dirty = false;
                try { _cfg.Save(); }
                catch (Exception e) { _log.LogWarning($"Settings: could not save the config: {e.Message}"); }
            }
        }

        public void ResetAll()
        {
            foreach (var s in All) s.ResetToDefault();
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
            _log.LogInfo($"Settings menu: PluginConfigurator page created ({All.Count} settings)");
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
            var ui = new SettingsMenuUi(pc.rootPanel);
            new PluginConfig.API.Decorators.ConfigHeader(pc.rootPanel, "Changes apply and save at once. Hotkeys: session only.", 14);
            string group = null;
            foreach (var s in menu.All)
            {
                if (s.Group != group)
                {
                    group = s.Group;
                    new PluginConfig.API.Decorators.ConfigHeader(pc.rootPanel, group, 22);
                }
                s.Build(ui);
            }
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
