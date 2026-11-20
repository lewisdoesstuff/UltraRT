using System;
using System.Linq;
using System.Runtime.CompilerServices;
using UnityEngine;
using UnityEngine.EventSystems;

namespace UltraRT
{
    /// <summary>
    /// Player-safe hotkeys (docs/settings.md, "Hotkeys for players and developers"; since 2026-11-20):
    ///  - no hotkey is read while ULTRAKILL's console (F8) or a text field has the keyboard;
    ///  - Page Up / Page Down cycle the three real view modes; the debug views join the cycle with [View] DebugModes;
    ///  - the developer and bench keys (F2, F3 and its variants, F6, F9, F10, F11) work with [View] DevHotkeys, which also
    ///    turns on the shader bundle's hot reload (a 0.5 s file poll).
    /// A bench launch (any -ultrart-bench* flag) keeps everything on, as before.
    /// </summary>
    public partial class Plugin
    {
        /// <summary>[View] DebugModes / -ultrart-debug-modes: Page Up / Page Down also cycle the debug views.</summary>
        private bool _debugModes;
        /// <summary>[View] DevHotkeys / -ultrart-dev-hotkeys: the developer and bench keys, and the bundle hot reload.</summary>
        private bool _devHotkeys;
        private bool DebugModesActive => _debugModes || BenchLaunch;
        private bool DevHotkeysActive => _devHotkeys || BenchLaunch;

        /// <summary>This frame's hotkeys may be read (UpdateHotkeyGuard, first in OnUpdate).</summary>
        private bool _keysLive = true;
        private bool _keyboardBusyPrev, _consoleCheckFailed, _devKeyHintShown;

        private void ParseHotkeyArgs()
        {
            var a = Environment.GetCommandLineArgs();
            if (a.Any(x => x.Equals("-ultrart-debug-modes", StringComparison.OrdinalIgnoreCase))) _debugModes = true;
            if (a.Any(x => x.Equals("-ultrart-dev-hotkeys", StringComparison.OrdinalIgnoreCase))) _devHotkeys = true;
        }

        /// <summary>The three view modes players use; everything else in Modes is a debug view.</summary>
        private static bool IsPlayerMode(int i) => Modes[i].name == "pathtrace" || Modes[i].name == "relight" || Modes[i].name == "pt-dn";

        /// <summary>Page Up (-1) / Page Down (+1): the next mode in Modes order, skipping the debug views unless they are on.
        /// From a debug view (a flag or the menu put us there) the cycle goes on to the next player mode.</summary>
        private void CycleMode(int dir)
        {
            int n = Modes.Length, i = _mode;
            for (int k = 0; k < n; k++)
            {
                i = ((i + dir) % n + n) % n;
                if (DebugModesActive || IsPlayerMode(i)) break;
            }
            _mode = i;
            Logger.LogInfo($"Mode: {Modes[_mode].name}");
            Toast($"UltraRT mode: {Modes[_mode].name}");
        }

        /// <summary>First in OnUpdate. The keyboard is busy while the console is open or a text field (uGUI, TextMeshPro or
        /// IMGUI) has focus. Keys are ignored only when it was busy in the previous frame too: the frame the console opens
        /// still reads them, because Ctrl+F8 (DLSS RR) shares F8 with the console, and whichever Update runs first decides
        /// whether the console already counts as open.</summary>
        private void UpdateHotkeyGuard()
        {
            bool busy = ConsoleOpen() || TextFieldFocused();
            _keysLive = !(busy && _keyboardBusyPrev);
            _keyboardBusyPrev = busy;
        }

        private bool ConsoleOpen()
        {
            if (_consoleCheckFailed) return false;
            try { return GameConsoleIsOpen(); }
            catch (Exception e)
            {
                // A game update that renames the console: hotkeys work as before, without the guard.
                _consoleCheckFailed = true;
                Logger.LogWarning($"Hotkeys: cannot read ULTRAKILL's console state ({e.GetType().Name}: {e.Message}); hotkeys are not paused while it is open");
                return false;
            }
        }

        // NoInlining: a missing type or member fails when this method is compiled, inside ConsoleOpen's try.
        // Console is [ConfigureSingleton(NoAutoInstance)]: IsOpen never creates one, and looks for it at most once while there is none.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool GameConsoleIsOpen() => GameConsole.Console.IsOpen;

        private static bool TextFieldFocused()
        {
            try
            {
                if (GUIUtility.keyboardControl != 0) return true; // IMGUI (other mods' windows)
                var es = EventSystem.current;
                var go = es != null ? es.currentSelectedGameObject : null;
                if (go == null) return false;
                var tmp = go.GetComponent<TMPro.TMP_InputField>();
                if (tmp != null && tmp.isFocused) return true;
                var input = go.GetComponent<UnityEngine.UI.InputField>();
                return input != null && input.isFocused;
            }
            catch (Exception) { return false; }
        }

        /// <summary>A developer / bench hotkey: true when <paramref name="pressed"/> and the developer keys are on. Pressed while
        /// they are off: once per session, a toast says where to turn them on.</summary>
        private bool DevKey(bool pressed, string key)
        {
            if (!pressed) return false;
            if (DevHotkeysActive) return true;
            if (!_devKeyHintShown)
            {
                _devKeyHintShown = true;
                Logger.LogInfo($"Hotkeys: {key} is a developer hotkey and they are off ([View] DevHotkeys, -ultrart-dev-hotkeys)");
                Toast($"{key} is a developer hotkey: turn them on in UltraRT's Advanced settings (View)");
            }
            return false;
        }
    }
}
