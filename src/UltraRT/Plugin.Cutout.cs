using System.Linq;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

namespace UltraRT
{
    /// <summary>
    /// The cutout pass (docs/path-tracing.md, "Cutout pass"). DLSS RR's Finish keeps the game's own pixel where no trace
    /// sample lies at the game's depth; grass blades and grates thinner than a trace texel often have none, and their game
    /// pixels sparkled over the traced image (7-1's grass, white in its fog). While a cutout instance is in view
    /// (SceneProxies.CutoutPass.cs), one DispatchRays at screen resolution (CutoutTrace.raytrace, recorded by
    /// DlssRR.Execute just before Finish) traces a structure of the alpha-tested submeshes alone, up to the game's depth,
    /// from each pixel the trace samples don't already match; Finish draws a blade it finds as traced. Lava, sprites and
    /// the HUD's world parts keep the game's pixel: no blade lies in front of them.
    /// [PathTrace] CutoutPass (default on), -ultrart-cutout-pass on|off, bench plugin._cutoutPass=0.
    /// </summary>
    public partial class Plugin
    {
        private RayTracingShader _cutoutShader;
        private bool _cutoutPass = true;
        private bool _cutoutDebug;  // bench plugin._cutoutDebug=1: Finish paints the pixels the game keeps by what the ray found
        private bool _cutoutFlip;   // bench plugin._cutoutFlip=1 (diagnostic): flip the cutout rays' y
        private bool _cutoutBound;
        private int _cutoutLogs;
        private string _cutoutLogged;

        private void LoadCutoutShader(AssetBundle bundle)
        {
            _cutoutShader = bundle.LoadAllAssets<RayTracingShader>().FirstOrDefault(s => s.name == "CutoutTrace");
            if (_cutoutShader == null) Logger.LogWarning("CutoutTrace shader missing: no cutout pass (grass blades keep the game's pixel)");
            if (!_cutoutBound)
            {
                _cutoutBound = true;
                _cutoutPass = Config.Bind("PathTrace", "CutoutPass", true,
                    "Grass blades, grates and leaf cards the trace missed are drawn traced instead of as the game's own pixel " +
                    "(DLSS RR only; one extra ray per unmatched screen pixel while cutouts are in view; docs/path-tracing.md, " +
                    "\"Cutout pass\"). Overridden by -ultrart-cutout-pass on|off.").Value;
                var args = System.Environment.GetCommandLineArgs();
                for (int i = 0; i + 1 < args.Length; i++)
                    if (args[i].ToLowerInvariant() == "-ultrart-cutout-pass") _cutoutPass = args[i + 1] != "off";
            }
        }

        /// <summary>Before DlssRR.Execute: sync the cutout RTAS, and hand the pass to RR while a cutout is in view.</summary>
        private void CutoutPassFrame()
        {
            if (_rr == null) return;
            int n = 0;
            if (_proxies != null && _cutoutPass) { _proxies.UpdateCutouts(_camera); n = _proxies.CutoutsInView; }
            var rtas = _proxies?.CutoutRtas;
            bool on = _cutoutPass && _cutoutShader != null && rtas != null && n > 0;
            if (on) _cmd.BuildRayTracingAccelerationStructure(rtas);
            _rr.CutoutShader = _cutoutShader;
            _rr.CutoutRtas = rtas;
            _rr.CutoutOn = on;
            _rr.CutoutDebug = _cutoutDebug;
            _rr.CutoutFlip = _cutoutFlip;
            string state = on ? "on" : _cutoutPass ? "idle" : "off";
            if (state != _cutoutLogged && _cutoutLogs++ < 20)
            {
                _cutoutLogged = state;
                Logger.LogInfo($"Cutout pass: {state} ({_proxies?.CutoutInstances ?? 0} cutout instances in its RTAS{(on || !_cutoutPass ? "" : ", none in view")})");
            }
        }
    }
}
