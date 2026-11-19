using UnityEngine;
using UnityEngine.Experimental.Rendering;

namespace UltraRT
{
    /// <summary>
    /// Glints: the lights' specular highlights on water and wet surfaces (docs/reflections.md, "Glints"). Bench variant
    /// <c>glint.&lt;field&gt;</c>, e.g. glint.strength=0.
    /// </summary>
    internal sealed class GlintSettings
    {
        /// <summary>-ultrart-glints X (0 = off, -ultrart-noglints): multiplies every highlight.</summary>
        public float strength = 1f;
        /// <summary>Water, glass and liquid blood (the transparent pass, added by the overlay).</summary>
        public bool water = true;
        /// <summary>Opaque wet surfaces: rain film and puddles, the Wet class, fresh blood (added to the reflection).</summary>
        public bool wet = true;
        /// <summary>GGX alpha of the water / glass lobe before the ripples a sample can't resolve widen it. Sharp, not a
        /// delta: the resolved ripples break a lamp into a glittering streak.</summary>
        public float waterAlpha = 0.05f;
        /// <summary>Wet surfaces: the lobe's minimum GGX alpha (roughness^2). A puddle is a near mirror (alpha 0.001) for
        /// the reflection ray; a pinpoint lamp under that lobe is a dot, under this one a streak.</summary>
        public float wetAlpha = 0.06f;
        /// <summary>Firefly clamp: the most a glint adds to a trace pixel (display luminance, before exposure).</summary>
        public float maxLuminance = 4f;
        /// <summary>Every light is weighed while there are at most this many; above, <see cref="candidates"/> from the light table.</summary>
        public int allLights = 256;
        public int candidates = 16;
        /// <summary>A light's size (soft-shadow sphere, area light, sun disk) widens its lobe by half its angular radius,
        /// at most this much GGX alpha (0 = not at all): a broad lobe is a sheen over the whole floor, not a streak.</summary>
        public float lightSize = 0.1f;
        /// <summary>The player's own lights (held torch, lights at the eye) make glints too. Off: they are fill for the
        /// view, and their highlight lit the floor around the camera.</summary>
        public bool playerLights = false;
        /// <summary>Only the lights whose reach meets the view frustum (Plugin.GlintList). Bench glint.cull=0: every light.</summary>
        public bool cull = true;
        /// <summary>Bench only (glint.noShadow=1): no shadow ray, to split the cost.</summary>
        public bool noShadow = false;

        public Vector4 Params => new Vector4(Mathf.Max(strength, 0f), Mathf.Clamp(waterAlpha, 0.002f, 1f), Mathf.Clamp(wetAlpha, 0f, 1f), Mathf.Max(maxLuminance, 0.01f));
        public Vector4 Params2 => new Vector4(Mathf.Max(allLights, 0), Mathf.Clamp(candidates, 1, 64), Mathf.Clamp(lightSize, 0f, 1f), (water ? 1f : 0f) + (wet ? 2f : 0f) + (playerLights ? 4f : 0f) + (noShadow ? 8f : 0f));
    }

    public partial class Plugin
    {
        private readonly GlintSettings _glintSettings = new GlintSettings();

        /// <summary>
        /// _Lights[i*4+3].w of a light whose fixture's glowing part carries its light (Plugin.Handover.cs): the light's own
        /// gamma colour x intensity for its glints, as 1 + r + 256 g + 65536 b with each channel sqrt(c / 16) in 8 bits
        /// (an exact integer in a float; 0 = none). PathTrace.raytrace, GlintTerm, decodes it.
        /// </summary>
        private static float GlintColorCode(Vector3 c)
        {
            int Q(float v) => Mathf.Clamp(Mathf.RoundToInt(Mathf.Sqrt(Mathf.Clamp01(v / 16f)) * 255f), 0, 255);
            int code = Q(c.x) + 256 * Q(c.y) + 65536 * Q(c.z);
            return code > 0 ? 1f + code : 0f;
        }

        private ComputeBuffer _glintBuffer, _glintRecBuffer;
        private readonly uint[] _glintList = new uint[MaxLights];
        private readonly Vector4[] _glintRecs = new Vector4[MaxLights];
        private readonly Plane[] _glintPlanes = new Plane[6];
        private int _glintCount;
        private float _glintNextLog;

        /// <summary>PathTrace bind block (BindTransparents): the glint parameters and light list for both ray generation shaders.</summary>
        private void BindGlints(RayTracingShader shader)
        {
            var s = _glintSettings;
            bool on = s.strength > 0f && (s.water || s.wet) && _reflSettings.enabled;
            _glintBuffer ??= new ComputeBuffer(MaxLights, 4) { name = "UltraRT GlintLights" };
            _glintRecBuffer ??= new ComputeBuffer(MaxLights, 16) { name = "UltraRT GlintRecords" };
            _glintCount = on ? GlintList() : 0;
            if (_glintCount > 0)
            {
                int padded = Mathf.Min((_glintCount + 3) & ~3, MaxLights); // the shader reads four at a time
                for (int k = _glintCount; k < padded; k++) { _glintList[k] = 0; _glintRecs[k] = new Vector4(0f, 0f, 0f, -1e9f); }
                _cmd.SetBufferData(_glintBuffer, _glintList, 0, 0, padded);
                _cmd.SetBufferData(_glintRecBuffer, _glintRecs, 0, 0, padded);
            }
            _cmd.SetRayTracingBufferParam(shader, "_GlintLights", _glintBuffer);
            _cmd.SetRayTracingBufferParam(shader, "_GlintRecs", _glintRecBuffer);
            _cmd.SetRayTracingIntParam(shader, "_GlintCount", _glintCount);
            if (on && Time.realtimeSinceStartup >= _glintNextLog)
            {
                _glintNextLog = Time.realtimeSinceStartup + 10f;
                Logger.LogInfo($"Glints: {_glintCount} of {_lightCount} lights reach the view ({(_portalViews.Count > 0 ? "portal in view: all" : "frustum")})");
            }
            _cmd.SetRayTracingVectorParam(shader, "_UltraRTGlint", on ? s.Params : Vector4.zero);
            _cmd.SetRayTracingVectorParam(shader, "_UltraRTGlint2", s.Params2);
        }

        /// <summary>
        /// The packed lights that can glint this frame: a point or spot light only where its reach (twice its range: every
        /// falloff ends there) meets the view frustum, since a glint lies on a visible surface within that reach; not the
        /// player's own lights unless <see cref="GlintSettings.playerLights"/>. While a portal is in view (a surface seen
        /// through it lies elsewhere) or with <see cref="GlintSettings.cull"/> off, every light.
        /// </summary>
        private int GlintList()
        {
            var s = _glintSettings;
            bool all = !s.cull || _portalViews.Count > 0 || _camera == null;
            if (!all) GeometryUtility.CalculateFrustumPlanes(_camera, _glintPlanes);
            int n = 0;
            for (int k = 0; k < _lightCount && n < _glintList.Length; k++)
            {
                var l0 = _lightData[k * 4];
                float w = _lightData[k * 4 + 1].w;
                if (!s.playerLights && w - 2f * Mathf.Floor(w * 0.5f) > 0.75f) continue; // UltraRTLights.hlsl, LightPlayerFlag
                if (!all && l0.w != 0f && !GeometryUtility.TestPlanesAABB(_glintPlanes, new Bounds(new Vector3(l0.x, l0.y, l0.z), Vector3.one * (4f * l0.w))))
                    continue;
                // The record the shader tests before the light model (PathTrace.raytrace, _GlintRecs).
                var l3 = _lightData[k * 4 + 3];
                float reach = 2f * l0.w;
                if (l0.w != 0f && l3.x < -1.5f) // area light (LightOverrides.PackArea): reach from its centre, no lobe test
                    reach = -(reach + 2f * Mathf.Max(_lightData[k * 4 + 2].w, -2f - l3.x));
                _glintRecs[n] = new Vector4(l0.x, l0.y, l0.z, l0.w != 0f ? reach : 0f);
                _glintList[n++] = (uint)k;
            }
            return n;
        }
    }
}
