using System;
using System.Collections.Generic;
using BepInEx.Logging;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace UltraRT
{
    /// <summary>Tunables of <see cref="EffectGeometry"/>, read every frame; bench prefix <c>fxg.</c>.</summary>
    public sealed class EffectGeometrySettings
    {
        /// <summary>Glowing effects (fire, explosions, flashes, beams) are visible to reflection rays.</summary>
        public bool enabled = true;
        /// <summary>Brightness of effects in reflections (1 = as the game draws them).</summary>
        public float glow = 1f;
        /// <summary>Optional fade on rough lobes: lobes up to this roughness show effects in full, rougher ones fade to
        /// <see cref="roughWeight"/> at 0.6. 1 = no fade (the default: a rough lobe already spreads the reflection).</summary>
        public float smoothBelow = 0.2f;
        public float roughWeight = 1f;
        /// <summary>Optional: a pixel the game draws an effect over shows less of the effects in its own reflections
        /// (1 - luminance of the effect there x this; one more any-hit ray per pixel while effects are active). 0 = off.</summary>
        public float coverFade = 0f;
        /// <summary>Most effect instances in the RTAS at once (the nearest ones win).</summary>
        public int maxInstances = 48;
        /// <summary>Effects farther from the camera than this are left out (m).</summary>
        public float maxDistance = 80f;
        /// <summary>Small glowing sprites (candles, small flames) farther than this are left out (m): sub-pixel in reflections.</summary>
        public float maxSpriteDistance = 35f;
        /// <summary>Particle systems with more live particles than this are left out (their mesh is rebuilt every frame).</summary>
        public int maxParticles = 400;
        /// <summary>Most billboards in the RTAS at once, nearest effects first: every specular ray that crosses a billboard
        /// runs its any-hit shader, so wide clouds of them cost dispatch time.</summary>
        public int maxQuads = 1200;
        /// <summary>Trails and lines (beams, projectile trails) too, not only particles and sprites.</summary>
        public bool trails = true;
    }

    /// <summary>
    /// Effects in reflections (docs/path-tracing.md): the game's glowing particle systems, sprites, trails and lines are
    /// drawn by the game after the RT image, so no ray ever saw them (a terminal screen next to a fire reflected an empty
    /// room). Every renderer EffectLights classifies as glowing (additive blend, not smoke or dust) gets a mesh of its
    /// billboards, rebuilt each frame with the renderer's own BakeMesh, on a hidden proxy in the RTAS under the
    /// transparent mask with the UltraRT/EffectProxy hit group. Specular rays collect them with one extra ray
    /// (UltraRTEffects.hlsl); nothing else sees them.
    /// </summary>
    internal sealed class EffectGeometry : IDisposable
    {
        public EffectGeometrySettings Settings = new EffectGeometrySettings();

        private enum Kind { Particles, Sprite, Line, Trail }

        private sealed class Entry
        {
            public Renderer R;
            public int Id;
            public Kind Kind;
            public ParticleSystem PS;
            public ParticleSystemRenderer PSR;
            public SpriteRenderer SR;
            public LineRenderer LR;
            public TrailRenderer TR;
            public Material Source;
            public Mesh Mesh;
            public GameObject Go;
            public MeshRenderer Proxy;
            public Material Mat;
            public bool InRtas;
            public int Wait;          // frames the proxy has existed with geometry, before it may enter the RTAS
            public Sprite Sprite;     // the sprite the mesh was built from
            public Color32 SpriteColor;
            public bool SpriteFlipX, SpriteFlipY;
            public float Dist;
            public int Quads;         // billboards this frame (estimate, for the budget)
            public int Idle;          // frames since it was last wanted: after IdleFrames it is only polled now and then
            public int Space = -1;    // how the baked mesh maps to the world: 0 world, 1 position only, 2 the full transform
        }

        private readonly ManualLogSource _log;
        private readonly List<Entry> _entries = new List<Entry>(128);
        private readonly HashSet<int> _ids = new HashSet<int>();
        private readonly List<Entry> _wanted = new List<Entry>(128);
        private readonly MaterialPropertyBlock _mpb = new MaterialPropertyBlock();
        private readonly List<Vector3> _verts = new List<Vector3>(4);
        private readonly List<Vector2> _uvs = new List<Vector2>(4);
        private readonly List<Color32> _colors = new List<Color32>(4);
        private readonly List<int> _tris = new List<int>(6);
        private struct SpriteGeom { public Vector2[] Verts, Uvs; public ushort[] Tris; }
        private readonly Dictionary<Sprite, SpriteGeom> _spriteCache = new Dictionary<Sprite, SpriteGeom>();
        private struct MatKey : IEquatable<MatKey>
        {
            public Color32 Tint;
            public bool OneOne;
            public Texture Tex;
            public Vector4 St;
            public bool Equals(MatKey o) => Tint.r == o.Tint.r && Tint.g == o.Tint.g && Tint.b == o.Tint.b && Tint.a == o.Tint.a && OneOne == o.OneOne && ReferenceEquals(Tex, o.Tex) && St == o.St;
            public override bool Equals(object o) => o is MatKey k && Equals(k);
            public override int GetHashCode()
            {
                unchecked
                {
                    int h = (Tint.r | (Tint.g << 8) | (Tint.b << 16) | (Tint.a << 24)) ^ (OneOne ? 0x5A5A5A5A : 0);
                    if (Tex != null) h = h * 31 + Tex.GetInstanceID();
                    return h * 31 + St.GetHashCode();
                }
            }
        }
        private readonly Dictionary<MatKey, Material> _matPool = new Dictionary<MatKey, Material>();
        private static readonly RayTracingSubMeshFlags[] OneEnabled = { RayTracingSubMeshFlags.Enabled };
        private RayTracingAccelerationStructure _rtas;
        private Shader _shader;
        private GameObject _root;
        private int _logs, _addLogs, _maxActive;
        private float _nextLog;
        private long _ticks;
        private int _frames;

        private static readonly int MainTexId = Shader.PropertyToID("_MainTex"), TintId = Shader.PropertyToID("_Tint"), ModeId = Shader.PropertyToID("_Mode"),
            CountId = Shader.PropertyToID("_UltraRTEffects"), ParamsId = Shader.PropertyToID("_UltraRTEffectParams");
        private const int ViewmodelLayer = 13;
        private static readonly string[] NotGlows = { "rain", "heat" };
        /// <summary>Frames a new mesh and proxy wait before AddInstance (as portal quads: an instance added in the frame its
        /// mesh was made is never hit).</summary>
        private const int AddDelay = 2;
        /// <summary>Most tracked effects are switched off (pooled fires, a level's unlit torches): an effect that has not been
        /// active for IdleFrames is checked only once every ColdStride frames, in turn. A fresh spawn starts hot.</summary>
        private const int IdleFrames = 90, ColdStride = 12;
        private int _frame;

        /// <summary>Instances in the RTAS after the last <see cref="Update"/>.</summary>
        public int Active { get; private set; }
        public int Tracked => _entries.Count;

        public EffectGeometry(ManualLogSource log) { _log = log; }

        public void SetShader(Shader shader)
        {
            if (shader == _shader) return;
            foreach (var e in _entries) ReleaseObjects(e); // materials of the old bundle; the effects stay tracked
            ClearMatPool();
            _shader = shader;
        }

        private void ClearMatPool()
        {
            foreach (var m in _matPool.Values) if (m != null) Object.Destroy(m);
            _matPool.Clear();
        }

        private Material PooledMaterial(Color tint, bool oneOne, Texture tex, Vector2 scale, Vector2 offset)
        {
            if (tex == null) tex = Texture2D.whiteTexture;
            var key = new MatKey { Tint = (Color32)tint, OneOne = oneOne, Tex = tex, St = new Vector4(scale.x, scale.y, offset.x, offset.y) };
            if (_matPool.TryGetValue(key, out var mat) && mat != null) return mat;
            if (_matPool.Count > 256) ClearMatPool();
            mat = new Material(_shader) { name = "UltraRT effect (pooled)", hideFlags = HideFlags.HideAndDontSave };
            mat.SetColor(TintId, tint);
            mat.SetFloat(ModeId, oneOne ? 1f : 0f);
            mat.SetTexture(MainTexId, tex);
            mat.SetTextureScale(MainTexId, scale);
            mat.SetTextureOffset(MainTexId, offset);
            _matPool[key] = mat;
            return mat;
        }

        /// <summary>A renderer whose material glows (EffectLights.Analyse): from the scene scan and from every spawn.</summary>
        public void Track(Renderer r)
        {
            if (r == null || _shader == null || r.gameObject.layer == ViewmodelLayer) return;
            Kind kind;
            if (r is ParticleSystemRenderer) kind = Kind.Particles;
            else if (r is SpriteRenderer) kind = Kind.Sprite;
            else if (r is LineRenderer) kind = Kind.Line;
            else if (r is TrailRenderer) kind = Kind.Trail;
            else return;
            int id = r.GetInstanceID();
            if (_ids.Contains(id)) return;
            // Additive, but not something a reflection should show as a glow: rain streaks, heat shimmer.
            string mn = r.sharedMaterial != null ? r.sharedMaterial.name : "";
            foreach (var x in NotGlows)
                if (mn.IndexOf(x, StringComparison.OrdinalIgnoreCase) >= 0 || r.name.IndexOf(x, StringComparison.OrdinalIgnoreCase) >= 0) return;
            var e = new Entry { R = r, Id = id, Kind = kind, Source = r.sharedMaterial };
            switch (kind)
            {
                case Kind.Particles: e.PSR = (ParticleSystemRenderer)r; e.PS = r.GetComponent<ParticleSystem>(); if (e.PS == null) return; break;
                case Kind.Sprite: e.SR = (SpriteRenderer)r; break;
                case Kind.Line: e.LR = (LineRenderer)r; break;
                default: e.TR = (TrailRenderer)r; break;
            }
            _ids.Add(id);
            _entries.Add(e);
        }

        /// <summary>
        /// Per traced frame, before the RTAS is built: drop destroyed effects, pick the nearest active ones, rebuild their
        /// meshes and keep their instances in step. Sets the globals the effects ray reads.
        /// </summary>
        public void Update(RayTracingAccelerationStructure rtas, Camera cam)
        {
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            if (rtas != null && rtas != _rtas) SceneReset(rtas); // a new RT scene: the old structure went with its instances
            var s = Settings;
            bool on = s.enabled && _shader != null && rtas != null && cam != null;
            Vector3 cp = cam != null ? cam.transform.position : Vector3.zero;
            float maxD2 = s.maxDistance * s.maxDistance;
            _wanted.Clear();
            _frame++;
            for (int i = _entries.Count - 1; i >= 0; i--)
            {
                var e = _entries[i];
                // Cold entries: one slice per frame (each Unity property below is a native call; hundreds of them cost 0.3 ms).
                if (on && e.Idle >= IdleFrames && !e.InRtas && (i + _frame) % ColdStride != 0) continue;
                if (e.R == null)
                {
                    Release(e);
                    _entries[i] = _entries[_entries.Count - 1];
                    _entries.RemoveAt(_entries.Count - 1);
                    continue;
                }
                bool want = on && e.R.enabled && e.R.gameObject.activeInHierarchy;
                if (want && (e.Kind == Kind.Line || e.Kind == Kind.Trail) && !s.trails) want = false;
                e.Quads = 1;
                if (want && e.Kind == Kind.Particles) { int n = e.PS.particleCount; e.Quads = n; want = n > 0 && n <= s.maxParticles; }
                if (want && e.Kind == Kind.Sprite) want = e.SR.sprite != null && e.SR.color.a > 0.004f;
                if (want && e.Kind == Kind.Line) e.Quads = Mathf.Max(1, e.LR.positionCount);
                if (want && e.Kind == Kind.Trail) e.Quads = Mathf.Max(1, e.TR.positionCount);
                if (want)
                {
                    e.Dist = PortalView.SqrDistance(e.R.bounds.center, cp); // or to the camera's image through a portal
                    float lim = e.Kind == Kind.Sprite && s.maxSpriteDistance > 0f ? Mathf.Min(s.maxDistance, s.maxSpriteDistance) : s.maxDistance;
                    want = e.Dist <= lim * lim;
                }
                if (want) { e.Idle = 0; _wanted.Add(e); }
                else { if (e.Idle < IdleFrames) e.Idle++; Remove(e); }
            }
            // The nearest effects, within the instance and billboard budgets.
            if (_wanted.Count > 1) _wanted.Sort((a, b) => a.Dist.CompareTo(b.Dist));
            int keep = 0, quads = 0;
            while (keep < _wanted.Count && keep < s.maxInstances && quads + _wanted[keep].Quads <= s.maxQuads) quads += _wanted[keep++].Quads;
            if (keep < _wanted.Count)
            {
                for (int i = keep; i < _wanted.Count; i++) Remove(_wanted[i]);
                _wanted.RemoveRange(keep, _wanted.Count - keep);
            }

            int active = 0;
            foreach (var e in _wanted)
            {
                try
                {
                    if (!Bake(e, cam)) { Remove(e); continue; }
                    if (!e.InRtas)
                    {
                        if (++e.Wait < AddDelay) continue;
                        rtas.AddInstance(e.Proxy, OneEnabled, enableTriangleCulling: false,
                            frontTriangleCounterClockwise: false, mask: SceneProxies.TransparentMask, id: 0);
                        e.InRtas = true;
                        if (_addLogs++ < 24)
                            _log.LogInfo($"Effect geometry: + {e.Kind} '{Bench.GameControl.PathOf(e.R.transform)}' mat '{(e.Source != null ? e.Source.name + "' / '" + e.Source.shader.name : "-")}' " +
                                         $"{e.Mesh.vertexCount} verts, bounds {e.Proxy.bounds.center:F1} size {e.Proxy.bounds.size:F1}, {Mathf.Sqrt(e.Dist):F1} m away");
                    }
                    else rtas.UpdateInstanceTransform(e.Proxy);
                    active++;
                }
                catch (Exception ex)
                {
                    if (_logs++ < 20) _log.LogWarning($"Effect geometry: '{(e.R != null ? e.R.name : "?")}' failed: {ex.Message}");
                    Remove(e);
                }
            }
            Active = active;
            if (active > _maxActive) _maxActive = active;
            Shader.SetGlobalInt(CountId, active);
            Shader.SetGlobalVector(ParamsId, new Vector4(Mathf.Max(0f, s.glow), Mathf.Clamp(s.smoothBelow, 0f, 0.55f), Mathf.Clamp01(s.roughWeight), Mathf.Max(0f, s.coverFade)));

            _ticks += System.Diagnostics.Stopwatch.GetTimestamp() - t0;
            _frames++;
            if (Time.realtimeSinceStartup >= _nextLog && _frames > 0)
            {
                _nextLog = Time.realtimeSinceStartup + 10f;
                if (active > 0 || _entries.Count > 0)
                    _log.LogInfo($"Effect geometry: {active} instances in the RTAS now (most {_maxActive} since the last line) of {_entries.Count} glowing effects tracked, " +
                                 $"{_ticks * 1000.0 / System.Diagnostics.Stopwatch.Frequency / _frames:F3} ms/frame CPU");
                _maxActive = 0;
                _ticks = 0;
                _frames = 0;
            }
        }

        /// <summary>Rebuild the entry's mesh for this frame and place its proxy. False = nothing to trace.</summary>
        private bool Bake(Entry e, Camera cam)
        {
            if (e.Go == null) Create(e);
            if (e.R.sharedMaterial != e.Source) { e.Source = e.R.sharedMaterial; ConfigureMaterial(e); }
            var t = e.R.transform;
            if (e.Kind == Kind.Sprite)
            {
                var sr = e.SR;
                Color32 c = sr.color;
                if (sr.sprite != e.Sprite || !c.Equals(e.SpriteColor) || sr.flipX != e.SpriteFlipX || sr.flipY != e.SpriteFlipY) BuildSprite(e, c);
                // An AnimatedTexture swaps the texture through the property block.
                Texture tex = null;
                if (sr.HasPropertyBlock()) { sr.GetPropertyBlock(_mpb); tex = _mpb.GetTexture(MainTexId); }
                var useTex = tex != null ? tex : e.Sprite.texture;
                var m = e.Source;
                Color tint = m != null ? LitParticles.EffectTint(m) : Color.white;
                bool oneOne = m != null && LitParticles.EffectSrcOne(m);
                var pooled = PooledMaterial(tint, oneOne, useTex, Vector2.one, Vector2.zero);
                if (!ReferenceEquals(e.Mat, pooled)) { e.Mat = pooled; e.Proxy.sharedMaterial = pooled; }
                e.Go.transform.SetPositionAndRotation(t.position, t.rotation);
                e.Go.transform.localScale = t.lossyScale;
                return e.Mesh.vertexCount > 0;
            }

            // The renderers' own billboards for this camera, with rotation and scale baked in.
            if (e.Kind == Kind.Particles) e.PSR.BakeMesh(e.Mesh, cam, true);
            else if (e.Kind == Kind.Line) e.LR.BakeMesh(e.Mesh, cam, true);
            else e.TR.BakeMesh(e.Mesh, cam, true);
            if (e.Mesh.vertexCount < 3) return false;
            e.Mesh.RecalculateBounds();
            // Where the baked mesh sits depends on the simulation space; find out once per effect by comparing it with the
            // renderer's world bounds.
            var mb = e.Mesh.bounds;
            var wb = e.R.bounds;
            if (e.Space < 0)
            {
                float world = (mb.center - wb.center).sqrMagnitude;
                float pos = (mb.center + t.position - wb.center).sqrMagnitude;
                float full = (t.TransformPoint(mb.center) - wb.center).sqrMagnitude;
                e.Space = world <= pos && world <= full ? 0 : pos <= full ? 1 : 2;
                if (_logs++ < 12)
                    _log.LogInfo($"Effect geometry: {e.Kind} '{Bench.GameControl.PathOf(t)}' mat '{(e.Source != null ? e.Source.name : "-")}' " +
                                 $"{e.Mesh.vertexCount} verts, baked in {(e.Space == 0 ? "world" : e.Space == 1 ? "position-relative" : "local")} space " +
                                 $"(offsets {Mathf.Sqrt(world):F2} / {Mathf.Sqrt(pos):F2} / {Mathf.Sqrt(full):F2} m)");
            }
            var gt = e.Go.transform;
            if (e.Space == 0) { gt.SetPositionAndRotation(Vector3.zero, Quaternion.identity); gt.localScale = Vector3.one; }
            else if (e.Space == 1) { gt.SetPositionAndRotation(t.position, Quaternion.identity); gt.localScale = Vector3.one; }
            else { gt.SetPositionAndRotation(t.position, t.rotation); gt.localScale = t.lossyScale; }
            return true;
        }

        private void BuildSprite(Entry e, Color32 c)
        {
            var sr = e.SR;
            var sp = sr.sprite;
            e.Sprite = sp;
            e.SpriteColor = c;
            e.SpriteFlipX = sr.flipX;
            e.SpriteFlipY = sr.flipY;
            _verts.Clear(); _uvs.Clear(); _colors.Clear(); _tris.Clear();
            if (!_spriteCache.TryGetValue(sp, out var g))
            {
                if (_spriteCache.Count > 2048) _spriteCache.Clear();
                g = new SpriteGeom { Verts = sp.vertices, Uvs = sp.uv, Tris = sp.triangles };
                _spriteCache[sp] = g;
            }
            var v = g.Verts;
            var uv = g.Uvs;
            var tri = g.Tris;
            float fx = sr.flipX ? -1f : 1f, fy = sr.flipY ? -1f : 1f;
            for (int i = 0; i < v.Length; i++)
            {
                _verts.Add(new Vector3(v[i].x * fx, v[i].y * fy, 0f));
                _uvs.Add(uv[i]);
                _colors.Add(c);
            }
            for (int i = 0; i < tri.Length; i++) _tris.Add(tri[i]);
            e.Mesh.Clear();
            e.Mesh.SetVertices(_verts);
            e.Mesh.SetUVs(0, _uvs);
            e.Mesh.SetColors(_colors);
            e.Mesh.SetTriangles(_tris, 0);
            e.Mesh.RecalculateBounds();
        }

        private void Create(Entry e)
        {
            if (_root == null)
            {
                _root = new GameObject("UltraRT effects [UltraRT]") { hideFlags = HideFlags.HideAndDontSave };
                Object.DontDestroyOnLoad(_root);
            }
            e.Mesh = new Mesh { name = "UltraRT effect", hideFlags = HideFlags.HideAndDontSave };
            e.Mesh.MarkDynamic();
            e.Go = new GameObject($"{e.R.name} (effect) [UltraRT]") { hideFlags = HideFlags.HideAndDontSave };
            e.Go.transform.SetParent(_root.transform, false);
            e.Go.AddComponent<MeshFilter>().sharedMesh = e.Mesh;
            e.Proxy = e.Go.AddComponent<MeshRenderer>();
            e.Proxy.shadowCastingMode = ShadowCastingMode.Off;
            e.Proxy.receiveShadows = false;
            e.Proxy.forceRenderingOff = true; // never rasterised; still eligible for the RTAS
            e.Proxy.rayTracingMode = RayTracingMode.DynamicGeometry; // the mesh changes every frame
            ConfigureMaterial(e);
        }

        /// <summary>What the source material draws per pixel of a billboard: texture x tint x vertex colour, as EffectLights.Analyse reads it.</summary>
        private void ConfigureMaterial(Entry e)
        {
            var m = e.Source;
            if (e.Proxy == null || _shader == null) return;
            Color tint = m != null ? LitParticles.EffectTint(m) : Color.white; // swapped materials (soft particles) keep their tint and blend
            bool oneOne = m != null && LitParticles.EffectSrcOne(m);
            Texture tex = Texture2D.whiteTexture;
            Vector2 sc = Vector2.one, off = Vector2.zero;
            if (e.Kind != Kind.Sprite && m != null && m.HasProperty(MainTexId))
            {
                tex = m.mainTexture != null ? m.mainTexture : Texture2D.whiteTexture;
                sc = m.mainTextureScale;
                off = m.mainTextureOffset;
            }
            e.Mat = PooledMaterial(tint, oneOne, tex, sc, off);
            e.Proxy.sharedMaterial = e.Mat;
        }

        private void Remove(Entry e)
        {
            if (e.InRtas && _rtas != null && e.Proxy != null)
            {
                try { _rtas.RemoveInstance(e.Proxy); } catch (Exception) { /* the structure was released with its scene */ }
            }
            e.InRtas = false;
            e.Wait = 0;
        }

        private void Release(Entry e)
        {
            ReleaseObjects(e);
            _ids.Remove(e.Id);
        }

        private void ReleaseObjects(Entry e)
        {
            Remove(e);
            e.Sprite = null;
            e.Space = -1;
            if (e.Go != null) Object.Destroy(e.Go);
            if (e.Mesh != null) Object.Destroy(e.Mesh);
            e.Go = null; e.Mesh = null; e.Mat = null; e.Proxy = null;
        }

        /// <summary>The RT scene is being released (or replaced): its instances go with it; the effects stay tracked.</summary>
        public void SceneReset(RayTracingAccelerationStructure next = null)
        {
            foreach (var e in _entries) { e.InRtas = false; e.Wait = 0; }
            _rtas = next;
            Active = 0;
            Shader.SetGlobalInt(CountId, 0);
        }

        /// <summary>Forget everything tracked (the effects of the next scene scan and spawns are tracked again).</summary>
        public void Clear()
        {
            foreach (var e in _entries) Release(e);
            _entries.Clear();
            _ids.Clear();
            _wanted.Clear();
            _rtas = null;
            Active = 0;
            Shader.SetGlobalInt(CountId, 0);
        }

        public void Dispose()
        {
            Clear();
            ClearMatPool();
            if (_root != null) Object.Destroy(_root);
            _root = null;
        }
    }
}
