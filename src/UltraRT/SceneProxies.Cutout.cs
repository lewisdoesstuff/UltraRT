using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Experimental.Rendering;

namespace UltraRT
{
    /// <summary>
    /// Diagnostics for cutout (alpha-tested) surfaces such as 1-1's grass cards (docs/scene-proxies.md, "Cutout audit").
    /// A cutout submesh needs both a proxy material with _AlphaTest and an instance flag without ClosestHitOnly, or the
    /// any-hit never runs and the card is traced as a solid quad. The audit walks every traced cutout submesh (dynamic
    /// proxies and static group slots) and counts those that lost either; logged after the scene build and every 600 frames.
    /// </summary>
    internal sealed partial class SceneProxies
    {
        private static readonly int IdAlphaTestProp = Shader.PropertyToID("_AlphaTest");

        private static bool ProxyAlphaTested(Material m) =>
            m != null && m.HasProperty(IdAlphaTestProp) && m.GetFloat(IdAlphaTestProp) > 0.5f;

        private bool _loggedMasterAlpha;

        /// <summary>Once per scene build: the Master shader's alpha/clip properties, and foliage-like materials' values.</summary>
        private void LogFoliageMaterials()
        {
            var seen = new HashSet<Material>();
            var lines = new List<string>();
            foreach (var d in _dynamic)
            {
                if (d.Source == null) continue;
                foreach (var m in d.Source.sharedMaterials)
                {
                    if (m == null || m.shader == null || !seen.Add(m)) continue;
                    string n = m.name.ToLowerInvariant();
                    if (!(n.Contains("grass") || n.Contains("bush") || n.Contains("leaf") || n.Contains("leaves") || n.Contains("foliage") || n.Contains("plant") || n.Contains("flower") || n.Contains("vine"))) continue;
                    var props = new List<string>();
                    var sh = m.shader;
                    for (int i = 0; i < sh.GetPropertyCount(); i++)
                    {
                        string pn = sh.GetPropertyName(i);
                        string l = pn.ToLowerInvariant();
                        if (!(l.Contains("alpha") || l.Contains("clip") || l.Contains("cutoff") || l.Contains("blend") || l.Contains("cull") || l.Contains("transp") || l.Contains("zwrite") || l.Contains("opac"))) continue;
                        var t = sh.GetPropertyType(i);
                        if (t == UnityEngine.Rendering.ShaderPropertyType.Float || t == UnityEngine.Rendering.ShaderPropertyType.Range) props.Add($"{pn}={m.GetFloat(pn):G3}");
                    }
                    if (lines.Count < 24)
                        lines.Add($"'{m.name}' ({sh.name}) q{m.renderQueue} tag {m.GetTag("RenderType", false, "-")} kw [{string.Join(" ", m.shaderKeywords)}] alphaTested {SourceAlphaTested(m)} {string.Join(" ", props)} on '{GameControlPath(d.SourceTransform)}'");
                }
            }
            if (lines.Count > 0) _log.LogInfo("Foliage materials: " + string.Join(" || ", lines));
        }

        public string CutoutAudit()
        {
            if (!_loggedMasterAlpha) { _loggedMasterAlpha = true; try { LogFoliageMaterials(); } catch (System.Exception e) { _log.LogWarning("Foliage materials: " + e.Message); } }
            int dynTotal = 0, dynActive = 0, dynBadMat = 0, dynBadFlag = 0;
            var bad = new List<string>();
            foreach (var d in _dynamic)
            {
                if (d.Source == null || d.Proxy == null || d.Mask == TransparentMask) continue;
                var src = d.Source.sharedMaterials;
                var pm = d.Proxy.sharedMaterials;
                for (int i = 0; i < d.Flags.Length && i < src.Length; i++)
                {
                    if (d.Flags[i] == RayTracingSubMeshFlags.Disabled || !SourceAlphaTested(src[i])) continue;
                    dynTotal++;
                    if (d.Visible) dynActive++;
                    var m = i < pm.Length ? pm[i] : null;
                    bool badMat = !ProxyAlphaTested(m);
                    bool badFlag = (d.Flags[i] & RayTracingSubMeshFlags.ClosestHitOnly) != 0;
                    if (badMat) dynBadMat++;
                    if (badFlag) dynBadFlag++;
                    if ((badMat || badFlag) && bad.Count < 8)
                        bad.Add($"dynamic '{GameControlPath(d.SourceTransform)}' slot {i} src '{src[i].name}' proxy '{m?.name}' alphaTest {(m != null && m.HasProperty(IdAlphaTestProp) ? m.GetFloat(IdAlphaTestProp) : -1)} flags {d.Flags[i]} visible {d.Visible}");
                }
            }

            int slotTotal = 0, slotBad = 0, partsCut = 0, partsCutIn = 0, partsBadFlag = 0;
            foreach (var g in _groups)
            {
                for (int p = 0; p < PartCount; p++)
                {
                    var list = g.Slots[p];
                    if (list == null) continue;
                    foreach (var s in list)
                    {
                        bool cutSrc = false;
                        if (OriginalMaterials.TryGet(s.Source, out var mats, out var counts, out _))
                            cutSrc = OriginalMaterials.Dominant(mats, counts, SourceAlphaTested) != null &&
                                     SourceAlphaTested(OriginalMaterials.Dominant(mats, counts, m => Traceable(m) && !(m.shader != null && m.shader.name.Contains("Stationary"))));
                        else if (s.Source != null && s.Sub - s.Source.subMeshStartIndex >= 0 && s.Sub - s.Source.subMeshStartIndex < s.Source.sharedMaterials.Length)
                            cutSrc = SourceAlphaTested(s.Source.sharedMaterials[s.Sub - s.Source.subMeshStartIndex]);
                        if (!cutSrc) continue;
                        slotTotal++;
                        if (!ProxyAlphaTested(s.Material))
                        {
                            slotBad++;
                            if (bad.Count < 8) bad.Add($"static slot {s.Sub} of '{g.Combined?.Name}' / '{g.Name}' source '{(s.Source != null ? GameControlPath(s.Source.transform) : "?")}' material '{s.Material?.name}'");
                        }
                    }
                    var gp = g.Parts[p];
                    if (gp?.Proxy == null) continue;
                    var pmats = gp.Proxy.sharedMaterials;
                    bool any = false;
                    foreach (var m in pmats) any |= ProxyAlphaTested(m);
                    if (!any) continue;
                    partsCut++;
                    if (gp.InRtas) partsCutIn++;
                    foreach (var m in pmats)
                        if (ProxyAlphaTested(m) && (SubmeshOn(m) & RayTracingSubMeshFlags.ClosestHitOnly) != 0) partsBadFlag++;
                }
            }
            var line = $"Cutout audit: dynamic {dynTotal} cutout submeshes ({dynActive} active; lost material {dynBadMat}, opaque flag {dynBadFlag}), " +
                       $"static {slotTotal} cutout slots (lost material {slotBad}), {partsCut} group parts with cutout materials ({partsCutIn} in the RTAS, opaque flag {partsBadFlag})";
            if (bad.Count > 0) line += "; first: " + string.Join(" | ", bad);
            return line;
        }
    }
}
