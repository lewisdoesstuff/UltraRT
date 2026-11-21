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
    /// Fixtures whose light is handed over to their glowing part (Plugin.Handover.cs) are imaged with the whole fixture's
    /// strength (since 2026-11-21, docs/portals.md section 20): the part's emissive triangles are sampled in world space and
    /// light nothing behind a portal, so a hybrid (8-3's dome lamps, keep 0.35) lost most of its light at the portal's
    /// border and a light handed over in full (keep 0) all of it. A hybrid's image is its packed light x 1 / keep; a light
    /// handed over in full is packed in a scratch slot as a source of images only.
    ///
    /// Guards (no feedback, no double light):
    ///  - one hop: images are never imaged again (they are packed after every source);
    ///  - mirrors in all but name: a portal whose light makes a U-turn (8-2's starting pool, a floor portal into a copy of
    ///    the street) or whose path says "Mirror" (8-2's MirrorBig into a mirrored copy of the room): the copy behind it
    ///    has its own copies of the lights, as behind the game's real mirrors, which are never imaged;
    ///  - an image within 1 m of its light (a pair that leads back to the same place) is skipped;
    ///  - loop / contiguous: the exit is within <see cref="_portalLightLoopSpan"/> (100 m) or the light's reach of the
    ///    light, or within that span of its entrance, and the light reaches the room the exit opens into straight through
    ///    the level (<see cref="LightReachesExit"/>: rays to the opening on the game's colliders). Shadow rays ignore
    ///    portals, so the real light already lights what lies there: 8-3's sideways loops and Portals A/B in the Clockwork
    ///    Crushers, its Shifting Hallway (doorways 10 m apart along one hall), 8-2's looping entrance hall and Black Lodge
    ///    corridor. An exit in another room behind a wall (8-2's BigDoors, 8-3's Fallway) keeps its images.
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
        /// <summary>Loop guard (bench plugin._portalLightLoopSpan, m): an exit this close to the light or to its entrance that
        /// the light reaches straight through the level is skipped.</summary>
        private float _portalLightLoopSpan = 100f;
        /// <summary>Diagnostics (bench plugin._portalLightGuards=0): no loop or contiguous guard, to see what they prevent.</summary>
        private bool _portalLightGuards = true;
        /// <summary>Diagnostics (bench plugin._portalLightHalfSpace=1): the loop and contiguous guards as before 2026-11-21, by
        /// the exit's plane and distance alone (no line of sight).</summary>
        private bool _portalLightHalfSpace;
        /// <summary>Handed-over fixtures shine through portals with their whole strength (bench plugin._portalHandedLights=0:
        /// only packed lights at what they kept, as before 2026-11-21).</summary>
        private bool _portalHandedLights = true;
        /// <summary>A handed-over fixture's image x this, in linear power (bench plugin._portalHandedGain; 1 = the light it
        /// kept + its part, as LightResolved.HandedLinear).</summary>
        private float _portalHandedGain = 1f;
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

        /// <summary>A light that can be imaged: its packed data (UltraRTLights.hlsl layout) and what the ids and the log need.</summary>
        private struct PortalSource
        {
            public Vector4 L0, L1, L2, L3;
            public uint Mask;
            public bool Player, Handed;
            public int Id;
            public string Name;
        }
        /// <summary>This frame's sources that are not packed lights (fixtures handed over in full); candidate index -(k + 1).</summary>
        private readonly List<PortalSource> _portalGhosts = new List<PortalSource>();
        /// <summary>Lights handed over in full this frame (GatherLightsBody): not packed, but their fixtures shine through portals.</summary>
        private readonly List<Light> _handedOff = new List<Light>();

        /// <summary>GatherLights, after the effect lights and before the air-only lights: the images of lights through portals.</summary>
        private void AppendPortalLightImages()
        {
            int added = 0, loops = 0, contiguous = 0, mirrors = 0;
            _portalCandidates.Clear();
            _portalLightNow.Clear();
            _portalGhosts.Clear();
            if (_portalLights && _portals && _proxies != null && _camera != null && _proxies.PortalSideCount > 0)
            {
                PlayerLightTestBegin();
                _proxies.BeginPortalImages(_camera.transform.position, _portalViews);
                int end = _lightCount;
                for (int i = 0; i < end; i++)
                {
                    if (_lightData[i * 4 + 3].w < -0.5f || _lightData[i * 4].w == 0f) continue; // an image, a directional light
                    PortalCandidates(i, SourceOf(i), ref loops, ref contiguous, ref mirrors);
                }
                // Lights handed over in full (keep 0) are not packed: their fixture's part lights the room, and nothing
                // behind a portal. Each is packed in a scratch slot at full strength, as a source of images only.
                if (_portalHandedLights)
                    foreach (var l in _handedOff)
                    {
                        if (l == null) continue;
                        if (_lightCount >= MaxLights - PortalLightReserve) break;
                        var rl = ResolveLight(l);
                        if (rl == null || !(rl.HandedFull > 0f)) continue;
                        int slot = _lightCount;
                        float kept = rl.Intensity;
                        rl.Intensity = HandedImageIntensity(rl);
                        try { PackSceneLight(l, rl, null); }
                        finally { rl.Intensity = kept; }
                        if (_lightCount == slot) continue;
                        var src = new PortalSource
                        {
                            L0 = _lightData[slot * 4], L1 = _lightData[slot * 4 + 1], L2 = _lightData[slot * 4 + 2], L3 = _lightData[slot * 4 + 3],
                            Mask = _lightMasks[slot], Player = false, Id = l.GetInstanceID(), Name = l.name, Handed = true,
                        };
                        _packedLights[slot] = null;
                        _lightCount = slot; // the slot is the next image's (or air-only light's)
                        _portalGhosts.Add(src);
                        PortalCandidates(-_portalGhosts.Count, src, ref loops, ref contiguous, ref mirrors);
                    }
                _portalCandidates.Sort((a, b) => b.score.CompareTo(a.score));
                int cap = Mathf.Min(_portalLightMax, MaxLights - PortalLightReserve - _lightCount);
                foreach (var cand in _portalCandidates)
                {
                    if (added >= cap) break;
                    PackPortalImage(SourceOf(cand.light), cand.im, cand.id);
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
                    var src = SourceOf(cand.light);
                    names.Append(k > 0 ? "; " : "").Append(src.Name).Append(src.Handed ? " (fixture)" : "").Append(" -> ").Append(cand.im.Side)
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
                Logger.LogInfo($"Portal light images: {added} of {_portalCandidates.Count} candidates (cap {_portalLightMax}; {_portalGhosts.Count} lights handed over in full as sources); guards skipped {mirrors} mirror, {loops} loop, {contiguous} contiguous");
            }
        }

        /// <summary>Candidate index -> its source: packed light i (a hybrid fixture at its whole strength), or a scratch one.</summary>
        private PortalSource SourceOf(int light)
        {
            if (light < 0) return _portalGhosts[-light - 1];
            int i = light * 4;
            var l1 = _lightData[i + 1];
            // A hybrid fixture (a handover with a keep): packed at its keep, imaged with its whole fixture.
            float scale = HandedImageScale(_packedLights[light]);
            if (scale != 1f) l1 = new Vector4(l1.x * scale, l1.y * scale, l1.z * scale, l1.w);
            return new PortalSource
            {
                L0 = _lightData[i], L1 = l1, L2 = _lightData[i + 2], L3 = _lightData[i + 3], Mask = _lightMasks[light],
                Player = PlayerOwnedLight(light), Id = SourceId(light), Name = LightName(_packedLights[light], _lightIds[light]), Handed = scale != 1f,
            };
        }

        /// <summary>The colour factor (gamma) that takes a packed hybrid fixture light, at its keep, to its whole fixture.</summary>
        private float HandedImageScale(Light l)
        {
            if (!_portalHandedLights || ReferenceEquals(l, null) || l == null) return 1f;
            var rl = ResolveLight(l);
            if (rl == null || !(rl.HandedFull > 0f) || !(rl.Intensity > 0f) || rl.Intensity >= rl.HandedFull) return 1f;
            return HandedImageIntensity(rl) / rl.Intensity;
        }

        /// <summary>The image's intensity factor (on the gamma colour, like LightResolved.Intensity): the light's strength
        /// before the handover x (the fixture's linear output x <see cref="_portalHandedGain"/>)^(1/2.2).</summary>
        private float HandedImageIntensity(LightResolved rl) =>
            rl.HandedFull * Mathf.Pow(Mathf.Max(rl.HandedLinear * _portalHandedGain, 0f), 1f / 2.2f);

        private static string LightName(Light l, int id) => !ReferenceEquals(l, null) && l != null ? l.name : $"light {id:X}";

        /// <summary>The images of one source through the portal sides it shines at, with the guards and the score.</summary>
        private void PortalCandidates(int light, PortalSource src, ref int loops, ref int contiguous, ref int mirrors)
        {
            var l0 = src.L0; var l1 = src.L1; var l2 = src.L2; var l3 = src.L3;
            if (l0.w == 0f || l3.z > 0.9f || l3.w < -0.5f || src.Mask == 0u) return; // sun, fill light, image
            float lum = 0.2126f * l1.x + 0.7152f * l1.y + 0.0722f * l1.z;
            if (!(lum > 1e-3f)) return;
            bool area = l3.x < -1.5f;
            uint code = area ? (uint)(Mathf.Max(l3.y, 0f) + 0.5f) : 0u;
            var cone = !area && l3.x > -0.999f ? new Vector4(l2.x, l2.y, l2.z, l3.x) : new Vector4(0, 0, 0, -1f);
            var emit = area && (code & 3u) >= 2u ? new Vector3(l2.x, l2.y, l2.z) : Vector3.zero;
            float reach = 2f * l0.w; // the game's falloff ends at 2 x range
            if (area && (code & 3u) == 0u) reach += l2.w; // a line: as far from its ends
            var p = new Vector3(l0.x, l0.y, l0.z);
            _proxies.PortalImages(p, reach, cone, emit, _portalImages, PortalImagesPerLight);
            foreach (var im in _portalImages)
            {
                int id = unchecked((int)(0x20000000u | ((uint)(src.Id * 31 + im.Side) & 0x1FFFFFFFu)));
                string skip = null;
                bool? sees = null;
                if (_portalLightGuards)
                {
                    if (im.MirrorLike) { skip = "mirror"; mirrors++; }
                    else if (_portalLightHalfSpace)
                    {
                        // The rules before 2026-11-21 (diagnostics): no line of sight for the first and the last.
                        if (im.ExitFacesLight && im.ExitDistance < Mathf.Max(reach, _portalLightLoopSpan)) { skip = "loop"; loops++; }
                        else if (im.Span < _portalLightLoopSpan && LightReachesExit(id, p, im)) { skip = "loop"; loops++; }
                        else if (im.ExitDistance < reach) { skip = "contiguous"; contiguous++; }
                    }
                    else if (im.ExitDistance < Mathf.Max(reach, _portalLightLoopSpan) || im.Span < _portalLightLoopSpan)
                    {
                        sees = LightReachesExit(id, p, im);
                        if (sees.Value)
                        {
                            if (im.ExitFacesLight || im.ExitDistance >= reach) { skip = "loop"; loops++; }
                            else { skip = "contiguous"; contiguous++; }
                        }
                    }
                }
                // How far the exit is from what the camera sees (the camera, or its images through portals), beyond
                // the light's reach; PortalImages already left out exits more than ImageViewCull beyond it.
                float outside = Mathf.Max(0f, im.ExitView - reach);
                float near = 1f - Mathf.Clamp01(im.Distance / Mathf.Max(reach, 1e-3f));
                float score = lum * (0.05f + near * near) / (1f + (outside / 30f) * (outside / 30f));
                if (_portalLightPrev.Contains(id)) score *= 1.5f;
                LogPortalLightPair(src, im, id, reach, score, skip, sees);
                if (skip == null) _portalCandidates.Add((light, im, score, id));
            }
        }

        /// <summary>Packs a source's image through one portal side at _lightCount (layout: UltraRTLights.hlsl, _Lights).</summary>
        private void PackPortalImage(PortalSource src, SceneProxies.PortalImage im, int id)
        {
            var l1 = src.L1;
            var l2 = src.L2;
            var l3 = src.L3;
            var t = im.Travel;
            int j = _lightCount * 4;
            _lightData[j] = new Vector4(im.Pos.x, im.Pos.y, im.Pos.z, src.L0.w);
            // The player's own lights keep their flag (V1 never shadows them; FlagPlayerLights leaves images alone).
            _lightData[j + 1] = new Vector4(l1.x, l1.y, l1.z, src.Player ? 1f : l1.w);
            var axis = t.MultiplyVector(new Vector3(l2.x, l2.y, l2.z)); // spot axis or area normal, carried through
            if (LightOverrides.IsLine(l3))
            {
                // A line (a beam): the same segment through the portal.
                var ax = axis.normalized * l2.w;
                LightOverrides.PackLine(im.Pos - ax, im.Pos + ax, -l3.x - 2f, l3.z, 0f, out _, out var a2, out var a3);
                _lightData[j + 2] = a2;
                l3 = a3;
            }
            else if (l3.x < -1.5f)
            {
                uint code = (uint)(Mathf.Max(l3.y, 0f) + 0.5f);
                var tangent = t.MultiplyVector(LightOverrides.AreaTangent(code));
                LightOverrides.PackArea((int)(code & 3u), axis, tangent, l2.w, -l3.x - 2f, l3.z, 0f, out var a2, out var a3);
                _lightData[j + 2] = a2;
                l3 = a3;
            }
            else _lightData[j + 2] = new Vector4(axis.x, axis.y, axis.z, l2.w);
            _lightData[j + 3] = new Vector4(l3.x, l3.y, l3.z, -(im.Side + 1)); // l3.w < 0: a portal image of side -w - 1
            _lightMasks[_lightCount] = src.Mask;
            _packedLights[_lightCount] = null;
            _lightIds[_lightCount] = id;
            _lightPlaced[_lightCount] = false;
            _lightMedium[_lightCount] = 0f; // the air behind a portal: the volumetric pass stops at the plane anyway
            _lightCount++;
        }

        private readonly Dictionary<int, (float until, bool sees)> _portalLightSight = new Dictionary<int, (float, bool)>();
        private readonly Dictionary<int, string> _portalLightBlocker = new Dictionary<int, string>();
        private readonly Dictionary<int, bool> _portalOpaqueCollider = new Dictionary<int, bool>();
        private readonly RaycastHit[] _portalSightHits = new RaycastHit[16];
        private object _portalLightSightScene;
        /// <summary>Line-of-sight targets on the exit's opening, as fractions of its half width and height (centre first).</summary>
        private static readonly Vector2[] ExitSightPoints = { Vector2.zero, new Vector2(-0.6f, -0.6f), new Vector2(0.6f, -0.6f), new Vector2(-0.6f, 0.6f), new Vector2(0.6f, 0.6f) };

        /// <summary>
        /// Does the real light reach the room the exit opens into straight through the level? Rays from the light to five
        /// points of the exit's opening, each 0.5 m into that room, on the game's Environment colliders; any clear ray
        /// counts. Only opaque geometry blocks (<see cref="OpaqueCollider"/>): light passes glass (8-3's Clockwork Crushers
        /// hall, whose sideways loops are behind glass walls) and invisible clips. A light in the room the exit opens into
        /// (a loop) or in one open hall with the exit (a shifting hall) sees one; a light in another room, or behind the wall
        /// the exit is set in, sees none. Cached per pair for a second.
        /// Before 2026-11-21 an exit within 100 m that the light was on the lit side of (of its infinite plane) was skipped
        /// without this test: 8-3's Fallway lamps 28-59 m away in walled-off rooms lost their images, and their light
        /// stopped at the doorway's border (docs/portals.md, section 20).
        /// </summary>
        private bool LightReachesExit(int id, Vector3 p, in SceneProxies.PortalImage im)
        {
            if (!ReferenceEquals(_portalLightSightScene, _proxies))
            {
                _portalLightSightScene = _proxies;
                _portalLightSight.Clear();
                _portalLightBlocker.Clear();
                _portalOpaqueCollider.Clear();
            }
            float now = Time.realtimeSinceStartup;
            if (_portalLightSight.TryGetValue(id, out var c) && now < c.until) return c.sees;
            var into = im.ExitDir.sqrMagnitude > 1e-8f ? im.ExitDir.normalized : Vector3.zero;
            int mask = LayerMaskDefaults.Get(LMD.Environment);
            bool sees = false;
            string blocker = null;
            foreach (var f in ExitSightPoints)
            {
                var target = im.Exit + im.ExitAx * f.x + im.ExitAy * f.y + into * 0.5f;
                var to = target - p;
                float len = to.magnitude;
                if (len <= 0.8f) { sees = true; break; }
                var dir = to / len;
                // From just off the light (out of its own fixture).
                int n = Physics.RaycastNonAlloc(p + dir * 0.3f, dir, _portalSightHits, len - 0.3f, mask, QueryTriggerInteraction.Ignore);
                Collider hitBy = null;
                float near = float.MaxValue;
                for (int k = 0; k < n; k++)
                {
                    var h = _portalSightHits[k];
                    if (h.collider == null || h.distance >= near || !OpaqueCollider(h.collider)) continue;
                    near = h.distance;
                    hitBy = h.collider;
                }
                if (hitBy == null) { sees = true; break; }
                if (blocker == null) blocker = hitBy.name;
            }
            if (_portalLightSight.Count > 4096) { _portalLightSight.Clear(); _portalLightBlocker.Clear(); }
            _portalLightSight[id] = (now + 1f + (id & 15) * 0.02f, sees); // spread the refreshes
            if (!sees && blocker != null) _portalLightBlocker[id] = blocker;
            return sees;
        }

        /// <summary>A collider that stops light: one drawn by an opaque renderer on its object. Glass, water and other
        /// transparent renderers (queue 2500 and up, or a material named glass) and colliders with no renderer (clips,
        /// triggers' solid twins) do not. Per collider, cached for the scene.</summary>
        private bool OpaqueCollider(Collider col)
        {
            int key = col.GetInstanceID();
            if (_portalOpaqueCollider.TryGetValue(key, out bool opaque)) return opaque;
            opaque = false;
            if (col.TryGetComponent<Renderer>(out var r)) // disabled too: the static scene optimizer draws some through its atlas
            {
                var m = r.sharedMaterial;
                opaque = m == null || (m.renderQueue < 2500 && m.name.IndexOf("glass", System.StringComparison.OrdinalIgnoreCase) < 0);
            }
            if (_portalOpaqueCollider.Count > 8192) _portalOpaqueCollider.Clear();
            _portalOpaqueCollider[key] = opaque;
            return opaque;
        }

        private void LogPortalLightPair(PortalSource src, SceneProxies.PortalImage im, int id, float reach, float score, string skip, bool? sees)
        {
            if (!ReferenceEquals(_portalLightLogScene, _proxies)) { _portalLightLogScene = _proxies; _portalLightLoggedPairs.Clear(); }
            if (_portalLightLoggedPairs.Count >= 80 || !_portalLightLoggedPairs.Add(id)) return;
            string seen = sees.HasValue ? (sees.Value ? ", sees the exit" : _portalLightBlocker.TryGetValue(id, out var by) ? $", exit walled off by '{by}'" : ", exit walled off") : "";
            Logger.LogInfo(System.FormattableString.Invariant(
                $"Portal light: '{src.Name}'{(src.Handed ? " (fixture)" : "")} (reach {reach:F1} m) through {_proxies.PortalSideName(im.Side)}: {(skip != null ? "skipped, " + skip : "image")} at ({im.Pos.x:F1}, {im.Pos.y:F1}, {im.Pos.z:F1}), {im.Distance:F1} m from the opening, exit {im.ExitDistance:F1} m from the light{(im.ExitFacesLight ? " (on its lit side)" : "")}{seen}, score {score:F3}"));
        }

        private int SourceId(int i)
        {
            var l = _packedLights[i];
            return !ReferenceEquals(l, null) && l != null ? l.GetInstanceID() : _lightIds[i];
        }
    }
}
