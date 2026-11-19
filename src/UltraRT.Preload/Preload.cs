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
                string dll = FindNativeDll(log);
                if (dll == null) { log.LogWarning($"HDR output asked for ({why}), but no {NativeDll} was found under {Paths.PluginPath}"); return; }
                log.LogInfo($"HDR output: using {dll}");
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

        private const string NativeDll = "UltraRT.Native.dll";
        private const string PluginDll = "UltraRT.dll";

        /// <summary>
        /// UltraRT.Native.dll, wherever UltraRT is installed under BepInEx\plugins: plugins\UltraRT\ (a manual install, the
        /// repo's deploy) or plugins\&lt;Team&gt;-UltraRT\ (r2modman / Thunderstore Mod Manager; docs/release.md). The plugin
        /// loads the DLL from the folder of UltraRT.dll, so that folder wins: the copy the HDR hooks go into has to be the one
        /// the plugin uses later. With several installs (BepInEx loads only one of them) the newest UltraRT.dll wins, with a
        /// warning. A DLL without UltraRT.dll beside it is the last resort.
        /// </summary>
        private static string FindNativeDll(ManualLogSource log)
        {
            var withPlugin = new List<(string dir, DateTime stamp)>();
            var loose = new List<(string dir, DateTime stamp)>();
            foreach (string dir in Folders(Paths.PluginPath, 4))
            {
                string native = Path.Combine(dir, NativeDll);
                if (!File.Exists(native)) continue;
                string plugin = Path.Combine(dir, PluginDll);
                if (File.Exists(plugin)) withPlugin.Add((dir, File.GetLastWriteTimeUtc(plugin)));
                else loose.Add((dir, File.GetLastWriteTimeUtc(native)));
            }
            var pick = withPlugin.Count > 0 ? withPlugin : loose;
            if (pick.Count == 0) return null;
            pick.Sort((a, b) => b.stamp.CompareTo(a.stamp));
            if (pick.Count > 1)
            {
                var dirs = new List<string>();
                foreach (var p in pick) dirs.Add(p.dir);
                log.LogWarning($"{NativeDll} is in {pick.Count} folders ({string.Join(", ", dirs)}): using {pick[0].dir}. " +
                               @"Keep only one UltraRT install in BepInEx\plugins.");
            }
            return Path.Combine(pick[0].dir, NativeDll);
        }

        /// <summary>A folder and its subfolders down to maxDepth levels; folders that can't be read are skipped.</summary>
        private static IEnumerable<string> Folders(string root, int maxDepth)
        {
            if (!Directory.Exists(root)) yield break;
            var stack = new Stack<(string dir, int depth)>();
            stack.Push((root, 0));
            while (stack.Count > 0)
            {
                var (dir, depth) = stack.Pop();
                yield return dir;
                if (depth >= maxDepth) continue;
                string[] subs;
                try { subs = Directory.GetDirectories(dir); } catch { continue; }
                foreach (string d in subs) stack.Push((d, depth + 1));
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
