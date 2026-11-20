using System.Collections.Generic;
using UnityEngine;

namespace UltraRT
{
    /// <summary>
    /// Lights through portals (docs/portals.md, "Scene lights through portals"; on by default since 2026-11-20). Layer 8's
    /// doorways join rooms that are far apart in the world. A ray from the camera teleports through them (TraceScene), but
    /// light sampling is in world space: a lamp in room A did not light room B through the doorway between them, so a
    /// bright room never spilled onto a dark one the way a real doorway does.
    ///
    /// Every packed point, spot and area light (not the sun, not fill lights, not an image itself) that shines at an
    /// active non-mirror portal side (from its back half-space, the game's rule, within 2 x range of the opening's
    /// rectangle, inside a spot's cone, in front of a one-sided emitter) gets a virtual copy: the light carried through the
    /// side's travel matrix (position, spot axis, area normal and tangent). The copy is marked with its side
    /// (l3.w = -(side + 1)); UltraRTLights.hlsl clips it to the exit's rectangular opening and traces its shadow in two
    /// segments, receiver to opening on the receiving side and opening to light on the light's side.
    ///
    /// Guards (no feedback, no double light):
    ///  - one hop: images are never imaged again (they are packed after every source);
    ///  - mirrors in all but name: a portal whose light makes a U-turn (8-2's starting pool, a floor portal into a copy of
    ///    the street) or whose path says "Mirror" (8-2's MirrorBig into a mirrored copy of the room): the copy behind it
    ///    has its own copies of the lights, as behind the game's real mirrors, which are never imaged;
    ///  - an image within 1 m of its light (a pair that leads back to the same place) is skipped;
    ///  - loop: an exit within <see cref="_portalLightLoopSpan"/> (100 m) that opens into the light's own room (the
    ///    light is on the side of the exit it would light: 8-3's sideways loops, 8-2's looping entrance hall and Black
    ///    Lodge corridor), or a local portal (exit within that span of its entrance) whose exit the light sees straight
    ///    through the level (8-3's Portals A/B at the Clockwork Crushers' ends; 8-2's BigDoors are as local but open into
    ///    another room behind a wall, and keep their images);
    ///  - contiguous: the exit opening is within the light's own reach in world space (8-3's Shifting Hallway, whose
    ///    doorways are 10 m apart along one hall): shadow rays ignore portals, so the real light already reaches what lies
    ///    behind the exit, straight through the world.
    /// Pairs are scored by the light's luminance, how close it is to the opening (in its reach) and how close the exit is
    /// to the camera or its portal viewpoints, with a bonus for last frame's pairs (no popping at the cap), and the best
    /// <see cref="_portalLightMax"/> are packed with the surface lights, before the lights that light the air only. Images
    /// light no air (_lightMedium 0): the volumetric pass stops at the portal plane.
    /// </summary>
    public partial class Plugin
    {
        /// <summary>-ultrart-portal-lights on|off, bench plugin._portalLights=0|1, settings menu Advanced "PortalLights".</summary>
        private bool _portalLights = true;
        /// <summary>Images per frame at most (bench plugin._portalLightMax).</summary>
        private int _portalLightMax = 16;
        /// <summary>Loop guard (bench plugin._portalLightLoopSpan, m): a portal whose exit is this close to its entrance, or an
        /// exit this close that opens into the light's own room, is skipped.</summary>
        private float _portalLightLoopSpan = 100f;
        /// <summary>Diagnostics (bench plugin._portalLightGuards=0): no loop or contiguous guard, to see what they prevent.</summary>
        private bool _portalLightGuards = true;
        private const int PortalLightReserve = 8; // slots left for the air-only lights packed after the images
        private const int PortalImagesPerLight = 4;
        private readonly List<SceneProxies.PortalImage> _portalImages = new List<SceneProxies.PortalImage>();
        private readonly List<(int light, SceneProxies.PortalImage im, float score, int id)> _portalCandidates = new List<(int, SceneProxies.PortalImage, float, int)>();
        private HashSet<int> _portalLightPrev = new HashSet<int>(), _portalLightNow = new HashSet<int>();
        private readonly HashSet<int> _portalLightLoggedPairs = new HashSet<int>();
        private int _portalLightLogs, _portalLightLogged = -1;
        private object _portalLightLogScene;
        private int _portalLightSetLogs;
        /// <summary>Images packed this frame (diagnostics).</summary>
        public int PortalLightImages { get; private set; }

        /// <summary>GatherLights, after the effect lights and before the air-only lights: the images of lights through portals.</summary>
        private void AppendPortalLightImages()
        {
            int added = 0, loops = 0, contiguous = 0, mirrors = 0;
            _portalCandidates.Clear();
            _portalLightNow.Clear();
            if (_portalLights && _portals && _proxies != null && _camera != null && _proxies.PortalSideCount > 0)
            {
                PlayerLightTestBegin();
                _proxies.BeginPortalImages(_camera.transform.position, _portalViews);
                int end = _lightCount;
                for (int i = 0; i < end; i++)
                {
                    var l0 = _lightData[i * 4];
                    var l1 = _lightData[i * 4 + 1];
                    var l2 = _lightData[i * 4 + 2];
                    var l3 = _lightData[i * 4 + 3];
                    if (l0.w == 0f || l3.z > 0.9f || l3.w < -0.5f || _lightMasks[i] == 0u) continue; // sun, fill light, image
                    float lum = 0.2126f * l1.x + 0.7152f * l1.y + 0.0722f * l1.z;
                    if (!(lum > 1e-3f)) continue;
                    bool area = l3.x < -1.5f;
                    uint code = area ? (uint)(Mathf.Max(l3.y, 0f) + 0.5f) : 0u;
                    var cone = !area && l3.x > -0.999f ? new Vector4(l2.x, l2.y, l2.z, l3.x) : new Vector4(0, 0, 0, -1f);
                    var emit = area && (code & 3u) >= 2u ? new Vector3(l2.x, l2.y, l2.z) : Vector3.zero;
                    float reach = 2f * l0.w; // the game's falloff ends at 2 x range
                    var p = new Vector3(l0.x, l0.y, l0.z);
                    _proxies.PortalImages(p, reach, cone, emit, _portalImages, PortalImagesPerLight);
                    foreach (var im in _portalImages)
                    {
                        int id = unchecked((int)(0x20000000u | ((uint)(SourceId(i) * 31 + im.Side) & 0x1FFFFFFFu)));
                        string skip = null;
                        if (_portalLightGuards)
                        {
                            if (im.MirrorLike) { skip = "mirror"; mirrors++; }
                            else if (im.ExitFacesLight && im.ExitDistance < Mathf.Max(reach, _portalLightLoopSpan)) { skip = "loop"; loops++; }
                            else if (im.Span < _portalLightLoopSpan && LightSeesExit(id, p, im.Exit)) { skip = "loop"; loops++; }
                            else if (im.ExitDistance < reach) { skip = "contiguous"; contiguous++; }
                        }
                        // How far the exit is from what the camera sees (the camera, or its images through portals), beyond
                        // the light's reach; PortalImages already left out exits more than ImageViewCull beyond it.
                        float outside = Mathf.Max(0f, im.ExitView - reach);
                        float near = 1f - Mathf.Clamp01(im.Distance / Mathf.Max(reach, 1e-3f));
                        float score = lum * (0.05f + near * near) / (1f + (outside / 30f) * (outside / 30f));
                        if (_portalLightPrev.Contains(id)) score *= 1.5f;
                        LogPortalLightPair(i, im, id, reach, score, skip);
                        if (skip == null) _portalCandidates.Add((i, im, score, id));
                    }
                }
                _portalCandidates.Sort((a, b) => b.score.CompareTo(a.score));
                int cap = Mathf.Min(_portalLightMax, MaxLights - PortalLightReserve - _lightCount);
                foreach (var cand in _portalCandidates)
                {
                    if (added >= cap) break;
                    PackPortalImage(cand.light, cand.im, cand.id);
                    _portalLightNow.Add(cand.id);
                    added++;
                }
            }
            if (!_portalLightNow.SetEquals(_portalLightPrev) && _portalLightSetLogs < 30 && added > 0)
            {
                _portalLightSetLogs++;
                var names = new System.Text.StringBuilder();
                for (int k = 0; k < added; k++)
                {
                    var cand = _portalCandidates[k];
                    var l = _packedLights[cand.light];
                    names.Append(k > 0 ? "; " : "").Append(!ReferenceEquals(l, null) && l != null ? l.name : "virtual").Append(" -> ").Append(cand.im.Side)
                         .Append(System.FormattableString.Invariant($" ({cand.score:F2})"));
                }
                Logger.LogInfo($"Portal light set ({added}): {names}");
            }
            var swap = _portalLightPrev; _portalLightPrev = _portalLightNow; _portalLightNow = swap;
            PortalLightImages = added;
            if (added != _portalLightLogged && _portalLightLogs < 40)
            {
                _portalLightLogs++;
                _portalLightLogged = added;
                Logger.LogInfo($"Portal light images: {added} of {_portalCandidates.Count} candidates (cap {_portalLightMax}); guards skipped {mirrors} mirror, {loops} loop, {contiguous} contiguous");
            }
        }

        /// <summary>Packs light i's image through one portal side at _lightCount (layout: UltraRTLights.hlsl, _Lights).</summary>
        private void PackPortalImage(int i, SceneProxies.PortalImage im, int id)
        {
            var l1 = _lightData[i * 4 + 1];
            var l2 = _lightData[i * 4 + 2];
            var l3 = _lightData[i * 4 + 3];
            var t = im.Travel;
            int j = _lightCount * 4;
            _lightData[j] = new Vector4(im.Pos.x, im.Pos.y, im.Pos.z, _lightData[i * 4].w);
            // The player's own lights keep their flag (V1 never shadows them; FlagPlayerLights leaves images alone).
            _lightData[j + 1] = new Vector4(l1.x, l1.y, l1.z, PlayerOwnedLight(i) ? 1f : l1.w);
            var axis = t.MultiplyVector(new Vector3(l2.x, l2.y, l2.z)); // spot axis or area normal, carried through
            if (l3.x < -1.5f)
            {
                uint code = (uint)(Mathf.Max(l3.y, 0f) + 0.5f);
                var tangent = t.MultiplyVector(LightOverrides.AreaTangent(code));
                LightOverrides.PackArea((int)(code & 3u), axis, tangent, l2.w, -l3.x - 2f, l3.z, 0f, out var a2, out var a3);
                _lightData[j + 2] = a2;
                l3 = a3;
            }
            else _lightData[j + 2] = new Vector4(axis.x, axis.y, axis.z, l2.w);
            _lightData[j + 3] = new Vector4(l3.x, l3.y, l3.z, -(im.Side + 1)); // l3.w < 0: a portal image of side -w - 1
            _lightMasks[_lightCount] = _lightMasks[i];
            _packedLights[_lightCount] = null;
            _lightIds[_lightCount] = id;
            _lightPlaced[_lightCount] = false;
            _lightMedium[_lightCount] = 0f; // the air behind a portal: the volumetric pass stops at the plane anyway
            _lightCount++;
        }

        private readonly Dictionary<int, (float until, bool sees)> _portalLightSight = new Dictionary<int, (float, bool)>();
        private object _portalLightSightScene;

        /// <summary>
        /// A local portal (exit within the loop span of its entrance) joins one space to itself when the light sees the
        /// exit's opening straight through the level (the game's colliders): 8-3's Portals A at the Clockwork Crushers'
        /// ends. 8-2's BigDoors are as local but open into another room behind a wall. Cached per pair for a second.
        /// </summary>
        private bool LightSeesExit(int id, Vector3 p, Vector3 exit)
        {
            if (!ReferenceEquals(_portalLightSightScene, _proxies)) { _portalLightSightScene = _proxies; _portalLightSight.Clear(); }
            float now = Time.realtimeSinceStartup;
            if (_portalLightSight.TryGetValue(id, out var c) && now < c.until) return c.sees;
            var to = exit - p;
            float len = to.magnitude;
            bool sees = true;
            if (len > 0.8f)
            {
                var dir = to / len;
                // From just off the light (out of its own fixture) to just before the opening (out of the wall it is set in).
                sees = !Physics.Raycast(p + dir * 0.3f, dir, len - 0.8f, LayerMaskDefaults.Get(LMD.Environment), QueryTriggerInteraction.Ignore);
            }
            if (_portalLightSight.Count > 4096) _portalLightSight.Clear();
            _portalLightSight[id] = (now + 1f + (id & 15) * 0.02f, sees); // spread the refreshes
            return sees;
        }

        private void LogPortalLightPair(int i, SceneProxies.PortalImage im, int id, float reach, float score, string skip)
        {
            if (!ReferenceEquals(_portalLightLogScene, _proxies)) { _portalLightLogScene = _proxies; _portalLightLoggedPairs.Clear(); }
            if (_portalLightLoggedPairs.Count >= 80 || !_portalLightLoggedPairs.Add(id)) return;
            var l = _packedLights[i];
            string name = !ReferenceEquals(l, null) && l != null ? l.name : $"light {_lightIds[i]:X}";
            Logger.LogInfo(System.FormattableString.Invariant(
                $"Portal light: '{name}' (reach {reach:F1} m) through {_proxies.PortalSideName(im.Side)}: {(skip != null ? "skipped, " + skip : "image")} at ({im.Pos.x:F1}, {im.Pos.y:F1}, {im.Pos.z:F1}), {im.Distance:F1} m from the opening, exit {im.ExitDistance:F1} m from the light{(im.ExitFacesLight ? " (opens into its room)" : "")}, score {score:F3}"));
        }

        private int SourceId(int i)
        {
            var l = _packedLights[i];
            return !ReferenceEquals(l, null) && l != null ? l.GetInstanceID() : _lightIds[i];
        }
    }
}
