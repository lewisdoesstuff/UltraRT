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
            public bool FakeMirror; // "Mirror" in the portal's path: a level's mirror built from a portal into a mirrored copy
            public int CreatedFrame; // Time.frameCount when the proxy was made (see PortalAddDelay)
        }

        private readonly Dictionary<long, PortalSide> _portalSides = new Dictionary<long, PortalSide>();
        private readonly List<PortalSide> _portalSideList = new List<PortalSide>();
        private ComputeBuffer _portalBuffer;
        private Vector4[] _portalRows = new Vector4[3 * 16];
        private bool _portalRowsDirty = true;
        private bool _portalQuadsDirty = true;
        // Last traced frame's rows (a moving portal's motion vectors: UltraRTPortals.hlsl, path.prevRot).
        private ComputeBuffer _portalPrevBuffer;
        private Vector4[] _portalRowsPrev = new Vector4[3 * 16];
        private bool _portalPrevStale = true, _portalPrevDirty = true;
        private static readonly int IdPortalsPrev = Shader.PropertyToID("_UltraRTPortalsPrev");
        private static readonly int IdPortalQuads = Shader.PropertyToID("_UltraRTPortalQuads"), IdPortalEdge = Shader.PropertyToID("_UltraRTPortalEdge");
        private ComputeBuffer _portalQuadBuffer;
        private Vector4[] _portalQuadRows = new Vector4[3 * 16];
        // Per side: world on the exit's side -> the side's quad space (the quad rows after the inverse travel), so a light
        // imaged through the side tests its opening without carrying points back (UltraRTPortalBuffers.hlsl, PortalImageSegmentThrough).
        private static readonly int IdPortalImageQuads = Shader.PropertyToID("_UltraRTPortalImageQuads");
        private ComputeBuffer _portalImageQuadBuffer;
        private Vector4[] _portalImageQuadRows = new Vector4[3 * 16];
        /// <summary>What slides into view at a moving portal's edge starts without history (bench plugin._portalEdges=0: off).</summary>
        public static bool MovingPortalEdges = true;
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
        internal ComputeBuffer PortalBuffer => _portalBuffer;
        internal ComputeBuffer PortalImageQuadBuffer => _portalImageQuadBuffer;

        /// <summary>
        /// Wave 3: trim redundant deep PortalRenderV2 raster recursions on non-mirror portals while RT replaces them.
        /// Mirrors are untouched, depth-0 portal quads/views stay intact (for depth matching, skyboxes &amp; particles),
        /// RT still reads the saved original appearsInRecursions for full 3-hop ray-traced portals, and originals are
        /// restored immediately when RT turns off or on scene unload. Bench plugin._portalRasterTrim=0.
        /// </summary>
        public static bool PortalRasterTrim = true;
        private readonly Dictionary<Portal, (int maxRec, bool appears)> _savedPortalRec = new Dictionary<Portal, (int, bool)>();
        private bool _portalRasterTrimmed;

        /// <summary>
        /// Skip the game's raster portal views while RT replaces them (PortalRasterSkip.cs; docs/portals.md, "Raster skip").
        /// -ultrart-portal-raster-skip on|off, bench plugin._portalRasterSkip=0.
        /// </summary>
        public static bool PortalRasterSkip = true;

        /// <summary>The portal side (Portal instance id, exit side) is in the RTAS, so camera rays go through it.</summary>
        public bool IsPortalSideTraced(int portalInstanceId, bool exitSide) =>
            _portalSides.TryGetValue(((long)portalInstanceId << 1) | (exitSide ? 1L : 0L), out var s) && s.InRtas && s.Seen;

        private bool OriginalAppearsInRecursions(Portal portal)
        {
            if (portal == null) return false;
            if (_savedPortalRec.TryGetValue(portal, out var orig)) return orig.appears;
            return portal.appearsInRecursions;
        }

        /// <summary>Applies or restores PortalRenderV2 recursion trimming on non-mirror portals.</summary>
        public void ApplyPortalRasterTrim(bool rtActive)
        {
            bool want = rtActive && PortalRasterTrim;
            if (!want)
            {
                if (_portalRasterTrimmed) RestorePortalRasterTrim();
                return;
            }
            var mgr = MonoSingleton.GetInstance(typeof(PortalManagerV2)) as PortalManagerV2;
            if (mgr == null || !(GetPortalList(mgr) is List<Portal> portals) || portals.Count == 0) return;
            for (int i = 0; i < portals.Count; i++)
            {
                var p = portals[i];
                if (p == null || p.mirror) continue;
                if (!_savedPortalRec.ContainsKey(p))
                    _savedPortalRec[p] = (p.maxRecursions, p.appearsInRecursions);
                if (p.maxRecursions != 0) p.maxRecursions = 0;
                if (p.appearsInRecursions) p.appearsInRecursions = false;
            }
            _portalRasterTrimmed = true;
        }

        public void RestorePortalRasterTrim()
        {
            foreach (var kv in _savedPortalRec)
            {
                if (kv.Key == null) continue;
                kv.Key.maxRecursions = kv.Value.maxRec;
                kv.Key.appearsInRecursions = kv.Value.appears;
            }
            _savedPortalRec.Clear();
            _portalRasterTrimmed = false;
        }

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
                        var names = new System.Text.StringBuilder();
                        int listed = 0;
                        foreach (var r in Object.FindObjectsByType<Renderer>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                        {
                            if (r.gameObject.layer != 30) continue;
                            onLayer++;
                            bool on = r.enabled && r.gameObject.activeInHierarchy;
                            if (on) active++;
                            if (listed++ < 24 && !IsPlayerModel(r))
                                names.Append($"; '{Bench.GameControl.PathOf(r.transform)}' {r.GetType().Name}{(on ? "" : " off")}{(r.isPartOfStaticBatch ? " static-batched (not traced)" : "")}");
                        }
                        _log.LogInfo($"Portals: {onLayer} renderers on layer 30 ({active} active); main camera traces layer 30: {((_cameraMask >> 30) & 1) != 0}{names}");
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
                s = new PortalSide { Portal = portal, Exit = exitSide, Index = _portalSideList.Count, CreatedFrame = Time.frameCount,
                    FakeMirror = Bench.GameControl.PathOf(portal.transform).IndexOf("mirror", StringComparison.OrdinalIgnoreCase) >= 0 };
                s.Material = new Material(PortalShader) { name = $"UltraRT Portal {s.Index}" };
                s.Material.SetFloat("_PortalIndex", s.Index);
                s.Material.SetFloat("_PortalNoRecursion", OriginalAppearsInRecursions(portal) ? 0f : 1f);
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
                _portalQuadsDirty = true;
            }
            if (travel != s.Travel)
            {
                bool first = s.Travel == default(Matrix4x4); // before the assignment: a new side did not move
                s.Travel = travel;
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
                _portalQuadsDirty = true;
            }
        }

        /// <summary>
        /// The camera's images through the portal sides it can look into (in front of the side, within
        /// <paramref name="maxDistance"/>): travel * camera, and the images of those images, up to
        /// <paramref name="hops"/> portals deep; the <paramref name="max"/> nearest. Light importance uses them (LightSampling.ViewPoints), so
        /// lights around what the camera sees through a portal are sampled like lights near the camera.
        /// </summary>
        public void PortalViewpoints(Vector3 camPos, List<Vector3> output, float maxDistance = 120f, int max = 16, int hops = 3)
        {
            output.Clear();
            _viewWork.Clear();
            _viewWork.Add((camPos, 0f, 0));
            // Breadth first: the images of hop k are the sources of hop k + 1 (8-3's Shifting Hallway shows three
            // doorways in a row; with one hop, what is behind the second was far from every viewpoint again).
            int from = 0;
            for (int hop = 1; hop <= hops; hop++)
            {
                int to = _viewWork.Count;
                for (int k = from; k < to; k++)
                {
                    var (src, length, _) = _viewWork[k];
                    foreach (var s in _portalSideList)
                    {
                        if (!s.Seen || !s.InRtas || s.Proxy == null) continue;
                        Vector3 c = s.Pose.GetColumn(3), f = s.Pose.GetColumn(2);
                        // The game's rule (PortalMath.Raycast): rays start in the side's back half-space and move along its forward.
                        if (Vector3.Dot(src - c, f) > 0f) continue;
                        float d = (src - c).magnitude;
                        if (d > maxDistance) continue;
                        var v = s.Travel.MultiplyPoint3x4(src);
                        if ((v - src).sqrMagnitude <= 1f) continue;
                        bool known = false;
                        foreach (var w in _viewWork) if ((w.p - v).sqrMagnitude <= 1f) { known = true; break; } // back through the pair
                        if (!known && _viewWork.Count < 256) _viewWork.Add((v, length + d, hop));
                    }
                }
                from = to;
            }
            // The nearest first (by the way to the last portal crossed): they are what fills the screen.
            _viewWork.RemoveAt(0);
            _viewWork.Sort((a, b) => a.length.CompareTo(b.length));
            for (int k = 0; k < _viewWork.Count && k < max; k++) output.Add(_viewWork[k].p);
        }

        /// <summary>
        /// A light's image behind one portal side (Plugin.PortalLights.cs): where it shines from on the exit side, and what
        /// the guards and the score need. The receiving room is the exit's opening's far side; the shader clips the image
        /// to that opening and traces its shadow on both sides (UltraRTLights.hlsl, "Portal images").
        /// </summary>
        public struct PortalImage
        {
            public Vector3 Pos;      // the image: travel x light
            public Vector3 Exit;     // the exit opening's centre (travel x the side's centre)
            public float ExitView;   // from the exit to the camera or its nearest portal viewpoint (m)
            public Matrix4x4 Travel; // the side's travel matrix (light side -> receiving side)
            public float Distance;   // from the light to the side's opening (the closest point of the rectangle)
            public float ExitDistance; // from the light to the exit opening, in world space (rigid: = the image to the side's opening)
            public bool ExitFacesLight; // the light is on the side of the exit that the image lights (a loop: same room)
            // A mirror in all but name: light comes back out of the exit the way it went in (a U-turn: 8-2's starting
            // pool, a floor portal into a copy of the street) or the portal is a level's mirror (8-2's MirrorBig into a
            // mirrored copy of the room). Its far side is a copy lit by its own copies of the lights.
            public bool MirrorLike;
            public float Span;       // entrance to exit (m): under ~100 m both openings are in one space (loops, shifting halls)
            public int Side;
        }

        public int PortalSideCount => _portalSideList.Count;

        /// <summary>
        /// The images of a light at <paramref name="p"/> through the active portal sides (not mirrors) it can shine through,
        /// by the game's rule as in <see cref="PortalViewpoints"/> (from the side's back half-space), within
        /// <paramref name="reach"/> of the opening's rectangle. <paramref name="cone"/> (spot lights: axis, cos of the half
        /// angle; w &lt;= -1 none) and <paramref name="emitNormal"/> (one-sided area lights; zero = none) drop sides the
        /// light does not shine at. One hop; nearest first, at most <paramref name="max"/>.
        /// </summary>
        public void PortalImages(Vector3 p, float reach, Vector4 cone, Vector3 emitNormal, List<PortalImage> output, int max = 4)
        {
            output.Clear();
            float cosCone = Mathf.Clamp(cone.w, -1f, 1f);
            float coneAngle = cone.w > -0.999f ? Mathf.Acos(cosCone) : 0f;
            for (int i = 0; i < _imageSides.Count; i++)
            {
                var s = _imageSides[i];
                // Scalar first tests: this runs for every light and side (Mono does not inline Vector3's operators).
                if (s.ExitView - reach > ImageViewCull) continue; // the exit is far from everything the camera sees
                float rx = p.x - s.C.x, ry = p.y - s.C.y, rz = p.z - s.C.z;
                if (rx * s.F.x + ry * s.F.y + rz * s.F.z > -0.01f) continue; // the game's rule: light enters a side from its back half-space
                float lim = reach + s.Rad;
                if (rx * rx + ry * ry + rz * rz > lim * lim) continue;
                var rel = new Vector3(rx, ry, rz);
                float qx = Mathf.Clamp(Vector3.Dot(rel, s.Ax), -s.Hw, s.Hw), qy = Mathf.Clamp(Vector3.Dot(rel, s.Ay), -s.Hh, s.Hh);
                Vector3 q = s.C + s.Ax * qx + s.Ay * qy;
                float d = (p - q).magnitude;
                if (d > reach) continue;
                if (cone.w > -0.999f)
                {
                    // A spot: the opening's bounding circle must touch the cone.
                    float dc = rel.magnitude;
                    float ang = Mathf.Acos(Mathf.Clamp(Vector3.Dot(-rel / Mathf.Max(dc, 1e-4f), (Vector3)cone), -1f, 1f));
                    if (ang > coneAngle + Mathf.Atan2(s.Rad, dc)) continue;
                }
                if (emitNormal != Vector3.zero)
                {
                    // A one-sided emitter (rect, disc): some corner of the opening must be in front of it.
                    bool any = false;
                    for (int k = 0; k < 4 && !any; k++)
                    {
                        var corner = s.C + s.Ax * ((k & 1) != 0 ? s.Hw : -s.Hw) + s.Ay * ((k & 2) != 0 ? s.Hh : -s.Hh);
                        any = Vector3.Dot(emitNormal, corner - p) > 0f;
                    }
                    if (!any) continue;
                }
                var v = s.Travel.MultiplyPoint3x4(p);
                if ((v - p).sqrMagnitude <= 1f) continue; // a pair that leads back to the same place
                // The exit's opening as the real light sees it: the side's rectangle carried by the travel matrix. Rigid, so
                // the distance from the light to it is the distance from the light carried back through the side to the side.
                Vector3 rb = s.Back.MultiplyPoint3x4(p) - s.C;
                float bx = Mathf.Clamp(Vector3.Dot(rb, s.Ax), -s.Hw, s.Hw), by = Mathf.Clamp(Vector3.Dot(rb, s.Ay), -s.Hh, s.Hh);
                float exitDist = (rb - (s.Ax * bx + s.Ay * by)).magnitude;
                // Light leaves the exit moving along travel x f (the side's forward): the light itself on that side of the
                // exit's plane means the exit opens into the light's own room (8-3's sideways loops, 8-2's looping halls).
                bool facesLight = Vector3.Dot(p - s.ExitC, s.ExitDir) > 0f;
                output.Add(new PortalImage { Pos = v, Exit = s.ExitC, ExitView = s.ExitView, Travel = s.Travel, Distance = d, ExitDistance = exitDist, ExitFacesLight = facesLight, MirrorLike = s.MirrorLike, Span = s.Span, Side = s.Index });
            }
            output.Sort((a, b) => a.Distance.CompareTo(b.Distance));
            if (output.Count > max) output.RemoveRange(max, output.Count - max);
        }

        /// <summary>What <see cref="PortalImages"/> needs of each active non-mirror side, once per frame (it runs per light).</summary>
        private struct ImageSide
        {
            public int Index;
            public Vector3 C, F, Ax, Ay, ExitC, ExitDir;
            public float Hw, Hh, Rad, Span, ExitView;
            public Matrix4x4 Travel, Back;
            public bool MirrorLike;
        }
        private readonly List<ImageSide> _imageSides = new List<ImageSide>();
        /// <summary>An exit further than this beyond a light's reach from the camera and its portal viewpoints is not imaged (m).</summary>
        public const float ImageViewCull = 150f;

        /// <summary>
        /// Once per frame before <see cref="PortalImages"/>: what it needs of each active non-mirror side, and how far the
        /// side's exit is from the camera or its nearest portal viewpoint (<paramref name="views"/>).
        /// </summary>
        public void BeginPortalImages(Vector3 cam, List<Vector3> views)
        {
            _imageSides.Clear();
            foreach (var s in _portalSideList)
            {
                if (!s.Seen || !s.InRtas || s.Proxy == null || s.Portal == null || s.Portal.mirror) continue;
                Vector3 c = s.Pose.GetColumn(3), f = s.Pose.GetColumn(2), ax = s.Pose.GetColumn(0), ay = s.Pose.GetColumn(1);
                float w = ax.magnitude, h = ay.magnitude;
                if (w < 1e-3f || h < 1e-3f) continue;
                var exitC = s.Travel.MultiplyPoint3x4(c);
                var exitDir = s.Travel.MultiplyVector(f);
                _imageSides.Add(new ImageSide
                {
                    Index = s.Index, C = c, F = f, Ax = ax / w, Ay = ay / h, Hw = 0.5f * w, Hh = 0.5f * h, Rad = 0.5f * Mathf.Sqrt(w * w + h * h),
                    Travel = s.Travel, Back = s.Travel.inverse, ExitC = exitC, ExitDir = exitDir, Span = (exitC - c).magnitude,
                    MirrorLike = s.FakeMirror || Vector3.Dot(exitDir, f) < -0.5f,
                    ExitView = ExitViewDistance(exitC, cam, views),
                });
            }
        }

        private static float ExitViewDistance(Vector3 e, Vector3 cam, List<Vector3> views)
        {
            float d = (e - cam).magnitude;
            if (views != null) foreach (var v in views) d = Mathf.Min(d, (e - v).magnitude);
            return d;
        }

        /// <summary>A side's name for the log.</summary>
        public string PortalSideName(int index) =>
            index >= 0 && index < _portalSideList.Count && _portalSideList[index].Portal != null
                ? $"{_portalSideList[index].Portal.name} {(_portalSideList[index].Exit ? "exit" : "enter")} ({index})" : $"side {index}";

        private readonly List<(Vector3 p, float length, int hop)> _viewWork = new List<(Vector3 p, float length, int hop)>();

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
            // World -> quad space per side, for the moving-portal edge test (UltraRTPortals.hlsl, PortalSegmentThrough).
            if (_portalQuadRows.Length < _portalRows.Length) { _portalQuadRows = new Vector4[_portalRows.Length]; _portalQuadsDirty = true; }
            if (_portalImageQuadRows.Length < _portalRows.Length) { _portalImageQuadRows = new Vector4[_portalRows.Length]; _portalQuadsDirty = true; }
            if (_portalQuadBuffer == null || _portalQuadBuffer.count < _portalQuadRows.Length)
            {
                _portalQuadBuffer?.Release();
                _portalQuadBuffer = new ComputeBuffer(_portalQuadRows.Length, 16, ComputeBufferType.Structured) { name = "UltraRT Portal Quads" };
                _portalQuadsDirty = true;
            }
            if (_portalImageQuadBuffer == null || _portalImageQuadBuffer.count < _portalImageQuadRows.Length)
            {
                _portalImageQuadBuffer?.Release();
                _portalImageQuadBuffer = new ComputeBuffer(_portalImageQuadRows.Length, 16, ComputeBufferType.Structured) { name = "UltraRT Portal Image Quads" };
                _portalQuadsDirty = true;
            }
            if (_portalQuadsDirty)
            {
                foreach (var s in _portalSideList)
                {
                    if (s.Index * 3 + 2 >= _portalQuadRows.Length) continue;
                    var inv = s.Pose.inverse;
                    _portalQuadRows[s.Index * 3] = inv.GetRow(0);
                    _portalQuadRows[s.Index * 3 + 1] = inv.GetRow(1);
                    _portalQuadRows[s.Index * 3 + 2] = inv.GetRow(2);
                    // The same quad seen from the exit: a point there, carried back through the travel, then into quad space.
                    var img = s.Travel == default(Matrix4x4) ? inv : inv * s.Travel.inverse;
                    _portalImageQuadRows[s.Index * 3] = img.GetRow(0);
                    _portalImageQuadRows[s.Index * 3 + 1] = img.GetRow(1);
                    _portalImageQuadRows[s.Index * 3 + 2] = img.GetRow(2);
                }
                _portalQuadBuffer.SetData(_portalQuadRows);
                _portalImageQuadBuffer.SetData(_portalImageQuadRows);
                _portalQuadsDirty = false;
            }
            Shader.SetGlobalBuffer(IdPortalQuads, _portalQuadBuffer);
            Shader.SetGlobalBuffer(IdPortalImageQuads, _portalImageQuadBuffer);
            Shader.SetGlobalInt(IdPortalEdge, MovingPortalMotion && MovingPortalEdges ? 1 : 0);
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
            RestorePortalRasterTrim();
            foreach (var s in _portalSideList) if (s.Material != null) Object.Destroy(s.Material);
            _portalSideList.Clear();
            _portalSides.Clear();
            _portalBuffer?.Release();
            _portalBuffer = null;
            _portalQuadBuffer?.Release();
            _portalQuadBuffer = null;
            _portalImageQuadBuffer?.Release();
            _portalImageQuadBuffer = null;
            _portalPrevBuffer?.Release();
            _portalPrevBuffer = null;
            _portalPrevStale = _portalPrevDirty = _portalQuadsDirty = true;
            if (_portalQuad != null) Object.Destroy(_portalQuad);
            _portalQuad = null;
        }
    }
}
