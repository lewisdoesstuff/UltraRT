using System.Collections.Generic;
using UnityEngine;

namespace UltraRT
{
    /// <summary>
    /// The player's own lights (docs/reflections.md, "The player in reflections"): lights attached to the player or the
    /// camera (the 7-3 head-mounted Flashlight under Main Camera, the 4-3 torch in the hand, weapon and muzzle lights)
    /// and any light within <see cref="PlayerLightEye"/> of the eye (Torch.Update moves a held torch's light to the
    /// player). They get flag 1 in <c>_Lights[i*4+1].w</c>, and shadow rays toward them skip V1's model
    /// (UltraRTLights.hlsl, LightVisible). The camera box in VisibleMasked only covers lights that end up near the
    /// first-person gun; a light elsewhere on the player (or a soft-shadow sample at its edge) let V1 shadow its own
    /// flashlight.
    /// </summary>
    public partial class Plugin
    {
        private const float PlayerLightEye = 0.75f;
        private readonly Dictionary<Light, bool> _playerLightCache = new Dictionary<Light, bool>();
        private int _playerLightCacheVersion = -1;
        public int PlayerLightCount { get; private set; }

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
            int n = 0;
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
                var c = _lightData[i * 4 + 1];
                _lightData[i * 4 + 1] = new Vector4(c.x, c.y, c.z, owned ? 1f : 0f);
                if (owned) n++;
            }
            PlayerLightCount = n;
        }

        // ---------------------------------------------------------------- rain on surfaces (SceneProxies.Rain.cs)

        private static readonly int RainTimeId = Shader.PropertyToID("_UltraRTRainTime");
        private float _nextRainSync;
        private bool _rainOn = true; // -ultrart-norain; bench variant plugin._rainOn=0

        /// <summary>Every traced frame: the rain clock (the game shaders' _Time.y), and the per-area rain state at 4 Hz.</summary>
        private void UpdateRain()
        {
            Shader.SetGlobalFloat(RainTimeId, Time.timeSinceLevelLoad);
            bool changed = SceneProxies.RainEnabled != _rainOn;
            SceneProxies.RainEnabled = _rainOn;
            if ((!changed && Time.realtimeSinceStartup < _nextRainSync) || _proxies == null) return;
            _nextRainSync = Time.realtimeSinceStartup + 0.25f;
            _proxies.SyncRain();
        }
    }
}
