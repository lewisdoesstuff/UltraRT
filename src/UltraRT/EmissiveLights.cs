using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Logging;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace UltraRT
{
    /// <summary>Tunables, read every <see cref="EmissiveLights.Update"/> / <see cref="EmissiveLights.Bind"/>.</summary>
    public sealed class EmissiveLightSettings
    {
        /// <summary>Sample emissive triangles as direct light. Off: emitters only show up through bounce hits, as before.</summary>
        public bool enabled = true;
        /// <summary>
        /// EmissiveLightScale: multiplies emitted radiance for direct lighting. 1 is physically consistent with the bounce
        /// (Le cos cos / (pi d^2)); ULTRAKILL's point lights are far brighter than that convention. See docs/emissive-lights.md.
        /// </summary>
        public float lightScale = 4f;
        /// <summary>EmissiveHybridAdd: hybrid mode adds this much of the shadowed emissive direct light on top of the game's colour.</summary>
        public float hybridAdd = 0.25f;
        /// <summary>Probability that a RIS candidate is an emissive triangle (when there are both lights and emitters).</summary>
        public float share = 0.5f;
        /// <summary>Reference light mode: emitter samples (shadow rays) per primary hit; the bounce takes at most one.</summary>
        public int referenceSamples = 4;
        /// <summary>Cap on emissive triangles (16-bit indices: at most 65535). The brightest (luminance x area) are kept.</summary>
        public int maxTriangles = 65535;
        /// <summary>Triangles whose estimated radiance luminance (before lightScale) is below this are not lights.</summary>
        public float minRadiance = 0.02f;
        /// <summary>Distance clamp in the 1/d^2 term (m): no fireflies on surfaces touching an emitter.</summary>
        public float minDistance = 0.25f;
        /// <summary>Source pdf from luminance x area x falloff with the camera distance; false = luminance x area only.</summary>
        public bool importance = true;
        /// <summary>Distance (m) at which the camera falloff of the importance weight halves.</summary>
        public float importanceRadius = 8f;
        /// <summary>Fraction of the emissive source pdf that stays proportional to luminance x area only.</summary>
        public float uniformMix = 0.2f;
        /// <summary>Rebuild the (camera-dependent) alias table when the camera moved this far (m) since the last build.</summary>
        public float rebuildDistance = 2f;
        /// <summary>Keep emission on bounce hits too (double counts sampled emitters; for A/B only).</summary>
        public bool bounceHitEmission = false;
        /// <summary>GPU readbacks (meshes, textures) allowed per frame for emitters that appear after the scene build.</summary>
        public int readbacksPerFrame = 2;
    }

    /// <summary>
    /// Emissive surfaces as sampled direct light sources (UltraRTLights.hlsl, docs/emissive-lights.md).
    ///  - At scene build, every traced submesh with an emissive proxy material is split into triangles (static batches
    ///    from StaticGroups' CPU copy of the combined mesh, dynamic MeshRenderers from a cached readback of their mesh).
    ///    Each triangle stores its vertices in its owner's space plus an average emitted radiance estimated from a 32x32
    ///    downsample of the emissive (or albedo) texture.
    ///  - Per frame: owner transforms (so moving emitters stay exact), which emitters are in the RTAS (a bit per
    ///    triangle), and an alias table over the active triangles weighted by luminance x area x camera falloff.
    ///  - The triangle list is append-only between compactions, so indices stay stable for ReSTIR history; a compaction
    ///    bumps the generation and the shader drops emissive history for one frame.
    /// Skinned emitters are not handled (TODO): they still light through bounce hits only if bounceHitEmission is on.
    /// </summary>
    internal sealed partial class EmissiveLights : IDisposable
    {
        public EmissiveLightSettings Settings = new EmissiveLightSettings();

        /// <summary>Triangles in the list / in the RTAS this frame (alias table entries).</summary>
        public int TriangleCount => _triCount;
        public int EmitterCount => _emitters.Count;
        public int OwnerCount => _owners.Count;
        public int ActiveCount { get; private set; }
        /// <summary>True on frames where the set of active emitters changed (pass to LightSampling to shorten history).</summary>
        public bool SetChanged { get; private set; }

        /// <summary>Scale on the light one surface gives, by its proxy material and emission texture (Plugin.LightClasses.cs,
        /// docs/lighting-overrides.md "Emitters"). Read when the triangle table is built: call Clear() when it changes.</summary>
        public Func<Material, Texture, float> LightScaleOf;

        public struct EmitterGroup
        {
            public string Material, Texture;
            public int Triangles, Visible;
            public float Area, Lum;
        }

        /// <summary>The live emissive triangles by material: count, how many are in the RTAS now, world area (m2), mean
        /// luminance of the emitted radiance (as stored: with the surface's own scale, before lightScale).</summary>
        public List<EmitterGroup> Describe()
        {
            var d = new Dictionary<string, EmitterGroup>();
            for (int t = 0; t < _triCount; t++)
            {
                var em = _emitters[_triEmitter[t]];
                if (em.Dead || em.Src?.Material == null) continue;
                var m = em.Src.Material;
                var tex = EmissionTexture(m, out _, out _, out _);
                string key = m.name + "|" + (tex != null ? tex.name : "");
                d.TryGetValue(key, out var g);
                g.Material = m.name; g.Texture = tex != null ? tex.name : "";
                float a = _triArea[t] * _owners[em.Owner].AreaScale;
                g.Triangles++;
                if (em.Visible) g.Visible++;
                g.Lum += _triLum[t] * a;
                g.Area += a;
                d[key] = g;
            }
            return d.Values.Select(g => { if (g.Area > 0f) g.Lum /= g.Area; return g; }).OrderByDescending(g => g.Lum * g.Area).ToList();
        }

        private const int TexRes = 32;
        private const int MaxOwners = 0xFFFF;
        private const int TriStride = 12; // uints per triangle (3 x uint4)

        private readonly ManualLogSource _log;

        private sealed class Emitter
        {
            public SceneProxies.TracedSubmesh Src;
            public int Owner;
            public bool Visible, Dead;
            public int Tris;
        }

        private sealed class OwnerEntry
        {
            public Transform Space;
            public bool World;
            public Matrix4x4 Matrix;
            public float AreaScale = 1f;
            /// <summary>Space is one of our proxy transforms, so its hasChanged flag is ours to consume.</summary>
            public bool OwnTransform;
        }

        private readonly List<Emitter> _emitters = new List<Emitter>();
        private readonly List<OwnerEntry> _owners = new List<OwnerEntry>();
        private readonly Dictionary<object, int> _ownerByKey = new Dictionary<object, int>();
        private readonly List<SceneProxies.TracedSubmesh> _pending = new List<SceneProxies.TracedSubmesh>();
        private int _dynamicCursor;
        private bool _built;

        // Per triangle (CPU): GPU words, emitter, local centroid, local area, luminance of Le.
        private uint[] _triData = new uint[0];
        private int[] _triEmitter = new int[0];
        private Vector3[] _triCentroid = new Vector3[0];
        private float[] _triArea = new float[0], _triLum = new float[0];
        private int _triCount, _deadTris, _uploadedTris;
        private bool _capWarned;

        private readonly Dictionary<Mesh, MeshSource> _meshCache = new Dictionary<Mesh, MeshSource>();
        private readonly Dictionary<Texture, Color[]> _texCache = new Dictionary<Texture, Color[]>();
        private readonly Dictionary<Material, bool> _emissiveMaterial = new Dictionary<Material, bool>();
        private Texture2D _readTex;

        // GPU
        private ComputeBuffer _tris, _ownersBuf, _alias, _active, _dummy4, _dummy1;
        private float[] _ownerData = new float[0];
        private uint[] _activeBits = new uint[0], _aliasData = new uint[0];
        private float[] _prob = new float[0], _threshold = new float[0];
        private int[] _aliasTri = new int[0], _small = new int[0], _large = new int[0], _aliasIdx = new int[0];

        private bool _activeDirty, _ownersDirty;
        private int _generation, _boundGeneration = -1;
        private Vector3 _aliasCam = new Vector3(float.MaxValue, 0, 0);
        private int _aliasFrame;

        private static readonly int
            IdTris = Shader.PropertyToID("_EmissiveTris"),
            IdOwners = Shader.PropertyToID("_EmissiveOwners"),
            IdAlias = Shader.PropertyToID("_EmissiveAlias"),
            IdActive = Shader.PropertyToID("_EmissiveActive"),
            IdCount = Shader.PropertyToID("_EmissiveCount"),
            IdTriCount = Shader.PropertyToID("_EmissiveTriCount"),
            IdRefSamples = Shader.PropertyToID("_EmissiveRefSamples"),
            IdHistoryValid = Shader.PropertyToID("_EmissiveHistoryValid"),
            IdParams = Shader.PropertyToID("_EmissiveParams"),
            IdBounceWeight = Shader.PropertyToID("_EmissiveBounceWeight"),
            IdHybridAdd = Shader.PropertyToID("_EmissiveHybridAdd");

        public EmissiveLights(ManualLogSource log)
        {
            _log = log;
            _dummy4 = new ComputeBuffer(4, 16) { name = "UltraRT Emissive (empty)" };
            _dummy4.SetData(new uint[16]);
            _dummy1 = new ComputeBuffer(1, 4) { name = "UltraRT Emissive mask (empty)" };
            _dummy1.SetData(new uint[1]);
        }

        // ---------------------------------------------------------------- build / update

        /// <summary>Scene build: collect every emissive traced submesh and build the triangle list (all readbacks now).</summary>
        public void Build(SceneProxies proxies)
        {
            Clear();
            if (proxies == null || !Settings.enabled) return;
            try { BuildCore(proxies); }
            catch (Exception e)
            {
                _log.LogWarning($"Emissive lights: build failed, emitters only light through bounces: {e}");
                Clear();
            }
            _built = true; // also after a failure: don't retry every frame
        }

        private void BuildCore(SceneProxies proxies)
        {
            var sw = Stopwatch.StartNew();
            LavaTriangles = 0;
            var subs = new List<SceneProxies.TracedSubmesh>();
            proxies.CollectStaticSubmeshes(IsEmissive, subs);
            int staticSubs = subs.Count;
            _dynamicCursor = proxies.CollectDynamicSubmeshes(IsEmissive, 0, subs);

            // Extract everything, then keep the brightest triangles if over the cap.
            var cands = new List<Candidate>();
            int skippedDark = 0, failed = 0, skyWalls = 0;
            foreach (var s in subs)
            {
                if (s.SkyWall) { skyWalls++; continue; }
                if (!Extract(s, cands, ref skippedDark)) failed++;
            }
            int cap = Mathf.Clamp(Settings.maxTriangles, 1, 0xFFFF);
            int dropped = 0;
            if (cands.Count > cap)
            {
                dropped = cands.Count - cap;
                cands.Sort((a, b) => (b.Lum * b.Area).CompareTo(a.Lum * a.Area));
                cands.RemoveRange(cap, cands.Count - cap);
            }
            foreach (var c in cands) AddTriangle(c);

            _activeDirty = _ownersDirty = true;
            var lums = _triLum.Take(_triCount).OrderBy(x => x).ToArray();
            double area = 0;
            for (int i = 0; i < _triCount; i++) area += _triArea[i] * _owners[_emitters[_triEmitter[i]].Owner].AreaScale;
            _log.LogInfo($"Emissive lights: {_triCount:N0} triangles from {_emitters.Count} submeshes ({staticSubs} static, {subs.Count - staticSubs} dynamic, " +
                         $"{_owners.Count} owners), {skippedDark:N0} dark triangles skipped, {dropped:N0} over the cap ({cap:N0}), {failed} readbacks failed, " +
                         $"{skyWalls} sky walls skipped, {LavaTriangles:N0} lava pieces; Le luminance median {(lums.Length > 0 ? lums[lums.Length / 2] : 0):F2} max {(lums.Length > 0 ? lums[lums.Length - 1] : 0):F2}, " +
                         $"total area {area:F1} m2; {_meshCache.Count} meshes, {_texCache.Count} textures read; took {sw.Elapsed.TotalMilliseconds:F0} ms, " +
                         $"GPU {(_triCount * 48 + _owners.Count * 48) / 1024.0:F0} KB");
        }

        /// <summary>Per traced frame, after SceneProxies.SyncDynamic: new emitters, visibility, transforms, alias table.</summary>
        public void Update(SceneProxies proxies, Vector3 camPos)
        {
            SetChanged = false;
            if (proxies == null || !Settings.enabled) return;
            long tStart = System.Diagnostics.Stopwatch.GetTimestamp();
            if (!_built) { Build(proxies); SetChanged = true; } // enabled after the scene build

            // New dynamic proxies (rescans, spawned hierarchies). Readbacks stall the GPU, so only a few per frame.
            if (proxies.DynamicProxyCount < _dynamicCursor) _dynamicCursor = 0; // proxies were rebuilt
            if (proxies.DynamicProxyCount > _dynamicCursor) _dynamicCursor = proxies.CollectDynamicSubmeshes(IsEmissive, _dynamicCursor, _pending);
            // Submeshes whose material changed at runtime (swaps, emission from property blocks; SceneProxies.Sync.cs).
            // Their old entries stop counting through the material generation in Visible.
            _changed.Clear();
            proxies.TakeMaterialChanges(_changed);
            foreach (var s in _changed)
            {
                _emissiveMaterial.Remove(s.Material); // its values may have changed
                if (IsEmissive(s.Material)) _pending.Add(s);
            }
            if (_pending.Count > 0) ProcessPending();

            long tVis = System.Diagnostics.Stopwatch.GetTimestamp();
            PendingTicks += tVis - tStart;
            // Visibility.
            for (int i = 0; i < _emitters.Count; i++)
            {
                var e = _emitters[i];
                if (e.Dead) continue;
                bool v;
                try { v = e.Src.Visible(); } catch { v = false; }
                if (!v && e.Src.Space == null && !e.Src.Static)
                {
                    e.Dead = true; // the proxy (and its source) are destroyed
                    _deadTris += e.Tris;
                }
                if (v == e.Visible) continue;
                e.Visible = v;
                _activeDirty = true;
            }
            if (_deadTris > 4096 && _deadTris * 2 > _triCount) Compact();

            long tXf = System.Diagnostics.Stopwatch.GetTimestamp();
            VisibilityTicks += tXf - tVis;
            // Transforms.
            // Reading localToWorldMatrix of every owner each frame cost ~0.1 ms in 8-3. Proxy transforms report changes
            // through hasChanged (set when the source or any ancestor moves); other spaces (static batch roots, which
            // don't move in practice) are re-read in turn, 1/16 per frame.
            _ownerPhase = (_ownerPhase + 1) & 15;
            for (int o = 0; o < _owners.Count; o++)
            {
                var ow = _owners[o];
                if (ow.World || ow.Space == null) continue;
                if (ow.OwnTransform)
                {
                    if (!ow.Space.hasChanged) continue;
                    ow.Space.hasChanged = false;
                }
                else if ((o & 15) != _ownerPhase) continue;
                var m = ow.Space.localToWorldMatrix;
                if (m == ow.Matrix) continue;
                ow.Matrix = m;
                ow.AreaScale = AreaScale(m);
                WriteOwner(o);
                _ownersDirty = true;
            }

            TransformTicks += System.Diagnostics.Stopwatch.GetTimestamp() - tXf;
            UploadTriangles();
            if (_ownersDirty)
            {
                EnsureBuffer(ref _ownersBuf, _owners.Count * 3, 16, "UltraRT EmissiveOwners");
                if (_owners.Count > 0) _ownersBuf.SetData(_ownerData, 0, 0, _owners.Count * 12);
                _ownersDirty = false;
            }

            // The alias table is rebuilt over every active triangle (20k+ in 8-3, ~1 ms). While moving, at most every
            // AliasInterval seconds; after visibility changes, at most every 4 frames (new emitters wait that long).
            float now = Time.realtimeSinceStartup;
            bool moved = Settings.importance && (camPos - _aliasCam).sqrMagnitude > Settings.rebuildDistance * Settings.rebuildDistance
                         && now - _aliasTime >= AliasInterval;
            if (_activeDirty)
            {
                SetChanged = true;
                ActiveUpdates++;
                UpdateActive();
                _aliasPending = true;
            }
            if ((_aliasPending && Time.frameCount - _aliasFrame >= 4) || moved)
            {
                long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                BuildAlias(camPos);
                AliasBuilds++;
                AliasTicks += System.Diagnostics.Stopwatch.GetTimestamp() - t0;
                _aliasCam = camPos;
                _aliasFrame = Time.frameCount;
                _aliasTime = now;
                _aliasPending = false;
            }
            _activeDirty = false;
        }

        private const float AliasInterval = 0.1f;
        private int _ownerPhase;
        private float _aliasTime;
        private bool _aliasPending;
        /// <summary>Alias table rebuilds and their CPU ticks, read and reset by the plugin's periodic log.</summary>
        public int AliasBuilds, ActiveUpdates;
        public long PendingTicks, VisibilityTicks, TransformTicks;
        public long AliasTicks;

        /// <summary>Bind everything UltraRTLights.hlsl declares for emitters. Call on every PathTrace dispatch, after LightSampling.Bind.</summary>
        public void Bind(CommandBuffer cmd, RayTracingShader shader, LightSamplingMode mode)
        {
            var s = Settings;
            int count = s.enabled ? ActiveCount : 0;
            bool sampled = count > 0 && (mode != LightSamplingMode.Reference || s.referenceSamples > 0);
            cmd.SetRayTracingBufferParam(shader, IdTris, _tris != null && _triCount > 0 ? _tris : _dummy4);
            cmd.SetRayTracingBufferParam(shader, IdOwners, _ownersBuf != null && _owners.Count > 0 ? _ownersBuf : _dummy4);
            cmd.SetRayTracingBufferParam(shader, IdAlias, _alias != null && count > 0 ? _alias : _dummy4);
            cmd.SetRayTracingBufferParam(shader, IdActive, _active != null && _triCount > 0 ? _active : _dummy1);
            cmd.SetRayTracingIntParam(shader, IdCount, count);
            cmd.SetRayTracingIntParam(shader, IdTriCount, s.enabled ? _triCount : 0);
            cmd.SetRayTracingIntParam(shader, IdRefSamples, Mathf.Clamp(s.referenceSamples, 0, 16));
            cmd.SetRayTracingIntParam(shader, IdHistoryValid, _generation == _boundGeneration ? 1 : 0);
            _boundGeneration = _generation;
            float dmin = Mathf.Max(1e-3f, s.minDistance);
            cmd.SetRayTracingVectorParam(shader, IdParams, new Vector4(Mathf.Clamp(s.share, 0.01f, 0.99f), Mathf.Max(0f, s.lightScale) / Mathf.PI, dmin * dmin, 0));
            cmd.SetRayTracingFloatParam(shader, IdBounceWeight, sampled && !s.bounceHitEmission ? 0f : 1f);
            cmd.SetRayTracingFloatParam(shader, IdHybridAdd, sampled ? Mathf.Max(0f, s.hybridAdd) : 0f);
        }

        /// <summary>Scene change / shader reload: forget everything (caches included: meshes and textures belong to the scene).</summary>
        /// <summary>One emissive triangle in world space (the light classifier, LightClassifier.cs).</summary>
        public struct WorldTriangle
        {
            public Vector3 P0, E1, E2, Normal;
            public float Area, Lum;
            public Color Le;
            public bool Visible;
            public Vector3 Centroid => P0 + (E1 + E2) / 3f;
        }

        /// <summary>Every live emissive triangle in world space, with its average emitted radiance (before lightScale).</summary>
        public List<WorldTriangle> WorldTriangles()
        {
            var list = new List<WorldTriangle>(_triCount);
            for (int t = 0; t < _triCount; t++)
            {
                var em = _emitters[_triEmitter[t]];
                if (em.Dead) continue;
                var ow = _owners[em.Owner];
                var m = ow.World ? Matrix4x4.identity : ow.Matrix;
                int b = t * TriStride;
                var p0 = new Vector3(F(_triData[b]), F(_triData[b + 1]), F(_triData[b + 2]));
                var e1 = new Vector3(F(_triData[b + 4]), F(_triData[b + 5]), F(_triData[b + 6]));
                var e2 = new Vector3(F(_triData[b + 8]), F(_triData[b + 9]), F(_triData[b + 10]));
                var w0 = m.MultiplyPoint3x4(p0);
                var w1 = m.MultiplyVector(e1);
                var w2 = m.MultiplyVector(e2);
                var n = Vector3.Cross(w1, w2);
                float a = 0.5f * n.magnitude;
                if (a <= 0f) continue;
                n.Normalize();
                if ((m.determinant < 0f) != SceneProxies.FrontCounterClockwise) n = -n;
                var le = new Color(Mathf.HalfToFloat((ushort)(_triData[b + 7] & 0xFFFF)), Mathf.HalfToFloat((ushort)(_triData[b + 7] >> 16)), Mathf.HalfToFloat((ushort)(_triData[b + 11] & 0xFFFF)));
                list.Add(new WorldTriangle { P0 = w0, E1 = w1, E2 = w2, Normal = n, Area = a, Lum = _triLum[t], Le = le, Visible = em.Src.Visible == null || em.Src.Visible() });
            }
            return list;
        }

        private static float F(uint u) => BitConverter.Int32BitsToSingle(unchecked((int)u));

        public void Clear()
        {
            _emitters.Clear();
            _owners.Clear();
            _ownerByKey.Clear();
            _pending.Clear();
            _meshCache.Clear();
            _texCache.Clear();
            _emissiveMaterial.Clear();
            _emitterBySrc.Clear();
            _dynamicCursor = 0;
            _built = false;
            _triCount = _deadTris = _uploadedTris = 0;
            ActiveCount = 0;
            _capWarned = false;
            _generation++;
            _aliasCam = new Vector3(float.MaxValue, 0, 0);
        }

        // ---------------------------------------------------------------- emitters and triangles

        private struct Candidate
        {
            public SceneProxies.TracedSubmesh Src;
            public Vector3 P0, E1, E2;
            public Color Le;
            public float Area, Lum;
        }

        private readonly List<SceneProxies.TracedSubmesh> _changed = new List<SceneProxies.TracedSubmesh>();

        private bool IsEmissive(Material m)
        {
            if (m == null) return false;
            if (_emissiveMaterial.TryGetValue(m, out bool e)) return e;
            e = m.HasProperty("_EmissiveIntensity") && m.HasProperty("_EmissiveColor")
                && m.GetFloat("_EmissiveIntensity") > 0f
                && Lum(m.GetColor("_EmissiveColor")) > 0f
                // StaticSceneOptimizer atlas geometry: an emissive average over a whole level atlas would be meaningless.
                && !(m.HasProperty("_AtlasMode") && m.GetFloat("_AtlasMode") > 0.5f)
                && !(m.HasProperty("_SkyWall") && m.GetFloat("_SkyWall") > 0.5f);
            _emissiveMaterial[m] = e;
            return e;
        }

        private void ProcessPending()
        {
            int budget = Mathf.Max(1, Settings.readbacksPerFrame);
            int i = 0, dark = 0;
            for (; i < _pending.Count; i++)
            {
                var s = _pending[i];
                int cost = (_meshCache.ContainsKey(s.Mesh) || s.Streams != null ? 0 : 1) + (TextureCached(s.Material) ? 0 : 1);
                if (cost > 0 && budget <= 0) break;
                budget -= cost;
                if (s.SkyWall) continue;
                var cands = new List<Candidate>();
                Extract(s, cands, ref dark);
                foreach (var c in cands)
                {
                    if (_triCount >= Mathf.Clamp(Settings.maxTriangles, 1, 0xFFFF))
                    {
                        if (_deadTris > 0) { Compact(); if (_triCount < Mathf.Clamp(Settings.maxTriangles, 1, 0xFFFF)) { AddTriangle(c); continue; } }
                        if (!_capWarned) _log.LogWarning($"Emissive lights: triangle cap ({Settings.maxTriangles}) reached; later emitters are not sampled");
                        _capWarned = true;
                        break;
                    }
                    AddTriangle(c);
                }
                _activeDirty = true;
            }
            _pending.RemoveRange(0, i);
        }

        private bool TextureCached(Material m)
        {
            var tex = EmissionTexture(m, out _, out _, out _);
            return tex == null || _texCache.ContainsKey(tex);
        }

        private int OwnerFor(SceneProxies.TracedSubmesh s)
        {
            var key = s.Owner ?? s;
            if (_ownerByKey.TryGetValue(key, out int o)) return o;
            if (_owners.Count >= MaxOwners) return -1;
            var ow = new OwnerEntry { Space = s.Space, World = s.Space == null, OwnTransform = s.Space != null && SceneProxies.IsProxyObject(s.Space.gameObject) };
            ow.Matrix = ow.World ? Matrix4x4.identity : s.Space.localToWorldMatrix;
            ow.AreaScale = AreaScale(ow.Matrix);
            o = _owners.Count;
            _owners.Add(ow);
            _ownerByKey[key] = o;
            WriteOwner(o);
            _ownersDirty = true;
            return o;
        }

        private void WriteOwner(int o)
        {
            if (_ownerData.Length < (o + 1) * 12) Array.Resize(ref _ownerData, Math.Max(64 * 12, Math.Max((o + 1) * 12, _ownerData.Length * 2)));
            var m = _owners[o].Matrix;
            int b = o * 12;
            for (int r = 0; r < 3; r++)
            {
                _ownerData[b + r * 4] = m[r, 0];
                _ownerData[b + r * 4 + 1] = m[r, 1];
                _ownerData[b + r * 4 + 2] = m[r, 2];
                _ownerData[b + r * 4 + 3] = m[r, 3];
            }
        }

        private static float AreaScale(Matrix4x4 m)
        {
            float det = Mathf.Abs(m.m00 * (m.m11 * m.m22 - m.m12 * m.m21) - m.m01 * (m.m10 * m.m22 - m.m12 * m.m20) + m.m02 * (m.m10 * m.m21 - m.m11 * m.m20));
            return Mathf.Pow(det, 2f / 3f); // exact for uniform scale; a heuristic otherwise (only used for importance)
        }

        private readonly Dictionary<SceneProxies.TracedSubmesh, int> _emitterBySrc = new Dictionary<SceneProxies.TracedSubmesh, int>();

        private void AddTriangle(Candidate c)
        {
            if (!_emitterBySrc.TryGetValue(c.Src, out int ei) || ei >= _emitters.Count || _emitters[ei].Src != c.Src)
            {
                int owner = OwnerFor(c.Src);
                if (owner < 0) return;
                ei = _emitters.Count;
                _emitters.Add(new Emitter { Src = c.Src, Owner = owner });
                _emitterBySrc[c.Src] = ei;
            }
            var em = _emitters[ei];
            int t = _triCount;
            if (_triEmitter.Length <= t)
            {
                int n = Math.Max(1024, _triEmitter.Length * 2);
                Array.Resize(ref _triEmitter, n);
                Array.Resize(ref _triCentroid, n);
                Array.Resize(ref _triArea, n);
                Array.Resize(ref _triLum, n);
                Array.Resize(ref _triData, n * TriStride);
            }
            uint flags = (c.Src.TwoSided ? 1u : 0u) | (SceneProxies.FrontCounterClockwise ? 2u : 0u);
            int b = t * TriStride;
            _triData[b + 0] = AsUInt(c.P0.x); _triData[b + 1] = AsUInt(c.P0.y); _triData[b + 2] = AsUInt(c.P0.z);
            _triData[b + 3] = (uint)em.Owner | (flags << 16);
            _triData[b + 4] = AsUInt(c.E1.x); _triData[b + 5] = AsUInt(c.E1.y); _triData[b + 6] = AsUInt(c.E1.z);
            _triData[b + 7] = Half(c.Le.r) | (Half(c.Le.g) << 16);
            _triData[b + 8] = AsUInt(c.E2.x); _triData[b + 9] = AsUInt(c.E2.y); _triData[b + 10] = AsUInt(c.E2.z);
            _triData[b + 11] = Half(c.Le.b);
            _triEmitter[t] = ei;
            _triCentroid[t] = c.P0 + (c.E1 + c.E2) / 3f;
            _triArea[t] = c.Area;
            _triLum[t] = c.Lum;
            em.Tris++;
            _triCount++;
        }

        /// <summary>Drop dead emitters' triangles. Indices change, so the shader drops emissive history for one frame.</summary>
        private void Compact()
        {
            int n = 0;
            for (int t = 0; t < _triCount; t++)
            {
                if (_emitters[_triEmitter[t]].Dead) continue;
                if (n != t)
                {
                    Array.Copy(_triData, t * TriStride, _triData, n * TriStride, TriStride);
                    _triEmitter[n] = _triEmitter[t];
                    _triCentroid[n] = _triCentroid[t];
                    _triArea[n] = _triArea[t];
                    _triLum[n] = _triLum[t];
                }
                n++;
            }
            foreach (var e in _emitters) if (e.Dead) { e.Tris = 0; _emitterBySrc.Remove(e.Src); }
            _log.LogInfo($"Emissive lights: compacted {_triCount:N0} -> {n:N0} triangles");
            _triCount = n;
            _deadTris = 0;
            _uploadedTris = 0; // re-upload everything
            _generation++;
            _activeDirty = true;
        }

        private void UploadTriangles()
        {
            if (_triCount == _uploadedTris) return;
            int cap = _tris?.count ?? 0;
            if (cap < _triCount * 3)
            {
                EnsureBuffer(ref _tris, Math.Max(_triCount, 1024) * 3, 16, "UltraRT EmissiveTris");
                _uploadedTris = 0;
            }
            int from = Math.Min(_uploadedTris, _triCount);
            // Offsets and count are in managed elements (uints): 12 per triangle.
            if (_triCount > from) _tris.SetData(_triData, from * TriStride, from * TriStride, (_triCount - from) * TriStride);
            _uploadedTris = _triCount;
        }

        private void EnsureBuffer(ref ComputeBuffer b, int count, int stride, string name)
        {
            count = Math.Max(1, count);
            if (b != null && b.count >= count) return;
            b?.Release();
            int n = 1;
            while (n < count) n <<= 1;
            b = new ComputeBuffer(n, stride) { name = name };
        }

        private void UpdateActive()
        {
            int words = (_triCount + 31) / 32;
            if (_activeBits.Length < Math.Max(1, words)) _activeBits = new uint[Math.Max(64, words * 2)];
            Array.Clear(_activeBits, 0, Math.Max(1, words));
            int active = 0;
            for (int t = 0; t < _triCount; t++)
            {
                var e = _emitters[_triEmitter[t]];
                if (e.Dead || !e.Visible) continue;
                _activeBits[t >> 5] |= 1u << (t & 31);
                active++;
            }
            EnsureBuffer(ref _active, Math.Max(1, words), 4, "UltraRT EmissiveActive");
            _active.SetData(_activeBits, 0, 0, Math.Max(1, words));
            ActiveCount = active;
        }

        /// <summary>Vose alias table over the active triangles: pdf = (1 - mix) w / sum + mix * lum*area / sum(lum*area).</summary>
        private void BuildAlias(Vector3 camPos)
        {
            int n = 0;
            if (_aliasTri.Length < _triCount)
            {
                int size = Math.Max(1024, _triCount);
                _aliasTri = new int[size]; _prob = new float[size]; _threshold = new float[size];
                _small = new int[size]; _large = new int[size]; _aliasIdx = new int[size];
                _aliasData = new uint[size * 4];
            }
            var basis = _threshold; // scratch: lum * area
            double sumW = 0, sumB = 0;
            float r2 = Mathf.Max(0.01f, Settings.importanceRadius * Settings.importanceRadius);
            bool importance = Settings.importance;
            int lastEmitter = -1;
            OwnerEntry ow = null;
            Matrix4x4 m = Matrix4x4.identity;
            for (int t = 0; t < _triCount; t++)
            {
                if ((_activeBits[t >> 5] & (1u << (t & 31))) == 0) continue;
                int e = _triEmitter[t];
                if (e != lastEmitter)
                {
                    // Triangles of one emitter are mostly contiguous: look its owner up once per run.
                    lastEmitter = e;
                    ow = _owners[_emitters[e].Owner];
                    m = ow.World ? Matrix4x4.identity : ow.Matrix;
                }
                float la = _triLum[t] * _triArea[t] * ow.AreaScale;
                float w = la;
                if (importance)
                {
                    var p = _triCentroid[t];
                    float dx = m.m00 * p.x + m.m01 * p.y + m.m02 * p.z + m.m03 - camPos.x;
                    float dy = m.m10 * p.x + m.m11 * p.y + m.m12 * p.z + m.m13 - camPos.y;
                    float dz = m.m20 * p.x + m.m21 * p.y + m.m22 * p.z + m.m23 - camPos.z;
                    w *= r2 / (r2 + dx * dx + dy * dy + dz * dz);
                }
                _aliasTri[n] = t;
                _prob[n] = w;
                basis[n] = la;
                sumW += w;
                sumB += la;
                n++;
            }
            ActiveCount = n;
            if (n == 0) return;

            float mix = Settings.importance && sumW > 0 ? Mathf.Clamp01(Settings.uniformMix) : 1f;
            for (int k = 0; k < n; k++)
            {
                double pw = sumW > 0 ? _prob[k] / sumW : 1.0 / n;
                double pb = sumB > 0 ? basis[k] / sumB : 1.0 / n;
                _prob[k] = (float)((1.0 - mix) * pw + mix * pb);
            }

            int ns = 0, nl = 0;
            for (int k = 0; k < n; k++)
            {
                _threshold[k] = _prob[k] * n;
                _aliasIdx[k] = k;
                if (_threshold[k] < 1f) _small[ns++] = k; else _large[nl++] = k;
            }
            while (ns > 0 && nl > 0)
            {
                int sm = _small[--ns], lg = _large[--nl];
                _aliasIdx[sm] = lg;
                _threshold[lg] = _threshold[lg] + _threshold[sm] - 1f;
                if (_threshold[lg] < 1f) _small[ns++] = lg; else _large[nl++] = lg;
            }
            while (nl > 0) _threshold[_large[--nl]] = 1f;
            while (ns > 0) _threshold[_small[--ns]] = 1f;

            for (int k = 0; k < n; k++)
            {
                int a = _aliasIdx[k];
                _aliasData[k * 4] = AsUInt(_threshold[k]);
                _aliasData[k * 4 + 1] = (uint)_aliasTri[k] | ((uint)_aliasTri[a] << 16);
                _aliasData[k * 4 + 2] = AsUInt(_prob[k]);
                _aliasData[k * 4 + 3] = AsUInt(_prob[a]);
            }
            EnsureBuffer(ref _alias, n, 16, "UltraRT EmissiveAlias");
            _alias.SetData(_aliasData, 0, 0, n * 4);
        }

        // ---------------------------------------------------------------- triangle extraction and radiance estimate

        /// <summary>World-independent triangles of one submesh (in its owner's space) with their estimated radiance.</summary>
        private bool Extract(SceneProxies.TracedSubmesh s, List<Candidate> output, ref int dark)
        {
            var mesh = s.Mesh;
            if (mesh == null || s.SubMesh < 0 || s.SubMesh >= mesh.subMeshCount) return false;
            if (mesh.GetTopology(s.SubMesh) != MeshTopology.Triangles) return true;
            var src = Source(s);
            if (src == null) return false;
            if (LavaClassifier.IsLavaEmitter(s.Material)) return ExtractLava(s, src, output, ref dark); // EmissiveLights.Lava.cs

            var m = s.Material;
            var tex = EmissionTexture(m, out var st, out var tint, out bool _);
            var img = tex != null ? LowRes(tex) : null;
            var baseLe = m.GetColor("_EmissiveColor") * m.GetFloat("_EmissiveIntensity");
            baseLe = new Color(baseLe.r * tint.r, baseLe.g * tint.g, baseLe.b * tint.b);
            // _EmissiveThreshold (an emitter mask, SceneProxies.Glow.cs): as Proxy.shader, only texels above the
            // threshold glow. On the 32x32 copy, so a mask of single texels is averaged before the step: approximate.
            float thr = m.HasProperty("_EmissiveThreshold") ? m.GetFloat("_EmissiveThreshold") : 0f;
            if (thr > 0f && img != null)
            {
                var cut = new Color[img.Length];
                for (int i = 0; i < img.Length; i++)
                {
                    var c = img[i];
                    cut[i] = c * Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(thr - 0.1f, thr + 0.1f, Lum(new Color(c.r * tint.r, c.g * tint.g, c.b * tint.b))));
                }
                img = cut;
            }
            // A surface's own scale on the light it gives (light overrides' "emitters"): only this table, so the surface
            // looks the same to the camera and in reflections.
            float own = LightScaleOf != null ? Mathf.Max(0f, LightScaleOf(m, tex)) : 1f;
            if (own <= 0f) return true;
            baseLe *= own;
            Color avg = img != null ? Average(img) : Color.white;
            // _EmissiveMask (Proxy.shader): emission x main texture alpha^2.2; the table takes its mean over the texture.
            if (m.HasProperty("_EmissiveMask") && m.GetFloat("_EmissiveMask") > 0.5f && m.HasProperty("_MainTex") && m.GetTexture("_MainTex") is Texture main && main.dimension == TextureDimension.Tex2D)
            {
                var mi = LowRes(main);
                if (mi != null && mi.Length > 0)
                {
                    float a = 0f;
                    foreach (var c in mi) a += Mathf.Pow(Mathf.Clamp01(c.a), 2.2f);
                    baseLe *= a / mi.Length;
                }
            }

            var d = mesh.GetSubMesh(s.SubMesh);
            int count = d.indexCount - d.indexCount % 3;
            for (int k = 0; k < count; k += 3)
            {
                int i0 = src.Index(d.indexStart + k) + d.baseVertex;
                int i1 = src.Index(d.indexStart + k + 1) + d.baseVertex;
                int i2 = src.Index(d.indexStart + k + 2) + d.baseVertex;
                if ((uint)i0 >= (uint)src.VertexCount || (uint)i1 >= (uint)src.VertexCount || (uint)i2 >= (uint)src.VertexCount) continue;
                Vector3 p0 = src.Position(i0), p1 = src.Position(i1), p2 = src.Position(i2);
                Vector3 e1 = p1 - p0, e2 = p2 - p0;
                float area = 0.5f * Vector3.Cross(e1, e2).magnitude;
                if (!(area > 1e-8f) || float.IsInfinity(area)) continue;

                Color t = avg;
                if (img != null && src.HasUv)
                {
                    Vector2 u0 = Tile(src.Uv(i0), st), u1 = Tile(src.Uv(i1), st), u2 = Tile(src.Uv(i2), st);
                    // Triangles covering the whole texture (tiled UVs) get its average; others 7 point samples in the 32x32.
                    var ext = Vector2.Max(Vector2.Max(u0, u1), u2) - Vector2.Min(Vector2.Min(u0, u1), u2);
                    if (ext.x < 1f || ext.y < 1f)
                    {
                        t = Sample(img, u0) + Sample(img, u1) + Sample(img, u2)
                          + Sample(img, (u0 + u1) * 0.5f) + Sample(img, (u1 + u2) * 0.5f) + Sample(img, (u2 + u0) * 0.5f)
                          + Sample(img, (u0 + u1 + u2) / 3f) * 2f;
                        t *= 1f / 8f;
                    }
                }
                var le = new Color(baseLe.r * t.r, baseLe.g * t.g, baseLe.b * t.b);
                float lum = Lum(le);
                if (!(lum >= Settings.minRadiance)) { dark++; continue; }
                output.Add(new Candidate { Src = s, P0 = p0, E1 = e1, E2 = e2, Le = le, Area = area, Lum = lum });
            }
            return true;
        }

        private static Vector2 Tile(Vector2 uv, Vector4 st) => new Vector2(uv.x * st.x + st.z, uv.y * st.y + st.w);

        /// <summary>
        /// What the proxy hit shader multiplies with _EmissiveColor * _EmissiveIntensity: _MainTex * _Color with
        /// _UseAlbedoAsEmissive, else _EmissiveTex (white when unset). <paramref name="st"/> is the texture's scale/offset.
        /// </summary>
        private static Texture EmissionTexture(Material m, out Vector4 st, out Color tint, out bool albedo)
        {
            albedo = m.HasProperty("_UseAlbedoAsEmissive") && m.GetFloat("_UseAlbedoAsEmissive") > 0.5f;
            string prop = albedo ? "_MainTex" : "_EmissiveTex";
            tint = albedo && m.HasProperty("_Color") ? m.GetColor("_Color") : Color.white;
            st = new Vector4(1, 1, 0, 0);
            if (!m.HasProperty(prop)) return null;
            var tex = m.GetTexture(prop);
            if (tex == null || tex.dimension != TextureDimension.Tex2D) return null;
            // The hit shader (like the game's Master shader) samples both the albedo and the emission map at
            // uv0 * _MainTex_ST; _EmissiveTex_ST is not used.
            if (m.HasProperty("_MainTex"))
            {
                var sc = m.GetTextureScale("_MainTex");
                var of = m.GetTextureOffset("_MainTex");
                st = new Vector4(sc.x, sc.y, of.x, of.y);
            }
            return tex;
        }

        /// <summary>
        /// 32x32 version of a (usually GPU-only) texture: one Blit (which picks the matching mip level when there are mips,
        /// else point samples a 32x32 grid) and one ReadPixels. Cached per texture.
        /// </summary>
        private Color[] LowRes(Texture tex)
        {
            if (_texCache.TryGetValue(tex, out var img)) return img;
            img = null;
            var fmt = SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.ARGBFloat) ? RenderTextureFormat.ARGBFloat : RenderTextureFormat.ARGBHalf;
            var rt = RenderTexture.GetTemporary(TexRes, TexRes, 0, fmt, RenderTextureReadWrite.Linear);
            var prev = RenderTexture.active;
            try
            {
                Graphics.Blit(tex, rt);
                RenderTexture.active = rt;
                if (_readTex == null) _readTex = new Texture2D(TexRes, TexRes, fmt == RenderTextureFormat.ARGBFloat ? TextureFormat.RGBAFloat : TextureFormat.RGBAHalf, false, true);
                _readTex.ReadPixels(new Rect(0, 0, TexRes, TexRes), 0, 0, false);
                img = _readTex.GetPixels();
            }
            catch (Exception e)
            {
                _log.LogWarning($"Emissive lights: could not read texture '{tex.name}', using white: {e.Message}");
            }
            finally
            {
                RenderTexture.active = prev;
                RenderTexture.ReleaseTemporary(rt);
            }
            _texCache[tex] = img;
            return img;
        }

        private static Color Sample(Color[] img, Vector2 uv)
        {
            float u = uv.x - Mathf.Floor(uv.x), v = uv.y - Mathf.Floor(uv.y);
            int x = Mathf.Min((int)(u * TexRes), TexRes - 1), y = Mathf.Min((int)(v * TexRes), TexRes - 1);
            return img[y * TexRes + x];
        }

        private static Color Average(Color[] img)
        {
            Color s = Color.clear;
            foreach (var c in img) s += c;
            return s * (1f / img.Length);
        }

        // ---------------------------------------------------------------- mesh access

        /// <summary>Positions, UV0 and raw indices of a mesh: a static batch's CPU copy, the CPU mesh, or a GPU readback.</summary>
        private sealed class MeshSource
        {
            public int VertexCount;
            // decoded (readable meshes)
            public Vector3[] Pos;
            public Vector2[] UvArr;
            public int[] IdxArr;
            // raw streams (combined-mesh CPU copy, GPU readback)
            public byte[][] Streams;
            public int[] Strides;
            public uint[] Idx;
            public int PS, PO, PD, US = -1, UO, UD;
            public VertexAttributeFormat PF, UF;

            public bool HasUv => UvArr != null ? UvArr.Length == VertexCount : US >= 0;
            public int Index(int k) => IdxArr != null ? IdxArr[k] : (int)Idx[k];

            public Vector3 Position(int v)
            {
                if (Pos != null) return Pos[v];
                var b = Streams[PS];
                int o = v * Strides[PS] + PO;
                return new Vector3(Decode(b, o, PF, 0), PD > 1 ? Decode(b, o, PF, 1) : 0f, PD > 2 ? Decode(b, o, PF, 2) : 0f);
            }

            public Vector2 Uv(int v)
            {
                if (UvArr != null) return UvArr[v];
                var b = Streams[US];
                int o = v * Strides[US] + UO;
                return new Vector2(Decode(b, o, UF, 0), UD > 1 ? Decode(b, o, UF, 1) : 0f);
            }
        }

        private MeshSource Source(SceneProxies.TracedSubmesh s)
        {
            var mesh = s.Mesh;
            if (s.Streams != null && s.Indices != null)
            {
                // StaticGroups' copy of the combined mesh: no readback. Not cached (the arrays are StaticGroups').
                var src = new MeshSource { Streams = s.Streams, Strides = s.Strides, Idx = s.Indices, VertexCount = mesh.vertexCount };
                return Describe(mesh, src) ? src : null;
            }
            if (_meshCache.TryGetValue(mesh, out var cached)) return cached;
            MeshSource r = null;
            try { r = mesh.isReadable ? FromReadable(mesh) : ReadBack(mesh); }
            catch (Exception e) { _log.LogWarning($"Emissive lights: could not read mesh '{mesh.name}': {e.Message}"); }
            _meshCache[mesh] = r;
            return r;
        }

        private static MeshSource FromReadable(Mesh mesh)
        {
            var src = new MeshSource { Pos = mesh.vertices, UvArr = mesh.uv, VertexCount = mesh.vertexCount };
            // Raw index layout (indexStart/baseVertex from GetSubMesh apply to it): concatenate submeshes in order.
            long end = 0;
            for (int sm = 0; sm < mesh.subMeshCount; sm++) end = Math.Max(end, (long)mesh.GetIndexStart(sm) + mesh.GetIndexCount(sm));
            var idx = new int[end];
            for (int sm = 0; sm < mesh.subMeshCount; sm++)
            {
                var part = mesh.GetIndices(sm, false);
                Array.Copy(part, 0, idx, (int)mesh.GetIndexStart(sm), Math.Min(part.Length, idx.Length - (int)mesh.GetIndexStart(sm)));
            }
            src.IdxArr = idx;
            return src;
        }

        /// <summary>GPU readback of a non-readable mesh: the streams holding position and UV0, and the index buffer.</summary>
        private static MeshSource ReadBack(Mesh mesh)
        {
            var src = new MeshSource { VertexCount = mesh.vertexCount, Streams = new byte[mesh.vertexBufferCount][], Strides = new int[mesh.vertexBufferCount] };
            if (!Describe(mesh, src)) return null;
            foreach (int s in new[] { src.PS, src.US })
            {
                if (s < 0 || src.Streams[s] != null) continue;
                int stride = mesh.GetVertexBufferStride(s);
                src.Strides[s] = stride;
                using (var vb = mesh.GetVertexBuffer(s))
                {
                    var words = new uint[(src.VertexCount * stride + 3) / 4];
                    vb.GetData(words, 0, 0, words.Length);
                    var b = new byte[src.VertexCount * stride];
                    Buffer.BlockCopy(words, 0, b, 0, b.Length);
                    src.Streams[s] = b;
                }
            }
            using (var ib = mesh.GetIndexBuffer())
            {
                uint[] idx;
                if (mesh.indexFormat == IndexFormat.UInt32) { idx = new uint[ib.count]; ib.GetData(idx); }
                else
                {
                    var packed = new uint[(ib.count + 1) / 2];
                    ib.GetData(packed, 0, 0, packed.Length); // 16-bit buffer read as 32-bit words (as StaticGroups does)
                    idx = new uint[ib.count];
                    for (int k = 0; k < ib.count; k++) idx[k] = (packed[k >> 1] >> ((k & 1) * 16)) & 0xFFFF;
                }
                src.Idx = idx;
            }
            return src;
        }

        private static bool Describe(Mesh mesh, MeshSource src)
        {
            if (!mesh.HasVertexAttribute(VertexAttribute.Position)) return false;
            src.PS = mesh.GetVertexAttributeStream(VertexAttribute.Position);
            src.PO = mesh.GetVertexAttributeOffset(VertexAttribute.Position);
            src.PF = mesh.GetVertexAttributeFormat(VertexAttribute.Position);
            src.PD = mesh.GetVertexAttributeDimension(VertexAttribute.Position);
            if (!Decodable(src.PF)) return false;
            if (mesh.HasVertexAttribute(VertexAttribute.TexCoord0))
            {
                src.UF = mesh.GetVertexAttributeFormat(VertexAttribute.TexCoord0);
                if (Decodable(src.UF))
                {
                    src.US = mesh.GetVertexAttributeStream(VertexAttribute.TexCoord0);
                    src.UO = mesh.GetVertexAttributeOffset(VertexAttribute.TexCoord0);
                    src.UD = mesh.GetVertexAttributeDimension(VertexAttribute.TexCoord0);
                }
            }
            if (src.Strides != null && src.Streams != null)
            {
                if (src.PS >= src.Streams.Length) return false;
                if (src.US >= src.Streams.Length) src.US = -1;
            }
            return true;
        }

        private static bool Decodable(VertexAttributeFormat f) =>
            f == VertexAttributeFormat.Float32 || f == VertexAttributeFormat.Float16 || f == VertexAttributeFormat.UNorm8 ||
            f == VertexAttributeFormat.SNorm8 || f == VertexAttributeFormat.UNorm16 || f == VertexAttributeFormat.SNorm16;

        private static float Decode(byte[] b, int off, VertexAttributeFormat f, int i)
        {
            switch (f)
            {
                case VertexAttributeFormat.Float32: return BitConverter.ToSingle(b, off + i * 4);
                case VertexAttributeFormat.Float16: return Mathf.HalfToFloat(BitConverter.ToUInt16(b, off + i * 2));
                case VertexAttributeFormat.UNorm8: return b[off + i] / 255f;
                case VertexAttributeFormat.SNorm8: return Mathf.Max((sbyte)b[off + i] / 127f, -1f);
                case VertexAttributeFormat.UNorm16: return BitConverter.ToUInt16(b, off + i * 2) / 65535f;
                case VertexAttributeFormat.SNorm16: return Mathf.Max(BitConverter.ToInt16(b, off + i * 2) / 32767f, -1f);
                default: return 0f;
            }
        }

        // ---------------------------------------------------------------- helpers

        private static float Lum(Color c) => 0.2126f * c.r + 0.7152f * c.g + 0.0722f * c.b;
        private static uint AsUInt(float f) => unchecked((uint)BitConverter.SingleToInt32Bits(f));
        private static uint Half(float f) => Mathf.FloatToHalf(Mathf.Clamp(float.IsNaN(f) ? 0f : f, 0f, 60000f));

        public void Dispose()
        {
            Clear();
            _tris?.Release(); _tris = null;
            _ownersBuf?.Release(); _ownersBuf = null;
            _alias?.Release(); _alias = null;
            _active?.Release(); _active = null;
            _dummy4?.Release(); _dummy4 = null;
            _dummy1?.Release(); _dummy1 = null;
            if (_readTex != null) Object.Destroy(_readTex);
            _readTex = null;
        }
    }
}
