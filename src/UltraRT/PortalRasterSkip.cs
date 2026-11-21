using System;
using System.Collections;
using System.Reflection;
using HarmonyLib;
using ULTRAKILL.Portal;
using UnityEngine;

namespace UltraRT
{
    /// <summary>
    /// Skips the game's raster portal views while ray tracing replaces them (docs/portals.md, "Raster skip").
    ///
    /// PortalRenderV2.Render (the main camera's OnPreRender, via PortalManagerV2) renders every visible portal view with a
    /// second camera (PortalManagerV2.portalCamera): a full forward render of the far side per view, plus blood/oil
    /// composites, the space sky and the composite into "Portal Composite Color". In the replace modes every opaque pixel
    /// of those views is overwritten by the trace (the camera ray follows the portal: UltraRTPortals.hlsl), so that work
    /// (6.4 ms GPU and 6.6 ms render thread in 8-3 b with RT off) is thrown away. While <see cref="Active"/>, a prefix
    /// runs only the part of Render that feeds the main view (the portal-aware light upload for the game's PORTAL_LIGHTS
    /// shaders, which the game's transparents still use) and skips the views.
    ///
    /// Untouched: PortalManagerV2.LateUpdate / PortalRenderV2.Setup (the portal mesh, the depth prepass, the main camera's
    /// "Portal Draw" quads that put the portal plane into the game's depth), the scene and traversal, and every portal the
    /// trace doesn't have yet (a side that is not in the RTAS: Render runs as before for that frame). The game's own
    /// Render runs again the frame RT turns off (F7), leaves a replace mode, or the plugin unloads.
    /// </summary>
    internal static class PortalRasterSkip
    {
        /// <summary>Set by the plugin every frame before the game's OnPreRender callback (Plugin.OnCameraPreRender).</summary>
        public static volatile bool Active;
        /// <summary>The scene whose portal sides are checked (SceneProxies.IsPortalSideTraced).</summary>
        public static SceneProxies Proxies;
        public static int Skipped, RanVanilla, Failed;
        public static bool Hooked;
        public static string Error;

        private static MethodInfo _updatePortalLights;
        private static FieldInfo _tempLights, _portalLights, _mainCam;

        public static void Patch(Harmony harmony)
        {
            try
            {
                var t = typeof(PortalRenderV2);
                var render = AccessTools.Method(t, "Render", new[] { typeof(Camera) });
                _updatePortalLights = AccessTools.Method(t, "UpdatePortalLights");
                _tempLights = AccessTools.Field(t, "tempPortalLights");
                _portalLights = AccessTools.Field(t, "portalLights");
                _mainCam = AccessTools.Field(t, "mainCam");
                if (render == null || _updatePortalLights == null || _tempLights == null || _portalLights == null)
                {
                    Error = "PortalRenderV2.Render / UpdatePortalLights / light lists not found";
                    return;
                }
                harmony.Patch(render, prefix: new HarmonyMethod(typeof(PortalRasterSkip).GetMethod(nameof(RenderPrefix), BindingFlags.NonPublic | BindingFlags.Static)));
                Hooked = true;
            }
            catch (Exception e) { Error = e.Message; }
        }

        private static bool RenderPrefix(PortalRenderV2 __instance, Camera cam)
        {
            if (!Active || Proxies == null) return true;
            try
            {
                if (!AllViewsTraced(__instance)) { RanVanilla++; return true; }
                _mainCam?.SetValue(__instance, cam);
                // Render's first and last steps: the portal-aware lights for the main view's PORTAL_LIGHTS shaders, then
                // the per-frame light lists cleared (they would grow otherwise).
                _updatePortalLights.Invoke(__instance, null);
                (_tempLights.GetValue(__instance) as IList)?.Clear();
                (_portalLights.GetValue(__instance) as IList)?.Clear();
                Skipped++;
                return false;
            }
            catch (Exception)
            {
                Failed++;
                return true; // never break the game's portals
            }
        }

        /// <summary>Every view Render would draw this frame looks through a side the trace has in its RTAS.</summary>
        private static bool AllViewsTraced(PortalRenderV2 r)
        {
            var scene = r.scene;
            if (scene == null || !scene.nativeScene.valid || !scene.nativeScene.renderPortals.IsCreated) return true; // Render returns early itself
            var portals = scene.nativeScene.renderPortals;
            var views = r.renderDatas;
            if (!views.IsCreated) return true;
            int n = portals.Length;
            for (int i = 0; i < views.Length; i++)
            {
                int h = views[i].handleIndex;
                if (h < 0 || h >= n) return false;
                var handle = portals[h].handle;
                if (!Proxies.IsPortalSideTraced(handle.instanceId, handle.side == PortalSide.Exit)) return false;
            }
            return true;
        }
    }
}
