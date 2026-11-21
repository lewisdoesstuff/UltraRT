using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace UltraRT
{
    /// <summary>
    /// V1's third-person model (docs/reflections.md, "The player in reflections"). The player prefab carries a full
    /// animated V1 (PlayerAnimations on Player/GroundCheck/v1_combined: the skinned v1_mdl plus the third-person weapons),
    /// on layer 30 so only portal views (8-x mirrors) draw it. Its proxies get their own instance mask,
    /// <see cref="PlayerMask"/>, which camera rays never include: the first-person view never shows it (the camera sits in
    /// its head), but reflection, bounce and shadow rays can (Plugin's _playerRays, global _UltraRTPlayerRays).
    /// </summary>
    internal sealed partial class SceneProxies
    {
        /// <summary>V1's third-person model. Not in ULTRART_MASK_SCENE; rays opt in per kind (UltraRTPortals.hlsl, PlayerRayMask).</summary>
        public const uint PlayerMask = 0x80;

        /// <summary>-ultrart-noplayer: leave the player model out of the RTAS (read at scene build).</summary>
        public static bool TracePlayerModel = true;

        /// <summary>
        /// Keep the model's Animator running although no camera draws it (Animator.cullingMode = AlwaysAnimate). Unity
        /// culls by the visibility of the animator's renderers; the source is on layer 30 (drawn only in portal views) and
        /// our proxy never renders, so with the default culling mode the pose could freeze outside 8-x.
        /// </summary>
        public static bool PlayerAlwaysAnimate = true;

        /// <summary>
        /// Light V1's fullbright materials like the scene in RT (<see cref="PlayerProxyOrDisabled"/>); false = fullbright
        /// as in the game's own mirrors (read when the model's proxies are made: bench variant needs a level reload).
        /// </summary>
        public static bool PlayerModelLit = true;

        /// <summary>
        /// -ultrart-v1-unlit F: the share of V1's own colour that shows whatever the light (0 = lit by the room only).
        /// A blue body under orange light is nearly black with grey highlights (1-1's terminal room, the trailer's "V1
        /// reads grey"): a small unlit share keeps the blue readable (the user's decision 7, docs/archive/plan-look-and-perf.md).
        /// Read when the model's proxies are made.
        /// </summary>
        public static float PlayerModelUnlit = 0.2f;

        private Transform _playerModelRoot;
        private Animator _playerAnimator;
        private AnimatorCullingMode _playerAnimatorCulling;
        private bool _playerAnimatorChanged;
        private readonly List<DynamicProxy> _playerProxies = new List<DynamicProxy>();

        /// <summary>Player model proxies currently in the RTAS.</summary>
        public int PlayerProxiesActive => _playerProxies.Count(d => d.Visible);
        public int PlayerProxiesTotal => _playerProxies.Count;

        /// <summary>Finds the third-person model's root at scene build: PlayerAnimations' transform (v1_combined).</summary>
        private void FindPlayerModel()
        {
            _playerModelRoot = null;
            _playerAnimator = null;
            FindPlayerHand(null);
            if (!TracePlayerModel) return;
            var anims = MonoSingleton.GetInstance(typeof(PlayerAnimations)) as PlayerAnimations; // NoAutoInstance: never creates one
            if (anims == null) anims = Object.FindObjectsByType<PlayerAnimations>(FindObjectsInactive.Include, FindObjectsSortMode.None).FirstOrDefault();
            if (anims == null)
            {
                _log.LogInfo("Player model: no PlayerAnimations in the scene; V1 is not traced");
                return;
            }
            _playerModelRoot = anims.transform;
            _playerAnimator = anims.GetComponent<Animator>();
            var rs = _playerModelRoot.GetComponentsInChildren<Renderer>(true);
            _log.LogInfo($"Player model: '{GameControlPath(_playerModelRoot)}' active {anims.gameObject.activeInHierarchy}, layer {anims.gameObject.layer}, " +
                         $"animator {(_playerAnimator != null ? $"enabled {_playerAnimator.enabled}, culling {_playerAnimator.cullingMode}, update {_playerAnimator.updateMode}" : "none")}, " +
                         $"{rs.Length} renderers:");
            foreach (var r in rs.Take(40))
                _log.LogInfo($"  player renderer '{GameControlPath(r.transform)}' {r.GetType().Name} layer {r.gameObject.layer} active {r.gameObject.activeInHierarchy} " +
                             $"enabled {r.enabled} mats [{string.Join(", ", r.sharedMaterials.Select(m => m != null ? $"{m.name} ({m.shader?.name}; {string.Join(" ", m.shaderKeywords)})" : "null"))}]");
            ApplyPlayerAnimatorCulling();
            FindPlayerHand(anims); // the held item's place in V1's left hand (SceneProxies.HeldItem.cs)
        }

        private void ApplyPlayerAnimatorCulling()
        {
            if (_playerAnimator == null || !PlayerAlwaysAnimate || _playerAnimator.cullingMode == AnimatorCullingMode.AlwaysAnimate) return;
            _playerAnimatorCulling = _playerAnimator.cullingMode;
            _playerAnimator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            _playerAnimatorChanged = true;
        }

        private void RestorePlayerAnimatorCulling()
        {
            if (_playerAnimatorChanged && _playerAnimator != null) _playerAnimator.cullingMode = _playerAnimatorCulling;
            _playerAnimatorChanged = false;
            _playerAnimator = null;
            _playerModelRoot = null;
            _playerProxies.Clear();
            _glowLinks.Clear();
            _glowWeapon = null;
            _glowWeaponSeen = false;
            _glowAppliedGain = -1f;
            _playerBody = null;
            _playerBodyVerts = 0;
            _playerLeftHand = null;
            _heldMirrors.Clear();
            _heldItem = null;
            _heldShown = null;
        }

        /// <summary>
        /// A renderer of V1's third-person model: under PlayerAnimations, never the first-person viewmodel (layer 13, under
        /// the main camera, which is not below PlayerAnimations anyway).
        /// </summary>
        private bool IsPlayerModel(Renderer r) =>
            _playerModelRoot != null && r.gameObject.layer != 13 && r.transform.IsChildOf(_playerModelRoot);

        /// <summary>Half-size of <see cref="PlayerShadowBox"/> around the camera (m): the first-person gun and its muzzle flash.</summary>
        public static float PlayerBoxCamera = 1.0f;
        /// <summary>Padding of the third-person weapon's bounds in <see cref="PlayerShadowBox"/> (m).</summary>
        public static float PlayerBoxPadding = 0.3f;

        /// <summary>
        /// Where the player's own lights are: a box around the camera (the first-person gun) grown to hold the third-person
        /// weapons in the RTAS (everything but the body), padded. Shadow rays toward a light inside it ignore the model
        /// (UltraRTLights.hlsl, VisibleMasked), so muzzle flashes and other effect lights at the gun aren't shadowed by the
        /// model's own arm and gun. The body is left out: its bounds (about 2.4 x 3.9 x 3.1 m with the wings) would exempt
        /// every light near the player. False when no player proxy is active.
        /// </summary>
        public bool PlayerShadowBox(Vector3 camPos, out Bounds box)
        {
            box = new Bounds(camPos, Vector3.one * (2f * PlayerBoxCamera));
            bool any = false;
            foreach (var d in _playerProxies)
            {
                if (!d.Visible || d.Proxy == null) continue;
                any = true;
                if (d == _playerBody) continue;
                var b = d.Proxy.bounds;
                b.Expand(2f * PlayerBoxPadding);
                box.Encapsulate(b);
            }
            return any;
        }

        /// <summary>The body (the skinned player renderer with the most vertices: v1_mdl).</summary>
        private DynamicProxy _playerBody;
        private int _playerBodyVerts;

        /// <summary>Diagnostics (600-frame log): is the model in the RTAS, animating, and where is it relative to the camera.</summary>
        public string DescribePlayerModel(Vector3 camPos)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append($"Player model: {PlayerProxiesActive}/{PlayerProxiesTotal} proxies in the RTAS");
            if (_playerAnimator != null)
            {
                var st = _playerAnimator.GetCurrentAnimatorStateInfo(0);
                sb.Append($"; animator enabled {_playerAnimator.isActiveAndEnabled}, culling {_playerAnimator.cullingMode}, state {st.shortNameHash} t {st.normalizedTime:F2}");
            }
            foreach (var d in _playerProxies)
            {
                if (!d.Visible || d.SkinnedSource == null || d.Root == null) continue;
                var b = d.Proxy != null ? d.Proxy.bounds : default;
                sb.Append($"; '{d.Source.name}' visible {d.Visible} source isVisible {d.Source.isVisible} root {d.Root.name} at camera {d.Root.position - camPos:F2}, " +
                          $"proxy bounds centre {b.center - camPos:F2} size {b.size:F2} contains camera {b.Contains(camPos)}");
            }
            return sb.ToString();
        }

        /// <summary>Scene build: where the mirror-named surfaces are (fake mirrors outside layer 8; docs/reflections.md).</summary>
        private void LogMirrorSurfaces()
        {
            int n = 0;
            foreach (var d in _dynamic)
            {
                if (d.Source == null || d.Mask == TransparentMask) continue;
                var m = d.Source.sharedMaterials.FirstOrDefault(x => x != null && (x.name.IndexOf("Mirror", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                                                                                     x.name.IndexOf("Terminal", System.StringComparison.OrdinalIgnoreCase) >= 0));
                if (m == null || n++ >= 16) continue;
                var b = d.Source.bounds;
                _log.LogInfo($"Mirror surface '{GameControlPath(d.Source.transform)}' material '{m.name}' layer {d.SourceObject.layer} active {d.SourceObject.activeInHierarchy} " +
                             $"bounds centre {b.center:F2} size {b.size:F2} forward {d.SourceTransform.forward:F2} mask 0x{d.Mask:X2}");
            }
        }

        /// <summary>
        /// V1's own emissive materials on the body (the wings, <c>V1Wing</c>) glow x this in RT: they are only ever seen in
        /// reflections, where the game's own yellow (emission 1) read faint, most of all in a terminal's CRT glass that
        /// reflects about a quarter head-on (user, 2026-11-20). Set every frame (Plugin's _v1Glow, bench
        /// plugin._v1Glow=F, -ultrart-v1-glow F); 1 = the game's emission. docs/reflections.md, "V1's glow".
        /// </summary>
        public static float PlayerEmissionGain = 2.5f;

        /// <summary>
        /// The third-person weapon in V1's hand glows as the first-person one does, x this (0 = not at all, as before
        /// 2026-11-21). The game's third-person guns (<c>M_Railcannon Unlit</c>, <c>Pistol New Unlit</c>, ...) have no
        /// EMISSIVE keyword: the game only draws them fullbright in 8-x's portal mirrors, so the railcannon's coils and
        /// pips were matte in every RT reflection while the viewmodel's glowed. Each third-person renderer is linked to the
        /// viewmodel renderer of the held weapon with the same name and main texture, and its proxy takes that renderer's
        /// live emission (colour and intensity, property block first: the railcannon's charge, its variation or custom
        /// colour) every frame. Plugin's _v1WeaponGlow, bench plugin._v1WeaponGlow=F, -ultrart-v1-weapon-glow F.
        /// </summary>
        public static float PlayerWeaponGlow = 1f;

        /// <summary>
        /// V1's camera eye (the yellow lens on its head, painted into the body texture of the fullbright <c>V1</c> material)
        /// glows x this in RT (0 = lit like the rest of the body, as before 2026-11-21). The body is lit by the room with a
        /// 20 % unlit share (<see cref="PlayerModelUnlit"/>), so in a dark room's mirror the lens read as dull ochre beside
        /// the glowing wings (user, playtest of 2026-11-21: "should the camera on v1's head glow?"). The lens texels are
        /// found once per material by colour (<see cref="BuildEyeMask"/>) and become the proxy's emission texture.
        /// Plugin's _v1EyeGlow, bench plugin._v1EyeGlow=F, -ultrart-v1-eye-glow F. docs/reflections.md, "V1's glow".
        /// </summary>
        public static float PlayerEyeGlow = 2.5f;
        private readonly Dictionary<Material, Texture2D> _eyeMasks = new Dictionary<Material, Texture2D>();

        private static readonly int IdEmissiveTexG = Shader.PropertyToID("_EmissiveTex");
        private static readonly int IdEmissiveMaskG = Shader.PropertyToID("_EmissiveMask");
        private static readonly int IdAlbedoEmissiveG = Shader.PropertyToID("_UseAlbedoAsEmissive");
        private static readonly int IdEmColorG = Shader.PropertyToID("_EmissiveColor");
        private static readonly int IdEmIntensityG = Shader.PropertyToID("_EmissiveIntensity");
        private static readonly int IdMainTexG = Shader.PropertyToID("_MainTex");

        /// <summary>A third-person renderer whose proxy mirrors a viewmodel renderer's emission, per material slot.</summary>
        private sealed class WeaponGlowLink
        {
            public DynamicProxy D;
            public Renderer Fp;
            public int[] FpSlot;         // per third-person slot: the viewmodel slot it follows, -1 none
            public Material[] MadeFrom;  // per slot: the source material the glow material was made from
            public Material[] Glow;      // per slot: our material on the proxy
            public Color[] LastColor;
            public float[] LastIntensity;
        }

        private readonly List<WeaponGlowLink> _glowLinks = new List<WeaponGlowLink>();
        private readonly List<Material> _glowMats = new List<Material>(4);
        private readonly List<Material> _glowFpMats = new List<Material>(4);
        private readonly MaterialPropertyBlock _glowBlock = new MaterialPropertyBlock();
        private GameObject _glowWeapon;
        private bool _glowWeaponSeen;
        private float _glowAppliedGain = -1f;

        /// <summary>Per frame (Plugin, after SyncDynamic): the wings' gain and the held weapon's glow.</summary>
        public void UpdatePlayerGlow()
        {
            if (_playerProxies.Count == 0) return;
            ApplyWingGain();

            GameObject current = null;
            if (PlayerWeaponGlow > 0f)
            {
                var gc = MonoSingleton.GetInstance(typeof(GunControl)) as GunControl; // never creates one
                if (gc != null) current = gc.currentWeapon;
            }
            if (!ReferenceEquals(current, _glowWeapon) || (current == null) != !_glowWeaponSeen)
            {
                ClearWeaponGlow();
                _glowWeapon = current;
                _glowWeaponSeen = current != null;
                if (current != null) BuildWeaponGlow(current);
            }
            for (int i = 0; i < _glowLinks.Count; i++) UpdateWeaponGlow(_glowLinks[i]);
        }

        /// <summary>The body's emissive materials (the wings) x <see cref="PlayerEmissionGain"/>.</summary>
        private void ApplyWingGain()
        {
            if (_playerBody == null || _playerBody.Source == null) return;
            float gain = Mathf.Max(0f, PlayerEmissionGain);
            _playerBody.Source.GetSharedMaterials(_glowMats);
            foreach (var src in _glowMats)
            {
                if (src != null && !src.IsKeywordEnabled("EMISSIVE")) ApplyEyeGlow(src);
                if (src == null || !src.IsKeywordEnabled("EMISSIVE") || !src.HasProperty(IdEmIntensityG)) continue;
                if (!_playerMaterialCache.TryGetValue(src, out var pm) || pm == null) continue;
                float want = Mathf.Max(src.GetFloat(IdEmIntensityG), 0.0001f) * gain;
                if (pm.GetFloat(IdEmIntensityG) != want) pm.SetFloat(IdEmIntensityG, want);
            }
            if (gain != _glowAppliedGain && _glowAppliedGain >= 0f) _log.LogInfo($"V1 glow: body emission x{gain:F2}");
            _glowAppliedGain = gain;
        }

        /// <summary>The body's lens texels (<see cref="BuildEyeMask"/>) glow x <see cref="PlayerEyeGlow"/> on its lit copy.</summary>
        private void ApplyEyeGlow(Material src)
        {
            if (!_playerMaterialCache.TryGetValue(src, out var pm) || pm == null || !pm.HasProperty(IdEmIntensityG)) return;
            if (!_eyeMasks.TryGetValue(src, out var mask))
                _eyeMasks[src] = mask = BuildEyeMask(src);
            if (mask == null) return;
            float want = Mathf.Max(0f, PlayerEyeGlow);
            if (pm.GetFloat(IdEmIntensityG) == want && ReferenceEquals(pm.GetTexture(IdEmissiveTexG), mask)) return;
            pm.SetTexture(IdEmissiveTexG, mask);
            pm.SetColor(IdEmColorG, Color.white);
            pm.SetFloat(IdAlbedoEmissiveG, 0f);
            pm.SetFloat(IdEmissiveMaskG, 0f);
            pm.SetFloat(IdEmIntensityG, want);
        }

        /// <summary>
        /// The texels of a V1 body material's main texture that show the camera lens: saturated yellow (min(r, g) - b over
        /// 0.25-0.4 and the brighter of r, g over 0.35-0.5; the body is blue and grey, the wings are their own material),
        /// as an emission texture (the texel's own colour where it keys, black elsewhere; point filtered like the game's
        /// textures). Null when nothing keys, or when more than 4 % of the texture does (then it is not just a lens).
        /// </summary>
        private Texture2D BuildEyeMask(Material src)
        {
            var tex = Tex2D(src, "_MainTex");
            if (tex == null) return null;
            int w = tex.width, h = tex.height;
            if (w <= 0 || h <= 0 || w > 4096 || h > 4096) return null;
            var rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            var prev = RenderTexture.active;
            Texture2D read = null, mask = null;
            try
            {
                Graphics.Blit(tex, rt);
                RenderTexture.active = rt;
                read = new Texture2D(w, h, TextureFormat.RGBA32, false, false);
                read.ReadPixels(new Rect(0, 0, w, h), 0, 0, false);
                var px = read.GetPixels32();
                var outPx = new Color32[px.Length];
                int keyed = 0, x0 = w, y0 = h, x1 = -1, y1 = -1;
                double sr = 0, sg = 0, sb = 0;
                for (int i = 0; i < px.Length; i++)
                {
                    float r = px[i].r / 255f, g = px[i].g / 255f, b = px[i].b / 255f;
                    float k = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.25f, 0.4f, Mathf.Min(r, g) - b))
                              * Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.35f, 0.5f, Mathf.Max(r, g)));
                    outPx[i] = new Color32((byte)(px[i].r * k), (byte)(px[i].g * k), (byte)(px[i].b * k), 255);
                    if (k < 0.5f) continue;
                    keyed++;
                    int x = i % w, y = i / w;
                    x0 = Mathf.Min(x0, x); x1 = Mathf.Max(x1, x); y0 = Mathf.Min(y0, y); y1 = Mathf.Max(y1, y);
                    sr += r; sg += g; sb += b;
                }
                float share = keyed / (float)px.Length;
                string where = keyed > 0 ? $"uv ({x0 / (float)w:F3}, {y0 / (float)h:F3})..({(x1 + 1) / (float)w:F3}, {(y1 + 1) / (float)h:F3}), mean colour ({sr / keyed:F2}, {sg / keyed:F2}, {sb / keyed:F2})" : "";
                if (keyed == 0 || share > 0.04f)
                {
                    _log.LogInfo($"V1 eye glow: '{src.name}' texture '{tex.name}' {w}x{h}: {keyed} lens texels ({share * 100f:F2} %) {where}: {(keyed == 0 ? "none" : "too many for a lens")}, not glowing");
                    return null;
                }
                mask = new Texture2D(w, h, TextureFormat.RGBA32, false, false) { name = src.name + " eye [UltraRT]", filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Repeat, hideFlags = HideFlags.HideAndDontSave };
                mask.SetPixels32(outPx);
                mask.Apply(false, true);
                _log.LogInfo($"V1 eye glow: '{src.name}' texture '{tex.name}' {w}x{h}: {keyed} lens texels ({share * 100f:F2} %) {where}, x{PlayerEyeGlow:F2}");
                return mask;
            }
            catch (System.Exception e)
            {
                _log.LogWarning($"V1 eye glow: '{src.name}': {e.Message}");
                if (mask != null) Object.Destroy(mask);
                return null;
            }
            finally
            {
                RenderTexture.active = prev;
                RenderTexture.ReleaseTemporary(rt);
                if (read != null) Object.Destroy(read);
            }
        }

        private void DisposeEyeMasks()
        {
            foreach (var m in _eyeMasks.Values) if (m != null) Object.Destroy(m);
            _eyeMasks.Clear();
        }

        private void BuildWeaponGlow(GameObject weapon)
        {
            var fps = weapon.GetComponentsInChildren<Renderer>(true);
            var notes = new List<string>();
            foreach (var d in _playerProxies)
            {
                if (d == _playerBody || d.Source == null || d.Proxy == null) continue;
                Renderer best = null;
                int bestScore = 0;
                foreach (var fp in fps)
                {
                    if (fp == null || fp.name != d.Source.name) continue;
                    int score = 1 + (fp.transform.parent != null && d.SourceTransform.parent != null && fp.transform.parent.name == d.SourceTransform.parent.name ? 1 : 0);
                    if (score > bestScore) { best = fp; bestScore = score; }
                }
                if (best == null) continue;
                d.Source.GetSharedMaterials(_glowMats);
                best.GetSharedMaterials(_glowFpMats);
                var slots = new int[_glowMats.Count];
                for (int i = 0; i < slots.Length; i++)
                {
                    slots[i] = -1;
                    var tm = _glowMats[i];
                    // Same slot first, else the viewmodel's first slot that fits.
                    for (int pass = 0; pass < 2 && slots[i] < 0; pass++)
                        for (int j = 0; j < _glowFpMats.Count; j++)
                        {
                            if (pass == 0 && j != i) continue;
                            var fm = _glowFpMats[j];
                            if (!GlowCompatible(tm, fm, out string why)) { if (pass == 0) notes.Add($"'{d.Source.name}' slot {i} '{tm?.name}' -> '{fm?.name}': {why}"); continue; }
                            slots[i] = j;
                            break;
                        }
                    if (slots[i] >= 0) notes.Add($"'{d.Source.name}' slot {i} '{tm?.name}' glows as '{GameControlPath(best.transform)}' '{_glowFpMats[slots[i]]?.name}'");
                }
                // Linked also without a glowing slot: the custom gun colours (CUSTOM_COLORS) are per renderer too.
                _glowLinks.Add(new WeaponGlowLink
                {
                    D = d, Fp = best, FpSlot = slots, MadeFrom = new Material[slots.Length], Glow = new Material[slots.Length],
                    LastColor = new Color[slots.Length], LastIntensity = new float[slots.Length],
                });
            }
            _log.LogInfo($"V1 weapon glow: '{weapon.name}' {_glowLinks.Count} linked renderer(s)" + (notes.Count > 0 ? ": " + string.Join("; ", notes.Take(16)) : ""));
        }

        /// <summary>A viewmodel material whose emission a third-person material can show: EMISSIVE, and when the emission
        /// is shaped by the main texture (albedo as emissive, the alpha mask), the same main texture.</summary>
        private static bool GlowCompatible(Material tp, Material fp, out string why)
        {
            why = null;
            if (tp == null || fp == null) { why = "no material"; return false; }
            if (!fp.IsKeywordEnabled("EMISSIVE") || !fp.HasProperty(IdEmColorG)) { why = "viewmodel not emissive"; return false; }
            bool mask = fp.HasProperty(IdEmissiveMaskG) && fp.GetFloat(IdEmissiveMaskG) > 0.5f;
            bool albedo = fp.IsKeywordEnabled("_USEALBEDOASEMISSIVE_ON") || fp.HasProperty(IdAlbedoEmissiveG) && fp.GetFloat(IdAlbedoEmissiveG) > 0.5f;
            var tt = tp.HasProperty(IdMainTexG) ? tp.GetTexture(IdMainTexG) : null;
            var ft = fp.HasProperty(IdMainTexG) ? fp.GetTexture(IdMainTexG) : null;
            if ((mask || albedo) && !ReferenceEquals(tt, ft)) { why = $"main texture '{tt?.name}' vs '{ft?.name}'"; return false; }
            if (!mask && !albedo && !(fp.HasProperty(IdEmissiveTexG) && fp.GetTexture(IdEmissiveTexG) != null) && !ReferenceEquals(tt, ft)) { why = "untextured emission on another texture"; return false; }
            return true;
        }

        private static readonly int IdIdTexG = Shader.PropertyToID("_IDTex");
        private static readonly int IdCustomColorsG = Shader.PropertyToID("_CustomColors");
        private static readonly int[] IdCustomG = { Shader.PropertyToID("_CustomColor1"), Shader.PropertyToID("_CustomColor2"), Shader.PropertyToID("_CustomColor3") };
        private readonly MaterialPropertyBlock _glowTpBlock = new MaterialPropertyBlock();

        /// <summary>CUSTOM_COLORS on a third-person gun material (GunColorGetter swaps in "... CustomColor" materials for
        /// alternate and custom gun colours) with a 2D colour-mask texture.</summary>
        private Texture CustomColorMask(Material src) =>
            src != null && src.IsKeywordEnabled("CUSTOM_COLORS") && src.HasProperty(IdIdTexG) ? Tex2D(src, "_IDTex") : null;

        private void UpdateWeaponGlow(WeaponGlowLink l)
        {
            var d = l.D;
            if (d.Source == null || d.Proxy == null || l.Fp == null) return;
            if (!d.Visible) return;
            d.Source.GetSharedMaterials(_glowMats);
            l.Fp.GetSharedMaterials(_glowFpMats);
            l.Fp.GetPropertyBlock(_glowBlock);
            bool tpBlockRead = false;
            Material[] proxyMats = null;
            for (int i = 0; i < l.FpSlot.Length && i < _glowMats.Count; i++)
            {
                var src = _glowMats[i];
                if (src == null) continue;
                int j = l.FpSlot[i];
                var fm = j >= 0 && j < _glowFpMats.Count ? _glowFpMats[j] : null;
                bool glow = fm != null && fm.IsKeywordEnabled("EMISSIVE");
                var idTex = CustomColorMask(src);
                if (!glow && idTex == null)
                {
                    // Nothing of ours to show (the gun's colours changed back): the plain lit variant.
                    if (l.Glow[i] != null)
                    {
                        if (proxyMats == null) proxyMats = d.Proxy.sharedMaterials;
                        if (i < proxyMats.Length && ReferenceEquals(proxyMats[i], l.Glow[i])) { proxyMats[i] = PlayerProxyOrDisabled(src); d.Proxy.sharedMaterials = proxyMats; }
                        RetireGlow(l.Glow[i]);
                        l.Glow[i] = null;
                        l.MadeFrom[i] = null;
                    }
                    continue;
                }
                var baseMat = PlayerProxyOrDisabled(src);
                if (baseMat == _disabledMaterial) continue;
                if (proxyMats == null) proxyMats = d.Proxy.sharedMaterials;
                if (i >= proxyMats.Length) continue;
                var g = l.Glow[i];
                bool fresh = g == null || !ReferenceEquals(l.MadeFrom[i], src);
                if (fresh)
                {
                    RetireGlow(g);
                    g = new Material(baseMat) { name = baseMat.name + " (V1 glow)" };
                    _ownMaterials.Add(g); // destroyed with the scene (SceneProxies.Sync.cs)
                    if (IsTwoSided(baseMat)) _twoSidedProxies.Add(g);
                    l.Glow[i] = g;
                    l.MadeFrom[i] = src;
                }
                if (fresh || (Time.frameCount & 15) == 0)
                {
                    // The lit variant's own values (lit weight, colour) follow it; the emission is the viewmodel's.
                    g.CopyPropertiesFromMaterial(baseMat);
                    if (glow)
                    {
                        bool mask = fm.HasProperty(IdEmissiveMaskG) && fm.GetFloat(IdEmissiveMaskG) > 0.5f;
                        bool albedo = fm.IsKeywordEnabled("_USEALBEDOASEMISSIVE_ON") || fm.HasProperty(IdAlbedoEmissiveG) && fm.GetFloat(IdAlbedoEmissiveG) > 0.5f;
                        g.SetFloat(IdEmissiveMaskG, mask ? 1f : 0f);
                        g.SetFloat(IdAlbedoEmissiveG, albedo ? 1f : 0f);
                        var et = fm.HasProperty(IdEmissiveTexG) ? Tex2D(fm, "_EmissiveTex") : null;
                        g.SetTexture(IdEmissiveTexG, et != null ? et : Texture2D.whiteTexture);
                    }
                    g.SetFloat(IdCustomColorsG, idTex != null ? 1f : 0f);
                    if (idTex != null) g.SetTexture(IdIdTexG, idTex);
                    l.LastIntensity[i] = -1f;
                }
                if (!ReferenceEquals(proxyMats[i], g)) { proxyMats[i] = g; d.Proxy.sharedMaterials = proxyMats; }

                if (idTex != null)
                {
                    // The colours live in a property block (the third-person renderer's, else the viewmodel's), falling
                    // back to the material's own values.
                    if (!tpBlockRead) { d.Source.GetPropertyBlock(_glowTpBlock); tpBlockRead = true; }
                    foreach (int id in IdCustomG)
                    {
                        Color cc = _glowTpBlock.HasColor(id) ? _glowTpBlock.GetColor(id) : _glowBlock.HasColor(id) ? _glowBlock.GetColor(id)
                                 : src.HasProperty(id) ? src.GetColor(id) : Color.white;
                        if (g.GetColor(id) != cc) g.SetColor(id, cc);
                    }
                }
                if (!glow)
                {
                    if (l.LastIntensity[i] != 0f) { g.SetFloat(IdEmIntensityG, 0f); l.LastIntensity[i] = 0f; }
                    continue;
                }
                Color c = _glowBlock.HasColor(IdEmColorG) ? _glowBlock.GetColor(IdEmColorG) : _glowBlock.HasVector(IdEmColorG) ? (Color)_glowBlock.GetVector(IdEmColorG) : fm.GetColor(IdEmColorG);
                float k = _glowBlock.HasFloat(IdEmIntensityG) ? _glowBlock.GetFloat(IdEmIntensityG) : fm.HasProperty(IdEmIntensityG) ? fm.GetFloat(IdEmIntensityG) : 1f;
                k = Mathf.Max(k, 0f) * Mathf.Max(PlayerWeaponGlow, 0f);
                if (c != l.LastColor[i] || k != l.LastIntensity[i])
                {
                    g.SetColor(IdEmColorG, c);
                    g.SetFloat(IdEmIntensityG, Mathf.Max(k, 0.0001f));
                    l.LastColor[i] = c;
                    l.LastIntensity[i] = k;
                }
            }
        }

        /// <summary>A glow material no proxy uses any more: destroyed a few frames later (SceneProxies.Sync.cs).</summary>
        private void RetireGlow(Material g)
        {
            if (g == null || !_ownMaterials.Remove(g)) return;
            _retiredMaterials.Add((g, Time.frameCount));
        }

        /// <summary>Puts the third-person proxies back on their plain (lit variant) materials.</summary>
        private void ClearWeaponGlow()
        {
            foreach (var l in _glowLinks)
            {
                var d = l.D;
                if (d.Source == null || d.Proxy == null) continue;
                var proxyMats = d.Proxy.sharedMaterials;
                bool changed = false;
                for (int i = 0; i < proxyMats.Length && i < l.Glow.Length; i++)
                {
                    if (l.Glow[i] == null || !ReferenceEquals(proxyMats[i], l.Glow[i])) continue;
                    proxyMats[i] = l.MadeFrom[i] != null ? PlayerProxyOrDisabled(l.MadeFrom[i]) : _disabledMaterial;
                    changed = true;
                }
                if (changed) d.Proxy.sharedMaterials = proxyMats;
                foreach (var g in l.Glow) RetireGlow(g);
            }
            _glowLinks.Clear();
            _glowWeapon = null;
            _glowWeaponSeen = false;
        }

        private void NotePlayerProxy(Renderer source)
        {
            var d = _dynamic.Count > 0 ? _dynamic[_dynamic.Count - 1] : null;
            if (d == null || d.Source != source) return;
            _playerProxies.Add(d);
            if (d.SkinnedSource != null && d.SkinVerts > _playerBodyVerts) { _playerBody = d; _playerBodyVerts = d.SkinVerts; }
        }
    }
}
