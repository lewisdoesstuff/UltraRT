using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace UltraRT
{
    /// <summary>Tunables of the screen-space effects pass (docs/vfx.md), read every frame. Bench variants: vfx.&lt;field&gt;=value.</summary>
    public sealed class VfxSettings
    {
        /// <summary>The whole late pass (bloom, distortion, underwater).</summary>
        public bool enabled = true;

        // ---- bloom
        public bool bloom = true;
        /// <summary>Multiplier on the blurred glow (linear light). Gentle by default.</summary>
        public float bloomStrength = 0.5f;
        /// <summary>HDR source: weight of the energy the tone curve compressed or clipped (lit hotspots).</summary>
        public float bloomExcess = 0.35f;
        /// <summary>HDR source: weight of bright emissive pixels (lava, neon, screens, glowing enemies).</summary>
        public float bloomEmission = 0.35f;
        /// <summary>Emission starts to glow above this brightest-channel value (display units) and fully from the second.</summary>
        public float bloomEmissionLo = 0.55f, bloomEmissionHi = 1.0f;
        /// <summary>Weight of what the game's transparents added after the RT composite (fire, flashes, explosions, beams).</summary>
        public float bloomEffects = 0.6f;
        /// <summary>That part only counts where the final pixel's brightest channel is between these (smoke and dust stay out).</summary>
        public float bloomEffectsLo = 0.6f, bloomEffectsHi = 0.95f;
        /// <summary>Source clamp per channel (linear), against fireflies.</summary>
        public float bloomClamp = 6f;
        /// <summary>The most the glow may add to a pixel (display units, soft). 0 = up to white.</summary>
        public float bloomMaxAdd = 0.3f;
        /// <summary>How much less of the glow lands on pixels that are already bright (0 = none of it is held back): the glow
        /// belongs around a light, not on top of the art that draws it.</summary>
        public float bloomProtect = 0.8f;
        /// <summary>Mip levels of the blur chain (half resolution down).</summary>
        public int bloomMips = 6;

        // ---- distortion
        public bool heatHaze = true;
        /// <summary>Displacement amplitude of the shimmer, as a fraction of the screen height.</summary>
        public float heatAmplitude = HeatAmplitudeDefault;
        public const float HeatAmplitudeDefault = 0.004f, ShockAmplitudeDefault = 0.3f;
        /// <summary>Noise frequency (1/m) and rise speed (m/s).</summary>
        public float heatFrequency = 0.14f, heatRise = 1.2f;
        /// <summary>Height of the hot air column over lava (m) and how far from the camera heat sources are considered (m).</summary>
        public float heatHeight = 8f, heatRange = 80f;
        /// <summary>Hot air over fires: radius multiplier of the flame's bounds, and the maximum radius (m).</summary>
        public float fireRadiusScale = 2.2f, fireRadiusMax = 3f;
        /// <summary>Largest displacement, as a fraction of the screen.</summary>
        public float heatMaxOffset = 0.006f;
        public bool shockwaves = true;
        /// <summary>Shock front: displacement (m) for an explosion of radius 5, thickness as a fraction of the radius reached,
        /// final radius as a multiple of the explosion's, and life (s).</summary>
        public float shockAmplitude = ShockAmplitudeDefault, shockThickness = 0.03f, shockRadius = 1.7f, shockLife = 0.6f;

        // ---- glowing debris
        /// <summary>Explosion debris (the game's ExplosionChunk meshes) glows when it is thrown out and cools down.</summary>
        public bool debrisGlow = true;
        /// <summary>Emission of debris at the moment of the explosion (display units; above 1 it blooms) and the time it takes to cool to a third (s).</summary>
        public float debrisIntensity = 3f, debrisCoolTime = 0.7f;

        // ---- underwater
        public bool underwater = true;
        /// <summary>Screen wobble (fraction of the screen).</summary>
        public float waterWobble = 0.0012f;
        /// <summary>Fog density (1/m) and how fast the fog colour darkens with the camera's depth (1/m).</summary>
        public float waterFog = 0.025f, waterDarken = 0.12f;
        /// <summary>Light shafts from the surface, caustics on surfaces, and the pattern's size (m).</summary>
        public float waterShafts = 0.6f, waterCaustics = 0.5f, waterPatternSize = 9f;
        /// <summary>Shafts are marched this far (m).</summary>
        public float waterShaftDistance = 24f;

        /// <summary>1 = show the bloom alone, 2 = the distortion field.</summary>
        public int debugView = 0;

        public void ParseArgs()
        {
            var a = Environment.GetCommandLineArgs();
            for (int i = 0; i < a.Length; i++)
            {
                string Next() => i + 1 < a.Length ? a[++i] : null;
                float F(float fallback) => float.TryParse(Next(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;
                bool On() { var v = (Next() ?? "").ToLowerInvariant(); return v != "off" && v != "0" && v != "false"; }
                switch (a[i].ToLowerInvariant())
                {
                    case "-ultrart-vfx": enabled = On(); break;
                    case "-ultrart-bloom": bloom = On(); break;
                    case "-ultrart-bloom-strength": bloomStrength = F(bloomStrength); break;
                    case "-ultrart-heathaze": heatHaze = On(); break;
                    case "-ultrart-heathaze-strength": heatAmplitude = HeatAmplitudeDefault * F(1f); break;
                    case "-ultrart-shockwaves": shockwaves = On(); break;
                    case "-ultrart-shockwave-strength": shockAmplitude = ShockAmplitudeDefault * F(1f); break;
                    case "-ultrart-underwater": underwater = On(); break;
                    case "-ultrart-debris-glow": debrisGlow = On(); break;
                }
            }
        }

        public override string ToString() =>
            FormattableString.Invariant($"{(enabled ? "on" : "off")}: bloom {(bloom ? "on" : "off")} x{bloomStrength}, heat haze {(heatHaze ? "on" : "off")}, shockwaves {(shockwaves ? "on" : "off")}, underwater {(underwater ? "on" : "off")}");
    }

    /// <summary>What the late pass needs from the plugin each frame.</summary>
    internal struct VfxFrame
    {
        public Camera camera;
        public Texture target;        // the camera's colour target (PostProcessV2_Handler.mainTex)
        public RenderTexture pre;     // copy of the target right after the RT composite (before the transparents)
        public RenderTexture hdr;     // HDR bloom source written this frame (null: none)
        public Vector2 hdrScale;      // visible / traced size of the HDR source
        public float time;            // seconds, the game's clock (pauses with it)
        public bool underwater;
        public float waterSurface;    // world height of the surface the camera is under
        public Color waterColor;
        public Vector3 waterLightDir; // direction to the light above the surface
    }

    /// <summary>
    /// The screen-space effects pass (docs/vfx.md): bloom, heat haze, shockwave distortion and the underwater look, in one
    /// fullscreen pass after the game's transparents. Owns the bloom chain, the scene copies and the lists of heat sources
    /// and shock fronts. The plugin feeds it (Plugin.Vfx.cs).
    /// </summary>
    internal sealed class Vfx : IDisposable
    {
        public const int MaxBoxes = 8, MaxPoints = 16, MaxShocks = 8;

        public VfxSettings Settings;
        private readonly Material _mat;
        private RenderTexture _scene, _pre, _hdr;
        private readonly List<RenderTexture> _mips = new List<RenderTexture>();
        private Texture2D _noise;

        private readonly Vector4[] _boxMin = new Vector4[MaxBoxes], _boxMax = new Vector4[MaxBoxes];
        private readonly Vector4[] _points = new Vector4[MaxPoints];
        private readonly Vector4[] _shock = new Vector4[MaxShocks], _shockP = new Vector4[MaxShocks];
        private int _boxCount, _pointCount;

        private struct Shock { public Vector3 Pos; public float Start, Size; }
        private readonly List<Shock> _shocks = new List<Shock>();

        public int HeatBoxes => _boxCount;
        public int HeatPoints => _pointCount;
        public int ActiveShocks => _shocks.Count;
        public bool WarpActive { get; private set; }

        private static readonly int
            IdScene = Shader.PropertyToID("_VfxScene"), IdPre = Shader.PropertyToID("_VfxPre"), IdHdr = Shader.PropertyToID("_VfxHdr"),
            IdSrc = Shader.PropertyToID("_VfxSrc"), IdBloom = Shader.PropertyToID("_VfxBloom"), IdNoise = Shader.PropertyToID("_VfxNoise"),
            IdSrcTexel = Shader.PropertyToID("_VfxSrcTexel"), IdBloomParams = Shader.PropertyToID("_VfxBloomParams"),
            IdBloomParams2 = Shader.PropertyToID("_VfxBloomParams2"), IdHdrScale = Shader.PropertyToID("_VfxHdrScale"),
            IdCamPos = Shader.PropertyToID("_VfxCamPos"), IdRayX = Shader.PropertyToID("_VfxRayX"), IdRayY = Shader.PropertyToID("_VfxRayY"),
            IdRayZ = Shader.PropertyToID("_VfxRayZ"), IdViewProj = Shader.PropertyToID("_VfxViewProj"), IdProj = Shader.PropertyToID("_VfxProj"),
            IdCounts = Shader.PropertyToID("_VfxCounts"), IdBoxMin = Shader.PropertyToID("_VfxHeatBoxMin"), IdBoxMax = Shader.PropertyToID("_VfxHeatBoxMax"),
            IdPoints = Shader.PropertyToID("_VfxHeatPoints"), IdHeat = Shader.PropertyToID("_VfxHeat"), IdShock = Shader.PropertyToID("_VfxShock"),
            IdShockP = Shader.PropertyToID("_VfxShockP"), IdWater = Shader.PropertyToID("_VfxWater"), IdWaterColor = Shader.PropertyToID("_VfxWaterColor"),
            IdWaterLight = Shader.PropertyToID("_VfxWaterLight"), IdWaterParams = Shader.PropertyToID("_VfxWaterParams"), IdDebug = Shader.PropertyToID("_VfxDebug");

        public Vfx(Shader shader, VfxSettings settings)
        {
            Settings = settings;
            _mat = new Material(shader) { name = "UltraRT Vfx", hideFlags = HideFlags.HideAndDontSave };
            _noise = MakeNoise(128);
            // Arrays keep the size of their first upload: set them at full length once.
            _mat.SetVectorArray(IdBoxMin, _boxMin);
            _mat.SetVectorArray(IdBoxMax, _boxMax);
            _mat.SetVectorArray(IdPoints, _points);
            _mat.SetVectorArray(IdShock, _shock);
            _mat.SetVectorArray(IdShockP, _shockP);
        }

        /// <summary>Tileable smooth noise, two independent channels (value noise from a wrapped random lattice).</summary>
        private static Texture2D MakeNoise(int n)
        {
            var tex = new Texture2D(n, n, TextureFormat.RGHalf, true, true)
                { name = "UltraRT Vfx Noise", wrapMode = TextureWrapMode.Repeat, filterMode = FilterMode.Bilinear, hideFlags = HideFlags.HideAndDontSave };
            var rnd = new System.Random(12345);
            const int cells = 16;
            var lat = new float[2, cells, cells];
            for (int c = 0; c < 2; c++)
                for (int y = 0; y < cells; y++)
                    for (int x = 0; x < cells; x++) lat[c, x, y] = (float)rnd.NextDouble();
            var px = new Color[n * n];
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    float fx = (float)x / n * cells, fy = (float)y / n * cells;
                    int x0 = (int)fx, y0 = (int)fy, x1 = (x0 + 1) % cells, y1 = (y0 + 1) % cells;
                    float tx = fx - x0, ty = fy - y0;
                    tx = tx * tx * (3f - 2f * tx);
                    ty = ty * ty * (3f - 2f * ty);
                    float r = Mathf.Lerp(Mathf.Lerp(lat[0, x0, y0], lat[0, x1, y0], tx), Mathf.Lerp(lat[0, x0, y1], lat[0, x1, y1], tx), ty);
                    float g = Mathf.Lerp(Mathf.Lerp(lat[1, x0, y0], lat[1, x1, y0], tx), Mathf.Lerp(lat[1, x0, y1], lat[1, x1, y1], tx), ty);
                    px[y * n + x] = new Color(r, g, 0, 1);
                }
            tex.SetPixels(px);
            tex.Apply(true, true); // with mips: the shimmer fades out where its pattern gets finer than a pixel
            return tex;
        }

        // ---------------------------------------------------------------- sources

        public void ClearHeat() { _boxCount = 0; _pointCount = 0; }

        /// <summary>A lava surface: hot air from its top up to <see cref="VfxSettings.heatHeight"/>.</summary>
        public void AddHeatBox(Bounds b, float strength)
        {
            if (_boxCount >= MaxBoxes) return;
            float top = b.max.y;
            _boxMin[_boxCount] = new Vector4(b.min.x, top - 0.1f, b.min.z, strength);
            _boxMax[_boxCount] = new Vector4(b.max.x, top + Mathf.Max(0.5f, Settings.heatHeight), b.max.z, 0f);
            _boxCount++;
        }

        /// <summary>A fire: a ball of hot air above its centre.</summary>
        public void AddHeatPoint(Vector3 centre, float radius)
        {
            if (_pointCount >= MaxPoints) return;
            float r = Mathf.Clamp(radius * Settings.fireRadiusScale, 0.4f, Settings.fireRadiusMax);
            _points[_pointCount++] = new Vector4(centre.x, centre.y + 0.6f * r, centre.z, r);
        }

        /// <summary>An explosion of radius <paramref name="size"/> (m) went off.</summary>
        public void AddShock(Vector3 pos, float size, float time)
        {
            if (!Settings.shockwaves) return;
            if (_shocks.Count >= MaxShocks) _shocks.RemoveAt(0);
            _shocks.Add(new Shock { Pos = pos, Start = time, Size = Mathf.Clamp(size, 1f, 30f) });
        }

        public void Reset()
        {
            _shocks.Clear();
            ClearHeat();
        }

        // ---------------------------------------------------------------- resources

        /// <summary>The HDR bloom source the RT composite writes (trace resolution).</summary>
        public RenderTexture EnsureHdr(int w, int h)
        {
            if (_hdr == null || _hdr.width != w || _hdr.height != h || !_hdr.IsCreated())
            {
                Release(ref _hdr);
                _hdr = new RenderTexture(w, h, 0, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear)
                    { name = "UltraRT Bloom Source", enableRandomWrite = true, filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.HideAndDontSave };
                _hdr.Create();
            }
            return _hdr;
        }

        /// <summary>The copy of the camera target taken right after the RT composite.</summary>
        public RenderTexture EnsurePre(Texture target) { EnsureCopy(ref _pre, target, "UltraRT Vfx PreAlpha"); return _pre; }

        private static void EnsureCopy(ref RenderTexture rt, Texture target, string name)
        {
            var src = target as RenderTexture;
            if (rt != null && rt.width == target.width && rt.height == target.height && (src == null || rt.format == src.format) && rt.IsCreated()) return;
            Release(ref rt);
            var desc = src != null ? src.descriptor : new RenderTextureDescriptor(target.width, target.height, RenderTextureFormat.ARGB32, 0);
            desc.depthBufferBits = 0;
            desc.msaaSamples = 1;
            desc.enableRandomWrite = false;
            desc.useMipMap = false;
            desc.autoGenerateMips = false;
            rt = new RenderTexture(desc) { name = name, filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.HideAndDontSave };
            rt.Create();
        }

        private void EnsureMips(int w, int h, int count)
        {
            count = Mathf.Clamp(count, 2, 8);
            int mw = Mathf.Max(1, w / 2), mh = Mathf.Max(1, h / 2);
            if (_mips.Count == count && _mips[0] != null && _mips[0].width == mw && _mips[0].height == mh && _mips[0].IsCreated()) return;
            foreach (var m in _mips) { var t = m; Release(ref t); }
            _mips.Clear();
            var fmt = SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.RGB111110Float) ? RenderTextureFormat.RGB111110Float : RenderTextureFormat.ARGBHalf;
            for (int i = 0; i < count; i++)
            {
                var rt = new RenderTexture(mw, mh, 0, fmt, RenderTextureReadWrite.Linear)
                    { name = $"UltraRT Bloom {i}", filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.HideAndDontSave };
                rt.Create();
                _mips.Add(rt);
                if (mw <= 2 || mh <= 2) break;
                mw = Mathf.Max(1, mw / 2);
                mh = Mathf.Max(1, mh / 2);
            }
        }

        private static void Release(ref RenderTexture rt)
        {
            if (rt == null) return;
            rt.Release();
            Object.Destroy(rt);
            rt = null;
        }

        // ---------------------------------------------------------------- the pass

        /// <summary>Whether the late pass has anything to draw this frame (decides the copies).</summary>
        public bool Wanted(in VfxFrame f)
        {
            var s = Settings;
            if (!s.enabled) return false;
            if (s.bloom && s.bloomStrength > 0f) return true;
            if (s.underwater && f.underwater) return true;
            if (s.heatHaze && (_boxCount > 0 || _pointCount > 0)) return true;
            return s.shockwaves && _shocks.Count > 0;
        }

        /// <summary>Records the late pass into <paramref name="cmd"/> (the after-transparents buffer).</summary>
        public void Execute(CommandBuffer cmd, in VfxFrame f)
        {
            var s = Settings;
            var cam = f.camera;
            var target = f.target;
            EnsureCopy(ref _scene, target, "UltraRT Vfx Scene");
            bool bloom = s.bloom && s.bloomStrength > 0f && f.pre != null;
            var m = _mat;

            // camera
            var proj = cam.projectionMatrix;
            var t = cam.transform;
            m.SetVector(IdCamPos, new Vector4(t.position.x, t.position.y, t.position.z, f.time));
            m.SetVector(IdRayX, t.right / proj.m00);
            m.SetVector(IdRayY, t.up / proj.m11);
            m.SetVector(IdRayZ, t.forward);
            m.SetMatrix(IdViewProj, proj * cam.worldToCameraMatrix);
            m.SetVector(IdProj, new Vector4(proj.m00, proj.m11, 2f / (proj.m11 * Mathf.Max(1, target.height)), 0)); // z: view-plane size of a pixel at 1 m
            m.SetTexture(IdNoise, _noise);
            m.SetFloat(IdDebug, s.debugView);

            // distortion sources
            int nb = s.heatHaze ? _boxCount : 0, np = s.heatHaze ? _pointCount : 0, ns = 0;
            for (int i = _shocks.Count - 1; i >= 0; i--)
                if (f.time - _shocks[i].Start > s.shockLife || f.time < _shocks[i].Start) _shocks.RemoveAt(i);
            if (s.shockwaves)
            {
                foreach (var sh in _shocks)
                {
                    float a = Mathf.Clamp01((f.time - sh.Start) / Mathf.Max(0.05f, s.shockLife));
                    // The front races out and slows (ease-out); the kick fades as it spreads.
                    float r = sh.Size * s.shockRadius * (1f - Mathf.Pow(1f - a, 1.5f));
                    float amp = s.shockAmplitude * Mathf.Sqrt(sh.Size / 5f) * (1f - a) * (1f - a) * Mathf.Clamp01(a * 12f);
                    _shock[ns] = new Vector4(sh.Pos.x, sh.Pos.y, sh.Pos.z, r);
                    _shockP[ns] = new Vector4(amp, 0.25f + r * s.shockThickness, 0, 0);
                    ns++;
                }
            }
            WarpActive = nb + np + ns > 0;
            m.SetVector(IdCounts, new Vector4(nb, np, ns, WarpActive ? 1f : 0f));
            if (nb > 0) { m.SetVectorArray(IdBoxMin, _boxMin); m.SetVectorArray(IdBoxMax, _boxMax); }
            if (np > 0) m.SetVectorArray(IdPoints, _points);
            if (ns > 0) { m.SetVectorArray(IdShock, _shock); m.SetVectorArray(IdShockP, _shockP); }
            m.SetVector(IdHeat, new Vector4(s.heatAmplitude, s.heatFrequency, s.heatRise, s.heatMaxOffset));

            // underwater
            bool water = s.underwater && f.underwater;
            m.SetVector(IdWater, new Vector4(water ? 1f : 0f, f.waterSurface, s.waterWobble, s.waterFog));
            m.SetVector(IdWaterColor, new Vector4(f.waterColor.r, f.waterColor.g, f.waterColor.b, s.waterDarken));
            var l = f.waterLightDir.sqrMagnitude > 1e-4f ? f.waterLightDir.normalized : Vector3.up;
            m.SetVector(IdWaterLight, new Vector4(l.x, l.y, l.z, s.waterShafts));
            m.SetVector(IdWaterParams, new Vector4(s.waterCaustics, s.waterShaftDistance, 1f / Mathf.Max(0.5f, s.waterPatternSize), 0));

            cmd.CopyTexture(target, _scene);
            cmd.SetGlobalTexture(IdScene, _scene);
            m.SetVector(IdBloomParams, new Vector4(bloom ? Mathf.Max(0f, s.bloomStrength) : 0f, Mathf.Max(0f, s.bloomEffects), s.bloomEffectsLo, Mathf.Max(s.bloomEffectsHi, s.bloomEffectsLo + 1e-3f)));
            m.SetVector(IdBloomParams2, new Vector4(f.hdr != null ? 1f : 0f, Mathf.Max(0.1f, s.bloomClamp), Mathf.Max(0f, s.bloomMaxAdd), Mathf.Clamp01(s.bloomProtect)));
            m.SetVector(IdHdrScale, new Vector4(f.hdrScale.x, f.hdrScale.y, 0, 0));
            if (bloom)
            {
                EnsureMips(target.width, target.height, s.bloomMips);
                cmd.SetGlobalTexture(IdPre, f.pre);
                cmd.SetGlobalTexture(IdHdr, f.hdr != null ? (Texture)f.hdr : Texture2D.blackTexture);
                cmd.SetRenderTarget(_mips[0]);
                cmd.DrawProcedural(Matrix4x4.identity, m, 0, MeshTopology.Triangles, 3);
                for (int i = 1; i < _mips.Count; i++)
                {
                    cmd.SetGlobalTexture(IdSrc, _mips[i - 1]);
                    cmd.SetGlobalVector(IdSrcTexel, new Vector4(1f / _mips[i - 1].width, 1f / _mips[i - 1].height, 0, 0));
                    cmd.SetRenderTarget(_mips[i]);
                    cmd.DrawProcedural(Matrix4x4.identity, m, 1, MeshTopology.Triangles, 3);
                }
                for (int i = _mips.Count - 2; i >= 0; i--)
                {
                    cmd.SetGlobalTexture(IdSrc, _mips[i + 1]);
                    cmd.SetGlobalVector(IdSrcTexel, new Vector4(1f / _mips[i + 1].width, 1f / _mips[i + 1].height, 0, 0));
                    cmd.SetRenderTarget(_mips[i]);
                    cmd.DrawProcedural(Matrix4x4.identity, m, 2, MeshTopology.Triangles, 3);
                }
                cmd.SetGlobalTexture(IdBloom, _mips[0]);
            }
            else cmd.SetGlobalTexture(IdBloom, Texture2D.blackTexture);
            cmd.SetRenderTarget(new RenderTargetIdentifier(target));
            cmd.DrawProcedural(Matrix4x4.identity, m, 3, MeshTopology.Triangles, 3);
        }

        public void Dispose()
        {
            Release(ref _scene);
            Release(ref _pre);
            Release(ref _hdr);
            foreach (var mip in _mips) { var t = mip; Release(ref t); }
            _mips.Clear();
            if (_noise != null) { Object.Destroy(_noise); _noise = null; }
            if (_mat != null) Object.Destroy(_mat);
        }
    }
}
