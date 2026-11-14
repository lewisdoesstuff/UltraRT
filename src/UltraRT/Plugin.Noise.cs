using System.Collections.Generic;
using UnityEngine;

namespace UltraRT
{
    /// <summary>
    /// Moving shadows (docs/denoiser.md, "Moving shadows"): uploads the boxes of objects that moved recently
    /// (SceneProxies.Movers.cs) for PathTrace's moving-occluder test.
    /// </summary>
    public partial class Plugin
    {
        /// <summary>Moving-occluder boxes on/off (bench: plugin._movers=0). The denoiser side is DenoiserSettings.moverAntilag.</summary>
        private bool _movers = true;
        /// <summary>Also enemies, doors and platforms, not only the player's model (bench: plugin._moverOthers=0).</summary>
        private bool _moverOthers = true;
        private ComputeBuffer _moverBuffer;
        /// <summary>Diagnostics: directional lights unshadowed (bench: plugin._sunsUnshadowed=1).</summary>
        private bool _sunsUnshadowed;
        private readonly Vector4[] _moverData = new Vector4[32];
        private int _moverCount;
        /// <summary>Player shadow split (docs/denoiser.md, "Moving shadows"): V1's shadow is kept out of the denoised lighting
        /// and applied after filtering (relight, pt-dn). Bench: plugin._playerSplit=0.</summary>
        private bool _playerSplit = true;
        private static readonly int PlayerModelMinId = Shader.PropertyToID("_UltraRTPlayerModelMin"), PlayerModelMaxId = Shader.PropertyToID("_UltraRTPlayerModelMax"),
            PlayerSplitId = Shader.PropertyToID("_UltraRTPlayerSplit");
        private void SetPlayerSplit(bool denoised) => Shader.SetGlobalInt(PlayerSplitId, _playerSplit && denoised ? 1 : 0);
        private static readonly int MoversId = Shader.PropertyToID("_UltraRTMovers"), MoverCountId = Shader.PropertyToID("_UltraRTMoverCount");

        private float _nextChangeLog;

        /// <summary>
        /// Eye lights (docs/light-sampling.md, "Eye lights"): a point light within 0.5 m of the camera (the game's player
        /// light, range 15, at the eye) can't cast a shadow on anything the camera sees, so it is evaluated like a fill light
        /// (every hit, no shadow ray, not cut by relight's fill cut) instead of competing for ReSTIR's single sample. In dark
        /// areas near the player it took a large share of the samples; 3-1's walkway, lit through rib gaps by a far spot
        /// light, boiled. Bench: plugin._eyeLightsUnshadowed=0.
        /// </summary>
        private bool _eyeLightsUnshadowed = true;
        private bool EyeLight(Vector3 pos) =>
            _eyeLightsUnshadowed && _camera != null && (pos - _camera.transform.position).sqrMagnitude < 0.25f;

        /// <summary>
        /// Baked level geometry (StaticSceneOptimizer, layers 6/7, traced as their original layers 24/8) is lit by the game
        /// through its own light buffer (_GlobalLightData + per-renderer light indices), which ignores culling masks; the
        /// optimizer even strips the baked layers from its lights' masks. Lights in its globalLights list therefore light
        /// the baked layers' originals here too (8-2's alley was lit in game by lights whose masks never included them).
        /// Bench: plugin._bakedLightMasks=0 for the old masks.
        /// </summary>
        private bool _bakedLightMasks = true;
        private readonly HashSet<Light> _globalLights = new HashSet<Light>();
        private object _globalLightsOwner;
        private float _nextGlobalLightScan;

        private uint BakedLightMask(Light l)
        {
            if (!_bakedLightMasks) return 0u;
            if (!ReferenceEquals(_globalLightsOwner, _proxies) || Time.realtimeSinceStartup >= _nextGlobalLightScan)
            {
                _globalLightsOwner = _proxies;
                _nextGlobalLightScan = Time.realtimeSinceStartup + 5f;
                _globalLights.Clear();
                foreach (var sso in FindObjectsOfType<StaticSceneOptimizer>())
                    if (sso != null && sso.globalLights != null)
                        foreach (var g in sso.globalLights) if (g != null) _globalLights.Add(g);
            }
            return _globalLights.Contains(l) ? (1u << 24) | (1u << 8) : 0u;
        }

        private void UpdateMovers()
        {
            if (_lightSampling != null && Time.realtimeSinceStartup >= _nextChangeLog)
            {
                _nextChangeLog = Time.realtimeSinceStartup + 10f;
                Logger.LogInfo($"Light sampling: {_lightSampling.TakeChangeStats()} in 10 s; movers {_moverCount}");
            }
            if (_moverBuffer == null || !_moverBuffer.IsValid()) _moverBuffer = new ComputeBuffer(_moverData.Length, 16) { name = "UltraRT movers" };
            SceneProxies.MoverOthers = _moverOthers;
            SceneProxies.MaxMovers = _movers ? 8 : 0;
            _moverCount = _proxies != null && _camera != null ? _proxies.CollectMovers(_camera.transform.position, _moverData) : 0;
            var pb = _proxies?.PlayerModelBounds;
            Shader.SetGlobalVector(PlayerModelMinId, pb.HasValue ? (Vector4)pb.Value.min : Vector4.one);
            Shader.SetGlobalVector(PlayerModelMaxId, pb.HasValue ? (Vector4)pb.Value.max : Vector4.zero);
            if (_moverCount > 0) _moverBuffer.SetData(_moverData, 0, 0, _moverCount * 2);
            Shader.SetGlobalBuffer(MoversId, _moverBuffer);
            Shader.SetGlobalInt(MoverCountId, _moverCount);
        }
    }
}
