// Stand-alone check of the HDR swapchain proxy (Hdr.cpp, docs/hdr.md), without the game:
//   HdrTest.exe <UltraRT.Native.dll> <output folder> [force]
// Installs the DXGI factory hooks, then does what Unity does: a D3D12 device, a small window, an R8G8B8A8 flip-model
// swapchain, frames that are half mid-grey (0.5) and half white. Asks the proxy for a capture and prints its log: with HDR
// on for the display, the capture must read about 43.5 nits and 200 nits (paper white 200, gamma 2.2). Also resizes once.
// The window is small, not activated, and open for about a second.

#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <d3d12.h>
#include <dxgi1_6.h>
#include <stdio.h>

typedef int (*InstallFn)(const wchar_t*, int);
typedef void (*CaptureFn)(const wchar_t*);
typedef int (*StateFn)();
typedef int (*TakeLogFn)(char*, int);

static ID3D12Device* device;
static ID3D12CommandQueue* queue;
static ID3D12CommandAllocator* alloc;
static ID3D12GraphicsCommandList* list;
static ID3D12Fence* fence;
static ID3D12DescriptorHeap* rtvHeap;
static UINT64 fenceValue;
static HANDLE fenceEvent;

static void WaitGpu()
{
    queue->Signal(fence, ++fenceValue);
    if (fence->GetCompletedValue() < fenceValue)
    {
        fence->SetEventOnCompletion(fenceValue, fenceEvent);
        WaitForSingleObject(fenceEvent, 5000);
    }
}

static bool Frame(IDXGISwapChain3* sc, UINT w, UINT h)
{
    UINT idx = sc->GetCurrentBackBufferIndex();
    ID3D12Resource* bb = nullptr;
    if (FAILED(sc->GetBuffer(idx, IID_PPV_ARGS(&bb)))) { printf("GetBuffer failed\n"); return false; }
    D3D12_CPU_DESCRIPTOR_HANDLE rtv = rtvHeap->GetCPUDescriptorHandleForHeapStart();
    device->CreateRenderTargetView(bb, nullptr, rtv);
    alloc->Reset();
    list->Reset(alloc, nullptr);
    D3D12_RESOURCE_BARRIER b = {};
    b.Type = D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;
    b.Transition.pResource = bb;
    b.Transition.Subresource = D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES;
    b.Transition.StateBefore = D3D12_RESOURCE_STATE_PRESENT;
    b.Transition.StateAfter = D3D12_RESOURCE_STATE_RENDER_TARGET;
    list->ResourceBarrier(1, &b);
    const float grey[4] = { 0.5f, 0.5f, 0.5f, 1.0f }, white[4] = { 1.0f, 1.0f, 1.0f, 1.0f };
    list->ClearRenderTargetView(rtv, grey, 0, nullptr);
    D3D12_RECT right = { (LONG)(w / 2), 0, (LONG)w, (LONG)h };
    list->ClearRenderTargetView(rtv, white, 1, &right);
    b.Transition.StateBefore = D3D12_RESOURCE_STATE_RENDER_TARGET;
    b.Transition.StateAfter = D3D12_RESOURCE_STATE_PRESENT;
    list->ResourceBarrier(1, &b);
    list->Close();
    ID3D12CommandList* lists[] = { list };
    queue->ExecuteCommandLists(1, lists);
    bb->Release();
    HRESULT hr = sc->Present(0, 0);
    WaitGpu();
    if (FAILED(hr)) { printf("Present failed 0x%08X\n", (unsigned)hr); return false; }
    return true;
}

int wmain(int argc, wchar_t** argv)
{
    if (argc < 3) { printf("usage: HdrTest <UltraRT.Native.dll> <output folder> [force]\n"); return 2; }
    HMODULE m = LoadLibraryW(argv[1]);
    if (!m) { printf("LoadLibrary failed: %lu\n", GetLastError()); return 1; }
    auto install = (InstallFn)GetProcAddress(m, "UltraRT_Hdr_Install");
    auto capture = (CaptureFn)GetProcAddress(m, "UltraRT_Hdr_RequestCapture");
    auto state = (StateFn)GetProcAddress(m, "UltraRT_Hdr_GetState");
    auto takeLog = (TakeLogFn)GetProcAddress(m, "UltraRT_Hdr_TakeLog");
    if (!install || !capture || !state || !takeLog) { printf("exports missing\n"); return 1; }
    wchar_t logPath[MAX_PATH], capPath[MAX_PATH];
    swprintf_s(logPath, L"%s\\hdrtest.log", argv[2]);
    swprintf_s(capPath, L"%s\\hdrtest.hdr10.bin", argv[2]);
    printf("install: %d\n", install(logPath, argc > 3 ? 1 : 0));

    WNDCLASSW wc = {};
    wc.lpfnWndProc = DefWindowProcW;
    wc.hInstance = GetModuleHandleW(nullptr);
    wc.lpszClassName = L"UltraRTHdrTest";
    RegisterClassW(&wc);
    UINT w = 640, h = 360;
    HWND hwnd = CreateWindowExW(WS_EX_NOACTIVATE, wc.lpszClassName, L"UltraRT HDR test", WS_POPUP, 80, 80, (int)w, (int)h, nullptr, nullptr, wc.hInstance, nullptr);
    ShowWindow(hwnd, SW_SHOWNOACTIVATE);

    IDXGIFactory4* factory = nullptr;
    if (FAILED(CreateDXGIFactory2(0, IID_PPV_ARGS(&factory)))) { printf("factory failed\n"); return 1; }
    if (FAILED(D3D12CreateDevice(nullptr, D3D_FEATURE_LEVEL_12_0, IID_PPV_ARGS(&device)))) { printf("device failed\n"); return 1; }
    D3D12_COMMAND_QUEUE_DESC qd = {};
    qd.Type = D3D12_COMMAND_LIST_TYPE_DIRECT;
    device->CreateCommandQueue(&qd, IID_PPV_ARGS(&queue));
    device->CreateCommandAllocator(D3D12_COMMAND_LIST_TYPE_DIRECT, IID_PPV_ARGS(&alloc));
    device->CreateCommandList(0, D3D12_COMMAND_LIST_TYPE_DIRECT, alloc, nullptr, IID_PPV_ARGS(&list));
    list->Close();
    device->CreateFence(0, D3D12_FENCE_FLAG_NONE, IID_PPV_ARGS(&fence));
    fenceEvent = CreateEventW(nullptr, FALSE, FALSE, nullptr);
    D3D12_DESCRIPTOR_HEAP_DESC hd = {};
    hd.Type = D3D12_DESCRIPTOR_HEAP_TYPE_RTV;
    hd.NumDescriptors = 1;
    device->CreateDescriptorHeap(&hd, IID_PPV_ARGS(&rtvHeap));

    DXGI_SWAP_CHAIN_DESC1 d = {};
    d.Width = w; d.Height = h;
    d.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
    d.SampleDesc.Count = 1;
    d.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT;
    d.BufferCount = 2;
    d.SwapEffect = DXGI_SWAP_EFFECT_FLIP_DISCARD;
    d.Flags = DXGI_SWAP_CHAIN_FLAG_ALLOW_TEARING;
    IDXGISwapChain1* sc1 = nullptr;
    HRESULT hr = factory->CreateSwapChainForHwnd(queue, hwnd, &d, nullptr, nullptr, &sc1);
    if (FAILED(hr)) { printf("CreateSwapChainForHwnd failed 0x%08X\n", (unsigned)hr); return 1; }
    IDXGISwapChain3* sc = nullptr;
    if (FAILED(sc1->QueryInterface(IID_PPV_ARGS(&sc)))) { printf("no IDXGISwapChain3\n"); return 1; }
    DXGI_SWAP_CHAIN_DESC1 got = {};
    sc->GetDesc1(&got);
    printf("swapchain: the app sees format %d (%s)\n", (int)got.Format, got.Format == DXGI_FORMAT_R8G8B8A8_UNORM ? "R8G8B8A8, as it asked" : "CHANGED");

    bool ok = true;
    for (int i = 0; i < 30 && ok; i++)
    {
        if (i == 20) capture(capPath);
        ok = Frame(sc, w, h);
    }
    printf("state after 30 frames: %d (2 = HDR10 active, -1 = passing through)\n", state());
    if (ok)
    {
        w = 800; h = 450;
        hr = sc->ResizeBuffers(2, w, h, DXGI_FORMAT_UNKNOWN, DXGI_SWAP_CHAIN_FLAG_ALLOW_TEARING);
        printf("ResizeBuffers: 0x%08X\n", (unsigned)hr);
        for (int i = 0; i < 10 && ok && SUCCEEDED(hr); i++)
        {
            if (i == 5) capture(nullptr);
            ok = Frame(sc, w, h);
        }
    }
    WaitGpu();
    ULONG left = sc->Release();
    left = sc1->Release();
    printf("swapchain released (%lu references left)\n", left);
    static char log[32768];
    takeLog(log, sizeof log);
    printf("---- proxy log\n%s", log);
    HRESULT removed = device->GetDeviceRemovedReason();
    printf("---- device removed reason: 0x%08X; frames %s\n", (unsigned)removed, ok ? "ok" : "FAILED");
    DestroyWindow(hwnd);
    return ok && removed == S_OK ? 0 : 1;
}
