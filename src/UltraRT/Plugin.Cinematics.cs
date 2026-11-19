using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx;
using HarmonyLib;
using UltraRT.Bench;
using UltraRT.Cinematics;
using ULTRAKILL.Cheats;
using UnityEngine;

namespace UltraRT
{
    /// <summary>Offline render view mode. Pathtrace / Relight / Current accumulate to convergence at full resolution.
    /// Off: the game's own image (ray tracing off), for before / after pairs. Live: the current view mode exactly as played
    /// (the real-time pipeline: trace scale, denoiser, DLSS RR / upsampler), one rendered frame per output frame.</summary>
    public enum CineRenderMode { Pathtrace = 0, Relight = 1, Current = 2, Off = 3, Live = 4 }
    public enum CineExposure { Auto = 0, Locked = 1 }

    /// <summary>Cinematic camera and offline render settings (docs/cinematics.md). Config section [Cinematics]; a bench
    /// scenario's "Cinematic" block overrides them for that scenario.</summary>
    public sealed class CineSettings
    {
        public CineRenderMode mode = CineRenderMode.Pathtrace;
        public float fps = 60f;
        /// <summary>Frames accumulated per anti-aliasing (or motion-blur) sample: the samples per pixel, times <see cref="paths"/>.</summary>
        public int samples = 32;
        /// <summary>Anti-aliasing samples per output frame (sub-pixel camera jitter), or motion-blur time steps.</summary>
        public int aa = 4;
        /// <summary>Path tracing: paths per pixel per accumulated frame, and bounces per path.</summary>
        public int paths = 1;
        public int bounces = 2;
        public bool cacheTail = true;
        /// <summary>Output size as a fraction of the screen (below 1: supersampled, box-filtered down).</summary>
        public float outputScale = 1f;
        public bool motionBlur = false;
        /// <summary>Shutter as a fraction of the frame time (0.5 = 180 degrees).</summary>
        public float shutter = 0.5f;
        public CineExposure exposure = CineExposure.Auto;
        /// <summary>Game time per path second (playback and render): 1 normal, 0.25 slow motion, 0 frozen world.</summary>
        public float timeScale = 1f;
        public bool png16 = false;
        public bool encode = true;
        public int crf = 16;
        /// <summary>Render only this many seconds of the path (0 = all), from <see cref="renderStart"/>.</summary>
        public float renderSeconds = 0f;
        public float renderStart = 0f;
        /// <summary>Frames rendered at the first pose before the first output frame (radiance cache, exposure).</summary>
        public int preroll = 60;
        // Free camera
        public float moveSpeed = 6f;
        public float lookSpeed = 1f;
        /// <summary>While the cinematic camera is on: enemies don't see the player and the player takes no damage.</summary>
        public bool protectPlayer = true;
        /// <summary>Keep V1's third-person model in the traced scene while the camera is free: its reflection (mirrors,
        /// water, metal) and its shadow stay where the player stands. Camera rays still never see the model itself, so
        /// frame it from near the player's eye, or only through the reflection.</summary>
        public bool tracePlayer = false;
        /// <summary>Paired capture (accumulating modes; bench "Paired"): every output frame is also rendered with ray
        /// tracing off, into this folder, while the world is held, so the game's image and the traced image show the
        /// identical world state frame for frame although the world runs. Null: off.</summary>
        [Newtonsoft.Json.JsonIgnore] public string pairOffDir;
        /// <summary>Paired capture windows (path seconds): the traced image is rendered from pairOnFrom on, the game's
        /// image until pairOffUntil. An edit that switches or sweeps from one to the other only needs both in between;
        /// before pairOnFrom one traced frame per output frame still runs (uncaptured), so exposure and the radiance
        /// cache are where they would be.</summary>
        public float pairOnFrom = float.NegativeInfinity, pairOffUntil = float.PositiveInfinity;
        public CineSettings Clone() => (CineSettings)MemberwiseClone();
    }

    /// <summary>
    /// Cinematic camera (docs/cinematics.md): a free camera (Home) that leaves the player frozen where it stood, keys with
    /// a duration and easing per segment, smooth playback (CineSpline), saved paths per level, and the offline renderer
    /// (Plugin.CineRender.cs). The camera is posed in Application.onBeforeRender, after every LateUpdate (as the bench's
    /// GameControl does), so the game's own camera code cannot fight it and every temporal filter sees one camera matrix per
    /// frame.
    /// </summary>
    public partial class Plugin
    {
        private enum CineState { Off, Free, Play, Render }

        private readonly CineSettings _cine = new CineSettings();
        private CineState _cineState;
        private readonly List<CineKey> _cineKeys = new List<CineKey>();
        private CineSpline _cineSpline;
        private bool _cineSplineDirty = true;
        private int _cineSel = -1;
        private string _cineName;
        private CinePathFile _cineMeta;
        private bool _cineShowHelp = true, _cineHideOverlay;
        private int _cineLoadIndex = -1;
        private float _cineNewArmed;

        // The camera this frame (applied in onBeforeRender).
        private CineSample _cineCur;
        private float _cineYaw, _cinePitch, _cineRoll;
        private Vector2 _cineJitter;   // sub-pixel projection offset (render), in pixels
        private bool _cineProjSet;
        private float _cinePlayT;
        private bool _cineHooked, _cineOwnsControl, _cineBench;
        private Vector3 _cinePrevPos;
        private Quaternion _cinePrevRot = Quaternion.identity;

        // Restored on exit.
        private CamPose _cineSavedPose;
        private float _cineSavedFov;
        private Vector3 _cineSavedCamLocalPos;
        private Quaternion _cineSavedCamLocalRot;
        private int _cineSavedHudMask = int.MinValue;
        private Camera _cineHudCam;
        private bool _cineSavedVm, _cineSavedPlayerModel = true;
        private readonly List<Canvas> _cineCanvases = new List<Canvas>();
        private bool _cineCanvasesHidden;
        private float _cineSavedSpeed = 1f;
        private bool _cineSpeedSet;
        private object _cineSavedBlind, _cineSavedInvincible;
        private bool _cineCheatsSet;
        private Vector3 _cineEye = new Vector3(0f, 1f, 0f);
        // A take that moves the player (bench "Player"): its velocity for V1's walk animation; and third-person shots.
        private bool _cinePlayerDriven, _cineShowPlayer, _cineShowPlayerMask, _cinePlayerRestart;
        private Vector3 _cinePlayerVel;
        private float _cinePlayerLastT = float.MaxValue;
        private readonly List<Collider> _cineShopTriggers = new List<Collider>();
        private readonly List<GameObject> _cineShopCanvases = new List<GameObject>();
        private Animator _cinePlayerAnimator;
        private const int PlayerModelLayer = 30;

        /// <summary>The overlay is not drawn on frames the offline renderer captures.</summary>
        private bool _cineHideGui;

        private static readonly FieldInfo BlindLast = AccessTools.Field(typeof(BlindEnemies), "_lastInstance");
        private static readonly FieldInfo InvincibleLast = AccessTools.Field(typeof(Invincibility), "_lastInstance");

        private string CineDir(string scene) => Path.Combine(_outDir, "cinematics", CineSanitize(scene));
        private static string CineSanitize(string s) => new string((s ?? "unknown").Select(c => char.IsLetterOrDigit(c) || c == '-' ? c : '_').ToArray());

        // ---------------------------------------------------------------- settings menu

        private void CineSettingsMenu(SettingsMenu m)
        {
            m.Group = "Cinematics (camera paths and offline render: docs/cinematics.md)";
            m.Choice("Cinematics", "Mode", "Render: view mode", () => _cine.mode, v => _cine.mode = v, v => v.ToString(),
                v => v == CineRenderMode.Pathtrace ? "pathtrace" : v == CineRenderMode.Relight ? "relight" : v == CineRenderMode.Off ? "ray tracing off (the game's image)"
                    : v == CineRenderMode.Live ? "as played (the current view mode, real-time pipeline)" : "the current view mode");
            m.Float("Cinematics", "Fps", "Render: frames per second", 12f, 120f, 0, () => _cine.fps, v => _cine.fps = Mathf.Round(v));
            m.Float("Cinematics", "Samples", "Render: accumulated frames per AA sample (spp)", 1f, 256f, 0, () => _cine.samples, v => _cine.samples = Mathf.Clamp(Mathf.RoundToInt(v), 1, 255));
            m.Float("Cinematics", "AA", "Render: anti-aliasing / motion-blur samples per frame", 1f, 16f, 0, () => _cine.aa, v => _cine.aa = Mathf.Clamp(Mathf.RoundToInt(v), 1, 64));
            m.Float("Cinematics", "Paths", "Render (pathtrace): paths per pixel per frame", 1f, 4f, 0, () => _cine.paths, v => _cine.paths = Mathf.Clamp(Mathf.RoundToInt(v), 1, 4));
            m.Float("Cinematics", "Bounces", "Render (pathtrace): bounces", 1f, 4f, 0, () => _cine.bounces, v => _cine.bounces = Mathf.Clamp(Mathf.RoundToInt(v), 1, 4));
            m.Bool("Cinematics", "CacheTail", "Render (pathtrace): radiance cache after the last bounce", () => _cine.cacheTail, v => _cine.cacheTail = v);
            m.Float("Cinematics", "OutputScale", "Render: output size (below 1 = supersampled)", 0.25f, 1f, 2, () => _cine.outputScale, v => _cine.outputScale = v);
            m.Bool("Cinematics", "MotionBlur", "Render: motion blur (AA samples spread over the shutter)", () => _cine.motionBlur, v => _cine.motionBlur = v);
            m.Float("Cinematics", "Shutter", "Render: shutter (fraction of a frame)", 0.05f, 1f, 2, () => _cine.shutter, v => _cine.shutter = v);
            m.Choice("Cinematics", "Exposure", "Render: exposure", () => _cine.exposure, v => _cine.exposure = v, v => v.ToString(),
                v => v == CineExposure.Auto ? "Auto (adapts once per frame)" : "Locked (as when Render starts)");
            m.Float("Cinematics", "TimeScale", "Game speed during playback / render (0 = frozen)", 0f, 2f, 2, () => _cine.timeScale, v => _cine.timeScale = v);
            m.Bool("Cinematics", "Png16", "Render: 16-bit PNG", () => _cine.png16, v => _cine.png16 = v);
            m.Bool("Cinematics", "Encode", "Render: encode MP4 with ffmpeg (when found)", () => _cine.encode, v => _cine.encode = v);
            m.Float("Cinematics", "Crf", "Render: H.264 quality (CRF, lower = better)", 0f, 35f, 0, () => _cine.crf, v => _cine.crf = Mathf.RoundToInt(v));
            m.Float("Cinematics", "MoveSpeed", "Free camera speed (m/s; mouse wheel)", 0.5f, 40f, 1, () => _cine.moveSpeed, v => _cine.moveSpeed = v);
            m.Float("Cinematics", "LookSpeed", "Free camera mouse sensitivity", 0.1f, 4f, 2, () => _cine.lookSpeed, v => _cine.lookSpeed = v);
            m.Bool("Cinematics", "ProtectPlayer", "Enemies ignore the player while the camera is free", () => _cine.protectPlayer, v => _cine.protectPlayer = v);
            m.Bool("Cinematics", "TracePlayer", "V1's reflection and shadow stay while the camera is free", () => _cine.tracePlayer, v =>
            {
                _cine.tracePlayer = v;
                if (_cineState != CineState.Off) SceneProxies.TracePlayerModel = v && _cineSavedPlayerModel;
            });
            m.Action("Cinematic camera on / off  [Home]", () => { if (_cineState == CineState.Off) CineEnter(); else if (_cineState != CineState.Render) CineExit(); });
            m.Action("Cinematic: play the path  [P]", () => CinePlay(0f));
            m.Action("Cinematic: render the path  [End]", CineStartRender);
            m.Action("Cinematic: save the path  [Ctrl+S]", CineSave);
            m.Action("Cinematic: load the next saved path  [L]", CineLoadNext);
        }

        private static readonly string[] CineKeybinds =
        {
            "Home|cinematic camera on / off (docs/cinematics.md)",
            "Cinematic: WASD, Q/E, mouse|fly (Shift fast, Alt slow; wheel: speed); Z/C roll, X level; R/F zoom, V reset zoom",
            "Cinematic: K / U / Delete|add key after the selected / update it / delete it",
            "Cinematic: Left / Right|select key and jump to it; Up/Down: segment duration (Shift: x4); T: easing; G: even speed",
            "Cinematic: P / Shift+P|play from the selected key / from the start; Ctrl+S save, L load next, N N new, H overlay",
            "Cinematic: End|render the path offline (End again cancels)",
        };

        // ---------------------------------------------------------------- state

        private bool CineInLevel() => GameControl.Player != null && GameControl.Cam != null && GameControl.Cam.cam != null;

        /// <summary>Home: the free camera starts where the game's camera is.</summary>
        private void CineEnter(bool bench = false)
        {
            if (_cineState != CineState.Off) return;
            if (!CineInLevel()) { Toast("Cinematic camera: no player in this scene"); return; }
            var cc = GameControl.Cam;
            var cam = cc.cam;
            _cineBench = bench;
            _cineEye = cam.transform.position - GameControl.Player.transform.position;
            _cineSavedPose = GameControl.CurrentPose();
            _cineSavedFov = cam.fieldOfView;
            _cineSavedCamLocalPos = cam.transform.localPosition;
            _cineSavedCamLocalRot = cam.transform.localRotation;
            _cineCur = new CineSample { Position = cam.transform.position, Rotation = cam.transform.rotation, Fov = cam.fieldOfView };
            CineSetEuler(_cineCur.Rotation);
            _cinePrevPos = _cineCur.Position;
            _cinePrevRot = _cineCur.Rotation;
            // The bench has already taken the player over; otherwise we freeze it the way the bench does (input locked,
            // NewMovement off, kinematic body).
            _cineOwnsControl = !bench;
            if (_cineOwnsControl) GameControl.TakeControl(_cineSavedPose);
            if (!_cineHooked) { Application.onBeforeRender += CineBeforeRender; _cineHooked = true; }
            // V1 stays where the player stood: keep it out of the traced scene (shadow, reflections) while the camera flies,
            // unless the shot wants it there (TracePlayer: a mirror shot with the player standing in front of the mirror).
            _cineSavedPlayerModel = SceneProxies.TracePlayerModel;
            SceneProxies.TracePlayerModel = _cine.tracePlayer && _cineSavedPlayerModel;
            _cineSavedVm = _vmEnabled;
            _vmEnabled = false;
            CineHideHud(true);
            if (_cine.protectPlayer && !bench) CineProtect(true);
            _cineState = CineState.Free;
            if (_cineKeys.Count == 0 && _cineMeta == null && !bench) CineLoadLatest();
            Logger.LogInfo($"Cinematic camera on at {_cineCur.Position} fov {_cineCur.Fov:F1} (eye offset {_cineEye})");
            if (!bench) Toast("Cinematic camera on (Home). K adds a key, P plays, End renders, H hides the help");
        }

        /// <summary>Leave the cinematic camera and put back everything it changed. Safe to call twice and from any state.</summary>
        private void CineExit()
        {
            if (_cineState == CineState.Render) _cineCancel = true; // the render loop restores its own settings
            var was = _cineState;
            _cineState = CineState.Off;
            if (_cineHooked) { Application.onBeforeRender -= CineBeforeRender; _cineHooked = false; }
            var cc = GameControl.Cam;
            try
            {
                if (cc != null && cc.cam != null)
                {
                    var cam = cc.cam;
                    if (_cineProjSet) { cam.ResetProjectionMatrix(); _cineProjSet = false; }
                    cam.fieldOfView = _cineSavedFov > 1f ? _cineSavedFov : cam.fieldOfView;
                    if (cam.transform != cc.transform)
                    {
                        cam.transform.localPosition = _cineSavedCamLocalPos;
                        cam.transform.localRotation = _cineSavedCamLocalRot;
                    }
                    cc.transform.localPosition = cc.defaultPos;
                    if (_cineOwnsControl)
                    {
                        cc.ResetCamera(_cineSavedPose.Yaw, _cineSavedPose.Pitch);
                        cc.ApplyRotations();
                    }
                }
            }
            catch (Exception e) { Logger.LogWarning($"Cinematic camera: restoring the camera failed: {e.Message}"); }
            if (_cineOwnsControl) { GameControl.ReleaseControl(); _cineOwnsControl = false; }
            SceneProxies.TracePlayerModel = _cineSavedPlayerModel;
            CineShowPlayer(false);
            CineShopScreensOff(false);
            _cinePlayerDriven = false;
            _cinePlayerLastT = float.MaxValue;
            _cinePlayerAnimator = null;
            _vmEnabled = _cineSavedVm;
            CineHideHud(false);
            CineProtect(false);
            CineSetSpeed(-1f);
            _cineJitter = Vector2.zero;
            _cineHideGui = false;
            if (was != CineState.Off)
            {
                CineCut();
                Logger.LogInfo("Cinematic camera off");
                if (!_cineBench) Toast("Cinematic camera off");
            }
            _cineBench = false;
        }

        /// <summary>A new level, or the level reloaded: nothing of the old one may stay changed.</summary>
        private void CineOnSceneChanged()
        {
            if (_cineState != CineState.Off) { Logger.LogInfo("Cinematic camera: level changed, restoring"); CineExit(); }
            _cineCanvases.Clear();
            _cineCanvasesHidden = false;
            _cineSavedHudMask = int.MinValue;
            _cineHudCam = null;
            _cineSpeedSet = false;
            _cineKeys.Clear();
            _cineMeta = null;
            _cineName = null;
            _cineSel = -1;
            _cineLoadIndex = -1;
            _cineSplineDirty = true;
        }

        /// <summary>HUD canvases (screen space) and the viewmodel (the HUD camera) hidden while the camera is free.</summary>
        private void CineHideHud(bool hide)
        {
            try
            {
                var pp = MonoSingleton<PostProcessV2_Handler>.Instance;
                var hud = pp != null ? pp.hudCam : null;
                if (hide)
                {
                    if (hud != null && _cineSavedHudMask == int.MinValue) { _cineHudCam = hud; _cineSavedHudMask = hud.cullingMask; hud.cullingMask = 0; }
                    if (!_cineCanvasesHidden)
                    {
                        _cineCanvases.Clear();
                        foreach (var c in FindObjectsOfType<Canvas>())
                            if (c != null && c.isRootCanvas && c.enabled && c.renderMode != RenderMode.WorldSpace) { c.enabled = false; _cineCanvases.Add(c); }
                        _cineCanvasesHidden = true;
                    }
                }
                else
                {
                    if (_cineHudCam != null && _cineSavedHudMask != int.MinValue) _cineHudCam.cullingMask = _cineSavedHudMask;
                    _cineSavedHudMask = int.MinValue;
                    _cineHudCam = null;
                    foreach (var c in _cineCanvases) if (c != null) c.enabled = true;
                    _cineCanvases.Clear();
                    _cineCanvasesHidden = false;
                }
            }
            catch (Exception e) { Logger.LogWarning($"Cinematic camera: HUD {(hide ? "hide" : "restore")} failed: {e.Message}"); }
        }

        /// <summary>The viewmodel back in view while the camera is free (bench "ShowWeapon", for a camera the player
        /// rides): the HUD camera draws again and the viewmodel is ray traced as in play; the HUD's own canvases, which
        /// the HUD camera draws too, are switched off. CineExit puts everything back (CineHideHud(false)).</summary>
        private void CineShowWeapon()
        {
            try
            {
                _vmEnabled = _cineSavedVm;
                if (_cineHudCam == null || _cineSavedHudMask == int.MinValue) { Logger.LogWarning("Cinematic camera: no HUD camera, the weapon stays hidden"); return; }
                _cineHudCam.cullingMask = _cineSavedHudMask;
                int n = 0;
                foreach (var c in FindObjectsOfType<Canvas>())
                    if (c != null && c.isRootCanvas && c.enabled && (c.worldCamera == _cineHudCam || c.gameObject.layer == 13) && !(c.GetComponentInParent<ShopZone>() != null))
                    {
                        c.enabled = false;
                        _cineCanvases.Add(c);
                        n++;
                    }
                Logger.LogInfo($"Cinematic camera: weapon shown (HUD camera mask 0x{_cineSavedHudMask:X}), {n} HUD canvas(es) hidden");
            }
            catch (Exception e) { Logger.LogWarning($"Cinematic camera: show weapon failed: {e.Message}"); }
        }

        /// <summary>Enemies blind and the player invincible, without the cheat menu: only the cheats' static state is swapped,
        /// so nothing is saved and the level is not marked as cheated.</summary>
        private void CineProtect(bool on)
        {
            try
            {
                if (on && !_cineCheatsSet && BlindLast != null && InvincibleLast != null)
                {
                    _cineSavedBlind = BlindLast.GetValue(null);
                    _cineSavedInvincible = InvincibleLast.GetValue(null);
                    var b = new BlindEnemies();
                    AccessTools.Property(typeof(BlindEnemies), "IsActive").SetValue(b, true);
                    var i = new Invincibility();
                    AccessTools.Property(typeof(Invincibility), "IsActive").SetValue(i, true);
                    BlindLast.SetValue(null, b);
                    InvincibleLast.SetValue(null, i);
                    _cineCheatsSet = true;
                }
                else if (!on && _cineCheatsSet)
                {
                    BlindLast.SetValue(null, _cineSavedBlind);
                    InvincibleLast.SetValue(null, _cineSavedInvincible);
                    _cineCheatsSet = false;
                }
            }
            catch (Exception e) { Logger.LogWarning($"Cinematic camera: player protection failed: {e.Message}"); _cineCheatsSet = false; }
        }

        /// <summary>Third-person shots: the camera sees V1's model. Traced: camera rays include its instance mask (bit 16 of
        /// _UltraRTPlayerRays, Plugin.cs). The game's own image ("off" takes): the main camera draws layer 30, which is
        /// otherwise only drawn in portal views.</summary>
        private void CineShowPlayer(bool show)
        {
            _cineShowPlayer = show;
            var cc = GameControl.Cam;
            var cam = cc != null ? cc.cam : null;
            if (cam == null) { _cineShowPlayerMask = false; return; }
            if (show && (cam.cullingMask & (1 << PlayerModelLayer)) == 0) { cam.cullingMask |= 1 << PlayerModelLayer; _cineShowPlayerMask = true; }
            else if (!show && _cineShowPlayerMask) { cam.cullingMask &= ~(1 << PlayerModelLayer); _cineShowPlayerMask = false; }
        }

        /// <summary>Shop terminals stay dark (bench "ShopScreensOff"): ShopZone switches its world-space canvas on when the
        /// player enters its trigger, and the UI then covers the CRT glass (the reflective part). The triggers are off
        /// while the camera is on, and the canvases are kept inactive; leaving the camera puts the triggers back, which
        /// turns a screen on again if the player stands in front of it.</summary>
        private void CineShopScreensOff(bool off)
        {
            if (!off)
            {
                foreach (var c in _cineShopTriggers) if (c != null) c.enabled = true;
                _cineShopTriggers.Clear();
                _cineShopCanvases.Clear();
                return;
            }
            if (_cineShopTriggers.Count > 0 || _cineShopCanvases.Count > 0) return;
            foreach (var z in FindObjectsOfType<ShopZone>())
            {
                foreach (var c in z.GetComponents<Collider>())
                    if (c.enabled && c.isTrigger) { c.enabled = false; _cineShopTriggers.Add(c); }
                var cv = z.GetComponentInChildren<Canvas>(true);
                if (cv != null) { _cineShopCanvases.Add(cv.gameObject); cv.gameObject.SetActive(false); }
            }
            Logger.LogInfo($"Cinematic camera: {_cineShopCanvases.Count} shop screen(s) kept off ({_cineShopTriggers.Count} trigger(s))");
        }

        /// <summary>A take that moves the player: the pose at path time <paramref name="t"/> of <paramref name="route"/>
        /// (camera positions: the player's eye) goes to GameControl, which applies it after the LateUpdates.</summary>
        private void CineDrivePlayer(CineSpline route, float t)
        {
            float d = route.Duration, tt = Mathf.Clamp(t, 0f, d);
            var s = route.Sample(tt);
            var e = s.Rotation.eulerAngles;
            GameControl.SetPose(new CamPose(s.Position - _cineEye, e.y, -Mathf.DeltaAngle(0f, e.x)));
            const float h = 1f / 120f;
            float a = Mathf.Max(0f, tt - h), b = Mathf.Min(d, tt + h);
            _cinePlayerVel = b - a > 1e-4f ? (route.Sample(b).Position - route.Sample(a).Position) / (b - a) : Vector3.zero;
            _cinePlayerDriven = true;
            // A new take (the path time went back): the walk cycle starts over, so every take of a pair animates alike.
            if (t < _cinePlayerLastT - 0.25f) _cinePlayerRestart = true;
            _cinePlayerLastT = t;
        }

        /// <summary>V1's model walks at the route's speed. PlayerAnimations.LateUpdate sets these parameters from
        /// NewMovement (velocity / 15 in the player's frame, on the ground or not), which the bench has switched off (a
        /// kinematic body has no velocity); this runs after it (onBeforeRender), and the Animator reads them next frame.</summary>
        private void CineAnimatePlayer()
        {
            var nm = GameControl.Player;
            if (nm == null) return;
            if (_cinePlayerAnimator == null)
            {
                var pa = nm.GetComponentInChildren<PlayerAnimations>(true);
                _cinePlayerAnimator = pa != null ? pa.GetComponent<Animator>() : null;
                if (_cinePlayerAnimator == null) return;
            }
            var v = nm.transform.InverseTransformVector(_cinePlayerVel / 15f);
            var an = _cinePlayerAnimator;
            if (_cinePlayerRestart)
            {
                _cinePlayerRestart = false;
                an.Play(an.GetCurrentAnimatorStateInfo(0).fullPathHash, 0, 0f);
            }
            an.SetBool("Dodging", false);
            an.SetBool("InAir", false);
            an.SetBool("Sliding", false);
            an.SetFloat("VelX", v.x);
            an.SetFloat("VelY", v.z);
        }

        /// <summary>Game speed through TimeController (as the bench's fast-forward); s &lt; 0 restores what it was.</summary>
        private void CineSetSpeed(float s)
        {
            var tc = MonoSingleton<TimeController>.Instance;
            if (s < 0f)
            {
                if (!_cineSpeedSet) return;
                _cineSpeedSet = false;
                if (tc != null) PoseLoader.SetGameSpeed(_cineSavedSpeed);
                return;
            }
            if (!_cineSpeedSet) { _cineSavedSpeed = tc != null ? tc.timeScaleModifier : 1f; _cineSpeedSet = true; }
            if (tc != null && (Mathf.Abs(tc.timeScaleModifier - s) > 1e-4f || Mathf.Abs(Time.timeScale - tc.timeScale * s) > 1e-4f)) PoseLoader.SetGameSpeed(s);
        }

        /// <summary>Temporal histories after the camera jumped (a key selected, playback start, a Cut).</summary>
        private void CineCut()
        {
            _denoiser?.Reset();
            _reflFilter?.Reset();
            _lightSampling?.Reset();
            _rr?.Reset();
            _upsampler?.Reset();
            _volumetrics?.Reset();
        }

        private void CineSetEuler(Quaternion q)
        {
            var e = q.eulerAngles;
            _cineYaw = e.y;
            _cinePitch = -Mathf.DeltaAngle(0f, e.x);
            _cineRoll = Mathf.DeltaAngle(0f, e.z);
        }

        private Quaternion CineEulerRotation() => Quaternion.Euler(-_cinePitch, _cineYaw, _cineRoll);

        private float CineDefaultFov()
        {
            try { var f = MonoSingleton<PrefsManager>.Instance.GetFloat("fieldOfView"); return f > 1f ? f : 105f; }
            catch { return _cineSavedFov > 1f ? _cineSavedFov : 105f; }
        }

        private CineSpline CineSplineNow()
        {
            if (_cineSpline == null || _cineSplineDirty)
            {
                _cineSpline = new CineSpline(_cineKeys, CineDefaultFov());
                _cineSplineDirty = false;
            }
            return _cineSpline;
        }

        /// <summary>Application.onBeforeRender: after every LateUpdate (and after GameControl's pose), right before culling.</summary>
        private void CineBeforeRender()
        {
            if (_cineState == CineState.Off) return;
            var cc = GameControl.Cam;
            if (cc == null || cc.cam == null) return;
            var cam = cc.cam;
            var s = _cineCur;
            cam.transform.SetPositionAndRotation(s.Position, s.Rotation);
            cam.fieldOfView = Mathf.Clamp(s.Fov > 1f ? s.Fov : CineDefaultFov(), 5f, 170f);
            if (_cineJitter != Vector2.zero)
            {
                cam.ResetProjectionMatrix();
                var p = cam.projectionMatrix;
                // Sub-pixel offset of the whole view: the game's raster and our trace (which reads this matrix) move together.
                p.m02 += 2f * _cineJitter.x / Mathf.Max(1, cam.pixelWidth);
                p.m12 += 2f * _cineJitter.y / Mathf.Max(1, cam.pixelHeight);
                cam.projectionMatrix = p;
                _cineProjSet = true;
            }
            else if (_cineProjSet) { cam.ResetProjectionMatrix(); _cineProjSet = false; }
            if (_cinePlayerDriven) CineAnimatePlayer();
            foreach (var g in _cineShopCanvases) if (g != null && g.activeSelf) g.SetActive(false);
            if (_cineState == CineState.Play)
            {
                // Live playback: a jump (Cut segment, a long hop) starts the temporal filters over instead of smearing.
                if (Vector3.Distance(_cinePrevPos, s.Position) > 3f || Quaternion.Angle(_cinePrevRot, s.Rotation) > 25f) CineCut();
            }
            _cinePrevPos = s.Position;
            _cinePrevRot = s.Rotation;
        }

        // ---------------------------------------------------------------- per frame (OnUpdate)

        private void CineUpdate()
        {
            if (BenchSafety.Active && !_cineBench) return; // the bench drives it (RunCinematicScenario)
            try
            {
                if (_cineState == CineState.Off)
                {
                    if (!BenchSafety.Active && KeyPressed(KeyCode.Home) && !CineGamePaused()) CineEnter();
                    return;
                }
                if (!CineInLevel()) { CineExit(); return; }
                // V1's head, chest and gun arm follow CameraController's transform (PlayerAnimations.LateUpdate), which
                // still has the free camera's rotation from the last frame: give it the player's own view back before the
                // LateUpdates run, so V1 looks where the player looks, not where the camera does.
                if (SceneProxies.TracePlayerModel && GameControl.Cam != null) GameControl.Cam.ApplyRotations();
                if (CineGamePaused())
                {
                    if (_cineState == CineState.Render) { _cineCancel = true; if (!_cineBench) Toast("Render cancelled (paused)"); }
                    // The pause menu (and PluginConfigurator's page) live on the canvases we hid.
                    if (_cineCanvasesHidden) { foreach (var c in _cineCanvases) if (c != null) c.enabled = true; _cineCanvasesHidden = false; }
                    return;
                }
                if (!_cineCanvasesHidden && _cineState != CineState.Off)
                {
                    foreach (var c in _cineCanvases) if (c != null) c.enabled = false;
                    _cineCanvasesHidden = true;
                }
                GameControl.HideCheatPanels();
                if (_cineState == CineState.Render)
                {
                    if (!_cineBench && KeyPressed(KeyCode.End)) { _cineCancel = true; Toast("Render: cancelled"); }
                    return;
                }
                if (_cineBench) return;
                if (KeyPressed(KeyCode.Home)) { CineExit(); return; }
                if (_cineState == CineState.Play) { CinePlayUpdate(); return; }
                CineFreeUpdate();
            }
            catch (InvalidOperationException) { /* legacy input disabled */ }
        }

        private static bool CineGamePaused()
        {
            try { var om = MonoSingleton<OptionsManager>.Instance; return om != null && om.paused; }
            catch { return false; }
        }

        private void CineFreeUpdate()
        {
            CineSetSpeed(_cine.timeScale);
            float dt = Mathf.Min(Time.unscaledDeltaTime, 0.1f);
            bool shift = ShiftHeld(), ctrl = CtrlHeld(), alt = KeyHeld(KeyCode.LeftAlt) || KeyHeld(KeyCode.RightAlt);

            // Look: raw mouse motion (the game's camera input is locked while we fly).
            var mouse = UnityEngine.InputSystem.Mouse.current;
            if (mouse != null)
            {
                var d = mouse.delta.ReadValue();
                float k = 0.06f * _cine.lookSpeed * Mathf.Clamp(_cineCur.Fov / 105f, 0.15f, 1.5f);
                _cineYaw += d.x * k;
                _cinePitch = Mathf.Clamp(_cinePitch + d.y * k, -89.5f, 89.5f);
                float wheel = mouse.scroll.ReadValue().y;
                if (Mathf.Abs(wheel) > 0.01f) _cine.moveSpeed = Mathf.Clamp(_cine.moveSpeed * Mathf.Pow(1.15f, Mathf.Sign(wheel)), 0.25f, 60f);
            }
            if (KeyHeld(KeyCode.Z)) _cineRoll -= 30f * dt;
            if (KeyHeld(KeyCode.C)) _cineRoll += 30f * dt;
            if (KeyPressed(KeyCode.X)) _cineRoll = 0f;
            float fov = _cineCur.Fov > 1f ? _cineCur.Fov : CineDefaultFov();
            if (KeyHeld(KeyCode.R)) fov = Mathf.Max(5f, fov - 25f * dt * (shift ? 3f : 1f));
            if (KeyHeld(KeyCode.F)) fov = Mathf.Min(150f, fov + 25f * dt * (shift ? 3f : 1f));
            if (KeyPressed(KeyCode.V)) fov = CineDefaultFov();
            var rot = CineEulerRotation();
            var move = Vector3.zero;
            if (!ctrl)
            {
                if (KeyHeld(KeyCode.W)) move += rot * Vector3.forward;
                if (KeyHeld(KeyCode.S)) move -= rot * Vector3.forward;
                if (KeyHeld(KeyCode.D)) move += rot * Vector3.right;
                if (KeyHeld(KeyCode.A)) move -= rot * Vector3.right;
                if (KeyHeld(KeyCode.E)) move += Vector3.up;
                if (KeyHeld(KeyCode.Q)) move -= Vector3.up;
            }
            float speed = _cine.moveSpeed * (shift ? 4f : 1f) * (alt ? 0.25f : 1f);
            _cineCur = new CineSample { Position = _cineCur.Position + move * speed * dt, Rotation = rot, Fov = fov };

            // Keys.
            if (KeyPressed(KeyCode.K)) CineAddKey();
            if (KeyPressed(KeyCode.U)) CineUpdateKey();
            if (KeyPressed(KeyCode.Delete) || KeyPressed(KeyCode.Backspace)) CineDeleteKey();
            if (KeyPressed(KeyCode.LeftArrow)) CineSelect(_cineSel - 1);
            if (KeyPressed(KeyCode.RightArrow)) CineSelect(_cineSel + 1);
            if (KeyPressed(KeyCode.UpArrow)) CineNudgeDuration(shift ? 1f : 0.25f);
            if (KeyPressed(KeyCode.DownArrow)) CineNudgeDuration(shift ? -1f : -0.25f);
            if (KeyPressed(KeyCode.T)) CineCycleEase();
            if (KeyPressed(KeyCode.G)) CineEvenSpeed();
            if (KeyPressed(KeyCode.P)) CinePlay(shift || _cineSel < 0 ? 0f : CineSplineNow().KeyTime(_cineSel));
            if (KeyPressed(KeyCode.S) && ctrl) CineSave();
            if (KeyPressed(KeyCode.L)) CineLoadNext();
            if (KeyPressed(KeyCode.N)) CineNew();
            if (KeyPressed(KeyCode.H)) { if (shift) _cineShowHelp = !_cineShowHelp; else _cineHideOverlay = !_cineHideOverlay; }
            if (KeyPressed(KeyCode.End)) CineStartRender();
        }

        private void CineAddKey()
        {
            float dur = 2f;
            if (_cineSel >= 0 && _cineSel < _cineKeys.Count)
            {
                // A sensible default: about the speed of the segment before.
                var prev = _cineKeys[_cineSel];
                float dist = Vector3.Distance(prev.Pos, _cineCur.Position);
                dur = Mathf.Clamp(dist / Mathf.Max(1f, _cine.moveSpeed * 0.5f), 1f, 8f);
            }
            int at = _cineSel >= 0 && _cineSel < _cineKeys.Count ? _cineSel + 1 : _cineKeys.Count;
            // Appended: the segment into the new key is the old last key's (unused until now). Inserted: the segment it
            // splits keeps its duration, the new key's own segment gets the default.
            if (at > 0 && at == _cineKeys.Count) { _cineKeys[at - 1].Duration = dur; _cineKeys[at - 1].Store(); }
            var k = CineKey.From(_cineCur.Position, _cineCur.Rotation, _cineCur.Fov, at == _cineKeys.Count ? 2f : dur, CineEase.Linear);
            _cineKeys.Insert(at, k);
            _cineSel = at;
            _cineSplineDirty = true;
            Toast($"Key {at + 1} of {_cineKeys.Count} added");
        }

        private void CineUpdateKey()
        {
            if (_cineSel < 0 || _cineSel >= _cineKeys.Count) { Toast("No key selected (Left / Right selects, K adds)"); return; }
            var k = _cineKeys[_cineSel];
            k.Pos = _cineCur.Position; k.Rot = _cineCur.Rotation; k.Fov = _cineCur.Fov;
            k.Store();
            _cineSplineDirty = true;
            Toast($"Key {_cineSel + 1} updated");
        }

        private void CineDeleteKey()
        {
            if (_cineSel < 0 || _cineSel >= _cineKeys.Count) return;
            _cineKeys.RemoveAt(_cineSel);
            _cineSel = Mathf.Min(_cineSel, _cineKeys.Count - 1);
            _cineSplineDirty = true;
            Toast(_cineKeys.Count == 0 ? "Path empty" : $"Key deleted ({_cineKeys.Count} left)");
        }

        private void CineSelect(int i)
        {
            if (_cineKeys.Count == 0) { Toast("No keys yet (K adds one)"); return; }
            _cineSel = Mathf.Clamp(i, 0, _cineKeys.Count - 1);
            var s = CineSplineNow().KeySample(_cineSel);
            _cineCur = s;
            CineSetEuler(s.Rotation);
            CineCut();
        }

        private void CineNudgeDuration(float d)
        {
            if (_cineSel < 0 || _cineSel >= _cineKeys.Count - 1) { Toast("Select a key that has a segment after it"); return; }
            var k = _cineKeys[_cineSel];
            k.Duration = Mathf.Clamp(k.Duration + d, 0.25f, 120f);
            _cineSplineDirty = true;
        }

        private void CineCycleEase()
        {
            if (_cineSel < 0 || _cineSel >= _cineKeys.Count - 1) { Toast("Select a key that has a segment after it"); return; }
            var k = _cineKeys[_cineSel];
            k.EaseMode = (CineEase)(((int)k.EaseMode + 1) % 5);
            k.Store();
            _cineSplineDirty = true;
        }

        /// <summary>G: same speed everywhere: every segment's duration in proportion to its length, the total kept.</summary>
        private void CineEvenSpeed()
        {
            if (_cineKeys.Count < 3) return;
            var sp = CineSplineNow();
            float total = 0f, len = 0f;
            for (int i = 0; i < _cineKeys.Count - 1; i++) { total += _cineKeys[i].Duration; len += sp.SegmentLength(i); }
            if (len < 1e-3f) return;
            for (int i = 0; i < _cineKeys.Count - 1; i++)
                if (_cineKeys[i].EaseMode != CineEase.Cut) _cineKeys[i].Duration = Mathf.Max(0.25f, total * sp.SegmentLength(i) / len);
            _cineSplineDirty = true;
            Toast($"Segments re-timed to an even speed ({len / total:F1} m/s)");
        }

        private void CineNew()
        {
            if (Time.realtimeSinceStartup > _cineNewArmed) { _cineNewArmed = Time.realtimeSinceStartup + 2f; Toast("Press N again to start a new, empty path"); return; }
            _cineNewArmed = 0f;
            _cineKeys.Clear();
            _cineSel = -1;
            _cineName = null;
            _cineMeta = null;
            _cineSplineDirty = true;
            Toast("New path");
        }

        // ---------------------------------------------------------------- playback

        private void CinePlay(float from)
        {
            if (_cineState == CineState.Off) CineEnter();
            if (_cineState == CineState.Off || _cineState == CineState.Render) return;
            if (_cineKeys.Count < 2) { Toast("A path needs two keys (K adds one)"); return; }
            _cinePlayT = Mathf.Clamp(from, 0f, CineSplineNow().Duration);
            _cineCur = CineSplineNow().Sample(_cinePlayT);
            _cineState = CineState.Play;
            CineCut();
        }

        private void CinePlayUpdate()
        {
            CineSetSpeed(_cine.timeScale);
            var sp = CineSplineNow();
            if (KeyPressed(KeyCode.P)) { CineStopPlay(); return; }
            if (KeyPressed(KeyCode.H)) _cineHideOverlay = !_cineHideOverlay;
            _cinePlayT += Time.unscaledDeltaTime;
            if (_cinePlayT >= sp.Duration) { _cinePlayT = sp.Duration; _cineCur = sp.Sample(_cinePlayT); CineStopPlay(); return; }
            _cineCur = sp.Sample(_cinePlayT);
        }

        private void CineStopPlay()
        {
            _cineState = CineState.Free;
            CineSetEuler(_cineCur.Rotation);
            _cineSel = Mathf.Clamp(CineSplineNow().Segment(_cinePlayT), 0, _cineKeys.Count - 1);
        }

        // ---------------------------------------------------------------- save / load

        private void CineSave()
        {
            if (_cineKeys.Count == 0) { Toast("Nothing to save (K adds a key)"); return; }
            string scene = SafeCurrentScene();
            if (string.IsNullOrEmpty(_cineName)) _cineName = $"{CineSanitize(scene)}_{DateTime.Now:yyyyMMdd-HHmmss}";
            var file = new CinePathFile
            {
                Name = _cineName, Scene = scene, Created = DateTime.Now.ToString("s"), Keys = _cineKeys,
                EyeOffset = new[] { _cineEye.x, _cineEye.y, _cineEye.z },
            };
            // The level state, as F11 saves it with a pose: the route walked so far and the area diff (the player is frozen
            // where it stood, so the route ends there).
            file.Approach = _recorder?.SaveApproach() ?? _cineMeta?.Approach;
            file.Areas = _recorder?.SaveAreas() ?? _cineMeta?.Areas;
            file.Checkpoint = _recorder?.LastCheckpoint?.Path ?? _cineMeta?.Checkpoint;
            string path = Path.Combine(CineDir(scene), _cineName + ".json");
            try
            {
                file.Save(path);
                _cineMeta = file;
                Logger.LogInfo($"Cinematic path saved: {path} ({_cineKeys.Count} keys, {CineSplineNow().Duration:F1}s, approach {file.Approach ?? "none"}, areas {file.Areas ?? "none"})");
                Toast($"Saved {_cineName} ({_cineKeys.Count} keys){(file.Approach != null ? " + level state" : "")}");
            }
            catch (Exception e) { Logger.LogError($"Cinematic path save failed: {e}"); Toast("Save failed (see the log)"); }
        }

        private string[] CineSavedPaths()
        {
            var dir = CineDir(SafeCurrentScene());
            return Directory.Exists(dir) ? Directory.GetFiles(dir, "*.json").OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToArray() : Array.Empty<string>();
        }

        private void CineLoadNext()
        {
            var files = CineSavedPaths();
            if (files.Length == 0) { Toast($"No saved paths for {SafeCurrentScene()} (Ctrl+S saves one)"); return; }
            _cineLoadIndex = (_cineLoadIndex + 1) % files.Length;
            CineLoad(files[_cineLoadIndex]);
        }

        private void CineLoadLatest()
        {
            var files = CineSavedPaths();
            if (files.Length == 0) return;
            var latest = files.OrderBy(File.GetLastWriteTimeUtc).Last();
            _cineLoadIndex = Array.IndexOf(files, latest);
            CineLoad(latest, quiet: true);
        }

        private bool CineLoad(string path, bool quiet = false)
        {
            try
            {
                var f = CinePathFile.Load(path);
                var eye = f.EyeOffset != null && f.EyeOffset.Length >= 3 && GameControl.Player == null ? new Vector3(f.EyeOffset[0], f.EyeOffset[1], f.EyeOffset[2]) : _cineEye;
                foreach (var k in f.Keys) k.Resolve(eye);
                _cineKeys.Clear();
                _cineKeys.AddRange(f.Keys);
                _cineMeta = f;
                _cineName = string.IsNullOrEmpty(f.Name) ? Path.GetFileNameWithoutExtension(path) : f.Name;
                _cineSel = _cineKeys.Count > 0 ? 0 : -1;
                _cineSplineDirty = true;
                if (!string.IsNullOrEmpty(f.Scene) && f.Scene != SafeCurrentScene())
                    Logger.LogWarning($"Cinematic path '{_cineName}' was saved in {f.Scene}, this is {SafeCurrentScene()}");
                Logger.LogInfo($"Cinematic path loaded: {path} ({_cineKeys.Count} keys, {CineSplineNow().Duration:F1}s)");
                if (!quiet) Toast($"Loaded {_cineName}: {_cineKeys.Count} keys, {CineSplineNow().Duration:F1}s");
                return true;
            }
            catch (Exception e)
            {
                Logger.LogError($"Cinematic path load failed ({path}): {e.Message}");
                if (!quiet) Toast("Load failed (see the log)");
                return false;
            }
        }

        // ---------------------------------------------------------------- overlay

        private GUIStyle _cineStyle, _cineSmall;

        private void DrawCine()
        {
            if (_cineState == CineState.Off || _cineHideGui || _camera == null) return;
            if (_cineStyle == null)
            {
                _cineStyle = new GUIStyle(GUI.skin.label) { fontSize = 18, richText = true, normal = { textColor = Color.white } };
                _cineSmall = new GUIStyle(GUI.skin.label) { fontSize = 14, richText = true, normal = { textColor = new Color(1f, 1f, 1f, 0.9f) } };
            }
            var prev = GUI.color;
            try
            {
                if (_cineState == CineState.Render) { CineDrawShadowed(new Rect(20, Screen.height - 70, 1600, 30), _cineProgress ?? "Rendering...", _cineStyle); return; }
                var sp = CineSplineNow();
                if (_cineState == CineState.Play)
                {
                    if (!_cineHideOverlay)
                        CineDrawShadowed(new Rect(20, Screen.height - 50, 900, 30), $"<b>PLAY</b> {_cinePlayT:F2} / {sp.Duration:F2} s   (P stop, H hide)", _cineSmall);
                    return;
                }
                if (_cineHideOverlay) return;
                CineDrawPath(sp);
                string sel = _cineSel >= 0 && _cineSel < _cineKeys.Count
                    ? $"key <b>{_cineSel + 1}</b>/{_cineKeys.Count} at {sp.KeyTime(_cineSel):F2}s" +
                      (_cineSel < _cineKeys.Count - 1 ? $", segment {_cineKeys[_cineSel].Duration:F2}s {_cineKeys[_cineSel].EaseMode.ToString().ToLowerInvariant()}" : " (last)")
                    : $"{_cineKeys.Count} keys";
                var lines = new List<string>
                {
                    $"<b>CINEMATIC CAMERA</b>  path <b>{_cineName ?? "(unsaved)"}</b>  {sp.Duration:F1}s  |  {sel}",
                    $"speed {_cine.moveSpeed:F1} m/s   FOV {_cineCur.Fov:F1}   roll {_cineRoll:F0}   game speed {_cine.timeScale:F2}   render {_cine.mode.ToString().ToLowerInvariant()} {Screen.width * _cine.outputScale:F0}x{Screen.height * _cine.outputScale:F0} {_cine.fps:F0} fps, {_cine.samples} spp x {_cine.aa} {(_cine.motionBlur ? "blur" : "AA")}",
                };
                if (_cineShowHelp)
                {
                    lines.Add("WASD Q/E mouse fly (Shift fast, Alt slow, wheel speed)   Z/C roll, X level   R/F zoom, V reset");
                    lines.Add("K add key  U update  Del delete  Left/Right select  Up/Down duration  T easing  G even speed");
                    lines.Add("P play (Shift: from start)  Ctrl+S save  L load  N N new  End render  H hide (Shift+H: help)  Home exit");
                }
                for (int i = 0; i < lines.Count; i++) CineDrawShadowed(new Rect(20, 100 + 24 * i, 1800, 26), lines[i], i == 0 ? _cineStyle : _cineSmall);
            }
            finally { GUI.color = prev; }
        }

        private static void CineDrawShadowed(Rect r, string text, GUIStyle style)
        {
            var c = style.normal.textColor;
            style.normal.textColor = new Color(0f, 0f, 0f, 0.8f);
            GUI.Label(new Rect(r.x + 1, r.y + 1, r.width, r.height), text, style);
            style.normal.textColor = c;
            GUI.Label(r, text, style);
        }

        private bool CineProject(Vector3 p, out Vector2 sp)
        {
            var v = _camera.WorldToScreenPoint(p);
            sp = new Vector2(v.x, Screen.height - v.y);
            return v.z > 0.1f && sp.x > -50 && sp.y > -50 && sp.x < Screen.width + 50 && sp.y < Screen.height + 50;
        }

        private void CineDrawPath(CineSpline sp)
        {
            if (_cineKeys.Count == 0) return;
            var tex = Texture2D.whiteTexture;
            if (_cineKeys.Count >= 2)
            {
                int n = Mathf.Clamp((int)(sp.Duration * 30f), 30, 600);
                for (int i = 0; i <= n; i++)
                {
                    var s = sp.Sample(sp.Duration * i / n);
                    if (!CineProject(s.Position, out var q)) continue;
                    GUI.color = new Color(1f, 0.85f, 0.2f, 0.85f);
                    GUI.DrawTexture(new Rect(q.x - 1.5f, q.y - 1.5f, 3f, 3f), tex);
                }
            }
            for (int i = 0; i < _cineKeys.Count; i++)
            {
                var k = _cineKeys[i];
                if (!CineProject(k.Pos, out var q)) continue;
                bool sel = i == _cineSel;
                GUI.color = sel ? new Color(0.3f, 1f, 0.3f, 1f) : new Color(1f, 1f, 1f, 0.9f);
                float r = sel ? 6f : 4f;
                GUI.DrawTexture(new Rect(q.x - r, q.y - r, 2 * r, 2 * r), tex);
                // Viewing direction.
                if (CineProject(k.Pos + k.Rot * Vector3.forward * 1.0f, out var f))
                {
                    var d = f - q;
                    for (int j = 1; j <= 8; j++) GUI.DrawTexture(new Rect(q.x + d.x * j / 8f - 1f, q.y + d.y * j / 8f - 1f, 2f, 2f), tex);
                }
                GUI.Label(new Rect(q.x + 8, q.y - 10, 60, 20), (i + 1).ToString(), _cineSmall);
            }
        }
    }
}
