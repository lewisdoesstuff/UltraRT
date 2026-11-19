using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using BepInEx.Logging;
using Newtonsoft.Json;
using UnityEngine;

namespace UltraRT
{
    /// <summary>ULTRAKILL's SurfaceType enum (a footstep-sound tag), plus two of ours. Values match the shaders' class ids.</summary>
    internal enum SurfaceClass
    {
        Generic = 0, Carpet = 1, Flesh = 2, Glass = 3, Grass = 4, Gravel = 5, Hologram = 6, Marble = 7, MetalStumpy = 8,
        MetalGrill = 9, MetalThin = 10, Rubble = 11, Sand = 12, Wet = 13, Wood = 14, WoodThin = 15, MetalHigh = 16,
        MetalLow = 17, Dirt = 18, Crunchy = 19, Snow = 20,
        /// <summary>Not an ULTRAKILL/Master material (or baked geometry whose original material is unknown).</summary>
        Other = 21,
        /// <summary>Name allow-list mirror (Mirror, MirrorTexture).</summary>
        Mirror = 22,
    }

    /// <summary>How the reflection is combined. Values match ULTRART_REFL_* in UltraRTMaterial.hlsl.</summary>
    internal enum ReflMode { None = 0, Specular = 1, Blend = 2, Additive = 3 }

    /// <summary>Per proxy material reflection parameters. Equality is on the 8-bit values the shaders see.</summary>
    internal struct MaterialParams : IEquatable<MaterialParams>
    {
        public SurfaceClass Class;
        public ReflMode Mode;
        public float Roughness, Metalness, F0, Weight, ReflStrength;
        /// <summary>Which rule decided it (for the dump).</summary>
        public string Source;
        /// <summary>Per-texel detail strengths from an override ("detail", "detailRoughness"); null = the class table
        /// (MaterialMaps.ClassNormal / ClassRoughness). Not part of equality: atlas cells bake them into the texture.</summary>
        public float? DetailNormal, DetailRoughness;

        public bool HasReflection => Mode != ReflMode.None;

        private static int Q(float x) => Mathf.RoundToInt(Mathf.Clamp01(x) * 255f);
        public bool Equals(MaterialParams o) => Class == o.Class && Mode == o.Mode && Q(Roughness) == Q(o.Roughness) && Q(Metalness) == Q(o.Metalness) &&
                                              Q(F0) == Q(o.F0) && Q(Weight) == Q(o.Weight) && Q(ReflStrength) == Q(o.ReflStrength);
        public override bool Equals(object obj) => obj is MaterialParams o && Equals(o);
        public override int GetHashCode() => ((int)Class * 31 + (int)Mode) * 16777619 ^ Q(Roughness) ^ (Q(Metalness) << 8) ^ (Q(F0) << 16) ^ (Q(Weight) << 24) ^ (Q(ReflStrength) * 7919);

        public override string ToString() => Mode == ReflMode.None
            ? $"{Class} none"
            : Mode == ReflMode.Specular
                ? $"{Class} specular r={Roughness:0.##} m={Metalness:0.##} f0={F0:0.###} w={Weight:0.##}"
                : $"{Class} {Mode.ToString().ToLowerInvariant()} rs={ReflStrength:0.###} r={Roughness:0.##}";

        public static MaterialParams None(SurfaceClass c, string source) =>
            new MaterialParams { Class = c, Mode = ReflMode.None, Roughness = 1f, F0 = 0.02f, Source = source };

        public static MaterialParams Spec(SurfaceClass c, float roughness, float metalness, float f0, float weight, string source) =>
            new MaterialParams { Class = c, Mode = ReflMode.Specular, Roughness = roughness, Metalness = metalness, F0 = f0, Weight = weight, Source = source };

        /// <summary>Writes the proxy material properties (Proxy.shader). <paramref name="secondary"/>: VERTEX_BLENDING set.</summary>
        public static void Apply(Material m, MaterialParams p, MaterialParams? secondary)
        {
            m.SetFloat("_Roughness", p.Roughness);
            m.SetFloat("_Metalness", p.Metalness);
            m.SetFloat("_F0", p.F0);
            m.SetFloat("_SpecWeight", p.Weight);
            m.SetFloat("_ReflMode", (float)p.Mode);
            m.SetFloat("_ReflStrength", p.ReflStrength);
            m.SetFloat("_MatClass", (float)p.Class);
            if (secondary is MaterialParams s)
            {
                m.SetFloat("_Secondary", 1f);
                m.SetFloat("_Roughness2", s.Roughness);
                m.SetFloat("_Metalness2", s.Metalness);
                m.SetFloat("_F02", s.F0);
                m.SetFloat("_SpecWeight2", s.Weight);
                m.SetFloat("_ReflMode2", (float)s.Mode);
                m.SetFloat("_MatClass2", (float)s.Class);
            }
            else m.SetFloat("_Secondary", 0f);
        }
    }

    /// <summary>
    /// The game's own cubemap reflection (ULTRAKILL/Master, REFLECTION keyword), copied to the proxy so the hit shader can
    /// reconstruct it for pattern cubes (_GameCubeLit; the hybrid composite that replaced it went on 2026-11-19). From the shader's DXBC:
    /// <c>C = cube(per-vertex reflect) * lerp(1, _Color, _ColorContribution) * lerp(1, vertex lighting, _LightingContribution)</c>,
    /// masked by <c>_ReflectionMask.r</c> and applied after emission and fog: blend (<c>_CubeMode</c> 0)
    /// <c>lit * (1 - m RS) + m^2 RS C</c>, additive <c>lit + m RS^2 C</c>, saturated.
    /// Independent of our classification (an override may change our mode; the game still draws its cube).
    /// </summary>
    internal static class GameReflection
    {
        /// <summary>Cubemaps that are a stylised pattern rather than a capture of an environment: DiamondCubemap (3-2's big
        /// eye, Eye, DiamondRed, Water 6 Reflection) and SwordCubemap (Gabriel's swords). An RT reflection in their place
        /// loses the pattern (the eye became a flat blob), so their materials keep the game's cube (Proxy.shader
        /// _GameCubeLit) and get no reflection of ours (<see cref="MaterialClassifier"/>).</summary>
        public static bool IsPatternCube(Texture cube) => cube != null && PatternCube.IsMatch(cube.name);

        private static readonly System.Text.RegularExpressions.Regex PatternCube =
            new System.Text.RegularExpressions.Regex(@"^(Diamond|Sword)Cubemap", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        public static bool Apply(Material proxy, Material src)
        {
            proxy.SetVector("_GameRefl", Vector4.zero);
            proxy.SetFloat("_GameCubeLit", 0f);
            if (src == null || src.shader == null || src.shader.name != "ULTRAKILL/Master" || !src.IsKeywordEnabled("REFLECTION")) return false;
            if (!src.HasProperty("_CubeTex") || !(src.GetTexture("_CubeTex") is Cubemap cube)) return false;
            float rs = src.HasProperty("_ReflectionStrength") ? src.GetFloat("_ReflectionStrength") : 1f;
            if (rs <= 0f) return false;
            bool additive = src.HasProperty("_CubeMode") && src.GetFloat("_CubeMode") != 0f; // the shader tests _CubeMode == 0
            float light = src.HasProperty("_LightingContribution") ? src.GetFloat("_LightingContribution") : 0f;
            float color = src.HasProperty("_ColorContribution") ? src.GetFloat("_ColorContribution") : 0f;
            proxy.SetTexture("_GameCube", cube);
            if (src.HasProperty("_ReflectionMask") && src.GetTexture("_ReflectionMask") is Texture mask && mask.dimension == UnityEngine.Rendering.TextureDimension.Tex2D) proxy.SetTexture("_GameReflMask", mask);
            proxy.SetVector("_GameRefl", new Vector4(additive ? 2f : 1f, rs, Mathf.Clamp01(light), Mathf.Clamp01(color)));
            proxy.SetFloat("_GameCubeLit", IsPatternCube(cube) ? 1f : 0f);
            return true;
        }
    }

    /// <summary>
    /// Material classification for reflections (docs/reflections.md):
    ///  1. ULTRAKILL/Master REFLECTION keyword with _ReflectionStrength > 0: the game's cubemap reflection, re-done with RT
    ///     (_CubeMode 0 blend, 1 additive), roughness from the strength.
    ///  2. Name allow-list mirrors and water: roughness 0.
    ///  3. _SurfaceType (and _SecondarySurfaceType with VERTEX_BLENDING) through the default table.
    ///  4. Other shaders: Standard's _Metallic/_Glossiness when clearly glossy, else nothing.
    ///  5. Override rules from ultrart-materials.json (plugin folder, then BepInEx/config), by material/texture/shader
    ///     name glob, applied in file order (later rules win).
    /// Unlit Master materials (no VERTEX_LIGHTING) and sky walls get no specular unless REFLECTION or an override says so.
    /// </summary>
    internal sealed class MaterialClassifier
    {
        public const string FileName = "ultrart-materials.json";

        private readonly ManualLogSource _log;
        private readonly List<Rule> _rules = new List<Rule>();
        private readonly Dictionary<Material, (MaterialParams p, MaterialParams? s)> _cache = new Dictionary<Material, (MaterialParams, MaterialParams?)>();
        private readonly Dictionary<Material, float?> _litCache = new Dictionary<Material, float?>();
        private readonly Dictionary<Material, float> _transmitCache = new Dictionary<Material, float>();
        /// <summary>The classifier of the current RT scene (SceneProxies.SubmeshOn reads "transmit" rules on source materials).</summary>
        public static MaterialClassifier Current;
        /// <summary>-ultrart-glass-extra GLOB (diagnostic): materials whose name matches transmit as stained glass (factor 1),
        /// whatever the rules say. "*" tints every shadow by its caster's colour: a check that the path works.</summary>
        public static Regex TransmitExtra;
        /// <summary>Materials a "transmit" rule matched (for the log).</summary>
        public int TransmitHits { get; private set; }
        /// <summary>Materials a "lit" rule matched (for the log).</summary>
        public int LitHits { get; private set; }
        private readonly List<string> _dump = new List<string>();
        public int OverrideHits { get; private set; }
        public string RuleSources { get; private set; } = "none";

#pragma warning disable CS0649 // assigned by Json.NET
        internal sealed class OverrideFile
        {
            [JsonProperty("rules")] public List<Rule> Rules;
        }

        internal sealed class Rule
        {
            [JsonProperty("material")] public string Material;
            [JsonProperty("texture")] public string Texture;
            [JsonProperty("shader")] public string Shader;
            [JsonProperty("class")] public string Class;
            [JsonProperty("roughness")] public float? Roughness;
            [JsonProperty("metalness")] public float? Metalness;
            [JsonProperty("f0")] public float? F0;
            [JsonProperty("specWeight")] public float? SpecWeight;
            [JsonProperty("reflMode")] public string ReflMode;
            [JsonProperty("reflStrength")] public float? ReflStrength;
            [JsonProperty("detail")] public float? Detail;
            [JsonProperty("detailRoughness")] public float? DetailRoughness;
            /// <summary>Lit weight 0-1 (0 = fullbright as the game draws unlit materials, 1 = fully relit). Independent of
            /// the reflection fields: a rule with only "lit" leaves the reflection alone (<see cref="LitOf"/>).</summary>
            [JsonProperty("lit")] public float? Lit;
            /// <summary>Stained glass: shadow rays pass, tinted by the texture colour x this (docs/materials.md "Stained
            /// glass"); 0 or none = opaque to light (transparent glass: no shadow at all, as before). Independent of the
            /// other fields, as "lit" (<see cref="TransmitOf"/>).</summary>
            [JsonProperty("transmit")] public float? Transmit;
            [JsonProperty("note")] public string Note;
            [JsonIgnore] public Regex MaterialRx, TextureRx, ShaderRx;
            [JsonIgnore] public string Origin;
        }
#pragma warning restore CS0649

        /// <param name="folders">Searched in order for <see cref="FileName"/>; every file found is loaded.</param>
        public MaterialClassifier(ManualLogSource log, IEnumerable<string> folders)
        {
            _log = log;
            var loaded = new List<string>();
            foreach (var dir in folders.Where(d => !string.IsNullOrEmpty(d)).Distinct())
            {
                var path = Path.Combine(dir, FileName);
                if (!File.Exists(path)) continue;
                try
                {
                    var file = JsonConvert.DeserializeObject<OverrideFile>(File.ReadAllText(path));
                    int n = 0;
                    foreach (var r in file?.Rules ?? new List<Rule>())
                    {
                        if (r == null || (r.Material == null && r.Texture == null && r.Shader == null)) continue;
                        r.MaterialRx = Glob(r.Material);
                        r.TextureRx = Glob(r.Texture);
                        r.ShaderRx = Glob(r.Shader);
                        r.Origin = $"{Path.GetFileName(dir)}/{FileName}#{n}";
                        if (r.Class != null && !Enum.TryParse(r.Class, true, out SurfaceClass _)) _log.LogWarning($"{path}: unknown class '{r.Class}'");
                        if (r.ReflMode != null && !Enum.TryParse(r.ReflMode, true, out ReflMode _)) _log.LogWarning($"{path}: unknown reflMode '{r.ReflMode}'");
                        _rules.Add(r);
                        n++;
                    }
                    loaded.Add($"{path} ({n} rules)");
                }
                catch (Exception e) { _log.LogWarning($"Material overrides {path} not loaded: {e.Message}"); }
            }
            if (loaded.Count > 0) RuleSources = string.Join(", ", loaded);
        }

        private static Regex Glob(string pattern) => string.IsNullOrEmpty(pattern) ? null
            : new Regex("^" + Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>The material's name without Unity's " (Instance)" suffixes.</summary>
        public static string CleanName(Material m)
        {
            var n = m != null ? m.name : "";
            while (n.EndsWith(" (Instance)", StringComparison.Ordinal)) n = n.Substring(0, n.Length - 11);
            return n;
        }

        public MaterialParams Classify(Material src, out MaterialParams? secondary)
        {
            if (src == null) { secondary = null; return MaterialParams.None(SurfaceClass.Other, "null"); }
            if (_cache.TryGetValue(src, out var hit)) { secondary = hit.s; return hit.p; }

            var p = Base(src, out secondary);
            string name = CleanName(src);
            string tex = src.HasProperty("_MainTex") && src.GetTexture("_MainTex") is Texture t ? t.name : "";
            string shader = src.shader != null ? src.shader.name : "";
            foreach (var r in _rules)
            {
                if (r.MaterialRx != null && !r.MaterialRx.IsMatch(name)) continue;
                if (r.TextureRx != null && !r.TextureRx.IsMatch(tex)) continue;
                if (r.ShaderRx != null && !r.ShaderRx.IsMatch(shader)) continue;
                // Rules that only set per-texel detail strengths (docs/materials.md) leave the reflection parameters alone.
                if (r.Class == null && !r.Roughness.HasValue && !r.Metalness.HasValue && !r.F0.HasValue && !r.SpecWeight.HasValue &&
                    !r.ReflStrength.HasValue && r.ReflMode == null)
                {
                    if (!r.Detail.HasValue && !r.DetailRoughness.HasValue) continue; // "lit" / "transmit" only: LitOf, TransmitOf
                    if (r.Detail.HasValue) p.DetailNormal = Mathf.Max(0f, r.Detail.Value);
                    if (r.DetailRoughness.HasValue) p.DetailRoughness = Mathf.Max(0f, r.DetailRoughness.Value);
                    if (secondary is MaterialParams sd) { sd.DetailNormal = p.DetailNormal; sd.DetailRoughness = p.DetailRoughness; secondary = sd; }
                    OverrideHits++;
                    continue;
                }
                if (r.Class != null && Enum.TryParse(r.Class, true, out SurfaceClass c)) p = ClassDefaults(c, r.Origin);
                if (r.Roughness.HasValue) p.Roughness = Mathf.Clamp01(r.Roughness.Value);
                if (r.Metalness.HasValue) p.Metalness = Mathf.Clamp01(r.Metalness.Value);
                if (r.F0.HasValue) p.F0 = Mathf.Clamp01(r.F0.Value);
                if (r.SpecWeight.HasValue) p.Weight = Mathf.Clamp01(r.SpecWeight.Value);
                if (r.ReflStrength.HasValue) p.ReflStrength = Mathf.Clamp01(r.ReflStrength.Value);
                if (r.ReflMode != null && Enum.TryParse(r.ReflMode, true, out ReflMode mode)) p.Mode = mode;
                if (r.Detail.HasValue) p.DetailNormal = Mathf.Max(0f, r.Detail.Value);
                if (r.DetailRoughness.HasValue) p.DetailRoughness = Mathf.Max(0f, r.DetailRoughness.Value);
                // A specular mode set without a weight would be invisible: default the weight to 1.
                if (p.Mode == ReflMode.Specular && p.Weight <= 0f && !r.SpecWeight.HasValue) p.Weight = 1f;
                p.Source = r.Origin;
                secondary = null; // an override owns the whole material
                OverrideHits++;
            }

            _cache[src] = (p, secondary);
            _dump.Add($"{name}\t{shader}\t{tex}\t{p}\t{p.Source}" + (secondary is MaterialParams s2 ? $"\tsecondary: {s2}" : ""));
            return p;
        }

        /// <summary>
        /// The lit weight a rule gives this material ("lit": 0 = fullbright, 1 = fully relit), or null when no rule sets
        /// one (SceneProxies.LitWeightOf then uses the shader: unlit Master fullbright, enemies partial, the rest lit).
        /// Matched like the reflection rules; the last matching rule with "lit" wins.
        /// </summary>
        public float? LitOf(Material src)
        {
            if (src == null) return null;
            if (_litCache.TryGetValue(src, out var hit)) return hit;
            float? lit = null;
            string name = CleanName(src);
            string tex = src.HasProperty("_MainTex") && src.GetTexture("_MainTex") is Texture t ? t.name : "";
            string shader = src.shader != null ? src.shader.name : "";
            foreach (var r in _rules)
            {
                if (!r.Lit.HasValue) continue;
                if (r.MaterialRx != null && !r.MaterialRx.IsMatch(name)) continue;
                if (r.TextureRx != null && !r.TextureRx.IsMatch(tex)) continue;
                if (r.ShaderRx != null && !r.ShaderRx.IsMatch(shader)) continue;
                lit = Mathf.Clamp01(r.Lit.Value);
            }
            if (lit.HasValue) LitHits++;
            _litCache[src] = lit;
            return lit;
        }

        /// <summary>The "transmit" factor a rule gives this material (stained glass, docs/materials.md), or 0. Matched like
        /// the reflection rules; the last matching rule with "transmit" wins.</summary>
        public float TransmitOf(Material src)
        {
            if (src == null) return 0f;
            if (_transmitCache.TryGetValue(src, out var hit)) return hit;
            float tr = 0f;
            string name = CleanName(src);
            string tex = src.HasProperty("_MainTex") && src.GetTexture("_MainTex") is Texture t ? t.name : "";
            string shader = src.shader != null ? src.shader.name : "";
            foreach (var r in _rules)
            {
                if (!r.Transmit.HasValue) continue;
                if (r.MaterialRx != null && !r.MaterialRx.IsMatch(name)) continue;
                if (r.TextureRx != null && !r.TextureRx.IsMatch(tex)) continue;
                if (r.ShaderRx != null && !r.ShaderRx.IsMatch(shader)) continue;
                tr = Mathf.Max(0f, r.Transmit.Value);
            }
            if (TransmitExtra != null && TransmitExtra.IsMatch(name)) tr = Mathf.Max(tr, 1f);
            if (tr > 0f) TransmitHits++;
            _transmitCache[src] = tr;
            return tr;
        }

        /// <summary>Relative weight of dielectric specular classes (metals use 1).</summary>
        public const float DielectricWeight = 0.6f;

        /// <summary>Default parameters of a class (the research table). Weights are relative: the global specular weight scales them.</summary>
        public static MaterialParams ClassDefaults(SurfaceClass c, string source)
        {
            switch (c)
            {
                // Dielectric weights are 0.6 of the metals' so that, with the global 0.5, they keep their old effective
                // weight (0.3 x 1.0) while metals reflect clearly (0.5).
                case SurfaceClass.Wood:
                case SurfaceClass.WoodThin: return MaterialParams.Spec(c, 0.75f, 0f, 0.03f, 0.3f, source);
                case SurfaceClass.Flesh: return MaterialParams.Spec(c, 0.45f, 0f, 0.03f, 0.36f, source);
                case SurfaceClass.Marble: return MaterialParams.Spec(c, 0.25f, 0f, 0.04f, DielectricWeight, source);
                case SurfaceClass.Glass: return MaterialParams.Spec(c, 0.05f, 0f, 0.04f, DielectricWeight, source);
                case SurfaceClass.Wet: return MaterialParams.Spec(c, 0.1f, 0f, 0.03f, DielectricWeight, source);
                case SurfaceClass.MetalHigh: return MaterialParams.Spec(c, 0.2f, 1f, 0.04f, 1f, source);
                case SurfaceClass.MetalLow: return MaterialParams.Spec(c, 0.35f, 0.8f, 0.04f, 1f, source);
                case SurfaceClass.MetalThin: return MaterialParams.Spec(c, 0.3f, 0.7f, 0.04f, 1f, source);
                case SurfaceClass.MetalStumpy:
                case SurfaceClass.MetalGrill: return MaterialParams.Spec(c, 0.4f, 0.6f, 0.04f, 1f, source);
                case SurfaceClass.Mirror: return new MaterialParams { Class = c, Mode = ReflMode.Blend, Roughness = 0f, F0 = 0.04f, ReflStrength = 0.6f, Source = source };
                // Generic, Carpet, Dirt, Sand, Snow, Grass, Gravel, Rubble, Crunchy (rough, 0.9-1.0), Hologram, Other.
                default: return MaterialParams.None(c, source);
            }
        }

        // Not FerrymanMirror: despite the name it is 5-3's painting of the Ferryman at an easel (a plain texture).
        private static bool IsMirrorName(string n) =>
            n.Equals("Mirror", StringComparison.OrdinalIgnoreCase) || n.Equals("MirrorTexture", StringComparison.OrdinalIgnoreCase) ||
            n.Equals("GlassReflective", StringComparison.OrdinalIgnoreCase);

        // "Water 10A", "Water 6 Still", "Limbo Water ...", "M_7S_Water", "M_WaterUps"; not signs like "WaterControl1".
        private static readonly Regex WaterName = new Regex(@"(^|[\s_])Water(?![A-Z])|WaterUps", RegexOptions.CultureInvariant);
        private static bool IsWaterName(string n) => WaterName.IsMatch(n);

        private static SurfaceClass TypeOf(Material m, string prop)
        {
            if (!m.HasProperty(prop)) return SurfaceClass.Generic;
            int v = Mathf.RoundToInt(m.GetFloat(prop));
            return v >= 0 && v <= 20 ? (SurfaceClass)v : SurfaceClass.Generic;
        }

        private MaterialParams Base(Material m, out MaterialParams? secondary)
        {
            secondary = null;
            string shader = m.shader != null ? m.shader.name : "";
            string name = CleanName(m);
            bool master = shader == "ULTRAKILL/Master";
            bool mirrorName = IsMirrorName(name);
            bool water = IsWaterName(name);

            if (master && m.IsKeywordEnabled("LIMBO_WALLS")) return MaterialParams.None(SurfaceClass.Other, "sky wall");

            // 1. REFLECTION keyword (gate on the keyword: _ReflectionStrength defaults to 1 on every other material).
            if (master && m.IsKeywordEnabled("REFLECTION") && m.HasProperty("_ReflectionStrength"))
            {
                float rs = m.GetFloat("_ReflectionStrength");
                // A pattern cube (DiamondCubemap, SwordCubemap) is part of the look, kept as the game draws it: no reflection.
                if (rs > 0f && m.HasProperty("_CubeTex") && GameReflection.IsPatternCube(m.GetTexture("_CubeTex")))
                    return MaterialParams.None(TypeOf(m, "_SurfaceType"), "pattern cube");
                if (rs > 0f)
                {
                    bool additive = m.HasProperty("_CubeMode") && Mathf.RoundToInt(m.GetFloat("_CubeMode")) == 1;
                    float rough = mirrorName || water ? 0f : rs >= 0.3f ? 0.02f : rs >= 0.1f ? 0.08f : 0.15f;
                    var cls = name.IndexOf("Mirror", StringComparison.OrdinalIgnoreCase) >= 0 ? SurfaceClass.Mirror : TypeOf(m, "_SurfaceType");
                    return new MaterialParams
                    {
                        Class = cls, Mode = additive ? ReflMode.Additive : ReflMode.Blend, Roughness = rough, F0 = 0.04f,
                        ReflStrength = Mathf.Clamp01(rs), Source = "REFLECTION",
                    };
                }
            }

            // 2. Name allow-list: fake mirrors (plain textures) and water.
            if (mirrorName) return ClassDefaults(SurfaceClass.Mirror, "mirror name");
            if (water && master)
                return MaterialParams.Spec(TypeOf(m, "_SurfaceType"), 0f, 0f, 0.02f, DielectricWeight, "water name");

            if (master)
            {
                // Unlit (fullbright) Master materials: screens, sky-ish props. The game draws them flat; leave them.
                if (!m.IsKeywordEnabled("VERTEX_LIGHTING")) return MaterialParams.None(TypeOf(m, "_SurfaceType"), "unlit");

                // 3. Surface type table, plus the secondary type on vertex-blended materials.
                var p = ClassDefaults(TypeOf(m, "_SurfaceType"), "SurfaceType");
                if (m.IsKeywordEnabled("VERTEX_BLENDING") && m.HasProperty("_SecondarySurfaceType"))
                {
                    var s = ClassDefaults(TypeOf(m, "_SecondarySurfaceType"), "SecondarySurfaceType");
                    if (!s.Equals(p)) secondary = s;
                }
                return p;
            }

            // 4. Unity Standard: only clearly glossy or metallic ones.
            if (shader == "Standard" && m.HasProperty("_Metallic") && m.HasProperty("_Glossiness"))
            {
                float metal = m.GetFloat("_Metallic"), gloss = m.GetFloat("_Glossiness");
                if (metal > 0.5f || gloss > 0.7f) return MaterialParams.Spec(SurfaceClass.Other, Mathf.Clamp01(1f - gloss), Mathf.Clamp01(metal), 0.04f, 1f, "Standard");
            }
            return MaterialParams.None(SurfaceClass.Other, "other shader");
        }

        // ---------------------------------------------------------------- reporting

        private readonly int[] _classCounts = new int[23];
        private readonly int[] _modeCounts = new int[4];

        /// <summary>Count one traced use (a proxy material, or an atlas slot) for the summary.</summary>
        public void Count(MaterialParams p)
        {
            _classCounts[Mathf.Clamp((int)p.Class, 0, 22)]++;
            _modeCounts[(int)p.Mode]++;
        }

        public string Summary()
        {
            var classes = Enumerable.Range(0, _classCounts.Length).Where(i => _classCounts[i] > 0)
                .OrderByDescending(i => _classCounts[i]).Select(i => $"{(SurfaceClass)i} {_classCounts[i]}");
            return $"by class: {string.Join(", ", classes)}; reflection modes: none {_modeCounts[0]}, specular {_modeCounts[1]}, " +
                   $"blend {_modeCounts[2]}, additive {_modeCounts[3]}; override hits {OverrideHits}, lit-weight rules matched {LitHits} materials, transmit rules {TransmitHits} (rules: {RuleSources})";
        }

        /// <summary>Tab-separated: name, shader, main texture, parameters, deciding rule (for tuning overrides).</summary>
        public void Dump(string path)
        {
            var sb = new StringBuilder("# material\tshader\ttexture\tparams\tsource\n");
            foreach (var line in _dump.OrderBy(s => s, StringComparer.OrdinalIgnoreCase)) sb.AppendLine(line);
            File.WriteAllText(path, sb.ToString());
        }
    }
}
