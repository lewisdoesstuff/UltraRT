using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace UltraRT
{
    /// <summary>Stained-glass projection (docs/materials.md, "Stained-glass projection"). Bench: sg.strength=0 (off),
    /// sg.tilt=35. Flag -ultrart-stained-glass F (off = 0); menu Advanced > Effects, config [Effects] StainedGlassProjection.</summary>
    internal sealed class StainedGlassSettings
    {
        /// <summary>Brightness of the projected mosaic: the pane's (gamma) colour x this is the light it gives a surface
        /// facing the beam (x relight exposure, x cos). 0 = off (no buffer, no shader work).</summary>
        public float strength = 3f;
        /// <summary>Degrees below the horizon the beam of an upright window falls (along its inward normal). 6-2's game
        /// beams fall 27 deg (docs/lighting-overrides.md, "Window beams in 6-2"); 40 lands the high windows of 1-4, 6-2
        /// and P-2 on the floor of their halls.</summary>
        public float tilt = 40f;
        /// <summary>How far a beam reaches (m).</summary>
        public float reach = 80f;
        /// <summary>Gamma colour below this passes no light (the dark leading); above it the colour is stretched back to 1.</summary>
        public float leadFloor = 0.12f;
        /// <summary>Saturation of the projected colour (1 = the pane's own).</summary>
        public float saturation = 1.3f;
        /// <summary>Windows bound per frame (the nearest beams to the camera and the portal views).</summary>
        public int maxWindows = 16;
        /// <summary>Texels per side of each window's slice.</summary>
        public int resolution = 256;
        /// <summary>Comma-separated globs of material names (original materials for StaticSceneOptimizer's atlas slots).</summary>
        public string materials = "StainedGlass*,GothicWindow*";
        /// <summary>1: no shadow ray (debug: the bare projection).</summary>
        public bool noShadow;
        /// <summary>Windows whose baked pane is greyer than this (mean chroma, 0-1) and flat are not stained glass (0 = keep all).</summary>
        public float minChroma = 0.08f;
        /// <summary>Diagnostic: 1 writes every baked slice to BepInEx/ultrart/stained-glass/&lt;scene&gt;_&lt;slice&gt;.png
        /// (game-derived images: never commit them).</summary>
        public bool dump;
        /// <summary>Diagnostic: 1 = no shadow ray; 2 = no shadow ray, white wherever a beam reaches (ignores the texture).</summary>
        public int debug;
        /// <summary>Diagnostic: false leaves the slices empty (no draws).</summary>
        public bool bake = true;
        /// <summary>Log every window found (path, plane, inward side, beam).</summary>
        public bool log = true;
        /// <summary>Under water (docs/materials.md, section 14, "Under water"): 1 = the light that lands under a water plane
        /// crossed the water (dimmed, shifted by the ripples, blurred with depth); 0 = as on a dry floor (the first look).</summary>
        public bool water = true;
        /// <summary>Under water: the blur's radius on the pane per metre of the light's path in the water (m / m).</summary>
        public float waterBlur = 0.25f;
        /// <summary>Under water: the ripples' shift of the lookup, x the ripple caustics' own (1: the mosaic moves with the
        /// caustic lines; 0 = no shift).</summary>
        public float waterShift = 3f;
        /// <summary>Under water: x the overlay's water absorption per metre (trans.waterAbsorb), along the light's path.</summary>
        public float waterAbsorbScale = 2.5f;

        public override string ToString() =>
            $"strength {strength:0.##}, tilt {tilt:0.#} deg, reach {reach:0} m, leading floor {leadFloor:0.##}, saturation {saturation:0.##}, " +
            $"max windows {maxWindows}, {resolution} px, materials '{materials}'{(noShadow ? ", no shadow (debug)" : "")}, " +
            (water ? $"under water: blur {waterBlur:0.###} m/m, shift x{waterShift:0.##}, absorption x{waterAbsorbScale:0.##}" : "under water: as dry");

        /// <summary>The fields that need a new scene build of the windows when they change.</summary>
        public string BuildKey => $"{tilt:0.###}|{reach:0.###}|{resolution}|{materials}|{bake}";
    }

    /// <summary>
    /// Stained-glass projection. ULTRAKILL's stained glass rarely has a sun behind it (1-4's arena, 6-2's halls, P-2's
    /// hall: docs/materials.md, "Stained glass"), so the transmission through it never shows. Here every stained-glass
    /// window sends its own collimated beam into the room: at the RT scene build each window renderer's triangles are
    /// grouped into flat windows (plane, extent, the side facing the room), each window's texture is baked by drawing its
    /// own mesh flat into one slice of a texture array (StainedGlassBake.shader), and per frame the windows nearest the
    /// camera go to PathTrace (UltraRTStainedGlass.hlsl), which adds the pane colour along the beam, shadowed by one
    /// ray, to the direct light at primary hits in relight and pathtrace.
    /// </summary>
    public partial class Plugin
    {
        private readonly StainedGlassSettings _sgSettings = new StainedGlassSettings();
        private const int SgStride = 5; // float4s per window, as UltraRTStainedGlass.hlsl
        private const int SgMaxSlices = 96;
        private Shader _sgBakeShader;
        private Material _sgBakeMat;
        private readonly List<SgWindow> _sgWindows = new List<SgWindow>();
        private object _sgBuiltFor;
        private string _sgBuiltKey;
        private float _sgNextCheck;
        private RenderTexture _sgArray;
        private ComputeBuffer _sgBuffer, _sgDummyBuffer;
        private Texture2DArray _sgDummyTex;
        private Vector4[] _sgData = new Vector4[0];
        private readonly List<(float, SgWindow)> _sgOrder = new List<(float, SgWindow)>();
        private float _sgNextLog;
        private int _sgBound;

        private static readonly PropertyInfo SgStaticBatchRoot =
            typeof(Renderer).GetProperty("staticBatchRootTransform", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly int IdSgTex = Shader.PropertyToID("_SGTex"), IdSgTexSt = Shader.PropertyToID("_SGTex_ST"),
            IdSgColor = Shader.PropertyToID("_SGColor"), IdSgOrigin = Shader.PropertyToID("_SGOrigin"), IdSgU = Shader.PropertyToID("_SGU"),
            IdSgV = Shader.PropertyToID("_SGV"), IdSgN = Shader.PropertyToID("_SGN");

        private sealed class SgWindow
        {
            public MeshRenderer Renderer;
            public string Path, Material;
            public Mesh BakeMesh;        // the window's own triangles (compact copy of the source's vertices), or null
            public Mesh SourceMesh;      // fallback: the source submesh, drawn whole (off-plane triangles are discarded)
            public int SourceSub;
            public Matrix4x4 ToWorld;
            public Texture Tex;
            public Vector4 TexSt;
            public Color Color;
            public bool Atlas;
            public Vector3 Centre, U, V, Normal, Dir;
            public float HalfW, HalfH;
            public int Slice;
            public string Key;           // windows with the same key share a slice
            public bool Baker;           // the window that bakes its slice
            public Vector3 N0;           // the plane normal (the area-weighted front of the faces)
            public float Facing;         // 1: one-sided towards N0, -1 away, ~0 two-sided
            public bool Decided, Dead;   // room side found (SgDecide); no room side (never bound)
            public string Tag;           // log description
            public int Tris;
            public string How;
            public bool Live => Renderer != null && Renderer.enabled && Renderer.gameObject.activeInHierarchy;
        }

        private void LoadStainedGlassShader(AssetBundle bundle)
        {
            _sgBakeShader = bundle.LoadAllAssets<Shader>().FirstOrDefault(s => s.name == "Hidden/UltraRT/StainedGlassBake");
            if (_sgBakeMat != null) Destroy(_sgBakeMat);
            _sgBakeMat = _sgBakeShader != null && _sgBakeShader.isSupported ? new Material(_sgBakeShader) { name = "UltraRT Stained Glass Bake" } : null;
            if (_sgBakeMat == null) Logger.LogWarning("StainedGlassBake shader missing or unsupported: no stained-glass projection");
            _sgBuiltFor = null; // re-bake with the new shader
        }

        /// <summary>Awake (from ParseMaterialArgs): -ultrart-stained-glass F|off, -ultrart-stained-glass-tilt F.</summary>
        private void ParseStainedGlassArgs()
        {
            var s = _sgSettings;
            var args = Environment.GetCommandLineArgs();
            for (int i = 0; i + 1 < args.Length; i++)
            {
                string a = args[i].ToLowerInvariant(), v = args[i + 1];
                bool parsed = float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var f);
                switch (a)
                {
                    case "-ultrart-stained-glass": s.strength = parsed ? Mathf.Max(0f, f) : (v == "off" ? 0f : s.strength); break;
                    case "-ultrart-stained-glass-tilt": if (parsed) s.tilt = f; break;
                    case "-ultrart-stained-glass-dump": s.dump = v != "off" && v != "0"; break;
                }
            }
            Logger.LogInfo($"Stained-glass projection: {s}");
        }

        /// <summary>Trace, before the dispatch (relight, pathtrace and the other traced modes): the windows nearest the
        /// camera and their texture array. Nothing is bound (count 0) when the strength is 0 or the scene has no windows.</summary>
        private void BindStainedGlass(RayTracingShader shader)
        {
            var s = _sgSettings;
            int count = 0;
            if (s.strength > 0f && _proxies != null && _sgBakeMat != null)
            {
                try
                {
                    SgEnsureBuilt();
                    count = SgPack();
                }
                catch (Exception e)
                {
                    Logger.LogWarning($"Stained-glass projection failed, switching it off: {e}");
                    s.strength = 0f;
                    count = 0;
                }
            }
            if (_sgDummyBuffer == null)
            {
                _sgDummyBuffer = new ComputeBuffer(SgStride, 16) { name = "UltraRT Stained Glass (none)" };
                _sgDummyBuffer.SetData(new Vector4[SgStride]);
                _sgDummyTex = new Texture2DArray(1, 1, 1, TextureFormat.RGBA32, false) { name = "UltraRT Stained Glass Tex (none)" };
                _sgDummyTex.SetPixels32(new[] { new Color32(0, 0, 0, 0) }, 0);
                _sgDummyTex.Apply(false, true);
            }
            bool on = count > 0;
            _cmd.SetRayTracingBufferParam(shader, "_UltraRTStainedGlass", on ? _sgBuffer : _sgDummyBuffer);
            _cmd.SetRayTracingIntParam(shader, "_UltraRTStainedGlassCount", count);
            _cmd.SetRayTracingTextureParam(shader, "_UltraRTStainedGlassTex", on ? (Texture)_sgArray : _sgDummyTex);
            _cmd.SetRayTracingVectorParam(shader, "_UltraRTStainedGlassParams",
                new Vector4(on ? s.strength : 0f, Mathf.Clamp(s.leadFloor, 0f, 0.95f), Mathf.Max(0f, s.saturation), s.noShadow ? Mathf.Max(1, s.debug) : s.debug));
            // Under water: the light's path through the water (the planes are the ripple caustics', bound while windows are).
            var t = SceneProxies.Transparents;
            _cmd.SetRayTracingVectorParam(shader, "_UltraRTStainedGlassWater",
                new Vector4(Mathf.Max(t.waterAbsorb, 0f) * Mathf.Max(s.waterAbsorbScale, 0f), Mathf.Clamp01(t.waterAbsorbFloor), Mathf.Max(t.waterAbsorbMaxPath, 0.1f), on && s.water ? 1f : 0f));
            _cmd.SetRayTracingVectorParam(shader, "_UltraRTStainedGlassWater2", new Vector4(Mathf.Max(s.waterBlur, 0f), Mathf.Max(s.waterShift, 0f), 0f, 0f));
            if (count != _sgBound) Logger.LogInfo($"Stained-glass projection: {count} windows bound (was {_sgBound})");
            _sgBound = count;
        }

        /// <summary>Builds the windows once per RT scene (and again when a window renderer is destroyed: checkpoint
        /// restores replace rooms, or when the build settings change).</summary>
        private void SgEnsureBuilt()
        {
            var s = _sgSettings;
            bool rebuild = !ReferenceEquals(_sgBuiltFor, _proxies) || _sgBuiltKey != s.BuildKey;
            if (!rebuild && Time.realtimeSinceStartup >= _sgNextCheck)
            {
                _sgNextCheck = Time.realtimeSinceStartup + 2f;
                foreach (var w in _sgWindows)
                    if (w.Renderer == null) { rebuild = true; Logger.LogInfo("Stained-glass projection: a window renderer was destroyed, finding the windows again"); break; }
            }
            if (!rebuild) return;
            _sgBuiltFor = _proxies;
            _sgBuiltKey = s.BuildKey;
            _sgNextCheck = Time.realtimeSinceStartup + 2f;
            SgBuild();
        }

        private void SgClear()
        {
            foreach (var w in _sgWindows) if (w.BakeMesh != null) Destroy(w.BakeMesh);
            _sgWindows.Clear();
            if (_sgArray != null) { _sgArray.Release(); Destroy(_sgArray); _sgArray = null; }
        }

        // ---------------------------------------------------------------- per frame

        /// <summary>The live windows whose beams pass nearest the camera (or a portal's view), packed for the shader.</summary>
        private int SgPack()
        {
            var s = _sgSettings;
            if (_sgWindows.Count == 0 || _sgArray == null) return 0;
            int cap = Mathf.Clamp(s.maxWindows, 1, 64);
            Vector3 cam = _camera.transform.position;
            _sgOrder.Clear();
            var decided = new StringBuilder();
            foreach (var w in _sgWindows)
            {
                if (!w.Live || w.Dead) continue;
                if (!w.Decided) { SgDecide(w, decided); if (w.Dead) continue; }
                float reach = Mathf.Max(1f, s.reach);
                float d = SgSegmentDistance(cam, w.Centre, w.Centre + w.Dir * reach);
                foreach (var p in _portalViews) d = Mathf.Min(d, SgSegmentDistance(p, w.Centre, w.Centre + w.Dir * reach));
                d -= Mathf.Max(w.HalfW, w.HalfH);
                _sgOrder.Add((d, w));
            }
            if (decided.Length > 0 && s.log) Logger.LogInfo("Stained-glass windows live:\n" + decided);
            if (_sgOrder.Count == 0) return 0;
            _sgOrder.Sort((a, b) => a.Item1.CompareTo(b.Item1));
            int n = Mathf.Min(cap, _sgOrder.Count);
            if (_sgData.Length < n * SgStride) _sgData = new Vector4[cap * SgStride];
            for (int i = 0; i < n; i++)
            {
                var w = _sgOrder[i].Item2;
                int o = i * SgStride;
                _sgData[o] = new Vector4(w.Centre.x, w.Centre.y, w.Centre.z, w.Slice);
                _sgData[o + 1] = new Vector4(w.U.x, w.U.y, w.U.z, 0.5f / w.HalfW);
                _sgData[o + 2] = new Vector4(w.V.x, w.V.y, w.V.z, 0.5f / w.HalfH);
                _sgData[o + 3] = new Vector4(w.Dir.x, w.Dir.y, w.Dir.z, Mathf.Max(1f, s.reach));
                _sgData[o + 4] = new Vector4(w.Normal.x, w.Normal.y, w.Normal.z, 2f * Mathf.Max(w.HalfW, w.HalfH) / Mathf.Max(1, _sgArray.width));
            }
            if (_sgBuffer == null || _sgBuffer.count < cap * SgStride)
            {
                _sgBuffer?.Release();
                _sgBuffer = new ComputeBuffer(64 * SgStride, 16) { name = "UltraRT Stained Glass" };
            }
            _cmd.SetBufferData(_sgBuffer, _sgData, 0, 0, n * SgStride);
            if (s.debug > 0 && Time.realtimeSinceStartup >= _sgNextProbe) { _sgNextProbe = Time.realtimeSinceStartup + 2f; SgProbe(n); }
            if (Time.realtimeSinceStartup >= _sgNextLog)
            {
                _sgNextLog = Time.realtimeSinceStartup + 30f;
                Logger.LogInfo($"Stained-glass projection: {n} of {_sgOrder.Count} live windows bound ({_sgWindows.Count} found), nearest " +
                               string.Join(", ", _sgOrder.Take(Mathf.Min(n, 4)).Select(x => $"'{x.Item2.Path}' {x.Item1:F1} m")));
            }
            return n;
        }

        private float _sgNextProbe;

        /// <summary>Debug (sg.debug &gt; 0): the shader's test, on the CPU, at the level surface under the crosshair.</summary>
        private void SgProbe(int n)
        {
            var cam = _camera.transform;
            if (!Physics.Raycast(cam.position, cam.forward, out var hit, 200f, ~0, QueryTriggerInteraction.Ignore)) { Logger.LogInfo("SG probe: nothing under the crosshair"); return; }
            var sb = new StringBuilder($"SG probe: hit {hit.point:F2} normal {hit.normal:F2} '{hit.collider.name}', {n} bound:");
            for (int i = 0; i < n; i++)
            {
                Vector4 a = _sgData[i * 5], bu = _sgData[i * 5 + 1], bv = _sgData[i * 5 + 2], d = _sgData[i * 5 + 3], e = _sgData[i * 5 + 4];
                Vector3 L = -(Vector3)d, en = e;
                float ndl = Vector3.Dot(hit.normal, L), denom = Vector3.Dot(L, en);
                float t = Vector3.Dot((Vector3)a - hit.point, en) / denom;
                var x = hit.point + L * t - (Vector3)a;
                float u = Vector3.Dot(x, bu) * bu.w + 0.5f, v = Vector3.Dot(x, bv) * bv.w + 0.5f;
                sb.Append($"\n  [{i}] slice {a.w} centre {(Vector3)a:F1} dir {(Vector3)d:F2} in {en:F2}: ndl {ndl:F2} denom {denom:F2} t {t:F1} uv ({u:F2}, {v:F2})");
            }
            Logger.LogInfo(sb.ToString());
        }

        private static float SgSegmentDistance(Vector3 p, Vector3 a, Vector3 b)
        {
            var ab = b - a;
            float t = Mathf.Clamp01(Vector3.Dot(p - a, ab) / Mathf.Max(1e-6f, ab.sqrMagnitude));
            return Vector3.Distance(p, a + ab * t);
        }

        // ---------------------------------------------------------------- scene build

        private void SgBuild()
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            SgClear();
            _sgColourful.Clear();
            var s = _sgSettings;
            var rx = SgGlobs(s.materials);
            if (rx == null) return;
            var meshCache = new Dictionary<Mesh, SgMeshData>();
            int renderers = 0, failed = 0;
            var log = new StringBuilder();
            foreach (var r in UnityEngine.Object.FindObjectsOfType<MeshRenderer>(true))
            {
                if (r == null || !r.gameObject.scene.IsValid()) continue;
                var mf = r.GetComponent<MeshFilter>();
                var mesh = mf != null ? mf.sharedMesh : null;
                if (mesh == null) continue;
                var cur = r.sharedMaterials;
                bool atlas = cur.Length > 0 && cur[0] != null && cur[0].shader != null && cur[0].shader.name.Contains("Stationary");
                Material[] mats = cur;
                int[] counts = null;
                if (atlas)
                {
                    if (!OriginalMaterials.TryGet(r, out var orig, out counts, out _) || orig == null) continue;
                    mats = orig;
                }
                if (r.name.EndsWith("[UltraRT]") || cur.Any(m => m != null && m.shader != null && m.shader.name.StartsWith("UltraRT/"))) continue; // our own proxies
                bool any = false;
                for (int k = 0; k < mats.Length; k++) if (SgWindowMaterial(mats[k], rx)) { any = true; break; }
                if (!any) continue;
                renderers++;
                try { SgFromRenderer(r, mesh, mats, counts, atlas, rx, meshCache, log); }
                catch (Exception e) { failed++; Logger.LogWarning($"Stained-glass projection: '{Bench.GameControl.PathOf(r.transform)}' failed: {e.Message}"); }
            }
            // Slices: one per distinct window, the biggest first, at most SgMaxSlices (windows beyond are dropped).
            _sgWindows.Sort((a, b) => (b.HalfW * b.HalfH).CompareTo(a.HalfW * a.HalfH));
            var slices = new Dictionary<string, int>();
            var keep = new List<SgWindow>();
            foreach (var w in _sgWindows)
            {
                if (!slices.TryGetValue(w.Key, out int sl))
                {
                    if (slices.Count >= SgMaxSlices) { if (w.BakeMesh != null) Destroy(w.BakeMesh); continue; }
                    sl = slices.Count;
                    slices[w.Key] = sl;
                    w.Baker = true;
                }
                else if (w.BakeMesh != null) { Destroy(w.BakeMesh); w.BakeMesh = null; }
                w.Slice = sl;
                keep.Add(w);
            }
            int dropped = _sgWindows.Count - keep.Count;
            _sgWindows.Clear();
            _sgWindows.AddRange(keep);
            if (slices.Count > 0)
            {
                Logger.LogInfo($"Stained-glass projection: baking {slices.Count} slices{(s.bake ? "" : " (sg.bake=0: left empty)")}");
                SgBake(slices.Count);
                Logger.LogInfo("Stained-glass projection: baked");
            }
            Logger.LogInfo($"Stained-glass projection ({s}): {_sgWindows.Count} windows ({slices.Count} distinct, {dropped} over the cap) from {renderers} renderers ({failed} failed) in {sw.Elapsed.TotalMilliseconds:F0} ms" +
                           (_sgArray != null ? $", array {_sgArray.width}x{_sgArray.height}x{_sgArray.volumeDepth}" : ""));
            if (s.log && log.Length > 0) Logger.LogInfo("Stained-glass windows:\n" + log);
        }

        /// <summary>A window pane: an opaque material whose name matches. Transparent stained glass (1-4's StainedGlass1A
        /// railings and arches, 8-3's panels) stands free in rooms, with room on both sides: it keeps its shadow-ray tint only.</summary>
        private static bool SgWindowMaterial(Material m, Regex rx) =>
            m != null && m.renderQueue < 2450 && rx.IsMatch(MaterialClassifier.CleanName(m));

        private static Regex SgGlobs(string globs)
        {
            var parts = (globs ?? "").Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries).Select(g => g.Trim()).Where(g => g.Length > 0).ToArray();
            if (parts.Length == 0) return null;
            return new Regex("^(" + string.Join("|", parts.Select(g => Regex.Escape(g).Replace("\\*", ".*"))) + ")$", RegexOptions.IgnoreCase);
        }

        /// <summary>Raw vertex streams and indices of a mesh (GPU readback: most level meshes are not readable).</summary>
        private sealed class SgMeshData
        {
            public byte[][] Streams;
            public int[] Strides;
            public VertexAttributeDescriptor[] Attrs;
            public uint[] Idx;
            public int VertexCount, PS, PO;
            public VertexAttributeFormat PF;

            public int US = -1, UO;
            public VertexAttributeFormat UF;

            public Vector2 Uv(int v)
            {
                if (US < 0) return Vector2.zero;
                var b = Streams[US];
                int o = v * Strides[US] + UO;
                if (UF == VertexAttributeFormat.Float16) return new Vector2(Mathf.HalfToFloat(BitConverter.ToUInt16(b, o)), Mathf.HalfToFloat(BitConverter.ToUInt16(b, o + 2)));
                if (UF == VertexAttributeFormat.Float32) return new Vector2(BitConverter.ToSingle(b, o), BitConverter.ToSingle(b, o + 4));
                return Vector2.zero;
            }

            public Vector3 Position(int v)
            {
                var b = Streams[PS];
                int o = v * Strides[PS] + PO;
                if (PF == VertexAttributeFormat.Float16)
                    return new Vector3(Mathf.HalfToFloat(BitConverter.ToUInt16(b, o)), Mathf.HalfToFloat(BitConverter.ToUInt16(b, o + 2)), Mathf.HalfToFloat(BitConverter.ToUInt16(b, o + 4)));
                return new Vector3(BitConverter.ToSingle(b, o), BitConverter.ToSingle(b, o + 4), BitConverter.ToSingle(b, o + 8));
            }
        }

        private static SgMeshData SgRead(Mesh mesh)
        {
            if (!mesh.HasVertexAttribute(VertexAttribute.Position)) return null;
            var d = new SgMeshData
            {
                VertexCount = mesh.vertexCount,
                Attrs = mesh.GetVertexAttributes(),
                PS = mesh.GetVertexAttributeStream(VertexAttribute.Position),
                PO = mesh.GetVertexAttributeOffset(VertexAttribute.Position),
                PF = mesh.GetVertexAttributeFormat(VertexAttribute.Position),
            };
            if (d.PF != VertexAttributeFormat.Float32 && d.PF != VertexAttributeFormat.Float16) return null;
            if (mesh.HasVertexAttribute(VertexAttribute.TexCoord0))
            {
                d.US = mesh.GetVertexAttributeStream(VertexAttribute.TexCoord0);
                d.UO = mesh.GetVertexAttributeOffset(VertexAttribute.TexCoord0);
                d.UF = mesh.GetVertexAttributeFormat(VertexAttribute.TexCoord0);
            }
            int ns = mesh.vertexBufferCount;
            d.Streams = new byte[ns][];
            d.Strides = new int[ns];
            for (int s = 0; s < ns; s++)
            {
                int stride = mesh.GetVertexBufferStride(s);
                d.Strides[s] = stride;
                if (stride <= 0) continue;
                using (var vb = mesh.GetVertexBuffer(s))
                {
                    var words = new uint[(d.VertexCount * stride + 3) / 4];
                    vb.GetData(words, 0, 0, words.Length);
                    var b = new byte[d.VertexCount * stride];
                    Buffer.BlockCopy(words, 0, b, 0, b.Length);
                    d.Streams[s] = b;
                }
            }
            using (var ib = mesh.GetIndexBuffer())
            {
                if (mesh.indexFormat == IndexFormat.UInt32) { d.Idx = new uint[ib.count]; ib.GetData(d.Idx); }
                else
                {
                    var packed = new uint[(ib.count + 1) / 2];
                    ib.GetData(packed, 0, 0, packed.Length); // 16-bit buffer read as 32-bit words (as StaticGroups does)
                    d.Idx = new uint[ib.count];
                    for (int k = 0; k < ib.count; k++) d.Idx[k] = (packed[k >> 1] >> ((k & 1) * 16)) & 0xFFFF;
                }
            }
            return d;
        }

        /// <summary>The windows of one renderer: the triangles of every slot whose material matches, grouped into flat pieces.</summary>
        private void SgFromRenderer(MeshRenderer r, Mesh mesh, Material[] mats, int[] counts, bool atlas, Regex rx,
                                    Dictionary<Mesh, SgMeshData> cache, StringBuilder log)
        {
            if (!cache.TryGetValue(mesh, out var md)) { md = SgRead(mesh); cache[mesh] = md; }
            if (md == null) return;
            string path = Bench.GameControl.PathOf(r.transform);
            int sub0 = r.isPartOfStaticBatch ? r.subMeshStartIndex : 0;
            Matrix4x4 toWorld;
            if (r.isPartOfStaticBatch)
            {
                var root = SgStaticBatchRoot?.GetValue(r) as Transform;
                toWorld = root != null ? root.localToWorldMatrix : Matrix4x4.identity;
            }
            else toWorld = r.localToWorldMatrix;
            for (int k = 0; k < mats.Length; k++)
            {
                var m = mats[k];
                if (!SgWindowMaterial(m, rx)) continue;
                int sub;
                int from = 0, to = -1; // index range within the submesh (atlas: the original submesh's share)
                string how;
                if (atlas)
                {
                    sub = sub0;
                    if (sub >= mesh.subMeshCount) continue;
                    int total = (int)mesh.GetIndexCount(sub);
                    if (counts != null && counts.Length == mats.Length && counts.Sum() == total)
                    {
                        for (int j = 0; j < k; j++) from += counts[j];
                        to = from + counts[k];
                        how = $"atlas slot {k}/{mats.Length}";
                    }
                    else if (mats.Length == 1) { to = total; how = "atlas"; }
                    else
                    {
                        var dom = OriginalMaterials.Dominant(mats, counts, x => x != null);
                        if (dom != m) continue;
                        to = total;
                        how = $"atlas (dominant of {mats.Length}, index counts unknown)";
                    }
                }
                else
                {
                    sub = sub0 + k;
                    if (k >= mesh.subMeshCount || sub >= mesh.subMeshCount) continue;
                    to = (int)mesh.GetIndexCount(sub);
                    how = r.isPartOfStaticBatch ? "static batch" : "mesh";
                }
                if (mesh.GetTopology(sub) != MeshTopology.Triangles) continue;
                var desc = mesh.GetSubMesh(sub);
                var tris = new List<int>();
                for (int i = from; i + 2 < to; i += 3)
                {
                    int a = desc.indexStart + i;
                    if (a + 2 >= md.Idx.Length) break;
                    for (int j = 0; j < 3; j++) tris.Add((int)md.Idx[a + j] + desc.baseVertex);
                }
                // Material for the bake: the atlas (with its per-vertex rects) or the slot's own texture.
                var texSrc = atlas ? r.sharedMaterials[0] : m;
                var tex = texSrc.HasProperty("_MainTex") ? texSrc.GetTexture("_MainTex") : null;
                if (tex != null && tex.dimension != TextureDimension.Tex2D) tex = null;
                var st = !atlas && texSrc.HasProperty("_MainTex") ? new Vector4(texSrc.GetTextureScale("_MainTex").x, texSrc.GetTextureScale("_MainTex").y,
                                                                               texSrc.GetTextureOffset("_MainTex").x, texSrc.GetTextureOffset("_MainTex").y) : new Vector4(1, 1, 0, 0);
                var col = texSrc.HasProperty("_Color") ? texSrc.GetColor("_Color") : Color.white;
                // A plain texture (no stained-glass colour: 6-2's and P-2's GothicWindow is a grey 'smoothgrid') is
                // dropped here, before it takes one of the capped slices; atlas slots are judged after the bake.
                if (!atlas && tex != null && !SgColourful(tex, col)) { log.AppendLine($"  '{path}' {MaterialClassifier.CleanName(m)}: texture '{tex.name}' has no stained-glass colour, skipped"); continue; }
                SgCluster(r, path, MaterialClassifier.CleanName(m), how, md, tris, toWorld, mesh, sub, tex, st, col, atlas, log);
            }
        }

        /// <summary>Groups a slot's triangles into flat connected pieces; each big enough is a window.</summary>
        private void SgCluster(MeshRenderer r, string path, string matName, string how, SgMeshData md, List<int> idx, Matrix4x4 toWorld,
                               Mesh mesh, int sub, Texture tex, Vector4 st, Color col, bool atlas, StringBuilder log)
        {
            int nt = idx.Count / 3;
            if (nt == 0) return;
            if (nt > 6000) { log.AppendLine($"  '{path}' {matName}: {nt} triangles, too many: skipped"); return; }
            bool mirrored = toWorld.determinant < 0f;
            var p = new Vector3[nt * 3];
            var nrm = new Vector3[nt];
            var area = new float[nt];
            var lo = new Vector3[nt];
            var hi = new Vector3[nt];
            for (int t = 0; t < nt; t++)
            {
                for (int j = 0; j < 3; j++)
                {
                    int v = idx[t * 3 + j];
                    p[t * 3 + j] = (uint)v < (uint)md.VertexCount ? toWorld.MultiplyPoint3x4(md.Position(v)) : Vector3.zero;
                }
                var c = Vector3.Cross(p[t * 3 + 1] - p[t * 3], p[t * 3 + 2] - p[t * 3]);
                if (mirrored) c = -c;
                float m = c.magnitude;
                area[t] = 0.5f * m;
                nrm[t] = m > 1e-9f ? c / m : Vector3.zero;
                lo[t] = Vector3.Min(p[t * 3], Vector3.Min(p[t * 3 + 1], p[t * 3 + 2]));
                hi[t] = Vector3.Max(p[t * 3], Vector3.Max(p[t * 3 + 1], p[t * 3 + 2]));
            }
            // Union-find: coplanar (either side) and within 0.3 m of each other.
            var parent = Enumerable.Range(0, nt).ToArray();
            int Find(int x) { while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; } return x; }
            for (int a = 0; a < nt; a++)
            {
                if (area[a] < 1e-6f) continue;
                for (int b = a + 1; b < nt; b++)
                {
                    if (area[b] < 1e-6f) continue;
                    if (Mathf.Abs(Vector3.Dot(nrm[a], nrm[b])) < 0.97f) continue;
                    if (Mathf.Abs(Vector3.Dot(p[b * 3] - p[a * 3], nrm[a])) > 0.05f) continue;
                    var gap = Vector3.Max(lo[a] - hi[b], lo[b] - hi[a]);
                    if (gap.x > 0.3f || gap.y > 0.3f || gap.z > 0.3f) continue;
                    int ra = Find(a), rb = Find(b);
                    if (ra != rb) parent[ra] = rb;
                }
            }
            var groups = new Dictionary<int, List<int>>();
            for (int t = 0; t < nt; t++)
            {
                if (area[t] < 1e-6f) continue;
                int g = Find(t);
                if (!groups.TryGetValue(g, out var l)) groups[g] = l = new List<int>();
                l.Add(t);
            }
            foreach (var g in groups.Values) SgWindowFrom(r, path, matName, how, md, idx, p, nrm, area, g, toWorld, mesh, sub, tex, st, col, atlas, log);
        }

        private void SgWindowFrom(MeshRenderer r, string path, string matName, string how, SgMeshData md, List<int> idx, Vector3[] p, Vector3[] nrm,
                                  float[] area, List<int> g, Matrix4x4 toWorld, Mesh mesh, int sub, Texture tex, Vector4 st, Color col, bool atlas, StringBuilder log)
        {
            var s = _sgSettings;
            // The plane: the biggest triangle's normal; front = where most of the area faces.
            int big = g[0];
            foreach (int t in g) if (area[t] > area[big]) big = t;
            Vector3 n0 = nrm[big];
            float total = 0f, signed = 0f;
            Vector3 centroid = Vector3.zero;
            foreach (int t in g)
            {
                total += area[t];
                signed += area[t] * Mathf.Sign(Vector3.Dot(nrm[t], n0));
                centroid += (p[t * 3] + p[t * 3 + 1] + p[t * 3 + 2]) * (area[t] / 3f);
            }
            centroid /= Mathf.Max(total, 1e-6f);
            float facing = signed / Mathf.Max(total, 1e-6f); // 1: one-sided towards n0, ~0: two-sided
            if (total < 0.3f) return;
            // Frame: U horizontal (or world x for flat windows), V = N x U.
            Vector3 U = Mathf.Abs(n0.y) < 0.9f ? Vector3.Cross(Vector3.up, n0).normalized : Vector3.Cross(n0, Vector3.forward).normalized;
            Vector3 V = Vector3.Cross(n0, U).normalized;
            float minU = float.MaxValue, maxU = float.MinValue, minV = float.MaxValue, maxV = float.MinValue;
            foreach (int t in g)
                for (int j = 0; j < 3; j++)
                {
                    var d = p[t * 3 + j] - centroid;
                    float u = Vector3.Dot(d, U), v = Vector3.Dot(d, V);
                    minU = Mathf.Min(minU, u); maxU = Mathf.Max(maxU, u);
                    minV = Mathf.Min(minV, v); maxV = Mathf.Max(maxV, v);
                }
            float halfW = 0.5f * (maxU - minU), halfH = 0.5f * (maxV - minV);
            Vector3 centre = centroid + U * (0.5f * (minU + maxU)) + V * (0.5f * (minV + maxV));
            Vector2 uvLo = new Vector2(float.MaxValue, float.MaxValue), uvHi = new Vector2(float.MinValue, float.MinValue);
            foreach (int t in g)
                for (int j = 0; j < 3; j++)
                {
                    int vi = idx[t * 3 + j];
                    if ((uint)vi >= (uint)md.VertexCount) continue;
                    var uv = md.Uv(vi);
                    uvLo = Vector2.Min(uvLo, uv); uvHi = Vector2.Max(uvHi, uv);
                }
            string tag = $"  '{path}' {matName} ({how}): {g.Count} tris, {2 * halfW:F1} x {2 * halfH:F1} m at {centre:F1}, normal {n0:F2}, facing {facing:F2}, uv0 {uvLo:F2}-{uvHi:F2} st {st:F2}";
            // Over 1.2 m both ways: the 0.5-1 m edges of a glass slab (1-4's V2 window is a 10 x 20 x 0.5 m box, its wall
            // panes 1 m thick) are not windows.
            if (halfW < 0.6f || halfH < 0.6f || halfW > 30f || halfH > 30f) { log.AppendLine(tag + ": size out of range, skipped"); return; }

            // The room side is decided when the window is first live (SgDecide): at the RT scene build most rooms (and
            // their colliders) are not active yet, so the physics tests saw nothing (2026-11-20, 1-4's arena).
            var w = new SgWindow
            {
                Renderer = r, Path = path, Material = matName, How = how, Atlas = atlas,
                Tex = tex, TexSt = st, Color = col, ToWorld = toWorld,
                Centre = centre, U = U, V = V, N0 = n0, Normal = n0, Dir = -n0, Facing = facing, HalfW = halfW, HalfH = halfH, Tris = g.Count,
                SourceMesh = mesh, SourceSub = sub, Tag = tag,
            };
            try { w.BakeMesh = SgCompactMesh(md, idx, g); }
            catch (Exception e) { w.BakeMesh = null; log.AppendLine($"    (compact mesh failed: {e.Message}; baking the whole submesh)"); }
            // Identical windows (the inactive copies checkpoints keep of a room) share one slice.
            w.Key = $"{Mathf.Round(centre.x * 20f)},{Mathf.Round(centre.y * 20f)},{Mathf.Round(centre.z * 20f)}|{n0:F2}|{halfW:F2}x{halfH:F2}|{(tex != null ? tex.GetInstanceID() : 0)}|{atlas}";
            _sgWindows.Add(w);
            log.AppendLine(tag + (tex == null ? ", no texture (white)" : $", texture '{tex.name}'"));
        }

        /// <summary>Which side of a window is the room, when it is first live: not the side with a wall right behind the
        /// pane (physics), and the side where the light has something to land on (rays fanned around the beam hit the
        /// level within its reach; outside a level's shell they escape); a tie goes to the side the faces show. A one-sided
        /// face whose front looks away from the room (the outer face of a glass slab) and a pane facing up are dropped.</summary>
        private void SgDecide(SgWindow w, StringBuilder log)
        {
            var s = _sgSettings;
            w.Decided = true;
            float tilt = Mathf.Clamp(s.tilt, 0f, 89f) * Mathf.Deg2Rad;
            float reach = Mathf.Max(1f, s.reach);
            var side = new float[2];
            var dirs = new Vector3[2];
            var notes = new string[2];
            for (int k = 0; k < 2; k++)
            {
                Vector3 nin = k == 0 ? w.N0 : -w.N0;
                dirs[k] = SgBeamDir(nin, tilt);
                bool wall = SgBlocked(w.Renderer, w.Centre, nin, 1.0f);
                float enclosed = SgEnclosure(w.Renderer, w.Centre + nin * 0.1f, nin, dirs[k], reach);
                float score = (wall ? -10f : 0f) + enclosed * 4f + (k == 0 ? w.Facing : -w.Facing) * 0.5f;
                if (nin.y > 0.6f) score -= 20f; // faces up: a floor window lights the sky
                side[k] = score;
                notes[k] = $"{(k == 0 ? "+" : "-")}: wall {(wall ? "yes" : "no")}, enclosed {enclosed:F2}, score {score:F1}";
            }
            int pick = side[0] >= side[1] ? 0 : 1;
            string why = null;
            if (side[pick] < -5f) why = "no room side";
            else if (Mathf.Abs(w.Facing) > 0.6f && (pick == 0) != (w.Facing > 0f)) why = "back face";
            if (why != null) { w.Dead = true; log.AppendLine($"{w.Tag}: {why} ({notes[0]}; {notes[1]}), dropped"); return; }
            w.Normal = pick == 0 ? w.N0 : -w.N0;
            w.Dir = dirs[pick];
            log.AppendLine($"{w.Tag} -> slice {w.Slice}: inward {w.Normal:F2}, beam {w.Dir:F2} ({notes[0]}; {notes[1]})");
        }

        /// <summary>The beam of a window with inward normal nin: tilted down to `tilt` below the horizon, or the normal
        /// itself where that already points further down (skylights).</summary>
        private static Vector3 SgBeamDir(Vector3 nin, float tilt)
        {
            var h = new Vector3(nin.x, 0f, nin.z);
            if (-nin.y >= Mathf.Sin(tilt) || h.sqrMagnitude < 1e-4f) return nin.normalized;
            h.Normalize();
            return (h * Mathf.Cos(tilt) + Vector3.down * Mathf.Sin(tilt)).normalized;
        }

        private static readonly RaycastHit[] SgHits = new RaycastHit[16];

        /// <summary>A collider (not the window's own) within `dist` on the `dir` side of the plane: looked for from the
        /// plane outwards and from `dist` out back towards the plane (a ray that starts inside a wall does not see it).</summary>
        private static bool SgBlocked(MeshRenderer r, Vector3 c, Vector3 dir, float dist)
        {
            return SgFirstHit(r, c + dir * 0.02f, dir, dist) < dist || SgFirstHit(r, c + dir * dist, -dir, dist - 0.03f) < dist;
        }

        /// <summary>The share of 6 rays fanned from the pane into one side (the beam, the normal, the beam swung 35 deg
        /// left and right, and two steeper) that hit the level within `reach`: about 1 in a room, 0 into the void.</summary>
        private static float SgEnclosure(MeshRenderer r, Vector3 o, Vector3 nin, Vector3 beam, float reach)
        {
            var dirs = new[]
            {
                beam, nin,
                Quaternion.AngleAxis(35f, Vector3.up) * beam, Quaternion.AngleAxis(-35f, Vector3.up) * beam,
                (beam + Vector3.down * 0.6f).normalized, (nin * 0.5f + beam).normalized,
            };
            int hits = 0;
            foreach (var d in dirs) if (SgFirstHit(r, o, d, reach) < reach) hits++;
            return hits / (float)dirs.Length;
        }

        private static float SgFirstHit(MeshRenderer r, Vector3 o, Vector3 dir, float dist)
        {
            int n = Physics.RaycastNonAlloc(o, dir, SgHits, dist, ~0, QueryTriggerInteraction.Ignore);
            float best = float.MaxValue;
            for (int i = 0; i < n; i++)
            {
                var col = SgHits[i].collider;
                if (col == null) continue;
                var t = col.transform;
                if (t == r.transform || t.IsChildOf(r.transform)) continue; // the window's own collider (a parent is the wall: it counts)
                if (col.GetComponentInParent<NewMovement>() != null) continue; // the player
                best = Mathf.Min(best, SgHits[i].distance);
            }
            return best;
        }

        /// <summary>A mesh of just the group's triangles: the source's vertex layout, only the vertices they use.</summary>
        private static Mesh SgCompactMesh(SgMeshData md, List<int> idx, List<int> group)
        {
            var map = new Dictionary<int, int>();
            var order = new List<int>();
            var ni = new int[group.Count * 3];
            for (int i = 0; i < group.Count; i++)
                for (int j = 0; j < 3; j++)
                {
                    int v = idx[group[i] * 3 + j];
                    if ((uint)v >= (uint)md.VertexCount) v = 0;
                    if (!map.TryGetValue(v, out int k)) { k = order.Count; map[v] = k; order.Add(v); }
                    ni[i * 3 + j] = k;
                }
            var m = new Mesh { name = "UltraRT stained glass window" };
            m.SetVertexBufferParams(order.Count, md.Attrs);
            const MeshUpdateFlags F = MeshUpdateFlags.DontValidateIndices | MeshUpdateFlags.DontRecalculateBounds | MeshUpdateFlags.DontNotifyMeshUsers;
            for (int s = 0; s < md.Streams.Length; s++)
            {
                if (md.Streams[s] == null) continue;
                int stride = md.Strides[s];
                if (m.GetVertexBufferStride(s) != stride) throw new InvalidOperationException($"stream {s} stride {m.GetVertexBufferStride(s)} != {stride}");
                var b = new byte[order.Count * stride];
                for (int i = 0; i < order.Count; i++) Buffer.BlockCopy(md.Streams[s], order[i] * stride, b, i * stride, stride);
                m.SetVertexBufferData(b, 0, 0, b.Length, s, F);
            }
            m.SetIndexBufferParams(ni.Length, IndexFormat.UInt32);
            m.SetIndexBufferData(ni, 0, 0, ni.Length, F);
            m.subMeshCount = 1;
            m.SetSubMesh(0, new SubMeshDescriptor(0, ni.Length), F);
            m.bounds = new Bounds(Vector3.zero, Vector3.one * 1e6f);
            return m;
        }

        /// <summary>Draws every window into its slice of the texture array (one command buffer, executed now).</summary>
        private void SgBake(int sliceCount)
        {
            var s = _sgSettings;
            int res = Mathf.Clamp(Mathf.ClosestPowerOfTwo(Mathf.Max(16, s.resolution)), 16, 1024);
            var desc = new RenderTextureDescriptor(res, res, RenderTextureFormat.ARGB32, 0)
            {
                dimension = TextureDimension.Tex2DArray,
                volumeDepth = sliceCount,
                // No mips: GenerateMips on a 2D array render texture crashes D3D12 here ("could not create a compute
                // pipeline state object", 2026-11-20). The projection is point sampled at full resolution.
                useMipMap = false,
                autoGenerateMips = false,
                sRGB = false,
                msaaSamples = 1,
            };
            _sgArray = new RenderTexture(desc) { name = "UltraRT Stained Glass Windows", filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
            _sgArray.Create();
            using (var cmd = new CommandBuffer { name = "UltraRT stained glass bake" })
            {
                for (int i = 0; i < _sgWindows.Count; i++)
                {
                    var w = _sgWindows[i];
                    if (!w.Baker) continue;
                    if (!_sgSettings.bake) continue;
                    cmd.SetRenderTarget(new RenderTargetIdentifier(_sgArray, 0, CubemapFace.Unknown, w.Slice));
                    cmd.ClearRenderTarget(false, true, Color.clear);
                    cmd.SetViewProjectionMatrices(Matrix4x4.identity, Matrix4x4.identity);
                    var mpb = new MaterialPropertyBlock();
                    mpb.SetTexture(IdSgTex, w.Tex != null ? w.Tex : Texture2D.whiteTexture);
                    mpb.SetVector(IdSgTexSt, w.TexSt);
                    mpb.SetColor(IdSgColor, w.Color);
                    var corner = w.Centre - w.U * w.HalfW - w.V * w.HalfH;
                    mpb.SetVector(IdSgOrigin, new Vector4(corner.x, corner.y, corner.z, w.Atlas ? 1f : 0f));
                    mpb.SetVector(IdSgU, w.U * (0.5f / w.HalfW));
                    mpb.SetVector(IdSgV, w.V * (0.5f / w.HalfH));
                    mpb.SetVector(IdSgN, new Vector4(w.Normal.x, w.Normal.y, w.Normal.z, 0.08f));
                    if (w.BakeMesh != null) cmd.DrawMesh(w.BakeMesh, w.ToWorld, _sgBakeMat, 0, 0, mpb);
                    else cmd.DrawMesh(w.SourceMesh, w.ToWorld, _sgBakeMat, w.SourceSub, 0, mpb);
                }
                Graphics.ExecuteCommandBuffer(cmd);
            }
            if (_sgSettings.dump) SgDump(sliceCount);
            SgRejectPlain();
        }

        private readonly Dictionary<(Texture, Color), bool> _sgColourful = new Dictionary<(Texture, Color), bool>();

        /// <summary>The texture x colour has stained-glass colour (SgRejectPlain's test on a 32 x 32 copy).</summary>
        private bool SgColourful(Texture tex, Color col)
        {
            var s = _sgSettings;
            if (s.minChroma <= 0f) return true;
            if (_sgColourful.TryGetValue((tex, col), out bool hit)) return hit;
            bool ok = true;
            var tmp = RenderTexture.GetTemporary(32, 32, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
            var t2 = new Texture2D(32, 32, TextureFormat.RGBA32, false, true);
            var prev = RenderTexture.active;
            try
            {
                Graphics.Blit(tex, tmp);
                RenderTexture.active = tmp;
                t2.ReadPixels(new Rect(0, 0, 32, 32), 0, 0, false);
                double chroma = 0, lum = 0, lum2 = 0;
                var px = t2.GetPixels32();
                foreach (var c0 in px)
                {
                    float r = c0.r / 255f * col.r, g = c0.g / 255f * col.g, b = c0.b / 255f * col.b;
                    chroma += Mathf.Max(r, Mathf.Max(g, b)) - Mathf.Min(r, Mathf.Min(g, b));
                    float l = 0.2126f * r + 0.7152f * g + 0.0722f * b;
                    lum += l; lum2 += l * l;
                }
                float mc = (float)(chroma / px.Length), ml = (float)(lum / px.Length);
                float sd = Mathf.Sqrt(Mathf.Max(0f, (float)(lum2 / px.Length) - ml * ml));
                ok = mc >= s.minChroma || sd >= 0.08f;
            }
            catch (Exception) { ok = true; }
            finally
            {
                RenderTexture.active = prev;
                RenderTexture.ReleaseTemporary(tmp);
                Destroy(t2);
            }
            _sgColourful[(tex, col)] = ok;
            return ok;
        }

        /// <summary>Drops windows whose baked pane has no stained-glass colour: nearly grey and nearly flat (6-2's
        /// GothicWindow wall faces bake as plain grey stone, mean 44/255, 2026-11-20). They would only add a grey wash.
        /// Mean chroma (max - min of the gamma colour) over covered texels below sg.minChroma and luminance spread below
        /// 0.08: rejected.</summary>
        private void SgRejectPlain()
        {
            var s = _sgSettings;
            if (_sgArray == null || s.minChroma <= 0f) return;
            int res = _sgArray.width, step = Mathf.Max(1, res / 64);
            var tmp = RenderTexture.GetTemporary(res, res, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
            var tex = new Texture2D(res, res, TextureFormat.RGBA32, false, true);
            var prev = RenderTexture.active;
            var verdict = new Dictionary<int, bool>();
            var sb = new StringBuilder();
            try
            {
                foreach (var w in _sgWindows)
                {
                    if (!w.Baker || verdict.ContainsKey(w.Slice)) continue;
                    Graphics.CopyTexture(_sgArray, w.Slice, 0, tmp, 0, 0);
                    RenderTexture.active = tmp;
                    tex.ReadPixels(new Rect(0, 0, res, res), 0, 0, false);
                    var px = tex.GetPixels32();
                    double chroma = 0, lum = 0, lum2 = 0;
                    int n = 0;
                    for (int y = 0; y < res; y += step)
                        for (int x = 0; x < res; x += step)
                        {
                            var c = px[y * res + x];
                            if (c.a == 0) continue;
                            float r = c.r / 255f, g = c.g / 255f, b = c.b / 255f;
                            chroma += Mathf.Max(r, Mathf.Max(g, b)) - Mathf.Min(r, Mathf.Min(g, b));
                            float l = 0.2126f * r + 0.7152f * g + 0.0722f * b;
                            lum += l; lum2 += l * l;
                            n++;
                        }
                    float mc = n > 0 ? (float)(chroma / n) : 0f;
                    float sd = n > 0 ? Mathf.Sqrt(Mathf.Max(0f, (float)(lum2 / n - (lum / n) * (lum / n)))) : 0f;
                    bool keep = n > 0 && (mc >= s.minChroma || sd >= 0.08f);
                    verdict[w.Slice] = keep;
                    sb.Append($" [{w.Slice} chroma {mc:F2} spread {sd:F2}{(keep ? "" : " DROP")}]");
                }
            }
            finally
            {
                RenderTexture.active = prev;
                RenderTexture.ReleaseTemporary(tmp);
                Destroy(tex);
            }
            int dropped = 0;
            foreach (var w in _sgWindows)
                if (verdict.TryGetValue(w.Slice, out bool k) && !k && !w.Dead) { w.Dead = true; dropped++; }
            Logger.LogInfo($"Stained-glass projection: {dropped} windows without stained-glass colour dropped; slices:{sb}");
        }

        private void SgDump(int sliceCount)
        {
            try
            {
                var dir = System.IO.Path.Combine(BepInEx.Paths.BepInExRootPath, "ultrart", "stained-glass");
                System.IO.Directory.CreateDirectory(dir);
                string scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
                int res = _sgArray.width;
                var tmp = RenderTexture.GetTemporary(res, res, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
                var tex = new Texture2D(res, res, TextureFormat.RGBA32, false, true);
                var prev = RenderTexture.active;
                foreach (var w in _sgWindows)
                {
                    if (!w.Baker) continue;
                    Graphics.CopyTexture(_sgArray, w.Slice, 0, tmp, 0, 0);
                    RenderTexture.active = tmp;
                    tex.ReadPixels(new Rect(0, 0, res, res), 0, 0, false);
                    tex.Apply(false);
                    System.IO.File.WriteAllBytes(System.IO.Path.Combine(dir, $"{scene}_{w.Slice:D3}.png"), tex.EncodeToPNG());
                }
                RenderTexture.active = prev;
                RenderTexture.ReleaseTemporary(tmp);
                Destroy(tex);
                Logger.LogInfo($"Stained-glass projection: {sliceCount} slices written to {dir}");
            }
            catch (Exception e) { Logger.LogWarning($"Stained-glass dump failed: {e.Message}"); }
        }
    }
}
