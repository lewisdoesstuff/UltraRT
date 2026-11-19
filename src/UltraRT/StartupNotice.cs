using System;
using UnityEngine;

namespace UltraRT
{
    /// <summary>
    /// A message box drawn once per launch when UltraRT cannot run (docs/release.md, "Startup notice"): the game is not on
    /// Direct3D 12 (the launch options are missing), the GPU or driver has no DXR, or the shader bundle is missing. It
    /// waits until the intro is over, stays for <see cref="Seconds"/> (any mouse click on it closes it earlier) and draws
    /// with IMGUI, so it works in the main menu and in levels alike. Not shown in bench launches (their DX11 comparison
    /// runs are deliberate). The same text goes to the BepInEx log.
    /// </summary>
    internal sealed class StartupNotice : MonoBehaviour
    {
        public const float Seconds = 30f;
        /// <summary>The launch options UltraRT needs (README.md, "Requirements").</summary>
        public const string LaunchOptions = "-force-d3d12 -force-gfx-jobs legacy";

        private string _title, _body;
        private float _since = -1f;
        private bool _closed;
        private GUIStyle _titleStyle, _bodyStyle, _boxStyle;
        private Texture2D _bg;

        /// <summary>Why UltraRT can't run here, or null when it can: the text of the notice.</summary>
        public static (string title, string body) Reason(bool bundleMissing, string bundlePath)
        {
            var api = SystemInfo.graphicsDeviceType;
            if (api != UnityEngine.Rendering.GraphicsDeviceType.Direct3D12)
                return ($"UltraRT is off: the game is running on {ApiName(api)}, ray tracing needs DirectX 12",
                    "Add these launch options and restart the game:\n\n" +
                    $"      {LaunchOptions}\n\n" +
                    "r2modman / Thunderstore Mod Manager: Settings > Debugging > Set launch parameters.\n" +
                    "Steam: right-click ULTRAKILL > Properties > General > Launch Options.\n" +
                    "-force-gfx-jobs legacy avoids a Unity DirectX 12 freeze when large levels unload.");
            if (!SystemInfo.supportsRayTracing)
                return ("UltraRT is off: this GPU or driver has no DirectX 12 ray tracing (DXR)",
                    $"The game runs on DirectX 12, but {SystemInfo.graphicsDeviceName} reports no ray tracing support.\n" +
                    "UltraRT needs a DXR GPU (NVIDIA RTX 20 series or newer, AMD RX 6000 or newer, Intel Arc) and a current driver.\n" +
                    "The game runs as normal without it; you can disable the mod in your mod manager.");
            if (bundleMissing)
                return ("UltraRT is off: its shader file is missing or damaged",
                    $"Could not load {bundlePath}.\nReinstall UltraRT (in r2modman: uninstall and install it again).");
            return (null, null);
        }

        private static string ApiName(UnityEngine.Rendering.GraphicsDeviceType t)
        {
            switch (t)
            {
                case UnityEngine.Rendering.GraphicsDeviceType.Direct3D11: return "DirectX 11";
                case UnityEngine.Rendering.GraphicsDeviceType.Vulkan: return "Vulkan";
                case UnityEngine.Rendering.GraphicsDeviceType.OpenGLCore: return "OpenGL";
                default: return t.ToString();
            }
        }

        /// <summary>Adds the notice to a host object (one that survives scene loads).</summary>
        public static void Show(GameObject host, string title, string body)
        {
            if (host == null || string.IsNullOrEmpty(title)) return;
            var n = host.AddComponent<StartupNotice>();
            n._title = title;
            n._body = body;
        }

        private void Update()
        {
            if (_closed) { Destroy(this); return; }
            if (_since >= 0f) return;
            string scene;
            try { scene = SceneHelper.CurrentScene; } catch { return; }
            // The intro and the bootstrap scene come first; the notice belongs on the main menu (or the first level).
            if (string.IsNullOrEmpty(scene) || scene == "Intro" || scene == "Bootstrap") return;
            _since = Time.realtimeSinceStartup;
        }

        private void OnGUI()
        {
            if (_closed || _since < 0f) return;
            float t = Time.realtimeSinceStartup - _since;
            if (t > Seconds) { _closed = true; return; }
            if (_titleStyle == null)
            {
                _bg = new Texture2D(1, 1) { hideFlags = HideFlags.HideAndDontSave };
                _bg.SetPixel(0, 0, new Color(0f, 0f, 0f, 0.85f));
                _bg.Apply();
                _boxStyle = new GUIStyle { normal = { background = _bg }, padding = new RectOffset(24, 24, 18, 18) };
                _titleStyle = new GUIStyle(GUI.skin.label) { fontSize = 24, fontStyle = FontStyle.Bold, wordWrap = true, normal = { textColor = new Color(1f, 0.35f, 0.25f) } };
                _bodyStyle = new GUIStyle(GUI.skin.label) { fontSize = 18, wordWrap = true, normal = { textColor = Color.white } };
            }
            float scale = Mathf.Max(1f, Screen.height / 1080f);
            var saved = GUI.matrix;
            GUI.matrix = Matrix4x4.Scale(new Vector3(scale, scale, 1f));
            float w = Mathf.Min(980f, Screen.width / scale - 40f);
            float x = (Screen.width / scale - w) * 0.5f;
            string footer = $"UltraRT {PluginVersion.Number}. Click to close; closes by itself in {Mathf.CeilToInt(Seconds - t)} s. Details: BepInEx\\LogOutput.log.";
            float th = _titleStyle.CalcHeight(new GUIContent(_title), w - 48f);
            float bh = _bodyStyle.CalcHeight(new GUIContent(_body), w - 48f);
            float fh = _bodyStyle.CalcHeight(new GUIContent(footer), w - 48f);
            var box = new Rect(x, 60f, w, th + bh + fh + 36f + 24f);
            GUI.Box(box, GUIContent.none, _boxStyle);
            GUI.Label(new Rect(x + 24f, box.y + 18f, w - 48f, th), _title, _titleStyle);
            GUI.Label(new Rect(x + 24f, box.y + 18f + th + 12f, w - 48f, bh), _body, _bodyStyle);
            var c = GUI.color;
            GUI.color = new Color(1f, 1f, 1f, 0.6f);
            GUI.Label(new Rect(x + 24f, box.y + 18f + th + 12f + bh + 12f, w - 48f, fh), footer, _bodyStyle);
            GUI.color = c;
            var e = Event.current;
            if (e != null && e.type == EventType.MouseDown && box.Contains(e.mousePosition)) { _closed = true; e.Use(); }
            GUI.matrix = saved;
        }

        private void OnDestroy()
        {
            if (_bg != null) Destroy(_bg);
        }
    }
}
