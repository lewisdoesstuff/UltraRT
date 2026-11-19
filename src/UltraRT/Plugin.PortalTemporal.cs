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
        private bool _matchDebug;      // bench plugin._matchDebug=1: Finish paints the pixels the game keeps by the nearest trace depth (diagnostic)
        private float _grazingMatch = 0.02f; // bench plugin._grazingMatch=0: DLSS RR's depth match without the bracket test (white rows on grazing floors)
        private bool _thinMatch = true;     // bench plugin._thinMatch=0: thin geometry keeps the game's pixel in pathtrace (grass sparkle)
        private float _thinNear = 0f;       // bench plugin._thinNear=0.1: unmatched thin pixels take the nearest-depth sample (off: breaks lava, docs/path-tracing.md)
        private bool _waterBand = true;     // bench plugin._waterBand=0: the water reflection ray uses every ripple (noisy and blocky at pathtrace's trace scale)
        private bool _hideHeld = true;      // bench plugin._hideHeld=0: a picked-up item stays in the RTAS (the held skull's ghost)
        private float _portalStart = 0f;   // bench plugin._portalStart=X: continued portal rays start X m past the exit plane (0: 1 mm)
        private bool _portalEdges = true;   // bench plugin._portalEdges=0: no fresh start at a moving portal's edge
        private bool _skinScale = true;     // bench plugin._skinScale=0: a skinned root's scale change in place is not followed
        private bool _portalMotion = true;  // bench plugin._portalMotion=0: no motion vectors behind moving portals
        private bool _portalJumpDiag;       // bench plugin._portalJumpDiag=1: log camera jumps near portals
        private Vector3 _portalPrevCam;
        private bool _portalDistances = true; // bench plugin._portalDistances=0: cull distances and scores by the camera alone (PortalView)
        private bool _cachePortalViews = true; // bench plugin._cachePortalViews=0: cache cells sized by the camera's distance alone
        private readonly System.Collections.Generic.List<Vector3> _portalViews = new System.Collections.Generic.List<Vector3>();
        private readonly Vector4[] _portalViewRows = new Vector4[16];
        private static readonly int CacheViewsId = Shader.PropertyToID("_UltraRTCacheViews"), CacheViewCountId = Shader.PropertyToID("_UltraRTCacheViewCount");

        /// <summary>Trace(), after SyncPortals: where the camera is as seen through each portal side it looks into
        /// (SceneProxies.PortalViewpoints). What is behind a portal is near one of these points and far from the camera
        /// in world space, so everything that weighs or sizes by camera distance uses them too: light importance,
        /// emitter importance, and the radiance cache's cell size (UltraRTCache.hlsl, CacheLevelF).</summary>
        private void PortalViews()
        {
            _portalViews.Clear();
            if (_portals) _proxies.PortalViewpoints(_camera.transform.position, _portalViews);
            PortalView.Enabled = _portalDistances;
            PortalView.Points.Clear();
            PortalView.Points.AddRange(_portalViews);
            int n = _cachePortalViews ? Mathf.Min(_portalViews.Count, _portalViewRows.Length) : 0;
            for (int i = 0; i < _portalViewRows.Length; i++) _portalViewRows[i] = i < n ? (Vector4)_portalViews[i] : Vector4.zero;
            // Immediate, not on _cmd: Trace clears the command buffer after this point.
            Shader.SetGlobalVectorArray(CacheViewsId, _portalViewRows);
            Shader.SetGlobalInt(CacheViewCountId, n);
        }
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
