using System;
using System.Globalization;
using UnityEngine;

namespace UltraRT
{
    /// <summary>
    /// Per-texel material detail wiring (docs/materials.md, MaterialMaps.cs). Plugin.cs only has one-line hooks:
    /// ParseMaterialArgs (Awake) and MaterialsUpdate (OnUpdate).
    ///   -ultrart-materials on|off (config [Materials] Enabled), -ultrart-materials-source ai|proc (config [Materials] Source),
    ///   -ultrart-materials-normal F, -ultrart-materials-roughness F,
    ///   -ultrart-materials-lod on|off (config [Materials] TextureLod), -ultrart-materials-lod-bias F (config [Materials] LodBias),
    ///   -ultrart-materials-toksvig F, -ultrart-materials-albedo-lod on|off. Ctrl+F1 toggles in game.
    /// </summary>
    public partial class Plugin
    {
        private void ParseMaterialArgs()
        {
            var s = MaterialMaps.Settings;
            s.enabled = Config.Bind("Materials", "Enabled", true,
                "Per-texel normal and roughness maps generated offline by tools/materials into BepInEx/ultrart/materials " +
                "(docs/materials.md). Overridden by -ultrart-materials on|off; Ctrl+F1 toggles in game.").Value;
            s.source = Config.Bind("Materials", "Source", "ai",
                "Which generated set to use: ai (PBRify Remix) or proc (procedural). Overridden by -ultrart-materials-source.").Value;
            s.normalStrength = Config.Bind("Materials", "NormalStrength", 1f, "Multiplier on the per-class normal strength (relight, pt-dn, pt). Default 1 since 2026-11-17 (was 2: too bumpy under DLSS RR).").Value;
            s.smooth = Config.Bind("Materials", "NormalSmoothing", 1f, "0-1 (default 1 since 2026-11-17; 0 = the maps as generated): pulls each texel's normal towards the average of its 3x3 neighbourhood when the maps are loaded, against fragments inside bricks (docs/materials.md, \"Matrix\"). Read when a level loads. Overridden by -ultrart-materials-smooth.").Value;
            s.roughness = Config.Bind("Materials", "Roughness", 1f, "Multiplier on the per-class roughness variation.").Value;
            s.lod = Config.Bind("Materials", "TextureLod", true,
                "Ray-cone texture LOD for the per-texel detail (docs/materials.md, \"Texture LOD\"): normal detail smaller than a " +
                "traced pixel is averaged (and turned into roughness) instead of aliasing into noise. Overridden by -ultrart-materials-lod on|off.").Value;
            s.lodBias = Config.Bind("Materials", "LodBias", 0f,
                "Mips added to the detail LOD: 0 = detail texels the size of a traced pixel, negative = sharper (more aliasing). " +
                "Overridden by -ultrart-materials-lod-bias.").Value;
            var u = SceneProxies.Unlit;
            u.enemy = Config.Bind("Materials", "EnemyLitWeight", 0.5f,
                "Lit weight of enemies the game draws unlit (most of them): 0 = fullbright like the game, 1 = fully relit. " +
                "In between keeps RT shadows on them (docs/materials.md, \"Lit weight\"). Overridden by -ultrart-enemy-lit.").Value;
            u.enemyTypes = Config.Bind("Materials", "EnemyTypeLitWeights", "",
                "Per enemy type, over EnemyLitWeight: EnemyType=weight pairs, e.g. \"Filth=0.65, Stray=0.65\" (names as in the game's " +
                "EnemyType). Overridden by -ultrart-enemy-types.").Value;
            var args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length; i++)
            {
                string Next() => i + 1 < args.Length ? args[++i] : "";
                float F(float d) => float.TryParse(Next(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : d;
                bool On() { var v = Next().ToLowerInvariant(); return v != "off" && v != "0" && v != "false"; }
                switch (args[i].ToLowerInvariant())
                {
                    case "-ultrart-materials": s.enabled = On(); break;
                    case "-ultrart-materials-source": s.source = Next(); break;
                    case "-ultrart-materials-smooth": s.smooth = F(s.smooth); break;
                    case "-ultrart-materials-normal": s.normalStrength = F(s.normalStrength); break;
                    case "-ultrart-materials-roughness": s.roughness = F(s.roughness); break;
                    case "-ultrart-materials-lod": s.lod = On(); break;
                    case "-ultrart-materials-lod-bias": s.lodBias = F(s.lodBias); break;
                    case "-ultrart-materials-toksvig": s.toksvig = F(s.toksvig); break;
                    case "-ultrart-materials-albedo-lod": s.albedoLod = On(); break;
                    case "-ultrart-lit-unlit": u.fullbright = false; break; // relight unlit materials (the old look)
                    case "-ultrart-enemy-lit": u.enemy = F(u.enemy); break;
                    case "-ultrart-enemy-types": u.enemyTypes = Next(); break;
                    case "-ultrart-lit-rules": u.rules = On(); break;
                    case "-ultrart-glass-gain": SceneProxies.Glass.gain = F(SceneProxies.Glass.gain); break;
                    case "-ultrart-glass-extra":
                    {
                        var g = Next();
                        MaterialClassifier.TransmitExtra = new System.Text.RegularExpressions.Regex("^" + System.Text.RegularExpressions.Regex.Escape(g).Replace("\\*", ".*") + "$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                        break;
                    }
                    case "-ultrart-lit-probe":
                    {
                        var g = Next();
                        SceneProxies.LitProbe = new System.Text.RegularExpressions.Regex("^" + System.Text.RegularExpressions.Regex.Escape(g).Replace("\\*", ".*") + "$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                        break;
                    }
                }
            }
            Logger.LogInfo($"Material maps: {s}");
            var glass = SceneProxies.Glass;
            glass.enabled = Config.Bind("Materials", "StainedGlass", true,
                "Light passes stained glass, tinted by the panes; the leading casts shadows (docs/materials.md, \"Stained glass\"). " +
                "Off: opaque stained glass blocks light, transparent stained glass casts no shadow. Overridden by -ultrart-glass on|off.").Value;
            for (int i = 0; i + 1 < args.Length; i++)
                if (args[i].ToLowerInvariant() == "-ultrart-glass") glass.enabled = args[i + 1] != "off";
            Logger.LogInfo($"Lit weights: {u}");
            Logger.LogInfo($"Stained glass: {glass}");
            ParseStainedGlassArgs(); // stained-glass projection (Plugin.StainedGlass.cs)
        }

        private void MaterialsUpdate()
        {
            var s = MaterialMaps.Settings;
            try
            {
                if (_keysLive && Input.GetKeyDown(KeyCode.F1) && CtrlHeld())
                {
                    s.enabled = !s.enabled;
                    Logger.LogInfo($"Material maps: {(s.enabled ? "on" : "off")}");
                    Toast($"Material detail {(s.enabled ? "on" : "off")} (Ctrl+F1)");
                }
            }
            catch (InvalidOperationException) { /* legacy input disabled */ }
            Shader.SetGlobalVector(MaterialMaps.GlobalId, new Vector4(s.enabled ? 1f : 0f, s.normalStrength, s.roughness, 0f));
            // Texture LOD (Proxy.shader, PathTrace.raytrace; docs/materials.md "Texture LOD"): x detail LOD on, y bias
            // (mips), z grazing-angle stretch exponent, w secondary-hit albedo LOD on; _UltraRTTexLod2.x the slope variance
            // that becomes roughness (0 with LOD off), y the cone spread a diffuse bounce adds.
            Shader.SetGlobalVector(MaterialMaps.LodId, new Vector4(s.lod ? 1f : 0f, s.lodBias, s.lodAniso, s.lod && s.albedoLod ? 1f : 0f));
            Shader.SetGlobalVector(MaterialMaps.Lod2Id, new Vector4(s.lod ? s.toksvig : 0f, s.lodDiffuse, 0f, 0f));
            SceneProxies.SetGlassGlobals(_proxies != null && _proxies.HasGlass); // stained glass: shadow rays pass, tinted (SceneProxies.Glass.cs)
        }
    }
}
