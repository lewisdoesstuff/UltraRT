using System;
using System.Collections.Generic;
using UnityEngine;

namespace UltraRT
{
    internal sealed class EnemyGlowSettings
    {
        /// <summary>-ultrart-noenemyglow / bench eg.enabled=0.</summary>
        public bool enabled = true;
        /// <summary>On the light's strength (the glowing area's output, as a handed-over fixture's).</summary>
        public float gain = 1f;
        /// <summary>Most glow lights per frame (the nearest win) and how far from the camera (m).</summary>
        public int max = 12;
        public float maxDistance = 45f;
        /// <summary>The sphere light's radius is the glowing area's spread, clamped to this range (m).</summary>
        public float minRadius = 0.05f, maxRadius = 0.5f;
        /// <summary>The nominal range the strength is written for (the light model's falloff is physical inside it).</summary>
        public float range = 10f;
        /// <summary>Strongest light, as colour x intensity in the game's units.</summary>
        public float maxGamma = 3f;
        /// <summary>An enemy with one of the game's own lights on it (not its spawn effect) gets no glow light.</summary>
        public bool skipLit = true;
    }

    /// <summary>
    /// Enemy glow lights (docs/emissive-lights.md, "Enemy glows"). A skinned mesh cannot be in the emissive triangle
    /// table (its vertices only exist skinned, on the GPU), so the glowing texels of an enemy's skinned mesh (its glow
    /// map, or an emitter rule's mask over an unlit texture) are summed once per mesh and material in the bind pose,
    /// and each frame one sphere light with that output follows the bone nearest to the glow. Enemies drawn with plain
    /// MeshRenderers (drones) are in the triangle table already when their material emits.
    /// </summary>
    public partial class Plugin
    {
        private readonly EnemyGlowSettings _enemyGlowSettings = new EnemyGlowSettings();

        private sealed class GlowPart
        {
            public SkinnedMeshRenderer Source;
            public Func<bool> Visible;
            public Transform Bone;
            public Vector3 Local;       // the light's centre in the bone's space
            public Vector3 LocalNormal; // the glow's facing in the bone's space
            public float Radius;        // bind-space spread
            public Vector3 Power;       // sum of Le x area, bind space
            public bool Lit;            // the enemy has a light of the game's
            public int Id;
            public string Name;
        }

        private sealed class GlowInfo { public bool Ok; public Vector3 Centre, Normal, Power; public float Radius; public int BoneIndex; }

        private readonly List<GlowPart> _glowParts = new List<GlowPart>();
        private readonly Dictionary<KeyValuePair<Mesh, Material>, GlowInfo> _glowInfo = new Dictionary<KeyValuePair<Mesh, Material>, GlowInfo>();
        private readonly List<SceneProxies.TracedSubmesh> _glowSubs = new List<SceneProxies.TracedSubmesh>();
        private readonly List<SkinnedMeshRenderer> _glowSources = new List<SkinnedMeshRenderer>();
        private readonly List<KeyValuePair<float, GlowPart>> _glowNear = new List<KeyValuePair<float, GlowPart>>();
        private object _glowScene;
        private int _glowCursor, _glowLogged;
        private string _glowKey = "";
        /// <summary>Glow lights appended in the last frame, and parts known.</summary>
        internal int EnemyGlowLights { get; private set; }

        private static bool GlowMaterial(Material m) => m != null && m.HasProperty("_EmissiveIntensity") && m.HasProperty("_EmissiveColor") && m.GetFloat("_EmissiveIntensity") > 0f;

        private void SyncEnemyGlowParts()
        {
            if (_proxies == null || _emissive == null) return;
            // The emitter rules decide which unlit textures glow: a change of them is a rebuild.
            if (!ReferenceEquals(_glowScene, _proxies) || _glowKey != _emitterKey || _proxies.DynamicProxyCount < _glowCursor)
            {
                _glowScene = _proxies;
                _glowKey = _emitterKey;
                _glowParts.Clear();
                _glowInfo.Clear();
                _glowCursor = 0;
            }
            if (_proxies.DynamicProxyCount == _glowCursor) return;
            _glowSubs.Clear();
            _glowSources.Clear();
            _glowCursor = _proxies.CollectSkinnedSubmeshes(GlowMaterial, _glowCursor, _glowSubs, _glowSources);
            for (int i = 0; i < _glowSubs.Count; i++)
            {
                var s = _glowSubs[i];
                var smr = _glowSources[i];
                if (smr == null) continue;
                var enemy = smr.GetComponentInParent<EnemyIdentifier>(true);
                if (enemy == null) continue;
                var key = new KeyValuePair<Mesh, Material>(s.Mesh, s.Material);
                if (!_glowInfo.TryGetValue(key, out var info))
                {
                    info = new GlowInfo();
                    try
                    {
                        if (_emissive.Summarise(s, out var c, out var n, out float r, out float _, out var p, out bool _))
                        {
                            info.Ok = true; info.Centre = c; info.Normal = n; info.Radius = r; info.Power = p;
                            // The bone whose bind position is nearest to the glow.
                            var binds = s.Mesh.bindposes;
                            float best = float.MaxValue;
                            info.BoneIndex = -1;
                            for (int b = 0; b < binds.Length; b++)
                            {
                                float d2 = (binds[b].inverse.MultiplyPoint3x4(Vector3.zero) - c).sqrMagnitude;
                                if (d2 < best) { best = d2; info.BoneIndex = b; }
                            }
                        }
                    }
                    catch (Exception ex) { Logger.LogWarning($"Enemy glow: '{s.Mesh.name}' / '{s.Material.name}' failed: {ex.Message}"); }
                    _glowInfo[key] = info;
                    if (_glowLogged++ < 40)
                        Logger.LogInfo(info.Ok
                            ? $"Enemy glow: {enemy.enemyType} '{s.Mesh.name}' / '{s.Material.name}': output ({info.Power.x:G3}, {info.Power.y:G3}, {info.Power.z:G3}), spread {info.Radius:F2} m, bone {info.BoneIndex}"
                            : $"Enemy glow: {enemy.enemyType} '{s.Mesh.name}' / '{s.Material.name}': nothing emits");
                }
                if (!info.Ok) continue;
                var bones = smr.bones;
                var bindposes = s.Mesh.bindposes;
                Transform bone = info.BoneIndex >= 0 && info.BoneIndex < bones.Length ? bones[info.BoneIndex] : null;
                var part = new GlowPart { Source = smr, Visible = s.Visible, Radius = info.Radius, Power = info.Power, Id = unchecked(smr.GetInstanceID() * 31 + s.SubMesh + 0x45474C), Name = enemy.enemyType + "/" + s.Material.name };
                if (bone != null && info.BoneIndex < bindposes.Length)
                {
                    part.Bone = bone;
                    part.Local = bindposes[info.BoneIndex].MultiplyPoint3x4(info.Centre);
                    part.LocalNormal = bindposes[info.BoneIndex].MultiplyVector(info.Normal);
                }
                else
                {
                    part.Bone = smr.rootBone != null ? smr.rootBone : smr.transform;
                    part.Local = part.Bone.InverseTransformPoint(smr.transform.TransformPoint(info.Centre));
                    part.LocalNormal = part.Bone.InverseTransformDirection(smr.transform.TransformDirection(info.Normal));
                }
                if (_enemyGlowSettings.skipLit)
                    foreach (var l in enemy.GetComponentsInChildren<Light>(true))
                        if (l != null && l.enabled && l.gameObject.activeSelf && GameControlPathBelow(l.transform, enemy.transform).IndexOf("SpawnEffect", StringComparison.OrdinalIgnoreCase) < 0) { part.Lit = true; break; }
                _glowParts.Add(part);
            }
        }

        /// <summary>GatherLights, after the effect lights: the glow lights of the nearest enemies.</summary>
        private void AppendEnemyGlowLights()
        {
            EnemyGlowLights = 0;
            var st = _enemyGlowSettings;
            if (!st.enabled || _camera == null || _proxies == null) return;
            SyncEnemyGlowParts();
            if (_glowParts.Count == 0) return;
            var eye = _camera.transform.position;
            _glowNear.Clear();
            float maxD2 = st.maxDistance * st.maxDistance;
            for (int i = _glowParts.Count - 1; i >= 0; i--)
            {
                var p = _glowParts[i];
                if (p.Source == null || p.Bone == null) { _glowParts.RemoveAt(i); continue; }
                if (p.Lit && st.skipLit) continue;
                bool v;
                try { v = p.Visible(); } catch { v = false; }
                if (!v || !p.Source.enabled || !p.Source.gameObject.activeInHierarchy) continue;
                float d2 = PortalView.SqrDistance(p.Bone.position, eye); // or to the camera's image through a portal
                if (d2 > maxD2) continue;
                _glowNear.Add(new KeyValuePair<float, GlowPart>(d2, p));
            }
            if (_glowNear.Count > st.max) { _glowNear.Sort((a, b) => a.Key.CompareTo(b.Key)); _glowNear.RemoveRange(st.max, _glowNear.Count - st.max); }
            float range = Mathf.Max(1f, st.range);
            foreach (var kv in _glowNear)
            {
                if (_lightCount >= MaxLights) break;
                var p = kv.Value;
                var m = p.Bone.localToWorldMatrix;
                float scale = Mathf.Pow(Mathf.Abs(m.determinant), 1f / 3f);
                float rad = Mathf.Clamp(p.Radius * scale, st.minRadius, st.maxRadius);
                var n = m.MultiplyVector(p.LocalNormal);
                n = n.sqrMagnitude > 1e-10f ? n.normalized : Vector3.zero;
                // Out of the mesh: a glow painted on a head has its centre inside the head.
                var pos = m.MultiplyPoint3x4(p.Local) + n * (rad + 0.05f);
                // The light whose output equals the glowing area's (the handover's formula, backwards).
                float k = st.gain * scale * scale / (LightClasses.HandoverGain * Mathf.PI * HandoverK * range * range);
                var g = new Vector3(Mathf.Pow(Mathf.Max(p.Power.x * k, 0f), 1f / 2.2f), Mathf.Pow(Mathf.Max(p.Power.y * k, 0f), 1f / 2.2f), Mathf.Pow(Mathf.Max(p.Power.z * k, 0f), 1f / 2.2f));
                float mx = Mathf.Max(g.x, Mathf.Max(g.y, g.z));
                if (!(mx > 1e-3f)) continue;
                if (mx > st.maxGamma) g *= st.maxGamma / mx;
                int j = _lightCount * 4;
                _lightData[j] = new Vector4(pos.x, pos.y, pos.z, range);
                _lightData[j + 1] = new Vector4(g.x, g.y, g.z, 0f);
                LightOverrides.PackArea(1, Vector3.down, Vector3.right, rad, rad, 0f, 1f, out var l2, out var l3);
                _lightData[j + 2] = l2;
                _lightData[j + 3] = l3;
                _lightMasks[_lightCount] = ~0u;
                _packedLights[_lightCount] = null;
                _lightIds[_lightCount] = p.Id;
                _lightCount++;
                EnemyGlowLights++;
            }
        }
    }
}
