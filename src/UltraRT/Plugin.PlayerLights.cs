using System.Collections.Generic;
using UnityEngine;

namespace UltraRT
{
    /// <summary>
    /// The player's own lights (docs/reflections.md, "The player in reflections"): lights attached to the player or the
    /// camera (the 7-3 head-mounted Flashlight under Main Camera, the 4-3 torch in the hand, weapon and muzzle lights)
    /// and any light within <see cref="PlayerLightEye"/> of the eye (Torch.Update moves a held torch's light to the
    /// player). They get flag 1 in <c>_Lights[i*4+1].w</c>, and shadow rays toward them skip V1's model
    /// (UltraRTLights.hlsl, LightVisible and the V1-only ray of the player shadow split). Spawned lights (muzzle flashes,
    /// effect and virtual lights) also get it while they are inside the player box (SceneProxies.PlayerShadowBox: the
    /// camera and the third-person weapon). The level's own lights never get it for being near the player: the box used
    /// to be tested in the shader for every light, which took V1's shadow away under 0-2's first-room light whenever
    /// the camera stood within a metre of it.
    /// </summary>
    public partial class Plugin
    {
        private const float PlayerLightEye = 0.75f;
        /// <summary>
        /// A free-standing level fill light right beside V1 (horizontally within <see cref="BesideFar"/> of the model's
        /// centre, not above its head) would cast V1's shadow as a magnified silhouette on the far wall, from a point in
        /// mid-air where nothing glows: 5-2's cabin fill light (range 20, at chest height 1.2 m behind the player) put a
        /// giant V1 on the fireplace. V1 skips that light's shadow with a probability rising from 0 at BesideFar to 1 at
        /// BesideNear (flag 0..0.5; UltraRTLights.hlsl LightSkipsPlayer). Only the level's own lights with no visible body:
        /// a light in or on a fixture (torch, lamp, candle: SceneProxies.FixtureBounds / StaticEnclosed), a statue orb's, one
        /// placed by a light override, spawned lights (explosions) and lights above the head (the shadow lands at the feet:
        /// 0-2's first room) keep V1's shadow. Off by default: 0-2's first-room light (0.9 m from the eye, at body height) is
        /// exactly such a light and its V1 shadow is wanted there; 5-2's cabin is fixed by its light override instead.
        /// -ultrart-beside / bench plugin._besideV1=1. docs/relight.md "V1 beside a light".
        /// </summary>
        private bool _besideV1 = false;
        private const float BesideNear = 1.5f, BesideFar = 3f, BesideAbove = 0.25f;
        public int BesideLightCount { get; private set; }
        private readonly Dictionary<Light, bool> _playerLightCache = new Dictionary<Light, bool>();
        private int _playerLightCacheVersion = -1;
        public int PlayerLightCount { get; private set; }
        private Bounds _playerLightBox;

        /// <summary>End of GatherLights, after the effect lights: sets the player-light flag on every packed light.</summary>
        private void FlagPlayerLights()
        {
            if (_camera == null) return;
            // Hierarchies change (an item picked up, a weapon switched): the cache follows the 4 Hz active-light refresh.
            if (_playerLightCacheVersion != _activeLightsVersion)
            {
                _playerLightCacheVersion = _activeLightsVersion;
                _playerLightCache.Clear();
            }
            var player = MonoSingleton<NewMovement>.Instance != null ? MonoSingleton<NewMovement>.Instance.transform : null;
            var camT = _camera.transform;
            var eye = camT.position;
            bool haveBox = _proxies != null && _proxies.PlayerShadowBox(eye, out _playerLightBox);
            var model = _besideV1 ? _proxies?.PlayerModelBounds : null;
            int n = 0, beside = 0;
            for (int i = 0; i < _lightCount; i++)
            {
                var l0 = _lightData[i * 4];
                if (l0.w == 0f) continue; // directional
                bool owned = (new Vector3(l0.x, l0.y, l0.z) - eye).sqrMagnitude < PlayerLightEye * PlayerLightEye;
                var l = _packedLights[i];
                if (!owned && !ReferenceEquals(l, null) && l != null)
                {
                    if (!_playerLightCache.TryGetValue(l, out owned))
                    {
                        var t = l.transform;
                        owned = (player != null && t.IsChildOf(player)) || t.IsChildOf(camT);
                        _playerLightCache[l] = owned;
                    }
                }
                // Spawned (or virtual: no Light) lights at the first-person gun / third-person weapon: muzzle flashes.
                if (!owned && haveBox && (ReferenceEquals(l, null) || l == null || !_proxies.IsBuildLight(l)))
                    owned = _playerLightBox.Contains(new Vector3(l0.x, l0.y, l0.z));
                float flag = owned ? 1f : 0f;
                if (!owned && model.HasValue && !_lightPlaced[i] && FreeStandingLevelLight(l))
                {
                    var mb = model.Value;
                    float dx = l0.x - mb.center.x, dz = l0.z - mb.center.z;
                    float h = Mathf.Sqrt(dx * dx + dz * dz);
                    if (h < BesideFar && l0.y < mb.max.y + BesideAbove && l0.y > mb.min.y - BesideAbove)
                    {
                        flag = 0.5f * Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(BesideFar, BesideNear, h));
                        if (flag > 0f) beside++;
                    }
                }
                var c = _lightData[i * 4 + 1];
                _lightData[i * 4 + 1] = new Vector4(c.x, c.y, c.z, flag);
                if (owned) n++;
            }
            PlayerLightCount = n;
            BesideLightCount = beside;
        }

        /// <summary>A light the level placed at build with nothing around it: not in or on a fixture, not a statue orb's.</summary>
        private bool FreeStandingLevelLight(Light l) =>
            !ReferenceEquals(l, null) && l != null && _proxies != null && _proxies.IsBuildLight(l) &&
            !_proxies.FixtureBounds.ContainsKey(l) && !_proxies.StaticEnclosed.Contains(l) && !_proxies.OrbLights.Contains(l);

        // ---------------------------------------------------------------- rain on surfaces (SceneProxies.Rain.cs)

        private static readonly int RainTimeId = Shader.PropertyToID("_UltraRTRainTime");
        private float _nextRainSync;
        private bool _rainOn = true; // -ultrart-norain; bench variant plugin._rainOn=0
        private static readonly int TexScrollId = Shader.PropertyToID("_UltraRTTexScroll");
        /// <summary>SCROLLING textures move in the RT scene (-ultrart-notexscroll; bench variant plugin._texScroll=0).</summary>
        private bool _texScroll = true;
        private static readonly int ScrollMotionId = Shader.PropertyToID("_UltraRTScrollMotion");
        private static readonly int FrameDtId = Shader.PropertyToID("_UltraRTFrameDt");
        /// <summary>Scrolling surfaces report the texture's motion to the temporal filters (bench variant plugin._texScrollMotion=0).</summary>
        private bool _texScrollMotion = true;

        /// <summary>Every traced frame: the rain clock (the game shaders' _Time.y), and the per-area rain state at 4 Hz.</summary>
        private void UpdateRain()
        {
            Shader.SetGlobalFloat(RainTimeId, Time.timeSinceLevelLoad);
            Shader.SetGlobalFloat(TexScrollId, _texScroll ? 1f : 0f);
            Shader.SetGlobalFloat(ScrollMotionId, _texScrollMotion ? 1f : 0f);
            Shader.SetGlobalFloat(FrameDtId, Time.deltaTime);
            bool changed = SceneProxies.RainEnabled != _rainOn;
            SceneProxies.RainEnabled = _rainOn;
            if ((!changed && Time.realtimeSinceStartup < _nextRainSync) || _proxies == null) return;
            _nextRainSync = Time.realtimeSinceStartup + 0.25f;
            _proxies.SyncRain();
        }
    }
}
