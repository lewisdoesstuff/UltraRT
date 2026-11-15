using System;
using System.Globalization;
using UnityEngine;

namespace UltraRT
{
    /// <summary>Wet surfaces (docs/vfx.md, "Wet surfaces"). Bench variants: wet.&lt;field&gt;=value.</summary>
    public sealed class WetSettings
    {
        /// <summary>Rain: surfaces the game rains on (its RAIN keyword, SceneProxies.Rain.cs) are darker and glossier, with
        /// puddles on floors and ripples from the game's own raindrop rings.</summary>
        public bool rain = true;
        /// <summary>How wet a rained-on surface is (0 = dry, the look before).</summary>
        public float rainWetness = 0.8f;
        /// <summary>Share of a floor that holds standing water (0 = no puddles).</summary>
        public float puddles = 0.45f;
        /// <summary>Size of the puddle pattern (m).</summary>
        public float puddleSize = 5f;
        /// <summary>How much the raindrop rings tilt the water surface.</summary>
        public float ripples = 1f;

        /// <summary>Wet blood: fresh stains are glossy and darker, and dry to the game's matte look.</summary>
        public bool blood = true;
        /// <summary>Seconds a stain stays fully wet, and seconds until it is dry.</summary>
        public float bloodWetTime = 6f, bloodDryTime = 40f;
        /// <summary>Strength (0-1), and how much darker a fully wet stain's surface is.</summary>
        public float bloodWetness = 1f, bloodDarken = 0.3f;
        /// <summary>Gloss of fully wet blood: roughness, reflectance at normal incidence, specular weight.</summary>
        public float bloodRoughness = 0.12f, bloodF0 = 0.12f, bloodWeight = 1f;
        /// <summary>At most this many of the newest stains are drawn into the wetness mask.</summary>
        public int bloodMaxStains = 4096;
        /// <summary>Blood stains show in reflections (Plugin.Stains.cs): reflection hits get the game's blood composite.</summary>
        public bool stainReflections = true;
        /// <summary>Stains further than this from the camera (m) are left out of the reflection lookup.</summary>
        public float stainDistance = 80f;

        public void ParseArgs()
        {
            var a = Environment.GetCommandLineArgs();
            for (int i = 0; i < a.Length; i++)
            {
                string Next() => i + 1 < a.Length ? a[++i] : null;
                float F(float fallback) => float.TryParse(Next(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;
                bool On() { var v = (Next() ?? "").ToLowerInvariant(); return v != "off" && v != "0" && v != "false"; }
                switch (a[i].ToLowerInvariant())
                {
                    case "-ultrart-wet-rain": rain = On(); break;
                    case "-ultrart-wet-rain-strength": rainWetness = F(rainWetness); break;
                    case "-ultrart-wet-puddles": puddles = F(puddles); break;
                    case "-ultrart-wet-blood": blood = On(); break;
                    case "-ultrart-wet-blood-dry": bloodDryTime = F(bloodDryTime); break;
                    case "-ultrart-stain-reflections": stainReflections = On(); break;
                }
            }
        }

        public override string ToString() => FormattableString.Invariant($"rain {(rain ? "on" : "off")} (wetness {rainWetness}, puddles {puddles}), blood {(blood ? "on" : "off")} (wet {bloodWetTime} s, dry after {bloodDryTime} s), stains in reflections {(stainReflections ? "on" : "off")}");
    }

    /// <summary>
    /// Wet surfaces wiring. Rain wetness lives in the proxy hit shader (Proxy.shader, RainWetness): this only publishes the
    /// global. Hooks in Plugin.cs: ParseWetArgs (Awake), UpdateWet (Trace, next to UpdateRain).
    ///   -ultrart-wet-rain on|off, -ultrart-wet-rain-strength F, -ultrart-wet-puddles F, -ultrart-wet-blood on|off,
    ///   -ultrart-wet-blood-dry SECONDS
    /// Wet blood: BloodWet.cs draws the mask (LoadWetShaders from LoadShaders, BindBloodWet in Trace before the dispatch).
    /// </summary>
    public partial class Plugin
    {
        private readonly WetSettings _wetSettings = new WetSettings();
        private static readonly int WetId = Shader.PropertyToID("_UltraRTWet");
        private BloodWet _bloodWet;
        private float _bloodNextLog;

        private void LoadWetShaders(AssetBundle bundle)
        {
            _bloodWet?.Dispose();
            _bloodWet = null;
            Shader sh = null;
            foreach (var s in bundle.LoadAllAssets<Shader>()) if (s.name == "Hidden/UltraRT/BloodWet") sh = s;
            if (sh == null || !sh.isSupported) { Logger.LogWarning("BloodWet shader missing or unsupported: no wet blood"); return; }
            _bloodWet = new BloodWet(Logger, sh);
        }

        /// <summary>Trace, before the ray dispatch: draws the wetness mask of the fresh stains (when there are any) and binds it.</summary>
        private void BindBloodWet(UnityEngine.Experimental.Rendering.RayTracingShader shader)
        {
            var s = _wetSettings;
            bool on = false;
            if (_bloodWet != null && s.blood && s.bloodWetness > 0f)
            {
                try { on = _bloodWet.Render(_cmd, _camera, s); }
                catch (Exception e)
                {
                    Logger.LogWarning($"Wet blood failed, switching it off: {e}");
                    s.blood = false;
                }
            }
            _cmd.SetRayTracingTextureParam(shader, "_BloodWetTex", on ? (Texture)_bloodWet.Mask : Texture2D.blackTexture);
            _cmd.SetRayTracingVectorParam(shader, "_BloodWetParams", on ? new Vector4(Mathf.Clamp01(s.bloodWetness), _guardScale.x, _guardScale.y, Mathf.Clamp01(s.bloodDarken)) : Vector4.zero);
            _cmd.SetRayTracingVectorParam(shader, "_BloodWetGloss", new Vector4(Mathf.Clamp(s.bloodRoughness, 0.02f, 1f), Mathf.Clamp01(s.bloodF0), Mathf.Clamp01(s.bloodWeight), 0f));
            if (on && Time.realtimeSinceStartup >= _bloodNextLog)
            {
                _bloodNextLog = Time.realtimeSinceStartup + 10f;
                Logger.LogInfo($"Wet blood: {_bloodWet.FreshStains} stains still wet");
            }
        }

        private void ParseWetArgs()
        {
            _wetSettings.ParseArgs();
            Logger.LogInfo($"Wet surfaces: {_wetSettings}");
        }

        /// <summary>Every traced frame.</summary>
        private void UpdateWet()
        {
            var s = _wetSettings;
            bool on = s.rain && _rainOn && s.rainWetness > 0f;
            Shader.SetGlobalVector(WetId, on
                ? new Vector4(Mathf.Clamp01(s.rainWetness), Mathf.Clamp01(s.puddles), 1f / Mathf.Max(0.5f, s.puddleSize), Mathf.Max(0f, s.ripples))
                : Vector4.zero);
        }
    }
}
