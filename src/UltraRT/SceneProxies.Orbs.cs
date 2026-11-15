using System.Collections.Generic;
using UnityEngine;

namespace UltraRT
{
    /// <summary>
    /// Statue orbs (docs/scene-proxies.md, "Statue orbs"). The cerberus statues (8-1's lobby, 1-3's water hall, 0-1) hold a
    /// lit ball (Cerb_Apple, the Cerberus material) with a point light at its centre and an ExplosionSphere glow around it.
    /// The game lights the statue per vertex without shadows, so the stone is pale on every side that faces the orb. Ray
    /// traced, the ball first blocked its own light (it was a light fixture whose radius is beyond the fixture clearance:
    /// fixed by giving orb fixtures EmitterMask, SceneProxies.IsGlowing), and then the cupped hands and arms shadowed the
    /// rest of the body: the statue went nearly black.
    ///
    /// The fix keeps the statue in the scene's shadows but lets its own orb light it like the game: the renderers that hold
    /// an orb (everything under the lowest common ancestor of the ball and its light, if that is statue-sized) get
    /// FixtureMask, which ordinary shadow rays still test (0x05), and the orb's light is packed with shadow flag 0.25
    /// (Plugin.GatherLights): its shadow rays skip fixtures (0x01 only, UltraRTLights.hlsl LightShadowSetup).
    /// -ultrart-noorbholders turns it off.
    /// </summary>
    internal sealed partial class SceneProxies
    {
        public static bool OrbHolders = true;
        /// <summary>A holder group larger than this (m, bounds diagonal) is a room, not a statue: left alone.</summary>
        private const float OrbHolderMaxSize = 14f;

        /// <summary>Lights at the centre of an orb fixture whose holder was found: their shadow rays skip fixtures.</summary>
        public readonly HashSet<Light> OrbLights = new HashSet<Light>();
        /// <summary>The statue orbs' own lights (OrbLights minus beam housings): dimmed by Plugin._orbLightScale.</summary>
        public readonly HashSet<Light> OrbFixtureLights = new HashSet<Light>();
        private readonly Dictionary<Renderer, List<Light>> _orbLightsByFixture = new Dictionary<Renderer, List<Light>>();
        private readonly HashSet<Renderer> _orbHolders = new HashSet<Renderer>();
        private int _orbHoldersVersion = -1;
        public int OrbHolderCount => _orbHolders.Count;

        /// <summary>IsLightFixture: light l is at the centre of orb fixture r.</summary>
        private void NoteOrbLight(Renderer r, Light l)
        {
            if (!_orbLightsByFixture.TryGetValue(r, out var list)) _orbLightsByFixture[r] = list = new List<Light>(1);
            if (!list.Contains(l)) list.Add(l);
        }

        /// <summary>The mask a dynamic proxy gets: holders of an orb are fixtures (the orb's own light passes through them).</summary>
        private uint OrbHolderMask(Renderer source, uint mask) =>
            OrbHolders && mask == GeometryMask && source != null && _orbHolders.Contains(source) ? FixtureMask : mask;

        /// <summary>When orb fixtures were added: find their holders, re-mask the holders already in the RTAS. Cheap otherwise.</summary>
        public void UpdateOrbHolders()
        {
            if (!OrbHolders || OrbVersion == _orbHoldersVersion) return;
            _orbHoldersVersion = OrbVersion;
            int added = 0;
            foreach (var kv in _orbLightsByFixture)
            {
                var orb = kv.Key;
                if (orb == null) continue;
                foreach (var l in kv.Value)
                {
                    if (l == null) continue;
                    var root = CommonAncestor(orb.transform, l.transform);
                    if (root == null) continue;
                    var rs = root.GetComponentsInChildren<Renderer>(true);
                    var b = new Bounds(orb.bounds.center, Vector3.zero);
                    foreach (var r in rs) if (r is MeshRenderer || r is SkinnedMeshRenderer) b.Encapsulate(r.bounds);
                    if (b.size.magnitude > OrbHolderMaxSize) continue;
                    OrbLights.Add(l);
                    OrbFixtureLights.Add(l);
                    foreach (var r in rs)
                        if (r != orb && (r is MeshRenderer || r is SkinnedMeshRenderer) && _orbHolders.Add(r)) added++;
                }
            }
            if (added == 0) return;
            int remasked = RemaskHolders();
            _log.LogInfo($"Orb holders: {_orbHolders.Count} renderers hold {OrbLights.Count} orb lights ({remasked} proxies re-masked as fixtures)");
            // Diagnostics: the scale of the statues' skinned renderers (a skinned proxy is placed by its root bone without
            // scale; docs/scene-proxies.md "Skinned meshes").
            int logged = 0;
            foreach (var r in _orbHolders)
            {
                if (logged >= 4 || !(r is SkinnedMeshRenderer smr) || smr == null) continue;
                logged++;
                var root = smr.rootBone != null ? smr.rootBone : smr.transform;
                _log.LogInfo($"Orb holder '{GameControlPath(smr.transform)}': transform scale {smr.transform.lossyScale:F3}, root '{root.name}' scale {root.lossyScale:F3}, bounds {smr.bounds.center:F2} size {smr.bounds.size:F2}");
            }
        }

        /// <summary>
        /// A light that lives inside a housing that is not a small fixture (3-1's eyes: the spot sits in the eyeball, inside
        /// a flesh socket several metres across): the housing's renderers become fixtures and the light skips fixtures,
        /// as for statue orbs, so the eye lights the floor and its beam (Plugin.Shafts.cs) instead of its own socket.
        /// Housing = the renderers under the light's parent and grandparent, minus <paramref name="exclude"/> (the beam
        /// meshes), if they span at most <see cref="HousingMaxSize"/>.
        /// </summary>
        public bool AddHousing(Light l, ICollection<Renderer> exclude)
        {
            if (!OrbHolders || l == null || l.transform.parent == null) return false;
            var root = l.transform.parent.parent != null ? l.transform.parent.parent : l.transform.parent;
            var p = l.transform.position;
            var rs = new List<Renderer>();
            var names = new List<string>();
            foreach (var r in root.GetComponentsInChildren<Renderer>(true))
            {
                if (!(r is MeshRenderer || r is SkinnedMeshRenderer) || exclude.Contains(r) || IsProxyObject(r.gameObject)) continue;
                var m = r.sharedMaterial;
                if (m != null && m.renderQueue >= 2500) continue; // glows and other transparents: not part of the housing
                // Only what wraps the light (the eyeball, the socket around it): its bounds hold the light.
                var b = r.bounds;
                b.Expand(0.5f);
                if (!b.Contains(p) || r.bounds.size.magnitude > HousingMaxSize) { if (names.Count < 6) names.Add($"-{r.name} ({r.bounds.size.magnitude:F1} m)"); continue; }
                rs.Add(r);
                if (names.Count < 6) names.Add($"+{r.name} ({r.bounds.size.magnitude:F1} m)");
            }
            if (rs.Count == 0)
            {
                _log.LogInfo($"Light housing: '{GameControlPath(l.transform)}': none ({string.Join(", ", names)})");
                return false;
            }
            OrbLights.Add(l);
            foreach (var r in rs) _orbHolders.Add(r);
            int remasked = RemaskHolders();
            _log.LogInfo($"Light housing: '{GameControlPath(l.transform)}' in {rs.Count} renderers ({string.Join(", ", names)}), {remasked} proxies re-masked as fixtures");
            return true;
        }

        private const float HousingMaxSize = 64f; // 3-1's eyes: BigEye 39 m, the socket 38 m, flesh around it 57 m (only beam lights get housings)

        private int RemaskHolders()
        {
            int remasked = 0;
            foreach (var d in _dynamic)
            {
                if (d.Source == null || d.Mask != GeometryMask || !_orbHolders.Contains(d.Source)) continue;
                d.Mask = FixtureMask;
                remasked++;
                if (d.Visible && d.Proxy != null)
                {
                    Rtas.RemoveInstance(d.Proxy);
                    Rtas.AddInstance(d.Proxy, d.Flags, enableTriangleCulling: !d.TwoSided, frontTriangleCounterClockwise: InstanceCcw(d), mask: d.Mask, id: d.Id);
                }
            }
            return remasked;
        }

        private static Transform CommonAncestor(Transform a, Transform b)
        {
            for (var p = a; p != null; p = p.parent)
                if (b.IsChildOf(p)) return p;
            return null;
        }
    }
}
