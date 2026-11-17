using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx.Logging;
using Newtonsoft.Json;
using UnityEngine;

namespace UltraRT
{
    /// <summary>Settings for per-texel material detail (docs/materials.md).</summary>
    public sealed class MaterialMapSettings
    {
        /// <summary>-ultrart-materials on|off (config [Materials] Enabled), Ctrl+F1 in game (shader-side switch, instant).</summary>
        public bool enabled = true;
        /// <summary>-ultrart-materials-source ai|proc: the cache folder under BepInEx/ultrart/materials (tools/materials).</summary>
        public string source = "ai";
        /// <summary>-ultrart-materials-smooth F ([Materials] NormalSmooth), 0-1: how far each texel's normal is pulled to
        /// the 3x3 average of its neighbourhood when a map is loaded (wrapped). PBRify tilts every texel on its own,
        /// which shows as fragments inside bricks under any denoiser (docs/materials.md, "Matrix"). Read at scene build.</summary>
        public float smooth = 0f;
        /// <summary>-ultrart-materials-normal F: global multiplier on the per-class normal strength (relight, pt-dn, pt).</summary>
        public float normalStrength = 1f;
        /// <summary>-ultrart-materials-hybrid-normal F: the same in hybrid, whose shadow ratio scales the game's lit colour
        /// (half-resolution, ambient included), so it takes less before texel rows look dirty.</summary>
        public float hybridNormalStrength = 1f;
        /// <summary>-ultrart-materials-roughness F: global multiplier on the per-class roughness variation.</summary>
        public float roughness = 1f;
        /// <summary>-ultrart-materials-guided on|off: hybrid composite picks the trace texel by albedo as well as depth
        /// (Composite.shader), so per-texel shading follows texel edges instead of the half-resolution trace grid.</summary>
        public bool guidedPick = true;
        /// <summary>-ultrart-materials-lod on|off ([Materials] TextureLod): ray-cone texture LOD for the detail maps
        /// (docs/materials.md, "Texture LOD"). Primary hits pick the detail mip whose texels match the traced pixel's
        /// footprint, so normal detail smaller than a pixel is averaged instead of aliasing (RR at 1/3 scale: noise).
        /// Off = mip 0 everywhere, as before. Shader-side, instant.</summary>
        public bool lod = true;
        /// <summary>-ultrart-materials-lod-bias F ([Materials] LodBias): mips added to the cone's LOD. 0 = texels the size of
        /// a traced pixel; -1.58 (log2 1/3) = texels the size of a screen pixel at RR's 1/3 scale (sharper, more aliasing).</summary>
        public float lodBias = 0f;
        /// <summary>Footprint stretch at grazing angles: lod += -aniso * log2(cos). 1 = the long axis (isotropic hardware
        /// filtering, blurs floors seen at grazing angles), 0.5 = the footprint's area (geometric mean of both axes).</summary>
        public float lodAniso = 0.5f;
        /// <summary>-ultrart-materials-toksvig F: how much of the slope variance that filtering removed becomes roughness
        /// (glossy classes; alpha^2 += F x variance, LEAN/Toksvig). 0 = off.</summary>
        public float toksvig = 1f;
        /// <summary>-ultrart-materials-albedo-lod on|off: secondary hits (reflection and bounce rays) sample the game's albedo
        /// and emission at the ray cone's mip where the texture has mips. Primary albedo stays mip 0 (point, the game's look).
        /// Off by default: almost none of the game's textures have mips (8-2: 12 of 370, 1-1: 1 of 121), so it changes next
        /// to nothing and would only cost the cone math at every secondary hit.</summary>
        public bool albedoLod = false;
        /// <summary>Spread angle (radians) a diffuse bounce adds to the ray cone for the albedo LOD at the next hit: one sample
        /// stands for a wide lobe, so its hit's texture is averaged over a matching area.</summary>
        public float lodDiffuse = 0.3f;

        public override string ToString() => $"{(enabled ? "on" : "off")}, source {source}, normal x{normalStrength:0.##} (hybrid x{hybridNormalStrength:0.##}), roughness x{roughness:0.##}, guided pick {(guidedPick ? "on" : "off")}, " +
                                             $"lod {(lod ? "on" : "off")} (bias {lodBias:0.##}, aniso {lodAniso:0.##}, toksvig {toksvig:0.##}, albedo {(albedoLod ? "on" : "off")})";
    }

    /// <summary>
    /// Per-texel normal and roughness maps for the game's textures (docs/materials.md). The maps are generated offline by
    /// tools/materials (PBRify Remix or the procedural generator) into BepInEx/ultrart/materials/&lt;source&gt;/, named after
    /// the texture: "&lt;name&gt;@&lt;w&gt;x&lt;h&gt;_d.png", plus manifest.json.
    ///
    /// Each is one RGBA32 "detail" texture (rg normal xy, b roughness minus the texture's mean, a 1), point
    /// sampled at the albedo's uv by the proxy hit shader (Proxy.shader, _DetailTex). Baked StaticSceneOptimizer geometry
    /// samples a scene atlas, so its detail texture is a matching atlas composed at load: each atlas rect (TEXCOORD1 of
    /// the baked mesh) is filled from the detail texture of the original material drawn there (OriginalMaterials), with
    /// that material's class strengths baked in, since all atlas geometry shares one proxy material.
    /// </summary>
    internal sealed class MaterialMaps
    {
        public static MaterialMapSettings Settings = new MaterialMapSettings();
        public static readonly int DetailTexId = Shader.PropertyToID("_DetailTex");
        public static readonly int DetailParamsId = Shader.PropertyToID("_DetailParams");
        public static readonly int GlobalId = Shader.PropertyToID("_UltraRTDetail");
        public static readonly int LodId = Shader.PropertyToID("_UltraRTTexLod");
        public static readonly int Lod2Id = Shader.PropertyToID("_UltraRTTexLod2");

        private sealed class Entry
        {
            public string Key, Name;
            public int W, H;
            public float RMean;
            public Texture2D Tex;     // detail texture, null until loaded
            public Color32[] Pixels;  // CPU copy, made for atlas composition
            public bool Failed;
        }

        private readonly ManualLogSource _log;
        private readonly string _dir;
        private readonly Dictionary<string, List<Entry>> _byName = new Dictionary<string, List<Entry>>(StringComparer.Ordinal);
        private readonly List<Texture2D> _owned = new List<Texture2D>();
        public bool Available { get; }
        public int Bound, Missing, Loaded;
        public double LoadMs;
        // The game's own textures at bound proxies: how many have a mip chain (secondary-hit albedo LOD needs one).
        public int MainMipped, MainFlat;
        // Where the load time goes (ms): PNG decode, CPU readback, mip chain build, texture create + upload.
        public static double MsDecode, MsRead, MsMips, MsUpload;

        public MaterialMaps(ManualLogSource log, string root)
        {
            _log = log;
            _dir = Path.Combine(root, Settings.source);
            var manifest = Path.Combine(_dir, "manifest.json");
            if (!File.Exists(manifest))
            {
                _log.LogInfo($"Material maps: no cache at {manifest} (run tools/materials): per-texel detail off");
                return;
            }
            try
            {
                var m = JsonConvert.DeserializeObject<Dictionary<string, ManifestEntry>>(File.ReadAllText(manifest));
                foreach (var kv in m)
                {
                    if (kv.Value?.name == null) continue;
                    var e = new Entry { Key = kv.Key, Name = kv.Value.name, W = kv.Value.w, H = kv.Value.h, RMean = kv.Value.rmean };
                    if (!_byName.TryGetValue(e.Name, out var list)) _byName[e.Name] = list = new List<Entry>(1);
                    list.Add(e);
                }
                Available = _byName.Count > 0;
                _log.LogInfo($"Material maps: {m.Count} textures in {_dir}");
            }
            catch (Exception e) { _log.LogWarning($"Material maps: {manifest} not loaded: {e.Message}"); }
        }

#pragma warning disable CS0649 // Json.NET
        private sealed class ManifestEntry { public string name; public int w, h; public float rmean; }
#pragma warning restore CS0649

        // ---------------------------------------------------------------- per-class strengths

        /// <summary>Normal strength per class: the offline maps' tilt is scaled by this (and the global setting).</summary>
        public static float ClassNormal(SurfaceClass c)
        {
            switch (c)
            {
                case SurfaceClass.Glass: case SurfaceClass.Hologram: case SurfaceClass.Mirror: return 0f;
                case SurfaceClass.Wet: return 0.3f;
                case SurfaceClass.Snow: case SurfaceClass.Carpet: return 0.5f;
                case SurfaceClass.Marble: return 0.4f; // PBRify carves marble veins into grooves; polished marble is flat
                case SurfaceClass.Flesh: case SurfaceClass.MetalHigh: return 0.6f;
                case SurfaceClass.MetalThin: case SurfaceClass.MetalLow: return 0.7f;
                case SurfaceClass.MetalStumpy: case SurfaceClass.MetalGrill: case SurfaceClass.Wood: case SurfaceClass.WoodThin:
                case SurfaceClass.Sand: case SurfaceClass.Grass: return 0.8f;
                default: return 1f; // Generic, Gravel, Rubble, Dirt, Crunchy, Other: stone, brick, concrete
            }
        }

        /// <summary>Roughness variation per class (glossy classes only; the shader adds amount * (texel - texture mean)).</summary>
        public static float ClassRoughness(SurfaceClass c)
        {
            switch (c)
            {
                case SurfaceClass.Glass: case SurfaceClass.Mirror: return 0f;
                case SurfaceClass.Wet: return 0.3f;
                case SurfaceClass.MetalHigh: case SurfaceClass.MetalLow: case SurfaceClass.MetalThin:
                case SurfaceClass.MetalStumpy: case SurfaceClass.MetalGrill: return 0.8f;
                default: return 0.6f;
            }
        }

        /// <summary>Strengths of a classified material: overrides (ultrart-materials.json "detail"/"detailRoughness") or the class table.</summary>
        public static (float normal, float rough) Strengths(MaterialParams p)
        {
            if (p.Source == "unlit" || p.Source == "sky wall") return (0f, 0f);
            float n = p.DetailNormal ?? ClassNormal(p.Class);
            float r = p.DetailRoughness ?? ClassRoughness(p.Class);
            return (n, r);
        }

        // ---------------------------------------------------------------- textures

        private Entry Find(Texture tex)
        {
            if (!Available || tex == null || !_byName.TryGetValue(tex.name, out var list)) return null;
            // Same name and size first; otherwise the only one with that name (the detail texture is sampled by uv).
            var e = list.FirstOrDefault(x => x.W == tex.width && x.H == tex.height) ?? (list.Count == 1 ? list[0] : null);
            if (e == null || e.Failed) return null;
            if (e.Tex == null) Load(e);
            return e.Tex != null ? e : null;
        }

        /// <summary>Pulls each texel's normal xy (r, g) towards the 1-2-1 average of its 3x3 neighbourhood, wrapped like
        /// the texture tiles (tools/materials/variants.py's "lp" set, done at load).</summary>
        private static void SmoothNormals(Color32[] px, int w, int h, float amount)
        {
            if (w < 3 || h < 3) return;
            var x = new float[px.Length];
            var y = new float[px.Length];
            for (int i = 0; i < px.Length; i++) { x[i] = px[i].r; y[i] = px[i].g; }
            var tx = new float[px.Length];
            var ty = new float[px.Length];
            for (int r = 0; r < h; r++)
            {
                int row = r * w;
                for (int c = 0; c < w; c++)
                {
                    int l = row + (c == 0 ? w - 1 : c - 1), m = row + c, rr = row + (c == w - 1 ? 0 : c + 1);
                    tx[m] = 0.25f * x[l] + 0.5f * x[m] + 0.25f * x[rr];
                    ty[m] = 0.25f * y[l] + 0.5f * y[m] + 0.25f * y[rr];
                }
            }
            for (int r = 0; r < h; r++)
            {
                int up = (r == 0 ? h - 1 : r - 1) * w, mid = r * w, dn = (r == h - 1 ? 0 : r + 1) * w;
                for (int c = 0; c < w; c++)
                {
                    float sx = 0.25f * tx[up + c] + 0.5f * tx[mid + c] + 0.25f * tx[dn + c];
                    float sy = 0.25f * ty[up + c] + 0.5f * ty[mid + c] + 0.25f * ty[dn + c];
                    var p = px[mid + c];
                    p.r = (byte)Mathf.Clamp(Mathf.RoundToInt(Mathf.Lerp(x[mid + c], sx, amount)), 0, 255);
                    p.g = (byte)Mathf.Clamp(Mathf.RoundToInt(Mathf.Lerp(y[mid + c], sy, amount)), 0, 255);
                    px[mid + c] = p;
                }
            }
        }

        /// <summary>"&lt;key&gt;_d.png" (generate.py write_detail: rg normal, b roughness - mean + 0.5, a 1), decoded straight
        /// into the detail texture (linear, point, repeat). It stays readable for atlas composition.</summary>
        private void Load(Entry e)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            Texture2D t = null;
            try
            {
                t = new Texture2D(2, 2, TextureFormat.RGBA32, false, true)
                {
                    name = e.Key + " [UltraRT detail]", hideFlags = HideFlags.HideAndDontSave,
                };
                long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                if (!t.LoadImage(File.ReadAllBytes(Path.Combine(_dir, e.Key + "_d.png")), false)) throw new Exception("bad image");
                long t1 = System.Diagnostics.Stopwatch.GetTimestamp();
                MsDecode += (t1 - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                // Decoded once on the CPU: the mip chain (with slope variance) is built from it, and atlas composition
                // reads it later. The GPU texture is a fresh one with mips.
                e.Pixels = t.GetPixels32();
                if (Settings.smooth > 0f) SmoothNormals(e.Pixels, t.width, t.height, Mathf.Clamp01(Settings.smooth));
                MsRead += (System.Diagnostics.Stopwatch.GetTimestamp() - t1) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                int w = t.width, h = t.height;
                UnityEngine.Object.Destroy(t);
                t = null;
                e.Tex = MakeDetailTexture(e.Pixels, w, h, e.Key + " [UltraRT detail]");
                _owned.Add(e.Tex);
                Loaded++;
            }
            catch (Exception ex)
            {
                e.Failed = true;
                if (t != null) UnityEngine.Object.Destroy(t);
                if (Missing < 10) _log.LogWarning($"Material maps: {e.Key}: {ex.Message}");
            }
            finally { LoadMs += sw.Elapsed.TotalMilliseconds; }
        }

        private static Color32[] PixelsOf(Entry e) => e.Pixels;

        /// <summary>
        /// A detail texture with its mip chain (docs/materials.md, "Texture LOD"). Mips are box filtered over the valid
        /// texels (a &gt; 127): rg the mean slope, b the mean roughness offset. a carries what the average loses: the standard
        /// deviation sd of the texel slopes (rg * 2 - 1, at strength 1) under the mip texel, as a = 255 - 85 sd (sd &lt;= 1.5),
        /// so a valid texel is always &gt;= 127 and 0 stays "no detail". Level 0 has sd = 0 (a = 255). The hit shader turns
        /// sd^2 x strength^2 into GGX roughness (alpha^2 += variance), so detail lost to filtering becomes gloss loss, not
        /// flicker. Each level is built from the one above with the second moment carried along, so its variance is over
        /// all level-0 texels beneath it (to 8-bit precision per level).
        /// </summary>
        internal static Texture2D MakeDetailTexture(Color32[] px, int w, int h, string name)
        {
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            var t = new Texture2D(w, h, TextureFormat.RGBA32, true, true)
            {
                name = name, filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Repeat, hideFlags = HideFlags.HideAndDontSave,
            };
            int levels = t.mipmapCount;
            // The whole chain in one buffer (RGBA bytes, level 0 first), uploaded with one LoadRawTextureData.
            int total = 0;
            for (int l = 0, lw = w, lh = h; l < levels; l++, lw = Math.Max(1, lw / 2), lh = Math.Max(1, lh / 2)) total += lw * lh;
            var buf = new byte[total * 4];
            for (int i = 0, o = 0; i < px.Length; i++, o += 4) { var c = px[i]; buf[o] = c.r; buf[o + 1] = c.g; buf[o + 2] = c.b; buf[o + 3] = c.a; }
            // Each level from the one above: children's means average into the mean, and E[x^2 + y^2] = sd^2 + |mean|^2 of
            // each child carries the second moment down the chain (8-bit quantised per level).
            int src = 0, sw = w, sh = h;
            for (int level = 1; level < levels; level++)
            {
                int nw = Math.Max(1, sw / 2), nh = Math.Max(1, sh / 2);
                int dst = src + sw * sh * 4;
                int s0 = src, w0 = sw, h0 = sh;
                Action<int> row = y =>
                {
                    // Odd sizes: the last parent row/column also takes the leftover child row/column.
                    int y0 = Math.Min(2 * y, h0 - 1), y1 = y == nh - 1 ? h0 : Math.Min(2 * y + 2, h0);
                    for (int x = 0; x < nw; x++)
                    {
                        int x0 = Math.Min(2 * x, w0 - 1), x1 = x == nw - 1 ? w0 : Math.Min(2 * x + 2, w0);
                        float ax = 0, ay = 0, a2 = 0, ab = 0;
                        int an = 0;
                        for (int cy = y0; cy < y1; cy++)
                            for (int cx = x0; cx < x1; cx++)
                            {
                                int ci = s0 + (cy * w0 + cx) * 4;
                                byte ca = buf[ci + 3];
                                if (ca <= 127) continue;
                                float cxs = SlopeLut[buf[ci]], cys = SlopeLut[buf[ci + 1]];
                                ax += cxs; ay += cys; a2 += cxs * cxs + cys * cys + VarLut[ca]; ab += buf[ci + 2]; an++;
                            }
                        int o = dst + (y * nw + x) * 4;
                        if (an == 0) { buf[o] = 128; buf[o + 1] = 128; buf[o + 2] = 128; buf[o + 3] = 0; continue; }
                        float inv = 1f / an, mx = ax * inv, my = ay * inv;
                        float sd = (float)Math.Sqrt(Math.Max(0f, a2 * inv - mx * mx - my * my));
                        buf[o] = (byte)Mathf.Clamp((int)((mx + 1f) * 127.5f + 0.5f), 0, 255);
                        buf[o + 1] = (byte)Mathf.Clamp((int)((my + 1f) * 127.5f + 0.5f), 0, 255);
                        buf[o + 2] = (byte)Mathf.Clamp((int)(ab * inv + 0.5f), 0, 255);
                        buf[o + 3] = (byte)Mathf.Clamp((int)(255f - 85f * Math.Min(sd, 1.5f) + 0.5f), 127, 255);
                    }
                };
                // Atlases (2048^2 in 8-2) in parallel rows; level textures are small enough for one thread.
                if (nw * nh >= 65536) System.Threading.Tasks.Parallel.For(0, nh, row);
                else for (int y = 0; y < nh; y++) row(y);
                src = dst; sw = nw; sh = nh;
            }
            long t1 = System.Diagnostics.Stopwatch.GetTimestamp();
            t.LoadRawTextureData(buf);
            t.Apply(false, true); // GPU only from here on (Entry.Pixels keeps level 0 for atlas composition)
            long t2 = System.Diagnostics.Stopwatch.GetTimestamp();
            MsMips += (t1 - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            MsUpload += (t2 - t1) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            return t;
        }

        // Detail texel decoding for the mip build: rg byte -> slope (rg * 2 - 1); a byte -> sd^2 (a = 255 - 85 sd).
        private static readonly float[] SlopeLut = Enumerable.Range(0, 256).Select(i => i / 127.5f - 1f).ToArray();
        private static readonly float[] VarLut = Enumerable.Range(0, 256).Select(i => { float sd = (255 - i) / 85f; return sd * sd; }).ToArray();

        /// <summary>Binds the detail texture of <paramref name="src"/>'s main texture to a (non-atlas) proxy material.</summary>
        public void Bind(Material proxy, Material src, MaterialParams p)
        {
            proxy.SetVector(DetailParamsId, Vector4.zero);
            if (src == null || !src.HasProperty("_MainTex")) return;
            var main = src.GetTexture("_MainTex");
            if (main != null) { if (main.mipmapCount > 1) MainMipped++; else MainFlat++; }
            var (sn, sr) = Strengths(p);
            if (sn <= 0f && sr <= 0f) return;
            var e = Find(main);
            if (e == null) { Missing++; return; }
            proxy.SetTexture(DetailTexId, e.Tex);
            proxy.SetVector(DetailParamsId, new Vector4(sn, sr, 0f, 1f));
            Bound++;
        }

        // ---------------------------------------------------------------- atlases

        /// <summary>One triangle range of a baked slot and the original material drawn there.</summary>
        public struct AtlasSpan
        {
            public Material Original;
            public MaterialParams Params;
            public int IndexStart, IndexCount; // into the combined mesh's index list (add BaseVertex)
            public int BaseVertex;
        }

        private sealed class AtlasBuild
        {
            public Texture Atlas;
            public readonly Dictionary<(int, int, int, int), (Entry e, float sn, float sr, int votes)> Cells = new Dictionary<(int, int, int, int), (Entry, float, float, int)>();
            public int Conflicts, Unmapped;
            public readonly List<Material> Materials = new List<Material>();
        }

        private readonly Dictionary<Texture, AtlasBuild> _atlases = new Dictionary<Texture, AtlasBuild>();

        /// <summary>
        /// Records the atlas cells of one baked slot: every triangle's TEXCOORD1 rect (read from the CPU copy of the
        /// combined mesh) maps to the original material of the span it lies in.
        /// </summary>
        public void AddAtlasSlot(Texture atlas, VertexAttributeReader uv1, uint[] indices, List<AtlasSpan> spans)
        {
            if (!Available || atlas == null || uv1 == null) return;
            if (!_atlases.TryGetValue(atlas, out var b)) _atlases[atlas] = b = new AtlasBuild { Atlas = atlas };
            int w = atlas.width, h = atlas.height;
            foreach (var s in spans)
            {
                var (sn, sr) = Strengths(s.Params);
                Entry e = s.Original != null && s.Original.HasProperty("_MainTex") ? Find(s.Original.GetTexture("_MainTex")) : null;
                var seen = new HashSet<(int, int, int, int)>();
                for (int k = 0; k + 2 < s.IndexCount; k += 3)
                {
                    int v = (int)indices[s.IndexStart + k] + s.BaseVertex;
                    var rc = uv1.Read4(v);
                    var cell = (Mathf.RoundToInt(rc.x * w), Mathf.RoundToInt(rc.y * h), Mathf.RoundToInt(rc.z * w), Mathf.RoundToInt(rc.w * h));
                    if (cell.Item3 <= cell.Item1 || cell.Item4 <= cell.Item2 || !seen.Add(cell)) continue;
                    if (e == null) { if (!b.Cells.ContainsKey(cell)) b.Unmapped++; continue; }
                    if (b.Cells.TryGetValue(cell, out var cur))
                    {
                        // The first source wins (captured originals are processed first); count disagreements.
                        if (cur.e == e) b.Cells[cell] = (e, cur.sn, cur.sr, cur.votes + 1);
                        else b.Conflicts++;
                    }
                    else b.Cells[cell] = (e, sn, sr, 1);
                }
            }
        }

        /// <summary>Atlas proxies (and their per-class variants) to receive the composed atlas once it is built.</summary>
        public void AddAtlasMaterial(Texture atlas, Material proxy)
        {
            if (!Available || atlas == null || proxy == null) return;
            if (!_atlases.TryGetValue(atlas, out var b)) _atlases[atlas] = b = new AtlasBuild { Atlas = atlas };
            if (!b.Materials.Contains(proxy)) b.Materials.Add(proxy);
        }

        /// <summary>Composes every recorded atlas and binds it. Cells without a known source keep a = 0 (no detail).</summary>
        public void BuildAtlases()
        {
            foreach (var b in _atlases.Values)
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                int w = b.Atlas.width, h = b.Atlas.height, filled = 0;
                var px = new Color32[w * h];
                foreach (var kv in b.Cells)
                {
                    var (x0, y0, x1, y1) = kv.Key;
                    var (e, sn, sr, _) = kv.Value;
                    if (e?.Tex == null) continue;
                    var src = PixelsOf(e);
                    x0 = Mathf.Clamp(x0, 0, w); x1 = Mathf.Clamp(x1, 0, w); y0 = Mathf.Clamp(y0, 0, h); y1 = Mathf.Clamp(y1, 0, h);
                    int cw = x1 - x0, ch = y1 - y0;
                    int sw2 = e.Tex.width, sh = e.Tex.height;
                    for (int y = 0; y < ch; y++)
                    {
                        int sy = Mathf.Min(sh - 1, (int)((y + 0.5f) * sh / ch)); // rows bottom-up in both (Unity pixel order)
                        for (int x = 0; x < cw; x++)
                        {
                            int sx = Mathf.Min(sw2 - 1, (int)((x + 0.5f) * sw2 / cw));
                            var s = src[sy * sw2 + sx];
                            // Class strengths baked in: the atlas proxy is shared by every class (_DetailParams = 1).
                            px[(y0 + y) * w + x0 + x] = new Color32(
                                (byte)Mathf.Clamp(Mathf.RoundToInt(127.5f + (s.r - 127.5f) * sn), 0, 255),
                                (byte)Mathf.Clamp(Mathf.RoundToInt(127.5f + (s.g - 127.5f) * sn), 0, 255),
                                (byte)Mathf.Clamp(Mathf.RoundToInt(128f + (s.b - 128f) * sr), 0, 255),
                                (byte)(sn > 0f || sr > 0f ? 255 : 0));
                        }
                    }
                    filled++;
                }
                if (filled == 0) continue;
                // Mips of the whole atlas: a cell's mip k is its own only where the cell is aligned to 2^k texels, so the hit
                // shader caps the LOD per cell from its rect (Proxy.shader, AtlasMaxLod). Alignment histogram for the log.
                var t = MakeDetailTexture(px, w, h, b.Atlas.name + " [UltraRT detail atlas]");
                var align = new int[4]; // cells whose rect allows mip >= 1, 2, 3, 5
                foreach (var cell in b.Cells.Keys)
                {
                    int v = cell.Item1 | cell.Item2 | (cell.Item3 - cell.Item1) | (cell.Item4 - cell.Item2);
                    int k = v == 0 ? 31 : TrailingZeros(v);
                    if (k >= 1) align[0]++;
                    if (k >= 2) align[1]++;
                    if (k >= 3) align[2]++;
                    if (k >= 5) align[3]++;
                }
                _owned.Add(t);
                foreach (var m in b.Materials)
                {
                    if (m == null) continue;
                    m.SetTexture(DetailTexId, t);
                    m.SetVector(DetailParamsId, new Vector4(1f, 1f, 1f, 1f));
                }
                _log.LogInfo($"Material maps: atlas '{b.Atlas.name}' {w}x{h} (game atlas mips {b.Atlas.mipmapCount}): {filled}/{b.Cells.Count} cells with detail " +
                             $"({b.Unmapped} rects without a known source, {b.Conflicts} conflicting votes), {b.Materials.Count} materials, " +
                             $"cells aligned for mip 1/2/3/5: {align[0]}/{align[1]}/{align[2]}/{align[3]}, {sw.Elapsed.TotalMilliseconds:F1} ms");
            }
            _atlases.Clear();
        }

        private static int TrailingZeros(int v)
        {
            int k = 0;
            while ((v & 1) == 0 && k < 31) { v >>= 1; k++; }
            return k;
        }

        public void Dispose()
        {
            foreach (var t in _owned) if (t != null) UnityEngine.Object.Destroy(t);
            _owned.Clear();
            foreach (var l in _byName.Values) foreach (var e in l) { e.Tex = null; e.Pixels = null; }
        }
    }

    /// <summary>Reads one float4-ish vertex attribute (Float32 or Float16) from a CPU copy of a vertex stream.</summary>
    internal sealed class VertexAttributeReader
    {
        private readonly byte[] _data;
        private readonly int _stride, _offset, _dim;
        private readonly bool _half;

        public VertexAttributeReader(byte[] data, int stride, int offset, int dim, bool half)
        {
            _data = data; _stride = stride; _offset = offset; _dim = dim; _half = half;
        }

        public Vector4 Read4(int v)
        {
            var r = Vector4.zero;
            int o = v * _stride + _offset;
            for (int i = 0; i < _dim && i < 4; i++)
                r[i] = _half ? Mathf.HalfToFloat(BitConverter.ToUInt16(_data, o + i * 2)) : BitConverter.ToSingle(_data, o + i * 4);
            return r;
        }
    }
}
