using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using UnityEngine.Rendering;

namespace UltraRT
{
    /// <summary>
    /// Screen-space effects wiring (Vfx.cs, docs/vfx.md): bloom, heat haze, shockwave distortion, underwater. Plugin.cs
    /// only has one-line hooks: ParseVfxArgs (Awake), LoadVfxShaders (LoadShaders), VfxFrameReset (OnCameraPreRender),
    /// VfxBloomSource (Trace, before the denoiser), TraceVfx (Trace, after the volumetrics), VfxHotkeys (OnUpdate),
    /// _vfx.Reset (ResetScene).
    ///   -ultrart-vfx on|off, -ultrart-bloom on|off, -ultrart-bloom-strength F, -ultrart-heathaze on|off,
    ///   -ultrart-heathaze-strength F, -ultrart-shockwaves on|off, -ultrart-shockwave-strength F, -ultrart-underwater on|off
    /// Ctrl+F2 toggles the whole pass, Shift+F2 bloom alone.
    /// </summary>
    public partial class Plugin
    {
        private readonly VfxSettings _vfxSettings = new VfxSettings();
        private Vfx _vfx;
        private CommandBuffer _vfxLateCmd; // after the game's transparents and the volumetric scatter
        private Camera _vfxLateCam;
        private Texture _lateTarget;       // PostProcessV2_Handler recreates its targets and strips AfterForwardAlpha buffers
        private RenderTexture _vfxHdr;     // this frame's HDR bloom source (null: the composite didn't write one)
        private Vector2 _vfxHdrScale = Vector2.one; // visible / stored size of it
        private int _vfxLastFrame = -10;
        private float _vfxNextHeat, _vfxNextLog, _vfxNextSummary;
        private readonly List<(Bounds bounds, Color color)> _vfxLava = new List<(Bounds, Color)>();
        private readonly List<Vector4> _vfxFlames = new List<Vector4>();
        private const int SlotVfx = 19, SlotVfxLate = 20;

        private void ParseVfxArgs()
        {
            _vfxSettings.ParseArgs();
            Logger.LogInfo($"Screen effects: {_vfxSettings}");
        }

        private void LoadVfxShaders(AssetBundle bundle)
        {
            _vfx?.Dispose();
            _vfx = null;
            var sh = bundle.LoadAllAssets<Shader>().FirstOrDefault(s => s.name == "UltraRT/Vfx");
            if (sh == null || !sh.isSupported)
            {
                Logger.LogWarning($"UltraRT/Vfx shader {(sh == null ? "missing from the bundle" : "unsupported")}: bloom, heat haze, shockwaves and the underwater look are off");
                return;
            }
            _vfx = new Vfx(sh, _vfxSettings);
        }

        /// <summary>Every frame from OnCameraPreRender, after VolumetricsFrameReset (so this buffer runs after the volumetric
        /// scatter): keeps the late buffer on the camera, empty unless the pass runs this frame.</summary>
        private void VfxFrameReset(Camera cam)
        {
            // The game strips every AfterForwardAlpha buffer when it recreates its render targets (resolution or
            // pixelization change): a new target means both late buffers have to be added again, in order.
            var target = MonoSingleton<PostProcessV2_Handler>.Instance?.mainTex;
            if (!ReferenceEquals(target, _lateTarget))
            {
                _lateTarget = target;
                if (_volLateCam != null && _volLateCmd != null)
                {
                    _volLateCam.RemoveCommandBuffer(CameraEvent.AfterForwardAlpha, _volLateCmd);
                    _volLateCam.AddCommandBuffer(CameraEvent.AfterForwardAlpha, _volLateCmd);
                }
                if (_vfxLateCam != null && _vfxLateCmd != null) _vfxLateCam.RemoveCommandBuffer(CameraEvent.AfterForwardAlpha, _vfxLateCmd);
                _vfxLateCam = null;
            }
            if (_vfxLateCmd == null) _vfxLateCmd = new CommandBuffer { name = "UltraRT Screen effects (after transparents)" };
            _vfxLateCmd.Clear();
            _vfxHdr = null;
            if (_vfxLateCam == cam) return;
            if (_vfxLateCam != null) _vfxLateCam.RemoveCommandBuffer(CameraEvent.AfterForwardAlpha, _vfxLateCmd);
            if (cam != null) cam.AddCommandBuffer(CameraEvent.AfterForwardAlpha, _vfxLateCmd);
            _vfxLateCam = cam;
        }

        private void VfxHotkeys()
        {
            if (!Input.GetKeyDown(KeyCode.F2)) return;
            if (CtrlHeld())
            {
                _vfxSettings.enabled = !_vfxSettings.enabled;
                Logger.LogInfo($"Screen effects: {(_vfxSettings.enabled ? "on" : "off")}");
                Toast($"Screen effects (bloom, heat haze, shockwaves, underwater) {(_vfxSettings.enabled ? "on" : "off")} (Ctrl+F2)");
            }
            else if (ShiftHeld())
            {
                _vfxSettings.bloom = !_vfxSettings.bloom;
                Logger.LogInfo($"Bloom: {(_vfxSettings.bloom ? "on" : "off")}");
                Toast($"Bloom {(_vfxSettings.bloom ? "on" : "off")} (Shift+F2)");
            }
        }

        /// <summary>Which modes get the pass: every mode that shows a lit image (not the debug views).</summary>
        private bool VfxFor(Kind kind) => _vfx != null && _vfxSettings.enabled && kind == Kind.Denoised && Modes[_mode].mode == (int)DenoiserDebugView.Final;

        /// <summary>
        /// Before the denoiser runs: hands its Composite the HDR bloom source to fill (relight and pt-dn: the composite
        /// has the image before the tone curve). DLSS RR leaves it out and bloom falls back to the
        /// displayed image.
        /// </summary>
        private void VfxBloomSource(Kind kind, int w, int h, bool rr, RenderTexture screenDepth)
        {
            _vfxHdr = null;
            _vfxHdrScale = Vector2.one;
            if (_denoiser == null) return;
            bool want = VfxFor(kind) && kind == Kind.Denoised && _vfxSettings.bloom && _vfxSettings.bloomStrength > 0f;
            var s = _vfxSettings;
            var p = new Vector4(Mathf.Max(0f, s.bloomExcess), Mathf.Max(0f, s.bloomEmission), s.bloomEmissionLo, Mathf.Max(s.bloomEmissionHi, s.bloomEmissionLo + 1e-3f));
            if (rr)
            {
                // DLSS RR (pathtrace; relight with Ctrl+F8): its Finish kernel has the image before the tone curve, after
                // the remodulation, at screen resolution.
                _denoiser.BloomTarget = null;
                if (_rr == null) return;
                if (want && screenDepth != null) _vfxHdr = _vfx.EnsureHdr(screenDepth.width, screenDepth.height);
                _rr.BloomTarget = _vfxHdr;
                _rr.BloomParams = p;
                return;
            }
            if (_rr != null) _rr.BloomTarget = null;
            if (want)
            {
                _vfxHdr = _vfx.EnsureHdr(w, h);
                _vfxHdrScale = new Vector2(1f / _guardScale.x, 1f / _guardScale.y); // trace resolution, with the guard band
            }
            _denoiser.BloomTarget = _vfxHdr;
            _denoiser.BloomParams = p;
        }

        // An explosion spawned (EffectLights' spawn queue): a shock front for the distortion pass.
        private readonly List<Explosion> _vfxExplosions = new List<Explosion>(4);
        // Glowing debris: the game's ExplosionChunk meshes waiting for their proxy, and when the last ones were thrown out.
        private readonly List<(Renderer r, int frame)> _debrisPending = new List<(Renderer, int)>();
        private float _debrisTime = -1e9f;
        private int _debrisMarked, _debrisLogs;
        private static readonly int DebrisGlowId = Shader.PropertyToID("_UltraRTDebrisGlow");

        /// <summary>Every traced frame: flags the proxies of new debris (made a few frames after the spawn) and publishes the
        /// glow, which cools from yellow-orange to dark red.</summary>
        private void UpdateDebrisGlow()
        {
            var s = _vfxSettings;
            float age = Time.time - _debrisTime;
            Vector4 glow = Vector4.zero;
            if (s.enabled && s.debrisGlow && age >= 0f && age < 6f * s.debrisCoolTime)
            {
                float k = Mathf.Max(0f, s.debrisIntensity) * Mathf.Pow(1f / 3f, age / Mathf.Max(0.05f, s.debrisCoolTime));
                float cool = Mathf.Clamp01(age / (3f * Mathf.Max(0.05f, s.debrisCoolTime))); // hue: hot yellow-orange -> deep red
                glow = new Vector4(k, k * Mathf.Lerp(0.55f, 0.12f, cool), k * Mathf.Lerp(0.12f, 0.01f, cool), 0f);
            }
            Shader.SetGlobalVector(DebrisGlowId, glow);
            if (_proxies == null) { _debrisPending.Clear(); return; }
            _proxies.PruneHotDebris();
            for (int i = _debrisPending.Count - 1; i >= 0; i--)
            {
                var (r, frame) = _debrisPending[i];
                bool done = r == null || Time.frameCount - frame > 90;
                if (!done && _proxies.MarkHotDebris(r)) { done = true; _debrisMarked++; }
                if (done) _debrisPending.RemoveAt(i);
            }
            if (_debrisMarked > 0 && _debrisPending.Count == 0 && _debrisLogs < 5)
            {
                _debrisLogs++;
                Logger.LogInfo($"Screen effects: {_debrisMarked} explosion chunks glow");
                _debrisMarked = 0;
            }
        }

        private void VfxOnSpawn(GameObject go)
        {
            if (go.TryGetComponent<ExplosionChunk>(out _))
            {
                if (!_vfxSettings.enabled || !_vfxSettings.debrisGlow) return;
                foreach (var r in go.GetComponentsInChildren<MeshRenderer>(true))
                    if (r != null && _debrisPending.Count < 256) _debrisPending.Add((r, Time.frameCount));
                _debrisTime = Time.time;
                return;
            }
            if (_vfx == null || !_vfxSettings.enabled || !_vfxSettings.shockwaves) return;
            go.GetComponentsInChildren(true, _vfxExplosions);
            if (_vfxExplosions.Count == 0) return;
            float size = 0f;
            foreach (var e in _vfxExplosions) if (e != null) size = Mathf.Max(size, e.maxSize);
            _vfxExplosions.Clear();
            if (size <= 0f) return;
            _vfx.AddShock(go.transform.position, size, Time.time);
            if (Time.realtimeSinceStartup >= _vfxNextLog)
            {
                _vfxNextLog = Time.realtimeSinceStartup + 2f;
                Logger.LogInfo(FormattableString.Invariant($"Screen effects: shockwave for '{go.name}' at {go.transform.position} size {size:F1}"));
            }
        }

        // ---- underwater: UnderwaterController.touchingWaters (private) -> the Water the camera is in

        private static readonly FieldInfo TouchingWatersField = typeof(UnderwaterController).GetField("touchingWaters", BindingFlags.NonPublic | BindingFlags.Instance);
        private Water _vfxWater;
        private float _vfxWaterSurface;
        private Collider[] _vfxWaterColliders;

        private bool VfxUnderwater(Vector3 cam, out float surface, out Color color)
        {
            surface = 0f;
            color = new Color(0f, 0.5f, 1f);
            UnderwaterController uc = null;
            try { uc = MonoSingleton<UnderwaterController>.Instance; } catch { /* no player in this scene */ }
            if (uc == null || !uc.inWater) { _vfxWater = null; return false; }
            Water water = null;
            if (TouchingWatersField?.GetValue(uc) is HashSet<Water> set)
                foreach (var w in set) { if (w != null) { water = w; break; } }
            if (water == null) return false;
            if (!ReferenceEquals(water, _vfxWater))
            {
                _vfxWater = water;
                _vfxWaterColliders = water.GetComponentsInChildren<Collider>();
            }
            // The surface: the top of the water collider the camera is in (else the highest top above the camera).
            float best = float.NegativeInfinity, any = float.NegativeInfinity;
            foreach (var c in _vfxWaterColliders)
            {
                if (c == null || !c.enabled) continue;
                var b = c.bounds;
                any = Mathf.Max(any, b.max.y);
                if (cam.x >= b.min.x - 0.5f && cam.x <= b.max.x + 0.5f && cam.z >= b.min.z - 0.5f && cam.z <= b.max.z + 0.5f && cam.y <= b.max.y + 0.5f && cam.y >= b.min.y - 0.5f)
                    best = Mathf.Max(best, b.max.y);
            }
            surface = !float.IsNegativeInfinity(best) ? best : any;
            if (float.IsNegativeInfinity(surface)) return false;
            _vfxWaterSurface = surface;
            color = water.clr;
            return true;
        }

        private SceneProxies _vfxWaterLogged;

        /// <summary>Once per level (bench runs only): the level's water volumes, for placing underwater test poses.</summary>
        private void VfxLogWater()
        {
            if (ReferenceEquals(_vfxWaterLogged, _proxies) || !Bench.BenchSafety.Active) return;
            _vfxWaterLogged = _proxies;
            int n = 0;
            foreach (var w in FindObjectsOfType<Water>())
            {
                if (w == null || n++ >= 12) continue;
                var b = new Bounds();
                bool any = false;
                foreach (var c in w.GetComponentsInChildren<Collider>()) { if (!any) { b = c.bounds; any = true; } else b.Encapsulate(c.bounds); }
                Logger.LogInfo(FormattableString.Invariant($"Screen effects: water '{Bench.GameControl.PathOf(w.transform)}' {b.min}..{b.max} colour {w.clr}"));
            }
        }

        private void VfxGatherHeat(Vector3 cam)
        {
            if (Time.realtimeSinceStartup < _vfxNextHeat) return;
            _vfxNextHeat = Time.realtimeSinceStartup + 0.2f;
            _vfx.ClearHeat();
            if (!_vfxSettings.heatHaze) return;
            float range2 = _vfxSettings.heatRange * _vfxSettings.heatRange;
            if (_proxies != null && _proxies.LavaCount > 0 && SceneProxies.Lava.active)
            {
                _proxies.ActiveLava(_vfxLava);
                // nearest first: the shader takes at most Vfx.MaxBoxes
                _vfxLava.Sort((a, b) => a.bounds.SqrDistance(cam).CompareTo(b.bounds.SqrDistance(cam)));
                foreach (var (b, _) in _vfxLava)
                    if (b.SqrDistance(cam) < range2) _vfx.AddHeatBox(b, 1f);
            }
            if (_effectLights != null)
            {
                _vfxFlames.Clear();
                _effectLights.CollectFlames(_vfxFlames, cam, 30f);
                foreach (var f in _vfxFlames) _vfx.AddHeatPoint(new Vector3(f.x, f.y, f.z), f.w);
            }
        }

        /// <summary>End of Trace (after the RT composite, the fog pass and the pre-alpha copy): copies the target for the
        /// bloom's "what the transparents added" term and records the late pass.</summary>
        private void TraceVfx(Kind kind)
        {
            if (!VfxFor(kind) || _camera == null || _vfxLateCmd == null || _vfxLateCam != _camera) return;
            var target = MonoSingleton<PostProcessV2_Handler>.Instance?.mainTex;
            if (target == null || _depthCopy == null || target.width != _depthCopy.width || target.height != _depthCopy.height) return;
            if (Time.frameCount - _vfxLastFrame > 2) _vfxNextHeat = 0f;
            _vfxLastFrame = Time.frameCount;
            var camPos = _camera.transform.position;
            VfxGatherHeat(camPos);
            VfxLogWater();

            var frame = new VfxFrame
            {
                camera = _camera,
                target = target,
                // The volumetric pass ran this frame, in its late buffer (added before this one): bloom from before the haze.
                preHaze = _volumetrics != null && _volumetrics.PreHazeFrame == Time.frameCount && _volumetrics.PreHazeCopy != null
                          && _volumetrics.PreHazeCopy.width == target.width && _volumetrics.PreHazeCopy.height == target.height
                    ? _volumetrics.PreHazeCopy : null,
                hdr = _vfxHdr,
                hdrScale = _vfxHdrScale,
                time = Time.time,
                waterLightDir = Vector3.up,
            };
            frame.underwater = _vfxSettings.underwater && VfxUnderwater(camPos, out frame.waterSurface, out frame.waterColor);
            if (frame.underwater)
            {
                // Shafts lean with the sun when one shines from above.
                for (int i = 0; i < _lightCount; i++)
                {
                    var l0 = _lightData[i * 4];
                    if (l0.w != 0f || _lightData[i * 4 + 3].z > 0.5f || l0.y < 0.3f) continue;
                    frame.waterLightDir = new Vector3(l0.x, l0.y, l0.z);
                    break;
                }
            }
            if (HdrLateFrame(out frame.hdrFx, out frame.hdrTop)) frame.hdrGain = _vfx.EnsureHdrGain(target);
            else if (_hdrNativeNow) // native float HDR (Plugin.Hdr.cs): pass 5
            {
                frame.native = true;
                frame.hdrFx = new Vector4(Mathf.Max(0f, _hdrEffects - 1f), _hdrFxLo, Mathf.Max(_hdrFxHi, _hdrFxLo + 1e-3f), _hdrBloom);
                frame.hdrTop = new Vector4(HdrTopDisplay(), _hdrGainMax, 0f, 0f);
            }
            if (!_vfx.Wanted(frame)) return;
            if (Time.realtimeSinceStartup >= _vfxNextSummary)
            {
                _vfxNextSummary = Time.realtimeSinceStartup + 10f;
                Logger.LogInfo($"Screen effects: {VfxSummary()}; HDR source {(_vfxHdr != null ? $"{_vfxHdr.width}x{_vfxHdr.height}" : "none (displayed image)")}");
            }
            bool bloom = _vfxSettings.bloom && _vfxSettings.bloomStrength > 0f;
            _gpu?.Begin(_cmd, SlotVfx);
            if (bloom || frame.hdrGain != null || frame.native)
            {
                frame.pre = _vfx.EnsurePre(target);
                _cmd.CopyTexture(target, frame.pre);
            }
            _gpu?.End(_cmd, SlotVfx);
            _gpu?.Begin(_vfxLateCmd, SlotVfxLate);
            _vfx.Execute(_vfxLateCmd, frame);
            _gpu?.End(_vfxLateCmd, SlotVfxLate);
            if (frame.hdrGain != null) HdrSubmitLate(_vfxLateCmd, frame.hdrGain, frame.hdrTop);
        }

        /// <summary>For bench logs.</summary>
        private string VfxSummary() => _vfx == null ? "screen effects unavailable"
            : $"{_vfxSettings}; heat boxes {_vfx.HeatBoxes}, fires {_vfx.HeatPoints}, shockwaves {_vfx.ActiveShocks}, underwater {(_vfxWater != null ? FormattableString.Invariant($"yes (surface {_vfxWaterSurface:F1}, colour {_vfxWater.clr})") : "no")}";
    }
}
