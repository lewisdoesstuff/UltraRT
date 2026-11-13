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
        /// <summary>RIS over the light list, one shadow ray. No history.</summary>
        Ris = 1,
        /// <summary>RIS plus temporal reservoir reuse (ReSTIR DI, biased 1/M combine). One shadow ray.</summary>
        ReSTIR = 2,
    }

    /// <summary>Tunables, read every <see cref="LightSampling.Bind"/>. Safe to change at any time.</summary>
    public sealed class LightSamplingSettings
    {
        public LightSamplingMode mode = LightSamplingMode.ReSTIR;
        /// <summary>RIS candidates per pixel at the primary hit (M). Costs one light evaluation each, no rays.</summary>
        public int candidates = 8;
        /// <summary>RIS candidates at the bounce hit (no reuse there).</summary>
        public int bounceCandidates = 4;
        /// <summary>Source pdf from light power and distance to the camera; false = uniform over shadowed lights.</summary>
        public bool importance = true;
        /// <summary>Fraction of the source pdf that stays uniform, so no light is ever starved.</summary>
        public float uniformMix = 0.2f;
        /// <summary>History M cap, as a multiple of <see cref="candidates"/>.</summary>
        public float historyLimit = 20f;
        /// <summary>On frames where lights appear or disappear, cap history M to 1x candidates (cuts lag on room toggles).</summary>
        public bool shortenHistoryOnLightChange = true;
        /// <summary>Extra reservoirs reused from the previous frame around the reprojected pixel (0 = temporal only).</summary>
        public int spatialSamples = 0;
        /// <summary>Radius of the spatial neighbourhood, in trace-resolution pixels.</summary>
        public float spatialRadius = 12f;
        /// <summary>Reuse: minimum cosine between current and history normal.</summary>
        public float normalTolerance = 0.9f;
        /// <summary>Reuse: maximum distance from the history surface's plane, relative to hit distance (+0.01 absolute).</summary>
        public float planeTolerance = 0.01f;
    }

    /// <summary>
    /// Light selection for PathTrace.raytrace (UltraRTLights.hlsl): per-frame alias table over the shadowed lights,
    /// fill-light list, light index remap between frames, and the ping-ponged per-pixel reservoirs.
    /// See docs/light-sampling.md. Call <see cref="Bind"/> once per traced frame, after the light buffers are filled
    /// and before DispatchRays; the recorded commands must run once, in order.
    /// </summary>
    internal sealed class LightSampling : IDisposable
    {
        public LightSamplingSettings Settings = new LightSamplingSettings();

        /// <summary>Lights in the table this frame that are shadowed (go through RIS) and unshadowed (evaluated directly).</summary>
        public int RisLightCount { get; private set; }
        public int FillLightCount { get; private set; }
        /// <summary>True when lights were added or removed relative to the previous <see cref="Bind"/>.</summary>
        public bool LightSetChanged { get; private set; }

        private const uint NoLight = 0xFFFF;

        private readonly int _maxLights;
        private readonly ComputeBuffer _alias, _fill, _remap, _dummyPrev, _dummyCur; // dummies: never bind one buffer as SRV and UAV
        private readonly uint[] _aliasData, _fillData, _remapData;
        private readonly float[] _prob;
        private readonly int[] _risLights, _small, _large, _aliasIdx;
        private readonly float[] _threshold;

        private readonly int[] _prevIds;
        private int _prevCount;
        private readonly Dictionary<int, int> _indexById = new Dictionary<int, int>();

        private readonly ComputeBuffer[] _reservoirs = new ComputeBuffer[2];
        private int _width, _height, _cur;
        private bool _historyValid;
        private Matrix4x4 _prevViewProj;

        private static readonly int
            IdLightSampling = Shader.PropertyToID("_LightSampling"),
            IdRisCandidates = Shader.PropertyToID("_RisCandidates"),
            IdBounceCandidates = Shader.PropertyToID("_BounceCandidates"),
            IdRisLightCount = Shader.PropertyToID("_RisLightCount"),
            IdLightAlias = Shader.PropertyToID("_LightAlias"),
            IdFillCount = Shader.PropertyToID("_FillCount"),
            IdFillLights = Shader.PropertyToID("_FillLights"),
            IdLightRemap = Shader.PropertyToID("_LightRemap"),
            IdPrevLightCount = Shader.PropertyToID("_PrevLightCount"),
            IdReservoirPrev = Shader.PropertyToID("_ReservoirPrev"),
            IdReservoirCur = Shader.PropertyToID("_ReservoirCur"),
            IdPrevViewProj = Shader.PropertyToID("_PrevViewProj"),
            IdRestirHistoryValid = Shader.PropertyToID("_RestirHistoryValid"),
            IdRestirParams = Shader.PropertyToID("_RestirParams"),
            IdRestirSpatial = Shader.PropertyToID("_RestirSpatial");

        public LightSampling(int maxLights)
        {
            if (maxLights > 0xFFFF) throw new ArgumentOutOfRangeException(nameof(maxLights), "light indices are stored in 16 bits");
            _maxLights = maxLights;
            _alias = new ComputeBuffer(maxLights, 16) { name = "UltraRT LightAlias" };
            _fill = new ComputeBuffer(maxLights, 4) { name = "UltraRT FillLights" };
            _remap = new ComputeBuffer(maxLights, 4) { name = "UltraRT LightRemap" };
            _dummyPrev = new ComputeBuffer(1, 16) { name = "UltraRT Reservoir (unused prev)" };
            _dummyCur = new ComputeBuffer(1, 16) { name = "UltraRT Reservoir (unused cur)" };
            _aliasData = new uint[maxLights * 4];
            _fillData = new uint[maxLights];
            _remapData = new uint[maxLights];
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
            _remap.SetData(_remapData);
            _dummyPrev.SetData(new uint[4]);
            _dummyCur.SetData(new uint[4]);
        }

        /// <summary>Drop the reservoir history (scene change, shader reload, camera cut, respawn, view re-enabled).</summary>
        public void Reset() => _historyValid = false;

        /// <summary>
        /// Build this frame's light tables and bind everything UltraRTLights.hlsl reads.
        /// </summary>
        /// <param name="lights">The Light behind each packed entry: lights[i] fills lightData[i*4 .. i*4+3].</param>
        /// <param name="lightData">The same array uploaded to _Lights (4 x Vector4 per light).</param>
        /// <param name="lightCount">Number of packed lights (_LightCount).</param>
        /// <param name="width">Dispatch width (trace resolution).</param>
        /// <param name="height">Dispatch height.</param>
        /// <param name="viewProj">projectionMatrix * worldToCameraMatrix of this frame (the inverse of _InvViewProj).</param>
        /// <param name="camPos">Camera position, for the importance heuristic.</param>
        public void Bind(CommandBuffer cmd, RayTracingShader shader, int width, int height, Matrix4x4 viewProj, Vector3 camPos,
                         IList<Light> lights, Vector4[] lightData, int lightCount)
        {
            var s = Settings;
            lightCount = Mathf.Clamp(lightCount, 0, _maxLights);
            BuildTables(lights, lightData, lightCount, camPos);

            bool restir = s.mode == LightSamplingMode.ReSTIR;
            ComputeBuffer prev = _dummyPrev, cur = _dummyCur;
            if (restir)
            {
                EnsureReservoirs(width, height);
                cur = _reservoirs[_cur];
                prev = _reservoirs[_cur ^ 1];
            }
            else
            {
                ReleaseReservoirs();
            }

            int candidates = Mathf.Clamp(s.candidates, 1, 64);
            float cap = Mathf.Max(1f, s.historyLimit) * candidates;
            if (s.shortenHistoryOnLightChange && LightSetChanged) cap = candidates;

            cmd.SetRayTracingIntParam(shader, IdLightSampling, (int)s.mode);
            cmd.SetRayTracingIntParam(shader, IdRisCandidates, candidates);
            cmd.SetRayTracingIntParam(shader, IdBounceCandidates, Mathf.Clamp(s.bounceCandidates, 1, 64));
            cmd.SetRayTracingIntParam(shader, IdRisLightCount, RisLightCount);
            cmd.SetRayTracingBufferParam(shader, IdLightAlias, _alias);
            cmd.SetRayTracingIntParam(shader, IdFillCount, FillLightCount);
            cmd.SetRayTracingBufferParam(shader, IdFillLights, _fill);
            cmd.SetRayTracingBufferParam(shader, IdLightRemap, _remap);
            cmd.SetRayTracingIntParam(shader, IdPrevLightCount, _prevCountForShader);
            cmd.SetRayTracingBufferParam(shader, IdReservoirPrev, prev);
            cmd.SetRayTracingBufferParam(shader, IdReservoirCur, cur);
            cmd.SetRayTracingMatrixParam(shader, IdPrevViewProj, _historyValid ? _prevViewProj : viewProj);
            cmd.SetRayTracingIntParam(shader, IdRestirHistoryValid, restir && _historyValid ? 1 : 0);
            cmd.SetRayTracingVectorParam(shader, IdRestirParams,
                new Vector4(Mathf.Min(cap, 60000f), Mathf.Clamp(s.normalTolerance, -1f, 1f), Mathf.Max(0f, s.planeTolerance), Mathf.Max(0f, s.spatialRadius)));
            cmd.SetRayTracingIntParam(shader, IdRestirSpatial, Mathf.Clamp(s.spatialSamples, 0, 8));

            if (restir)
            {
                _cur ^= 1;
                _historyValid = true;
                _prevViewProj = viewProj;
            }
        }

        private int _prevCountForShader;

        private void BuildTables(IList<Light> lights, Vector4[] lightData, int count, Vector3 camPos)
        {
            // ---- stable identity: remap last frame's indices to this frame's (by Light instance id)
            _indexById.Clear();
            for (int i = 0; i < count; i++)
            {
                var l = i < lights.Count ? lights[i] : null;
                int id = ReferenceEquals(l, null) ? 0 : l.GetInstanceID();
                if (id != 0) _indexById[id] = i;
            }
            int kept = 0;
            for (int i = 0; i < _prevCount; i++)
            {
                if (_prevIds[i] != 0 && _indexById.TryGetValue(_prevIds[i], out int j)) { _remapData[i] = (uint)j; kept++; }
                else _remapData[i] = NoLight;
            }
            LightSetChanged = kept != _prevCount || kept != count;
            _prevCountForShader = _prevCount;
            if (_prevCount > 0) _remap.SetData(_remapData, 0, 0, _prevCount);
            _prevCount = count;
            for (int i = 0; i < count; i++)
            {
                var l = i < lights.Count ? lights[i] : null;
                _prevIds[i] = ReferenceEquals(l, null) ? 0 : l.GetInstanceID();
            }

            // ---- split: unshadowed fill lights are evaluated exhaustively, the rest go through RIS
            int nRis = 0, nFill = 0;
            double sum = 0;
            for (int i = 0; i < count; i++)
            {
                if (lightData[i * 4 + 3].z > 0.5f) { _fillData[nFill++] = (uint)i; continue; }
                _risLights[nRis] = i;
                float w = Settings.importance ? ImportanceWeight(lightData, i, camPos) : 1f;
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
            for (int k = 0; k < nRis; k++)
                _prob[k] = (float)((1.0 - mix) * (sum > 0 ? _prob[k] / sum : 0.0) + mix / nRis);

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
            float outside = Mathf.Max(0f, Vector3.Distance(camPos, new Vector3(l0.x, l0.y, l0.z)) - reach);
            return lum * reach * reach / (reach * reach + outside * outside);
        }

        private static uint AsUInt(float f) => unchecked((uint)BitConverter.SingleToInt32Bits(f));

        private void EnsureReservoirs(int w, int h)
        {
            if (_reservoirs[0] != null && w == _width && h == _height) return;
            ReleaseReservoirs();
            _width = w;
            _height = h;
            for (int i = 0; i < 2; i++)
                _reservoirs[i] = new ComputeBuffer(Math.Max(1, w * h), 16, ComputeBufferType.Structured) { name = $"UltraRT Reservoirs {i}" };
            _cur = 0;
            _historyValid = false;
        }

        private void ReleaseReservoirs()
        {
            for (int i = 0; i < 2; i++)
            {
                _reservoirs[i]?.Release();
                _reservoirs[i] = null;
            }
            _width = _height = 0;
            _historyValid = false;
        }

        public void Dispose()
        {
            ReleaseReservoirs();
            _alias?.Release();
            _fill?.Release();
            _remap?.Release();
            _dummyPrev?.Release();
            _dummyCur?.Release();
        }
    }
}
