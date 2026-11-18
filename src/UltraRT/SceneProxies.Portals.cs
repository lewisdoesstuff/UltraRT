using System;
using System.Collections.Generic;
using System.Reflection;
using ULTRAKILL.Portal;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace UltraRT
{
    /// <summary>
    /// Portals (layer 8's non-euclidean spaces, and portal mirrors) in the RTAS, so rays teleport through them like the
    /// game's portal cameras see (docs/portals.md).
    ///
    /// Each rendered portal side becomes one quad instance (shader UltraRT/Portal, instance mask <see cref="PortalMask"/>)
    /// at the side's transform, sized like the portal's PlaneShape. Its material carries the side's index; the side's
    /// travel matrix is row <c>3 * index</c> of the global <c>_UltraRTPortals</c> buffer. The ray generation shaders
    /// (UltraRTPortals.hlsl, TraceScene) move a ray that hits a side through that matrix and trace on. Shadow rays test
    /// 0x05 only, so portals never block light.
    ///
    /// The set is read from the game every frame (PortalManagerV2's private portal list) with the rules of
    /// NativePortalScene.Create and PortalRenderV2.RebuildMesh: the portal and both ends active, the player within
    /// disableRange, the side rendered (renderSettings), and only the enter side of a mirror.
    /// </summary>
    internal sealed partial class SceneProxies
    {
        /// <summary>Portal quads. Camera, bounce, reflection and cache rays (ULTRART_MASK_SCENE, 0xEF) see them; shadow rays (0x05) don't.</summary>
        public const uint PortalMask = 0x20;
        /// <summary>
        /// Renderers on layer 30 ("Portal"): the main camera never draws them, but a portal view whose canSeePortalLayer is
        /// set adds the layer to its culling mask (PortalRenderV2), e.g. 8-3's theatre chandeliers. Rays include this mask
        /// only after passing through such a portal (UltraRTPortals.hlsl). Static-batched ones are not traced.
        /// </summary>
        public const uint PortalLayerMask = 0x40;
        public const int PortalLayer = 30;
        /// <summary>Trace layer-30 renderers (read at scene build).</summary>
        public static bool TracePortalLayer = true;
        private static bool IsPortalLayer(int layer) => TracePortalLayer && layer == PortalLayer;
        public const int MaxPortalSides = 1024;

        /// <summary>UltraRT/Portal; set by the plugin when the bundle loads. Null: portals are not traced.</summary>
        public static Shader PortalShader;

        private sealed class PortalSide
        {
            public Portal Portal;
            public bool Exit;
            public int Index;
            public MeshRenderer Proxy;
            public Material Material;
            public bool InRtas;
            public Matrix4x4 Pose;  // TRS(pos, rot, size) of the quad when last placed
            public Matrix4x4 Travel;
            public bool Seen;       // active this frame
            public int CreatedFrame; // Time.frameCount when the proxy was made (see PortalAddDelay)
        }

        private readonly Dictionary<long, PortalSide> _portalSides = new Dictionary<long, PortalSide>();
        private readonly List<PortalSide> _portalSideList = new List<PortalSide>();
        private ComputeBuffer _portalBuffer;
        private Vector4[] _portalRows = new Vector4[3 * 16];
        private bool _portalRowsDirty = true;
        // Last traced frame's rows (a moving portal's motion vectors: UltraRTPortals.hlsl, path.prevRot).
        private ComputeBuffer _portalPrevBuffer;
        private Vector4[] _portalRowsPrev = new Vector4[3 * 16];
        private bool _portalPrevStale = true, _portalPrevDirty = true;
        private static readonly int IdPortalsPrev = Shader.PropertyToID("_UltraRTPortalsPrev");
        private Mesh _portalQuad;
        private int _portalQuadFrame;
        /// <summary>
        /// Frames a new portal quad mesh and proxy wait before AddInstance. Added in the frame they were made, the sides
        /// were never hit (all of them, for the whole level: the RTAS built the shared quad's acceleration structure
        /// before the mesh was usable, and nothing rebuilt it while any side used it), until something removed and
        /// re-added every side (docs/portals.md, "Portal quads added too early").
        /// </summary>
        public static int PortalAddDelay = 2;
        private static FieldInfo _portalListField;
        private static readonly int IdPortals = Shader.PropertyToID("_UltraRTPortals");
        private static readonly int IdPortalHops = Shader.PropertyToID("_UltraRTPortalHops");
        private static readonly int IdMirrors = Shader.PropertyToID("_UltraRTMirrors");
        /// <summary>
        /// Camera rays reflect off portal mirrors (Plugin._rtMirrors: -ultrart-rtmirrors, bench plugin._rtMirrors=1).
        /// Off (default): they stop at a mirror and every composite keeps the game's own mirror view there, which
        /// shows V1 and is sharper than a traced reflection. Bounce and reflection rays always follow mirrors.
        /// </summary>
        public static bool TraceMirrors;
        /// <summary>Teleports per ray before portals are ignored (the game's views go 3 to 4 deep).</summary>
        public static int PortalHops = 3;
        private int _portalLogs;
        private bool _portalLayerChecked;
        private int _portalSidesInRtas;

        /// <summary>Portal sides in the RTAS right now.</summary>
        public int PortalSidesInRtas => _portalSidesInRtas;

        private static readonly Matrix4x4 PortalFlip = Matrix4x4.Scale(new Vector3(-1f, 1f, -1f)); // NativePortal.ScaleMatrix
        private static readonly Matrix4x4 MirrorFlip = Matrix4x4.Scale(new Vector3(1f, 1f, -1f));  // reflection about local z = 0

        /// <summary>
        /// Per frame, before the RTAS build: add, move and remove portal quads, upload the travel matrices and bind them
        /// (always, also with portals off: the ray generation shaders declare the buffer).
        /// </summary>
        public void SyncPortals(bool enabled)
        {
            foreach (var s in _portalSideList) s.Seen = false;
            if (_portalPrevStale)
            {
                // Rows changed last frame (or never copied): last frame's rows are the current ones, before this frame's sync.
                if (_portalRowsPrev.Length != _portalRows.Length) _portalRowsPrev = new Vector4[_portalRows.Length];
                System.Array.Copy(_portalRows, _portalRowsPrev, _portalRows.Length);
                _portalPrevStale = false;
                _portalPrevDirty = true;
            }
            if (enabled && PortalShader != null && Rtas != null)
            {
                var mgr = MonoSingleton.GetInstance(typeof(PortalManagerV2)) as PortalManagerV2; // never auto-creates one
                if (mgr != null && GetPortalList(mgr) is List<Portal> portals && portals.Count > 0)
                {
                    var player = MonoSingleton.GetInstance(typeof(NewMovement)) as NewMovement;
                    Vector3? playerPos = player != null ? player.transform.position : (Vector3?)null;
                    foreach (var portal in portals) SyncPortal(portal, playerPos);
                    if (!_portalLayerChecked)
                    {
                        // Layer 30 ("Portal"): portal views with canSeePortalLayer add it to their culling mask (PortalRenderV2).
                        _portalLayerChecked = true;
                        int onLayer = 0, active = 0;
                        foreach (var r in Object.FindObjectsByType<Renderer>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                            if (r.gameObject.layer == 30) { onLayer++; if (r.enabled && r.gameObject.activeInHierarchy) active++; }
                        _log.LogInfo($"Portals: {onLayer} renderers on layer 30 ({active} active); main camera traces layer 30: {((_cameraMask >> 30) & 1) != 0}");
                    }
                }
            }

            int inRtas = 0;
            bool changed = false;
            foreach (var s in _portalSideList)
            {
                if (s.Seen && !s.InRtas && Time.frameCount - s.CreatedFrame >= PortalAddDelay && Time.frameCount - _portalQuadFrame >= PortalAddDelay)
                {
                    Rtas.AddInstance(s.Proxy, new[] { RayTracingSubMeshFlags.Enabled }, enableTriangleCulling: false,
                        frontTriangleCounterClockwise: false, mask: PortalMask, id: 0);
                    s.InRtas = true;
                    changed = true;
                }
                else if (!s.Seen && s.InRtas)
                {
                    if (s.Proxy != null && Rtas != null) Rtas.RemoveInstance(s.Proxy);
                    s.InRtas = false;
                    changed = true;
                }
                if (s.InRtas) inRtas++;
            }
            _portalSidesInRtas = inRtas;
            if (changed && _portalLogs++ < 40)
                _log.LogInfo($"Portals: {inRtas} portal sides in the RTAS ({_portalSideList.Count} known)");
            BindPortalBuffer();
        }

        private static IList<Portal> GetPortalList(PortalManagerV2 mgr)
        {
            if (_portalListField == null)
                _portalListField = typeof(PortalManagerV2).GetField("portalComponents", BindingFlags.NonPublic | BindingFlags.Instance);
            return _portalListField?.GetValue(mgr) as IList<Portal>;
        }

        private void SyncPortal(Portal portal, Vector3? playerPos)
        {
            if (portal == null || !portal.isActiveAndEnabled || portal.entry == null || portal.exit == null) return;
            if (!portal.entry.gameObject.activeInHierarchy || !portal.exit.gameObject.activeInHierarchy) return;
            if (!(portal.shape is ULTRAKILL.Portal.Geometry.PlaneShape shape) || !shape.IsValid) return;
            if (portal.disableRange > 0f && playerPos.HasValue)
            {
                float r2 = portal.disableRange * portal.disableRange;
                if ((playerPos.Value - portal.entry.position).sqrMagnitude > r2 && (playerPos.Value - portal.exit.position).sqrMagnitude > r2) return;
            }
            portal.entry.GetPositionAndRotation(out var ep, out var er);
            var entry = Matrix4x4.TRS(ep, er, Vector3.one);
            var flags = portal.renderSettings;
            if (portal.mirror)
            {
                // The game renders a mirror as a portal onto itself (exit = entry, rotated 180 degrees about up) and flips
                // the projection's x; for a ray that is a reflection about the plane.
                if ((flags & PortalSideFlags.Enter) != 0)
                    SyncSide(portal, false, entry, entry * MirrorFlip * entry.inverse, shape);
                return;
            }
            portal.exit.GetPositionAndRotation(out var xp, out var xr);
            var exit = Matrix4x4.TRS(xp, xr, Vector3.one);
            if ((flags & PortalSideFlags.Enter) != 0) SyncSide(portal, false, entry, exit * PortalFlip * entry.inverse, shape);
            if ((flags & PortalSideFlags.Exit) != 0) SyncSide(portal, true, exit, entry * PortalFlip * exit.inverse, shape);
        }

        private void SyncSide(Portal portal, bool exitSide, Matrix4x4 side, Matrix4x4 travel, ULTRAKILL.Portal.Geometry.PlaneShape shape)
        {
            long key = ((long)portal.GetInstanceID() << 1) | (exitSide ? 1L : 0L);
            if (!_portalSides.TryGetValue(key, out var s))
            {
                if (_portalSideList.Count >= MaxPortalSides) return;
                if (_portalQuad == null) { _portalQuad = BuildPortalQuad(); _portalQuadFrame = Time.frameCount; }
                s = new PortalSide { Portal = portal, Exit = exitSide, Index = _portalSideList.Count, CreatedFrame = Time.frameCount };
                s.Material = new Material(PortalShader) { name = $"UltraRT Portal {s.Index}" };
                s.Material.SetFloat("_PortalIndex", s.Index);
                s.Material.SetFloat("_PortalNoRecursion", portal.appearsInRecursions ? 0f : 1f);
                s.Material.SetFloat("_PortalSeesLayer", portal.canSeePortalLayer ? 1f : 0f);
                s.Material.SetFloat("_PortalMirror", portal.mirror ? 1f : 0f);
                s.Proxy = CreateMeshProxy($"{portal.name} {(exitSide ? "exit" : "enter")} [UltraRT]", null, _portalQuad, new[] { s.Material });
                _portalSides[key] = s;
                _portalSideList.Add(s);
                if (s.Index < 64)
                    _log.LogInfo($"Portal side {s.Index}: '{Bench.GameControl.PathOf(portal.transform)}' {(portal.mirror ? "mirror" : exitSide ? "exit" : "enter")} " +
                                 $"{shape.width:F2}x{shape.height:F2} at {side.GetColumn(3)} fwd {side.GetColumn(2)}, render {portal.renderSettings}, " +
                                 $"recursions {portal.maxRecursions}, disableRange {portal.disableRange}, appearsInRecursions {portal.appearsInRecursions}, " +
                                 $"canSeeItself {portal.canSeeItself}, canSeePortalLayer {portal.canSeePortalLayer}, clipping {portal.clippingMethod}" +
                                 (portal.enableOverrideFog ? FormattableString.Invariant($", fog override {(exitSide ? (portal.useFogExit ? "on" : "off") : (portal.useFogEnter ? "on" : "off"))} colour {(exitSide ? portal.overrideFogColorExit : portal.overrideFogColorEnter)} {(exitSide ? portal.overrideFogStartExit : portal.overrideFogStartEnter):F0}-{(exitSide ? portal.overrideFogEndExit : portal.overrideFogEndEnter):F0} m") : ""));
                s.Pose = Matrix4x4.zero;
            }
            if (s.Proxy == null) return; // destroyed with the scene
            s.Seen = true;
            var pose = side * Matrix4x4.Scale(new Vector3(shape.width, shape.height, 1f));
            if (pose != s.Pose)
            {
                s.Pose = pose;
                var t = s.Proxy.transform;
                t.SetPositionAndRotation(side.GetColumn(3), side.rotation);
                t.localScale = new Vector3(shape.width, shape.height, 1f);
                if (s.InRtas) Rtas.UpdateInstanceTransform(s.Proxy);
            }
            if (travel != s.Travel)
            {
                s.Travel = travel;
                bool first = s.Travel == default(Matrix4x4);
                int need = (s.Index + 1) * 3;
                if (_portalRows.Length < need) System.Array.Resize(ref _portalRows, Mathf.NextPowerOfTwo(need));
                if (_portalRowsPrev.Length < _portalRows.Length) System.Array.Resize(ref _portalRowsPrev, _portalRows.Length);
                if (first)
                {
                    // A new side did not move.
                    _portalRowsPrev[s.Index * 3] = travel.GetRow(0);
                    _portalRowsPrev[s.Index * 3 + 1] = travel.GetRow(1);
                    _portalRowsPrev[s.Index * 3 + 2] = travel.GetRow(2);
                    _portalPrevDirty = true;
                }
                else { _portalPrevStale = true; PortalMoves++; }
                _portalRows[s.Index * 3] = travel.GetRow(0);
                _portalRows[s.Index * 3 + 1] = travel.GetRow(1);
                _portalRows[s.Index * 3 + 2] = travel.GetRow(2);
                _portalRowsDirty = true;
            }
        }

        /// <summary>
        /// The camera's images through the portal sides it can look into (in front of the side, within
        /// <paramref name="maxDistance"/>): travel * camera. Light importance uses them (LightSampling.ViewPoints), so
        /// lights around what the camera sees through a portal are sampled like lights near the camera.
        /// </summary>
        public void PortalViewpoints(Vector3 camPos, List<Vector3> output, float maxDistance = 120f, int max = 8)
        {
            output.Clear();
            foreach (var s in _portalSideList)
            {
                if (output.Count >= max) break;
                if (!s.Seen || !s.InRtas || s.Proxy == null) continue;
                Vector3 c = s.Pose.GetColumn(3), f = s.Pose.GetColumn(2);
                // The game's rule (PortalMath.Raycast): rays start in the side's back half-space and move along its forward.
                if (Vector3.Dot(camPos - c, f) > 0f) continue;
                if ((camPos - c).sqrMagnitude > maxDistance * maxDistance) continue;
                var v = s.Travel.MultiplyPoint3x4(camPos);
                if ((v - camPos).sqrMagnitude > 1f) output.Add(v);
            }
        }

        private Vector4[] _portalFogRows = new Vector4[2];

        /// <summary>
        /// Per portal side, the fog the game renders its view with (Portal.enableOverrideFog; NativePortalScene: the enter
        /// side's view takes the Enter values): 2 rows, (rgb colour, 0 = the level's fog, 1 = this fog, 2 = no fog),
        /// (start, end, 0, 0). For the veil pass (Plugin.Veils.cs). Returns true if a side in the RTAS overrides the fog.
        /// </summary>
        public bool PortalFogRows(out Vector4[] rows)
        {
            int need = Mathf.Max(1, _portalSideList.Count) * 2;
            if (_portalFogRows.Length < need) _portalFogRows = new Vector4[Mathf.NextPowerOfTwo(need)];
            bool any = false;
            foreach (var s in _portalSideList)
            {
                var p = s.Portal;
                Vector4 a = Vector4.zero, b = Vector4.zero;
                if (p != null && p.enableOverrideFog)
                {
                    bool use = s.Exit ? p.useFogExit : p.useFogEnter;
                    Color c = s.Exit ? p.overrideFogColorExit : p.overrideFogColorEnter;
                    a = new Vector4(c.r, c.g, c.b, use ? 1f : 2f);
                    b = new Vector4(s.Exit ? p.overrideFogStartExit : p.overrideFogStartEnter, s.Exit ? p.overrideFogEndExit : p.overrideFogEndEnter, 0f, 0f);
                    any |= s.InRtas;
                }
                _portalFogRows[s.Index * 2] = a;
                _portalFogRows[s.Index * 2 + 1] = b;
            }
            rows = _portalFogRows;
            return any;
        }

        /// <summary>Motion vectors for what is seen through a moving portal (bench plugin._portalMotion=0: off, as before).</summary>
        public static bool MovingPortalMotion = true;
        /// <summary>Travel matrix changes of existing sides, counted (diagnostics).</summary>
        public int PortalMoves;
        private int _portalJumpLogs;
        /// <summary>Log every camera jump near a portal with what each side would make of it (bench plugin._portalJumpDiag=1).</summary>
        public static bool PortalJumpDiag;

        /// <summary>
        /// Did the camera go through a portal between two traced frames? True if it moved further than a step and one
        /// active side's travel matrix takes the old position (close) to the new one: <paramref name="travel"/> is that
        /// matrix (last frame's world -> this frame's, for everything the camera saw).
        /// </summary>
        public bool PortalCrossing(Vector3 prevCam, Vector3 cam, out Matrix4x4 travel, out string name)
        {
            travel = Matrix4x4.identity;
            name = null;
            float jump = (cam - prevCam).magnitude;
            if (jump < 1.5f) return false;
            // Closer to the new position than the old one was (a replayed path lands metres off a moving exit).
            float best = Mathf.Min(0.9f * jump, 12f);
            bool found = false;
            foreach (var s in _portalSideList)
            {
                if (!s.Seen || s.Portal == null || s.Portal.mirror) continue;
                // The camera was at the side (within a step of its plane, inside its rectangle with a margin).
                Vector3 local = s.Pose.inverse.MultiplyPoint3x4(prevCam); // unit quad space; z in metres (scale 1)
                if (Mathf.Abs(local.x) > 0.75f || Mathf.Abs(local.y) > 0.75f || Mathf.Abs(local.z) > 8f) continue;
                float d = (s.Travel.MultiplyPoint3x4(prevCam) - cam).magnitude;
                if (d < best) { best = d; travel = s.Travel; name = s.Proxy != null ? s.Proxy.name : "?"; found = true; }
            }
            if (PortalJumpDiag && _portalJumpLogs < 24)
            {
                // A jump no side explains: the nearest sides, for the log.
                var parts = new List<string>();
                foreach (var s in _portalSideList)
                {
                    if (!s.Seen || s.Portal == null || s.Portal.mirror) continue;
                    Vector3 local = s.Pose.inverse.MultiplyPoint3x4(prevCam);
                    if (Mathf.Abs(local.z) > 30f || Mathf.Abs(local.x) > 3f || Mathf.Abs(local.y) > 3f) continue;
                    parts.Add($"{(s.Proxy != null ? s.Proxy.name : "?")} local ({local.x:F2}, {local.y:F2}, {local.z:F1}) lands {(s.Travel.MultiplyPoint3x4(prevCam) - cam).magnitude:F1} m off");
                }
                if (parts.Count > 0 && _portalJumpLogs++ < 24)
                    _log.LogInfo($"Portal crossing: a {jump:F1} m camera jump from {prevCam:F1} to {cam:F1} ({(found ? "matched " + name : "no match")}); sides: {string.Join("; ", parts)}");
            }
            return found;
        }

        private void BindPortalBuffer()
        {
            if (_portalBuffer == null || _portalBuffer.count < _portalRows.Length)
            {
                _portalBuffer?.Release();
                _portalBuffer = new ComputeBuffer(_portalRows.Length, 16, ComputeBufferType.Structured) { name = "UltraRT Portals" };
                _portalRowsDirty = true;
            }
            if (_portalRowsDirty)
            {
                _portalBuffer.SetData(_portalRows);
                _portalRowsDirty = false;
            }
            Shader.SetGlobalBuffer(IdPortals, _portalBuffer);
            if (_portalPrevBuffer == null || _portalPrevBuffer.count < _portalRowsPrev.Length)
            {
                _portalPrevBuffer?.Release();
                _portalPrevBuffer = new ComputeBuffer(_portalRowsPrev.Length, 16, ComputeBufferType.Structured) { name = "UltraRT Portals Prev" };
                _portalPrevDirty = true;
            }
            if (_portalPrevDirty)
            {
                _portalPrevBuffer.SetData(_portalRowsPrev);
                _portalPrevDirty = false;
            }
            Shader.SetGlobalBuffer(IdPortalsPrev, MovingPortalMotion ? _portalPrevBuffer : _portalBuffer);
            Shader.SetGlobalInt(IdPortalHops, Mathf.Clamp(PortalHops, 0, 8));
            Shader.SetGlobalInt(IdMirrors, TraceMirrors ? 1 : 0);
        }

        /// <summary>A unit quad in the side's local xy plane (PortalVertices: width along x, height along y).</summary>
        private static Mesh BuildPortalQuad()
        {
            var m = new Mesh { name = "UltraRT Portal Quad" };
            m.vertices = new[] { new Vector3(-0.5f, -0.5f, 0f), new Vector3(0.5f, -0.5f, 0f), new Vector3(0.5f, 0.5f, 0f), new Vector3(-0.5f, 0.5f, 0f) };
            m.normals = new[] { Vector3.back, Vector3.back, Vector3.back, Vector3.back };
            m.triangles = new[] { 0, 1, 2, 0, 2, 3 };
            m.RecalculateBounds();
            m.UploadMeshData(false);
            return m;
        }

        private void DisposePortals()
        {
            foreach (var s in _portalSideList) if (s.Material != null) Object.Destroy(s.Material);
            _portalSideList.Clear();
            _portalSides.Clear();
            _portalBuffer?.Release();
            _portalBuffer = null;
            _portalPrevBuffer?.Release();
            _portalPrevBuffer = null;
            _portalPrevStale = _portalPrevDirty = true;
            if (_portalQuad != null) Object.Destroy(_portalQuad);
            _portalQuad = null;
        }
    }
}
