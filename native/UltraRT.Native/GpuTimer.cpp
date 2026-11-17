// UltraRT.Native: D3D12 timestamp queries recorded into Unity's own command list.
//
// Flow (see docs/gpu-timer.md):
//   main thread    UltraRT_Init()  -> registers this DLL with Unity -> Unity calls UnityPluginLoad(interfaces)
//   render thread  plugin events Begin(slot) / End(slot) / Resolve, issued from a CommandBuffer via
//                  IssuePluginEventAndData(GetRenderEventAndDataFunc(), UltraRT_GetEventBase() + n, slot)
//   any thread     UltraRT_GetTimings() returns the newest frame whose GPU work has completed;
//                  UltraRT_GetCpuTimings() the render thread's own time between the same Begin and End events
//                  (what it took to execute the commands recorded between them: shader tables, state, copies).

#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <d3d12.h>
#include <dxgi.h> // IDXGISwapChain, referenced by IUnityGraphicsD3D12v7
#include <stdarg.h>
#include <stdio.h>
#include <stdlib.h>

#include "IUnityInterface.h"
#include "IUnityGraphics.h"
#include "IUnityGraphicsD3D12.h"
#include "UnityLocator.h"
#include "Native.h"

#define ULTRART_API extern "C" __declspec(dllexport)

namespace
{
    constexpr int kMaxSlots = 64;
    constexpr int kQueriesPerFrame = kMaxSlots * 2;   // begin + end per slot
    constexpr int kRing = 8;                          // frames in flight we can track (Unity keeps <= 3 queued)
    constexpr UINT kQueryCount = kRing * kQueriesPerFrame;

    // Small IDs on purpose: Unity's per-event D3D12 config table is keyed by event ID, so keep them away from 0
    // and from huge values. Must match GpuTimer.cs only through UltraRT_GetEventBase().
    constexpr int kEventBase = 91;
    constexpr int kEventBegin = kEventBase + 0;
    constexpr int kEventEnd = kEventBase + 1;
    constexpr int kEventResolve = kEventBase + 2;

    // --- D3D12 interface (v7 preferred, v6 has everything we need) ---
    struct D3D12Api
    {
        ID3D12Device* (UNITY_INTERFACE_API* GetDevice)();
        ID3D12Fence* (UNITY_INTERFACE_API* GetFrameFence)();
        UINT64 (UNITY_INTERFACE_API* GetNextFrameFenceValue)();
        ID3D12CommandQueue* (UNITY_INTERFACE_API* GetCommandQueue)();
        void (UNITY_INTERFACE_API* ConfigureEvent)(int, const UnityD3D12PluginEventConfig*);
        bool (UNITY_INTERFACE_API* CommandRecordingState)(UnityGraphicsD3D12RecordingState*);
        int version;
    };

    IUnityInterfaces* s_Unity = nullptr;
    IUnityGraphics* s_Graphics = nullptr;
    D3D12Api s_D3D = {};
    bool s_HaveD3D = false;

    // --- status / results, shared between threads ---
    SRWLOCK s_Lock = SRWLOCK_INIT;
    char s_Status[512] = "not initialized (call UltraRT_Init)";
    char s_StatusOut[512];
    float s_Latest[kMaxSlots];
    float s_LatestCpu[kMaxSlots];
    int s_LatestCount = 0;
    volatile LONG s_Ready = 0;
    volatile LONG64 s_ResolvedFrames = 0;
    volatile LONG64 s_DroppedFrames = 0;

    void SetStatus(const char* fmt, ...)
    {
        char buf[512];
        va_list args;
        va_start(args, fmt);
        vsnprintf(buf, sizeof buf, fmt, args);
        va_end(args);
        AcquireSRWLockExclusive(&s_Lock);
        memcpy(s_Status, buf, sizeof buf);
        ReleaseSRWLockExclusive(&s_Lock);
        OutputDebugStringA("[UltraRT.Native] ");
        OutputDebugStringA(buf);
        OutputDebugStringA("\n");
    }

    // --- render-thread state ---
    struct Frame
    {
        uint64_t beginMask, endMask;
        UINT64 fence;
        bool pending;      // resolved on the GPU timeline, waiting for the frame fence
        LONGLONG cpuBegin[kMaxSlots];  // render-thread clock (QueryPerformanceCounter) at Begin
        float cpuMs[kMaxSlots];        // render-thread time from Begin to End
    };

    ID3D12Device* s_Device = nullptr;
    ID3D12QueryHeap* s_Heap = nullptr;
    ID3D12Resource* s_Readback = nullptr;
    const UINT64* s_Mapped = nullptr;
    ID3D12Fence* s_FrameFence = nullptr;
    double s_TicksToMs = 0;
    double s_QpcToMs = 0;
    Frame s_Frames[kRing];
    int s_Cur = 0;
    bool s_CurDropped = false;
    bool s_RtInit = false, s_RtFailed = false;

    void ReleaseResources()
    {
        InterlockedExchange(&s_Ready, 0);
        if (s_Readback && s_Mapped) s_Readback->Unmap(0, nullptr);
        s_Mapped = nullptr;
        if (s_Readback) s_Readback->Release();
        s_Readback = nullptr;
        if (s_Heap) s_Heap->Release();
        s_Heap = nullptr;
        s_Device = nullptr;      // not AddRef'd
        s_FrameFence = nullptr;  // not AddRef'd
        memset(s_Frames, 0, sizeof s_Frames);
        s_Cur = 0;
        s_CurDropped = false;
        s_RtInit = s_RtFailed = false;
    }

    bool RenderThreadInit()
    {
        s_RtFailed = true; // until proven otherwise
        if (!s_HaveD3D) { SetStatus("no IUnityGraphicsD3D12v6/v7 interface"); return false; }

        s_Device = s_D3D.GetDevice();
        ID3D12CommandQueue* queue = s_D3D.GetCommandQueue();
        s_FrameFence = s_D3D.GetFrameFence();
        if (!s_Device || !queue || !s_FrameFence) { SetStatus("Unity returned null device/queue/fence"); return false; }

        UINT64 freq = 0;
        HRESULT hr = queue->GetTimestampFrequency(&freq);
        if (FAILED(hr) || freq == 0) { SetStatus("GetTimestampFrequency failed (hr=0x%08X)", (unsigned)hr); return false; }
        s_TicksToMs = 1000.0 / (double)freq;
        LARGE_INTEGER qpf = {};
        QueryPerformanceFrequency(&qpf);
        s_QpcToMs = qpf.QuadPart > 0 ? 1000.0 / (double)qpf.QuadPart : 0.0;

        D3D12_QUERY_HEAP_DESC qd = {};
        qd.Type = D3D12_QUERY_HEAP_TYPE_TIMESTAMP;
        qd.Count = kQueryCount;
        hr = s_Device->CreateQueryHeap(&qd, IID_PPV_ARGS(&s_Heap));
        if (FAILED(hr)) { SetStatus("CreateQueryHeap failed (hr=0x%08X)", (unsigned)hr); return false; }
        s_Heap->SetName(L"UltraRT GpuTimer");

        D3D12_HEAP_PROPERTIES hp = {};
        hp.Type = D3D12_HEAP_TYPE_READBACK;
        D3D12_RESOURCE_DESC rd = {};
        rd.Dimension = D3D12_RESOURCE_DIMENSION_BUFFER;
        rd.Width = (UINT64)kQueryCount * sizeof(UINT64);
        rd.Height = 1;
        rd.DepthOrArraySize = 1;
        rd.MipLevels = 1;
        rd.SampleDesc.Count = 1;
        rd.Layout = D3D12_TEXTURE_LAYOUT_ROW_MAJOR;
        hr = s_Device->CreateCommittedResource(&hp, D3D12_HEAP_FLAG_NONE, &rd, D3D12_RESOURCE_STATE_COPY_DEST,
                                               nullptr, IID_PPV_ARGS(&s_Readback));
        if (FAILED(hr)) { SetStatus("readback buffer creation failed (hr=0x%08X)", (unsigned)hr); return false; }
        s_Readback->SetName(L"UltraRT GpuTimer readback");

        // Persistent map is allowed for readback heaps; we only read ranges whose frame fence has passed.
        D3D12_RANGE read = { 0, (SIZE_T)rd.Width };
        void* p = nullptr;
        hr = s_Readback->Map(0, &read, &p);
        if (FAILED(hr)) { SetStatus("readback Map failed (hr=0x%08X)", (unsigned)hr); return false; }
        s_Mapped = static_cast<const UINT64*>(p);

        // Event configuration happens at device initialization (main thread), see ConfigureEvents().
        memset(s_Frames, 0, sizeof s_Frames);
        s_RtFailed = false;
        s_RtInit = true;
        InterlockedExchange(&s_Ready, 1);
        SetStatus("ready: IUnityGraphicsD3D12v%d, timestamp freq %llu Hz, %d slots x %d frames",
                  s_D3D.version, (unsigned long long)freq, kMaxSlots, kRing);
        return true;
    }

    // Reads every pending frame whose GPU work is done (oldest first, so the newest wins).
    void Collect()
    {
        UINT64 completed = s_FrameFence->GetCompletedValue();
        if (completed == UINT64_MAX)
        {
            // Fences read UINT64_MAX once the device is removed.
            HRESULT reason = s_Device->GetDeviceRemovedReason();
            SetStatus("device removed (reason 0x%08X); timer stopped", (unsigned)reason);
            InterlockedExchange(&s_Ready, 0);
            s_RtFailed = true;
            s_RtInit = false;
            return;
        }
        for (int i = 1; i <= kRing; i++)
        {
            Frame& f = s_Frames[(s_Cur + i) % kRing];
            if (!f.pending || completed < f.fence) continue;
            const UINT64* q = s_Mapped + (size_t)((&f - s_Frames) * kQueriesPerFrame);
            float ms[kMaxSlots], cpu[kMaxSlots];
            int count = 0;
            uint64_t valid = f.beginMask & f.endMask;
            for (int s = 0; s < kMaxSlots; s++)
            {
                ms[s] = cpu[s] = -1.0f;
                if (!(valid >> s & 1)) continue;
                UINT64 b = q[s * 2], e = q[s * 2 + 1];
                if (e >= b) ms[s] = (float)((double)(e - b) * s_TicksToMs);
                cpu[s] = f.cpuMs[s];
                count = s + 1;
            }
            AcquireSRWLockExclusive(&s_Lock);
            memcpy(s_Latest, ms, sizeof ms);
            memcpy(s_LatestCpu, cpu, sizeof cpu);
            s_LatestCount = count;
            ReleaseSRWLockExclusive(&s_Lock);
            InterlockedIncrement64(&s_ResolvedFrames);
            f = {};
        }
    }

    ID3D12GraphicsCommandList* CurrentCommandList()
    {
        UnityGraphicsD3D12RecordingState rs = {};
        if (!s_D3D.CommandRecordingState(&rs) || !rs.commandList)
        {
            static bool warned = false;
            if (!warned) { warned = true; SetStatus("CommandRecordingState returned no command list"); }
            return nullptr;
        }
        return rs.commandList;
    }

    // Returns the current frame if it can be written, else marks the frame dropped.
    Frame* WritableFrame()
    {
        if (s_CurDropped) return nullptr;
        Frame* f = &s_Frames[s_Cur];
        if (f->pending)
        {
            Collect(); // ring wrapped: the GPU is more than kRing frames behind
            if (!s_RtInit) return nullptr;
            if (f->pending) { s_CurDropped = true; InterlockedIncrement64(&s_DroppedFrames); return nullptr; }
        }
        return f;
    }

    void OnBegin(int slot)
    {
        Frame* f = WritableFrame();
        ID3D12GraphicsCommandList* cl = f ? CurrentCommandList() : nullptr;
        if (!cl) return;
        cl->EndQuery(s_Heap, D3D12_QUERY_TYPE_TIMESTAMP, (UINT)(s_Cur * kQueriesPerFrame + slot * 2));
        f->beginMask |= 1ull << slot;
        f->endMask &= ~(1ull << slot); // re-begun: previous end no longer pairs with it
        // Last thing in Begin, first thing in End: the span is the commands between the two events, not this bookkeeping.
        LARGE_INTEGER now;
        QueryPerformanceCounter(&now);
        f->cpuBegin[slot] = now.QuadPart;
    }

    void OnEnd(int slot)
    {
        LARGE_INTEGER now;
        QueryPerformanceCounter(&now);
        Frame* f = WritableFrame();
        if (!f || !(f->beginMask >> slot & 1)) return;
        f->cpuMs[slot] = (float)((double)(now.QuadPart - f->cpuBegin[slot]) * s_QpcToMs);
        ID3D12GraphicsCommandList* cl = CurrentCommandList();
        if (!cl) return;
        cl->EndQuery(s_Heap, D3D12_QUERY_TYPE_TIMESTAMP, (UINT)(s_Cur * kQueriesPerFrame + slot * 2 + 1));
        f->endMask |= 1ull << slot;
    }

    void OnResolve()
    {
        Frame* f = WritableFrame();
        if (f)
        {
            uint64_t valid = f->beginMask & f->endMask;
            ID3D12GraphicsCommandList* cl = valid ? CurrentCommandList() : nullptr;
            if (cl)
            {
                // Resolve only queries that were written this frame, in contiguous runs.
                const UINT base = (UINT)(s_Cur * kQueriesPerFrame);
                for (int s = 0; s < kMaxSlots;)
                {
                    if (!(valid >> s & 1)) { s++; continue; }
                    int e = s;
                    while (e < kMaxSlots && (valid >> e & 1)) e++;
                    UINT first = base + (UINT)s * 2;
                    cl->ResolveQueryData(s_Heap, D3D12_QUERY_TYPE_TIMESTAMP, first, (UINT)(e - s) * 2, s_Readback,
                                         (UINT64)first * sizeof(UINT64));
                    s = e;
                }
                f->beginMask = f->endMask = valid;
                f->fence = s_D3D.GetNextFrameFenceValue();
                f->pending = true;
            }
            else
            {
                *f = {};
            }
            s_Cur = (s_Cur + 1) % kRing;
        }
        // A dropped frame keeps s_Cur on the still-pending ring entry; try again next frame.
        s_CurDropped = false;
        if (s_RtInit) Collect();
    }

    void UNITY_INTERFACE_API OnRenderEvent(int eventId, void* data)
    {
        if (Dlss_OnRenderEvent(eventId, data)) return; // DLSS Ray Reconstruction events (Dlss.cpp)
        if (eventId < kEventBegin || eventId > kEventResolve) return;
        if (!s_RtInit)
        {
            if (s_RtFailed || !RenderThreadInit()) return;
        }
        int slot = (int)(intptr_t)data;
        switch (eventId)
        {
            case kEventBegin: if (slot >= 0 && slot < kMaxSlots) OnBegin(slot); break;
            case kEventEnd: if (slot >= 0 && slot < kMaxSlots) OnEnd(slot); break;
            case kEventResolve: OnResolve(); break;
        }
    }

    // Event flags default to 0: configuring at device init (not mid-frame) is what makes CommandRecordingState work,
    // Override for experiments with the ULTRART_GPUTIMER_FLAGS environment variable (decimal or 0x hex).
    UINT32 s_EventFlags = 0; // timestamps touch no bound state; ModifiesCommandBuffersState breaks draws that follow inside a camera

    void ConfigureEvents()
    {
        char buf[32] = {};
        if (GetEnvironmentVariableA("ULTRART_GPUTIMER_FLAGS", buf, sizeof buf) > 0)
            s_EventFlags = (UINT32)strtoul(buf, nullptr, 0);
        UnityD3D12PluginEventConfig cfg = {};
        cfg.graphicsQueueAccess = kUnityD3D12GraphicsQueueAccess_DontCare;
        cfg.flags = s_EventFlags;
        cfg.ensureActiveRenderTextureIsBound = false;
        for (int id = kEventBegin; id <= kEventResolve; id++) s_D3D.ConfigureEvent(id, &cfg);
    }

    void UNITY_INTERFACE_API OnGraphicsDeviceEvent(UnityGfxDeviceEventType type)
    {
        if (type == kUnityGfxDeviceEventInitialize)
        {
            ReleaseResources();
            s_HaveD3D = false;
            UnityGfxRenderer renderer = s_Graphics->GetRenderer();
            if (renderer != kUnityGfxRendererD3D12)
            {
                SetStatus("renderer is %d, not D3D12 (%d); timer disabled", (int)renderer, (int)kUnityGfxRendererD3D12);
                return;
            }
            if (auto v7 = s_Unity->Get<IUnityGraphicsD3D12v7>())
            {
                s_D3D = { v7->GetDevice, v7->GetFrameFence, v7->GetNextFrameFenceValue, v7->GetCommandQueue,
                          v7->ConfigureEvent, v7->CommandRecordingState, 7 };
                s_HaveD3D = true;
            }
            else if (auto v6 = s_Unity->Get<IUnityGraphicsD3D12v6>())
            {
                s_D3D = { v6->GetDevice, v6->GetFrameFence, v6->GetNextFrameFenceValue, v6->GetCommandQueue,
                          v6->ConfigureEvent, v6->CommandRecordingState, 6 };
                s_HaveD3D = true;
            }
            if (s_HaveD3D)
            {
                ConfigureEvents();
                SetStatus("D3D12 interface v%d acquired, event flags 0x%X; waiting for first render event", s_D3D.version, s_EventFlags);
            }
            else SetStatus("IUnityGraphicsD3D12v6/v7 not available");
            Dlss_OnDeviceEvent(type);
        }
        else if (type == kUnityGfxDeviceEventShutdown)
        {
            Dlss_OnDeviceEvent(type);
            ReleaseResources();
            s_HaveD3D = false;
            SetStatus("graphics device shut down");
        }
    }

    // Kept free of C++ objects so __try is allowed here.
    bool CallRegister(UnityRegisterPluginFn fn, HMODULE self, DWORD* code)
    {
        __try
        {
            fn(self);
            return true;
        }
        __except (EXCEPTION_EXECUTE_HANDLER)
        {
            *code = GetExceptionCode();
            return false;
        }
    }
}

IUnityInterfaces* Native_UnityInterfaces() { return s_Unity; }

// ------------------------------------------------------------------ Unity entry points

ULTRART_API void UNITY_INTERFACE_API UnityPluginLoad(IUnityInterfaces* unityInterfaces)
{
    s_Unity = unityInterfaces;
    s_Graphics = s_Unity->Get<IUnityGraphics>();
    s_Graphics->RegisterDeviceEventCallback(OnGraphicsDeviceEvent);
    // The device already exists when we are loaded late: initialize by hand, as Unity's own samples do.
    OnGraphicsDeviceEvent(kUnityGfxDeviceEventInitialize);
}

ULTRART_API void UNITY_INTERFACE_API UnityPluginUnload()
{
    if (s_Graphics) s_Graphics->UnregisterDeviceEventCallback(OnGraphicsDeviceEvent);
    Dlss_Unload();
    ReleaseResources();
    s_HaveD3D = false;
}

ULTRART_API UnityRenderingEventAndData UNITY_INTERFACE_API GetRenderEventAndDataFunc()
{
    return OnRenderEvent;
}

// ------------------------------------------------------------------ UltraRT API

// Main thread only. Returns 1 when Unity has handed us IUnityInterfaces and the renderer is D3D12, else 0
// (see UltraRT_GetStatus). Safe to call repeatedly.
ULTRART_API int UltraRT_Init()
{
    if (!s_Unity)
    {
        HMODULE player = GetModuleHandleW(L"UnityPlayer.dll");
        if (!player) { SetStatus("UnityPlayer.dll is not loaded in this process"); return 0; }

        UnityLocateResult r = LocateUnityRegisterPlugin(reinterpret_cast<const uint8_t*>(player));
        if (!r.function) { SetStatus("%s (candidates=%d)", r.error, r.candidates); return 0; }

        HMODULE self = nullptr;
        GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                           reinterpret_cast<LPCWSTR>(&UltraRT_Init), &self);
        DWORD code = 0;
        if (!CallRegister(reinterpret_cast<UnityRegisterPluginFn>(const_cast<uint8_t*>(r.function)), self, &code))
        {
            SetStatus("Unity plugin registration at UnityPlayer+0x%X faulted (0x%08X)", r.rva, (unsigned)code);
            return 0;
        }
        if (!s_Unity)
        {
            SetStatus("Unity plugin registration at UnityPlayer+0x%X ran but UnityPluginLoad was not called "
                      "(not on the main thread? see Player.log)", r.rva);
            return 0;
        }
    }
    return s_HaveD3D ? 1 : 0;
}

ULTRART_API int UltraRT_IsReady()
{
    return (int)InterlockedCompareExchange(&s_Ready, 0, 0);
}

ULTRART_API const char* UltraRT_GetStatus()
{
    AcquireSRWLockShared(&s_Lock);
    memcpy(s_StatusOut, s_Status, sizeof s_Status);
    ReleaseSRWLockShared(&s_Lock);
    return s_StatusOut;
}

ULTRART_API int UltraRT_GetEventBase()
{
    return kEventBase;
}

ULTRART_API int UltraRT_GetMaxSlots()
{
    return kMaxSlots;
}

// Copies the newest completed frame's durations (ms) into outMs[0..maxSlots). Slots not timed in that frame
// get -1. Returns 1 + the highest timed slot (0 if no frame has completed yet).
ULTRART_API int UltraRT_GetTimings(float* outMs, int maxSlots)
{
    if (!outMs || maxSlots <= 0) return 0;
    int n = maxSlots < kMaxSlots ? maxSlots : kMaxSlots;
    AcquireSRWLockShared(&s_Lock);
    int count = s_LatestCount;
    if (count > 0) memcpy(outMs, s_Latest, sizeof(float) * n);
    ReleaseSRWLockShared(&s_Lock);
    if (count <= 0)
        for (int i = 0; i < n; i++) outMs[i] = -1.0f;
    for (int i = n; i < maxSlots; i++) outMs[i] = -1.0f;
    return count < n ? count : n;
}

// The render thread's time between Begin and End of each slot, for the same frame as UltraRT_GetTimings (ms; -1 for a
// slot not timed in that frame). Returns 1 + the highest timed slot.
ULTRART_API int UltraRT_GetCpuTimings(float* outMs, int maxSlots)
{
    if (!outMs || maxSlots <= 0) return 0;
    int n = maxSlots < kMaxSlots ? maxSlots : kMaxSlots;
    AcquireSRWLockShared(&s_Lock);
    int count = s_LatestCount;
    if (count > 0) memcpy(outMs, s_LatestCpu, sizeof(float) * n);
    ReleaseSRWLockShared(&s_Lock);
    if (count <= 0)
        for (int i = 0; i < n; i++) outMs[i] = -1.0f;
    for (int i = n; i < maxSlots; i++) outMs[i] = -1.0f;
    return count < n ? count : n;
}

// Diagnostics: frames read back so far, and frames skipped because the ring was still in flight.
ULTRART_API long long UltraRT_GetResolvedFrameCount() { return InterlockedCompareExchange64(&s_ResolvedFrames, 0, 0); }
ULTRART_API long long UltraRT_GetDroppedFrameCount() { return InterlockedCompareExchange64(&s_DroppedFrames, 0, 0); }
