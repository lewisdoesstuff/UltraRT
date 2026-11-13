using System;
using System.IO;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Rendering;

namespace UltraRT
{
    /// <summary>
    /// GPU timings via D3D12 timestamp queries written into Unity's own command list (UltraRT.Native.dll).
    /// Unity's Recorder reports 0 GPU time in this player, so this is the only per-pass GPU clock we have.
    ///
    /// Usage: TryCreate once on the main thread (Awake); per frame, bracket work recorded into a CommandBuffer
    /// with Begin(cmd, slot)/End(cmd, slot) and finish with EndFrame(cmd); read TryGetMs(slot) any time.
    /// Results lag 2-4 frames (they are read once Unity's frame fence says the GPU finished that frame).
    /// See docs/gpu-timer.md.
    /// </summary>
    internal sealed class GpuTimer : IDisposable
    {
        public const string DllName = "UltraRT.Native.dll";

        public int MaxSlots { get; }

        private readonly IntPtr _eventFunc;
        private readonly int _eventBase;
        private readonly GetTimingsFn _getTimings;
        private readonly IntFn _isReady;
        private readonly StatusFn _getStatus;
        private readonly LongFn _resolvedFrames, _droppedFrames;
        private readonly float[] _ms;
        private int _cachedFrame = -1;
        private int _cachedCount;
        private bool _disposed;

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int IntFn();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate long LongFn();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr StatusFn();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr PtrFn();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int GetTimingsFn([Out] float[] outMs, int maxSlots);

        [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibraryW(string path);

        [DllImport("kernel32", CharSet = CharSet.Ansi, SetLastError = true, ExactSpelling = true)]
        private static extern IntPtr GetProcAddress(IntPtr module, string name);

        private GpuTimer(IntPtr module)
        {
            _eventFunc = Get<PtrFn>(module, "GetRenderEventAndDataFunc")();
            _eventBase = Get<IntFn>(module, "UltraRT_GetEventBase")();
            MaxSlots = Get<IntFn>(module, "UltraRT_GetMaxSlots")();
            _getTimings = Get<GetTimingsFn>(module, "UltraRT_GetTimings");
            _isReady = Get<IntFn>(module, "UltraRT_IsReady");
            _getStatus = Get<StatusFn>(module, "UltraRT_GetStatus");
            _resolvedFrames = Get<LongFn>(module, "UltraRT_GetResolvedFrameCount");
            _droppedFrames = Get<LongFn>(module, "UltraRT_GetDroppedFrameCount");
            _ms = new float[MaxSlots];
            if (_eventFunc == IntPtr.Zero) throw new InvalidOperationException("GetRenderEventAndDataFunc returned null");
        }

        /// <summary>
        /// Loads the native DLL from an explicit path and hands it to Unity (main thread only).
        /// Never throws; on failure returns false with a reason (wrong graphics API, DLL missing, Unity internals
        /// not recognised, ...). Note the native DLL stays loaded for the life of the process either way.
        /// </summary>
        public static bool TryCreate(string nativeDllPath, out GpuTimer timer, out string error)
        {
            timer = null;
            error = null;
            try
            {
                if (SystemInfo.graphicsDeviceType != GraphicsDeviceType.Direct3D12)
                {
                    error = $"graphics API is {SystemInfo.graphicsDeviceType}, GPU timer needs Direct3D12";
                    return false;
                }
                if (!File.Exists(nativeDllPath))
                {
                    error = $"{nativeDllPath} not found";
                    return false;
                }

                IntPtr module = LoadLibraryW(Path.GetFullPath(nativeDllPath));
                if (module == IntPtr.Zero)
                {
                    error = $"LoadLibrary({nativeDllPath}) failed: Win32 error {Marshal.GetLastWin32Error()}";
                    return false;
                }

                var init = Get<IntFn>(module, "UltraRT_Init");
                var t = new GpuTimer(module);
                if (init() == 0)
                {
                    error = "native init failed: " + t.Status;
                    return false;
                }
                timer = t;
                return true;
            }
            catch (Exception e)
            {
                error = $"{e.GetType().Name}: {e.Message}";
                return false;
            }
        }

        private static T Get<T>(IntPtr module, string name) where T : Delegate
        {
            IntPtr p = GetProcAddress(module, name);
            if (p == IntPtr.Zero) throw new EntryPointNotFoundException($"{DllName} has no export '{name}'");
            return Marshal.GetDelegateForFunctionPointer<T>(p);
        }

        /// <summary>True once the render thread has created the query heap; Begin/End before that are no-ops.</summary>
        public bool IsReady => !_disposed && _isReady() != 0;

        /// <summary>Human-readable native state (also explains failures).</summary>
        public string Status => Marshal.PtrToStringAnsi(_getStatus()) ?? "";

        public long ResolvedFrames => _resolvedFrames();
        public long DroppedFrames => _droppedFrames();

        public void Begin(CommandBuffer cmd, int slot)
        {
            if (_disposed || (uint)slot >= (uint)MaxSlots) return;
            cmd.IssuePluginEventAndData(_eventFunc, _eventBase + 0, (IntPtr)slot);
        }

        public void End(CommandBuffer cmd, int slot)
        {
            if (_disposed || (uint)slot >= (uint)MaxSlots) return;
            cmd.IssuePluginEventAndData(_eventFunc, _eventBase + 1, (IntPtr)slot);
        }

        /// <summary>Resolves this frame's queries to the readback buffer. Issue once per frame, after the last End.</summary>
        public void EndFrame(CommandBuffer cmd)
        {
            if (_disposed) return;
            cmd.IssuePluginEventAndData(_eventFunc, _eventBase + 2, IntPtr.Zero);
        }

        /// <summary>Latest completed GPU duration of a slot in milliseconds (false if not timed in that frame).</summary>
        public bool TryGetMs(int slot, out float ms)
        {
            ms = 0f;
            if (_disposed || (uint)slot >= (uint)MaxSlots) return false;
            if (_cachedFrame != Time.frameCount)
            {
                _cachedFrame = Time.frameCount;
                _cachedCount = _getTimings(_ms, _ms.Length);
            }
            if (slot >= _cachedCount || _ms[slot] < 0f) return false;
            ms = _ms[slot];
            return true;
        }

        /// <summary>
        /// Stops issuing events. The native library is intentionally not unloaded: Unity keeps it in its plugin
        /// list and command buffers already queued on the render thread may still call into it.
        /// </summary>
        public void Dispose() => _disposed = true;
    }
}
