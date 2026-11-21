using System.Collections.Generic;
using UnityEngine;

namespace UltraRT
{
    /// <summary>A texture's mean colour, read once per texture on the CPU (a blit at up to 256 x 256 and a synchronous
    /// ReadPixels: for scene builds and diagnostics, not per frame).</summary>
    internal static class TextureMean
    {
        private static readonly Dictionary<Texture, Color> Cache = new Dictionary<Texture, Color>();

        public static Color Of(Texture tex)
        {
            if (tex == null) return Color.white;
            if (Cache.TryGetValue(tex, out var c)) return c;
            if (Cache.Count > 256) Cache.Clear();
            int w = Mathf.Clamp(tex.width, 1, 256), h = Mathf.Clamp(tex.height, 1, 256);
            var rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
            var prev = RenderTexture.active;
            Texture2D t2 = null;
            try
            {
                Graphics.Blit(tex, rt);
                RenderTexture.active = rt;
                t2 = new Texture2D(w, h, TextureFormat.RGBA32, false, true);
                t2.ReadPixels(new Rect(0, 0, w, h), 0, 0);
                var px = t2.GetPixels32();
                double r = 0, g = 0, b = 0, a = 0;
                foreach (var p in px) { r += p.r; g += p.g; b += p.b; a += p.a; }
                double n = px.Length * 255.0;
                c = new Color((float)(r / n), (float)(g / n), (float)(b / n), (float)(a / n));
            }
            catch (System.Exception) { c = Color.white; }
            finally
            {
                RenderTexture.active = prev;
                RenderTexture.ReleaseTemporary(rt);
                if (t2 != null) Object.Destroy(t2);
            }
            Cache[tex] = c;
            return c;
        }
    }
}
