using UnityEngine;

namespace UltraRT
{
    /// <summary>
    /// Temporal histories across a portal (docs/portals.md, "Temporal"). When the player walks through a portal the
    /// camera jumps to the other side: the world it sees is the one it saw through the portal a frame ago, but every
    /// history (denoiser, DLSS RR's motion vectors, upsampler, reflections, volumetrics, light sampling) looks the
    /// previous frame up with the previous camera, hundreds of metres away, finds nothing and starts again: a noisy,
    /// soft image for a moment. The jump is a rigid move, the portal's travel matrix T, so the previous frame's view of
    /// this frame's world is prevViewProj x T^-1; with that the lookups land where the same surfaces were.
    /// </summary>
    public partial class Plugin
    {
        private bool _portalRemap = true;   // bench plugin._portalRemap=0: off (histories restart at a crossing, as before)
        private bool _portalMotion = true;  // bench plugin._portalMotion=0: no motion vectors behind moving portals
        private bool _portalJumpDiag;       // bench plugin._portalJumpDiag=1: log camera jumps near portals
        private Vector3 _portalPrevCam;
        private int _portalPrevFrame = -10;
        private int _portalCrossLogs;

        /// <summary>Trace(), after SyncPortals and before any history is read.</summary>
        private void PortalCrossingRemap()
        {
            var cam = _camera.transform.position;
            SceneProxies.PortalJumpDiag = _portalJumpDiag;
            bool adjacent = Time.frameCount - _portalPrevFrame <= 2;
            var prev = _portalPrevCam;
            _portalPrevCam = cam;
            _portalPrevFrame = Time.frameCount;
            if (!adjacent || !_portals || !_proxies.PortalCrossing(prev, cam, out var travel, out string name)) return;
            if (_portalCrossLogs++ < 40)
                Logger.LogInfo($"Portal crossing: through '{name}', {(cam - prev).magnitude:F1} m, residual {(travel.MultiplyPoint3x4(prev) - cam).magnitude:F2} m" +
                               $"{(_portalRemap ? "" : " (remap off)")}");
            if (!_portalRemap) return;
            // The surfaces moved by the travel matrix and nothing else. What is left of the camera's jump (a step; in a
            // replayed path up to metres, the recording's exit being elsewhere) is camera motion, which the view matrix has.
            var nowToOld = travel.inverse;
            _denoiser?.RemapHistory(nowToOld);
            _denoiserDirect?.RemapHistory(nowToOld);
            _reflFilter?.RemapHistory(nowToOld);
            _lightSampling?.RemapHistory(nowToOld);
            _upsampler?.RemapHistory(nowToOld);
            _remodUpX?.RemapHistory(nowToOld);
            _remodUpD?.RemapHistory(nowToOld);
            _rr?.RemapHistory(nowToOld);
            _volumetrics?.RemapHistory(nowToOld, travel);
            _particleLight?.RemapHistory(nowToOld, travel);
        }
    }
}
