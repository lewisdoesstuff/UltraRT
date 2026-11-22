using System;
using System.IO;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Rendering;

namespace UltraRT
{
    /// <summary>
    /// Guards Unity's BLAS cache against a missing scratch buffer (native/UltraRT.Native/BlasPatch.cpp; docs/scene-proxies.md,
    /// "Native crash in the BLAS cache (6-1, 2026-11-22)"). Unity 2022.3.29f1 crashed on the render thread
    /// (BufferD3D12::AccessBuffer reading [null + 0x60]) when a cached BLAS's result buffer was in RayTracingHeap but its
    /// scratch was not: the cached lookup stored a null buffer instead of creating the scratch. The patch sends that case to
    /// Unity's own FindOrCreateResource. Applied once, at plugin start, before any acceleration structure exists; the
    /// native code finds the bytes by signature and changes nothing for another Unity build. -ultrart-noblaspatch skips it.
    /// </summary>
    internal static class UnityBlasPatch
    {
        public static bool Enabled = true;

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int IntFn();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr PtrFn();

        [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibraryW(string path);
        [DllImport("kernel32", CharSet = CharSet.Ansi, SetLastError = true, ExactSpelling = true)]
        private static extern IntPtr GetProcAddress(IntPtr module, string name);

        /// <summary>Applies the patch; returns a line for the log. Never throws.</summary>
        public static string Apply(string nativeDllPath)
        {
            if (!Enabled) return "off (-ultrart-noblaspatch)";
            try
            {
                if (SystemInfo.graphicsDeviceType != GraphicsDeviceType.Direct3D12) return $"not needed ({SystemInfo.graphicsDeviceType})";
                if (!File.Exists(nativeDllPath)) return $"not applied: {nativeDllPath} not found";
                IntPtr module = LoadLibraryW(Path.GetFullPath(nativeDllPath));
                if (module == IntPtr.Zero) return $"not applied: LoadLibrary failed ({Marshal.GetLastWin32Error()})";
                IntPtr apply = GetProcAddress(module, "UltraRT_PatchBlasScratch");
                IntPtr status = GetProcAddress(module, "UltraRT_PatchBlasScratchStatus");
                if (apply == IntPtr.Zero || status == IntPtr.Zero) return "not applied: UltraRT.Native.dll has no patch export (older build)";
                int ok = Marshal.GetDelegateForFunctionPointer<IntFn>(apply)();
                string text = Marshal.PtrToStringAnsi(Marshal.GetDelegateForFunctionPointer<PtrFn>(status)()) ?? "";
                return (ok != 0 ? "" : "NOT APPLIED: ") + text;
            }
            catch (Exception e)
            {
                return $"not applied: {e.GetType().Name}: {e.Message}";
            }
        }
    }
}
