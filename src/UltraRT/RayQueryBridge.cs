using System;
using System.IO;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Rendering;

namespace UltraRT
{
    /// <summary>
    /// Managed wrapper for UltraRT.Native.dll's D3D12 cs_6_5 inline RayQuery bridge (RayQuery.cpp, Wave 3).
    /// Hooks Unity's scene TLAS build during <see cref="BeginRtasBuild"/>/<see cref="EndRtasBuild"/> and dispatches
    /// native RayQuery compute shaders for Volumetrics and ParticleLight directly on Unity's command list, eliminating
    /// Unity's per-DispatchRays shader-table rebuilds (~1.25 ms RT-CPU in 8-x).
    /// </summary>
    internal sealed class RayQueryBridge
    {
        [StructLayout(LayoutKind.Sequential)]
        internal struct UInt4
        {
            public uint x, y, z, w;
            public UInt4(uint x, uint y, uint z, uint w) { this.x = x; this.y = y; this.z = z; this.w = w; }
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct VolRqFrame
        {
            public IntPtr depthTex;
            public IntPtr lightsBuf;
            public IntPtr skipBuf;
            public IntPtr coneBuf;
            public IntPtr beamsBuf;
            public IntPtr emTrisBuf;
            public IntPtr emOwnersBuf;
            public IntPtr emAliasBuf;
            public IntPtr emActiveBuf;
            public IntPtr outTex;
            public IntPtr beamOutTex;
            public int width, height, pad0, pad1;
            public Matrix4x4 invViewProj;
            public Vector4 camPos;
            public Vector4 dim;
            public Vector4 medium;
            public Vector4 volParams;
            public Vector4 sunDir;
            public Vector4 sunColor;
            public Vector4 lightParams;
            public Vector4 falloff;
            public Vector4 lightShape;
            public Vector4 samples;
            public Vector4 emissiveParams;
            public Vector4 misc;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct PlRqFrame
        {
            public IntPtr lightsBuf;
            public IntPtr lightMasksBuf;
            public IntPtr lightAliasBuf;
            public IntPtr fillLightsBuf;
            public IntPtr emTrisBuf;
            public IntPtr emOwnersBuf;
            public IntPtr emAliasBuf;
            public IntPtr emActiveBuf;
            public IntPtr cacheKeysBuf;
            public IntPtr cacheResolvedBuf;
            public IntPtr skyTex;
            public IntPtr portalQuadsBuf;
            public IntPtr portalTravelsBuf;
            public IntPtr outTex;
            public int gridX, gridY, gridZ, pad0;
            public Matrix4x4 invViewProj;
            public Vector4 camPos;
            public Vector4 grid;
            public Vector4 depth;
            public Vector4 camFwd;
            public Vector4 plParams;
            public Vector4 ambient;
            public Vector4 relight;
            public Vector4 skyLight;
            public Vector4 skyColor;
            public Vector4 skyTint;
            public Vector4 skyParams;
            public Vector4 emissiveParams;
            public Vector4 cacheGrid;
            public Vector4 cacheQuery;
            public UInt4 int0;
            public UInt4 int1;
            public UInt4 int2;
            public Vector4 cv0, cv1, cv2, cv3, cv4, cv5, cv6, cv7;
            public Vector4 cv8, cv9, cv10, cv11, cv12, cv13, cv14, cv15;

            public void SetCacheView(int idx, Vector4 v)
            {
                switch (idx)
                {
                    case 0: cv0 = v; break; case 1: cv1 = v; break; case 2: cv2 = v; break; case 3: cv3 = v; break;
                    case 4: cv4 = v; break; case 5: cv5 = v; break; case 6: cv6 = v; break; case 7: cv7 = v; break;
                    case 8: cv8 = v; break; case 9: cv9 = v; break; case 10: cv10 = v; break; case 11: cv11 = v; break;
                    case 12: cv12 = v; break; case 13: cv13 = v; break; case 14: cv14 = v; break; case 15: cv15 = v; break;
                }
            }
        }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr PtrFn();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int IntFn();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate long LongFn();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void SetVolFn(int slot, ref VolRqFrame f);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void SetPlFn(int slot, ref PlRqFrame f);

        [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibraryW(string path);
        [DllImport("kernel32", CharSet = CharSet.Ansi, SetLastError = true, ExactSpelling = true)]
        private static extern IntPtr GetProcAddress(IntPtr module, string name);

        private readonly IntPtr _eventFunc;
        private readonly int _eventBase, _ring;
        private readonly IntFn _isReady, _hasTlas;
        private readonly LongFn _volCount, _plCount;
        private readonly PtrFn _getStatus;
        private readonly SetVolFn _setVol;
        private readonly SetPlFn _setPl;
        private int _volSlot, _plSlot;

        public bool Ready => _isReady() != 0;
        public bool HasTlas => _hasTlas() != 0;
        public long VolDispatches => _volCount();
        public long PlDispatches => _plCount();
        public string Status => Marshal.PtrToStringAnsi(_getStatus()) ?? "";

        private RayQueryBridge(IntPtr module)
        {
            _eventFunc = Get<PtrFn>(module, "GetRenderEventAndDataFunc")();
            _eventBase = Get<IntFn>(module, "UltraRT_RQ_GetEventBase")();
            _ring = Get<IntFn>(module, "UltraRT_RQ_GetRingSize")();
            int volSz = Get<IntFn>(module, "UltraRT_RQ_GetVolFrameSize")();
            int plSz = Get<IntFn>(module, "UltraRT_RQ_GetPlFrameSize")();
            int myVolSz = Marshal.SizeOf<VolRqFrame>();
            int myPlSz = Marshal.SizeOf<PlRqFrame>();
            if (volSz != myVolSz) throw new InvalidOperationException($"VolRqFrame size mismatch: native {volSz}, managed {myVolSz}");
            if (plSz != myPlSz) throw new InvalidOperationException($"PlRqFrame size mismatch: native {plSz}, managed {myPlSz}");
            _isReady = Get<IntFn>(module, "UltraRT_RQ_IsReady");
            _hasTlas = Get<IntFn>(module, "UltraRT_RQ_HasTlas");
            _volCount = Get<LongFn>(module, "UltraRT_RQ_GetVolDispatches");
            _plCount = Get<LongFn>(module, "UltraRT_RQ_GetPlDispatches");
            _getStatus = Get<PtrFn>(module, "UltraRT_RQ_GetStatus");
            _setVol = Get<SetVolFn>(module, "UltraRT_RQ_SetVolFrame");
            _setPl = Get<SetPlFn>(module, "UltraRT_RQ_SetPlFrame");
        }

        private static T Get<T>(IntPtr module, string name) where T : Delegate
        {
            IntPtr p = GetProcAddress(module, name);
            if (p == IntPtr.Zero) throw new EntryPointNotFoundException($"UltraRT.Native.dll has no export '{name}'");
            return Marshal.GetDelegateForFunctionPointer<T>(p);
        }

        public static bool TryCreate(string nativeDllPath, out RayQueryBridge bridge, out string error)
        {
            bridge = null;
            error = null;
            try
            {
                if (SystemInfo.graphicsDeviceType != GraphicsDeviceType.Direct3D12)
                {
                    error = $"graphics API is {SystemInfo.graphicsDeviceType}, RayQuery needs Direct3D12";
                    return false;
                }
                if (!File.Exists(nativeDllPath)) { error = $"{nativeDllPath} not found"; return false; }
                IntPtr module = LoadLibraryW(Path.GetFullPath(nativeDllPath));
                if (module == IntPtr.Zero) { error = $"LoadLibrary({nativeDllPath}) failed: {Marshal.GetLastWin32Error()}"; return false; }
                if (GetProcAddress(module, "UltraRT_RQ_GetEventBase") == IntPtr.Zero)
                {
                    error = "UltraRT.Native.dll has no RayQuery exports (older build)";
                    return false;
                }
                var init = Get<IntFn>(module, "UltraRT_Init");
                if (init() == 0) { error = "UltraRT_Init failed"; return false; }
                bridge = new RayQueryBridge(module);
                return true;
            }
            catch (Exception e)
            {
                error = $"{e.GetType().Name}: {e.Message}";
                return false;
            }
        }

        public void BeginRtasBuild(CommandBuffer cmd) => cmd.IssuePluginEventAndData(_eventFunc, _eventBase + 2, IntPtr.Zero);
        public void EndRtasBuild(CommandBuffer cmd) => cmd.IssuePluginEventAndData(_eventFunc, _eventBase + 3, IntPtr.Zero);

        public void DispatchVolumetrics(CommandBuffer cmd, ref VolRqFrame f)
        {
            int slot = _volSlot;
            _volSlot = (_volSlot + 1) % _ring;
            _setVol(slot, ref f);
            cmd.IssuePluginEventAndData(_eventFunc, _eventBase + 0, (IntPtr)slot);
        }

        public void DispatchParticleLight(CommandBuffer cmd, ref PlRqFrame f)
        {
            int slot = _plSlot;
            _plSlot = (_plSlot + 1) % _ring;
            _setPl(slot, ref f);
            cmd.IssuePluginEventAndData(_eventFunc, _eventBase + 1, (IntPtr)slot);
        }
    }
}
