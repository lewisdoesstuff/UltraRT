// UltraRT.Native: DLSS Ray Reconstruction (NGX "DLSS-D") on Unity's D3D12 device. See docs/dlss.md.
//
// NGX calls happen on Unity's render thread inside plugin events, except the warm-up (UltraRT_Dlss_Warm): NGX's
// initialisation (seconds: it loads the model DLL and its kernels) and the feature's creation run on a worker thread,
// the creation's commands on a queue of our own, so neither stalls the render thread. s_NgxCs serialises the two
// threads; a render event that finds the worker busy skips (the C# side doesn't ask for RR until it is done).
// The C# side (src/UltraRT/DlssRR.cs) writes a DlssFrame into a ring slot with UltraRT_Dlss_SetFrame and issues
//   IssuePluginEventAndData(GetRenderEventAndDataFunc(), UltraRT_Dlss_GetEventBase() + k, slot)
// k = 0  evaluate, recorded into Unity's own command list (kEventEvalUnity; event flag ModifiesCommandBuffersState)
// k = 1  evaluate in our own command list, submitted with IUnityGraphicsD3D12v7::ExecuteCommandList and resource-state
//        declarations (kEventEvalOwn; event flag FlushCommandBuffers so Unity's earlier work is submitted first)
// k = 2  release the feature (resize, mode switch)
//
// Resource states: DLSS wants inputs in NON_PIXEL_SHADER_RESOURCE and the output in UNORDERED_ACCESS, and returns
// them in those states. Every resource we pass is written as a UAV by a compute dispatch just before the event
// (RayReconstruction.compute Prepare), so Unity's tracked state for all of them is UNORDERED_ACCESS:
//   own list:   declared to Unity as {expected, after} = {NPSR, NPSR} for inputs, {UAV, UAV} for the output;
//   Unity list: we transition UAV -> NPSR -> eval -> NPSR -> UAV ourselves, so Unity's tracking stays right.
//
// This software contains source code provided by NVIDIA Corporation (the NGX helper headers it includes).

#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <d3d11.h> // nvsdk_ngx_helpers_dlssd_d3d.h declares D3D11 entry points too
#include <d3d12.h>
#include <dxgi.h>
#include <stdarg.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include "IUnityInterface.h"
#include "IUnityGraphics.h"
#include "IUnityGraphicsD3D12.h"
#include "Native.h"

#include "nvsdk_ngx.h"
#include "nvsdk_ngx_helpers.h"
#include "nvsdk_ngx_helpers_dlssd_d3d.h"

// NGX's engine version string follows Version.props: build.ps1 passes /DULTRART_VERSION=<Major.Minor.Patch> (a bare
// pp-number, stringised here, so no quotes have to survive cmd). A build without it (an IDE, a hand cl) says "dev".
#define ULTRART_STR2(x) #x
#define ULTRART_STR(x) ULTRART_STR2(x)
#ifdef ULTRART_VERSION
#define ULTRART_ENGINE_VERSION "UltraRT-" ULTRART_STR(ULTRART_VERSION)
#else
#define ULTRART_ENGINE_VERSION "UltraRT-dev"
#endif

#define ULTRART_API extern "C" __declspec(dllexport)

namespace
{
    constexpr int kEventBase = 94;                  // GpuTimer uses 91-93
    constexpr int kEventEvalUnity = kEventBase + 0;
    constexpr int kEventEvalOwn = kEventBase + 1;
    constexpr int kEventRelease = kEventBase + 2;
    constexpr int kFrameRing = 16;
    constexpr int kAllocRing = 8;

    // Must match DlssRR.cs FrameData (sequential layout, 8-byte pointers).
    struct DlssFrame
    {
        ID3D12Resource* color;      // RGBA16F noisy radiance (render size)
        ID3D12Resource* diffuse;    // diffuse albedo
        ID3D12Resource* specular;   // specular albedo
        ID3D12Resource* normals;    // xyz world normal, w roughness (packed)
        ID3D12Resource* depth;      // linear view depth
        ID3D12Resource* motion;     // RG16F, pixels, previous - current
        ID3D12Resource* specHit;    // optional: specular hit distance
        ID3D12Resource* output;     // RGBA16F output size, UAV
        int renderW, renderH, outW, outH;
        int reset, preset, quality, flags; // flags: bit0 = pass specHit + matrices, bit1 = log resource states
        float jitterX, jitterY, mvScaleX, mvScaleY;
        float frameTimeMs, pad0, pad1, pad2;
        float worldToView[16];      // row-major, row-vector convention (i.e. Unity's column-major memory order)
        float viewToClip[16];
    };

    struct D3D12Api
    {
        ID3D12Device* (UNITY_INTERFACE_API* GetDevice)();
        ID3D12Fence* (UNITY_INTERFACE_API* GetFrameFence)();
        UINT64 (UNITY_INTERFACE_API* GetNextFrameFenceValue)();
        UINT64 (UNITY_INTERFACE_API* ExecuteCommandList)(ID3D12GraphicsCommandList*, int, UnityGraphicsD3D12ResourceState*);
        void (UNITY_INTERFACE_API* ConfigureEvent)(int, const UnityD3D12PluginEventConfig*);
        bool (UNITY_INTERFACE_API* CommandRecordingState)(UnityGraphicsD3D12RecordingState*);
    };

    D3D12Api s_Api = {};
    bool s_HaveApi = false;
    IUnityGraphicsD3D12* s_V1 = nullptr; // obsolete interface; only for logging Unity's tracked resource states

    // ---- status / log (any thread)
    SRWLOCK s_Lock = SRWLOCK_INIT;
    char s_Status[1024] = "not initialized";
    char s_StatusOut[1024];
    char s_Log[65536];
    int s_LogLen = 0;
    volatile LONG s_State = 0; // 0 idle, 1 NGX ready, 2 feature ready, -1 failed (see status)
    volatile LONG64 s_Evals = 0;

    // ---- warm-up worker (UltraRT_Dlss_Warm)
    CRITICAL_SECTION s_NgxCs;      // held by whoever calls NGX: the render thread (TryEnter) or the worker
    INIT_ONCE s_NgxCsOnce = INIT_ONCE_STATIC_INIT;
    BOOL CALLBACK InitCs(PINIT_ONCE, PVOID, PVOID*) { InitializeCriticalSection(&s_NgxCs); return TRUE; }
    void EnsureCs() { InitOnceExecuteOnce(&s_NgxCsOnce, InitCs, nullptr, nullptr); }
    HANDLE s_Worker = nullptr;
    volatile LONG s_Busy = 0;      // 1 while the worker runs
    volatile LONG s_ReleasePending = 0;
    struct WarmRequest { int renderW, renderH, outW, outH, preset, quality; };
    WarmRequest s_Warm = {};

    double NowMs()
    {
        static LARGE_INTEGER freq = {};
        if (!freq.QuadPart) QueryPerformanceFrequency(&freq);
        LARGE_INTEGER t;
        QueryPerformanceCounter(&t);
        return (double)t.QuadPart * 1000.0 / (double)freq.QuadPart;
    }

    void AppendLog(const char* line)
    {
        AcquireSRWLockExclusive(&s_Lock);
        int n = (int)strlen(line);
        if (s_LogLen + n + 2 < (int)sizeof s_Log)
        {
            memcpy(s_Log + s_LogLen, line, n);
            s_LogLen += n;
            if (n == 0 || line[n - 1] != '\n') s_Log[s_LogLen++] = '\n';
            s_Log[s_LogLen] = 0;
        }
        ReleaseSRWLockExclusive(&s_Lock);
        OutputDebugStringA("[UltraRT.Dlss] ");
        OutputDebugStringA(line);
        OutputDebugStringA("\n");
    }

    void SetStatus(const char* fmt, ...)
    {
        char buf[1024];
        va_list args;
        va_start(args, fmt);
        vsnprintf(buf, sizeof buf, fmt, args);
        va_end(args);
        AcquireSRWLockExclusive(&s_Lock);
        memcpy(s_Status, buf, sizeof buf);
        ReleaseSRWLockExclusive(&s_Lock);
        AppendLog(buf);
    }

    void NVSDK_CONV NgxLog(const char* message, NVSDK_NGX_Logging_Level, NVSDK_NGX_Feature)
    {
        // NGX's start-up is chatty (config tables, model cache probes); keep the rest (its full log is in the data dir).
        static const char* const noise[] = { "NGXLoadConfig", "failed to load (cmsid", "IsFeatureDenied", "LoadMappingFileData", "NGXReportStatusFeedback" };
        for (const char* n : noise) if (message && strstr(message, n)) return;
        char buf[1024];
        snprintf(buf, sizeof buf, "NGX: %s", message ? message : "");
        size_t n = strlen(buf);
        while (n > 0 && (buf[n - 1] == '\n' || buf[n - 1] == '\r')) buf[--n] = 0;
        AppendLog(buf);
    }

    // ---- configuration from C# (main thread, before the first event)
    wchar_t s_DllDir[MAX_PATH] = L"";
    wchar_t s_DataDir[MAX_PATH] = L"";

    // ---- frames from C#
    DlssFrame s_Frames[kFrameRing];

    // ---- render-thread state
    ID3D12Device* s_Device = nullptr;
    bool s_NgxInit = false, s_NgxFailed = false;
    NVSDK_NGX_Parameter* s_Params = nullptr;
    NVSDK_NGX_Handle* s_Feature = nullptr;
    int s_FeatW = 0, s_FeatH = 0, s_FeatOutW = 0, s_FeatOutH = 0, s_FeatPreset = -1, s_FeatQuality = -1;
    UINT64 s_LastFence = 0;
    int s_StateLogs = 0;

    ID3D12CommandAllocator* s_Alloc[kAllocRing] = {};
    UINT64 s_AllocFence[kAllocRing] = {};
    ID3D12GraphicsCommandList* s_List = nullptr;
    int s_AllocCur = 0;

    const char* ResultText(NVSDK_NGX_Result r)
    {
        static char buf[256];
        const wchar_t* w = GetNGXResultAsString(r);
        snprintf(buf, sizeof buf, "0x%08X %ls", (unsigned)r, w ? w : L"");
        return buf;
    }

    bool WaitFence(UINT64 value, DWORD ms)
    {
        ID3D12Fence* f = s_Api.GetFrameFence();
        if (!f || value == 0 || f->GetCompletedValue() >= value) return true;
        HANDLE e = CreateEventW(nullptr, FALSE, FALSE, nullptr);
        if (!e) return false;
        bool ok = SUCCEEDED(f->SetEventOnCompletion(value, e)) && WaitForSingleObject(e, ms) == WAIT_OBJECT_0;
        CloseHandle(e);
        return ok;
    }

    void ReleaseFeature()
    {
        if (!s_Feature) return;
        // The feature's resources may still be used by frames in flight.
        if (!WaitFence(s_LastFence, 2000))
        {
            AppendLog("release: GPU did not finish the last evaluate within 2 s; leaking the feature");
            s_Feature = nullptr;
            return;
        }
        NVSDK_NGX_Result r = NVSDK_NGX_D3D12_ReleaseFeature(s_Feature);
        if (NVSDK_NGX_FAILED(r)) AppendLog("ReleaseFeature failed");
        s_Feature = nullptr;
        s_FeatW = s_FeatH = s_FeatOutW = s_FeatOutH = 0;
        if (s_State == 2) InterlockedExchange(&s_State, 1);
    }

    void JoinWorker()
    {
        if (!s_Worker) return;
        WaitForSingleObject(s_Worker, INFINITE);
        CloseHandle(s_Worker);
        s_Worker = nullptr;
    }

    void ShutdownAll()
    {
        JoinWorker();
        if (s_NgxInit)
        {
            ReleaseFeature();
            if (s_Params) NVSDK_NGX_D3D12_DestroyParameters(s_Params);
            s_Params = nullptr;
            NVSDK_NGX_D3D12_Shutdown1(s_Device);
        }
        for (int i = 0; i < kAllocRing; i++)
        {
            if (s_Alloc[i]) s_Alloc[i]->Release();
            s_Alloc[i] = nullptr;
            s_AllocFence[i] = 0;
        }
        if (s_List) s_List->Release();
        s_List = nullptr;
        s_NgxInit = false;
        s_NgxFailed = false;
        s_Device = nullptr;
        InterlockedExchange(&s_State, 0);
    }

    bool InitNgx()
    {
        s_NgxFailed = true;
        if (!s_HaveApi) { SetStatus("no IUnityGraphicsD3D12v6/v7"); return false; }
        s_Device = s_Api.GetDevice();
        if (!s_Device) { SetStatus("Unity returned no D3D12 device"); return false; }

        const wchar_t* paths[1] = { s_DllDir };
        NVSDK_NGX_FeatureCommonInfo info = {};
        info.PathListInfo.Path = paths;
        info.PathListInfo.Length = s_DllDir[0] ? 1u : 0u;
        info.LoggingInfo.LoggingCallback = NgxLog;
        info.LoggingInfo.MinimumLoggingLevel = NVSDK_NGX_LOGGING_LEVEL_ON;
        info.LoggingInfo.DisableOtherLoggingSinks = false;
        const wchar_t* data = s_DataDir[0] ? s_DataDir : L".";
        double t0 = NowMs();
        // A project id (GUID format) and engine type CUSTOM: the documented route for apps without an NVIDIA app id.
        NVSDK_NGX_Result r = NVSDK_NGX_D3D12_Init_with_ProjectID("6d1e8a4b-2f3c-4b7e-9a51-0c2d7e9f4a18",
            NVSDK_NGX_ENGINE_TYPE_CUSTOM, ULTRART_ENGINE_VERSION, data, s_Device, &info, NVSDK_NGX_Version_API);
        if (NVSDK_NGX_FAILED(r)) { SetStatus("NVSDK_NGX_D3D12_Init_with_ProjectID failed: %s", ResultText(r)); InterlockedExchange(&s_State, -1); return false; }
        s_NgxInit = true;

        NVSDK_NGX_Parameter* caps = nullptr;
        r = NVSDK_NGX_D3D12_GetCapabilityParameters(&caps);
        if (NVSDK_NGX_FAILED(r) || !caps) { SetStatus("GetCapabilityParameters failed: %s", ResultText(r)); InterlockedExchange(&s_State, -1); return false; }
        int avail = 0, needsDriver = 0, initResult = 0;
        unsigned int minMajor = 0, minMinor = 0;
        NVSDK_NGX_Parameter_GetI(caps, NVSDK_NGX_Parameter_SuperSamplingDenoising_Available, &avail);
        NVSDK_NGX_Parameter_GetI(caps, NVSDK_NGX_Parameter_SuperSamplingDenoising_NeedsUpdatedDriver, &needsDriver);
        NVSDK_NGX_Parameter_GetUI(caps, NVSDK_NGX_Parameter_SuperSamplingDenoising_MinDriverVersionMajor, &minMajor);
        NVSDK_NGX_Parameter_GetUI(caps, NVSDK_NGX_Parameter_SuperSamplingDenoising_MinDriverVersionMinor, &minMinor);
        NVSDK_NGX_Parameter_GetI(caps, NVSDK_NGX_Parameter_SuperSamplingDenoising_FeatureInitResult, &initResult);
        NVSDK_NGX_D3D12_DestroyParameters(caps);
        if (!avail)
        {
            SetStatus("Ray Reconstruction not available: needsUpdatedDriver=%d (min %u.%u), featureInitResult=%s",
                      needsDriver, minMajor, minMinor, ResultText((NVSDK_NGX_Result)initResult));
            InterlockedExchange(&s_State, -1);
            return false;
        }
        r = NVSDK_NGX_D3D12_AllocateParameters(&s_Params);
        if (NVSDK_NGX_FAILED(r) || !s_Params) { SetStatus("AllocateParameters failed: %s", ResultText(r)); InterlockedExchange(&s_State, -1); return false; }

        s_NgxFailed = false;
        InterlockedExchange(&s_State, 1);
        SetStatus("NGX ready, Ray Reconstruction available (dll dir %ls; init %.0f ms on the %s thread)", s_DllDir, NowMs() - t0,
                  s_Busy ? "warm-up" : "render");
        return true;
    }

    bool EnsureOwnList()
    {
        if (s_List) return true;
        for (int i = 0; i < kAllocRing; i++)
        {
            if (FAILED(s_Device->CreateCommandAllocator(D3D12_COMMAND_LIST_TYPE_DIRECT, IID_PPV_ARGS(&s_Alloc[i]))))
            { SetStatus("CreateCommandAllocator failed"); return false; }
            s_Alloc[i]->SetName(L"UltraRT DLSS allocator");
        }
        if (FAILED(s_Device->CreateCommandList(0, D3D12_COMMAND_LIST_TYPE_DIRECT, s_Alloc[0], nullptr, IID_PPV_ARGS(&s_List))))
        { SetStatus("CreateCommandList failed"); return false; }
        s_List->SetName(L"UltraRT DLSS");
        s_List->Close();
        return true;
    }

    bool FeatureMatches(int renderW, int renderH, int outW, int outH, int preset, int quality)
    {
        return s_Feature && s_FeatW == renderW && s_FeatH == renderH && s_FeatOutW == outW && s_FeatOutH == outH &&
               s_FeatPreset == preset && s_FeatQuality == quality;
    }

    bool EnsureFeature(ID3D12GraphicsCommandList* cl, const DlssFrame& f, const char* where = "render")
    {
        if (FeatureMatches(f.renderW, f.renderH, f.outW, f.outH, f.preset, f.quality))
            return true;
        ReleaseFeature();
        double t0 = NowMs();
        unsigned preset = (unsigned)(f.preset < 0 ? 0 : f.preset);
        NVSDK_NGX_Parameter_SetUI(s_Params, NVSDK_NGX_Parameter_RayReconstruction_Hint_Render_Preset_DLAA, preset);
        NVSDK_NGX_Parameter_SetUI(s_Params, NVSDK_NGX_Parameter_RayReconstruction_Hint_Render_Preset_Quality, preset);
        NVSDK_NGX_Parameter_SetUI(s_Params, NVSDK_NGX_Parameter_RayReconstruction_Hint_Render_Preset_Balanced, preset);
        NVSDK_NGX_Parameter_SetUI(s_Params, NVSDK_NGX_Parameter_RayReconstruction_Hint_Render_Preset_Performance, preset);
        NVSDK_NGX_Parameter_SetUI(s_Params, NVSDK_NGX_Parameter_RayReconstruction_Hint_Render_Preset_UltraPerformance, preset);
        NVSDK_NGX_DLSSD_Create_Params cp = {};
        cp.InDenoiseMode = NVSDK_NGX_DLSS_Denoise_Mode_DLUnified;
        cp.InRoughnessMode = NVSDK_NGX_DLSS_Roughness_Mode_Packed;
        cp.InUseHWDepth = NVSDK_NGX_DLSS_Depth_Type_Linear;
        cp.InWidth = (unsigned)f.renderW;
        cp.InHeight = (unsigned)f.renderH;
        cp.InTargetWidth = (unsigned)f.outW;
        cp.InTargetHeight = (unsigned)f.outH;
        cp.InPerfQualityValue = (NVSDK_NGX_PerfQuality_Value)f.quality;
        cp.InFeatureCreateFlags = NVSDK_NGX_DLSS_Feature_Flags_IsHDR | NVSDK_NGX_DLSS_Feature_Flags_MVLowRes;
        cp.InEnableOutputSubrects = false;
        NVSDK_NGX_Result r = NGX_D3D12_CREATE_DLSSD_EXT(cl, 1, 1, &s_Feature, s_Params, &cp);
        if (NVSDK_NGX_FAILED(r) || !s_Feature)
        {
            s_Feature = nullptr;
            SetStatus("CreateFeature(RayReconstruction %dx%d -> %dx%d, preset %d, quality %d) failed: %s",
                      f.renderW, f.renderH, f.outW, f.outH, f.preset, f.quality, ResultText(r));
            return false;
        }
        s_FeatW = f.renderW; s_FeatH = f.renderH; s_FeatOutW = f.outW; s_FeatOutH = f.outH;
        s_FeatPreset = f.preset; s_FeatQuality = f.quality;
        InterlockedExchange(&s_State, 2);
        SetStatus("Ray Reconstruction feature %dx%d -> %dx%d (preset %d, quality %d; created in %.0f ms on the %s thread)",
                  f.renderW, f.renderH, f.outW, f.outH, f.preset, f.quality, NowMs() - t0, where);
        return true;
    }

    NVSDK_NGX_Result Evaluate(ID3D12GraphicsCommandList* cl, const DlssFrame& f);

    ID3D12Resource* MakeTexture(int w, int h, DXGI_FORMAT fmt, bool uav, D3D12_RESOURCE_STATES state)
    {
        D3D12_HEAP_PROPERTIES hp = {};
        hp.Type = D3D12_HEAP_TYPE_DEFAULT;
        D3D12_RESOURCE_DESC d = {};
        d.Dimension = D3D12_RESOURCE_DIMENSION_TEXTURE2D;
        d.Width = (UINT64)w;
        d.Height = (UINT)h;
        d.DepthOrArraySize = 1;
        d.MipLevels = 1;
        d.Format = fmt;
        d.SampleDesc.Count = 1;
        d.Flags = uav ? D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS : D3D12_RESOURCE_FLAG_NONE;
        ID3D12Resource* r = nullptr;
        // Committed resources are zero-filled: the throw-away evaluate below sees black, not garbage.
        if (FAILED(s_Device->CreateCommittedResource(&hp, D3D12_HEAP_FLAG_NONE, &d, state, nullptr, IID_PPV_ARGS(&r)))) return nullptr;
        return r;
    }

    // ---- warm-up worker: NGX init and (optionally) the feature, with its creation commands on a queue of our own.
    // NGX does more lazy work on a feature's first evaluate (130-190 ms measured, on the render thread it was a hitch), so the
    // worker also runs one evaluate on zero-filled inputs of the right sizes. The first real frame resets the history.
    DWORD WINAPI WarmWorker(void*)
    {
        EnterCriticalSection(&s_NgxCs);
        double t0 = NowMs();
        bool ok = s_NgxInit || (!s_NgxFailed && InitNgx());
        const WarmRequest w = s_Warm;
        if (ok && w.renderW > 0 && !FeatureMatches(w.renderW, w.renderH, w.outW, w.outH, w.preset, w.quality))
        {
            ID3D12CommandQueue* q = nullptr;
            ID3D12CommandAllocator* alloc = nullptr;
            ID3D12GraphicsCommandList* list = nullptr;
            ID3D12Fence* fence = nullptr;
            HANDLE ev = CreateEventW(nullptr, FALSE, FALSE, nullptr);
            D3D12_COMMAND_QUEUE_DESC qd = {};
            qd.Type = D3D12_COMMAND_LIST_TYPE_DIRECT;
            bool made = ev && SUCCEEDED(s_Device->CreateCommandQueue(&qd, IID_PPV_ARGS(&q))) &&
                        SUCCEEDED(s_Device->CreateCommandAllocator(D3D12_COMMAND_LIST_TYPE_DIRECT, IID_PPV_ARGS(&alloc))) &&
                        SUCCEEDED(s_Device->CreateCommandList(0, D3D12_COMMAND_LIST_TYPE_DIRECT, alloc, nullptr, IID_PPV_ARGS(&list))) &&
                        SUCCEEDED(s_Device->CreateFence(0, D3D12_FENCE_FLAG_NONE, IID_PPV_ARGS(&fence)));
            if (made)
            {
                q->SetName(L"UltraRT DLSS warm-up");
                DlssFrame f = {};
                f.renderW = w.renderW; f.renderH = w.renderH; f.outW = w.outW; f.outH = w.outH;
                f.preset = w.preset; f.quality = w.quality;
                bool created = EnsureFeature(list, f, "warm-up");
                ID3D12Resource* tex[8] = {};
                double tEval = NowMs();
                if (created)
                {
                    const D3D12_RESOURCE_STATES in = D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE;
                    tex[0] = MakeTexture(f.renderW, f.renderH, DXGI_FORMAT_R16G16B16A16_FLOAT, false, in); // color
                    tex[1] = MakeTexture(f.renderW, f.renderH, DXGI_FORMAT_R8G8B8A8_UNORM, false, in);     // diffuse
                    tex[2] = MakeTexture(f.renderW, f.renderH, DXGI_FORMAT_R8G8B8A8_UNORM, false, in);     // specular
                    tex[3] = MakeTexture(f.renderW, f.renderH, DXGI_FORMAT_R16G16B16A16_FLOAT, false, in); // normals
                    tex[4] = MakeTexture(f.renderW, f.renderH, DXGI_FORMAT_R32_FLOAT, false, in);          // depth
                    tex[5] = MakeTexture(f.renderW, f.renderH, DXGI_FORMAT_R16G16_FLOAT, false, in);       // motion
                    tex[6] = MakeTexture(f.renderW, f.renderH, DXGI_FORMAT_R16_FLOAT, false, in);          // spec hit
                    tex[7] = MakeTexture(f.outW, f.outH, DXGI_FORMAT_R16G16B16A16_FLOAT, true, D3D12_RESOURCE_STATE_UNORDERED_ACCESS);
                    bool all = true;
                    for (ID3D12Resource* t : tex) all = all && t;
                    if (all)
                    {
                        DlssFrame e = f;
                        e.color = tex[0]; e.diffuse = tex[1]; e.specular = tex[2]; e.normals = tex[3]; e.depth = tex[4];
                        e.motion = tex[5]; e.specHit = tex[6]; e.output = tex[7];
                        e.reset = 1; e.flags = 1; e.mvScaleX = e.mvScaleY = 1.0f; e.frameTimeMs = 8.0f;
                        for (int k = 0; k < 16; k++) e.worldToView[k] = e.viewToClip[k] = (k % 5 == 0) ? 1.0f : 0.0f;
                        NVSDK_NGX_Result r = Evaluate(list, e);
                        if (NVSDK_NGX_FAILED(r)) { char b[160]; snprintf(b, sizeof b, "warm-up: evaluate failed: %s", ResultText(r)); AppendLog(b); }
                    }
                    else AppendLog("warm-up: could not create the evaluate's textures; skipped");
                }
                list->Close();
                ID3D12CommandList* lists[] = { list };
                q->ExecuteCommandLists(1, lists);
                q->Signal(fence, 1);
                // The feature's first use (on Unity's queue) must see its initialisation done.
                if (fence->GetCompletedValue() < 1 && SUCCEEDED(fence->SetEventOnCompletion(1, ev))) WaitForSingleObject(ev, 10000);
                for (ID3D12Resource* t : tex) if (t) t->Release();
                if (!created) AppendLog("warm-up: feature creation failed (it is retried on the render thread)");
                else { char b[128]; snprintf(b, sizeof b, "warm-up: first evaluate %.0f ms (CPU + GPU, own queue)", NowMs() - tEval); AppendLog(b); }
            }
            else AppendLog("warm-up: could not create a command queue; the feature is created on the render thread");
            if (fence) fence->Release();
            if (list) list->Release();
            if (alloc) alloc->Release();
            if (q) q->Release();
            if (ev) CloseHandle(ev);
        }
        char buf[160];
        snprintf(buf, sizeof buf, "warm-up done in %.0f ms (NGX %s, feature %s)", NowMs() - t0, s_NgxInit ? "ready" : "failed",
                 s_Feature ? "ready" : "none");
        AppendLog(buf);
        InterlockedExchange(&s_Busy, 0);
        LeaveCriticalSection(&s_NgxCs);
        return 0;
    }

    NVSDK_NGX_Result Evaluate(ID3D12GraphicsCommandList* cl, const DlssFrame& f)
    {
        NVSDK_NGX_D3D12_DLSSD_Eval_Params ep = {};
        ep.pInColor = f.color;
        ep.pInOutput = f.output;
        ep.pInDepth = f.depth;
        ep.pInMotionVectors = f.motion;
        ep.pInDiffuseAlbedo = f.diffuse;
        ep.pInSpecularAlbedo = f.specular;
        ep.pInNormals = f.normals;
        ep.pInRoughness = nullptr; // packed in normals.w
        ep.InJitterOffsetX = f.jitterX;
        ep.InJitterOffsetY = f.jitterY;
        ep.InMVScaleX = f.mvScaleX;
        ep.InMVScaleY = f.mvScaleY;
        ep.InReset = f.reset;
        ep.InRenderSubrectDimensions.Width = (unsigned)f.renderW;
        ep.InRenderSubrectDimensions.Height = (unsigned)f.renderH;
        ep.InFrameTimeDeltaInMsec = f.frameTimeMs;
        ep.InPreExposure = 1.0f;
        ep.InExposureScale = 1.0f;
        float w2v[16], v2c[16];
        memcpy(w2v, f.worldToView, sizeof w2v);
        memcpy(v2c, f.viewToClip, sizeof v2c);
        ep.pInWorldToViewMatrix = w2v;
        ep.pInViewToClipMatrix = v2c;
        if (f.flags & 1) ep.pInSpecularHitDistance = f.specHit;
        return NGX_D3D12_EVALUATE_DLSSD_EXT(cl, s_Feature, s_Params, &ep);
    }

    int CollectInputs(const DlssFrame& f, ID3D12Resource** out)
    {
        int n = 0;
        ID3D12Resource* in[] = { f.color, f.diffuse, f.specular, f.normals, f.depth, f.motion, (f.flags & 1) ? f.specHit : nullptr };
        for (ID3D12Resource* r : in) if (r) out[n++] = r;
        return n;
    }

    void LogStates(const DlssFrame& f)
    {
        if (!(f.flags & 2) || s_StateLogs >= 3 || !s_V1) return;
        s_StateLogs++;
        const char* names[] = { "color", "diffuse", "specular", "normals", "depth", "motion", "specHit", "output" };
        ID3D12Resource* rs[] = { f.color, f.diffuse, f.specular, f.normals, f.depth, f.motion, f.specHit, f.output };
        char buf[512];
        int len = snprintf(buf, sizeof buf, "Unity-tracked states before evaluate:");
        for (int i = 0; i < 8; i++)
        {
            D3D12_RESOURCE_STATES st = (D3D12_RESOURCE_STATES)0;
            bool ok = rs[i] && s_V1->GetResourceState(rs[i], &st);
            len += snprintf(buf + len, sizeof buf - len, " %s=%s0x%X", names[i], ok ? "" : "?", (unsigned)st);
        }
        AppendLog(buf);
    }

    void Barriers(ID3D12GraphicsCommandList* cl, ID3D12Resource** rs, int n, D3D12_RESOURCE_STATES from, D3D12_RESOURCE_STATES to)
    {
        D3D12_RESOURCE_BARRIER b[8] = {};
        for (int i = 0; i < n; i++)
        {
            b[i].Type = D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;
            b[i].Transition.pResource = rs[i];
            b[i].Transition.Subresource = D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES;
            b[i].Transition.StateBefore = from;
            b[i].Transition.StateAfter = to;
        }
        if (n > 0) cl->ResourceBarrier((UINT)n, b);
    }

    bool Ready(const DlssFrame& f)
    {
        if (!s_NgxInit)
        {
            if (s_NgxFailed || !InitNgx()) return false;
        }
        if (!f.color || !f.output || !f.depth || !f.motion || !f.diffuse || !f.specular || !f.normals)
        {
            static bool warned = false;
            if (!warned) { warned = true; SetStatus("evaluate skipped: a required resource is null"); }
            return false;
        }
        return true;
    }

    void EvalUnityList(const DlssFrame& f)
    {
        if (!Ready(f)) return;
        UnityGraphicsD3D12RecordingState rs = {};
        if (!s_Api.CommandRecordingState(&rs) || !rs.commandList)
        {
            static bool warned = false;
            if (!warned) { warned = true; SetStatus("CommandRecordingState returned no command list"); }
            return;
        }
        ID3D12GraphicsCommandList* cl = rs.commandList;
        LogStates(f);
        if (!EnsureFeature(cl, f)) return;
        ID3D12Resource* in[8];
        int n = CollectInputs(f, in);
        Barriers(cl, in, n, D3D12_RESOURCE_STATE_UNORDERED_ACCESS, D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE);
        D3D12_RESOURCE_BARRIER uav = {};
        uav.Type = D3D12_RESOURCE_BARRIER_TYPE_UAV;
        uav.UAV.pResource = f.output;
        cl->ResourceBarrier(1, &uav);
        NVSDK_NGX_Result r = Evaluate(cl, f);
        Barriers(cl, in, n, D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE, D3D12_RESOURCE_STATE_UNORDERED_ACCESS);
        cl->ResourceBarrier(1, &uav);
        s_LastFence = s_Api.GetNextFrameFenceValue();
        if (NVSDK_NGX_FAILED(r)) { static int fails = 0; if (fails++ < 5) SetStatus("evaluate (Unity list) failed: %s", ResultText(r)); }
        else InterlockedIncrement64(&s_Evals);
    }

    void EvalOwnList(const DlssFrame& f)
    {
        if (!Ready(f) || !EnsureOwnList()) return;
        ID3D12Fence* fence = s_Api.GetFrameFence();
        int i = s_AllocCur;
        if (fence && fence->GetCompletedValue() < s_AllocFence[i] && !WaitFence(s_AllocFence[i], 1000))
        {
            static int warned = 0;
            if (warned++ < 5) AppendLog("evaluate skipped: command allocator still in flight");
            return;
        }
        s_AllocCur = (s_AllocCur + 1) % kAllocRing;
        s_Alloc[i]->Reset();
        s_List->Reset(s_Alloc[i], nullptr);
        LogStates(f);
        bool ok = EnsureFeature(s_List, f);
        NVSDK_NGX_Result r = NVSDK_NGX_Result_Fail;
        if (ok) r = Evaluate(s_List, f);
        s_List->Close();
        UnityGraphicsD3D12ResourceState st[8] = {};
        ID3D12Resource* in[8];
        int n = CollectInputs(f, in);
        for (int k = 0; k < n; k++) st[k] = { in[k], D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE, D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE };
        st[n] = { f.output, D3D12_RESOURCE_STATE_UNORDERED_ACCESS, D3D12_RESOURCE_STATE_UNORDERED_ACCESS };
        UINT64 v = s_Api.ExecuteCommandList(s_List, n + 1, st);
        s_AllocFence[i] = v;
        s_LastFence = v;
        if (!ok) return;
        if (NVSDK_NGX_FAILED(r)) { static int fails = 0; if (fails++ < 5) SetStatus("evaluate (own list) failed: %s", ResultText(r)); }
        else InterlockedIncrement64(&s_Evals);
    }

    UINT32 EnvFlags(const char* name, UINT32 def)
    {
        char buf[32] = {};
        return GetEnvironmentVariableA(name, buf, sizeof buf) > 0 ? (UINT32)strtoul(buf, nullptr, 0) : def;
    }
}

// ------------------------------------------------------------------ hooks from GpuTimer.cpp

void Dlss_OnDeviceEvent(UnityGfxDeviceEventType type)
{
    if (type == kUnityGfxDeviceEventShutdown)
    {
        ShutdownAll();
        s_HaveApi = false;
        return;
    }
    if (type != kUnityGfxDeviceEventInitialize) return;
    ShutdownAll();
    s_HaveApi = false;
    IUnityInterfaces* u = Native_UnityInterfaces();
    if (!u) return;
    IUnityGraphics* g = u->Get<IUnityGraphics>();
    if (!g || g->GetRenderer() != kUnityGfxRendererD3D12) return;
    if (auto v7 = u->Get<IUnityGraphicsD3D12v7>())
    {
        s_Api = { v7->GetDevice, v7->GetFrameFence, v7->GetNextFrameFenceValue, v7->ExecuteCommandList, v7->ConfigureEvent, v7->CommandRecordingState };
        s_HaveApi = true;
    }
    else if (auto v6 = u->Get<IUnityGraphicsD3D12v6>())
    {
        s_Api = { v6->GetDevice, v6->GetFrameFence, v6->GetNextFrameFenceValue, v6->ExecuteCommandList, v6->ConfigureEvent, v6->CommandRecordingState };
        s_HaveApi = true;
    }
    s_V1 = u->Get<IUnityGraphicsD3D12>();
    if (!s_HaveApi) { SetStatus("IUnityGraphicsD3D12v6/v7 not available"); return; }

    // Event configuration must happen at device init (see docs/gpu-timer.md). Overridable for experiments.
    UnityD3D12PluginEventConfig cfg = {};
    cfg.graphicsQueueAccess = kUnityD3D12GraphicsQueueAccess_DontCare;
    cfg.flags = EnvFlags("ULTRART_DLSS_FLAGS_UNITY", kUnityD3D12EventConfigFlag_ModifiesCommandBuffersState);
    cfg.ensureActiveRenderTextureIsBound = EnvFlags("ULTRART_DLSS_ENSURE_RT", 0) != 0;
    s_Api.ConfigureEvent(kEventEvalUnity, &cfg);
    UINT32 ownFlags = cfg.flags;
    cfg.flags = EnvFlags("ULTRART_DLSS_FLAGS_OWN", kUnityD3D12EventConfigFlag_FlushCommandBuffers);
    cfg.ensureActiveRenderTextureIsBound = false;
    s_Api.ConfigureEvent(kEventEvalOwn, &cfg);
    cfg.flags = 0;
    s_Api.ConfigureEvent(kEventRelease, &cfg);
    SetStatus("D3D12 interfaces ready (v1 state query %s); event flags unity-list 0x%X, own-list 0x%X",
              s_V1 ? "available" : "unavailable", ownFlags, EnvFlags("ULTRART_DLSS_FLAGS_OWN", kUnityD3D12EventConfigFlag_FlushCommandBuffers));
}

bool Dlss_OnRenderEvent(int eventId, void* data)
{
    if (eventId < kEventBase || eventId > kEventRelease) return false;
    if (!s_HaveApi) return true;
    int slot = (int)(intptr_t)data;
    EnsureCs();
    // The warm-up worker holds the lock for seconds: never wait for it here. A release is done later; an evaluate is
    // skipped (the C# side doesn't ask for RR while the worker runs, see UltraRT_Dlss_IsBusy).
    if (!TryEnterCriticalSection(&s_NgxCs))
    {
        if (eventId == kEventRelease) InterlockedExchange(&s_ReleasePending, 1);
        return true;
    }
    if (eventId == kEventRelease || InterlockedExchange(&s_ReleasePending, 0)) ReleaseFeature();
    if (eventId != kEventRelease && slot >= 0 && slot < kFrameRing)
    {
        DlssFrame f = s_Frames[slot]; // copy: the main thread may already be writing the next frame into another slot
        if (eventId == kEventEvalUnity) EvalUnityList(f);
        else EvalOwnList(f);
    }
    LeaveCriticalSection(&s_NgxCs);
    return true;
}

void Dlss_Unload()
{
    ShutdownAll();
    s_HaveApi = false;
}

// ------------------------------------------------------------------ API for DlssRR.cs

// Main thread, before the first evaluate: where nvngx_dlssd.dll is, and a writable folder for NGX's own logs.
ULTRART_API int UltraRT_Dlss_Configure(const wchar_t* dllDir, const wchar_t* dataDir)
{
    if (dllDir) wcsncpy_s(s_DllDir, dllDir, _TRUNCATE);
    if (dataDir) wcsncpy_s(s_DataDir, dataDir, _TRUNCATE);
    EnsureCs();
    return s_HaveApi ? 1 : 0;
}

// Main thread: initialise NGX and, with renderW > 0, create the feature for these sizes, on a worker thread (seconds the
// first time; docs/dlss.md, "Warm-up"). Returns 1 if a warm-up is running (started now or before), 0 if there is nothing
// to do (already done, NGX failed, no D3D12 interface).
ULTRART_API int UltraRT_Dlss_Warm(int renderW, int renderH, int outW, int outH, int preset, int quality)
{
    if (!s_HaveApi || s_NgxFailed) return 0;
    EnsureCs();
    if (InterlockedCompareExchange(&s_Busy, 0, 0)) return 1;
    // Nothing to do? (Read without the lock: only the render thread and the worker change these, and the worker isn't
    // running; a render thread change in between only costs a redundant warm-up.)
    if (s_NgxInit && (renderW <= 0 || FeatureMatches(renderW, renderH, outW, outH, preset, quality))) return 0;
    JoinWorker(); // a finished worker's handle
    s_Warm = { renderW, renderH, outW, outH, preset, quality };
    InterlockedExchange(&s_Busy, 1);
    s_Worker = CreateThread(nullptr, 0, WarmWorker, nullptr, 0, nullptr);
    if (!s_Worker) { InterlockedExchange(&s_Busy, 0); return 0; }
    return 1;
}

ULTRART_API int UltraRT_Dlss_IsBusy() { return (int)InterlockedCompareExchange(&s_Busy, 0, 0); }

// 1 if the feature exists for exactly these sizes (and no warm-up is running).
ULTRART_API int UltraRT_Dlss_FeatureReady(int renderW, int renderH, int outW, int outH, int preset, int quality)
{
    if (InterlockedCompareExchange(&s_Busy, 0, 0)) return 0;
    return FeatureMatches(renderW, renderH, outW, outH, preset, quality) ? 1 : 0;
}

ULTRART_API int UltraRT_Dlss_GetEventBase() { return kEventBase; }
ULTRART_API int UltraRT_Dlss_GetRingSize() { return kFrameRing; }
ULTRART_API int UltraRT_Dlss_GetFrameSize() { return (int)sizeof(DlssFrame); }

ULTRART_API void UltraRT_Dlss_SetFrame(int slot, const DlssFrame* f)
{
    if (slot < 0 || slot >= kFrameRing || !f) return;
    s_Frames[slot] = *f;
}

ULTRART_API int UltraRT_Dlss_GetState() { return (int)InterlockedCompareExchange(&s_State, 0, 0); }
ULTRART_API long long UltraRT_Dlss_GetEvalCount() { return InterlockedCompareExchange64(&s_Evals, 0, 0); }

ULTRART_API const char* UltraRT_Dlss_GetStatus()
{
    AcquireSRWLockShared(&s_Lock);
    memcpy(s_StatusOut, s_Status, sizeof s_Status);
    ReleaseSRWLockShared(&s_Lock);
    return s_StatusOut;
}

// Copies and clears the accumulated log lines (NGX's own log callback and ours). Returns the length copied.
ULTRART_API int UltraRT_Dlss_TakeLog(char* out, int size)
{
    if (!out || size <= 0) return 0;
    AcquireSRWLockExclusive(&s_Lock);
    int n = s_LogLen < size - 1 ? s_LogLen : size - 1;
    memcpy(out, s_Log, n);
    out[n] = 0;
    s_LogLen = 0;
    s_Log[0] = 0;
    ReleaseSRWLockExclusive(&s_Lock);
    return n;
}
