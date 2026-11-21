using System.Linq;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace UltraRT
{
    /// <summary>
    /// Blended veils behind portals (docs/scene-proxies.md, "Blended veils"). The game draws soft blended layers (8-1's
    /// cloud sheets over the city) in its transparent pass; behind a portal that happens in the portal's own camera, and
    /// the traced image replaces those pixels, so the layers were missing there. While veil proxies are in the RTAS
    /// (SceneProxies.Veils.cs) one extra DispatchRays at screen resolution finds the pixels seen through a portal and
    /// collects the layers along their last segment (VeilTrace.raytrace); a fullscreen pass blends the result over the
    /// RT composite, before the game's transparents (VeilComposite.shader). No veil proxy in the RTAS: no cost.
    /// </summary>
    public partial class Plugin
    {
        private RayTracingShader _veilShader;
        private Material _veilCompositeMat;
        private RenderTexture _veilTex;
        private string _veilLogged;
        private int _veilLogs;
        private ComputeBuffer _veilFogBuffer;
        private static readonly int VeilTexId = Shader.PropertyToID("_VeilTex");
        /// <summary>Bench: plugin._coneCoverOverride K (>= 0) replaces the level's coneCover (-1: the level's).</summary>
        private float _coneCoverOverride = -1f;
        /// <summary>Bench A/B: plugin._veilMiniRtas=0 traces VeilTrace against the full scene RTAS instead of the tiny VeilRtas.</summary>
        private bool _veilMiniRtas = true;

        /// <summary>
        /// Light cones over the traced image (2026-11-21; the level's "exposure": { "coneCover": K }, docs/lighting-overrides.md
        /// "Light cones"). The game adds its LightPillar cones over the traced room, which is brighter than the game's: 0-3's
        /// emissive wall strips behind the yellow pillars stayed near white and showed through them as pale lines (user,
        /// pose 1). An additive cone only adds, and the camera target is 8-bit, so making the cone's own blend cover the
        /// image clipped its colour (tried: the pillars went pale). Instead the veil pass, which already collects the cones
        /// as additive layers (VeilTrace.raytrace), darkens the traced image behind them before the game draws them:
        /// x (1 - saturate(K x the brightest channel of the cones' added light)). The game's cone then adds as always.
        /// </summary>
        private float ConeCover()
        {
            if (_coneCoverOverride >= 0f) return _coneCoverOverride;
            return _lightOverrides && _lightOv != null ? _lightOv.LevelConeCover : 0f;
        }

        private void LoadVeilShaders(AssetBundle bundle)
        {
            _veilShader = bundle.LoadAllAssets<RayTracingShader>().FirstOrDefault(s => s.name == "VeilTrace");
            var proxy = bundle.LoadAllAssets<Shader>().FirstOrDefault(s => s.name == "UltraRT/VeilProxy");
            var composite = bundle.LoadAllAssets<Shader>().FirstOrDefault(s => s.name == "UltraRT/VeilComposite");
            if (_veilCompositeMat != null) Destroy(_veilCompositeMat);
            _veilCompositeMat = null;
            if (_veilShader == null || proxy == null || composite == null)
            {
                Logger.LogWarning($"Veil shaders missing (raytrace={(_veilShader ? "ok" : "MISSING")} proxy={(proxy ? "ok" : "MISSING")} composite={(composite ? "ok" : "MISSING")}): no veils behind portals");
                SceneProxies.VeilShader = null;
                _veilShader = null;
                return;
            }
            _veilCompositeMat = new Material(composite) { name = "UltraRT Veil Composite" };
            SceneProxies.VeilShader = proxy;
        }

        /// <summary>Trace(), right after the RT composite draw (the camera target is bound).</summary>
        private void TraceVeils(Kind kind, int mode)
        {
            var s = SceneProxies.Veils;
            if (_veilShader == null || _veilCompositeMat == null || !s.enabled || _proxies == null) return;
            if (kind != Kind.Denoised || mode != (int)DenoiserDebugView.Final) return; // the modes that replace the game's image
            float fogMode = !RenderSettings.fog ? 0f : RenderSettings.fogMode == FogMode.Linear ? 1f : RenderSettings.fogMode == FogMode.Exponential ? 2f : 3f;
            bool gameFog = _volumetrics != null && _volSettings.enabled && _volSettings.gameFog; // the fog pass (Plugin.Volumetrics.cs)
            bool portalFog = _proxies.PortalFogRows(out var fogRows); // a portal with its own fog is in the RTAS
            if (!portalFog && fogMode == 0f) gameFog = false;
            bool fog = s.fog > 0f && gameFog && _proxies.PortalSidesInRtas > 0;
            bool fogAlone = fog && (portalFog || fogMode > 1.5f || RenderSettings.fogEndDistance <= s.fogOnlyWithin);
            // Nothing is seen through a portal without a portal side in the RTAS, and the pass only writes such pixels.
            // (Since light cones count as veils, levels without any portal have veil proxies: 6-2 has 20, and the
            // pass ran there for nothing, +0.4 ms GPU and a DispatchRays on the render thread; merge-regress 2026-11-18.)
            // Light cones covering the traced image (ConeCover): every pixel, so not tied to a portal.
            float coneCover = _proxies.VeilsVisible > 0 && s.beams ? Mathf.Max(0f, ConeCover()) : 0f;
            if (_proxies.PortalSidesInRtas == 0 && coneCover <= 0f) return;
            if (_proxies.VeilsVisible == 0 && !fogAlone) return;
            float scale = Mathf.Clamp(s.scale, 0.25f, 1f);
            int w = Mathf.Max(1, Mathf.RoundToInt(_camera.pixelWidth * scale)), h = Mathf.Max(1, Mathf.RoundToInt(_camera.pixelHeight * scale));
            EnsureGBuffer(ref _veilTex, w, h, RenderTextureFormat.ARGBHalf, "UltraRT Veils");
            _veilTex.filterMode = FilterMode.Bilinear;
            var invViewProj = (_camera.projectionMatrix * _camera.worldToCameraMatrix).inverse; // no jitter, no guard band
            bool mini = _veilMiniRtas && _depthCopy != null && _gNormalDepth != null && _gInstanceId != null;
            RayTracingAccelerationStructure rtas = _proxies.Rtas;
            _gpu?.Begin(_cmd, SlotVeils);
            if (mini)
            {
                _proxies.UpdateVeilRtas();
                if (_proxies.VeilRtas != null && _proxies.VeilRtasInstances > 0)
                {
                    _cmd.BuildRayTracingAccelerationStructure(_proxies.VeilRtas);
                    rtas = _proxies.VeilRtas;
                }
                else mini = false;
            }
            _veilShader.SetShaderPass("UltraRTHit");
            _cmd.SetRayTracingAccelerationStructure(_veilShader, "_Scene", rtas);
            _cmd.SetRayTracingTextureParam(_veilShader, "_VeilOutput", _veilTex);
            _cmd.SetRayTracingMatrixParam(_veilShader, "_VeilInvViewProj", invViewProj);
            _cmd.SetRayTracingVectorParam(_veilShader, "_VeilCamPos", _camera.transform.position);
            _cmd.SetRayTracingVectorParam(_veilShader, "_VeilParams", new Vector4(Mathf.Max(0f, s.gain), Mathf.Max(0f, s.cover), s.everywhere, coneCover));
            float near = _camera.nearClipPlane, far = _camera.farClipPlane;
            Vector4 zp = SystemInfo.usesReversedZBuffer
                ? new Vector4(-1f + far / near, 1f, (-1f + far / near) / far, 1f / far)
                : new Vector4(1f - far / near, far / near, (1f - far / near) / far, far / near / far);
            float miniMode = !mini ? 0f : _proxies.PortalSidesInRtas > 0 ? 1f : 2f;
            _cmd.SetRayTracingVectorParam(_veilShader, "_VeilMini", new Vector4(miniMode, _guardScale.x, _guardScale.y, _flipY ? 1f : 0f));
            _cmd.SetRayTracingVectorParam(_veilShader, "_VeilZParams", zp);
            if (_depthCopy != null) _cmd.SetRayTracingTextureParam(_veilShader, "_VeilGameDepth", _depthCopy);
            if (_gNormalDepth != null) _cmd.SetRayTracingTextureParam(_veilShader, "_VeilNormalDepth", _gNormalDepth);
            if (_gInstanceId != null) _cmd.SetRayTracingTextureParam(_veilShader, "_VeilInstanceId", _gInstanceId);
            var fc = RenderSettings.fogColor;
            _cmd.SetRayTracingVectorParam(_veilShader, "_VeilFog", new Vector4(fogMode, RenderSettings.fogStartDistance, RenderSettings.fogEndDistance, RenderSettings.fogDensity));
            _cmd.SetRayTracingVectorParam(_veilShader, "_VeilFogColor", new Vector4(fc.r, fc.g, fc.b, fog ? Mathf.Clamp01(s.fog) : 0f));
            var fwd = _camera.transform.forward;
            _cmd.SetRayTracingVectorParam(_veilShader, "_VeilCamFwd", new Vector4(fwd.x, fwd.y, fwd.z, fogMode > 0f ? 1f : 0f));
            if (_veilFogBuffer == null || _veilFogBuffer.count < fogRows.Length)
            {
                _veilFogBuffer?.Release();
                _veilFogBuffer = new ComputeBuffer(fogRows.Length, 16, ComputeBufferType.Structured) { name = "UltraRT Portal Fog" };
            }
            _veilFogBuffer.SetData(fogRows);
            _cmd.SetRayTracingBufferParam(_veilShader, "_VeilPortalFog", _veilFogBuffer);
            _cmd.DispatchRays(_veilShader, "RayGen", (uint)w, (uint)h, 1);
            _cmd.SetGlobalTexture(VeilTexId, _veilTex);
            _cmd.DrawProcedural(Matrix4x4.identity, _veilCompositeMat, 0, MeshTopology.Triangles, 3);
            _gpu?.End(_cmd, SlotVeils);
            string state = $"{_proxies.VeilsVisible > 0}/{fog}/{portalFog}/{coneCover}/{mini}";
            if (state != _veilLogged && _veilLogs++ < 12)
            {
                _veilLogged = state;
                Logger.LogInfo($"Veil pass: {_proxies.VeilsVisible} veil proxies, {_proxies.PortalSidesInRtas} portal sides ({(mini ? $"mini RTAS {_proxies.VeilRtasInstances} inst" : "full RTAS")}), {w}x{h}, gain {s.gain} cover {s.cover}, cone cover {coneCover:F2}, fog behind portals {(fog ? $"x{s.fog} (level fog mode {fogMode}, {RenderSettings.fogStartDistance:F0}-{RenderSettings.fogEndDistance:F0} m{(portalFog ? ", a portal with its own fog" : "")})" : "off")}");
            }
        }
    }
}
