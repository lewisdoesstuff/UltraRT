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
        /// <summary>Largest tilt of the ripple normal, about radians (0.12 = 7 degrees; docs/vfx.md "Rain", 2026-11-20).</summary>
        public float rippleTilt = 0.12f;
        /// <summary>Bench A/B (wet.ripplesOld=1): the ripples of 2026-11-15, which drew dark stamped rings on P-2's street.</summary>
        public bool ripplesOld;
        /// <summary>Width of a puddle's edge in units of the puddle noise (0.12 = the hard outline of 2026-11-14).</summary>
        public float puddleEdge = 0.12f;
        /// <summary>A wetter margin around each puddle, this wide in noise units: the floor there is as wet as it gets
        /// without standing water (0 = none).</summary>
        public float puddleMargin = 0f;
        /// <summary>Roughness and specular weight of a fully wet surface that holds no puddle.</summary>
        public float wetRoughness = 0.3f, wetWeight = 0.5f;
        /// <summary>-ultrart-wet-soft on|off (bench wet.soft=0 for the old look): soft puddle edges with a wet margin and a glossier wet
        /// floor, in place of the four values above (docs/vfx.md, "Soft puddles"). On since the review of 2026-11-17.</summary>
        public bool soft = true;
        /// <summary>Rain (drops, trails, wetness, puddles) on VERTEX_DISPLACEMENT surfaces is evaluated on their rest pose, as the
        /// game's shader does, so it moves with the shifting surface (bench wet.restPose=0: at the displaced hit, before
        /// 2026-11-20; docs/scene-proxies.md, "Rain on displaced surfaces").</summary>
        public bool restPose = true;

        /// <summary>Wet blood: fresh stains are glossy and darker, and dry to the game's matte look.</summary>
        public bool blood = true;
        /// <summary>Seconds a stain stays fully wet, and seconds until it is dry.</summary>
        public float bloodWetTime = 6f, bloodDryTime = 40f;
        /// <summary>Strength (0-1), and how much darker a fully wet stain's surface is.</summary>
        public float bloodWetness = 1f, bloodDarken = 0.3f;
        /// <summary>Gloss of fully wet blood: roughness, reflectance at normal incidence, specular weight.</summary>
        public float bloodRoughness = 0.12f, bloodF0 = 0.12f, bloodWeight = 1f;
        /// <summary>Wet blood on surfaces the game moves with VERTEX_DISPLACEMENT (3-1 / 3-2 / P-1 flesh). Off: the game's stain
        /// decal stays where it was stamped while the surface slides under it (vanilla does that too, as a flat tint), and a
        /// glossy film there read as a layer that does not move with the flesh (docs/vfx.md, "Wet blood on moving
        /// surfaces"). Bench wet.bloodOnDisplaced=1 is the behaviour before 2026-11-20.</summary>
        public bool bloodOnDisplaced;
        /// <summary>At most this many of the newest stains are drawn into the wetness mask.</summary>
        public int bloodMaxStains = 4096;
        /// <summary>Bench A/B (wet.bloodFullRes=1): draw the wetness mask at full screen res instead of trace resolution.</summary>
        public bool bloodFullRes;
        /// <summary>Blood stains show in reflections (Plugin.Stains.cs): reflection hits get the game's blood composite.</summary>
        public bool stainReflections = true;
        /// <summary>Stains further than this from the camera (m) are left out of the reflection lookup.</summary>
        public float stainDistance = 80f;
        /// <summary>Debug (bench wet.stainDebug=1): stains found at reflection hits show bright green.</summary>
        public bool stainDebug;

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
                    case "-ultrart-wet-soft": soft = On(); break;
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
        private static readonly int Wet2Id = Shader.PropertyToID("_UltraRTWet2");
        private static readonly int Wet3Id = Shader.PropertyToID("_UltraRTWet3");
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
        private void BindBloodWet(UnityEngine.Experimental.Rendering.RayTracingShader shader, int traceW = 0, int traceH = 0)
        {
            var s = _wetSettings;
            bool on = false;
            if (_bloodWet != null && s.blood && s.bloodWetness > 0f)
            {
                try { on = _bloodWet.Render(_cmd, _camera, s, traceW, traceH); }
                catch (Exception e)
                {
                    Logger.LogWarning($"Wet blood failed, switching it off: {e}");
                    s.blood = false;
                }
            }
            _cmd.SetRayTracingTextureParam(shader, "_BloodWetTex", on ? (Texture)_bloodWet.Mask : Texture2D.blackTexture);
            _cmd.SetRayTracingVectorParam(shader, "_BloodWetParams", on ? new Vector4(Mathf.Clamp01(s.bloodWetness), _guardScale.x, _guardScale.y, Mathf.Clamp01(s.bloodDarken)) : Vector4.zero);
            _cmd.SetRayTracingVectorParam(shader, "_BloodWetGloss", new Vector4(Mathf.Clamp(s.bloodRoughness, 0.02f, 1f), Mathf.Clamp01(s.bloodF0), Mathf.Clamp01(s.bloodWeight), s.bloodOnDisplaced ? 1f : 0f));
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
            Shader.SetGlobalFloat("_UltraRTTransAlpha", SceneProxies.Transparents.alphaTint ? 1f : 0f); // every frame, every mode
            // x puddle edge width, y wet margin width (noise units), z wet roughness, w wet specular weight.
            Shader.SetGlobalVector(Wet2Id, s.soft
                ? new Vector4(0.4f, 0.3f, 0.15f, 1f)
                : new Vector4(Mathf.Clamp(s.puddleEdge, 0.01f, 1f), Mathf.Clamp01(s.puddleMargin), Mathf.Clamp01(s.wetRoughness), Mathf.Clamp01(s.wetWeight)));
            // x the 2026-11-15 ripples (A/B), y largest ripple tilt.
            Shader.SetGlobalVector(Wet3Id, new Vector4(s.ripplesOld ? 1f : 0f, Mathf.Clamp(s.rippleTilt, 0f, 1f), s.restPose ? 1f : 0f, 0f));
        }
    }
}
