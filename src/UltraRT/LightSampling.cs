using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace UltraRT
{
    public enum LightSamplingMode
    {
        /// <summary>Brute force: every light, one shadow ray each (the original DirectAll). Uniform pick at the bounce.</summary>
        Reference = 0,
        /// <summary>RIS over the light list, one shadow ray. No history. (2 was ReSTIR, removed on 2026-11-19 with the hybrid
        /// mode, its last user; docs/light-sampling.md.)</summary>
        Ris = 1,
    }

    /// <summary>Tunables, read every <see cref="LightSampling.Bind"/>. Safe to change at any time.</summary>
    public sealed class LightSamplingSettings
    {
        /// <summary>RIS since 2026-11-15: unbiased, and quieter under relight's denoiser at rest than ReSTIR was (its samples
        /// are independent from frame to frame), the same in motion (docs/light-sampling.md, "Bias and noise").</summary>
        public LightSamplingMode mode = LightSamplingMode.Ris;
        /// <summary>RIS candidates in the shaders bound with <see cref="LightSampling.BindTables"/> (the radiance cache update)
        /// and the viewmodel's default. Costs one light evaluation each, no rays. (ReSTIR's per-frame M before 2026-11-19.)</summary>
        public int candidates = 8;
        /// <summary>RIS candidates per pixel at the primary hit (no history, so every pixel draws this many).</summary>
        public int risCandidates = 16;
        /// <summary>RIS candidates at the bounce hit.</summary>
        public int bounceCandidates = 4;
        /// <summary>Source pdf from light power and distance to the camera; false = uniform over shadowed lights.</summary>
        public bool importance = true;
        /// <summary>Fraction of the source pdf that stays uniform, so no light is ever starved.</summary>
        public float uniformMix = 0.2f;
        /// <summary>Importance also counts the camera's images through visible portals (<see cref="LightSampling.ViewPoints"/>):
        /// surfaces seen through a portal (8-2's fake mirrors, 8-3's doorways) are often far from the camera in world space,
        /// and their lights were sampled almost only through the uniform mix (docs/light-sampling.md, "Portals").</summary>
        public bool portalImportance = true;
        /// <summary>World-space light grid (ReGIR-lite, Wave 3): 32x16x32 camera-centered 4 m cells storing top 8 local
        /// shadowed lights per cell, combined via exact 50/50 MIS mixture with global _LightAlias. Bench ls.grid=0.</summary>
        public bool grid = true;
        /// <summary>Primary RIS candidates when the world-space light grid is active (halves RIS loop ALU with equal/lower variance).</summary>
        /// <remarks>A point outside the grid (beyond it, or seen through a portal) gets the full <see cref="risCandidates"/>
        /// from the global table, as without the grid (docs/light-sampling.md, "Light grid and portals").</remarks>
        public int risCandidatesGrid = 8;
    }

    /// <summary>
    /// Light selection for PathTrace.raytrace (UltraRTLights.hlsl): per-frame alias table over the shadowed lights and the
    /// fill-light list. See docs/light-sampling.md. Call <see cref="Bind"/> once per traced frame, after the light buffers
    /// are filled and before DispatchRays.
    /// </summary>
    internal sealed class LightSampling : IDisposable
    {
        public LightSamplingSettings Settings = new LightSamplingSettings();

        /// <summary>Lights in the table this frame that are shadowed (go through RIS) and unshadowed (evaluated directly).</summary>
        public int RisLightCount { get; private set; }
        /// <summary>The mode the last <see cref="Bind"/> used.</summary>
        public LightSamplingMode ActiveMode { get; private set; } = LightSamplingMode.Ris;
        public int FillLightCount { get; private set; }
        internal ComputeBuffer AliasBuffer => _alias;
        internal ComputeBuffer FillBuffer => _fill;
        /// <summary>True when lights were added or removed relative to the previous <see cref="Bind"/> (the radiance cache and
        /// the particle and viewmodel histories react to it).</summary>
        public bool LightSetChanged { get; private set; }
        /// <summary>Extra viewpoints for the importance weights: the camera seen through each portal side in front of it
        /// (SceneProxies.PortalViewpoints). Set by the plugin before <see cref="Bind"/>.</summary>
        public readonly List<Vector3> ViewPoints = new List<Vector3>();
        /// <summary>Diagnostics: frames with a light-set change since the last <see cref="TakeChangeStats"/>, and some names.</summary>
        public int SetChangeFrames;
        private readonly List<string> _changeNames = new List<string>();
        public string TakeChangeStats()
        {
            string r = $"{SetChangeFrames} frames with light-set changes [{string.Join(", ", _changeNames)}]";
            SetChangeFrames = 0; _changeNames.Clear();
            return r;
        }
        /// <summary>
        /// Identity of packed entries that have no Light (EffectLights' virtual lights): VirtualIds[i] is used when
        /// lights[i] is null. Ids must not collide with Light instance ids (use another object's instance id).
        /// </summary>
        public int[] VirtualIds;

        private const int GridX = 32, GridY = 16, GridZ = 32;
        private const float GridCell = 4f;
        private const int GridCells = GridX * GridY * GridZ;
        private readonly int _maxLights;
        private readonly ComputeBuffer _alias, _fill, _gridBuf, _risIdxBuf, _globalPdfBuf;
        private readonly uint[] _aliasData, _fillData, _risIdxData;
        private readonly float[] _prob, _globalPdfData;
        private readonly int[] _risLights, _small, _large, _aliasIdx;
        private readonly float[] _threshold;
        private ComputeShader _gridCs;
        private int _kBuildGrid = -1;
        private Vector4 _gridOrigin;
        private bool _gridBuiltThisFrame;

        private readonly int[] _prevIds;
        private int _prevCount;
        private readonly HashSet<int> _ids = new HashSet<int>(), _prevIdSet = new HashSet<int>();

        private static readonly int
            IdLightSampling = Shader.PropertyToID("_LightSampling"),
            IdRisCandidates = Shader.PropertyToID("_RisCandidates"),
            IdBounceCandidates = Shader.PropertyToID("_BounceCandidates"),
            IdRisLightCount = Shader.PropertyToID("_RisLightCount"),
            IdLightAlias = Shader.PropertyToID("_LightAlias"),
            IdFillCount = Shader.PropertyToID("_FillCount"),
            IdFillLights = Shader.PropertyToID("_FillLights"),
            IdLightGrid = Shader.PropertyToID("_LightGrid"),
            IdLightGlobalPdf = Shader.PropertyToID("_LightGlobalPdf"),
            IdLightGridOn = Shader.PropertyToID("_LightGridOn"),
            IdLightsCs = Shader.PropertyToID("_Lights"),
            IdRisLightIdxCs = Shader.PropertyToID("_RisLightIdx"),
            IdGridOriginCs = Shader.PropertyToID("_GridOrigin"),
            IdGridDimsCs = Shader.PropertyToID("_GridDims"),
            IdRisCandidatesFar = Shader.PropertyToID("_RisCandidatesFar"),
            IdLightGridOrigin = Shader.PropertyToID("_LightGridOrigin");

        public LightSampling(int maxLights)
        {
            if (maxLights > 0xFFFF) throw new ArgumentOutOfRangeException(nameof(maxLights), "light indices are stored in 16 bits");
            _maxLights = maxLights;
            _alias = new ComputeBuffer(maxLights, 16) { name = "UltraRT LightAlias" };
            _fill = new ComputeBuffer(maxLights, 4) { name = "UltraRT FillLights" };
            _gridBuf = new ComputeBuffer(GridCells, 16) { name = "UltraRT LightGrid" };
            _risIdxBuf = new ComputeBuffer(maxLights, 4) { name = "UltraRT RisLightIdx" };
            _globalPdfBuf = new ComputeBuffer(maxLights, 4) { name = "UltraRT LightGlobalPdf" };
            _aliasData = new uint[maxLights * 4];
            _fillData = new uint[maxLights];
            _risIdxData = new uint[maxLights];
            _globalPdfData = new float[maxLights];
            _prob = new float[maxLights];
            _risLights = new int[maxLights];
            _small = new int[maxLights];
            _large = new int[maxLights];
            _aliasIdx = new int[maxLights];
            _threshold = new float[maxLights];
            _prevIds = new int[maxLights];
            // Zero-fill so nothing is ever read uninitialised.
            _alias.SetData(_aliasData);
            _fill.SetData(_fillData);
            _risIdxBuf.SetData(_risIdxData);
            _globalPdfBuf.SetData(_globalPdfData);
            var emptyGrid = new uint[GridCells * 4];
            for (int i = 0; i < emptyGrid.Length; i++) emptyGrid[i] = 0xFFFFFFFFu;
            _gridBuf.SetData(emptyGrid);
        }

        public void SetGridShader(ComputeShader cs)
        {
            _gridCs = cs;
            _kBuildGrid = cs != null && cs.HasKernel("BuildGrid") ? cs.FindKernel("BuildGrid") : -1;
        }

        /// <summary>No sampling history is kept since ReSTIR went (2026-11-19); kept for the callers' reset paths.</summary>
        public void Reset() { }

        /// <summary>
        /// Build this frame's light tables and bind everything UltraRTLights.hlsl reads.
        /// </summary>
        /// <param name="camPos">Camera position, for the importance heuristic.</param>
        /// <param name="lights">The Light behind each packed entry: lights[i] fills lightData[i*4 .. i*4+3].</param>
        /// <param name="lightData">The same array uploaded to _Lights (4 x Vector4 per light).</param>
        /// <param name="lightCount">Number of packed lights (_LightCount).</param>
        public void Bind(CommandBuffer cmd, RayTracingShader shader, Vector3 camPos, IList<Light> lights, Vector4[] lightData, int lightCount, ComputeBuffer lightsBuffer = null)
        {
            var s = Settings;
            lightCount = Mathf.Clamp(lightCount, 0, _maxLights);
            BuildTables(lights, lightData, lightCount, camPos);
            ActiveMode = s.mode;
            _gridBuiltThisFrame = s.grid && s.mode == LightSamplingMode.Ris && _gridCs != null && _kBuildGrid >= 0 && lightsBuffer != null && RisLightCount > 12;
            if (_gridBuiltThisFrame)
            {
                _gridOrigin = GridOriginAt(camPos);
                cmd.SetComputeBufferParam(_gridCs, _kBuildGrid, IdLightsCs, lightsBuffer);
                cmd.SetComputeBufferParam(_gridCs, _kBuildGrid, IdRisLightIdxCs, _risIdxBuf);
                cmd.SetComputeBufferParam(_gridCs, _kBuildGrid, IdLightGrid, _gridBuf);
                cmd.SetComputeIntParams(_gridCs, IdGridDimsCs, GridX, GridY, GridZ, RisLightCount);
                cmd.SetComputeVectorParam(_gridCs, IdGridOriginCs, _gridOrigin);
                cmd.DispatchCompute(_gridCs, _kBuildGrid, GridX / 4, GridY / 4, GridZ / 4);
            }
            int cands = s.mode == LightSamplingMode.Ris ? (_gridBuiltThisFrame ? Mathf.Min(s.risCandidates, s.risCandidatesGrid) : s.risCandidates) : s.candidates;
            int far = _gridBuiltThisFrame ? s.risCandidates : cands; // outside the grid: the full candidates
            cmd.SetRayTracingIntParam(shader, IdLightSampling, (int)s.mode);
            cmd.SetRayTracingIntParam(shader, IdRisCandidates, Mathf.Clamp(cands, 1, 64));
            cmd.SetRayTracingIntParam(shader, IdRisCandidatesFar, Mathf.Clamp(far, 1, 64));
            BindShared(cmd, shader, _gridBuiltThisFrame);
        }

        /// <summary>
        /// Bind this frame's tables (built by the last <see cref="Bind"/>) to another shader that includes
        /// UltraRTLights.hlsl, e.g. the radiance cache update. Advances no state.
        /// </summary>
        public void BindTables(CommandBuffer cmd, RayTracingShader shader)
        {
            var s = Settings;
            cmd.SetRayTracingIntParam(shader, IdLightSampling, (int)s.mode);
            cmd.SetRayTracingIntParam(shader, IdRisCandidates, Mathf.Clamp(s.candidates, 1, 64));
            BindShared(cmd, shader, false);
        }

        private void BindShared(CommandBuffer cmd, RayTracingShader shader, bool useGrid)
        {
            cmd.SetRayTracingIntParam(shader, IdBounceCandidates, Mathf.Clamp(Settings.bounceCandidates, 1, 64));
            cmd.SetRayTracingIntParam(shader, IdRisLightCount, RisLightCount);
            cmd.SetRayTracingBufferParam(shader, IdLightAlias, _alias);
            cmd.SetRayTracingIntParam(shader, IdFillCount, FillLightCount);
            cmd.SetRayTracingBufferParam(shader, IdFillLights, _fill);
            cmd.SetRayTracingBufferParam(shader, IdLightGrid, _gridBuf);
            cmd.SetRayTracingBufferParam(shader, IdLightGlobalPdf, _globalPdfBuf);
            cmd.SetRayTracingVectorParam(shader, IdLightGridOrigin, useGrid ? _gridOrigin : new Vector4(0f, 0f, 0f, -1f)); // w <= 0: unused
            cmd.SetRayTracingFloatParam(shader, IdLightGridOn, useGrid ? 1f : 0f);
        }

        private static Vector4 GridOriginAt(Vector3 c) => new Vector4(
            Mathf.Floor(c.x / GridCell) * GridCell - 0.5f * GridX * GridCell,
            Mathf.Floor(c.y / GridCell) * GridCell - 0.5f * GridY * GridCell,
            Mathf.Floor(c.z / GridCell) * GridCell - 0.5f * GridZ * GridCell, GridCell);

        private int IdOf(Light l, int i) =>
            !ReferenceEquals(l, null) ? l.GetInstanceID() : VirtualIds != null && i < VirtualIds.Length ? VirtualIds[i] : 0;

        private void BuildTables(IList<Light> lights, Vector4[] lightData, int count, Vector3 camPos)
        {
            // ---- light-set change: did lights appear or disappear since the previous frame (by Light instance id)?
            _ids.Clear();
            for (int i = 0; i < count; i++)
            {
                int id = IdOf(i < lights.Count ? lights[i] : null, i);
                if (id != 0) _ids.Add(id);
            }
            int kept = 0;
            _prevIdSet.Clear();
            for (int i = 0; i < _prevCount; i++)
            {
                if (_prevIds[i] != 0 && _ids.Contains(_prevIds[i])) kept++;
                _prevIdSet.Add(_prevIds[i]);
            }
            LightSetChanged = kept != _prevCount || kept != count;
            if (LightSetChanged)
            {
                SetChangeFrames++;
                // Diagnostics: a light that keeps appearing and disappearing (flicker by enable toggling) speeds up the
                // radiance cache every time.
                for (int j = 0; j < count && _changeNames.Count < 6; j++)
                    if (j < lights.Count && !ReferenceEquals(lights[j], null) && lights[j] != null && !_prevIdSet.Contains(IdOf(lights[j], j)))
                        _changeNames.Add("+" + lights[j].name);
            }
            _prevCount = count;
            for (int i = 0; i < count; i++) _prevIds[i] = IdOf(i < lights.Count ? lights[i] : null, i);

            // ---- split: unshadowed fill lights are evaluated exhaustively, the rest go through RIS
            int nRis = 0, nFill = 0;
            double sum = 0;
            for (int i = 0; i < count; i++)
            {
                if (lightData[i * 4 + 3].z > 0.5f) { _fillData[nFill++] = (uint)i; continue; }
                _risLights[nRis] = i;
                float w = Settings.importance ? ImportanceWeight(lightData, i, camPos) : 1f;
                if (Settings.importance && Settings.portalImportance)
                    foreach (var vp in ViewPoints) w = Mathf.Max(w, ImportanceWeight(lightData, i, vp));
                _prob[nRis] = w;
                sum += w;
                nRis++;
            }
            RisLightCount = nRis;
            FillLightCount = nFill;
            if (nFill > 0) _fill.SetData(_fillData, 0, 0, nFill);
            if (nRis == 0) return;

            // ---- pdf = (1 - mix) * w / sum + mix / N, then Vose's alias table
            float mix = sum > 0 ? Mathf.Clamp01(Settings.uniformMix) : 1f;
            Array.Clear(_globalPdfData, 0, count);
            for (int k = 0; k < nRis; k++)
            {
                float pk = (float)((1.0 - mix) * (sum > 0 ? _prob[k] / sum : 0.0) + mix / nRis);
                _prob[k] = pk;
                _risIdxData[k] = (uint)_risLights[k];
                _globalPdfData[_risLights[k]] = pk;
            }
            _risIdxBuf.SetData(_risIdxData, 0, 0, nRis);
            _globalPdfBuf.SetData(_globalPdfData, 0, 0, count);

            int ns = 0, nl = 0;
            for (int k = 0; k < nRis; k++)
            {
                _threshold[k] = _prob[k] * nRis;
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
            while (ns > 0) _threshold[_small[--ns]] = 1f; // float round-off leftovers

            for (int k = 0; k < nRis; k++)
            {
                int a = _aliasIdx[k];
                _aliasData[k * 4] = AsUInt(_threshold[k]);
                _aliasData[k * 4 + 1] = (uint)_risLights[k] | ((uint)_risLights[a] << 16);
                _aliasData[k * 4 + 2] = AsUInt(_prob[k]);
                _aliasData[k * 4 + 3] = AsUInt(_prob[a]);
            }
            _alias.SetData(_aliasData, 0, 0, nRis * 4);
        }

        /// <summary>
        /// Cheap guess at how much a light matters to what the camera sees: luminance times a soft falloff with the
        /// distance from the camera to the light's sphere of influence (2 x range, where ULTRAKILL's fade reaches 0).
        /// Directional lights get their luminance. The uniform mix keeps every light reachable.
        /// </summary>
        private static float ImportanceWeight(Vector4[] d, int i, Vector3 camPos)
        {
            Vector4 l0 = d[i * 4], l1 = d[i * 4 + 1];
            float lum = Mathf.Max(0f, 0.2126f * l1.x + 0.7152f * l1.y + 0.0722f * l1.z);
            if (l0.w == 0f) return lum;
            float reach = 2f * l0.w;
            var pos = new Vector3(l0.x, l0.y, l0.z);
            // A line light (a beam, LightOverrides.PackLine) reaches as far from every point along it: its nearest point.
            Vector4 l3 = d[i * 4 + 3];
            if (l3.x < -1.5f && LightOverrides.IsLine(l3))
            {
                Vector4 l2 = d[i * 4 + 2];
                var axis = new Vector3(l2.x, l2.y, l2.z);
                pos += axis * Mathf.Clamp(Vector3.Dot(camPos - pos, axis), -l2.w, l2.w);
            }
            float outside = Mathf.Max(0f, Vector3.Distance(camPos, pos) - reach);
            return lum * reach * reach / (reach * reach + outside * outside);
        }

        private static uint AsUInt(float f) => unchecked((uint)BitConverter.SingleToInt32Bits(f));

        public void Dispose()
        {
            _alias?.Release();
            _fill?.Release();
            _gridBuf?.Release();
            _risIdxBuf?.Release();
            _globalPdfBuf?.Release();
        }
    }
}
