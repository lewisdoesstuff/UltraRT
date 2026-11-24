using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace UltraRT
{
    /// <summary>
    /// Harmony hooks for Cyber Grind's <c>EndlessGrid</c> (docs/scene-proxies.md, "Cyber Grind").
    /// <c>EndlessGrid</c> alternates between 256 individual <c>EndlessCube</c> (plus <c>EndlessStairs</c>) renderers while
    /// blocks rise/fall (<c>MakeGridDynamic</c>) and a single <c>Combined Static Mesh</c> once blocks settle and enemies
    /// spawn (<c>SetupStaticGridMesh</c>). It reuses the same <c>Mesh</c> instance in place (<c>Clear</c> +
    /// <c>CombineMeshes</c>), which Unity 2022.3's DXR static BLAS cache does not detect: the RTAS kept the pre-wave flat
    /// floor BLAS while the game drew the raised wave towers, making shadows vanish or glitch as soon as enemies spawned.
    /// </summary>
    internal static class CyberGrindHooks
    {
        public static readonly List<Component> PendingStatic = new List<Component>();
        public static readonly List<Component> PendingDynamic = new List<Component>();

        public static bool Hooked;
        public static string HookError;

        private static FieldInfo _combinedObjField;
        private static FieldInfo _combinedRendererField;

        public static void Patch(Harmony harmony)
        {
            try
            {
                var type = AccessTools.TypeByName("EndlessGrid");
                if (type == null) { HookError = "EndlessGrid not found"; return; }
                var setupStatic = AccessTools.Method(type, "SetupStaticGridMesh");
                var makeDynamic = AccessTools.Method(type, "MakeGridDynamic");
                if (setupStatic == null || makeDynamic == null)
                {
                    HookError = $"EndlessGrid methods missing (static={setupStatic != null}, dynamic={makeDynamic != null})";
                    return;
                }
                _combinedObjField = AccessTools.Field(type, "combinedGridStaticObject");
                _combinedRendererField = AccessTools.Field(type, "combinedGridStaticMeshRenderer");
                harmony.Patch(setupStatic, postfix: new HarmonyMethod(typeof(CyberGrindHooks).GetMethod(nameof(SetupStaticPostfix), BindingFlags.NonPublic | BindingFlags.Static)));
                harmony.Patch(makeDynamic, postfix: new HarmonyMethod(typeof(CyberGrindHooks).GetMethod(nameof(MakeDynamicPostfix), BindingFlags.NonPublic | BindingFlags.Static)));
                Hooked = true;
            }
            catch (Exception e)
            {
                HookError = e.Message;
            }
        }

        public static void ClearPending()
        {
            PendingStatic.Clear();
            PendingDynamic.Clear();
        }

        public static GameObject GetCombinedObject(Component grid)
        {
            if (grid == null) return null;
            if (_combinedObjField != null && _combinedObjField.GetValue(grid) is GameObject go && go != null) return go;
            var child = grid.transform.Find("Combined Static Mesh");
            return child != null ? child.gameObject : null;
        }

        public static MeshRenderer GetCombinedRenderer(Component grid)
        {
            if (grid == null) return null;
            if (_combinedRendererField != null && _combinedRendererField.GetValue(grid) is MeshRenderer mr && mr != null) return mr;
            var go = GetCombinedObject(grid);
            return go != null ? go.GetComponent<MeshRenderer>() : null;
        }

        private static void SetupStaticPostfix(object __instance)
        {
            try
            {
                if (__instance is Component c && c != null && !PendingStatic.Contains(c))
                    PendingStatic.Add(c);
            }
            catch (Exception) { /* never break Cyber Grind wave setup */ }
        }

        private static void MakeDynamicPostfix(object __instance)
        {
            try
            {
                if (__instance is Component c && c != null && !PendingDynamic.Contains(c))
                    PendingDynamic.Add(c);
            }
            catch (Exception) { /* never break Cyber Grind wave setup */ }
        }
    }

    internal sealed partial class SceneProxies
    {
        // Fresh Mesh snapshots owned by Cyber Grind's Combined Static Mesh proxy so Unity DXR allocates a new static BLAS
        // on every wave rebuild. Old snapshots are destroyed after RetireFrames (same policy as StaticGroups).
        private readonly Dictionary<DynamicProxy, Mesh> _cyberOwnedMeshes = new Dictionary<DynamicProxy, Mesh>();
        private readonly List<(Mesh mesh, int frame)> _retiredCyberMeshes = new List<(Mesh, int)>();
        private bool _cyberUnreadableWarned;
        private Transform _cyberGridRoot;
        private GameObject _cyberCombinedObj;

        /// <summary>Total Cyber Grind combined-mesh BLAS refreshes this scene.</summary>
        public int CyberGrindRebuilds { get; private set; }

        /// <summary>
        /// Runs at the start of <see cref="SyncDynamic"/>, before dirty proxies are evaluated:
        ///  1. Retires previous wave mesh copies once the GPU is done with them.
        ///  2. During the dynamic phase (<c>Combined Static Mesh</c> inactive), flushes deferred proxies under
        ///     <c>EndlessGrid</c> immediately so newly spawned <c>EndlessStairs</c> and <c>JumpPad</c>s enter the RTAS
        ///     before they start rising.
        ///  3. On <c>MakeGridDynamic</c>: marks every cube and stair proxy under <c>EndlessGrid</c> dirty so all 256 cubes
        ///     enter the RTAS on the same frame <c>Combined Static Mesh</c> deactivates (no ~23-frame dormant-poll pop-in).
        ///  4. On <c>SetupStaticGridMesh</c>: marks cubes/stairs dirty so they leave immediately (no 4-frame Z-fighting
        ///     overlap), ensures <c>Combined Static Mesh</c> has a proxy, replaces its proxy mesh with a fresh
        ///     <c>Object.Instantiate</c> snapshot so DXR builds a new BLAS, re-syncs submesh materials/flags (stairs can add
        ///     or remove submeshes between waves), and forces an RTAS re-add.
        /// </summary>
        private void SyncCyberGrind(bool force)
        {
            RetireCyberMeshes(force);
            if (_cyberGridRoot != null && _deferred.Count > 0 && _cyberCombinedObj != null && !_cyberCombinedObj.activeSelf)
                FlushCyberGrindDeferred();
            if (CyberGrindHooks.PendingDynamic.Count == 0 && CyberGrindHooks.PendingStatic.Count == 0) return;

            for (int i = 0; i < CyberGrindHooks.PendingDynamic.Count; i++)
            {
                var grid = CyberGrindHooks.PendingDynamic[i];
                if (grid != null)
                {
                    _cyberGridRoot = grid.transform;
                    _cyberCombinedObj = CyberGrindHooks.GetCombinedObject(grid);
                    MarkHierarchyDirty(grid.transform);
                }
            }
            CyberGrindHooks.PendingDynamic.Clear();

            for (int i = 0; i < CyberGrindHooks.PendingStatic.Count; i++)
            {
                var grid = CyberGrindHooks.PendingStatic[i];
                if (grid == null) continue;
                _cyberGridRoot = grid.transform;
                _cyberCombinedObj = CyberGrindHooks.GetCombinedObject(grid);
                MarkHierarchyDirty(grid.transform);
                RefreshCyberGrindCombined(grid);
            }
            CyberGrindHooks.PendingStatic.Clear();
        }

        private void MarkHierarchyDirty(Transform root)
        {
            for (int i = 0; i < _dynamic.Count; i++)
            {
                var d = _dynamic[i];
                if (d.SourceTransform != null && d.SourceTransform.IsChildOf(root))
                    _dirty.Add(d);
            }
        }

        /// <summary>
        /// <c>EndlessGrid.OneDone</c> spawns up to 64 <c>EndlessStairs</c> (128 renderers) and <c>JumpPad</c>s in one frame
        /// right before they rise out of the blocks. Waiting for the 0.75 ms/frame deferred proxy budget left newly rising
        /// stairs unproxied (missing from the RTAS) until right before <c>SetupStaticGridMesh</c>. Register deferred
        /// renderers under <c>EndlessGrid</c> immediately during the dynamic phase; non-arena spawns stay queued.
        /// </summary>
        private void FlushCyberGrindDeferred()
        {
            int count = _deferred.Count;
            if (count == 0) return;
            var stats = new ScanStats();
            for (int i = 0; i < count; i++)
            {
                var r = _deferred.Dequeue();
                if (r == null) continue;
                if (r.transform.IsChildOf(_cyberGridRoot))
                {
                    if (r is SkinnedMeshRenderer smr) ProcessSkinnedRenderer(smr, stats);
                    else if (r is MeshRenderer mr) ProcessMeshRenderer(mr, null, stats);
                }
                else
                {
                    _deferred.Enqueue(r);
                }
            }
            SkinnedCount += stats.Skinned;
            DeferredMade += stats.Dynamic + stats.Skinned;
        }

        private DynamicProxy FindDynamicBySource(Renderer src)
        {
            for (int i = _dynamic.Count - 1; i >= 0; i--)
                if (ReferenceEquals(_dynamic[i].Source, src)) return _dynamic[i];
            return null;
        }

        private void RefreshCyberGrindCombined(Component grid)
        {
            var srcMr = CyberGrindHooks.GetCombinedRenderer(grid);
            if (srcMr == null) return;
            var srcMf = srcMr.GetComponent<MeshFilter>();
            var srcMesh = srcMf != null ? srcMf.sharedMesh : null;
            if (srcMesh == null || srcMesh.vertexCount == 0) return;

            var d = FindDynamicBySource(srcMr);
            if (d == null)
            {
                // Combined Static Mesh didn't exist yet at Build() time (or was skipped before it had a mesh): register now.
                _known.Add(srcMr);
                ProcessMeshRenderer(srcMr, null, new ScanStats());
                d = FindDynamicBySource(srcMr);
                if (d == null) return;
            }
            if (d.Proxy == null) return;

            var proxyMf = d.Proxy.GetComponent<MeshFilter>();
            if (proxyMf == null) return;

            Mesh targetMesh = srcMesh;
            if (srcMesh.isReadable)
            {
                var fresh = Object.Instantiate(srcMesh);
                fresh.name = (string.IsNullOrEmpty(srcMesh.name) ? "Combined Static Mesh" : srcMesh.name) + " [UltraRT CG]";
                if (_cyberOwnedMeshes.TryGetValue(d, out var prev) && prev != null)
                    _retiredCyberMeshes.Add((prev, Time.frameCount));
                _cyberOwnedMeshes[d] = fresh;
                proxyMf.sharedMesh = fresh;
                targetMesh = fresh;
            }
            else
            {
                // Fallback if a future game build passes markNoLongerReadable: true: DynamicGeometry rebuilds the BLAS.
                proxyMf.sharedMesh = srcMesh;
                d.Proxy.rayTracingMode = RayTracingMode.DynamicGeometry;
                if (!_cyberUnreadableWarned)
                {
                    _cyberUnreadableWarned = true;
                    _log.LogWarning("Cyber Grind: combinedGridStaticMesh is not readable; falling back to DynamicGeometry");
                }
            }

            // Re-sync materials and submesh flags: waves with/without EndlessStairs change the submesh and material list.
            srcMr.GetSharedMaterials(_syncMats);
            int count = _syncMats.Count;
            while (count > 0 && IsOverlayMat(_syncMats[count - 1])) count--;
            var mats = new Material[count];
            for (int i = 0; i < count; i++) mats[i] = _syncMats[i];

            int subCount = Math.Max(1, targetMesh.subMeshCount);
            var flags = SubmeshFlags(mats, subCount);
            var proxyMats = new Material[mats.Length];
            for (int i = 0; i < mats.Length; i++) proxyMats[i] = ProxyOrDisabled(mats[i]);
            CausticVariants(srcMr, proxyMats);
            PathGlowVariants(srcMr, mats, proxyMats);

            ReleaseOwn(d);
            d.Proxy.sharedMaterials = proxyMats;
            if (d.Sync != null)
            {
                SetSyncMaterials(d.Sync, mats);
                d.Sync.Signature = 0;
                d.Sync.BlockCheckFrame = -1000;
            }
            d.Flags = flags;
            d.Cutout = HasCutoutSubmesh(flags, mats);
            d.TwoSided = mats.Any(m => Traceable(m) && SourceTwoSided(m));
            d.MatGen++;
            NoteDynamicChange(d);

            // Force a clean RTAS add with the new mesh (and queue the 2-frame safety re-add for this new mesh ID).
            d.EverAdded = false;
            if (d.Visible)
            {
                Rtas.RemoveInstance(d.Proxy);
                Rtas.AddInstance(d.Proxy, d.Flags, enableTriangleCulling: !d.TwoSided, frontTriangleCounterClockwise: InstanceCcw(d), mask: d.Mask | d.ShadowMask, id: d.Id);
                QueueReAdd(d);
            }
            else
            {
                _dirty.Add(d);
            }

            CyberGrindRebuilds++;
            if (CyberGrindRebuilds <= 4 || (!string.IsNullOrEmpty(TraceFilter) && GameControlPath(srcMr.transform).IndexOf(TraceFilter, StringComparison.OrdinalIgnoreCase) >= 0))
                _log.LogInfo($"Cyber Grind: refreshed '{srcMr.name}' BLAS (rebuild #{CyberGrindRebuilds}, mesh {targetMesh.GetInstanceID()}, {targetMesh.vertexCount} verts, {subCount} submeshes)");
        }

        private void RetireCyberMeshes(bool all)
        {
            for (int i = _retiredCyberMeshes.Count - 1; i >= 0; i--)
            {
                if (!all && Time.frameCount - _retiredCyberMeshes[i].frame <= RetireFrames) continue;
                var m = _retiredCyberMeshes[i].mesh;
                if (m != null)
                {
                    _meshesInRtas.Remove(m);
                    _meshFirstFrame.Remove(m);
                    Object.Destroy(m);
                }
                _retiredCyberMeshes.RemoveAt(i);
            }
        }

        private void DisposeCyberGrind()
        {
            foreach (var m in _cyberOwnedMeshes.Values)
            {
                if (m != null)
                {
                    _meshesInRtas.Remove(m);
                    _meshFirstFrame.Remove(m);
                    Object.Destroy(m);
                }
            }
            _cyberOwnedMeshes.Clear();
            RetireCyberMeshes(true);
            _cyberMaterials.Clear();
            _cyberGridRoot = null;
            _cyberCombinedObj = null;
        }

        // ---------------------------------------------------------------- psx/vertexlit/cyber animated wave pulse

        private static readonly int IdCyberGrind = Shader.PropertyToID("_CyberGrind");
        private static readonly int IdCyberIntensity = Shader.PropertyToID("_CyberIntensity");
        private static readonly int IdCyberParams = Shader.PropertyToID("_CyberParams");
        private static readonly int IdCyberOffset = Shader.PropertyToID("_CyberOffset");
        private static readonly int IdCyberFlags = Shader.PropertyToID("_CyberFlags");

        private static readonly int IdEmissiveTex = Shader.PropertyToID("_EmissiveTex");
        private static readonly int IdWorldOffset = Shader.PropertyToID("_WorldOffset");
        private static readonly int IdGradientScale = Shader.PropertyToID("_GradientScale");
        private static readonly int IdGradientFalloff = Shader.PropertyToID("_GradientFalloff");
        private static readonly int IdGradientSpeed = Shader.PropertyToID("_GradientSpeed");
        private static readonly int IdPCGamerMode = Shader.PropertyToID("_PCGamerMode");
        private static readonly int IdRainbowDensity = Shader.PropertyToID("_RainbowDensity");
        private static readonly int IdUseAlbedoAsEmissive = Shader.PropertyToID("_UseAlbedoAsEmissive");
        private static readonly int IdEmissiveReplaces = Shader.PropertyToID("_EmissiveReplaces");
        private static readonly int IdRGBEmissive = Shader.PropertyToID("_RGBEmissive");

        private sealed class CyberMaterial
        {
            public Material Src, Proxy;
            public Color Color, Emissive;
            public float Intensity;
            public Vector4 Params, Offset, Flags;
            public Texture MainTex, EmissiveTex;
            public Vector2 MainScale, MainOffset;
        }

        private readonly List<CyberMaterial> _cyberMaterials = new List<CyberMaterial>();

        private static bool IsCyberMaterial(Material src) =>
            src != null && src.shader != null && src.shader.name == "psx/vertexlit/cyber";

        /// <summary>
        /// <c>psx/vertexlit/cyber</c> (Cyber Grind's Virtual / VirtualTop / VirtualTopRow materials) draws a world-space
        /// pulsing neon wave (and a Wave 25+ rainbow and last-enemy tracking pulse) without the <c>EMISSIVE</c> keyword.
        /// We mirror it via <c>_CyberGrind</c> / <c>_CyberIntensity</c> (keeping <c>_EmissiveIntensity == 0</c> so the
        /// shifting wave is not baked into <see cref="EmissiveLights"/>'s static triangle table).
        /// </summary>
        private void ApplyCyberMaterial(Material src, Material proxy)
        {
            if (!IsCyberMaterial(src)) return;
            proxy.SetFloat(IdCyberGrind, 1f);
            var entry = new CyberMaterial { Src = src, Proxy = proxy };
            CopyCyberMaterial(entry, force: true);
            _cyberMaterials.Add(entry);
        }

        /// <summary>
        /// Called every frame from <see cref="SyncMaterials"/>: <c>EndlessGrid.Update</c> lerps <c>_EmissiveColor</c> and
        /// updates <c>_WorldOffset</c> to the last enemy's position every frame, and <c>CustomTextures</c> swaps the main
        /// and glow textures at runtime. Outside Cyber Grind the list is empty (zero cost).
        /// </summary>
        private void SyncCyberMaterials()
        {
            for (int i = _cyberMaterials.Count - 1; i >= 0; i--)
            {
                var c = _cyberMaterials[i];
                if (c.Src == null || c.Proxy == null)
                {
                    _cyberMaterials.RemoveAt(i);
                    continue;
                }
                if (CopyCyberMaterial(c, force: false)) SyncPropertyUpdates++;
            }
        }

        private bool CopyCyberMaterial(CyberMaterial c, bool force)
        {
            var src = c.Src;
            var proxy = c.Proxy;
            Color col = src.HasProperty(IdColor) ? src.GetColor(IdColor) : Color.white;
            Color emCol = src.HasProperty(IdEmissiveColor) ? src.GetColor(IdEmissiveColor) : Color.black;
            float intensity = src.HasProperty(IdEmissiveIntensity) ? Mathf.Max(src.GetFloat(IdEmissiveIntensity), 0f) : 0f;
            var p = new Vector4(
                src.HasProperty(IdGradientScale) ? src.GetFloat(IdGradientScale) : 1f,
                src.HasProperty(IdGradientFalloff) ? src.GetFloat(IdGradientFalloff) : 1f,
                src.HasProperty(IdGradientSpeed) ? src.GetFloat(IdGradientSpeed) : 1f,
                src.HasProperty(IdPCGamerMode) ? src.GetFloat(IdPCGamerMode) : 0f);
            Vector4 worldOff = src.HasProperty(IdWorldOffset) ? src.GetVector(IdWorldOffset) : Vector4.zero;
            float rainbowDensity = src.HasProperty(IdRainbowDensity) ? src.GetFloat(IdRainbowDensity) : 1f;
            var off = new Vector4(worldOff.x, worldOff.y, worldOff.z, rainbowDensity);
            var fl = new Vector4(
                src.HasProperty(IdUseAlbedoAsEmissive) ? src.GetFloat(IdUseAlbedoAsEmissive) : 0f,
                src.HasProperty(IdEmissiveReplaces) ? src.GetFloat(IdEmissiveReplaces) : 0f,
                src.HasProperty(IdRGBEmissive) ? src.GetFloat(IdRGBEmissive) : 0f,
                0f);
            Texture mainTex = src.HasProperty(IdMainTex) ? Tex2D(src, "_MainTex") : null;
            Vector2 mainScale = src.HasProperty(IdMainTex) ? src.GetTextureScale(IdMainTex) : Vector2.one;
            Vector2 mainOff = src.HasProperty(IdMainTex) ? src.GetTextureOffset(IdMainTex) : Vector2.zero;
            Texture emTex = src.HasProperty(IdEmissiveTex) ? Tex2D(src, "_EmissiveTex") : null;

            if (!force && col == c.Color && emCol == c.Emissive && intensity == c.Intensity &&
                p == c.Params && off == c.Offset && fl == c.Flags &&
                ReferenceEquals(mainTex, c.MainTex) && mainScale == c.MainScale && mainOff == c.MainOffset &&
                ReferenceEquals(emTex, c.EmissiveTex))
                return false;

            if (force || col != c.Color) proxy.SetColor(IdColor, col);
            if (force || emCol != c.Emissive) proxy.SetColor(IdEmissiveColor, emCol);
            if (force || intensity != c.Intensity) proxy.SetFloat(IdCyberIntensity, intensity);
            if (force || p != c.Params) proxy.SetVector(IdCyberParams, p);
            if (force || off != c.Offset) proxy.SetVector(IdCyberOffset, off);
            if (force || fl != c.Flags) proxy.SetVector(IdCyberFlags, fl);
            if ((force || !ReferenceEquals(mainTex, c.MainTex)) && mainTex != null) proxy.SetTexture(IdMainTex, mainTex);
            if (force || mainScale != c.MainScale) proxy.SetTextureScale(IdMainTex, mainScale);
            if (force || mainOff != c.MainOffset) proxy.SetTextureOffset(IdMainTex, mainOff);
            if (force || !ReferenceEquals(emTex, c.EmissiveTex)) proxy.SetTexture(IdEmissiveTex, emTex != null ? emTex : Texture2D.whiteTexture);

            c.Color = col; c.Emissive = emCol; c.Intensity = intensity;
            c.Params = p; c.Offset = off; c.Flags = fl;
            c.MainTex = mainTex; c.MainScale = mainScale; c.MainOffset = mainOff; c.EmissiveTex = emTex;
            return true;
        }
    }
}
