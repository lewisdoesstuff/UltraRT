using System.Collections.Generic;
using UnityEngine;

namespace UltraRT
{
    /// <summary>
    /// Lava emitters (docs/lava.md): our lava proxies (SceneProxies.Lava.cs) and the game's opaque lava. They are big
    /// (channels and pools of tens to hundreds of m^2 in a few triangles), and the shader samples a uniform point on a
    /// triangle, so a wall next to the lava would get most of its candidates from metres away. Their triangles are split
    /// into pieces of at most LavaSettings.maxTriangleArea, which lets the camera-distance importance of the alias table
    /// concentrate samples on the lava near the camera. Faces pointing down (a lava cube's bottom, inside the floor) emit
    /// nothing anyone sees and are dropped.
    /// </summary>
    internal sealed partial class EmissiveLights
    {
        public int LavaTriangles { get; private set; }

        private static readonly int GameEmissiveId = Shader.PropertyToID("_GameEmissive");

        private bool ExtractLava(SceneProxies.TracedSubmesh s, MeshSource src, List<Candidate> output, ref int dark)
        {
            var lava = SceneProxies.Lava;
            var m = s.Material;
            var mesh = s.Mesh;
            bool ours = SceneProxies.IsLavaProxy(m);

            // Radiance = texture x base. Our proxies: _MainTex x _Color (the game's additive colour), plus the EMISSIVE
            // part (_EmissiveTex x _GameEmissive) for acid, times the lava light scale. The game's opaque lava: as Extract.
            Color[] img, img2 = null;
            Vector4 st, st2 = new Vector4(1, 1, 0, 0);
            Color baseLe, base2 = Color.black;
            if (ours)
            {
                var tex = m.GetTexture("_MainTex");
                img = tex != null && tex.dimension == UnityEngine.Rendering.TextureDimension.Tex2D ? LowRes(tex) : null;
                var sc = m.GetTextureScale("_MainTex");
                var of = m.GetTextureOffset("_MainTex");
                st = new Vector4(sc.x, sc.y, of.x, of.y);
                baseLe = m.GetColor("_Color") * lava.lightScale;
                var ge = m.GetColor(GameEmissiveId);
                if (Lum(ge) > 0f)
                {
                    var etex = m.GetTexture("_EmissiveTex");
                    img2 = etex != null && etex.dimension == UnityEngine.Rendering.TextureDimension.Tex2D ? LowRes(etex) : null;
                    var sc2 = m.GetTextureScale("_EmissiveTex");
                    var of2 = m.GetTextureOffset("_EmissiveTex");
                    st2 = new Vector4(sc2.x, sc2.y, of2.x, of2.y);
                    base2 = ge * lava.lightScale;
                }
            }
            else
            {
                var tex = EmissionTexture(m, out st, out var tint, out bool _);
                img = tex != null ? LowRes(tex) : null;
                baseLe = m.GetColor("_EmissiveColor") * m.GetFloat("_EmissiveIntensity");
                baseLe = new Color(baseLe.r * tint.r, baseLe.g * tint.g, baseLe.b * tint.b);
            }
            Color avg = img != null ? Average(img) : Color.white;
            Color avg2 = img2 != null ? Average(img2) : Color.white;

            // Facing test in world space (at build time; lava doesn't rotate).
            var l2w = s.Space != null ? s.Space.localToWorldMatrix : Matrix4x4.identity;
            bool flip = l2w.determinant < 0f != SceneProxies.FrontCounterClockwise;

            var d = mesh.GetSubMesh(s.SubMesh);
            int count = d.indexCount - d.indexCount % 3;
            // Split area: at most maxTriangleArea, grown so the surface stays under maxTrianglesPerSurface pieces.
            double total = 0;
            for (int k = 0; k < count; k += 3)
            {
                int i0 = src.Index(d.indexStart + k) + d.baseVertex, i1 = src.Index(d.indexStart + k + 1) + d.baseVertex, i2 = src.Index(d.indexStart + k + 2) + d.baseVertex;
                if ((uint)i0 >= (uint)src.VertexCount || (uint)i1 >= (uint)src.VertexCount || (uint)i2 >= (uint)src.VertexCount) continue;
                Vector3 p0 = src.Position(i0);
                total += 0.5f * Vector3.Cross(l2w.MultiplyVector(src.Position(i1) - p0), l2w.MultiplyVector(src.Position(i2) - p0)).magnitude;
            }
            float maxArea = Mathf.Max(0.05f, lava.maxTriangleArea);
            int cap = Mathf.Max(16, lava.maxTrianglesPerSurface);
            if (total / maxArea > cap) maxArea = (float)(total / cap);

            var stack = new Stack<(Vector3 a, Vector3 b, Vector3 c, Vector2 ua, Vector2 ub, Vector2 uc)>();
            for (int k = 0; k < count; k += 3)
            {
                int i0 = src.Index(d.indexStart + k) + d.baseVertex;
                int i1 = src.Index(d.indexStart + k + 1) + d.baseVertex;
                int i2 = src.Index(d.indexStart + k + 2) + d.baseVertex;
                if ((uint)i0 >= (uint)src.VertexCount || (uint)i1 >= (uint)src.VertexCount || (uint)i2 >= (uint)src.VertexCount) continue;
                Vector3 p0 = src.Position(i0), p1 = src.Position(i1), p2 = src.Position(i2);
                var nw = Vector3.Cross(l2w.MultiplyVector(p1 - p0), l2w.MultiplyVector(p2 - p0));
                if (!(nw.sqrMagnitude > 1e-16f)) continue;
                if (flip) nw = -nw;
                if (nw.normalized.y < -0.5f) { dark++; continue; } // faces down: the bottom of a lava cube
                bool uv = src.HasUv;
                stack.Push((p0, p1, p2, uv ? src.Uv(i0) : Vector2.zero, uv ? src.Uv(i1) : Vector2.zero, uv ? src.Uv(i2) : Vector2.zero));
                while (stack.Count > 0)
                {
                    var t = stack.Pop();
                    Vector3 e1 = t.b - t.a, e2 = t.c - t.a;
                    float worldArea = 0.5f * Vector3.Cross(l2w.MultiplyVector(e1), l2w.MultiplyVector(e2)).magnitude;
                    if (worldArea > maxArea)
                    {
                        // Split the longest edge at its midpoint.
                        float lab = (t.b - t.a).sqrMagnitude, lbc = (t.c - t.b).sqrMagnitude, lca = (t.a - t.c).sqrMagnitude;
                        if (lab >= lbc && lab >= lca)
                        {
                            Vector3 mp = (t.a + t.b) * 0.5f; Vector2 mu = (t.ua + t.ub) * 0.5f;
                            stack.Push((t.a, mp, t.c, t.ua, mu, t.uc)); stack.Push((mp, t.b, t.c, mu, t.ub, t.uc));
                        }
                        else if (lbc >= lca)
                        {
                            Vector3 mp = (t.b + t.c) * 0.5f; Vector2 mu = (t.ub + t.uc) * 0.5f;
                            stack.Push((t.a, t.b, mp, t.ua, t.ub, mu)); stack.Push((t.a, mp, t.c, t.ua, mu, t.uc));
                        }
                        else
                        {
                            Vector3 mp = (t.c + t.a) * 0.5f; Vector2 mu = (t.uc + t.ua) * 0.5f;
                            stack.Push((t.a, t.b, mp, t.ua, t.ub, mu)); stack.Push((mp, t.b, t.c, mu, t.ub, t.uc));
                        }
                        continue;
                    }
                    float area = 0.5f * Vector3.Cross(e1, e2).magnitude;
                    if (!(area > 1e-8f) || float.IsInfinity(area)) continue;
                    Color tx = uv && img != null ? TriangleAverage(img, avg, st, t.ua, t.ub, t.uc) : avg;
                    var le = new Color(baseLe.r * tx.r, baseLe.g * tx.g, baseLe.b * tx.b);
                    if (img2 != null || base2.maxColorComponent > 0f)
                    {
                        Color tx2 = uv && img2 != null ? TriangleAverage(img2, avg2, st2, t.ua, t.ub, t.uc) : avg2;
                        le += new Color(base2.r * tx2.r, base2.g * tx2.g, base2.b * tx2.b);
                    }
                    float lum = Lum(le);
                    if (!(lum >= Settings.minRadiance)) { dark++; continue; }
                    output.Add(new Candidate { Src = s, P0 = t.a, E1 = e1, E2 = e2, Le = le, Area = area, Lum = lum });
                    LavaTriangles++;
                }
            }
            return true;
        }

        /// <summary>Texture average over a triangle's UVs: 8 lookups in the 32x32 copy, or the texture's average when the
        /// triangle covers all of it (tiled).</summary>
        private static Color TriangleAverage(Color[] img, Color avg, Vector4 st, Vector2 a, Vector2 b, Vector2 c)
        {
            Vector2 u0 = Tile(a, st), u1 = Tile(b, st), u2 = Tile(c, st);
            var ext = Vector2.Max(Vector2.Max(u0, u1), u2) - Vector2.Min(Vector2.Min(u0, u1), u2);
            if (ext.x >= 1f && ext.y >= 1f) return avg;
            var t = Sample(img, u0) + Sample(img, u1) + Sample(img, u2)
                  + Sample(img, (u0 + u1) * 0.5f) + Sample(img, (u1 + u2) * 0.5f) + Sample(img, (u2 + u0) * 0.5f)
                  + Sample(img, (u0 + u1 + u2) / 3f) * 2f;
            return t * (1f / 8f);
        }
    }
}
