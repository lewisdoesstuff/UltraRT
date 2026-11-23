using System.Collections.Generic;
using UnityEngine;

namespace UltraRT
{
    /// <summary>
    /// The camera's images through the portals it looks into (Plugin.PortalViews fills the list once per traced
    /// frame from SceneProxies.PortalViewpoints; empty without portals). What is seen through a portal is near one
    /// of these points and can be hundreds of metres from the camera in world space, so a "near the camera" test
    /// (a cull distance, a score, a budget's ranking) asks here for the distance to the camera or its nearest image.
    /// docs/portals.md, "Distance to the camera".
    /// </summary>
    internal static class PortalView
    {
        public static readonly List<Vector3> Points = new List<Vector3>();
        /// <summary>The camera's forward and up axes as each image sees them (same order as <see cref="Points"/>).</summary>
        public static readonly List<Vector3> Forwards = new List<Vector3>(), Ups = new List<Vector3>();

        public static float SqrDistance(Vector3 p, Vector3 cam)
        {
            float d2 = (p - cam).sqrMagnitude;
            for (int i = 0; i < Points.Count; i++) d2 = Mathf.Min(d2, (p - Points[i]).sqrMagnitude);
            return d2;
        }

        /// <summary>The same, and which viewpoint is nearest: -1 the camera, else an index into <see cref="Points"/>.</summary>
        public static float SqrDistance(Vector3 p, Vector3 cam, out int nearest)
        {
            float d2 = (p - cam).sqrMagnitude;
            nearest = -1;
            for (int i = 0; i < Points.Count; i++)
            {
                float v = (p - Points[i]).sqrMagnitude;
                if (v < d2) { d2 = v; nearest = i; }
            }
            return d2;
        }

        public static float SqrDistance(Bounds b, Vector3 cam)
        {
            float d2 = b.SqrDistance(cam);
            for (int i = 0; i < Points.Count; i++) d2 = Mathf.Min(d2, b.SqrDistance(Points[i]));
            return d2;
        }
    }
}
