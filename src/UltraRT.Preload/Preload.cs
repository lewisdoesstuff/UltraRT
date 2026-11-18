using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using BepInEx;
using BepInEx.Logging;
using Mono.Cecil;

namespace UltraRT.Preload
{
    /// <summary>
    /// BepInEx preloader patcher for UltraRT's HDR display output (docs/hdr.md). The preloader runs when Mono starts,
    /// before Unity creates its graphics device and swapchain, so this is where UltraRT.Native.dll's DXGI factory hooks
    /// go in. It patches no assembly, and does nothing at all unless HDR is asked for:
    ///   -ultrart-hdr [on|off|force]                    launch flag (wins over the config)
    ///   [Display] HDR = true                           BepInEx\config\ooo.lew.ultrart.cfg (read only here)
    /// "force" also uses the HDR10 swapchain when Windows reports HDR off for the display (testing).
    /// </summary>
    public static class HdrPreload
    {
        public static IEnumerable<string> TargetDLLs { get; } = new string[0];

        public static void Patch(AssemblyDefinition assembly) { }

        [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibraryW(string path);
        [DllImport("kernel32", CharSet = CharSet.Ansi, SetLastError = true, ExactSpelling = true)]
        private static extern IntPtr GetProcAddress(IntPtr module, string name);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
        private delegate int InstallFn(string logPath, int force);

        public static void Initialize()
        {
            ManualLogSource log = null;
            try
            {
                bool force;
                if (!Wanted(out force, out string why)) return;
                log = Logger.CreateLogSource("UltraRT.Preload");
                string dll = Path.Combine(Paths.PluginPath, "UltraRT", "UltraRT.Native.dll");
                if (!File.Exists(dll)) { log.LogWarning($"HDR output asked for ({why}), but {dll} is missing"); return; }
                IntPtr module = LoadLibraryW(dll);
                if (module == IntPtr.Zero) { log.LogWarning($"HDR output: LoadLibrary({dll}) failed, Win32 error {Marshal.GetLastWin32Error()}"); return; }
                IntPtr fn = GetProcAddress(module, "UltraRT_Hdr_Install");
                if (fn == IntPtr.Zero) { log.LogWarning("HDR output: UltraRT.Native.dll has no UltraRT_Hdr_Install (old build?)"); return; }
                string dataDir = Path.Combine(Paths.BepInExRootPath, "ultrart");
                Directory.CreateDirectory(dataDir);
                string logPath = Path.Combine(dataDir, "hdr.log");
                int ok = Marshal.GetDelegateForFunctionPointer<InstallFn>(fn)(logPath, force ? 1 : 0);
                log.LogInfo($"HDR output ({why}): swapchain hooks {(ok != 0 ? "installed" : "NOT installed")}; details in {logPath}");
            }
            catch (Exception e)
            {
                try { (log ?? Logger.CreateLogSource("UltraRT.Preload")).LogWarning($"HDR output: preload failed: {e.GetType().Name}: {e.Message}"); }
                catch { }
            }
        }

        private static bool Wanted(out bool force, out string why)
        {
            force = false;
            why = null;
            var a = Environment.GetCommandLineArgs();
            for (int i = 0; i < a.Length; i++)
            {
                if (!string.Equals(a[i], "-ultrart-hdr", StringComparison.OrdinalIgnoreCase)) continue;
                string v = i + 1 < a.Length ? a[i + 1].ToLowerInvariant() : "";
                why = "-ultrart-hdr";
                if (v == "off" || v == "0" || v == "false") return false;
                force = v == "force";
                return true;
            }
            string cfg = Path.Combine(Paths.ConfigPath, "ooo.lew.ultrart.cfg");
            if (!File.Exists(cfg)) return false;
            bool display = false;
            foreach (string raw in File.ReadAllLines(cfg))
            {
                string line = raw.Trim();
                if (line.StartsWith("[")) { display = string.Equals(line, "[Display]", StringComparison.OrdinalIgnoreCase); continue; }
                if (!display || line.StartsWith("#")) continue;
                int eq = line.IndexOf('=');
                if (eq <= 0 || !string.Equals(line.Substring(0, eq).Trim(), "HDR", StringComparison.OrdinalIgnoreCase)) continue;
                why = "[Display] HDR = true";
                return string.Equals(line.Substring(eq + 1).Trim(), "true", StringComparison.OrdinalIgnoreCase);
            }
            return false;
        }
    }
}
