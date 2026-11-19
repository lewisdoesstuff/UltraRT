using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using BepInEx.Logging;
using UnityEngine;
using Object = UnityEngine.Object;

namespace UltraRT
{
    /// <summary>Ray-traced water and glass (docs/reflections.md, "Water and glass").</summary>
    internal enum TransparentKind { None = 0, Glass = 1, Water = 2, Blood = 3 }

    internal sealed class TransparentSettings
    {
        /// <summary>-ultrart-notransparent; F4 (reflections) also switches this pass.</summary>
        public bool enabled = true;
        /// <summary>-ultrart-transparent-strength: multiplies the Fresnel weight.</summary>
        public float strength = 1f;
        /// <summary>Cap on the reflection weight, so a surface never becomes a full mirror over what is drawn on it.</summary>
        public float maxWeight = 0.85f;
        /// <summary>-ultrart-water-waves: ripple slope scale (0 = flat mirror water).</summary>
        public float waves = 1f;
        /// <summary>Ripples fade out over this view distance (m).</summary>
        public float waveFade = 60f;
        /// <summary>-ultrart-water-refraction: screen-space refraction strength (0 = off, saves the colour copy).</summary>
        public float refraction = 1f;
        /// <summary>Lobe spread passed to the reflection-hit cache test: large = always shade from the radiance cache.</summary>
        public float cacheSpread = 1000f;
        /// <summary>Fresnel F0. Above the physical 0.02/0.04: the game's water and glass textures are bright and fairly
        /// opaque, which hides a physical-strength reflection everywhere but at grazing angles.</summary>
        public float waterF0 = 0.12f, glassF0 = 0.08f;
        /// <summary>Pathtrace: water tints a path by the game's blend alpha (texture alpha x colour alpha x opacity; full
        /// tint from 0.5 up) instead of a fixed share. Off: it changed little at the five poses tried and the flaw it was
        /// written for did not reproduce (bench trans.alphaTint=1; docs/reflections.md, "Water tint").</summary>
        public bool alphaTint = false;
        /// <summary>Liquid blood (0-2's river): thick and opaque, so a calmer, weaker sheen than water, no ripples in the
        /// reflection ray (traced like glass), no refraction (overlay _Kind 2), and a lower cap on the reflection weight.
        /// The game's blood is alpha blended and thin; with the floor under it relit (brighter, more contrast than the
        /// game's) it looked like water. <see cref="bloodAbsorb"/> absorbs what shows through toward the blood's colour
        /// (overlay pass "Absorb"); a weaker reflection keeps a glossy sheen without washing the colour out.</summary>
        public float bloodF0 = 0.06f, bloodWeight = 0.4f, bloodAbsorb = 0.9f;
        /// <summary>Frosted transparents (0-E's ice, <see cref="TransparentClassifier.Frosted"/>): reflection weight of the
        /// overlay. The game draws ice as a bright, fairly opaque frost; a full mirror-Fresnel reflection on top replaced
        /// that with the reflected room and the ice read as clear glass. A rough surface gives a faint sheen.</summary>
        public float frostedWeight = 0.25f;
        /// <summary>-ultrart-nofrosted / bench variant trans.frosted=0: frosted transparents leave the scene (they stay a
        /// transparent surface). Runtime switch, like LavaSettings.active.</summary>
        public bool frosted = true;
    }

    /// <summary>
    /// Which transparent ULTRAKILL/Master materials are water or glass. The game draws them in the transparent queue
    /// (alpha blended, mostly without depth writes), so they are not part of the opaque RT scene: they get their own
    /// instance mask (SceneProxies.TransparentMask) that only the transparent pass traces.
    /// </summary>
    internal static class TransparentClassifier
    {
        private static readonly Regex WaterName = new Regex(@"(^|[\s_])(Water|Sea)(?![A-Z])", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
        private static readonly Regex BloodLiquidName = new Regex(@"^Mulch|Blood(?!Splat|Spray|Drop|Decal|Stain)", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
        // Not surfaces: force fields, ghost curtains, plants and enemies tagged Wet, lamp shades tagged Glass.
        private static readonly Regex Excluded = new Regex(@"ForceField|Ghost|Succulent|Splash|Metal Light", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        public const string PsxWater = "psx/water";

        private static readonly Dictionary<Material, TransparentKind> Cache = new Dictionary<Material, TransparentKind>();

        public static TransparentKind Of(Material m)
        {
            if (m == null) return TransparentKind.None;
            if (Cache.TryGetValue(m, out var k)) return k;
            if (Cache.Count > 20000) Cache.Clear(); // materials of unloaded scenes
            return Cache[m] = Classify(m);
        }

        private static TransparentKind Classify(Material m)
        {
            if (m.shader == null) return TransparentKind.None;
            // psx/water: the older alpha-blended water shader (_OpacScale), e.g. the museum's fountain, pools and bird baths
            // ("Calm Water vapor"). A liquid surface; it has no _SurfaceType or keywords to check.
            if (m.shader.name == PsxWater)
            {
                // 0-2's crusher-arena blood tanks ("Blood") use it too: liquid blood, like Mulch.
                string pn = MaterialClassifier.CleanName(m);
                return Excluded.IsMatch(pn) ? TransparentKind.None : BloodLiquidName.IsMatch(pn) ? TransparentKind.Blood : TransparentKind.Water;
            }
            if (m.shader.name != "ULTRAKILL/Master") return TransparentKind.None;
            if (!m.IsKeywordEnabled("TRANSPARENCY")) return TransparentKind.None;
            // 2 = alpha blend. 3 (additive) is lava, acid and light shafts: glows, not surfaces.
            if (m.HasProperty("_BlendMode") && Mathf.RoundToInt(m.GetFloat("_BlendMode")) != 2) return TransparentKind.None;
            // Distance-faded waterfalls and stained-glass light shafts, sky walls, enemies.
            if (m.IsKeywordEnabled("DISTANCE_FADE") || m.IsKeywordEnabled("LIMBO_WALLS") || m.IsKeywordEnabled("ENEMY")) return TransparentKind.None;
            string n = MaterialClassifier.CleanName(m);
            if (Excluded.IsMatch(n)) return TransparentKind.None;
            int st = m.HasProperty("_SurfaceType") ? Mathf.RoundToInt(m.GetFloat("_SurfaceType")) : -1;
            if (WaterName.IsMatch(n) && (st == (int)SurfaceClass.Wet || n.Equals("Water", StringComparison.OrdinalIgnoreCase))) return TransparentKind.Water;
            // Liquid blood: 0-2's blood river ("Mulch") is the same alpha-blended Master liquid as 6-2's blood (a water
            // material), so it gets the same treatment. Opaque BloodPool and particle/decal blood never reach here.
            if (BloodLiquidName.IsMatch(n)) return TransparentKind.Blood;
            if (n.IndexOf("Glass", StringComparison.OrdinalIgnoreCase) >= 0) return TransparentKind.Glass;
            if (st == (int)SurfaceClass.Glass && n.StartsWith("Ice", StringComparison.OrdinalIgnoreCase)) return TransparentKind.Glass;
            return TransparentKind.None;
        }

        /// <summary>Frosted, nearly opaque transparents (0-E's ice): besides the transparent proxy they are scene geometry for
        /// every ray but the camera's, so reflections (the glass doors next to 0-E's ice), bounce and the radiance cache see
        /// them. Clear glass and water stay invisible to those rays.</summary>
        public static bool Frosted(Material m) =>
            Of(m) == TransparentKind.Glass && m.HasProperty("_SurfaceType") && Mathf.RoundToInt(m.GetFloat("_SurfaceType")) == (int)SurfaceClass.Glass
            && MaterialClassifier.CleanName(m).StartsWith("Ice", StringComparison.OrdinalIgnoreCase);

        /// <summary>Reflection parameters of the transparent proxy (the hit shader packs them into the payload).</summary>
        public static MaterialParams Params(TransparentKind kind, TransparentSettings s) =>
            kind == TransparentKind.Water ? MaterialParams.Spec(SurfaceClass.Wet, 0f, 0f, s.waterF0, 1f, "transparent water")
            : kind == TransparentKind.Blood ? MaterialParams.Spec(SurfaceClass.Glass, 0f, 0f, s.bloodF0, 1f, "transparent blood") // Glass class: no ripples
            : MaterialParams.Spec(SurfaceClass.Glass, 0f, 0f, s.glassF0, 1f, "transparent glass");
    }

    /// <summary>
    /// The overlay material (TransparentOverlay.shader) appended to each traced water/glass renderer, so Unity draws its
    /// last submesh once more right after the game's own material. The original material lists are restored on Dispose.
    /// </summary>
    internal sealed class TransparentOverlays : IDisposable
    {
        private readonly Shader _shader;
        private readonly TransparentSettings _settings;
        private readonly Dictionary<Material, Material> _materials = new Dictionary<Material, Material>();
        private readonly List<(Renderer r, Material[] original, Material overlay)> _attached = new List<(Renderer, Material[], Material)>();

        public TransparentOverlays(Shader shader, TransparentSettings settings)
        {
            _shader = shader;
            _settings = settings;
        }

        public int Count => _attached.Count;
        public bool Available => _shader != null && _shader.isSupported;

        // ---- Limbo water fade (docs/reflections.md, "Limbo water fade"). The game fades LIMBO_WATER_FADE water out
        // around the player: alpha = saturate(n + (d - near) / (far - near)), n = length(rg) of its _DitherTexture
        // (bayer16) at uv x 4, so inside the fade band the surface shows the texture's squares (1-1's fountain: the user's
        // "checkerboard", 2026-11-18). While frames are traced the dither is a flat, very large n: the water is not faded at
        // all (user, 2026-11-18: "drop the fade out on the water too"). A half-float texture, so n is not clamped to 1.
        public const float FlatFadeN = 1000f;
        private static Texture2D _flatDither;
        // Keyed by the material instance in use: the game instances a fading water's materials (renderer.materials, ours
        // included) after the overlay is attached, so the instances are found on the renderers, again every 30 frames.
        private readonly Dictionary<Material, (string prop, Texture original)> _fades = new Dictionary<Material, (string, Texture)>();
        private bool _flatFade;
        private int _fadeScanFrame = -1000;
        public int FadeCount => _fades.Count;

        private static Texture2D FlatDither()
        {
            if (_flatDither != null) return _flatDither;
            float c = FlatFadeN / Mathf.Sqrt(2f); // length(rg) = FlatFadeN: saturate(n + (d - near) / (far - near)) = 1
            _flatDither = new Texture2D(4, 4, TextureFormat.RGBAHalf, false, true) { name = "UltraRT no fade dither", filterMode = FilterMode.Point, hideFlags = HideFlags.DontUnloadUnusedAsset };
            var px = new Color[16];
            for (int i = 0; i < px.Length; i++) px[i] = new Color(c, c, 0f, 1f);
            _flatDither.SetPixels(px);
            _flatDither.Apply(false, true);
            return _flatDither;
        }

        /// <summary>Every frame from the plugin: no fade while frames are traced, the game's dither otherwise.</summary>
        public void SetFlatFade(bool on)
        {
            if (!on)
            {
                if (_flatFade) RestoreFades();
                _flatFade = false;
                return;
            }
            if (_flatFade && Time.frameCount - _fadeScanFrame < 30) return;
            _flatFade = true;
            _fadeScanFrame = Time.frameCount;
            var flat = FlatDither();
            foreach (var (r, _, _) in _attached)
            {
                if (r == null) continue;
                foreach (var m in r.sharedMaterials)
                {
                    if (m == null) continue;
                    string prop = IsOverlay(m) ? (m.HasProperty("_LimboFade") && m.GetFloat("_LimboFade") > 0.5f ? "_FadeDither" : null)
                                               : (m.IsKeywordEnabled("LIMBO_WATER_FADE") && m.HasProperty("_DitherTexture") ? "_DitherTexture" : null);
                    if (prop == null) continue;
                    var t = m.GetTexture(prop);
                    if (t == flat) continue;
                    _fades[m] = (prop, t);
                    m.SetTexture(prop, flat);
                }
            }
        }

        private void RestoreFades()
        {
            foreach (var kv in _fades) if (kv.Key != null) kv.Key.SetTexture(kv.Value.prop, kv.Value.original);
            _fades.Clear();
        }

        /// <summary>Appends the overlay for the renderer's last material slot (the submesh Unity repeats). False if that
        /// slot is not a water/glass material.</summary>
        public bool Attach(Renderer r)
        {
            if (!Available || r == null) return false;
            var mats = r.sharedMaterials;
            if (mats.Length == 0) return false;
            if (IsOverlay(mats[mats.Length - 1]))
            {
                // A copy of a renderer we already attached to (checkpoint room clones keep the material list): adopt it,
                // so Dispose removes it there too.
                if (mats.Length < 2 || TransparentClassifier.Of(mats[mats.Length - 2]) == TransparentKind.None) return false;
                var original = new Material[mats.Length - 1];
                Array.Copy(mats, original, original.Length);
                _attached.Add((r, original, mats[mats.Length - 1]));
                return true;
            }
            var src = mats[mats.Length - 1];
            var kind = TransparentClassifier.Of(src);
            if (kind == TransparentKind.None) return false;
            // Unity draws the last submesh again for an extra material. A static-batched renderer's MeshFilter holds the
            // combined mesh; Unity clamps the extra material to the renderer's own last submesh of it (subMeshStartIndex +
            // count - 1), so the check is for unbatched meshes only.
            if (r is MeshRenderer && !r.isPartOfStaticBatch && r.GetComponent<MeshFilter>()?.sharedMesh is Mesh mesh && mesh.subMeshCount > mats.Length) return false;
            if (!_materials.TryGetValue(src, out var overlay))
            {
                overlay = new Material(_shader) { name = src.name + " [UltraRT overlay]", renderQueue = src.renderQueue };
                if (src.HasProperty("_MainTex"))
                {
                    var tex = src.GetTexture("_MainTex");
                    if (tex != null && tex.dimension == UnityEngine.Rendering.TextureDimension.Tex2D) overlay.SetTexture("_MainTex", tex);
                    overlay.SetTextureScale("_MainTex", src.GetTextureScale("_MainTex"));
                    overlay.SetTextureOffset("_MainTex", src.GetTextureOffset("_MainTex"));
                }
                if (src.HasProperty("_Color")) overlay.SetColor("_Color", src.GetColor("_Color"));
                overlay.SetFloat("_Opacity", src.HasProperty("_Opacity") ? src.GetFloat("_Opacity") : src.HasProperty("_OpacScale") ? src.GetFloat("_OpacScale") : 1f);
                overlay.SetFloat("_Kind", kind == TransparentKind.Water ? 1f : kind == TransparentKind.Blood ? 2f : 0f);
                overlay.SetFloat("_F0", kind == TransparentKind.Water ? _settings.waterF0 : kind == TransparentKind.Blood ? _settings.bloodF0 : _settings.glassF0);
                overlay.SetFloat("_Weight", kind == TransparentKind.Blood ? _settings.bloodWeight : TransparentClassifier.Frosted(src) ? _settings.frostedWeight : 1f);
                overlay.SetFloat("_CullMode", src.HasProperty("_CullMode") ? src.GetFloat("_CullMode") : 2f);
                overlay.SetFloat("_Absorb", kind == TransparentKind.Blood ? _settings.bloodAbsorb : 0f);
                if (src.IsKeywordEnabled("LIMBO_WATER_FADE"))
                {
                    // The game hides this water around the player and enemies (1-3's floor puddles): follow its fade.
                    overlay.SetFloat("_LimboFade", 1f);
                    overlay.SetFloat("_NearClip", src.HasProperty("_NearClip") ? src.GetFloat("_NearClip") : 0f);
                    overlay.SetFloat("_FarClip", src.HasProperty("_FarClip") ? src.GetFloat("_FarClip") : 1f);
                    var dither = src.HasProperty("_DitherTexture") ? src.GetTexture("_DitherTexture") : null;
                    if (dither != null && dither.dimension == UnityEngine.Rendering.TextureDimension.Tex2D) overlay.SetTexture("_FadeDither", dither);
                }
                _materials[src] = overlay;
            }
            var withOverlay = new Material[mats.Length + 1];
            Array.Copy(mats, withOverlay, mats.Length);
            withOverlay[mats.Length] = overlay;
            r.sharedMaterials = withOverlay;
            _attached.Add((r, mats, overlay));
            return true;
        }

        /// <summary>True for our overlay materials (never traced, never classified).</summary>
        public static bool IsOverlay(Material m) => m != null && m.shader != null && m.shader.name == "UltraRT/TransparentOverlay";

        public void Dispose()
        {
            foreach (var (r, original, overlay) in _attached)
            {
                if (r == null) continue;
                // Put the original list back, unless a game script replaced the materials since: then keep its choice minus
                // ours (also instanced copies of ours, from scripts that touched renderer.materials).
                var now = r.sharedMaterials;
                if (now.Length == original.Length + 1 && now[now.Length - 1] == overlay) r.sharedMaterials = original;
                else if (now.Any(IsOverlay)) r.sharedMaterials = now.Where(m => !IsOverlay(m)).ToArray();
            }
            _attached.Clear();
            RestoreFades();
            _flatFade = false;
            foreach (var m in _materials.Values) if (m != null) Object.Destroy(m);
            _materials.Clear();
        }
    }
}
