using System.Reflection;
using UnityEngine;

namespace UltraRT
{
    // MSAA feasibility probe (docs/aliasing.md, "MSAA on the game's raster"). Measurement only, off by default: bench
    // variant plugin._msaaProbe=4 (or 2, 8). The game renders its world camera into an MRT of its own render textures
    // (PostProcessV2_Handler.SetupRTs: mainTex ARGB32, reusableBufferA RG16 outline mask, viewNormal RGB565, and a 32-bit
    // depthBuffer), all created with antiAliasing = 1, so the game has no MSAA however QualitySettings is set. The probe
    // swaps those four targets for multisampled ones of the same formats (reusableBufferB stays single-sampled), re-binds
    // the cameras and the final pass, and logs what the game and our passes do with them. Colour targets resolve
    // automatically where they are sampled as textures (the final pass, the outline blits); the depth target does not:
    // anything that samples or copies depthBuffer (the game's blood/oil stains, heat waves' stencil, UltraRT's composite,
    // upsampler, volumetrics, viewmodel) sees a multisampled depth surface it was not written for. Setting the field back
    // to 0 makes the game rebuild its own targets (reinitializeTextures), as a resolution change does.
    public partial class Plugin
    {
        /// <summary>Probe only: MSAA sample count forced onto the game's world-camera targets (0 / 1 = the game's own).</summary>
        private int _msaaProbe;
        private int _msaaProbeActive = 1;
        private int _msaaProbeLogged;
        private static readonly FieldInfo PpReinit = typeof(PostProcessV2_Handler).GetField("reinitializeTextures", BindingFlags.NonPublic | BindingFlags.Instance);

        private void MsaaProbeUpdate()
        {
            if (_msaaProbe <= 1 && _msaaProbeActive <= 1) return;
            var pp = MonoSingleton<PostProcessV2_Handler>.Instance;
            if (pp == null || pp.mainTex == null || pp.mainCam == null) return;
            if (_msaaProbe <= 1)
            {
                // Back to the game's targets: it releases ours and rebuilds its own in the next SetupRTs (OnPreRender).
                if (pp.mainTex.antiAliasing > 1) PpReinit?.SetValue(pp, true);
                Logger.LogInfo($"MSAA probe: off, the game rebuilds its targets (was x{_msaaProbeActive})");
                _msaaProbeActive = 1;
                return;
            }
            if (pp.mainTex.antiAliasing == _msaaProbe) return; // in place (the game recreates them on a size change)
            int w = pp.mainTex.width, h = pp.mainTex.height;
            var probeDesc = new RenderTextureDescriptor(w, h, RenderTextureFormat.ARGB32, 0) { msaaSamples = _msaaProbe };
            int supported = SystemInfo.GetRenderTextureSupportedMSAASampleCount(probeDesc);
            Logger.LogInfo($"MSAA probe: game targets {w}x{h}: mainTex aa {pp.mainTex.antiAliasing}, depth aa {pp.depthBuffer?.antiAliasing}, " +
                           $"viewNormal {pp.viewNormal?.format} aa {pp.viewNormal?.antiAliasing}, reusableA {pp.reusableBufferA?.format}; " +
                           $"QualitySettings.antiAliasing {QualitySettings.antiAliasing}, mainCam.allowMSAA {pp.mainCam.allowMSAA}, " +
                           $"hudCam.allowMSAA {pp.hudCam?.allowMSAA}; ARGB32 x{_msaaProbe} supported as x{supported}");
            RenderTexture Make(string name, RenderTextureFormat fmt, int depth)
            {
                var rt = new RenderTexture(w, h, depth, fmt) { name = name + " (MSAA probe)", antiAliasing = _msaaProbe, filterMode = FilterMode.Point };
                rt.Create();
                return rt;
            }
            var oldMain = pp.mainTex; var oldA = pp.reusableBufferA; var oldN = pp.viewNormal; var oldD = pp.depthBuffer;
            pp.mainTex = Make("Main", RenderTextureFormat.ARGB32, 0);
            pp.reusableBufferA = Make("Reusable A", RenderTextureFormat.RG16, 0);
            pp.viewNormal = Make("View Normal", RenderTextureFormat.RGB565, 0);
            pp.depthBuffer = Make("Depth Buffer", RenderTextureFormat.Depth, 32);
            pp.mainCam.allowMSAA = true;
            if (pp.hudCam != null) pp.hudCam.allowMSAA = true;
            pp.buffers[0] = pp.mainTex.colorBuffer;
            pp.buffers[1] = pp.reusableBufferA.colorBuffer;
            pp.buffers[2] = pp.viewNormal.colorBuffer;
            pp.mainCam.SetTargetBuffers(pp.buffers, pp.depthBuffer.depthBuffer);
            pp.hudCam?.SetTargetBuffers(pp.mainTex.colorBuffer, pp.depthBuffer.depthBuffer);
            pp.postProcessV2_VSRM.SetTexture("_MainTex", pp.mainTex);
            try { pp.SetupOutlines(); } catch (System.Exception e) { Logger.LogWarning($"MSAA probe: SetupOutlines: {e.Message}"); }
            try { pp.onReinitialize?.Invoke(true); } catch (System.Exception e) { Logger.LogWarning($"MSAA probe: onReinitialize: {e.Message}"); }
            foreach (var rt in new[] { oldMain, oldA, oldN, oldD }) if (rt != null) { rt.Release(); Object.Destroy(rt); }
            _msaaProbeActive = _msaaProbe;
            Logger.LogInfo($"MSAA probe: world camera now renders into x{pp.mainTex.antiAliasing} targets (main {pp.mainTex.antiAliasing}, " +
                           $"depth {pp.depthBuffer.antiAliasing}, bindTextureMS {pp.depthBuffer.bindTextureMS}); view {(_viewEnabled ? Modes[_mode].name : "off")}");
            if (_msaaProbeLogged++ == 0)
                Application.logMessageReceived += (msg, stack, type) =>
                {
                    if (_msaaProbeActive > 1 && type != LogType.Log && _msaaProbeLogged < 40)
                    {
                        _msaaProbeLogged++;
                        Logger.LogWarning($"MSAA probe: Unity {type}: {msg}");
                    }
                };
        }
    }
}
