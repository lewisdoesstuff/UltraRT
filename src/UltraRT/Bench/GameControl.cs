using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using ULTRAKILL.Cheats;
using UnityEngine;

namespace UltraRT.Bench
{
    /// <summary>
    /// Thin wrappers over ULTRAKILL internals for benchmarking: cheats (never persisted), taking the player over the
    /// way the Noclip cheat does, and posing the camera after every LateUpdate (Application.onBeforeRender).
    /// </summary>
    internal static class GameControl
    {
        private const string StateKey = "ultrart-bench";
        private static bool _controlled;
        private static CamPose _pose;
        private static Behaviour[] _disabled = Array.Empty<Behaviour>();

        private static readonly FieldInfo CheatsEnabledPanel = AccessTools.Field(typeof(CheatsController), "cheatsEnabledPanel");
        private static readonly FieldInfo CheatsInfoPanel = AccessTools.Field(typeof(CheatsController), "cheatsInfoPanel");

        public static NewMovement Player => MonoSingleton<NewMovement>.Instance;
        public static CameraController Cam => MonoSingleton<CameraController>.Instance;

        public static Transform PlayerTransform { get { var nm = Player; return nm != null ? nm.transform : null; } }

        public static CamPose CurrentPose()
        {
            var nm = Player;
            var cc = Cam;
            return new CamPose(nm.transform.position, cc.rotationY, cc.rotationX);
        }

        /// <summary>Enable cheats for this session only (saveState:false). Call at least one frame after the scene starts.</summary>
        public static void EnableCheats(bool blindEnemies, bool disableSpawns)
        {
            MonoSingleton<CheatsController>.Instance.ActivateCheats();
            var cm = MonoSingleton<CheatsManager>.Instance;
            cm.SetCheatActive(cm.GetCheatInstance<Invincibility>(), true, saveState: false);
            if (blindEnemies) cm.SetCheatActive(cm.GetCheatInstance<BlindEnemies>(), true, saveState: false);
            if (disableSpawns) cm.SetCheatActive(cm.GetCheatInstance<DisableEnemySpawns>(), true, saveState: false);
            HideCheatPanels();
        }

        private static readonly FieldInfo[] LevelTitleTexts = { AccessTools.Field(typeof(LevelNamePopup), "layerText"), AccessTools.Field(typeof(LevelNamePopup), "nameText") };

        /// <summary>The level title the game types out when a level starts (a teleport starts it) would sit in a tour's
        /// screenshots: its two texts stop drawing. The popup itself keeps running; a level load brings them back.</summary>
        public static void HideLevelTitle()
        {
            var popup = MonoSingleton<LevelNamePopup>.Instance;
            if (popup == null) return;
            foreach (var f in LevelTitleTexts)
                if (f?.GetValue(popup) is Behaviour b && b != null) b.enabled = false;
        }

        /// <summary>The "CHEATS ENABLED" panels would show up in screenshots.</summary>
        public static void HideCheatPanels()
        {
            var cc = MonoSingleton<CheatsController>.Instance;
            if (cc == null) return;
            (CheatsEnabledPanel?.GetValue(cc) as GameObject)?.SetActive(false);
            (CheatsInfoPanel?.GetValue(cc) as GameObject)?.SetActive(false);
        }

        public static void TakeControl(CamPose pose)
        {
            var nm = Player;
            if (!_controlled)
            {
                GameStateManager.Instance.RegisterState(new GameState(StateKey)
                {
                    playerInputLock = LockMode.Lock,
                    cameraInputLock = LockMode.Lock,
                    cursorLock = LockMode.Lock,
                });
                nm.StopSlide();
                nm.rb.velocity = Vector3.zero;
                nm.rb.isKinematic = true;
                _disabled = new Behaviour[] { nm, nm.GetComponent<KeepInBounds>(), nm.GetComponent<VerticalClippingBlocker>() }
                    .Where(b => b != null && b.enabled).ToArray();
                foreach (var b in _disabled) b.enabled = false;
                Application.onBeforeRender += ApplyPose;
                _controlled = true;
            }
            _pose = pose;
            ApplyPose();
        }

        public static void SetPose(CamPose pose) => _pose = pose;

        /// <summary>Weapon bob (Scenario.GunBob): amplitude scale (0 = off) and the time it follows (path time).</summary>
        public static float GunBob, BobTime;
        private static readonly System.Reflection.FieldInfo BobOriginal =
            typeof(WalkingBob).GetField("originalPos", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        private static WalkingBob[] _bobs = Array.Empty<WalkingBob>();
        private static float _nextBobScan;

        /// <summary>The bench takes the player over (NewMovement off), so WalkingBob never sees the player walk. This moves
        /// the gun as WalkingBob does at full walking speed (it goes centre, right, centre, left at about 0.5 m/s), as a
        /// function of the path time so replays are identical.</summary>
        private static void ApplyGunBob()
        {
            if (GunBob <= 0f || BobOriginal == null) return;
            if (Time.realtimeSinceStartup >= _nextBobScan || _bobs.Any(b => b == null))
            {
                _nextBobScan = Time.realtimeSinceStartup + 1f;
                _bobs = UnityEngine.Object.FindObjectsOfType<WalkingBob>();
            }
            float ph = Mathf.Sin(BobTime * (2f * Mathf.PI / 0.68f));
            foreach (var b in _bobs)
            {
                if (b == null || !b.isActiveAndEnabled) continue;
                var o = (Vector3)BobOriginal.GetValue(b);
                b.transform.localPosition = o + new Vector3(0.08f * ph, -0.025f * Mathf.Abs(ph), 0f) * GunBob;
            }
        }

        /// <summary>Move the player right now (from FixedUpdate), so every physics step sees the new position.</summary>
        public static void SetPoseNow(CamPose pose)
        {
            _pose = pose;
            var nm = Player;
            if (nm == null) return;
            nm.transform.position = pose.Position;
            nm.rb.position = pose.Position;
        }

        public static void ReleaseControl()
        {
            GunBob = 0f;
            if (!_controlled) return;
            Application.onBeforeRender -= ApplyPose;
            foreach (var b in _disabled) if (b != null) b.enabled = true;
            _disabled = Array.Empty<Behaviour>();
            var nm = Player;
            if (nm != null) nm.rb.isKinematic = false;
            GameStateManager.Instance?.PopState(StateKey);
            _controlled = false;
        }

        /// <summary>Runs after all LateUpdates, so CameraController's input/head-bob can't fight the path.</summary>
        private static void ApplyPose()
        {
            var nm = Player;
            var cc = Cam;
            if (nm == null || cc == null) return;
            nm.transform.position = _pose.Position;
            nm.rb.position = _pose.Position;
            cc.ResetCamera(_pose.Yaw, _pose.Pitch);
            cc.ApplyRotations();
            cc.transform.localPosition = cc.defaultPos;
            cc.StopShake();
            ApplyGunBob();
        }

        /// <summary>Hierarchy path, used to find the same ActivateArena again on replay.</summary>
        public static string PathOf(Transform t)
        {
            var s = t.name;
            for (var p = t.parent; p != null; p = p.parent) s = p.name + "/" + s;
            return s;
        }
    }
}
