// UltraRT.Native: HDR10 display output through a swapchain proxy. See docs/hdr.md.
//
// ULTRAKILL is built without HDR display support, so Unity only ever makes an 8-bit SDR swapchain. This file gives the
// display a real HDR10 signal without Unity knowing:
//   UltraRT_Hdr_Install()   called by the BepInEx preloader patcher (src/UltraRT.Preload), before Unity creates its
//                           graphics device. Patches IDXGIFactory::CreateSwapChain / CreateSwapChainForHwnd (vtable slots).
//   the hook                creates the real swapchain as R10G10B10A2 with the HDR10 colour space (PQ, Rec.2020) and hands
//                           Unity a ProxySwapChain. Only when the window's display has HDR on; otherwise Unity gets its
//                           plain swapchain and nothing else in this file runs.
//   ProxySwapChain          GetBuffer returns ordinary 8-bit textures ("fake back buffers") that Unity renders into as
//                           before. Present runs one fullscreen pass on Unity's command queue: fake buffer (sRGB) -> linear
//                           -> x paper white -> x the HDR gain map of the frame -> PQ, into the real back buffer.
//   the gain map            a float texture the plugin's composite writes (rgb = HDR / SDR per channel, a = the SDR luma it
//                           expected there, packed; a < 0: a pixel the game drew, r = factor for its bright part), announced per frame with a plugin event so the Present of that frame uses it.
//   the late map            the same layout, written by the screen-effects pass after the game's transparents (flags bit 1):
//                           effects (explosions, fire, beams) and bloom, which the game draws after the composite. The pass
//                           takes the larger of the two gains per pixel.
//
// Threads: the hook and the proxy's methods run on Unity's render thread; UltraRT_Hdr_* is called from the main thread.

#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <d3d12.h>
#include <dxgi1_6.h>
#include <d3dcompiler.h>
#include <math.h>
#include <stdarg.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include "IUnityInterface.h"
#include "IUnityGraphics.h"
#include "IUnityGraphicsD3D12.h"
#include "Native.h"

#define ULTRART_API extern "C" __declspec(dllexport)

namespace
{
    constexpr int kEventHdrFrame = 97;  // GpuTimer 91-93, DLSS 94-96
    constexpr int kMaxBuffers = 8;
    constexpr int kSlots = 8;           // command allocators / descriptor pairs in flight
    constexpr int kFrameRing = 16;      // two maps per frame (composite, late)
    constexpr DXGI_FORMAT kHdrFormat = DXGI_FORMAT_R10G10B10A2_UNORM;
    constexpr DXGI_COLOR_SPACE_TYPE kHdrSpace = DXGI_COLOR_SPACE_RGB_FULL_G2084_NONE_P2020;

    // Must match Plugin.Hdr.cs (sequential layout).
    struct HdrFrame
    {
        ID3D12Resource* gain;     // RGBA16F, Unity-tracked state UNORDERED_ACCESS (a compute kernel just wrote it)
        int width, height;
        int flags;                // bit 0: sample with a linear filter (not with the packed alpha the plugin writes: unused)
                                  // bit 1: the late map (screen effects, after the game's transparents)
        int pad;
        float uvScaleX, uvScaleY, uvOffsetX, uvOffsetY; // screen uv (0,0 = top left) -> gain uv
    };

    struct HdrParams
    {
        float paperNits;   // SDR white; 0 = what Windows shows SDR content at on this display
        float peakNits;    // 0 = the display's reported maximum
        float gamma22;     // 1 = decode the SDR image with gamma 2.2, 0 = piecewise sRGB
        float tolLo, tolHi; // gain fades out where the frame is darker than the composite's luma by this much (gamma units)
        float sdrBoost;    // frames without a gain map: highlight expansion (0 = none)
        float debug;       // 1 = tint the pixels that got gain
        float gainMax;
        float gainStrength; // 0..1
        float pad0, pad1, pad2;
    };

    // ---- log / status (any thread)
    SRWLOCK s_LogLock = SRWLOCK_INIT;
    char s_Log[32768];
    int s_LogLen = 0;
    char s_Status[512] = "not installed";
    char s_StatusOut[512];
    wchar_t s_LogPath[MAX_PATH] = L"";
    volatile LONG s_State = 0;      // 0 not installed, 1 hooks installed, 2 HDR10 active, -1 installed but passing through
    volatile LONG64 s_Presents = 0;
    volatile LONG64 s_GainFrames = 0;
    float s_DisplayMaxNits = 0.0f;
    float s_SdrWhiteNits = 0.0f;    // Windows' "SDR content brightness" for the game's display (0 = unknown)
    int s_Width = 0, s_Height = 0;

    void Log(const char* fmt, ...)
    {
        char buf[1024];
        va_list args;
        va_start(args, fmt);
        vsnprintf(buf, sizeof buf, fmt, args);
        va_end(args);
        AcquireSRWLockExclusive(&s_LogLock);
        int n = (int)strlen(buf);
        if (s_LogLen + n + 2 < (int)sizeof s_Log)
        {
            memcpy(s_Log + s_LogLen, buf, n);
            s_LogLen += n;
            s_Log[s_LogLen++] = '\n';
            s_Log[s_LogLen] = 0;
        }
        memcpy(s_Status, buf, n < (int)sizeof s_Status - 1 ? n + 1 : sizeof s_Status - 1);
        s_Status[sizeof s_Status - 1] = 0;
        if (s_LogPath[0])
        {
            HANDLE h = CreateFileW(s_LogPath, FILE_APPEND_DATA, FILE_SHARE_READ | FILE_SHARE_WRITE, nullptr, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
            if (h != INVALID_HANDLE_VALUE)
            {
                DWORD w = 0;
                WriteFile(h, buf, (DWORD)n, &w, nullptr);
                WriteFile(h, "\r\n", 2, &w, nullptr);
                CloseHandle(h);
            }
        }
        ReleaseSRWLockExclusive(&s_LogLock);
        OutputDebugStringA("[UltraRT.Hdr] ");
        OutputDebugStringA(buf);
        OutputDebugStringA("\n");
    }

    // ---- shared with the plugin
    SRWLOCK s_Lock = SRWLOCK_INIT;
    HdrParams s_Params = { 0.0f, 0.0f, 1.0f, 0.06f, 0.25f, 0.0f, 0.0f, 16.0f, 1.0f, 0, 0, 0 };
    HdrFrame s_Frames[kFrameRing] = {};   // each holds a reference on its gain texture
    HdrFrame s_Present = {};              // the frame the next Present uses (holds a reference), set by the plugin event
    bool s_PresentValid = false;
    HdrFrame s_PresentLate = {};          // the same for the late map
    bool s_PresentLateValid = false;
    volatile LONG64 s_LateFrames = 0;
    wchar_t s_CapturePath[MAX_PATH] = L"";
    bool s_CaptureWanted = false;
    bool s_Force = false;

    const char kShader[] = R"(
cbuffer C : register(b0)
{
    float paperNits, peakNits, gainOn, gammaMode;
    float4 gainUV;
    float tolLo, tolHi, sdrBoost, debugMode;
    float gainMax, linearGain, gainStrength, lateOn;
    float4 lateUV;
};
Texture2D<float4> Src : register(t0);
Texture2D<float4> Gain : register(t1);
Texture2D<float4> Late : register(t2);
SamplerState PointClamp : register(s0);
SamplerState LinearClamp : register(s1);
struct VO { float4 pos : SV_Position; float2 uv : TEXCOORD0; };
VO VS(uint id : SV_VertexID)
{
    VO o;
    o.uv = float2((id << 1) & 2, id & 2);
    o.pos = float4(o.uv * float2(2.0, -2.0) + float2(-1.0, 1.0), 0.0, 1.0);
    return o;
}
float3 Decode(float3 c)
{
    if (gammaMode > 0.5) return pow(c, 2.2);
    return lerp(c / 12.92, pow((c + 0.055) / 1.055, 2.4), step(0.04045, c));
}
float3 PQ(float3 nits)
{
    float3 y = pow(saturate(nits / 10000.0), 0.1593017578125);
    return pow((0.8359375 + 18.8515625 * y) / (1.0 + 18.6875 * y), 78.84375);
}
// The gain belongs to the colour the writer expected here (packed in a: 6 bits luma, 5 bits r - b). Where the frame is
// darker or another hue, something was drawn over it (HUD, the gun, smoke): that stays SDR.
float Match(float a, float3 s, float tolScale)
{
    float pa = abs(a) - (a < 0.0 ? 1.0 : 0.0);
    float pl = floor(pa / 32.0);
    float eL = pl / 63.0, eC = (pa - pl * 32.0) / 31.0 * 2.0 - 1.0;
    float l = dot(s, float3(0.2126, 0.7152, 0.0722));
    float w = (1.0 - smoothstep(tolLo * tolScale, tolHi * tolScale, eL - l)) * (1.0 - smoothstep(0.15, 0.4, abs(eC - (s.r - s.b))));
    return saturate(w * gainStrength);
}
// Fixed factors (the game's own pixels: lava, sky; the late map: effects) know nothing of the display, so they are
// rolled off towards the peak instead of clipping there: the brightest channel is untouched up to halfway between
// paper white and the peak, then approaches the peak, so a lava crust or a fireball keeps its gradation. (The
// composite's gain already rolls off to the peak with its own curve and is left as it was.)
float3 SoftPeak(float3 r, float top)
{
    float v = max(r.r, max(r.g, r.b));
    float k = 1.0 + 0.5 * (top - 1.0);
    if (top <= 1.001 || v <= k) return r;
    return r * ((k + (top - k) * (1.0 - exp(-(v - k) / (top - k)))) / v);
}
float4 PS(VO i) : SV_Target
{
    float3 s = saturate(Src.Load(int3(i.pos.xy, 0)).rgb);
    float3 rel = Decode(s); // 1 = paper white
    float3 base = rel;
    float top = peakNits / paperNits;
    bool mapped = false;
    if (gainOn > 0.5)
    {
        float2 uv = i.uv * gainUV.xy + gainUV.zw;
        float4 g = linearGain > 0.5 ? Gain.SampleLevel(LinearClamp, uv, 0) : Gain.SampleLevel(PointClamp, uv, 0);
        float w = Match(g.a, s, 1.0);
        if (g.a < 0.0)
        {
            // A pixel the game drew itself (sky; lava, which the camera's rays skip): g.r scales its bright part.
            float t = smoothstep(0.7, 1.0, max(s.r, max(s.g, s.b)));
            rel = SoftPeak(base * (1.0 + (clamp(g.r, 1.0, gainMax) - 1.0) * t * t * w), top);
        }
        else rel = base * lerp(1.0, clamp(g.rgb, 1.0, gainMax), w);
        mapped = true;
    }
    if (lateOn > 0.5)
    {
        // Screen effects (explosions, fire, beams, bloom; the gun's glowing parts): drawn after the composite, so the
        // brighter of the two wins.
        float4 g = Late.SampleLevel(PointClamp, i.uv * lateUV.xy + lateUV.zw, 0);
        // Half the darkening tolerance: the late map covers large areas (bloom, lava under a fire), and the game's translucent
        // HUD panel over them must not pass. Nothing but the HUD, menus and the viewmodel's transparents is drawn after it.
        rel = max(rel, SoftPeak(base * lerp(1.0, clamp(g.rgb, 1.0, gainMax), Match(g.a, s, 0.5)), top));
        mapped = true;
    }
    float applied = 1.0;
    if (mapped) applied = max(rel.r, max(rel.g, rel.b)) / max(max(base.r, max(base.g, base.b)), 1e-6);
    else if (sdrBoost > 0.0)
    {
        float t = smoothstep(0.7, 1.0, max(s.r, max(s.g, s.b)));
        rel *= 1.0 + sdrBoost * t * t;
    }
    float3 nits = min(rel * paperNits, peakNits);
    if (debugMode > 0.5 && applied > 1.02) nits = lerp(nits, float3(paperNits, 0.0, paperNits), 0.6);
    const float3x3 to2020 = { 0.627404, 0.329283, 0.043313, 0.069097, 0.919540, 0.011362, 0.016391, 0.088013, 0.895595 };
    return float4(PQ(mul(to2020, nits)), 1.0);
}
)";

    struct Constants
    {
        float paperNits, peakNits, gainOn, gammaMode;
        float uvScaleX, uvScaleY, uvOffsetX, uvOffsetY;
        float tolLo, tolHi, sdrBoost, debugMode;
        float gainMax, linearGain, gainStrength, lateOn;
        float lateScaleX, lateScaleY, lateOffsetX, lateOffsetY;
    };

    double PqToNits(double v)
    {
        const double m1 = 0.1593017578125, m2 = 78.84375, c1 = 0.8359375, c2 = 18.8515625, c3 = 18.6875;
        double p = pow(v, 1.0 / m2);
        double num = p - c1;
        if (num < 0) num = 0;
        return 10000.0 * pow(num / (c2 - c3 * p), 1.0 / m1);
    }

    class ProxySwapChain final : public IDXGISwapChain4
    {
    public:
        static ProxySwapChain* Create(IUnknown* real, ID3D12CommandQueue* queue, DXGI_FORMAT unityFormat, float displayMaxNits)
        {
            ProxySwapChain* p = new ProxySwapChain();
            p->m_unityFormat = unityFormat;
            p->m_displayMaxNits = displayMaxNits;
            p->m_queue = queue;
            queue->AddRef();
            if (FAILED(real->QueryInterface(IID_PPV_ARGS(&p->m_real)))) { Log("the swapchain has no IDXGISwapChain3"); delete p; return nullptr; }
            real->QueryInterface(IID_PPV_ARGS(&p->m_real4));
            if (!p->Init()) { delete p; return nullptr; }
            return p;
        }

        // ---- IUnknown
        HRESULT STDMETHODCALLTYPE QueryInterface(REFIID riid, void** ppv) override
        {
            if (!ppv) return E_POINTER;
            if (riid == __uuidof(IUnknown) || riid == __uuidof(IDXGIObject) || riid == __uuidof(IDXGIDeviceSubObject) ||
                riid == __uuidof(IDXGISwapChain) || riid == __uuidof(IDXGISwapChain1) || riid == __uuidof(IDXGISwapChain2) ||
                riid == __uuidof(IDXGISwapChain3) || (riid == __uuidof(IDXGISwapChain4) && m_real4))
            {
                *ppv = static_cast<IDXGISwapChain4*>(this);
                AddRef();
                return S_OK;
            }
            *ppv = nullptr;
            if (m_qiLogs++ < 8)
                Log("proxy: QueryInterface for an interface it does not have: {%08lX-%04X-%04X}", riid.Data1, riid.Data2, riid.Data3);
            return E_NOINTERFACE;
        }
        ULONG STDMETHODCALLTYPE AddRef() override { return (ULONG)InterlockedIncrement(&m_ref); }
        ULONG STDMETHODCALLTYPE Release() override
        {
            LONG r = InterlockedDecrement(&m_ref);
            if (r == 0) { Log("proxy swapchain released"); delete this; }
            return (ULONG)r;
        }

        // ---- IDXGIObject / IDXGIDeviceSubObject
        HRESULT STDMETHODCALLTYPE SetPrivateData(REFGUID g, UINT n, const void* d) override { return m_real->SetPrivateData(g, n, d); }
        HRESULT STDMETHODCALLTYPE SetPrivateDataInterface(REFGUID g, const IUnknown* u) override { return m_real->SetPrivateDataInterface(g, u); }
        HRESULT STDMETHODCALLTYPE GetPrivateData(REFGUID g, UINT* n, void* d) override { return m_real->GetPrivateData(g, n, d); }
        HRESULT STDMETHODCALLTYPE GetParent(REFIID riid, void** pp) override { return m_real->GetParent(riid, pp); }
        HRESULT STDMETHODCALLTYPE GetDevice(REFIID riid, void** pp) override { return m_real->GetDevice(riid, pp); }

        // ---- IDXGISwapChain
        HRESULT STDMETHODCALLTYPE Present(UINT sync, UINT flags) override
        {
            if (!(flags & DXGI_PRESENT_TEST)) Compose();
            return m_real->Present(sync, flags);
        }
        HRESULT STDMETHODCALLTYPE GetBuffer(UINT i, REFIID riid, void** pp) override
        {
            if (!pp) return E_POINTER;
            if (i >= m_count || !m_fake[i]) { *pp = nullptr; return DXGI_ERROR_INVALID_CALL; }
            return m_fake[i]->QueryInterface(riid, pp);
        }
        HRESULT STDMETHODCALLTYPE SetFullscreenState(BOOL fs, IDXGIOutput* o) override
        {
            HRESULT hr = m_real->SetFullscreenState(fs, o);
            Log("proxy: SetFullscreenState(%d) -> 0x%08X", (int)fs, (unsigned)hr);
            return hr;
        }
        HRESULT STDMETHODCALLTYPE GetFullscreenState(BOOL* fs, IDXGIOutput** o) override { return m_real->GetFullscreenState(fs, o); }
        HRESULT STDMETHODCALLTYPE GetDesc(DXGI_SWAP_CHAIN_DESC* d) override
        {
            HRESULT hr = m_real->GetDesc(d);
            if (SUCCEEDED(hr) && d) d->BufferDesc.Format = m_unityFormat;
            return hr;
        }
        HRESULT STDMETHODCALLTYPE ResizeBuffers(UINT count, UINT w, UINT h, DXGI_FORMAT f, UINT flags) override
        {
            return Resize(count, w, h, f, flags, nullptr, nullptr);
        }
        HRESULT STDMETHODCALLTYPE ResizeTarget(const DXGI_MODE_DESC* d) override
        {
            if (!d) return m_real->ResizeTarget(d);
            DXGI_MODE_DESC m = *d;
            if (m.Format == m_unityFormat) m.Format = kHdrFormat;
            return m_real->ResizeTarget(&m);
        }
        HRESULT STDMETHODCALLTYPE GetContainingOutput(IDXGIOutput** o) override { return m_real->GetContainingOutput(o); }
        HRESULT STDMETHODCALLTYPE GetFrameStatistics(DXGI_FRAME_STATISTICS* s) override { return m_real->GetFrameStatistics(s); }
        HRESULT STDMETHODCALLTYPE GetLastPresentCount(UINT* c) override { return m_real->GetLastPresentCount(c); }

        // ---- IDXGISwapChain1
        HRESULT STDMETHODCALLTYPE GetDesc1(DXGI_SWAP_CHAIN_DESC1* d) override
        {
            HRESULT hr = m_real->GetDesc1(d);
            if (SUCCEEDED(hr) && d) d->Format = m_unityFormat;
            return hr;
        }
        HRESULT STDMETHODCALLTYPE GetFullscreenDesc(DXGI_SWAP_CHAIN_FULLSCREEN_DESC* d) override { return m_real->GetFullscreenDesc(d); }
        HRESULT STDMETHODCALLTYPE GetHwnd(HWND* h) override { return m_real->GetHwnd(h); }
        HRESULT STDMETHODCALLTYPE GetCoreWindow(REFIID riid, void** pp) override { return m_real->GetCoreWindow(riid, pp); }
        HRESULT STDMETHODCALLTYPE Present1(UINT sync, UINT flags, const DXGI_PRESENT_PARAMETERS* p) override
        {
            if (!(flags & DXGI_PRESENT_TEST)) Compose();
            return m_real->Present1(sync, flags, p);
        }
        BOOL STDMETHODCALLTYPE IsTemporaryMonoSupported() override { return m_real->IsTemporaryMonoSupported(); }
        HRESULT STDMETHODCALLTYPE GetRestrictToOutput(IDXGIOutput** o) override { return m_real->GetRestrictToOutput(o); }
        HRESULT STDMETHODCALLTYPE SetBackgroundColor(const DXGI_RGBA* c) override { return m_real->SetBackgroundColor(c); }
        HRESULT STDMETHODCALLTYPE GetBackgroundColor(DXGI_RGBA* c) override { return m_real->GetBackgroundColor(c); }
        HRESULT STDMETHODCALLTYPE SetRotation(DXGI_MODE_ROTATION r) override { return m_real->SetRotation(r); }
        HRESULT STDMETHODCALLTYPE GetRotation(DXGI_MODE_ROTATION* r) override { return m_real->GetRotation(r); }

        // ---- IDXGISwapChain2
        HRESULT STDMETHODCALLTYPE SetSourceSize(UINT w, UINT h) override { return m_real->SetSourceSize(w, h); }
        HRESULT STDMETHODCALLTYPE GetSourceSize(UINT* w, UINT* h) override { return m_real->GetSourceSize(w, h); }
        HRESULT STDMETHODCALLTYPE SetMaximumFrameLatency(UINT n) override { return m_real->SetMaximumFrameLatency(n); }
        HRESULT STDMETHODCALLTYPE GetMaximumFrameLatency(UINT* n) override { return m_real->GetMaximumFrameLatency(n); }
        HANDLE STDMETHODCALLTYPE GetFrameLatencyWaitableObject() override { return m_real->GetFrameLatencyWaitableObject(); }
        HRESULT STDMETHODCALLTYPE SetMatrixTransform(const DXGI_MATRIX_3X2_F* m) override { return m_real->SetMatrixTransform(m); }
        HRESULT STDMETHODCALLTYPE GetMatrixTransform(DXGI_MATRIX_3X2_F* m) override { return m_real->GetMatrixTransform(m); }

        // ---- IDXGISwapChain3
        UINT STDMETHODCALLTYPE GetCurrentBackBufferIndex() override { return m_real->GetCurrentBackBufferIndex(); }
        HRESULT STDMETHODCALLTYPE CheckColorSpaceSupport(DXGI_COLOR_SPACE_TYPE cs, UINT* s) override { return m_real->CheckColorSpaceSupport(cs, s); }
        HRESULT STDMETHODCALLTYPE SetColorSpace1(DXGI_COLOR_SPACE_TYPE cs) override
        {
            Log("proxy: Unity asked for colour space %d; kept HDR10", (int)cs); // the real chain stays PQ / Rec.2020
            return S_OK;
        }
        HRESULT STDMETHODCALLTYPE ResizeBuffers1(UINT count, UINT w, UINT h, DXGI_FORMAT f, UINT flags, const UINT* masks, IUnknown* const* queues) override
        {
            return Resize(count, w, h, f, flags, masks, queues);
        }

        // ---- IDXGISwapChain4
        HRESULT STDMETHODCALLTYPE SetHDRMetaData(DXGI_HDR_METADATA_TYPE, UINT, void*) override { return S_OK; }

    private:
        ProxySwapChain() = default;
        ~ProxySwapChain()
        {
            WaitIdle();
            ReleaseBuffers();
            for (int i = 0; i < kSlots; i++)
            {
                if (m_slotGain[i]) m_slotGain[i]->Release();
                if (m_slotLate[i]) m_slotLate[i]->Release();
                if (m_alloc[i]) m_alloc[i]->Release();
            }
            if (m_list) m_list->Release();
            if (m_pso) m_pso->Release();
            if (m_rootSig) m_rootSig->Release();
            if (m_rtvHeap) m_rtvHeap->Release();
            if (m_srvHeap) m_srvHeap->Release();
            if (m_readback) m_readback->Release();
            if (m_queryRead) m_queryRead->Release();
            if (m_queryHeap) m_queryHeap->Release();
            if (m_fence) m_fence->Release();
            if (m_event) CloseHandle(m_event);
            if (m_device) m_device->Release();
            if (m_queue) m_queue->Release();
            if (m_real4) m_real4->Release();
            if (m_real) m_real->Release();
            if (s_State == 2) InterlockedExchange(&s_State, 1);
        }

        bool Init()
        {
            HRESULT hr = m_queue->GetDevice(IID_PPV_ARGS(&m_device));
            if (FAILED(hr)) { Log("proxy: the queue has no ID3D12Device (0x%08X)", (unsigned)hr); return false; }
            UINT support = 0;
            hr = m_real->CheckColorSpaceSupport(kHdrSpace, &support);
            if (FAILED(hr) || !(support & DXGI_SWAP_CHAIN_COLOR_SPACE_SUPPORT_FLAG_PRESENT))
            { Log("proxy: the swapchain does not support the HDR10 colour space (hr 0x%08X, support %u)", (unsigned)hr, support); return false; }
            hr = m_real->SetColorSpace1(kHdrSpace);
            if (FAILED(hr)) { Log("proxy: SetColorSpace1(HDR10) failed (0x%08X)", (unsigned)hr); return false; }

            D3D12_DESCRIPTOR_HEAP_DESC hd = {};
            hd.Type = D3D12_DESCRIPTOR_HEAP_TYPE_RTV;
            hd.NumDescriptors = kMaxBuffers;
            if (FAILED(m_device->CreateDescriptorHeap(&hd, IID_PPV_ARGS(&m_rtvHeap)))) { Log("proxy: RTV heap failed"); return false; }
            hd.Type = D3D12_DESCRIPTOR_HEAP_TYPE_CBV_SRV_UAV;
            hd.NumDescriptors = kSlots * 3;
            hd.Flags = D3D12_DESCRIPTOR_HEAP_FLAG_SHADER_VISIBLE;
            if (FAILED(m_device->CreateDescriptorHeap(&hd, IID_PPV_ARGS(&m_srvHeap)))) { Log("proxy: SRV heap failed"); return false; }
            m_rtvStep = m_device->GetDescriptorHandleIncrementSize(D3D12_DESCRIPTOR_HEAP_TYPE_RTV);
            m_srvStep = m_device->GetDescriptorHandleIncrementSize(D3D12_DESCRIPTOR_HEAP_TYPE_CBV_SRV_UAV);

            if (FAILED(m_device->CreateFence(0, D3D12_FENCE_FLAG_NONE, IID_PPV_ARGS(&m_fence)))) { Log("proxy: fence failed"); return false; }
            m_event = CreateEventW(nullptr, FALSE, FALSE, nullptr);
            for (int i = 0; i < kSlots; i++)
            {
                if (FAILED(m_device->CreateCommandAllocator(D3D12_COMMAND_LIST_TYPE_DIRECT, IID_PPV_ARGS(&m_alloc[i])))) { Log("proxy: allocator failed"); return false; }
                m_alloc[i]->SetName(L"UltraRT HDR allocator");
            }
            if (FAILED(m_device->CreateCommandList(0, D3D12_COMMAND_LIST_TYPE_DIRECT, m_alloc[0], nullptr, IID_PPV_ARGS(&m_list)))) { Log("proxy: command list failed"); return false; }
            m_list->SetName(L"UltraRT HDR present");
            m_list->Close();
            if (!CreatePipeline()) return false;
            CreateTimer();
            return CreateBuffers();
        }

        // Optional: without it the capture line reports no cost.
        void CreateTimer()
        {
            if (FAILED(m_queue->GetTimestampFrequency(&m_queryFreq)) || m_queryFreq == 0) { m_queryFreq = 0; return; }
            D3D12_QUERY_HEAP_DESC qd = {};
            qd.Type = D3D12_QUERY_HEAP_TYPE_TIMESTAMP;
            qd.Count = kSlots * 2;
            D3D12_HEAP_PROPERTIES hp = {};
            hp.Type = D3D12_HEAP_TYPE_READBACK;
            D3D12_RESOURCE_DESC bd = {};
            bd.Dimension = D3D12_RESOURCE_DIMENSION_BUFFER;
            bd.Width = kSlots * 2 * sizeof(UINT64);
            bd.Height = 1;
            bd.DepthOrArraySize = 1;
            bd.MipLevels = 1;
            bd.SampleDesc.Count = 1;
            bd.Layout = D3D12_TEXTURE_LAYOUT_ROW_MAJOR;
            if (FAILED(m_device->CreateQueryHeap(&qd, IID_PPV_ARGS(&m_queryHeap))) ||
                FAILED(m_device->CreateCommittedResource(&hp, D3D12_HEAP_FLAG_NONE, &bd, D3D12_RESOURCE_STATE_COPY_DEST, nullptr, IID_PPV_ARGS(&m_queryRead))))
            {
                if (m_queryHeap) { m_queryHeap->Release(); m_queryHeap = nullptr; }
                m_queryFreq = 0;
            }
        }

        // The slot's commands are done (its fence passed): read the two timestamps it resolved.
        void CollectTiming(int k)
        {
            if (!m_slotTimed[k] || !m_queryRead) return;
            m_slotTimed[k] = false;
            D3D12_RANGE range = { (SIZE_T)k * 2 * sizeof(UINT64), (SIZE_T)(k * 2 + 2) * sizeof(UINT64) };
            void* p = nullptr;
            if (FAILED(m_queryRead->Map(0, &range, &p))) return;
            const UINT64* q = (const UINT64*)p + k * 2;
            if (q[1] >= q[0]) { m_gpuMs += (double)(q[1] - q[0]) * 1000.0 / (double)m_queryFreq; m_gpuCount++; }
            D3D12_RANGE none = { 0, 0 };
            m_queryRead->Unmap(0, &none);
        }

        bool CreatePipeline()
        {
            ID3DBlob* vs = nullptr; ID3DBlob* ps = nullptr; ID3DBlob* err = nullptr;
            HRESULT hr = D3DCompile(kShader, sizeof kShader - 1, "UltraRT.Hdr", nullptr, nullptr, "VS", "vs_5_0", 0, 0, &vs, &err);
            if (FAILED(hr)) { Log("proxy: VS compile failed: %s", err ? (const char*)err->GetBufferPointer() : "?"); if (err) err->Release(); return false; }
            hr = D3DCompile(kShader, sizeof kShader - 1, "UltraRT.Hdr", nullptr, nullptr, "PS", "ps_5_0", 0, 0, &ps, &err);
            if (FAILED(hr)) { Log("proxy: PS compile failed: %s", err ? (const char*)err->GetBufferPointer() : "?"); if (err) err->Release(); vs->Release(); return false; }
            if (err) { err->Release(); err = nullptr; }

            D3D12_DESCRIPTOR_RANGE range = {};
            range.RangeType = D3D12_DESCRIPTOR_RANGE_TYPE_SRV;
            range.NumDescriptors = 3;
            D3D12_ROOT_PARAMETER rp[2] = {};
            rp[0].ParameterType = D3D12_ROOT_PARAMETER_TYPE_DESCRIPTOR_TABLE;
            rp[0].DescriptorTable.NumDescriptorRanges = 1;
            rp[0].DescriptorTable.pDescriptorRanges = &range;
            rp[0].ShaderVisibility = D3D12_SHADER_VISIBILITY_PIXEL;
            rp[1].ParameterType = D3D12_ROOT_PARAMETER_TYPE_32BIT_CONSTANTS;
            rp[1].Constants.Num32BitValues = sizeof(Constants) / 4;
            rp[1].ShaderVisibility = D3D12_SHADER_VISIBILITY_PIXEL;
            D3D12_STATIC_SAMPLER_DESC ss[2] = {};
            for (int i = 0; i < 2; i++)
            {
                ss[i].Filter = i == 0 ? D3D12_FILTER_MIN_MAG_MIP_POINT : D3D12_FILTER_MIN_MAG_LINEAR_MIP_POINT;
                ss[i].AddressU = ss[i].AddressV = ss[i].AddressW = D3D12_TEXTURE_ADDRESS_MODE_CLAMP;
                ss[i].MaxLOD = D3D12_FLOAT32_MAX;
                ss[i].ShaderRegister = (UINT)i;
                ss[i].ShaderVisibility = D3D12_SHADER_VISIBILITY_PIXEL;
            }
            D3D12_ROOT_SIGNATURE_DESC rd = {};
            rd.NumParameters = 2;
            rd.pParameters = rp;
            rd.NumStaticSamplers = 2;
            rd.pStaticSamplers = ss;
            ID3DBlob* sig = nullptr;
            hr = D3D12SerializeRootSignature(&rd, D3D_ROOT_SIGNATURE_VERSION_1, &sig, &err);
            if (SUCCEEDED(hr)) hr = m_device->CreateRootSignature(0, sig->GetBufferPointer(), sig->GetBufferSize(), IID_PPV_ARGS(&m_rootSig));
            if (sig) sig->Release();
            if (err) err->Release();
            if (FAILED(hr)) { Log("proxy: root signature failed (0x%08X)", (unsigned)hr); vs->Release(); ps->Release(); return false; }

            D3D12_GRAPHICS_PIPELINE_STATE_DESC pd = {};
            pd.pRootSignature = m_rootSig;
            pd.VS = { vs->GetBufferPointer(), vs->GetBufferSize() };
            pd.PS = { ps->GetBufferPointer(), ps->GetBufferSize() };
            pd.BlendState.RenderTarget[0].RenderTargetWriteMask = D3D12_COLOR_WRITE_ENABLE_ALL;
            pd.SampleMask = UINT_MAX;
            pd.RasterizerState.FillMode = D3D12_FILL_MODE_SOLID;
            pd.RasterizerState.CullMode = D3D12_CULL_MODE_NONE;
            pd.RasterizerState.DepthClipEnable = TRUE;
            pd.PrimitiveTopologyType = D3D12_PRIMITIVE_TOPOLOGY_TYPE_TRIANGLE;
            pd.NumRenderTargets = 1;
            pd.RTVFormats[0] = kHdrFormat;
            pd.SampleDesc.Count = 1;
            hr = m_device->CreateGraphicsPipelineState(&pd, IID_PPV_ARGS(&m_pso));
            vs->Release();
            ps->Release();
            if (FAILED(hr)) { Log("proxy: pipeline state failed (0x%08X)", (unsigned)hr); return false; }
            return true;
        }

        D3D12_CPU_DESCRIPTOR_HANDLE Rtv(UINT i) const
        {
            D3D12_CPU_DESCRIPTOR_HANDLE h = m_rtvHeap->GetCPUDescriptorHandleForHeapStart();
            h.ptr += (SIZE_T)i * m_rtvStep;
            return h;
        }

        bool CreateBuffers()
        {
            DXGI_SWAP_CHAIN_DESC1 d = {};
            if (FAILED(m_real->GetDesc1(&d))) { Log("proxy: GetDesc1 failed"); return false; }
            if (d.BufferCount > kMaxBuffers) { Log("proxy: %u buffers (max %d)", d.BufferCount, kMaxBuffers); return false; }
            m_count = d.BufferCount;
            for (UINT i = 0; i < m_count; i++)
            {
                HRESULT hr = m_real->GetBuffer(i, IID_PPV_ARGS(&m_realBuf[i]));
                if (FAILED(hr)) { Log("proxy: GetBuffer(%u) failed (0x%08X)", i, (unsigned)hr); return false; }
                m_device->CreateRenderTargetView(m_realBuf[i], nullptr, Rtv(i));
                D3D12_RESOURCE_DESC rd = m_realBuf[i]->GetDesc();
                m_w = (UINT)rd.Width;
                m_h = rd.Height;
                // What Unity renders into: its own format, a render target, in the state a back buffer is in (PRESENT).
                D3D12_HEAP_PROPERTIES hp = {};
                hp.Type = D3D12_HEAP_TYPE_DEFAULT;
                D3D12_RESOURCE_DESC fd = {};
                fd.Dimension = D3D12_RESOURCE_DIMENSION_TEXTURE2D;
                fd.Width = rd.Width;
                fd.Height = rd.Height;
                fd.DepthOrArraySize = 1;
                fd.MipLevels = 1;
                fd.Format = m_unityFormat;
                fd.SampleDesc.Count = 1;
                fd.Flags = D3D12_RESOURCE_FLAG_ALLOW_RENDER_TARGET;
                D3D12_CLEAR_VALUE cv = {};
                cv.Format = m_unityFormat;
                cv.Color[3] = 1.0f;
                hr = m_device->CreateCommittedResource(&hp, D3D12_HEAP_FLAG_NONE, &fd, D3D12_RESOURCE_STATE_PRESENT, &cv, IID_PPV_ARGS(&m_fake[i]));
                if (FAILED(hr)) { Log("proxy: back buffer texture %u failed (0x%08X)", i, (unsigned)hr); return false; }
                m_fake[i]->SetName(L"UltraRT HDR proxy back buffer");
            }
            s_Width = (int)m_w;
            s_Height = (int)m_h;
            Log("proxy: %u buffers %ux%u, real %s, Unity sees format %d, flags 0x%X", m_count, m_w, m_h, "R10G10B10A2 HDR10", (int)m_unityFormat, d.Flags);
            return true;
        }

        void ReleaseBuffers()
        {
            for (int i = 0; i < kMaxBuffers; i++)
            {
                if (m_realBuf[i]) { m_realBuf[i]->Release(); m_realBuf[i] = nullptr; }
                if (m_fake[i]) { m_fake[i]->Release(); m_fake[i] = nullptr; }
            }
        }

        void WaitFor(UINT64 value, DWORD ms)
        {
            if (!m_fence || value == 0 || m_fence->GetCompletedValue() >= value) return;
            if (SUCCEEDED(m_fence->SetEventOnCompletion(value, m_event))) WaitForSingleObject(m_event, ms);
        }

        // Everything on the queue done (Unity's lists too: they may still draw into the textures we are about to drop).
        void WaitIdle()
        {
            if (!m_fence || !m_queue) return;
            if (SUCCEEDED(m_queue->Signal(m_fence, ++m_fenceValue))) WaitFor(m_fenceValue, 5000);
        }

        HRESULT Resize(UINT count, UINT w, UINT h, DXGI_FORMAT f, UINT flags, const UINT* masks, IUnknown* const* queues)
        {
            WaitIdle();
            ReleaseBuffers();
            DXGI_FORMAT rf = f == DXGI_FORMAT_UNKNOWN ? DXGI_FORMAT_UNKNOWN : kHdrFormat;
            if (f != DXGI_FORMAT_UNKNOWN) m_unityFormat = f;
            HRESULT hr = masks || queues ? m_real->ResizeBuffers1(count, w, h, rf, flags, masks, queues) : m_real->ResizeBuffers(count, w, h, rf, flags);
            Log("proxy: ResizeBuffers(%u, %ux%u, format %d, flags 0x%X) -> 0x%08X", count, w, h, (int)f, flags, (unsigned)hr);
            if (FAILED(hr)) { m_failed = true; return hr; }
            m_real->SetColorSpace1(kHdrSpace);
            if (!CreateBuffers()) { m_failed = true; return E_FAIL; }
            m_failed = false;
            return hr;
        }

        static void Transition(D3D12_RESOURCE_BARRIER& b, ID3D12Resource* r, D3D12_RESOURCE_STATES from, D3D12_RESOURCE_STATES to)
        {
            b = {};
            b.Type = D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;
            b.Transition.pResource = r;
            b.Transition.Subresource = D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES;
            b.Transition.StateBefore = from;
            b.Transition.StateAfter = to;
        }

        // The fullscreen pass: Unity's finished frame (fake buffer) -> HDR10 in the real back buffer.
        void Compose()
        {
            if (m_failed) return;
            UINT idx = m_real->GetCurrentBackBufferIndex();
            if (idx >= m_count || !m_fake[idx] || !m_realBuf[idx]) return;

            HdrParams prm;
            HdrFrame fr = {}, fl = {};
            bool haveGain = false, haveLate = false, capture = false;
            wchar_t capturePath[MAX_PATH] = L"";
            AcquireSRWLockExclusive(&s_Lock);
            prm = s_Params;
            if (s_PresentValid) { fr = s_Present; s_Present = {}; s_PresentValid = false; haveGain = fr.gain != nullptr; }
            if (s_PresentLateValid) { fl = s_PresentLate; s_PresentLate = {}; s_PresentLateValid = false; haveLate = fl.gain != nullptr; }
            if (s_CaptureWanted) { capture = true; s_CaptureWanted = false; wcsncpy_s(capturePath, s_CapturePath, _TRUNCATE); }
            ReleaseSRWLockExclusive(&s_Lock);

            LARGE_INTEGER t0;
            QueryPerformanceCounter(&t0);
            int k = m_slot;
            m_slot = (m_slot + 1) % kSlots;
            WaitFor(m_slotFence[k], 2000);
            CollectTiming(k);
            if (m_slotGain[k]) { m_slotGain[k]->Release(); m_slotGain[k] = nullptr; }
            m_slotGain[k] = fr.gain; // keeps the reference until this slot's commands are done
            if (m_slotLate[k]) { m_slotLate[k]->Release(); m_slotLate[k] = nullptr; }
            m_slotLate[k] = fl.gain;
            if (haveGain && prm.gainStrength <= 0.0f) haveGain = false;
            if (haveLate && prm.gainStrength <= 0.0f) haveLate = false;

            if (FAILED(m_alloc[k]->Reset()) || FAILED(m_list->Reset(m_alloc[k], m_pso))) { m_failed = true; Log("proxy: command list reset failed; HDR pass stopped"); return; }

            D3D12_CPU_DESCRIPTOR_HANDLE srv = m_srvHeap->GetCPUDescriptorHandleForHeapStart();
            srv.ptr += (SIZE_T)(k * 3) * m_srvStep;
            D3D12_SHADER_RESOURCE_VIEW_DESC sd = {};
            sd.Format = m_unityFormat;
            sd.ViewDimension = D3D12_SRV_DIMENSION_TEXTURE2D;
            sd.Shader4ComponentMapping = D3D12_DEFAULT_SHADER_4_COMPONENT_MAPPING;
            sd.Texture2D.MipLevels = 1;
            m_device->CreateShaderResourceView(m_fake[idx], &sd, srv);
            srv.ptr += m_srvStep;
            sd.Format = DXGI_FORMAT_R16G16B16A16_FLOAT;
            m_device->CreateShaderResourceView(haveGain ? fr.gain : nullptr, &sd, srv);
            srv.ptr += m_srvStep;
            m_device->CreateShaderResourceView(haveLate ? fl.gain : nullptr, &sd, srv);

            if (m_queryHeap) m_list->EndQuery(m_queryHeap, D3D12_QUERY_TYPE_TIMESTAMP, (UINT)k * 2);
            D3D12_RESOURCE_BARRIER b[4];
            int nb = 0;
            Transition(b[nb++], m_fake[idx], D3D12_RESOURCE_STATE_PRESENT, D3D12_RESOURCE_STATE_PIXEL_SHADER_RESOURCE);
            Transition(b[nb++], m_realBuf[idx], D3D12_RESOURCE_STATE_PRESENT, D3D12_RESOURCE_STATE_RENDER_TARGET);
            if (haveGain) Transition(b[nb++], fr.gain, D3D12_RESOURCE_STATE_UNORDERED_ACCESS, D3D12_RESOURCE_STATE_PIXEL_SHADER_RESOURCE);
            if (haveLate) Transition(b[nb++], fl.gain, D3D12_RESOURCE_STATE_UNORDERED_ACCESS, D3D12_RESOURCE_STATE_PIXEL_SHADER_RESOURCE);
            m_list->ResourceBarrier((UINT)nb, b);

            Constants c = {};
            c.paperNits = prm.paperNits > 1.0f ? prm.paperNits : (s_SdrWhiteNits > 1.0f ? s_SdrWhiteNits : 200.0f);
            float peak = prm.peakNits > 0.0f ? prm.peakNits : (m_displayMaxNits > 0.0f ? m_displayMaxNits : 1000.0f);
            c.peakNits = peak > c.paperNits ? peak : c.paperNits;
            c.gainOn = haveGain ? 1.0f : 0.0f;
            c.gammaMode = prm.gamma22;
            c.uvScaleX = fr.uvScaleX; c.uvScaleY = fr.uvScaleY; c.uvOffsetX = fr.uvOffsetX; c.uvOffsetY = fr.uvOffsetY;
            c.tolLo = prm.tolLo; c.tolHi = prm.tolHi > prm.tolLo ? prm.tolHi : prm.tolLo + 0.01f;
            c.sdrBoost = prm.sdrBoost;
            c.debugMode = prm.debug;
            c.gainMax = prm.gainMax > 1.0f ? prm.gainMax : 1.0f;
            c.linearGain = (fr.flags & 1) ? 1.0f : 0.0f;
            c.gainStrength = prm.gainStrength;
            c.lateOn = haveLate ? 1.0f : 0.0f;
            c.lateScaleX = fl.uvScaleX; c.lateScaleY = fl.uvScaleY; c.lateOffsetX = fl.uvOffsetX; c.lateOffsetY = fl.uvOffsetY;

            D3D12_GPU_DESCRIPTOR_HANDLE table = m_srvHeap->GetGPUDescriptorHandleForHeapStart();
            table.ptr += (UINT64)(k * 3) * m_srvStep;
            D3D12_VIEWPORT vp = { 0.0f, 0.0f, (float)m_w, (float)m_h, 0.0f, 1.0f };
            D3D12_RECT sc = { 0, 0, (LONG)m_w, (LONG)m_h };
            D3D12_CPU_DESCRIPTOR_HANDLE rtv = Rtv(idx);
            m_list->SetGraphicsRootSignature(m_rootSig);
            m_list->SetDescriptorHeaps(1, &m_srvHeap);
            m_list->SetGraphicsRootDescriptorTable(0, table);
            m_list->SetGraphicsRoot32BitConstants(1, sizeof(Constants) / 4, &c, 0);
            m_list->RSSetViewports(1, &vp);
            m_list->RSSetScissorRects(1, &sc);
            m_list->OMSetRenderTargets(1, &rtv, FALSE, nullptr);
            m_list->IASetPrimitiveTopology(D3D_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
            m_list->DrawInstanced(3, 1, 0, 0);

            D3D12_PLACED_SUBRESOURCE_FOOTPRINT fp = {};
            if (capture) capture = PrepareReadback(idx, &fp);
            nb = 0;
            Transition(b[nb++], m_fake[idx], D3D12_RESOURCE_STATE_PIXEL_SHADER_RESOURCE, D3D12_RESOURCE_STATE_PRESENT);
            if (capture)
            {
                D3D12_RESOURCE_BARRIER cb;
                Transition(cb, m_realBuf[idx], D3D12_RESOURCE_STATE_RENDER_TARGET, D3D12_RESOURCE_STATE_COPY_SOURCE);
                m_list->ResourceBarrier(1, &cb);
                D3D12_TEXTURE_COPY_LOCATION dst = {}, src = {};
                dst.pResource = m_readback;
                dst.Type = D3D12_TEXTURE_COPY_TYPE_PLACED_FOOTPRINT;
                dst.PlacedFootprint = fp;
                src.pResource = m_realBuf[idx];
                src.Type = D3D12_TEXTURE_COPY_TYPE_SUBRESOURCE_INDEX;
                m_list->CopyTextureRegion(&dst, 0, 0, 0, &src, nullptr);
                Transition(b[nb++], m_realBuf[idx], D3D12_RESOURCE_STATE_COPY_SOURCE, D3D12_RESOURCE_STATE_PRESENT);
            }
            else Transition(b[nb++], m_realBuf[idx], D3D12_RESOURCE_STATE_RENDER_TARGET, D3D12_RESOURCE_STATE_PRESENT);
            if (haveGain) Transition(b[nb++], fr.gain, D3D12_RESOURCE_STATE_PIXEL_SHADER_RESOURCE, D3D12_RESOURCE_STATE_UNORDERED_ACCESS);
            if (haveLate) Transition(b[nb++], fl.gain, D3D12_RESOURCE_STATE_PIXEL_SHADER_RESOURCE, D3D12_RESOURCE_STATE_UNORDERED_ACCESS);
            m_list->ResourceBarrier((UINT)nb, b);
            if (m_queryHeap)
            {
                m_list->EndQuery(m_queryHeap, D3D12_QUERY_TYPE_TIMESTAMP, (UINT)k * 2 + 1);
                m_list->ResolveQueryData(m_queryHeap, D3D12_QUERY_TYPE_TIMESTAMP, (UINT)k * 2, 2, m_queryRead, (UINT64)k * 2 * sizeof(UINT64));
                m_slotTimed[k] = true;
            }
            if (FAILED(m_list->Close())) { m_failed = true; Log("proxy: command list close failed; HDR pass stopped"); return; }

            ID3D12CommandList* lists[] = { m_list };
            m_queue->ExecuteCommandLists(1, lists);
            m_queue->Signal(m_fence, ++m_fenceValue);
            m_slotFence[k] = m_fenceValue;
            LARGE_INTEGER t1, qpf;
            QueryPerformanceCounter(&t1);
            QueryPerformanceFrequency(&qpf);
            m_cpuMs += (double)(t1.QuadPart - t0.QuadPart) * 1000.0 / (double)qpf.QuadPart;
            m_cpuCount++;
            InterlockedIncrement64(&s_Presents);
            if (haveGain) InterlockedIncrement64(&s_GainFrames);
            if (haveLate) InterlockedIncrement64(&s_LateFrames);
            if (m_presentLogs < 1)
            {
                m_presentLogs++;
                InterlockedExchange(&s_State, 2);
                Log("proxy: first HDR10 present (paper white %.0f nits, peak %.0f nits, display max %.0f)", c.paperNits, c.peakNits, m_displayMaxNits);
            }
            if (capture) FinishReadback(fp, capturePath, c, haveGain, haveLate);
        }

        bool PrepareReadback(UINT idx, D3D12_PLACED_SUBRESOURCE_FOOTPRINT* fp)
        {
            D3D12_RESOURCE_DESC rd = m_realBuf[idx]->GetDesc();
            UINT64 total = 0;
            m_device->GetCopyableFootprints(&rd, 0, 1, 0, fp, nullptr, nullptr, &total);
            if (m_readback && m_readbackSize < total) { m_readback->Release(); m_readback = nullptr; }
            if (!m_readback)
            {
                D3D12_HEAP_PROPERTIES hp = {};
                hp.Type = D3D12_HEAP_TYPE_READBACK;
                D3D12_RESOURCE_DESC bd = {};
                bd.Dimension = D3D12_RESOURCE_DIMENSION_BUFFER;
                bd.Width = total;
                bd.Height = 1;
                bd.DepthOrArraySize = 1;
                bd.MipLevels = 1;
                bd.SampleDesc.Count = 1;
                bd.Layout = D3D12_TEXTURE_LAYOUT_ROW_MAJOR;
                if (FAILED(m_device->CreateCommittedResource(&hp, D3D12_HEAP_FLAG_NONE, &bd, D3D12_RESOURCE_STATE_COPY_DEST, nullptr, IID_PPV_ARGS(&m_readback))))
                { Log("capture: readback buffer failed"); return false; }
                m_readbackSize = total;
            }
            return true;
        }

        // Blocks the render thread until the GPU is done (a diagnostic, not a per-frame path): statistics of what the
        // display was sent, and optionally the raw R10G10B10A2 frame.
        void FinishReadback(const D3D12_PLACED_SUBRESOURCE_FOOTPRINT& fp, const wchar_t* path, const Constants& c, bool haveGain, bool haveLate)
        {
            WaitFor(m_fenceValue, 5000);
            void* p = nullptr;
            D3D12_RANGE range = { 0, (SIZE_T)m_readbackSize };
            if (FAILED(m_readback->Map(0, &range, &p))) { Log("capture: Map failed"); return; }
            static float lut[1024];
            static bool lutInit = false;
            if (!lutInit) { for (int i = 0; i < 1024; i++) lut[i] = (float)PqToNits(i / 1023.0); lutInit = true; }
            const UINT w = fp.Footprint.Width, h = fp.Footprint.Height;
            double sum = 0;
            float maxY = 0, maxC = 0;
            UINT64 above = 0, above2 = 0, nearPeak = 0;
            UINT maxCode = 0;
            for (UINT y = 0; y < h; y++)
            {
                const UINT32* row = (const UINT32*)((const BYTE*)p + fp.Offset + (SIZE_T)y * fp.Footprint.RowPitch);
                for (UINT x = 0; x < w; x++)
                {
                    UINT32 v = row[x];
                    UINT r = v & 1023, g = (v >> 10) & 1023, bl = (v >> 20) & 1023;
                    UINT mc = r > g ? (r > bl ? r : bl) : (g > bl ? g : bl);
                    if (mc > maxCode) maxCode = mc;
                    float Y = 0.2627f * lut[r] + 0.6780f * lut[g] + 0.0593f * lut[bl];
                    sum += Y;
                    if (Y > maxY) maxY = Y;
                    if (lut[mc] > maxC) maxC = lut[mc];
                    if (Y > c.paperNits * 1.05f) above++;
                    if (Y > c.paperNits * 2.0f) above2++;
                    if (Y > c.peakNits * 0.9f) nearPeak++;
                }
            }
            double n = (double)w * h;
            Log("capture: %ux%u HDR10, gain map %s, late map %s; mean %.1f nits, max luminance %.0f nits, max channel %.0f nits (PQ code %u); "
                "above paper white (%.0f nits) %.3f%% of pixels, above 2x %.3f%%, within 10%% of peak (%.0f nits) %.3f%%",
                w, h, haveGain ? "applied" : "none", haveLate ? "applied" : "none", sum / n, maxY, maxC, maxCode, c.paperNits, 100.0 * above / n, 100.0 * above2 / n,
                c.peakNits, 100.0 * nearPeak / n);
            if (m_cpuCount > 0)
            {
                Log("present pass since the last capture: GPU %.3f ms (%llu frames), render thread %.3f ms (%llu frames)",
                    m_gpuCount ? m_gpuMs / (double)m_gpuCount : 0.0, (unsigned long long)m_gpuCount, m_cpuMs / (double)m_cpuCount, (unsigned long long)m_cpuCount);
                m_gpuMs = m_cpuMs = 0;
                m_gpuCount = m_cpuCount = 0;
            }
            if (path && path[0])
            {
                HANDLE f = CreateFileW(path, GENERIC_WRITE, 0, nullptr, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
                if (f != INVALID_HANDLE_VALUE)
                {
                    // "URTHDR10", width, height, then rows of R10G10B10A2 (PQ, Rec.2020), top row first.
                    DWORD wr = 0;
                    UINT32 head[2] = { w, h };
                    WriteFile(f, "URTHDR10", 8, &wr, nullptr);
                    WriteFile(f, head, sizeof head, &wr, nullptr);
                    for (UINT y = 0; y < h; y++)
                        WriteFile(f, (const BYTE*)p + fp.Offset + (SIZE_T)y * fp.Footprint.RowPitch, w * 4, &wr, nullptr);
                    CloseHandle(f);
                }
                else Log("capture: could not write %ls", path);
            }
            D3D12_RANGE none = { 0, 0 };
            m_readback->Unmap(0, &none);
        }

        LONG m_ref = 1;
        IDXGISwapChain3* m_real = nullptr;
        IDXGISwapChain4* m_real4 = nullptr;
        ID3D12CommandQueue* m_queue = nullptr;
        ID3D12Device* m_device = nullptr;
        DXGI_FORMAT m_unityFormat = DXGI_FORMAT_R8G8B8A8_UNORM;
        float m_displayMaxNits = 0.0f;
        UINT m_count = 0, m_w = 0, m_h = 0;
        ID3D12Resource* m_fake[kMaxBuffers] = {};
        ID3D12Resource* m_realBuf[kMaxBuffers] = {};
        ID3D12DescriptorHeap* m_rtvHeap = nullptr;
        ID3D12DescriptorHeap* m_srvHeap = nullptr;
        UINT m_rtvStep = 0, m_srvStep = 0;
        ID3D12RootSignature* m_rootSig = nullptr;
        ID3D12PipelineState* m_pso = nullptr;
        ID3D12CommandAllocator* m_alloc[kSlots] = {};
        UINT64 m_slotFence[kSlots] = {};
        ID3D12Resource* m_slotGain[kSlots] = {};
        ID3D12Resource* m_slotLate[kSlots] = {};
        ID3D12GraphicsCommandList* m_list = nullptr;
        ID3D12Fence* m_fence = nullptr;
        UINT64 m_fenceValue = 0;
        HANDLE m_event = nullptr;
        ID3D12Resource* m_readback = nullptr;
        UINT64 m_readbackSize = 0;
        int m_slot = 0;
        int m_presentLogs = 0, m_qiLogs = 0;
        // Cost of the pass: GPU timestamps around it (read back when the slot comes round again) and the render thread's time.
        ID3D12QueryHeap* m_queryHeap = nullptr;
        ID3D12Resource* m_queryRead = nullptr;
        UINT64 m_queryFreq = 0;
        bool m_slotTimed[kSlots] = {};
        double m_gpuMs = 0, m_cpuMs = 0;
        UINT64 m_gpuCount = 0, m_cpuCount = 0;
        bool m_failed = false;
    };

    // ---- the factory hooks
    typedef HRESULT (STDMETHODCALLTYPE* CreateSwapChainFn)(IDXGIFactory*, IUnknown*, DXGI_SWAP_CHAIN_DESC*, IDXGISwapChain**);
    typedef HRESULT (STDMETHODCALLTYPE* CreateForHwndFn)(IDXGIFactory2*, IUnknown*, HWND, const DXGI_SWAP_CHAIN_DESC1*,
                                                          const DXGI_SWAP_CHAIN_FULLSCREEN_DESC*, IDXGIOutput*, IDXGISwapChain1**);
    CreateSwapChainFn s_OrigCreate = nullptr;
    CreateForHwndFn s_OrigForHwnd = nullptr;
    thread_local bool t_Inside = false;

    // Windows' SDR white level of a display in HDR mode (Settings > Display > HDR > SDR content brightness): what an SDR
    // game is shown at there, so the natural paper white. Read only. 0 if it can't be found.
    float SdrWhiteNits(const wchar_t* gdiName)
    {
        UINT32 np = 0, nm = 0;
        if (GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, &np, &nm) != ERROR_SUCCESS || np == 0) return 0.0f;
        DISPLAYCONFIG_PATH_INFO* paths = (DISPLAYCONFIG_PATH_INFO*)calloc(np, sizeof(DISPLAYCONFIG_PATH_INFO));
        DISPLAYCONFIG_MODE_INFO* modes = (DISPLAYCONFIG_MODE_INFO*)calloc(nm ? nm : 1, sizeof(DISPLAYCONFIG_MODE_INFO));
        float nits = 0.0f;
        if (paths && modes && QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, &np, paths, &nm, modes, nullptr) == ERROR_SUCCESS)
        {
            for (UINT32 i = 0; i < np; i++)
            {
                DISPLAYCONFIG_SOURCE_DEVICE_NAME sn = {};
                sn.header.type = DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME;
                sn.header.size = sizeof sn;
                sn.header.adapterId = paths[i].sourceInfo.adapterId;
                sn.header.id = paths[i].sourceInfo.id;
                if (DisplayConfigGetDeviceInfo(&sn.header) != ERROR_SUCCESS || wcscmp(sn.viewGdiDeviceName, gdiName) != 0) continue;
                DISPLAYCONFIG_SDR_WHITE_LEVEL wl = {};
                wl.header.type = DISPLAYCONFIG_DEVICE_INFO_GET_SDR_WHITE_LEVEL;
                wl.header.size = sizeof wl;
                wl.header.adapterId = paths[i].targetInfo.adapterId;
                wl.header.id = paths[i].targetInfo.id;
                if (DisplayConfigGetDeviceInfo(&wl.header) == ERROR_SUCCESS) nits = (float)wl.SDRWhiteLevel / 1000.0f * 80.0f;
                break;
            }
        }
        free(paths);
        free(modes);
        return nits;
    }

    // Is HDR on (Windows "Use HDR") for the display the window is on? Also its reported peak luminance.
    bool OutputIsHdr(IDXGIFactory* factory, HWND hwnd, float* maxNits)
    {
        *maxNits = 0.0f;
        IDXGIFactory1* f1 = nullptr;
        if (FAILED(factory->QueryInterface(IID_PPV_ARGS(&f1)))) return false;
        HMONITOR mon = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        bool result = false, found = false;
        IDXGIAdapter1* a = nullptr;
        for (UINT i = 0; SUCCEEDED(f1->EnumAdapters1(i, &a)); i++)
        {
            IDXGIOutput* o = nullptr;
            for (UINT j = 0; SUCCEEDED(a->EnumOutputs(j, &o)); j++)
            {
                IDXGIOutput6* o6 = nullptr;
                DXGI_OUTPUT_DESC1 d = {};
                if (SUCCEEDED(o->QueryInterface(IID_PPV_ARGS(&o6))) && SUCCEEDED(o6->GetDesc1(&d)))
                {
                    bool hdr = d.ColorSpace == DXGI_COLOR_SPACE_RGB_FULL_G2084_NONE_P2020;
                    bool mine = d.Monitor == mon;
                    Log("display %ls%s: HDR %s, max %.0f nits, max full frame %.0f nits, min %.4f nits, %u bits", d.DeviceName,
                        mine ? " (the game's)" : "", hdr ? "on" : "off", d.MaxLuminance, d.MaxFullFrameLuminance, d.MinLuminance, d.BitsPerColor);
                    if (mine && !found)
                    {
                        found = true; result = hdr; *maxNits = d.MaxLuminance;
                        s_SdrWhiteNits = hdr ? SdrWhiteNits(d.DeviceName) : 0.0f;
                        if (hdr) Log("display %ls: Windows shows SDR content at %.0f nits", d.DeviceName, s_SdrWhiteNits);
                    }
                }
                if (o6) o6->Release();
                o->Release();
            }
            a->Release();
        }
        f1->Release();
        return result;
    }

    bool UpgradableFormat(DXGI_FORMAT f) { return f == DXGI_FORMAT_R8G8B8A8_UNORM || f == DXGI_FORMAT_B8G8R8A8_UNORM; }
    bool FlipModel(DXGI_SWAP_EFFECT e) { return e == DXGI_SWAP_EFFECT_FLIP_DISCARD || e == DXGI_SWAP_EFFECT_FLIP_SEQUENTIAL; }

    // Decides whether this swapchain gets the proxy; returns the D3D12 queue (a reference) if so.
    ID3D12CommandQueue* WantProxy(IDXGIFactory* factory, IUnknown* device, HWND hwnd, DXGI_FORMAT format, DXGI_SWAP_EFFECT effect,
                                  UINT w, UINT h, UINT count, UINT flags, float* maxNits)
    {
        ID3D12CommandQueue* queue = nullptr;
        if (!device || FAILED(device->QueryInterface(IID_PPV_ARGS(&queue))))
        {
            Log("swapchain %ux%u: not created on a D3D12 queue (the game is not on -force-d3d12?); left alone", w, h);
            InterlockedExchange(&s_State, -1);
            return nullptr;
        }
        bool hdr = OutputIsHdr(factory, hwnd, maxNits);
        s_DisplayMaxNits = *maxNits;
        Log("swapchain requested: %ux%u, format %d, %u buffers, swap effect %d, flags 0x%X", w, h, (int)format, count, (int)effect, flags);
        const char* why = nullptr;
        if (!hdr && !s_Force) why = "HDR is off for the game's display (Windows Settings > Display > Use HDR)";
        else if (!UpgradableFormat(format)) why = "unexpected back buffer format";
        else if (!FlipModel(effect)) why = "not a flip-model swapchain";
        if (why)
        {
            Log("HDR output not used: %s. The game runs in SDR as without UltraRT's HDR.", why);
            InterlockedExchange(&s_State, -1);
            queue->Release();
            return nullptr;
        }
        return queue;
    }

    HRESULT STDMETHODCALLTYPE Hook_CreateForHwnd(IDXGIFactory2* self, IUnknown* device, HWND hwnd, const DXGI_SWAP_CHAIN_DESC1* desc,
                                                 const DXGI_SWAP_CHAIN_FULLSCREEN_DESC* fs, IDXGIOutput* restrictTo, IDXGISwapChain1** out)
    {
        if (t_Inside || !desc || !out) return s_OrigForHwnd(self, device, hwnd, desc, fs, restrictTo, out);
        float maxNits = 0.0f;
        ID3D12CommandQueue* queue = WantProxy(self, device, hwnd, desc->Format, desc->SwapEffect, desc->Width, desc->Height, desc->BufferCount, desc->Flags, &maxNits);
        if (!queue) return s_OrigForHwnd(self, device, hwnd, desc, fs, restrictTo, out);
        DXGI_SWAP_CHAIN_DESC1 d = *desc;
        d.Format = kHdrFormat;
        IDXGISwapChain1* real = nullptr;
        t_Inside = true;
        HRESULT hr = s_OrigForHwnd(self, device, hwnd, &d, fs, restrictTo, &real);
        t_Inside = false;
        ProxySwapChain* proxy = nullptr;
        if (SUCCEEDED(hr) && real) proxy = ProxySwapChain::Create(real, queue, desc->Format, maxNits);
        else Log("creating the R10G10B10A2 swapchain failed (0x%08X)", (unsigned)hr);
        if (real) real->Release();
        queue->Release();
        if (!proxy)
        {
            Log("HDR output not used: falling back to the game's own SDR swapchain");
            InterlockedExchange(&s_State, -1);
            return s_OrigForHwnd(self, device, hwnd, desc, fs, restrictTo, out);
        }
        *out = proxy;
        return hr;
    }

    HRESULT STDMETHODCALLTYPE Hook_Create(IDXGIFactory* self, IUnknown* device, DXGI_SWAP_CHAIN_DESC* desc, IDXGISwapChain** out)
    {
        if (t_Inside || !desc || !out) return s_OrigCreate(self, device, desc, out);
        float maxNits = 0.0f;
        ID3D12CommandQueue* queue = WantProxy(self, device, desc->OutputWindow, desc->BufferDesc.Format, desc->SwapEffect, desc->BufferDesc.Width,
                                              desc->BufferDesc.Height, desc->BufferCount, desc->Flags, &maxNits);
        if (!queue) return s_OrigCreate(self, device, desc, out);
        DXGI_SWAP_CHAIN_DESC d = *desc;
        d.BufferDesc.Format = kHdrFormat;
        IDXGISwapChain* real = nullptr;
        t_Inside = true;
        HRESULT hr = s_OrigCreate(self, device, &d, &real);
        t_Inside = false;
        ProxySwapChain* proxy = nullptr;
        if (SUCCEEDED(hr) && real) proxy = ProxySwapChain::Create(real, queue, desc->BufferDesc.Format, maxNits);
        else Log("creating the R10G10B10A2 swapchain failed (0x%08X)", (unsigned)hr);
        if (real) real->Release();
        queue->Release();
        if (!proxy)
        {
            Log("HDR output not used: falling back to the game's own SDR swapchain");
            InterlockedExchange(&s_State, -1);
            return s_OrigCreate(self, device, desc, out);
        }
        *out = proxy;
        return hr;
    }

    bool PatchSlot(void** vtable, int slot, void* hook, void** original)
    {
        DWORD old = 0;
        if (!VirtualProtect(&vtable[slot], sizeof(void*), PAGE_READWRITE, &old)) return false;
        *original = vtable[slot];
        vtable[slot] = hook;
        VirtualProtect(&vtable[slot], sizeof(void*), old, &old);
        return true;
    }
}

// ------------------------------------------------------------------ hooks from GpuTimer.cpp

void Hdr_OnDeviceEvent(UnityGfxDeviceEventType type)
{
    if (type != kUnityGfxDeviceEventInitialize) return;
    IUnityInterfaces* u = Native_UnityInterfaces();
    if (!u) return;
    // Event configuration must happen at device init (docs/gpu-timer.md). The event touches no command list.
    UnityD3D12PluginEventConfig cfg = {};
    cfg.graphicsQueueAccess = kUnityD3D12GraphicsQueueAccess_DontCare;
    cfg.flags = 0;
    cfg.ensureActiveRenderTextureIsBound = false;
    if (auto v7 = u->Get<IUnityGraphicsD3D12v7>()) v7->ConfigureEvent(kEventHdrFrame, &cfg);
    else if (auto v6 = u->Get<IUnityGraphicsD3D12v6>()) v6->ConfigureEvent(kEventHdrFrame, &cfg);
}

// Render thread, in command order: the frame being recorded has this gain map.
bool Hdr_OnRenderEvent(int eventId, void* data)
{
    if (eventId != kEventHdrFrame) return false;
    int slot = (int)(intptr_t)data;
    AcquireSRWLockExclusive(&s_Lock);
    // flags bit 1: the late map (screen effects), kept apart from the composite's.
    bool late = slot >= 0 && slot < kFrameRing && (s_Frames[slot].flags & 2) != 0;
    HdrFrame& dst = late ? s_PresentLate : s_Present;
    bool& valid = late ? s_PresentLateValid : s_PresentValid;
    if (valid && dst.gain) dst.gain->Release();
    dst = {};
    valid = false;
    if (slot >= 0 && slot < kFrameRing && s_Frames[slot].gain && s_State == 2)
    {
        dst = s_Frames[slot];
        dst.gain->AddRef();
        valid = true;
    }
    ReleaseSRWLockExclusive(&s_Lock);
    return true;
}

// ------------------------------------------------------------------ API

// Preloader patcher, before Unity creates its graphics device. logPath: a text file for this module's lines (they are
// also kept for UltraRT_Hdr_TakeLog). force != 0: use the HDR10 swapchain even when the display reports HDR off.
ULTRART_API int UltraRT_Hdr_Install(const wchar_t* logPath, int force)
{
    if (s_State != 0) return 1;
    if (logPath) { wcsncpy_s(s_LogPath, logPath, _TRUNCATE); DeleteFileW(s_LogPath); }
    s_Force = force != 0;
    IDXGIFactory2* f = nullptr;
    HRESULT hr = CreateDXGIFactory1(IID_PPV_ARGS(&f));
    if (FAILED(hr) || !f) { Log("install: CreateDXGIFactory1 failed (0x%08X)", (unsigned)hr); return 0; }
    void** vtable = *reinterpret_cast<void***>(f);
    // IDXGIFactory: slot 10 CreateSwapChain; IDXGIFactory2: slot 15 CreateSwapChainForHwnd.
    bool ok = PatchSlot(vtable, 15, reinterpret_cast<void*>(&Hook_CreateForHwnd), reinterpret_cast<void**>(&s_OrigForHwnd)) &&
              PatchSlot(vtable, 10, reinterpret_cast<void*>(&Hook_Create), reinterpret_cast<void**>(&s_OrigCreate));
    f->Release();
    if (!ok) { Log("install: could not patch the DXGI factory (error %lu)", GetLastError()); return 0; }
    InterlockedExchange(&s_State, 1);
    Log("install: DXGI factory hooks in place%s; waiting for the game's swapchain", s_Force ? " (forced)" : "");
    return 1;
}

ULTRART_API int UltraRT_Hdr_GetState() { return (int)InterlockedCompareExchange(&s_State, 0, 0); }
ULTRART_API int UltraRT_Hdr_GetEvent() { return kEventHdrFrame; }
ULTRART_API int UltraRT_Hdr_GetRingSize() { return kFrameRing; }
ULTRART_API int UltraRT_Hdr_GetFrameSize() { return (int)sizeof(HdrFrame); }
ULTRART_API int UltraRT_Hdr_GetParamsSize() { return (int)sizeof(HdrParams); }

// out[0] display max nits, [1] back buffer width, [2] height, [3] presents, [4] presents with a gain map,
// [5] Windows' SDR white level for the display in nits (0 = unknown)
ULTRART_API void UltraRT_Hdr_GetInfo(double* out)
{
    if (!out) return;
    out[0] = s_DisplayMaxNits;
    out[1] = s_Width;
    out[2] = s_Height;
    out[3] = (double)InterlockedCompareExchange64(&s_Presents, 0, 0);
    out[4] = (double)InterlockedCompareExchange64(&s_GainFrames, 0, 0);
    out[5] = s_SdrWhiteNits;
}

ULTRART_API void UltraRT_Hdr_SetParams(const HdrParams* p)
{
    if (!p) return;
    AcquireSRWLockExclusive(&s_Lock);
    s_Params = *p;
    ReleaseSRWLockExclusive(&s_Lock);
}

// Main thread: the gain map of a frame; the plugin then issues the event (UltraRT_Hdr_GetEvent) with the slot as data.
// The slot keeps a reference on the texture until it is written again.
ULTRART_API void UltraRT_Hdr_SetFrame(int slot, const HdrFrame* f)
{
    if (slot < 0 || slot >= kFrameRing) return;
    AcquireSRWLockExclusive(&s_Lock);
    ID3D12Resource* old = s_Frames[slot].gain;
    s_Frames[slot] = f ? *f : HdrFrame{};
    if (s_Frames[slot].gain) s_Frames[slot].gain->AddRef();
    ReleaseSRWLockExclusive(&s_Lock);
    if (old) old->Release();
}

// The next Present logs statistics of the HDR10 frame it sent, and writes the raw frame to path (null or empty: no file).
ULTRART_API void UltraRT_Hdr_RequestCapture(const wchar_t* path)
{
    AcquireSRWLockExclusive(&s_Lock);
    if (path) wcsncpy_s(s_CapturePath, path, _TRUNCATE); else s_CapturePath[0] = 0;
    s_CaptureWanted = true;
    ReleaseSRWLockExclusive(&s_Lock);
}

ULTRART_API const char* UltraRT_Hdr_GetStatus()
{
    AcquireSRWLockShared(&s_LogLock);
    memcpy(s_StatusOut, s_Status, sizeof s_Status);
    ReleaseSRWLockShared(&s_LogLock);
    return s_StatusOut;
}

ULTRART_API int UltraRT_Hdr_TakeLog(char* out, int size)
{
    if (!out || size <= 0) return 0;
    AcquireSRWLockExclusive(&s_LogLock);
    int n = s_LogLen < size - 1 ? s_LogLen : size - 1;
    memcpy(out, s_Log, n);
    out[n] = 0;
    s_LogLen = 0;
    s_Log[0] = 0;
    ReleaseSRWLockExclusive(&s_LogLock);
    return n;
}
