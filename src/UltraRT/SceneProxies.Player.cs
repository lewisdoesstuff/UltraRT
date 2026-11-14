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
            foreach (var r in rs.Take(24))
                _log.LogInfo($"  player renderer '{GameControlPath(r.transform)}' {r.GetType().Name} layer {r.gameObject.layer} active {r.gameObject.activeInHierarchy} " +
                             $"enabled {r.enabled} mats [{string.Join(", ", r.sharedMaterials.Select(m => m != null ? $"{m.name} ({m.shader?.name})" : "null"))}]");
            ApplyPlayerAnimatorCulling();
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
            _playerBody = null;
            _playerBodyVerts = 0;
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

        private void NotePlayerProxy(Renderer source)
        {
            var d = _dynamic.Count > 0 ? _dynamic[_dynamic.Count - 1] : null;
            if (d == null || d.Source != source) return;
            _playerProxies.Add(d);
            if (d.SkinnedSource != null && d.SkinVerts > _playerBodyVerts) { _playerBody = d; _playerBodyVerts = d.SkinVerts; }
        }
    }
}
