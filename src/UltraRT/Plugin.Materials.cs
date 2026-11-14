using System;
using System.Globalization;
using UnityEngine;

namespace UltraRT
{
    /// <summary>
    /// Per-texel material detail wiring (docs/materials.md, MaterialMaps.cs). Plugin.cs only has one-line hooks:
    /// ParseMaterialArgs (Awake) and MaterialsUpdate (OnUpdate).
    ///   -ultrart-materials on|off (config [Materials] Enabled), -ultrart-materials-source ai|proc (config [Materials] Source),
    ///   -ultrart-materials-normal F, -ultrart-materials-hybrid-normal F, -ultrart-materials-roughness F, -ultrart-materials-guided on|off. Ctrl+F1 toggles in game.
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
            s.normalStrength = Config.Bind("Materials", "NormalStrength", 2f, "Multiplier on the per-class normal strength (relight, pt-dn, pt).").Value;
            s.hybridNormalStrength = Config.Bind("Materials", "HybridNormalStrength", 1f, "Multiplier on the per-class normal strength in hybrid.").Value;
            s.roughness = Config.Bind("Materials", "Roughness", 1f, "Multiplier on the per-class roughness variation.").Value;
            var args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length; i++)
            {
                string Next() => i + 1 < args.Length ? args[++i] : "";
                float F(float d) => float.TryParse(Next(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : d;
                switch (args[i].ToLowerInvariant())
                {
                    case "-ultrart-materials": s.enabled = Next() != "off"; break;
                    case "-ultrart-materials-source": s.source = Next(); break;
                    case "-ultrart-materials-normal": s.normalStrength = F(s.normalStrength); break;
                    case "-ultrart-materials-hybrid-normal": s.hybridNormalStrength = F(s.hybridNormalStrength); break;
                    case "-ultrart-materials-roughness": s.roughness = F(s.roughness); break;
                    case "-ultrart-materials-guided": s.guidedPick = Next() != "off"; break;
                }
            }
            Logger.LogInfo($"Material maps: {s}");
        }

        private void MaterialsUpdate()
        {
            var s = MaterialMaps.Settings;
            try
            {
                if (Input.GetKeyDown(KeyCode.F1) && CtrlHeld())
                {
                    s.enabled = !s.enabled;
                    Logger.LogInfo($"Material maps: {(s.enabled ? "on" : "off")}");
                    Toast($"Material detail {(s.enabled ? "on" : "off")} (Ctrl+F1)");
                }
            }
            catch (InvalidOperationException) { /* legacy input disabled */ }
            float normal = Modes[_mode].kind == Kind.Hybrid ? s.hybridNormalStrength : s.normalStrength;
            Shader.SetGlobalVector(MaterialMaps.GlobalId, new Vector4(s.enabled ? 1f : 0f, normal, s.roughness, s.enabled && s.guidedPick ? 1f : 0f));
        }
    }
}
