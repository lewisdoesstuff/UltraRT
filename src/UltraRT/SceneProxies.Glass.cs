using UnityEngine;
using UnityEngine.Experimental.Rendering;

namespace UltraRT
{
    /// <summary>Stained glass (docs/materials.md, "Stained glass"). Bench variants glass.gain=2, glass.enabled=0.</summary>
    internal sealed class GlassSettings
    {
        /// <summary>Shadow rays pass stained glass, tinted. Off: opaque stained glass blocks light and transparent glass
        /// casts no shadow at all (the look before 2026-11-18). Read when proxy materials are made (a level load) and per
        /// frame (the any-hit).</summary>
        public bool enabled = true;
        /// <summary>Transmission = pane colour (linear) x gain x the material's "transmit" factor, at most 1 per channel.</summary>
        public float gain = 1.5f;

        public override string ToString() => $"enabled {enabled}, gain {gain:0.##}";
    }

    /// <summary>
    /// Stained glass lets light through, tinted by the panes, and its lead lines cast shadows. Materials get a "transmit"
    /// factor from ultrart-materials.json rules (StainedGlass*); their proxy materials carry it as _Transmit and their
    /// submeshes run the any-hit (Proxy.shader): a shadow ray (ULTRART_PAYLOAD_SHADOW) multiplies its transmittance
    /// (the payload's albedo) by the pane's colour and goes on; where nothing is left (the dark leading) the hit stands.
    /// VisibleRaw (UltraRTLights.hlsl) hands the tint to the lighting as g_ShadowTint, Volumetrics.raytrace as g_VolTint.
    /// Transparent stained glass (StainedGlass1A, mask 0x10) is not in the shadow rays' masks: its instances also get
    /// the geometry bit, with every enabled submesh running the any-hit, which ignores all rays but shadow rays and the
    /// transparent pass's own (ULTRART_PAYLOAD_TRANSPARENT).
    /// </summary>
    internal sealed partial class SceneProxies
    {
        public static readonly GlassSettings Glass = new GlassSettings();
        private static readonly int IdTransmit = Shader.PropertyToID("_Transmit");
        public static readonly int IdGlobalGlass = Shader.PropertyToID("_UltraRTGlass");
        /// <summary>Counts for the scene log.</summary>
        public int GlassMaterials, GlassAtlasSlots, GlassTransparentInstances;

        /// <summary>The scene has stained glass: a material or atlas variant with _Transmit, or a transparent instance with
        /// the geometry bit. Without, every any-hit runs the plain alpha test (Proxy.shader, _UltraRTGlass.y).</summary>
        public bool HasGlass => GlassMaterials + GlassAtlasSlots + GlassTransparentInstances > 0;

        /// <summary>Per frame (Plugin.MaterialsUpdate): x the any-hit's gain (0 = off), y 1 = the scene has stained glass.</summary>
        public static void SetGlassGlobals(bool sceneHasGlass) =>
            Shader.SetGlobalVector(IdGlobalGlass, new Vector4(Glass.enabled ? Mathf.Max(0f, Glass.gain) : 0f, sceneHasGlass ? 1f : 0f, 0f, 0f));

        /// <summary>Sets _Transmit on a new proxy material from its source's "transmit" rule.</summary>
        private void ApplyTransmit(Material src, Material proxy)
        {
            if (!Glass.enabled || src == null || proxy == null) return;
            float t = _classifier.TransmitOf(src);
            if (t <= 0f) return;
            proxy.SetFloat(IdTransmit, t);
            GlassMaterials++;
            ProbeLit("glass", src, $"transmit {t:0.##}");
        }

        /// <summary>A proxy material with _Transmit, or a source material with a "transmit" rule: its submesh must run the
        /// any-hit (not ClosestHitOnly).</summary>
        private static bool Transmits(Material m)
        {
            if (m == null || !Glass.enabled) return false;
            if (m.shader != null && m.shader.name == "UltraRT/Proxy") return m.HasProperty(IdTransmit) && m.GetFloat(IdTransmit) > 0f;
            return MaterialClassifier.Current != null && MaterialClassifier.Current.TransmitOf(m) > 0f;
        }

        /// <summary>A transparent instance with stained glass among its enabled submeshes: every enabled submesh runs the
        /// any-hit (the geometry bit lets every scene ray meet the instance, and the any-hit sends them on), and the
        /// returned bit goes into its instance mask. 0 (flags untouched) otherwise.</summary>
        private uint GlassShadowBit(RayTracingSubMeshFlags[] flags, Material[] mats)
        {
            if (!Glass.enabled || flags == null || mats == null) return 0u;
            bool any = false;
            for (int i = 0; i < flags.Length && i < mats.Length; i++)
                if (flags[i] != RayTracingSubMeshFlags.Disabled && Transmits(mats[i])) { any = true; break; }
            if (!any) return 0u;
            for (int i = 0; i < flags.Length; i++)
                if (flags[i] != RayTracingSubMeshFlags.Disabled) flags[i] &= ~RayTracingSubMeshFlags.ClosestHitOnly;
            GlassTransparentInstances++;
            return GeometryMask;
        }

        private string GlassSummary() =>
            $"Stained glass ({Glass}): {GlassMaterials} proxy materials and {GlassAtlasSlots} atlas variants transmit, " +
            $"{GlassTransparentInstances} transparent instances (re)added with the geometry bit";
    }
}
