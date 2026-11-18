using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using UltraRT.Bench;
using UnityEngine;

namespace UltraRT
{
    /// <summary>
    /// Bench: a static pose aimed at an object (PathSpec.LookAt, bench/README.md "Aiming at an object"), for objects no
    /// saved pose shows: "LookAt": "LionStatue" (GameObject name glob), "Room/Statue*" (path glob) or "mat:Gibs" (renderers
    /// with that material). The camera is placed LookDistance from the bounds centre (default from the size), on the side
    /// LookYaw names, else the most open of 16 sides, preferring the object's front (-Z of a Quad, the side its forward
    /// points to otherwise), and looks at the centre (Pitch is added).
    /// </summary>
    public partial class Plugin
    {
        private CameraPath LookAtPath(PathSpec p, CameraPath fallback)
        {
            var target = FindLookTarget(p, fallback.Start.Position);
            if (target == null) { Logger.LogWarning($"Benchmark: LookAt '{p.LookAt}' found nothing"); return null; }
            if (p.LookActivate)
                for (var t = target.transform; t != null; t = t.parent)
                    if (!t.gameObject.activeSelf) { Logger.LogInfo($"Benchmark: LookAt switches on '{GameControl.PathOf(t)}'"); t.gameObject.SetActive(true); }
            var b = WorldBounds(target);
            var c = b.center;
            float size = Mathf.Max(b.extents.magnitude, 0.25f);
            float dist = p.LookDistance > 0f ? p.LookDistance : Mathf.Clamp(size * 2.6f, 1.5f, 60f);
            float pitch = p.Pitch;

            float yaw;
            if (p.LookYaw.HasValue) yaw = p.LookYaw.Value;
            else
            {
                // Front: a Quad's visible face is -Z, so look along +Z from the -Z side; for other meshes the side forward
                // points to. The yaw is the camera's heading (towards the target).
                bool quad = target.GetComponent<MeshFilter>() is MeshFilter mf && mf.sharedMesh != null && mf.sharedMesh.name.StartsWith("Quad");
                var front = quad ? -target.transform.forward : target.transform.forward;
                front.y = 0f;
                if (front.sqrMagnitude < 1e-4f) front = Vector3.forward;
                front.Normalize();
                int mask = ~(1 << 2);
                float best = float.NegativeInfinity;
                yaw = 0f;
                for (int i = 0; i < 16; i++)
                {
                    float h = i * 22.5f;
                    var heading = Quaternion.Euler(-pitch, h, 0f) * Vector3.forward; // camera looks along heading
                    var eye = c - heading * dist;
                    float score = Vector3.Dot(-new Vector3(heading.x, 0f, heading.z).normalized, front); // camera on the front side
                    if (Physics.CheckSphere(eye, 0.3f, mask, QueryTriggerInteraction.Ignore)) score -= 10f; // inside geometry
                    if (Physics.Raycast(eye, heading, out var hit, dist, mask, QueryTriggerInteraction.Ignore) && hit.distance < dist - size)
                        score -= 5f + (dist - size - hit.distance); // something in between
                    if (score > best) { best = score; yaw = h; }
                }
            }
            var dir = Quaternion.Euler(-pitch, yaw, 0f) * Vector3.forward;
            var eyePos = c - dir * dist;
            var eyeOffset = GameControl.Cam != null && GameControl.Player != null
                ? GameControl.Cam.transform.position - GameControl.Player.transform.position
                : new Vector3(0f, 1.5f, 0f);
            if (eyeOffset.sqrMagnitude > 9f) eyeOffset = new Vector3(0f, 1.5f, 0f);
            var pose = new CamPose(eyePos - eyeOffset, yaw, pitch);
            Logger.LogInfo($"Benchmark: LookAt '{p.LookAt}' -> '{GameControl.PathOf(target.transform)}' ({MaterialList(target)}) " +
                           $"centre {c:F1} size {size:F1}, camera {eyePos:F1} yaw {yaw:F0} pitch {pitch:F0} dist {dist:F1}{(target.gameObject.activeInHierarchy ? "" : " (inactive)")}");
            return CameraPath.Static(pose, fallback.Duration);
        }

        /// <summary>World bounds from the mesh (a switched-off renderer's own bounds are empty).</summary>
        private static Bounds WorldBounds(Renderer r)
        {
            if (r.gameObject.activeInHierarchy && r.enabled && r.bounds.size.sqrMagnitude > 1e-6f) return r.bounds;
            Mesh mesh = r is SkinnedMeshRenderer smr ? smr.sharedMesh : r.GetComponent<MeshFilter>()?.sharedMesh;
            var lb = r is SkinnedMeshRenderer s2 ? s2.localBounds : mesh != null ? mesh.bounds : new Bounds(Vector3.zero, Vector3.one);
            var m = r.transform.localToWorldMatrix;
            var wb = new Bounds(m.MultiplyPoint3x4(lb.center), Vector3.zero);
            for (int i = 0; i < 8; i++)
                wb.Encapsulate(m.MultiplyPoint3x4(lb.center + Vector3.Scale(lb.extents, new Vector3((i & 1) != 0 ? 1 : -1, (i & 2) != 0 ? 1 : -1, (i & 4) != 0 ? 1 : -1))));
            return wb;
        }

        private static string MaterialList(Renderer r) =>
            string.Join(", ", r.sharedMaterials.Where(m => m != null).Select(m => MaterialClassifier.CleanName(m)).Distinct().Take(4));

        private Renderer FindLookTarget(PathSpec p, Vector3 near)
        {
            string spec = p.LookAt.Trim();
            bool byMat = spec.StartsWith("mat:", StringComparison.OrdinalIgnoreCase);
            if (byMat) spec = spec.Substring(4);
            var rx = new Regex("^" + Regex.Escape(spec).Replace("\\*", ".*").Replace("\\?", ".") + "$", RegexOptions.IgnoreCase);
            bool byPath = !byMat && spec.Contains("/");
            var all = new List<Renderer>();
            all.AddRange(FindObjectsByType<MeshRenderer>(FindObjectsInactive.Include, FindObjectsSortMode.None));
            all.AddRange(FindObjectsByType<SkinnedMeshRenderer>(FindObjectsInactive.Include, FindObjectsSortMode.None));
            var hits = all.Where(r => r != null && !SceneProxies.IsProxyObject(r.gameObject) && (byMat
                    ? r.sharedMaterials.Any(m => m != null && rx.IsMatch(MaterialClassifier.CleanName(m)))
                    : rx.IsMatch(byPath ? GameControl.PathOf(r.transform) : r.name)))
                .ToList();
            var off = hits.Where(r => !(r.gameObject.activeInHierarchy && r.enabled)).ToList();
            if (!p.LookActivate) hits = hits.Where(r => r.gameObject.activeInHierarchy && r.enabled).ToList();
            // Active ones first (LookActivate may pick a switched-off one only when no active one matches), nearest first.
            var sorted = hits.OrderBy(r => r.gameObject.activeInHierarchy && r.enabled ? 0 : 1).ThenBy(r => (WorldBounds(r).center - near).sqrMagnitude).ToList();
            Logger.LogInfo($"Benchmark: LookAt '{p.LookAt}': {sorted.Count} matches" +
                           (sorted.Count > 0 ? ": " + string.Join("; ", sorted.Take(12).Select((r, i) => $"#{i} {GameControl.PathOf(r.transform)} @{WorldBounds(r).center:F0}{(r.gameObject.activeInHierarchy ? "" : " off")}")) : "") +
                           (!p.LookActivate && off.Count > 0 ? $"; {off.Count} switched off (LookActivate): " + string.Join("; ", off.Take(8).Select(r => $"{GameControl.PathOf(r.transform)} @{WorldBounds(r).center:F0}")) : ""));
            return sorted.Count > p.LookIndex ? sorted[p.LookIndex] : sorted.LastOrDefault();
        }
    }
}
