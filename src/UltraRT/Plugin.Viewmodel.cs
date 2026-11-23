using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace UltraRT
{
    /// <summary>
    /// Ray-traced lighting for the first-person viewmodel (docs/viewmodel.md). ULTRAKILL draws the arms and guns (layer 13,
    /// under Main Camera) with the HUD Camera (FOV 90) into the same colour and depth buffers, after the world. A command
    /// buffer on that camera, right after its opaques:
    ///   1. G-buffer: draws the viewmodel renderers again with ViewmodelGBuffer.shader (albedo, normal, position, emission,
    ///      motion) at full resolution, with the HUD camera's projection, so the pixels line up with the game's.
    ///   2. Viewmodel.raytrace at half resolution over the viewmodel's screen rectangle: direct light with shadow rays into
    ///      the world RTAS, one bounce lit from the radiance cache, one reflection ray on glossy/metal parts.
    ///   3. ViewmodelFilter.compute: screen-space temporal accumulation (rigid motion of each renderer) and a-trous.
    ///   4. ViewmodelComposite.shader: replaces the game's viewmodel pixels where the depths match (relight / pt-dn: albedo
    ///      x RT lighting, exposure and tone curve as the world).
    /// Hooks in Plugin.cs: ParseViewmodelArgs (Awake), LoadViewmodelShaders (LoadShaders), ViewmodelPreRender
    /// (OnCameraPreRender, non-main cameras), ViewmodelTraced (end of Trace), ViewmodelReset (ResetScene), ViewmodelHotkeys.
    ///   -ultrart-viewmodel on|off (config [Viewmodel] Enabled), -ultrart-vm-debug N,
    ///   -ultrart-vm-samples N, -ultrart-vm-history N, -ultrart-vm-lightmask HEX
    /// Ctrl+F7 toggles in game.
    /// </summary>
    public partial class Plugin
    {
        // Settings (Plugin fields, so bench variants can set them: plugin._vmEnabled=0, plugin._vmDebug=1, ...).
        private bool _vmEnabled = true;
        private int _vmDebug;              // composite debug view: 1 coverage, 2 lighting, 3 albedo, 4 reflection, 5 history
        private int _vmSamples = 1;        // direct light samples (shadow rays) per half-res pixel
        private float _vmMaxHistory = 32f, _vmMaxHistoryRefl = 12f, _vmClampK = 3f;
        /// <summary>Fast response (ViewmodelFilter Temporal): history cut to 2 frames where the 3x3 mean leaves the history by
        /// more than this many standard errors plus the relative tolerance (lower tolerance on frames where lights appeared or
        /// disappeared). Sigma 0 = the old ratio test (1.8x / 0.55x, and every light-set change), which fired on noise.</summary>
        private float _vmFastSigma = 3f, _vmFastTol = 0.3f, _vmFastTolChange = 0.1f;
        /// <summary>RIS candidates per direct-light sample on the viewmodel (0 = the world's LightSamplingSettings.candidates).</summary>
        private int _vmCandidates = 16;
        private int _vmAtrous = 3;         // a-trous iterations (0-3: steps 1, 2, 4)
        private float _vmDepthTol = 0.03f; // composite: |game depth - ours| tolerance, fraction of the depth (+ 5 mm)
        private int _vmLightMask;          // 0 = auto (layer 13 | the layer under the player)
        // The player's own lights (eye light, flashlight, muzzle flashes, a held torch) light the gun only when the game does:
        // their culling mask includes layer 13. The eye light leaves 13 out; through the ground-layer bit of the mask it lit
        // the RT gun white in every room (4-4's lamp-lit gold hall: a grey gun; docs/viewmodel.md, "The eye light").
        private bool _vmFullScreen;        // dispatch over the whole screen instead of the viewmodel's rectangle
        private bool _vmCheckerboard = true; // trace half the half-res pixels per frame (alternating); the filter fills the rest
        private int _vmRayTest = 0;            // measurement only: 1 = dispatch a single ray-gen thread (fixed cost of the dispatch)
        private bool _vmStableRefl = true;   // mirror-direction reflection rays (stable); false = GGX samples like the world
        /// <summary>Grazing reflections (ViewmodelComposite): below this cos(normal, view) the gun's reflection is blended
        /// towards its own diffuse lighting (the environment's average, as a fully rough lobe would see it). The viewmodel is
        /// not in the RTAS, so a grazing mirror ray runs straight past the gun and returned the scene right behind it: the gun
        /// looked see-through (docs/viewmodel.md, "See-through gun"). 0 = the old mirror reflection everywhere.</summary>
        private float _vmReflGraze = 0.5f;
        /// <summary>Per-vertex motion of skinned renderers (bone animation) from a copy of last frame's skinned vertex buffer;
        /// false = rigid motion of the root bone only (the first version). Bench plugin._vmSkinnedMotion=0.</summary>
        private bool _vmSkinnedMotion = true;
        /// <summary>Exponent of the cosine for the player's own lights (flashlight, muzzle flashes, held torch) on the gun
        /// under linear transport (Viewmodel.raytrace): 2.2 shades like the game's gamma-space Lambert; 1 = the physical
        /// cosine (flat, blown out under 7-3's flashlight).</summary>
        private float _vmNdlPow = 2.2f;
        private int _vmKBounds;
        private ComputeBuffer _vmBoundsBuf;
        private static readonly uint[] VmBoundsInit = { 0xFFFFu, 0xFFFFu, 0u, 0u };
        // GPU-measured bounds of the viewmodel (half-res pixels; xMax/yMax exclusive), read back a few frames late, with the
        // dispatch rectangle they were measured in.
        private RectInt _vmGpuBounds, _vmGpuDispatch;
        private uint _vmGpuBoundsFrame;
        private bool _vmGpuBoundsValid;

        private RayTracingShader _vmRt;
        private ComputeShader _vmFilter;
        private Shader _vmGbShader;
        private Material _vmCompositeMat, _vmClearMat;
        private int _vmKTemporal, _vmKAtrous;
        private CommandBuffer _vmCmd;
        private Camera _vmCam;
        private int _vmTraceFrame = -1;
        private Kind _vmTraceKind;
        private string _vmTraceMode;
        private uint _vmFrame;
        private bool _vmNeedsClear = true, _vmHistValid;
        private int _vmHist; // which history set is "current"
        private float _vmPrevStamp;
        private float _vmNextRefresh, _vmNextLog;
        private int _vmLogs, _vmGroundLayer = 24;
        private const int SlotViewmodel = 11, SlotVmGBuffer = 12, SlotVmRays = 13, SlotVmFilter = 14, SlotVmComposite = 15;

        private struct VmDraw { public Renderer r; public int sub; public Material mat, src; public Transform t; public bool custom; public int baseVertex; }

        /// <summary>Skinned renderer: a copy of its skinned vertex buffer from the last frame it was drawn (ViewmodelGBuffer's
        /// _VMPrevVerts), so the G-buffer's motion vectors carry bone animation.</summary>
        private sealed class VmSkin
        {
            public GraphicsBuffer prev, cur; // cur: this frame's skinned buffer (Unity's; disposed next frame)
            public int bytes, stride, posOffset, vertexCount;
            public uint copiedFrame;         // _vmFrame of the last copy into prev (0 = never)
            public bool logged;
        }
        private readonly Dictionary<SkinnedMeshRenderer, VmSkin> _vmSkins = new Dictionary<SkinnedMeshRenderer, VmSkin>();
        private readonly List<GraphicsBuffer> _vmDisposeNext = new List<GraphicsBuffer>();
        private GraphicsBuffer _vmDummyVerts;
        private int _vmKCopyVerts = -1;
        private int _vmSkinLogs;
        private static readonly int VmPrevVertsId = Shader.PropertyToID("_VMPrevVerts"), VmPrevSkinId = Shader.PropertyToID("_VMPrevSkin");
        private MaterialPropertyBlock _vmMpb;
        private int _vmDrawKey, _vmDrawLogs;
        private static readonly int[] VmCustomIds = { Shader.PropertyToID("_CustomColor1"), Shader.PropertyToID("_CustomColor2"), Shader.PropertyToID("_CustomColor3") };
        private readonly List<Renderer> _vmRenderers = new List<Renderer>();
        private readonly List<VmDraw> _vmDraws = new List<VmDraw>();
        private readonly Dictionary<Renderer, Matrix4x4> _vmPrevMV = new Dictionary<Renderer, Matrix4x4>();
        private readonly Dictionary<Material, Material> _vmMaterials = new Dictionary<Material, Material>();
        private readonly List<Vector4> _vmMatTable = new List<Vector4>();
        private ComputeBuffer _vmMatBuffer, _vmDummyExposure;
        private bool _vmMatDirty = true;

        // Targets: full-res G-buffer + depth, game depth/colour copies; half-res raw samples and two history sets.
        private RenderTexture _vmGB0, _vmGB1, _vmGB2, _vmGB3, _vmGB4, _vmDepth;
        private RenderTexture _vmRawLight, _vmRawDirect, _vmRawRefl, _vmRawGeo;
        private readonly RenderTexture[] _vmHLight = new RenderTexture[2], _vmHDirect = new RenderTexture[2], _vmHRefl = new RenderTexture[2], _vmHGeo = new RenderTexture[2];
        private readonly RenderTargetIdentifier[] _vmMrt = new RenderTargetIdentifier[5];

        private static readonly int
            VmStampId = Shader.PropertyToID("_VMStamp"), VmPrevDeltaId = Shader.PropertyToID("_VMPrevDelta"),
            VmGB0Id = Shader.PropertyToID("_VMGB0"), VmGB1Id = Shader.PropertyToID("_VMGB1"), VmGB2Id = Shader.PropertyToID("_VMGB2"),
            VmGB3Id = Shader.PropertyToID("_VMGB3"),
            VmLightId = Shader.PropertyToID("_VMLight"), VmReflId = Shader.PropertyToID("_VMRefl"),
            VmGeoId = Shader.PropertyToID("_VMGeo"), VmGameDepthId = Shader.PropertyToID("_VMGameDepth"),
            VmMaterialsId = Shader.PropertyToID("_VMMaterials"), VmDebugId = Shader.PropertyToID("_VMDebug"),
            VmCamFwdId = Shader.PropertyToID("_VMCamFwd"), VmParamsId = Shader.PropertyToID("_VMParams"), VmRefl2Id = Shader.PropertyToID("_VMRefl2"),
            VmExposureId = Shader.PropertyToID("_UltraRTExposureGlobal");

        private void ParseViewmodelArgs()
        {
            _vmEnabled = Config.Bind("Viewmodel", "Enabled", true,
                "Ray-traced lighting for the first-person arms and guns in relight, pt-dn and pathtrace (docs/viewmodel.md). " +
                "Overridden by -ultrart-viewmodel on|off; Ctrl+F7 toggles in game.").Value;
            var a = Environment.GetCommandLineArgs();
            for (int i = 0; i < a.Length; i++)
            {
                string Next() => i + 1 < a.Length ? a[++i] : "";
                bool On() { var v = Next().ToLowerInvariant(); return v != "off" && v != "0" && v != "false"; }
                switch (a[i].ToLowerInvariant())
                {
                    case "-ultrart-viewmodel": _vmEnabled = On(); break;
                    case "-ultrart-vm-debug": int.TryParse(Next(), out _vmDebug); break;
                    case "-ultrart-vm-samples": int.TryParse(Next(), out _vmSamples); break;
                    case "-ultrart-vm-history": float.TryParse(Next(), NumberStyles.Float, CultureInfo.InvariantCulture, out _vmMaxHistory); break;
                    case "-ultrart-vm-lightmask": int.TryParse(Next(), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _vmLightMask); break;
                    case "-ultrart-vm-fullscreen": _vmFullScreen = true; break;
                    case "-ultrart-vm-skinned": _vmSkinnedMotion = On(); break;
                }
            }
            Logger.LogInfo($"Viewmodel RT: {(_vmEnabled ? "on" : "off")}");
        }

        private void LoadViewmodelShaders(AssetBundle bundle)
        {
            _vmRt = bundle.LoadAllAssets<RayTracingShader>().FirstOrDefault(s => s.name == "Viewmodel");
            _vmFilter = bundle.LoadAllAssets<ComputeShader>().FirstOrDefault(s => s.name == "ViewmodelFilter");
            var shaders = bundle.LoadAllAssets<Shader>();
            _vmGbShader = shaders.FirstOrDefault(s => s.name == "UltraRT/ViewmodelGBuffer");
            var comp = shaders.FirstOrDefault(s => s.name == "UltraRT/ViewmodelComposite");
            ViewmodelReset();
            if (_vmCompositeMat != null) Destroy(_vmCompositeMat);
            if (_vmClearMat != null) Destroy(_vmClearMat);
            _vmCompositeMat = _vmClearMat = null;
            if (_vmRt == null || _vmFilter == null || _vmGbShader == null || comp == null)
            {
                Logger.LogWarning($"Viewmodel shaders missing (raytrace={(_vmRt ? "ok" : "MISSING")} filter={(_vmFilter ? "ok" : "MISSING")} " +
                                  $"gbuffer={(_vmGbShader ? "ok" : "MISSING")} composite={(comp ? "ok" : "MISSING")}): viewmodel RT off");
                _vmRt = null;
                return;
            }
            _vmCompositeMat = new Material(comp) { name = "UltraRT Viewmodel Composite" };
            _vmClearMat = new Material(_vmGbShader) { name = "UltraRT Viewmodel Clear" };
            _vmKTemporal = _vmFilter.FindKernel("Temporal");
            _vmKAtrous = _vmFilter.FindKernel("Atrous");
            _vmKBounds = _vmFilter.FindKernel("Bounds");
            _vmKCopyVerts = _vmFilter.HasKernel("CopyVerts") ? _vmFilter.FindKernel("CopyVerts") : -1;
        }

        private void ViewmodelHotkeys()
        {
            if (!Input.GetKeyDown(KeyCode.F7) || !CtrlHeld()) return;
            _vmEnabled = !_vmEnabled;
            _vmHistValid = false;
            Logger.LogInfo($"Viewmodel RT: {(_vmEnabled ? "on" : "off")}");
            Toast($"Viewmodel ray tracing {(_vmEnabled ? "on" : "off")} (Ctrl+F7)");
        }

        /// <summary>End of Trace: the world was traced this frame in this mode.</summary>
        private void ViewmodelTraced(Kind kind)
        {
            _vmTraceFrame = Time.frameCount;
            _vmTraceKind = kind;
            _vmTraceMode = Modes[_mode].name;
        }

        /// <summary>Scene change / shader reload: materials and renderers belong to the old scene or bundle.</summary>
        private void ViewmodelReset()
        {
            foreach (var m in _vmMaterials.Values) if (m != null) Destroy(m);
            _vmMaterials.Clear();
            _vmMatTable.Clear();
            _vmMatDirty = true;
            _vmRenderers.Clear();
            _vmDraws.Clear();
            _vmPrevMV.Clear();
            ReleaseViewmodelSkins();
            _vmNextRefresh = 0f;
            _vmHistValid = false;
            _vmNeedsClear = true;
            _vmLogs = 0;
        }

        private bool ViewmodelWanted() =>
            _vmEnabled && _vmRt != null && _vmTraceFrame == Time.frameCount && _proxies?.Rtas != null && _radianceCache != null &&
            (RelightLook(_vmTraceMode) || _vmTraceMode == "pt-dn");

        /// <summary>Camera.onPreRender for every camera other than Main Camera: record the pass on the HUD camera.</summary>
        private void ViewmodelPreRender(Camera cam)
        {
            var pp = MonoSingleton<PostProcessV2_Handler>.Instance;
            if (pp == null || cam != pp.hudCam) return;
            if (_vmCmd == null) _vmCmd = new CommandBuffer { name = "UltraRT Viewmodel" };
            if (_vmCam != cam)
            {
                if (_vmCam != null) _vmCam.RemoveCommandBuffer(CameraEvent.AfterForwardOpaque, _vmCmd);
                cam.AddCommandBuffer(CameraEvent.AfterForwardOpaque, _vmCmd);
                _vmCam = cam;
                _vmHistValid = false;
                Logger.LogInfo($"Viewmodel RT attached to {Bench.GameControl.PathOf(cam.transform)}: depth {cam.depth} (main {(_camera != null ? _camera.depth : float.NaN)}), " +
                               $"clear {cam.clearFlags}, fov {cam.fieldOfView:F1} (main {(_camera != null ? _camera.fieldOfView : 0f):F1}), near {cam.nearClipPlane} far {cam.farClipPlane}, " +
                               $"mask 0x{cam.cullingMask:X8} (main 0x{(_camera != null ? _camera.cullingMask : 0):X8}), local pos {cam.transform.localPosition} rot {cam.transform.localEulerAngles} scale {cam.transform.localScale}, " +
                               $"target {(cam.targetTexture != null ? cam.targetTexture.name : "buffers")}, {cam.pixelWidth}x{cam.pixelHeight}, rendering path {cam.actualRenderingPath}");
            }
            _vmCmd.Clear();
            if (!ViewmodelWanted()) { _vmHistValid = false; return; }
            long tvm = Perf.Now;
            try { RecordViewmodel(cam, pp); Perf.Add(Perf.Sec.Viewmodel, tvm); }
            catch (Exception e)
            {
                _vmCmd.Clear();
                Logger.LogError($"Viewmodel RT failed, disabling it: {e}");
                _vmEnabled = false;
            }
        }

        // ---------------------------------------------------------------- renderers and materials

        private void RefreshViewmodelRenderers(Camera hud)
        {
            _vmRenderers.Clear();
            var main = _camera != null ? _camera : hud.transform.parent?.GetComponent<Camera>();
            var root = main != null ? main.transform : hud.transform.parent;
            if (root == null) return;
            int mainMask = main != null ? main.cullingMask : 0;
            foreach (var r in root.GetComponentsInChildren<Renderer>(true))
            {
                if (!(r is MeshRenderer) && !(r is SkinnedMeshRenderer)) continue;
                int layer = r.gameObject.layer;
                if (((hud.cullingMask >> layer) & 1) == 0 || ((mainMask >> layer) & 1) != 0) continue; // drawn by the HUD camera only
                _vmRenderers.Add(r);
                // Skinned motion: the skinned vertex buffer must be bindable as a raw buffer (it is copied every frame).
                if (_vmSkinnedMotion && r is SkinnedMeshRenderer smr && (smr.vertexBufferTarget & GraphicsBuffer.Target.Raw) == 0)
                    smr.vertexBufferTarget |= GraphicsBuffer.Target.Raw;
            }
            if (_vmLogs < 3)
            {
                _vmLogs++;
                var sb = new StringBuilder($"Viewmodel renderers under {root.name}: {_vmRenderers.Count}");
                foreach (var r in _vmRenderers.Take(80))
                {
                    sb.Append($"\n  {Bench.GameControl.PathOf(r.transform)} [{r.GetType().Name}, layer {r.gameObject.layer}, {(r.enabled && r.gameObject.activeInHierarchy ? "on" : "off")}]");
                    foreach (var m in r.sharedMaterials)
                    {
                        if (m == null) continue;
                        var p = _proxies?.ClassifyMaterial(m);
                        sb.Append($"\n     '{m.name}' {(m.shader != null ? m.shader.name : "?")} q{m.renderQueue} [{string.Join(" ", m.shaderKeywords)}] " +
                                  $"tex {(m.HasProperty("_MainTex") && m.mainTexture ? m.mainTexture.name : "-")} id {(m.HasProperty("_IDTex") && m.GetTexture("_IDTex") ? m.GetTexture("_IDTex").name : "-")} -> {p}");
                    }
                }
                Logger.LogInfo(sb.ToString());
            }
        }

        private static bool VmSkipShader(Material m)
        {
            if (m == null || m.shader == null) return true;
            if (m.renderQueue >= 3000) return true; // transparents: the game draws them after our composite
            string n = m.shader.name;
            return n.IndexOf("Particle", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("Additive", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   n.IndexOf("Sprite", StringComparison.OrdinalIgnoreCase) >= 0 || n.StartsWith("UI/", StringComparison.Ordinal);
        }

        private Material ViewmodelMaterial(Material src)
        {
            if (_vmMaterials.TryGetValue(src, out var m)) return m;
            m = new Material(_vmGbShader) { name = src.name + " [UltraRT VM]" };
            var t = src.HasProperty("_MainTex") ? src.GetTexture("_MainTex") : null;
            if (t != null && t.dimension == TextureDimension.Tex2D)
            {
                m.SetTexture("_MainTex", t);
                m.SetTextureScale("_MainTex", src.GetTextureScale("_MainTex"));
                m.SetTextureOffset("_MainTex", src.GetTextureOffset("_MainTex"));
            }
            if (src.HasProperty("_Color")) m.SetColor("_Color", src.GetColor("_Color"));
            m.SetFloat("_VMVertexColors", src.IsKeywordEnabled("_VERTEXCOLORS_ON") ? 1f : 0f); // ULTRAKILL/Master tints the albedo by the vertex colour
            // Alternate / custom gun colours (GunColorGetter swaps in materials with CUSTOM_COLORS and sets _CustomColor1-3 in a
            // property block): the albedo comes from _IDTex (ViewmodelGBuffer.shader).
            var idTex = src.HasProperty("_IDTex") ? src.GetTexture("_IDTex") : null;
            if (src.IsKeywordEnabled("CUSTOM_COLORS") && idTex != null && idTex.dimension == TextureDimension.Tex2D)
            {
                m.SetTexture("_IDTex", idTex);
                m.SetFloat("_VMCustom", 1f);
            }
            if (SceneProxies.SourceAlphaTested(src))
            {
                m.SetFloat("_AlphaTest", 1f);
                m.SetFloat("_Cutoff", src.HasProperty("_Cutoff") ? src.GetFloat("_Cutoff") : 0.5f);
            }
            if (src.IsKeywordEnabled("EMISSIVE"))
            {
                var e = src.HasProperty("_EmissiveTex") ? src.GetTexture("_EmissiveTex") : null;
                if (e != null && e.dimension == TextureDimension.Tex2D) m.SetTexture("_EmissiveTex", e);
                if (src.HasProperty("_EmissiveColor")) m.SetColor("_EmissiveColor", src.GetColor("_EmissiveColor"));
                m.SetFloat("_EmissiveIntensity", src.HasProperty("_EmissiveIntensity") ? Mathf.Max(src.GetFloat("_EmissiveIntensity"), 0.0001f) : 1f);
                bool albedoAsEmissive = src.IsKeywordEnabled("_USEALBEDOASEMISSIVE_ON") || src.HasProperty("_UseAlbedoAsEmissive") && src.GetFloat("_UseAlbedoAsEmissive") > 0.5f;
                m.SetFloat("_UseAlbedoAsEmissive", albedoAsEmissive ? 1 : 0);
            }
            else if (src.IsKeywordEnabled("_EMISSION") && src.HasProperty("_EmissionColor"))
            {
                var e = src.HasProperty("_EmissionMap") ? src.GetTexture("_EmissionMap") : null;
                if (e != null && e.dimension == TextureDimension.Tex2D) m.SetTexture("_EmissiveTex", e);
                m.SetColor("_EmissiveColor", src.GetColor("_EmissionColor"));
                m.SetFloat("_EmissiveIntensity", 1f);
            }
            // ULTRAKILL/Master without VERTEX_LIGHTING is fullbright: the game never lights it, neither do we.
            bool fullbright = src.shader != null && src.shader.name == "ULTRAKILL/Master" && !src.IsKeywordEnabled("VERTEX_LIGHTING");
            m.SetFloat("_VMLit", fullbright ? 0f : 1f);
            var p = _proxies != null ? _proxies.ClassifyMaterial(src) : MaterialParams.None(SurfaceClass.Other, "none");
            if (_vmMatTable.Count == 0) { _vmMatTable.Add(new Vector4(1, 0, 0.02f, 0)); _vmMatTable.Add(Vector4.zero); } // index 0: none
            int index = _vmMatTable.Count / 2;
            _vmMatTable.Add(new Vector4(p.Roughness, p.Metalness, p.F0, p.Weight));
            _vmMatTable.Add(new Vector4((float)p.Mode, p.ReflStrength, (float)p.Class, 0f));
            _vmMatDirty = true;
            m.SetFloat("_VMMatIndex", index);
            _vmMaterials[src] = m;
            return m;
        }

        private void UploadViewmodelMaterials()
        {
            if (_vmMatTable.Count == 0) { _vmMatTable.Add(new Vector4(1, 0, 0.02f, 0)); _vmMatTable.Add(Vector4.zero); }
            if (_vmMatBuffer == null || _vmMatBuffer.count < _vmMatTable.Count)
            {
                _vmMatBuffer?.Release();
                _vmMatBuffer = new ComputeBuffer(Mathf.Max(64, Mathf.NextPowerOfTwo(_vmMatTable.Count)), 16) { name = "UltraRT Viewmodel materials" };
                _vmMatDirty = true;
            }
            if (!_vmMatDirty) return;
            _vmMatBuffer.SetData(_vmMatTable);
            _vmMatDirty = false;
        }

        /// <summary>This frame's draws: active renderers, opaque submeshes, and each one's rigid motion since last frame.</summary>
        private void CollectViewmodelDraws()
        {
            _vmDraws.Clear();
            foreach (var r in _vmRenderers)
            {
                if (r == null || !r.enabled || !r.gameObject.activeInHierarchy || r.forceRenderingOff) continue;
                Mesh mesh = r is SkinnedMeshRenderer smr ? smr.sharedMesh : r.GetComponent<MeshFilter>()?.sharedMesh;
                if (mesh == null) continue;
                var mats = r.sharedMaterials;
                Transform t = r is SkinnedMeshRenderer sk && sk.rootBone != null ? sk.rootBone : r.transform;
                int n = Mathf.Min(mats.Length, mesh.subMeshCount);
                for (int i = 0; i < n; i++)
                {
                    if (VmSkipShader(mats[i])) continue;
                    var gm = ViewmodelMaterial(mats[i]);
                    _vmDraws.Add(new VmDraw { r = r, sub = i, mat = gm, src = mats[i], t = t, custom = gm.GetFloat("_VMCustom") > 0.5f, baseVertex = (int)mesh.GetBaseVertex(i) });
                }
            }
        }

        private Rect ViewmodelRect(Camera hud, int w, int h)
        {
            if (_vmFullScreen) return new Rect(0, 0, w, h);
            // Corners are projected in view space; one behind the near plane is pulled onto it (conservative: it moves
            // outwards on screen), since skinned arm bounds often contain the camera.
            var v = hud.worldToCameraMatrix;
            var proj = hud.projectionMatrix;
            float near = hud.nearClipPlane;
            float x0 = float.MaxValue, y0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue;
            Renderer last = null;
            foreach (var d in _vmDraws)
            {
                if (d.r == last) continue;
                last = d.r;
                var b = d.r.bounds;
                for (int c = 0; c < 8; c++)
                {
                    var p = new Vector3((c & 1) != 0 ? b.max.x : b.min.x, (c & 2) != 0 ? b.max.y : b.min.y, (c & 4) != 0 ? b.max.z : b.min.z);
                    var vp = v.MultiplyPoint3x4(p);
                    if (-vp.z < near) vp.z = -near;
                    var c4 = proj * new Vector4(vp.x, vp.y, vp.z, 1f);
                    float sxp = (c4.x / c4.w * 0.5f + 0.5f) * w, syp = (c4.y / c4.w * 0.5f + 0.5f) * h;
                    x0 = Mathf.Min(x0, sxp); x1 = Mathf.Max(x1, sxp);
                    y0 = Mathf.Min(y0, syp); y1 = Mathf.Max(y1, syp);
                }
            }
            const float pad = 8f;
            x0 = Mathf.Clamp(Mathf.Floor(x0 - pad), 0, w); x1 = Mathf.Clamp(Mathf.Ceil(x1 + pad), 0, w);
            y0 = Mathf.Clamp(Mathf.Floor(y0 - pad), 0, h); y1 = Mathf.Clamp(Mathf.Ceil(y1 + pad), 0, h);
            // The renderer bounds are loose (skinned arms reach behind the camera, so this is often the whole screen).
            // Narrow them with the GPU-measured bounds of the last frames, padded for motion. If an edge touched during
            // rapid recoil/punch animation, expand only the touching edge(s) by 160 px instead of snapping straight to full screen.
            if (_vmGpuBoundsValid && _vmFrame - _vmGpuBoundsFrame <= 8u && _vmGpuBounds.width > 0)
            {
                var g = _vmGpuBounds;
                var dr = _vmGpuDispatch;
                int hw = (w + 1) / 2, hh = (h + 1) / 2;
                const float motionPad = 64f, touchPad = 160f;
                float padL = (g.xMin <= dr.xMin + 1 && dr.xMin > 0) ? touchPad : motionPad;
                float padB = (g.yMin <= dr.yMin + 1 && dr.yMin > 0) ? touchPad : motionPad;
                float padR = (g.xMax >= dr.xMax - 1 && dr.xMax < hw) ? touchPad : motionPad;
                float padT = (g.yMax >= dr.yMax - 1 && dr.yMax < hh) ? touchPad : motionPad;
                x0 = Mathf.Max(x0, g.xMin * 2f - padL); x1 = Mathf.Min(x1, g.xMax * 2f + padR);
                y0 = Mathf.Max(y0, g.yMin * 2f - padB); y1 = Mathf.Min(y1, g.yMax * 2f + padT);
                x0 = Mathf.Clamp(Mathf.Floor(x0), 0, w); x1 = Mathf.Clamp(Mathf.Ceil(x1), 0, w);
                y0 = Mathf.Clamp(Mathf.Floor(y0), 0, h); y1 = Mathf.Clamp(Mathf.Ceil(y1), 0, h);
            }
            // Even origin: the half-res grid starts on a 2x2 block.
            x0 -= x0 % 2; y0 -= y0 % 2;
            return x1 > x0 && y1 > y0 ? new Rect(x0, y0, x1 - x0, y1 - y0) : new Rect(0, 0, 0, 0);
        }

        /// <summary>Lights reaching the viewmodel: layer 13 (the game's own choice) plus the layer under the player, so the
        /// sun (Outdoors-only mask) lights the gun outdoors; indoors the RT shadow keeps it out.</summary>
        private int ViewmodelLightMask(Camera hud)
        {
            if (_vmLightMask != 0) return _vmLightMask;
            if (Physics.Raycast(hud.transform.position, Vector3.down, out var hit, 6f, ~((1 << 13) | (1 << 2)), QueryTriggerInteraction.Ignore))
            {
                int l = hit.collider.gameObject.layer;
                _vmGroundLayer = l == 6 ? 24 : l == 7 ? 8 : l; // baked layers map back to their originals
            }
            return (1 << 13) | (1 << _vmGroundLayer);
        }

        // ---------------------------------------------------------------- targets

        private static void VmTex(ref RenderTexture rt, int w, int h, RenderTextureFormat f, string name, bool uav)
        {
            if (rt != null && rt.width == w && rt.height == h && rt.format == f) return;
            if (rt != null) rt.Release();
            rt = new RenderTexture(w, h, 0, f, RenderTextureReadWrite.Linear) { enableRandomWrite = uav, filterMode = FilterMode.Point, name = name };
            rt.Create();
        }

        private void EnsureViewmodelTargets(RenderTexture gameDepth)
        {
            int w = gameDepth.width, h = gameDepth.height;
            bool resized = _vmGB0 == null || _vmGB0.width != w || _vmGB0.height != h;
            VmTex(ref _vmGB0, w, h, RenderTextureFormat.ARGB32, "UltraRT VM albedo", false);
            VmTex(ref _vmGB1, w, h, RenderTextureFormat.ARGBHalf, "UltraRT VM normal", false);
            VmTex(ref _vmGB2, w, h, RenderTextureFormat.ARGBHalf, "UltraRT VM position", false);
            VmTex(ref _vmGB3, w, h, RenderTextureFormat.RGB111110Float, "UltraRT VM emission", false);
            VmTex(ref _vmGB4, w, h, RenderTextureFormat.ARGBHalf, "UltraRT VM motion", false);
            if (_vmDepth == null || _vmDepth.width != w || _vmDepth.height != h)
            {
                if (_vmDepth != null) _vmDepth.Release();
                _vmDepth = new RenderTexture(w, h, 24, RenderTextureFormat.Depth) { name = "UltraRT VM depth" };
                _vmDepth.Create();
            }
            int hw = (w + 1) / 2, hh = (h + 1) / 2;
            VmTex(ref _vmRawLight, hw, hh, RenderTextureFormat.ARGBHalf, "UltraRT VM light", true);
            VmTex(ref _vmRawDirect, hw, hh, RenderTextureFormat.RGHalf, "UltraRT VM direct", true);
            VmTex(ref _vmRawRefl, hw, hh, RenderTextureFormat.ARGBHalf, "UltraRT VM reflection", true);
            VmTex(ref _vmRawGeo, hw, hh, RenderTextureFormat.ARGBHalf, "UltraRT VM geo", true);
            for (int i = 0; i < 2; i++)
            {
                VmTex(ref _vmHLight[i], hw, hh, RenderTextureFormat.ARGBHalf, "UltraRT VM light history", true);
                VmTex(ref _vmHDirect[i], hw, hh, RenderTextureFormat.ARGBHalf, "UltraRT VM direct history", true);
                VmTex(ref _vmHRefl[i], hw, hh, RenderTextureFormat.ARGBHalf, "UltraRT VM reflection history", true);
                VmTex(ref _vmHGeo[i], hw, hh, RenderTextureFormat.RGHalf, "UltraRT VM geo history", true);
            }
            if (resized) { _vmNeedsClear = true; _vmHistValid = false; }
            _vmMrt[0] = _vmGB0; _vmMrt[1] = _vmGB1; _vmMrt[2] = _vmGB2; _vmMrt[3] = _vmGB3; _vmMrt[4] = _vmGB4;
        }

        // ---------------------------------------------------------------- the pass

        private void RecordViewmodel(Camera hud, PostProcessV2_Handler pp)
        {
            var color = pp.mainTex;
            var depth = pp.depthBuffer;
            if (color == null || depth == null) return;
            if (Time.realtimeSinceStartup >= _vmNextRefresh)
            {
                _vmNextRefresh = Time.realtimeSinceStartup + 1f; // weapons are switched on and off, rarely created
                RefreshViewmodelRenderers(hud);
            }
            CollectViewmodelDraws();
            int drawKey = 17;
            foreach (var d in _vmDraws) drawKey = drawKey * 31 + (d.src != null ? d.src.GetInstanceID() : 0);
            if (drawKey != _vmDrawKey && _vmDrawLogs < 30)
            {
                _vmDrawKey = drawKey;
                _vmDrawLogs++;
                Logger.LogInfo("Viewmodel draws: " + string.Join(", ", _vmDraws.Select(d => $"{d.r.name}[{d.sub}] '{d.src.name}'{(d.custom ? " custom colours" : "")} {_proxies?.ClassifyMaterial(d.src)}")));
            }
            if (_vmDraws.Count == 0) { _vmHistValid = false; return; }
            EnsureViewmodelTargets(depth);
            UploadViewmodelMaterials();
            int w = depth.width, h = depth.height, hw = (w + 1) / 2, hh = (h + 1) / 2;
            var rect = ViewmodelRect(hud, w, h);
            if (rect.width <= 0 || rect.height <= 0) { _vmHistValid = false; return; }
            int hx = (int)rect.x / 2, hy = (int)rect.y / 2;
            int hrw = Mathf.Min(hw - hx, Mathf.CeilToInt(rect.width / 2f) + 1), hrh = Mathf.Min(hh - hy, Mathf.CeilToInt(rect.height / 2f) + 1);

            _vmFrame++;
            float stamp = _vmFrame % 2000u + 1u;
            _vmLastStamp = stamp;
            _vmLastStampFrame = Time.frameCount;
            var cmd = _vmCmd;
            _gpu?.Begin(cmd, SlotViewmodel);

            // 1. G-buffer with the HUD camera's matrices. Never cleared (stamps), except every 1000 frames.
            _gpu?.Begin(cmd, SlotVmGBuffer);
            cmd.SetRenderTarget(_vmMrt, _vmDepth.depthBuffer);
            cmd.ClearRenderTarget(true, false, Color.clear);
            if (_vmNeedsClear || _vmFrame % 1000u == 0u)
            {
                cmd.DrawProcedural(Matrix4x4.identity, _vmClearMat, 1, MeshTopology.Triangles, 3);
                _vmNeedsClear = false;
            }
            cmd.SetGlobalFloat(VmStampId, stamp);
            var w2c = hud.worldToCameraMatrix;
            Renderer lastR = null;
            Matrix4x4 delta = Matrix4x4.identity;
            VmSkin skin = null;
            bool skinPrev = false;
            BeginViewmodelSkins();
            foreach (var d in _vmDraws)
            {
                if (d.r != lastR)
                {
                    lastR = d.r;
                    var mv = w2c * d.t.localToWorldMatrix;
                    bool hadPrev = _vmPrevMV.TryGetValue(d.r, out var prev);
                    delta = _vmHistValid && hadPrev ? prev * mv.inverse : Matrix4x4.identity;
                    _vmPrevMV[d.r] = mv;
                    cmd.SetGlobalMatrix(VmPrevDeltaId, delta);
                    // Skinned: last frame's skinned positions (this renderer was drawn, and copied, on the previous frame).
                    skin = d.r is SkinnedMeshRenderer smr ? ViewmodelSkin(smr) : null;
                    skinPrev = skin != null && skin.prev != null && _vmHistValid && hadPrev && skin.copiedFrame == _vmFrame - 1u;
                    cmd.SetGlobalBuffer(VmPrevVertsId, skinPrev ? skin.prev : ViewmodelDummyVerts());
                }
                cmd.SetGlobalVector(VmPrevSkinId, skinPrev ? new Vector4(1f, skin.stride, d.baseVertex, skin.posOffset) : Vector4.zero);
                if (d.custom)
                {
                    // The colours live in the renderer's property block (falling back to the material's own values).
                    _vmMpb ??= new MaterialPropertyBlock();
                    d.r.GetPropertyBlock(_vmMpb);
                    foreach (int id in VmCustomIds)
                        cmd.SetGlobalColor(id, !_vmMpb.isEmpty && _vmMpb.HasColor(id) ? _vmMpb.GetColor(id) : d.src.HasProperty(id) ? d.src.GetColor(id) : Color.white);
                }
                cmd.DrawRenderer(d.r, d.mat, d.sub, 0);
            }
            // This frame's skinned positions become next frame's "previous" ones (after the draws that read them).
            CopyViewmodelSkins(cmd);

            _gpu?.End(cmd, SlotVmGBuffer);
            // 3. Rays.
            _gpu?.Begin(cmd, SlotVmRays);
            var rt = _vmRt;
            rt.SetShaderPass("UltraRTHit");
            cmd.SetRayTracingAccelerationStructure(rt, "_Scene", _proxies.Rtas);
            cmd.SetRayTracingTextureParam(rt, "_VMLightOut", _vmRawLight);
            cmd.SetRayTracingTextureParam(rt, "_VMDirectOut", _vmRawDirect);
            cmd.SetRayTracingTextureParam(rt, "_VMReflOut", _vmRawRefl);
            cmd.SetRayTracingTextureParam(rt, "_VMGeoOut", _vmRawGeo);
            cmd.SetRayTracingTextureParam(rt, "_VMGB0", _vmGB0);
            cmd.SetRayTracingTextureParam(rt, "_VMGB1", _vmGB1);
            cmd.SetRayTracingTextureParam(rt, "_VMGB2", _vmGB2);
            cmd.SetRayTracingBufferParam(rt, "_VMMaterials", _vmMatBuffer);
            cmd.SetRayTracingVectorParam(rt, "_VMCamPos", hud.transform.position);
            cmd.SetRayTracingVectorParam(rt, "_CamPos", _camera != null ? _camera.transform.position : hud.transform.position);
            cmd.SetRayTracingVectorParam(rt, "_VMRect", new Vector4(hx, hy, w, h));
            cmd.SetRayTracingFloatParam(rt, "_VMStamp", stamp);
            cmd.SetRayTracingIntParam(rt, "_VMLightMask", ViewmodelLightMask(hud));
            cmd.SetRayTracingIntParam(rt, "_VMDirectSamples", Mathf.Clamp(_vmSamples, 1, 4));
            cmd.SetRayTracingIntParam(rt, "_VMCheckerboard", _vmCheckerboard ? 1 : 0);
            cmd.SetRayTracingIntParam(rt, "_VMStableRefl", _vmStableRefl ? 1 : 0);
            cmd.SetRayTracingFloatParam(rt, "_VMNdlPow", Mathf.Max(1f, _vmNdlPow));
            cmd.SetRayTracingIntParam(rt, "_FrameIndex", (int)_frameIndex);
            cmd.SetRayTracingBufferParam(rt, "_Lights", _lightBuffer);
            cmd.SetRayTracingBufferParam(rt, "_LightMasks", _lightMaskBuffer);
            cmd.SetRayTracingIntParam(rt, "_LightCount", _surfLightCount);
            cmd.SetRayTracingFloatParam(rt, "_LightRadius", _lightRadius);
            cmd.SetRayTracingVectorParam(rt, "_Relight", RelightParams);
            cmd.SetRayTracingFloatParam(rt, "_FixtureShadows", _fixtureShadows ? 1f : 0f);
            _lightSampling.BindTables(cmd, rt);
            // More RIS candidates than the cache's 8: the viewmodel has no history to lean on, and candidates are
            // light evaluations only (no rays) over a few thousand half-res pixels.
            if (_vmCandidates > 0) cmd.SetRayTracingIntParam(rt, "_RisCandidates", Mathf.Clamp(_vmCandidates, 1, 64));
            _emissive.Bind(cmd, rt, _lightSamplingSettings.mode);
            _radianceCache.BindLookupOn(cmd, rt);
            var ambient = RenderSettings.ambientLight;
            cmd.SetRayTracingVectorParam(rt, "_SkyColor", new Vector4(ambient.r, ambient.g, ambient.b, 1));
            var mainCmd = _cmd;
            _cmd = cmd; // BindSky records into _cmd
            try { BindSky(rt); }
            finally { _cmd = mainCmd; }
            cmd.SetRayTracingVectorParam(rt, "_Ambient", new Vector4(ambient.r, ambient.g, ambient.b, 1) * 2f);
            cmd.SetRayTracingFloatParam(rt, "_PrimaryAmbient", _primaryAmbient);
            cmd.SetRayTracingFloatParam(rt, "_BounceAmbient", 1f);
            cmd.SetRayTracingFloatParam(rt, "_LightScale", 1f);
            cmd.SetRayTracingFloatParam(rt, "_EmissionScale", 1f);
            cmd.SetRayTracingVectorParam(rt, "_ReflParams", _reflSettings.ShaderParams);
            cmd.SetRayTracingFloatParam(rt, "_ReflMaxRadiance", Mathf.Max(0.01f, _reflSettings.maxRadiance));
            cmd.SetRayTracingVectorParam(rt, "_ReflCache", _reflSettings.CacheParams);
            cmd.DispatchRays(rt, "RayGen", _vmRayTest == 1 ? 1u : (uint)hrw, _vmRayTest == 1 ? 1u : (uint)hrh, 1);
            _gpu?.End(cmd, SlotVmRays);
            _gpu?.Begin(cmd, SlotVmFilter);

            // 4. Filter: temporal (raw + previous history -> current history), then a-trous ping-pong through the raw
            // textures and the previous history set, which are free by then.
            int cur = _vmHist ^ 1, prv = _vmHist;
            var cs = _vmFilter;
            var fwd = hud.transform.forward;
            cmd.SetComputeVectorParam(cs, "_Rect", new Vector4(hx, hy, hrw, hrh));
            cmd.SetComputeVectorParam(cs, "_HalfDim", new Vector4(hw, hh, w, h));
            cmd.SetComputeVectorParam(cs, "_CamFwd", fwd);
            cmd.SetComputeVectorParam(cs, "_Stamps", new Vector4(stamp, _vmPrevStamp, 0, 0));
            // Lights appeared or disappeared (muzzle flashes, explosions): keep only a few frames of history.
            cmd.SetComputeVectorParam(cs, "_Params2", new Vector4(_lightSampling.LightSetChanged ? 1f : 0f, Mathf.Max(0f, _vmFastSigma), Mathf.Max(0f, _vmFastTol), Mathf.Max(0f, _vmFastTolChange)));
            bool stats = VmStatsOn;
            cmd.SetComputeVectorParam(cs, "_Params3", new Vector4(1f, stats ? 1f : 0f, 0, 0)); // x: 3x3 history search when the bilinear taps fail (edges of an idling gun)
            _vmStatsBuf ??= new ComputeBuffer(4, 4) { name = "UltraRT VM stats" };
            cmd.SetComputeBufferParam(cs, _vmKTemporal, "_VMStats", _vmStatsBuf);
            if (stats) cmd.SetBufferData(_vmStatsBuf, VmStatsZero);
            cmd.SetComputeVectorParam(cs, "_Params", new Vector4(Mathf.Max(1f, _vmMaxHistory), Mathf.Max(1f, _vmMaxHistoryRefl), Mathf.Max(0.5f, _vmClampK), _vmHistValid ? 1f : 0f));
            int k = _vmKTemporal;
            cmd.SetComputeTextureParam(cs, k, "_RawLight", _vmRawLight);
            cmd.SetComputeTextureParam(cs, k, "_RawDirect", _vmRawDirect);
            cmd.SetComputeTextureParam(cs, k, "_RawRefl", _vmRawRefl);
            cmd.SetComputeTextureParam(cs, k, "_RawGeo", _vmRawGeo);
            cmd.SetComputeTextureParam(cs, k, "_HistLight", _vmHLight[prv]);
            cmd.SetComputeTextureParam(cs, k, "_HistDirect", _vmHDirect[prv]);
            cmd.SetComputeTextureParam(cs, k, "_HistRefl", _vmHRefl[prv]);
            cmd.SetComputeTextureParam(cs, k, "_HistGeo", _vmHGeo[prv]);
            cmd.SetComputeTextureParam(cs, k, "_VMGB4", _vmGB4);
            cmd.SetComputeTextureParam(cs, k, "_OutLight", _vmHLight[cur]);
            cmd.SetComputeTextureParam(cs, k, "_OutDirect", _vmHDirect[cur]);
            cmd.SetComputeTextureParam(cs, k, "_OutRefl", _vmHRefl[cur]);
            cmd.SetComputeTextureParam(cs, k, "_OutGeo", _vmHGeo[cur]);
            int gx = (hrw + 7) / 8, gy = (hrh + 7) / 8;
            cmd.DispatchCompute(cs, k, gx, gy, 1);
            if (stats)
                cmd.RequestAsyncReadback(_vmStatsBuf, r =>
                {
                    if (r.hasError) return;
                    var d = r.GetData<uint>();
                    for (int i = 0; i < 4; i++) VmStatTotals[i] += d[i];
                });
            RenderTexture fl = _vmHLight[cur], fd = _vmHDirect[cur], fr = _vmHRefl[cur];
            int iters = Mathf.Clamp(_vmAtrous, 0, 3);
            for (int it = 0; it < iters; it++)
            {
                // Even iterations write the previous history set, odd ones the raw samples (raw geo stays: the composite reads it).
                bool even = (it & 1) == 0;
                var outL = even ? _vmHLight[prv] : _vmRawLight;
                var outD = even ? _vmHDirect[prv] : _vmRawDirect;
                var outR = even ? _vmHRefl[prv] : _vmRawRefl;
                k = _vmKAtrous;
                cmd.SetComputeVectorParam(cs, "_Atrous", new Vector4(1 << it, 0.1f, 0, 0));
                cmd.SetComputeTextureParam(cs, k, "_RawGeo", _vmRawGeo);
                cmd.SetComputeTextureParam(cs, k, "_VMGB1", _vmGB1);
                cmd.SetComputeBufferParam(cs, k, "_VMMaterials", _vmMatBuffer);
                cmd.SetComputeTextureParam(cs, k, "_InLight", fl);
                cmd.SetComputeTextureParam(cs, k, "_InDirect", fd);
                cmd.SetComputeTextureParam(cs, k, "_InRefl", fr);
                cmd.SetComputeTextureParam(cs, k, "_OutLight", outL);
                cmd.SetComputeTextureParam(cs, k, "_OutDirect", outD);
                cmd.SetComputeTextureParam(cs, k, "_OutRefl", outR);
                cmd.DispatchCompute(cs, k, gx, gy, 1);
                fl = outL; fd = outD; fr = outR;
            }
            // Bounds of this frame's viewmodel pixels, read back for the next frames' rectangle.
            _vmBoundsBuf ??= new ComputeBuffer(4, 4) { name = "UltraRT VM bounds" };
            cmd.SetBufferData(_vmBoundsBuf, VmBoundsInit);
            cmd.SetComputeTextureParam(cs, _vmKBounds, "_RawGeo", _vmRawGeo);
            cmd.SetComputeBufferParam(cs, _vmKBounds, "_Bounds", _vmBoundsBuf);
            cmd.DispatchCompute(cs, _vmKBounds, gx, gy, 1);
            uint issued = _vmFrame;
            var dispatched = new RectInt(hx, hy, hrw, hrh);
            cmd.RequestAsyncReadback(_vmBoundsBuf, r =>
            {
                if (r.hasError || issued < _vmGpuBoundsFrame) return;
                var b = r.GetData<uint>();
                _vmGpuBoundsFrame = issued;
                _vmGpuDispatch = dispatched;
                _vmGpuBounds = b[2] > 0 ? new RectInt((int)b[0], (int)b[1], (int)(b[2] - b[0]), (int)(b[3] - b[1])) : new RectInt(0, 0, 0, 0);
                _vmGpuBoundsValid = true;
            });
            _gpu?.End(cmd, SlotVmFilter);
            _vmHist = cur;
            _vmPrevStamp = stamp;
            _vmHistValid = true;

            // 5. Composite into the game's colour buffer (the HUD camera's target), under the viewmodel's rectangle.
            cmd.SetGlobalTexture(VmGB0Id, _vmGB0);
            cmd.SetGlobalTexture(VmGB1Id, _vmGB1);
            cmd.SetGlobalTexture(VmGB2Id, _vmGB2);
            cmd.SetGlobalTexture(VmGB3Id, _vmGB3);
            cmd.SetGlobalTexture(VmLightId, fl);
            cmd.SetGlobalTexture(VmReflId, fr);
            cmd.SetGlobalTexture(VmGeoId, _vmRawGeo);
            // The game's depth (its viewmodel drawn) is read directly: the composite binds only the colour target.
            cmd.SetGlobalTexture(VmGameDepthId, depth, RenderTextureSubElement.Depth);
            var mat = _vmCompositeMat;
            mat.SetBuffer(VmMaterialsId, _vmMatBuffer);
            var exposure = _autoExposure?.ExposureBuffer;
            if (exposure == null)
            {
                _vmDummyExposure ??= new ComputeBuffer(4, 16) { name = "UltraRT VM dummy exposure" };
                exposure = _vmDummyExposure;
            }
            mat.SetBuffer(VmExposureId, exposure);
            mat.SetInt(VmDebugId, _vmDebug);
            mat.SetFloat(VmStampId, stamp);
            mat.SetVector(VmCamFwdId, fwd);
            mat.SetVector(VmParamsId, new Vector4(Mathf.Max(0f, _vmReflGraze), 0f, Mathf.Max(0.001f, _vmDepthTol), 1f));
            mat.SetVector(VmRefl2Id, new Vector4(_reflSettings.specWeight, _reflSettings.reflStrength, _reflSettings.enabled ? 1f : 0f, 0f));
            _gpu?.Begin(cmd, SlotVmComposite);
            cmd.SetRenderTarget(color.colorBuffer);
            cmd.EnableScissorRect(rect);
            var hdrLate = HdrLateForViewmodel(mat);
            if (HdrNativeForViewmodel(mat)) cmd.DrawProcedural(Matrix4x4.identity, mat, 2, MeshTopology.Triangles, 3); // pass 2: native float HDR
            else if (hdrLate != null)
            {
                // The gun's glowing parts into the HDR late map the screen-effects pass wrote this frame (docs/hdr.md).
                cmd.SetRandomWriteTarget(1, hdrLate);
                cmd.DrawProcedural(Matrix4x4.identity, mat, 1, MeshTopology.Triangles, 3); // pass 1: replace + HDR gain
                cmd.ClearRandomWriteTargets();
            }
            else cmd.DrawProcedural(Matrix4x4.identity, mat, 0, MeshTopology.Triangles, 3);
            cmd.DisableScissorRect();
            cmd.SetRenderTarget(color.colorBuffer, depth.depthBuffer); // the HUD camera's own targets, for its transparents
            _gpu?.End(cmd, SlotVmComposite);
            _gpu?.End(cmd, SlotViewmodel);

            if (Time.realtimeSinceStartup >= _vmNextLog)
            {
                _vmNextLog = Time.realtimeSinceStartup + 20f;
                Logger.LogInfo($"Viewmodel RT: {_vmDraws.Count} draws ({_vmRenderers.Count} renderers, {_vmMaterials.Count} materials), rect {rect.x}x{rect.y} {rect.width}x{rect.height} of {w}x{h} " +
                               $"({100f * rect.width * rect.height / (w * h):F0}%), light mask 0x{ViewmodelLightMask(hud):X8} (ground layer {_vmGroundLayer}), mode {_vmTraceMode}");
            }
            if (Time.realtimeSinceStartup >= _vmNextLightLog && _vmLightLogs < 10)
            {
                _vmNextLightLog = Time.realtimeSinceStartup + 2f;
                LogViewmodelLights(hud);
            }
        }
    
        // ---------------------------------------------------------------- skinned motion

        private void BeginViewmodelSkins()
        {
            // Unity's buffer wrappers from last frame: their command buffer has run by now.
            foreach (var b in _vmDisposeNext) b?.Dispose();
            _vmDisposeNext.Clear();
            foreach (var s in _vmSkins.Values) s.cur = null;
        }

        private GraphicsBuffer ViewmodelDummyVerts()
        {
            _vmDummyVerts ??= new GraphicsBuffer(GraphicsBuffer.Target.Raw, 4, 4) { name = "UltraRT VM dummy verts" };
            return _vmDummyVerts;
        }

        /// <summary>This frame's skinned vertex buffer of <paramref name="smr"/> and the copy of last frame's, or null (rigid
        /// motion only) when skinned motion is off or Unity has no raw buffer for it.</summary>
        private VmSkin ViewmodelSkin(SkinnedMeshRenderer smr)
        {
            if (!_vmSkinnedMotion || _vmKCopyVerts < 0 || smr.sharedMesh == null) return null;
            if (!_vmSkins.TryGetValue(smr, out var s)) _vmSkins[smr] = s = new VmSkin();
            if (s.cur != null) return s;
            if ((smr.vertexBufferTarget & GraphicsBuffer.Target.Raw) == 0) smr.vertexBufferTarget |= GraphicsBuffer.Target.Raw;
            GraphicsBuffer cur;
            try { cur = smr.GetVertexBuffer(); }
            catch (Exception e) { cur = null; if (_vmSkinLogs++ < 5) Logger.LogWarning($"Viewmodel skinned motion: {smr.name}: {e.Message}"); }
            if (cur == null) { s.copiedFrame = 0; return null; }
            _vmDisposeNext.Add(cur);
            // The raw flag takes effect when Unity next (re)creates the buffer: until then it can't be bound as a raw
            // buffer (doing so crashed D3D12 on the first frame of a new level).
            if ((cur.target & GraphicsBuffer.Target.Raw) == 0) { s.copiedFrame = 0; return null; }
            var mesh = smr.sharedMesh;
            int bytes = cur.count * cur.stride, n = mesh.vertexCount;
            if (n <= 0 || bytes % n != 0 || bytes / n < 12 || (bytes & 3) != 0)
            {
                if (!s.logged) { s.logged = true; Logger.LogWarning($"Viewmodel skinned motion: {smr.name}: buffer {cur.count}x{cur.stride} for {n} vertices, not used"); }
                s.copiedFrame = 0;
                return null;
            }
            int posOffset = mesh.GetVertexAttributeStream(VertexAttribute.Position) == 0 ? mesh.GetVertexAttributeOffset(VertexAttribute.Position) : 0;
            if (s.prev == null || s.bytes != bytes)
            {
                s.prev?.Release();
                s.prev = new GraphicsBuffer(GraphicsBuffer.Target.Raw, bytes / 4, 4) { name = "UltraRT VM prev skin " + smr.name };
                s.copiedFrame = 0;
            }
            s.cur = cur; s.bytes = bytes; s.stride = bytes / n; s.posOffset = posOffset; s.vertexCount = n;
            if (!s.logged)
            {
                s.logged = true;
                Logger.LogInfo($"Viewmodel skinned motion: {Bench.GameControl.PathOf(smr.transform)}: {n} vertices, {s.stride} bytes each (mesh stream 0: " +
                               $"{mesh.GetVertexBufferStride(0)}), position at {posOffset}, buffer {cur.count}x{cur.stride} {cur.target}, root bone {(smr.rootBone != null ? smr.rootBone.name : "-")}");
            }
            return s;
        }

        /// <summary>After the G-buffer draws: this frame's skinned positions into the "previous" copies.</summary>
        private void CopyViewmodelSkins(CommandBuffer cmd)
        {
            if (_vmKCopyVerts < 0) return;
            foreach (var s in _vmSkins.Values)
            {
                if (s.cur == null || s.prev == null) continue;
                int words = s.bytes / 4;
                cmd.SetComputeBufferParam(_vmFilter, _vmKCopyVerts, "_CopySrc", s.cur);
                cmd.SetComputeBufferParam(_vmFilter, _vmKCopyVerts, "_CopyDst", s.prev);
                cmd.SetComputeIntParam(_vmFilter, "_CopyWords", words);
                cmd.DispatchCompute(_vmFilter, _vmKCopyVerts, (words + 255) / 256, 1, 1);
                s.copiedFrame = _vmFrame;
            }
        }

        private void ReleaseViewmodelSkins()
        {
            foreach (var s in _vmSkins.Values) s.prev?.Release();
            _vmSkins.Clear();
            foreach (var b in _vmDisposeNext) b?.Dispose();
            _vmDisposeNext.Clear();
        }

        // ---------------------------------------------------------------- diagnostics

        /// <summary>Bench (motion noise): count history hits and misses in the temporal filter into <see cref="VmStatTotals"/>
        /// (viewmodel pixels with a history to look for, bilinear hits, 3x3 search hits, misses).</summary>
        internal bool VmStatsOn;
        internal readonly long[] VmStatTotals = new long[4];
        private ComputeBuffer _vmStatsBuf;
        private static readonly uint[] VmStatsZero = new uint[4];

        private float _vmNextLightLog;
        private int _vmLightLogs;
        private float _vmLastStamp;
        private int _vmLastStampFrame = -1, _vmCoverageLogs;

        /// <summary>Bench (motion noise): the screen pixels whose viewmodel G-buffer was written this frame, bottom-up like
        /// GetPixels32; null when the pass didn't run this frame. Synchronous readback of the position target (bench only).</summary>
        private bool[] ViewmodelCoverage(int w, int h)
        {
            if (_vmLastStampFrame != Time.frameCount || _vmGB2 == null || _vmGB2.width != w || _vmGB2.height != h) return null;
            var req = AsyncGPUReadback.Request(_vmGB2);
            req.WaitForCompletion();
            if (req.hasError) return null;
            var data = req.GetData<ushort>();
            if (data.Length < w * h * 4) return null;
            var cov = new bool[w * h];
            long lower = 0, upper = 0;
            for (int i = 0; i < w * h; i++)
            {
                if (Mathf.HalfToFloat(data[i * 4 + 3]) != _vmLastStamp) continue;
                cov[i] = true;
                if (i < w * h / 2) lower++; else upper++;
            }
            // Row order: the gun is at the bottom of the screen, which GetPixels32 puts first. Readbacks of render
            // textures are expected in that order already; flip if they are not.
            bool flip = upper > lower;
            if (flip)
            {
                var f = new bool[w * h];
                for (int y = 0; y < h; y++) Array.Copy(cov, (h - 1 - y) * w, f, y * w, w);
                cov = f;
            }
            if (_vmCoverageLogs++ < 2) Logger.LogInfo($"Viewmodel coverage readback: {lower + upper} px ({(lower + upper) * 100.0 / (w * h):F1}%), rows {(flip ? "flipped" : "as read")}");
            return cov;
        }

        /// <summary>The strongest lights reaching the viewmodel at the camera (by the game's own falloff), with what decides how
        /// bright they make the gun: distance, range, spot cone, culling mask (layer 13 = the game lights its own gun with
        /// it), player-light flag.</summary>
        private void LogViewmodelLights(Camera hud)
        {
            int vmMask = ViewmodelLightMask(hud);
            var p = hud.transform.position;
            var rows = new List<KeyValuePair<float, string>>();
            for (int i = 0; i < _lightCount; i++)
            {
                uint m = _lightMasks[i];
                if ((m & (uint)vmMask) == 0u) continue;
                var l0 = _lightData[i * 4]; var l1 = _lightData[i * 4 + 1]; var l2 = _lightData[i * 4 + 2]; var l3 = _lightData[i * 4 + 3];
                bool own = l1.w - 2f * Mathf.Floor(l1.w * 0.5f) > 0.75f; // the player-light flag, as LightPlayerFlag
                if (own && ((m >> 13) & 1u) == 0u) continue; // as Viewmodel.raytrace: not on the gun
                float game = 1f, d = 0f, cone = 1f;
                if (l0.w != 0f)
                {
                    var toP = p - new Vector3(l0.x, l0.y, l0.z);
                    d = toP.magnitude;
                    float r = l0.w;
                    game = 1f / (1f + 25f * d * d / (r * r)) * Mathf.Clamp01(2f - d / r);
                    if (l3.x > -0.5f && d > 1e-4f) cone = Mathf.Clamp01((Vector3.Dot(toP / d, new Vector3(l2.x, l2.y, l2.z)) - l3.x) * l3.y);
                }
                float lum = 0.2126f * l1.x + 0.7152f * l1.y + 0.0722f * l1.z;
                float s = lum * game * cone;
                if (s < 0.02f) continue;
                var l = _packedLights[i];
                string name = !ReferenceEquals(l, null) && l != null ? Bench.GameControl.PathOf(l.transform) + $" [{l.type}, angle {(l.type == LightType.Spot ? l.spotAngle : 0f):F0}]" : "(virtual)";
                rows.Add(new KeyValuePair<float, string>(s, $"{name}: colour x intensity {l1.x:F2},{l1.y:F2},{l1.z:F2} range {l0.w:F1} dist {d:F2} " +
                    $"game falloff {game:F2} cone {cone:F2} mask 0x{m:X8}{(((m >> 13) & 1u) != 0u ? " (lights layer 13)" : "")} player {(own ? 1 : 0)} fill {l3.z:F2}"));
            }
            if (rows.Count == 0) return;
            _vmLightLogs++;
            var sb = new StringBuilder($"Viewmodel lights at the camera (mask 0x{vmMask:X8}, mode {_vmTraceMode}):");
            foreach (var r in rows.OrderByDescending(x => x.Key).Take(8)) sb.Append($"\n  {r.Key:F2} {r.Value}");
            Logger.LogInfo(sb.ToString());
        }
    }
}
