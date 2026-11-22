// UltraRT.Native: D3D12 cs_6_5 inline RayQuery bridge (Wave 3).
//
// Captures Unity's scene TLAS GPU virtual address by hooking ID3D12GraphicsCommandList4::BuildRaytracingAccelerationStructure
// (vtable slot 72) during GpuTimer's SlotRtas (slot 1), then dispatches compiled DXIL cs_6_5 RayQuery compute shaders
// (VolumetricsRQ, ParticleLightRQ) directly on Unity's command list with zero shader-table rebuilds.

#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <d3d12.h>
#include <dxgi.h>
#include <stdarg.h>
#include <stddef.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include "IUnityInterface.h"
#include "IUnityGraphics.h"
#include "IUnityGraphicsD3D12.h"
#include "Native.h"

#include "obj/VolumetricsRQ_dxil.h"
#include "obj/ParticleLightRQ_dxil.h"

#define ULTRART_API extern "C" __declspec(dllexport)

namespace
{
    constexpr int kEventBase = 100; // GpuTimer 91-93, DLSS 94-96, HDR 97
    constexpr int kEventVol       = kEventBase + 0;
    constexpr int kEventPl        = kEventBase + 1;
    constexpr int kEventRtasBegin = kEventBase + 2;
    constexpr int kEventRtasEnd   = kEventBase + 3;

    constexpr int kFrameRing = 16;
    constexpr int kDescRing  = 64;
    constexpr int kSrvCount  = 13; // t1..t13
    constexpr int kUavCount  = 2;  // u0..u1
    constexpr int kDescPerSlot = kSrvCount + kUavCount;
    constexpr int kCbStride  = 1024; // 256-byte aligned, fits both VolCB (272 B) and PlCB (608 B)

    // Verified directly from Windows SDK 10.0.26100.0 d3d12.h ID3D12GraphicsCommandList4Vtbl.
    constexpr size_t kBuildRtasVtblSlot = 72;

    using BuildRtasFn = void (STDMETHODCALLTYPE*)(
        ID3D12GraphicsCommandList4*,
        const D3D12_BUILD_RAYTRACING_ACCELERATION_STRUCTURE_DESC*,
        UINT,
        const D3D12_RAYTRACING_ACCELERATION_STRUCTURE_POSTBUILD_INFO_DESC*);

    struct D3D12Api
    {
        ID3D12Device* (UNITY_INTERFACE_API* GetDevice)();
        void (UNITY_INTERFACE_API* ConfigureEvent)(int, const UnityD3D12PluginEventConfig*);
        bool (UNITY_INTERFACE_API* CommandRecordingState)(UnityGraphicsD3D12RecordingState*);
    };

    // Must match RayQueryBridge.cs VolRqFrame byte-for-byte.
    struct VolRqFrame
    {
        ID3D12Resource* depthTex;
        ID3D12Resource* lightsBuf;
        ID3D12Resource* skipBuf;
        ID3D12Resource* coneBuf;
        ID3D12Resource* beamsBuf;
        ID3D12Resource* emTrisBuf;
        ID3D12Resource* emOwnersBuf;
        ID3D12Resource* emAliasBuf;
        ID3D12Resource* emActiveBuf;
        ID3D12Resource* outTex;
        ID3D12Resource* beamOutTex;
        int width, height, pad0, pad1;
        // VolCB (272 bytes = 17 float4s)
        float invViewProj[16];
        float camPos[4];
        float dim[4];
        float medium[4];
        float volParams[4];
        float sunDir[4];
        float sunColor[4];
        float lightParams[4];
        float falloff[4];
        float lightShape[4];
        float samples[4];
        float emissiveParams[4];
        float misc[4];
    };

    // Must match RayQueryBridge.cs PlRqFrame byte-for-byte.
    struct PlRqFrame
    {
        ID3D12Resource* lightsBuf;
        ID3D12Resource* lightMasksBuf;
        ID3D12Resource* lightAliasBuf;
        ID3D12Resource* fillLightsBuf;
        ID3D12Resource* emTrisBuf;
        ID3D12Resource* emOwnersBuf;
        ID3D12Resource* emAliasBuf;
        ID3D12Resource* emActiveBuf;
        ID3D12Resource* cacheKeysBuf;
        ID3D12Resource* cacheResolvedBuf;
        ID3D12Resource* skyTex;
        ID3D12Resource* portalQuadsBuf;
        ID3D12Resource* portalTravelsBuf;
        ID3D12Resource* outTex;
        int gridX, gridY, gridZ, pad0;
        // PlCB (608 bytes = 38 float4/uint4s)
        float invViewProj[16];
        float camPos[4];
        float grid[4];
        float depth[4];
        float camFwd[4];
        float plParams[4];
        float ambient[4];
        float relight[4];
        float skyLight[4];
        float skyColor[4];
        float skyTint[4];
        float skyParams[4];
        float emissiveParams[4];
        float cacheGrid[4];
        float cacheQuery[4];
        uint32_t int0[4];
        uint32_t int1[4];
        uint32_t int2[4];
        float cacheViews[16 * 4];
    };

    D3D12Api s_Api = {};
    IUnityGraphicsD3D12* s_V1 = nullptr;
    bool s_HaveApi = false;

    SRWLOCK s_Lock = SRWLOCK_INIT;
    char s_Status[512] = "not initialized";
    char s_StatusOut[512];
    volatile LONG s_Ready = 0;
    volatile LONG64 s_TlasCaptures = 0;
    volatile LONG64 s_VolDispatches = 0;
    volatile LONG64 s_PlDispatches = 0;

    // Render-thread TLAS capture state
    volatile LONG s_InRtasBuild = 0;
    D3D12_GPU_VIRTUAL_ADDRESS s_SceneTlasVA = 0;
    BuildRtasFn s_OrigBuildRtas = nullptr;
    void** s_HookedVtbl = nullptr;

    // Render-thread D3D12 objects
    ID3D12Device* s_Device = nullptr;
    ID3D12RootSignature* s_RootSig = nullptr;
    ID3D12PipelineState* s_VolPso = nullptr;
    ID3D12PipelineState* s_PlPso = nullptr;
    ID3D12DescriptorHeap* s_Heap = nullptr;
    ID3D12Resource* s_CbBuf = nullptr;
    uint8_t* s_CbMapped = nullptr;
    UINT s_DescInc = 0;
    int s_DescCur = 0;
    bool s_RtInit = false, s_RtFailed = false;

    VolRqFrame s_VolFrames[kFrameRing] = {};
    PlRqFrame  s_PlFrames[kFrameRing] = {};

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
        OutputDebugStringA("[UltraRT.RayQuery] ");
        OutputDebugStringA(buf);
        OutputDebugStringA("\n");
    }

    // Diagnostics: which threads record acceleration structure builds, and whether two ever overlap.
    constexpr int kMaxBuildThreads = 8;
    volatile LONG s_BuildThreadIds[kMaxBuildThreads] = {};
    volatile LONG64 s_BuildThreadBlas[kMaxBuildThreads] = {};
    volatile LONG64 s_BuildThreadTlas[kMaxBuildThreads] = {};
    volatile LONG s_BuildsInFlight = 0;
    volatile LONG64 s_BuildOverlaps = 0;
    char s_BuildThreadsOut[512];

    void NoteBuildThread(bool tlas)
    {
        LONG tid = (LONG)GetCurrentThreadId();
        for (int i = 0; i < kMaxBuildThreads; i++)
        {
            LONG cur = s_BuildThreadIds[i];
            if (cur == 0) cur = InterlockedCompareExchange(&s_BuildThreadIds[i], tid, 0) == 0 ? tid : s_BuildThreadIds[i];
            if (cur == tid)
            {
                InterlockedIncrement64(tlas ? &s_BuildThreadTlas[i] : &s_BuildThreadBlas[i]);
                return;
            }
        }
    }

    void STDMETHODCALLTYPE HookedBuildRtas(
        ID3D12GraphicsCommandList4* self,
        const D3D12_BUILD_RAYTRACING_ACCELERATION_STRUCTURE_DESC* pDesc,
        UINT numPostbuild,
        const D3D12_RAYTRACING_ACCELERATION_STRUCTURE_POSTBUILD_INFO_DESC* pPostbuild)
    {
        if (InterlockedIncrement(&s_BuildsInFlight) > 1) InterlockedIncrement64(&s_BuildOverlaps);
        NoteBuildThread(pDesc && pDesc->Inputs.Type == D3D12_RAYTRACING_ACCELERATION_STRUCTURE_TYPE_TOP_LEVEL);
        if (s_InRtasBuild && pDesc && pDesc->Inputs.Type == D3D12_RAYTRACING_ACCELERATION_STRUCTURE_TYPE_TOP_LEVEL)
        {
            s_SceneTlasVA = pDesc->DestAccelerationStructureData;
            InterlockedIncrement64(&s_TlasCaptures);
        }
        s_OrigBuildRtas(self, pDesc, numPostbuild, pPostbuild);
        InterlockedDecrement(&s_BuildsInFlight);
    }

    void EnsureBuildRtasHook(ID3D12GraphicsCommandList* cl)
    {
        if (s_OrigBuildRtas || !cl) return;
        ID3D12GraphicsCommandList4* cl4 = nullptr;
        if (FAILED(cl->QueryInterface(IID_PPV_ARGS(&cl4))) || !cl4) return;
        void** vtbl = *reinterpret_cast<void***>(cl4);
        if (vtbl && vtbl[kBuildRtasVtblSlot])
        {
            DWORD oldProt = 0;
            if (VirtualProtect(&vtbl[kBuildRtasVtblSlot], sizeof(void*), PAGE_EXECUTE_READWRITE, &oldProt))
            {
                s_OrigBuildRtas = reinterpret_cast<BuildRtasFn>(vtbl[kBuildRtasVtblSlot]);
                s_HookedVtbl = vtbl;
                InterlockedExchangePointer(&vtbl[kBuildRtasVtblSlot], reinterpret_cast<void*>(&HookedBuildRtas));
                VirtualProtect(&vtbl[kBuildRtasVtblSlot], sizeof(void*), oldProt, &oldProt);
            }
        }
        cl4->Release();
    }

    void RemoveBuildRtasHook()
    {
        if (s_HookedVtbl && s_OrigBuildRtas)
        {
            DWORD oldProt = 0;
            if (VirtualProtect(&s_HookedVtbl[kBuildRtasVtblSlot], sizeof(void*), PAGE_EXECUTE_READWRITE, &oldProt))
            {
                InterlockedExchangePointer(&s_HookedVtbl[kBuildRtasVtblSlot], reinterpret_cast<void*>(s_OrigBuildRtas));
                VirtualProtect(&s_HookedVtbl[kBuildRtasVtblSlot], sizeof(void*), oldProt, &oldProt);
            }
        }
        s_HookedVtbl = nullptr;
        s_OrigBuildRtas = nullptr;
        s_SceneTlasVA = 0;
    }

    void ReleaseResources()
    {
        InterlockedExchange(&s_Ready, 0);
        if (s_CbBuf && s_CbMapped) s_CbBuf->Unmap(0, nullptr);
        s_CbMapped = nullptr;
        if (s_CbBuf) s_CbBuf->Release(); s_CbBuf = nullptr;
        if (s_Heap) s_Heap->Release(); s_Heap = nullptr;
        if (s_PlPso) s_PlPso->Release(); s_PlPso = nullptr;
        if (s_VolPso) s_VolPso->Release(); s_VolPso = nullptr;
        if (s_RootSig) s_RootSig->Release(); s_RootSig = nullptr;
        s_Device = nullptr;
        s_SceneTlasVA = 0;
        s_RtInit = s_RtFailed = false;
    }

    bool RenderThreadInit()
    {
        s_RtFailed = true;
        if (!s_HaveApi) { SetStatus("no D3D12 API"); return false; }
        s_Device = s_Api.GetDevice();
        if (!s_Device) { SetStatus("null D3D12 device"); return false; }

        D3D12_FEATURE_DATA_D3D12_OPTIONS5 opt5 = {};
        if (FAILED(s_Device->CheckFeatureSupport(D3D12_FEATURE_D3D12_OPTIONS5, &opt5, sizeof opt5)) ||
            opt5.RaytracingTier < D3D12_RAYTRACING_TIER_1_1)
        {
            SetStatus("DXR Tier 1.1 (RayQuery) not supported (tier=%d)", (int)opt5.RaytracingTier);
            return false;
        }

        // Root signature:
        //   0: Root SRV t0 (inline TLAS GPU VA)
        //   1: Root CBV b0 (inline constant buffer GPU VA)
        //   2: Descriptor table t1..t13 (13 SRVs)
        //   3: Descriptor table u0..u1 (2 UAVs)
        //   Static sampler s0 (linear wrap U, clamp V for lat-long sky)
        D3D12_DESCRIPTOR_RANGE srvRange = {};
        srvRange.RangeType = D3D12_DESCRIPTOR_RANGE_TYPE_SRV;
        srvRange.NumDescriptors = kSrvCount;
        srvRange.BaseShaderRegister = 1;
        srvRange.RegisterSpace = 0;
        srvRange.OffsetInDescriptorsFromTableStart = 0;

        D3D12_DESCRIPTOR_RANGE uavRange = {};
        uavRange.RangeType = D3D12_DESCRIPTOR_RANGE_TYPE_UAV;
        uavRange.NumDescriptors = kUavCount;
        uavRange.BaseShaderRegister = 0;
        uavRange.RegisterSpace = 0;
        uavRange.OffsetInDescriptorsFromTableStart = 0;

        D3D12_ROOT_PARAMETER params[4] = {};
        params[0].ParameterType = D3D12_ROOT_PARAMETER_TYPE_SRV;
        params[0].Descriptor.ShaderRegister = 0;
        params[0].Descriptor.RegisterSpace = 0;
        params[0].ShaderVisibility = D3D12_SHADER_VISIBILITY_ALL;

        params[1].ParameterType = D3D12_ROOT_PARAMETER_TYPE_CBV;
        params[1].Descriptor.ShaderRegister = 0;
        params[1].Descriptor.RegisterSpace = 0;
        params[1].ShaderVisibility = D3D12_SHADER_VISIBILITY_ALL;

        params[2].ParameterType = D3D12_ROOT_PARAMETER_TYPE_DESCRIPTOR_TABLE;
        params[2].DescriptorTable.NumDescriptorRanges = 1;
        params[2].DescriptorTable.pDescriptorRanges = &srvRange;
        params[2].ShaderVisibility = D3D12_SHADER_VISIBILITY_ALL;

        params[3].ParameterType = D3D12_ROOT_PARAMETER_TYPE_DESCRIPTOR_TABLE;
        params[3].DescriptorTable.NumDescriptorRanges = 1;
        params[3].DescriptorTable.pDescriptorRanges = &uavRange;
        params[3].ShaderVisibility = D3D12_SHADER_VISIBILITY_ALL;

        D3D12_STATIC_SAMPLER_DESC samp = {};
        samp.Filter = D3D12_FILTER_MIN_MAG_MIP_LINEAR;
        samp.AddressU = D3D12_TEXTURE_ADDRESS_MODE_WRAP;
        samp.AddressV = D3D12_TEXTURE_ADDRESS_MODE_CLAMP;
        samp.AddressW = D3D12_TEXTURE_ADDRESS_MODE_CLAMP;
        samp.MaxLOD = D3D12_FLOAT32_MAX;
        samp.ShaderRegister = 0;
        samp.ShaderVisibility = D3D12_SHADER_VISIBILITY_ALL;

        D3D12_ROOT_SIGNATURE_DESC rsd = {};
        rsd.NumParameters = 4;
        rsd.pParameters = params;
        rsd.NumStaticSamplers = 1;
        rsd.pStaticSamplers = &samp;
        rsd.Flags = D3D12_ROOT_SIGNATURE_FLAG_NONE;

        ID3DBlob* sigBlob = nullptr;
        ID3DBlob* errBlob = nullptr;
        HRESULT hr = D3D12SerializeRootSignature(&rsd, D3D_ROOT_SIGNATURE_VERSION_1, &sigBlob, &errBlob);
        if (FAILED(hr))
        {
            SetStatus("SerializeRootSignature failed (hr=0x%08X): %s", (unsigned)hr,
                      errBlob ? (const char*)errBlob->GetBufferPointer() : "");
            if (errBlob) errBlob->Release();
            return false;
        }
        hr = s_Device->CreateRootSignature(0, sigBlob->GetBufferPointer(), sigBlob->GetBufferSize(), IID_PPV_ARGS(&s_RootSig));
        sigBlob->Release();
        if (errBlob) errBlob->Release();
        if (FAILED(hr)) { SetStatus("CreateRootSignature failed (hr=0x%08X)", (unsigned)hr); return false; }

        D3D12_COMPUTE_PIPELINE_STATE_DESC psoDesc = {};
        psoDesc.pRootSignature = s_RootSig;
        psoDesc.CS.pShaderBytecode = g_VolumetricsRQ_dxil;
        psoDesc.CS.BytecodeLength = sizeof(g_VolumetricsRQ_dxil);
        hr = s_Device->CreateComputePipelineState(&psoDesc, IID_PPV_ARGS(&s_VolPso));
        if (FAILED(hr)) { SetStatus("CreateComputePipelineState(VolumetricsRQ) failed (hr=0x%08X)", (unsigned)hr); return false; }
        s_VolPso->SetName(L"UltraRT VolumetricsRQ PSO");

        psoDesc.CS.pShaderBytecode = g_ParticleLightRQ_dxil;
        psoDesc.CS.BytecodeLength = sizeof(g_ParticleLightRQ_dxil);
        hr = s_Device->CreateComputePipelineState(&psoDesc, IID_PPV_ARGS(&s_PlPso));
        if (FAILED(hr)) { SetStatus("CreateComputePipelineState(ParticleLightRQ) failed (hr=0x%08X)", (unsigned)hr); return false; }
        s_PlPso->SetName(L"UltraRT ParticleLightRQ PSO");

        D3D12_DESCRIPTOR_HEAP_DESC hd = {};
        hd.Type = D3D12_DESCRIPTOR_HEAP_TYPE_CBV_SRV_UAV;
        hd.NumDescriptors = kDescRing * kDescPerSlot;
        hd.Flags = D3D12_DESCRIPTOR_HEAP_FLAG_SHADER_VISIBLE;
        hr = s_Device->CreateDescriptorHeap(&hd, IID_PPV_ARGS(&s_Heap));
        if (FAILED(hr)) { SetStatus("CreateDescriptorHeap failed (hr=0x%08X)", (unsigned)hr); return false; }
        s_Heap->SetName(L"UltraRT RayQuery Heap");
        s_DescInc = s_Device->GetDescriptorHandleIncrementSize(D3D12_DESCRIPTOR_HEAP_TYPE_CBV_SRV_UAV);

        D3D12_HEAP_PROPERTIES hp = {};
        hp.Type = D3D12_HEAP_TYPE_UPLOAD;
        D3D12_RESOURCE_DESC rd = {};
        rd.Dimension = D3D12_RESOURCE_DIMENSION_BUFFER;
        rd.Width = (UINT64)kDescRing * kCbStride;
        rd.Height = 1; rd.DepthOrArraySize = 1; rd.MipLevels = 1;
        rd.SampleDesc.Count = 1;
        rd.Layout = D3D12_TEXTURE_LAYOUT_ROW_MAJOR;
        hr = s_Device->CreateCommittedResource(&hp, D3D12_HEAP_FLAG_NONE, &rd, D3D12_RESOURCE_STATE_GENERIC_READ,
                                               nullptr, IID_PPV_ARGS(&s_CbBuf));
        if (FAILED(hr)) { SetStatus("CreateCommittedResource(CB) failed (hr=0x%08X)", (unsigned)hr); return false; }
        s_CbBuf->SetName(L"UltraRT RayQuery CB Ring");
        D3D12_RANGE zeroRange = { 0, 0 };
        void* p = nullptr;
        hr = s_CbBuf->Map(0, &zeroRange, &p);
        if (FAILED(hr)) { SetStatus("CB Map failed (hr=0x%08X)", (unsigned)hr); return false; }
        s_CbMapped = static_cast<uint8_t*>(p);

        s_RtFailed = false;
        s_RtInit = true;
        InterlockedExchange(&s_Ready, 1);
        SetStatus("ready: DXR Tier 1.1 RayQuery (VolumetricsRQ + ParticleLightRQ, %d ring slots)", kDescRing);
        return true;
    }

    DXGI_FORMAT ResolveFormat(DXGI_FORMAT f)
    {
        switch (f)
        {
            case DXGI_FORMAT_R32_TYPELESS:           return DXGI_FORMAT_R32_FLOAT;
            case DXGI_FORMAT_R16G16B16A16_TYPELESS:  return DXGI_FORMAT_R16G16B16A16_FLOAT;
            case DXGI_FORMAT_R8G8B8A8_TYPELESS:      return DXGI_FORMAT_R8G8B8A8_UNORM;
            case DXGI_FORMAT_B8G8R8A8_TYPELESS:      return DXGI_FORMAT_B8G8R8A8_UNORM;
            case DXGI_FORMAT_R32G32B32A32_TYPELESS:  return DXGI_FORMAT_R32G32B32A32_FLOAT;
            case DXGI_FORMAT_R16G16_TYPELESS:        return DXGI_FORMAT_R16G16_FLOAT;
            case DXGI_FORMAT_R16_TYPELESS:           return DXGI_FORMAT_R16_FLOAT;
            case DXGI_FORMAT_BC1_TYPELESS:           return DXGI_FORMAT_BC1_UNORM;
            case DXGI_FORMAT_BC3_TYPELESS:           return DXGI_FORMAT_BC3_UNORM;
            case DXGI_FORMAT_BC7_TYPELESS:           return DXGI_FORMAT_BC7_UNORM;
            default:                                 return f;
        }
    }

    void WriteBufSrv(D3D12_CPU_DESCRIPTOR_HANDLE h, ID3D12Resource* res, UINT stride)
    {
        D3D12_SHADER_RESOURCE_VIEW_DESC d = {};
        d.Format = DXGI_FORMAT_UNKNOWN;
        d.ViewDimension = D3D12_SRV_DIMENSION_BUFFER;
        d.Shader4ComponentMapping = D3D12_DEFAULT_SHADER_4_COMPONENT_MAPPING;
        d.Buffer.StructureByteStride = stride;
        if (res)
        {
            D3D12_RESOURCE_DESC rd = res->GetDesc();
            d.Buffer.NumElements = (UINT)(rd.Width / stride);
            if (d.Buffer.NumElements == 0) d.Buffer.NumElements = 1;
            s_Device->CreateShaderResourceView(res, &d, h);
        }
        else
        {
            d.Buffer.NumElements = 1;
            s_Device->CreateShaderResourceView(nullptr, &d, h);
        }
    }

    void WriteTex2DSrv(D3D12_CPU_DESCRIPTOR_HANDLE h, ID3D12Resource* res, DXGI_FORMAT fallbackFmt)
    {
        D3D12_SHADER_RESOURCE_VIEW_DESC d = {};
        d.ViewDimension = D3D12_SRV_DIMENSION_TEXTURE2D;
        d.Shader4ComponentMapping = D3D12_DEFAULT_SHADER_4_COMPONENT_MAPPING;
        if (res)
        {
            D3D12_RESOURCE_DESC rd = res->GetDesc();
            d.Format = ResolveFormat(rd.Format);
            d.Texture2D.MipLevels = rd.MipLevels ? rd.MipLevels : 1;
            s_Device->CreateShaderResourceView(res, &d, h);
        }
        else
        {
            d.Format = fallbackFmt;
            d.Texture2D.MipLevels = 1;
            s_Device->CreateShaderResourceView(nullptr, &d, h);
        }
    }

    void WriteTexUav(D3D12_CPU_DESCRIPTOR_HANDLE h, ID3D12Resource* res, bool is3D)
    {
        D3D12_UNORDERED_ACCESS_VIEW_DESC d = {};
        if (res)
        {
            D3D12_RESOURCE_DESC rd = res->GetDesc();
            d.Format = ResolveFormat(rd.Format);
            if (is3D)
            {
                d.ViewDimension = D3D12_UAV_DIMENSION_TEXTURE3D;
                d.Texture3D.MipSlice = 0;
                d.Texture3D.FirstWSlice = 0;
                d.Texture3D.WSize = rd.DepthOrArraySize;
            }
            else
            {
                d.ViewDimension = D3D12_UAV_DIMENSION_TEXTURE2D;
                d.Texture2D.MipSlice = 0;
            }
            s_Device->CreateUnorderedAccessView(res, nullptr, &d, h);
        }
        else
        {
            d.Format = DXGI_FORMAT_R16G16B16A16_FLOAT;
            d.ViewDimension = is3D ? D3D12_UAV_DIMENSION_TEXTURE3D : D3D12_UAV_DIMENSION_TEXTURE2D;
            s_Device->CreateUnorderedAccessView(nullptr, nullptr, &d, h);
        }
    }

    struct TrackedRes
    {
        ID3D12Resource* res;
        D3D12_RESOURCE_STATES orig;
        D3D12_RESOURCE_STATES want;
    };

    void PushUnique(TrackedRes* list, int& count, int maxCount, ID3D12Resource* res, D3D12_RESOURCE_STATES want)
    {
        if (!res || count >= maxCount) return;
        for (int i = 0; i < count; i++) if (list[i].res == res) return;
        D3D12_RESOURCE_STATES orig = want;
        if (s_V1) s_V1->GetResourceState(res, &orig);
        list[count++] = { res, orig, want };
    }

    void TransitionAll(ID3D12GraphicsCommandList* cl, const TrackedRes* list, int count, bool restore)
    {
        D3D12_RESOURCE_BARRIER b[16] = {};
        int nb = 0;
        for (int i = 0; i < count; i++)
        {
            D3D12_RESOURCE_STATES from = restore ? list[i].want : list[i].orig;
            D3D12_RESOURCE_STATES to   = restore ? list[i].orig : list[i].want;
            if (from == to) continue;
            b[nb].Type = D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;
            b[nb].Transition.pResource = list[i].res;
            b[nb].Transition.Subresource = D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES;
            b[nb].Transition.StateBefore = from;
            b[nb].Transition.StateAfter = to;
            nb++;
        }
        if (nb > 0) cl->ResourceBarrier((UINT)nb, b);
    }

    void DispatchVol(const VolRqFrame& f)
    {
        if (!s_SceneTlasVA || !f.outTex || !f.beamOutTex || !f.depthTex || f.width <= 0 || f.height <= 0) return;
        UnityGraphicsD3D12RecordingState rs = {};
        if (!s_Api.CommandRecordingState(&rs) || !rs.commandList) return;
        ID3D12GraphicsCommandList* cl = rs.commandList;

        int ring = s_DescCur;
        s_DescCur = (s_DescCur + 1) % kDescRing;

        // Upload CB
        static_assert(sizeof(VolRqFrame) - offsetof(VolRqFrame, invViewProj) <= kCbStride, "VolCB exceeds kCbStride");
        memcpy(s_CbMapped + (size_t)ring * kCbStride, f.invViewProj, sizeof(VolRqFrame) - offsetof(VolRqFrame, invViewProj));
        D3D12_GPU_VIRTUAL_ADDRESS cbVA = s_CbBuf->GetGPUVirtualAddress() + (UINT64)ring * kCbStride;

        // Fill descriptor table
        D3D12_CPU_DESCRIPTOR_HANDLE cpuBase = s_Heap->GetCPUDescriptorHandleForHeapStart();
        D3D12_GPU_DESCRIPTOR_HANDLE gpuBase = s_Heap->GetGPUDescriptorHandleForHeapStart();
        SIZE_T slotOff = (SIZE_T)ring * kDescPerSlot * s_DescInc;
        auto CpuAt = [&](int idx) { D3D12_CPU_DESCRIPTOR_HANDLE h = { cpuBase.ptr + slotOff + (SIZE_T)idx * s_DescInc }; return h; };
        auto GpuAt = [&](int idx) { D3D12_GPU_DESCRIPTOR_HANDLE h = { gpuBase.ptr + slotOff + (UINT64)idx * s_DescInc }; return h; };

        WriteTex2DSrv(CpuAt(0), f.depthTex, DXGI_FORMAT_R32_FLOAT);      // t1
        WriteBufSrv(CpuAt(1), f.lightsBuf, 16);                           // t2
        WriteBufSrv(CpuAt(2), f.skipBuf, 4);                              // t3
        WriteBufSrv(CpuAt(3), f.coneBuf, 16);                             // t4
        WriteBufSrv(CpuAt(4), f.beamsBuf, 16);                            // t5
        WriteBufSrv(CpuAt(5), f.emTrisBuf, 16);                           // t6
        WriteBufSrv(CpuAt(6), f.emOwnersBuf, 16);                         // t7
        WriteBufSrv(CpuAt(7), f.emAliasBuf, 16);                          // t8
        WriteBufSrv(CpuAt(8), f.emActiveBuf, 4);                          // t9
        WriteBufSrv(CpuAt(9), nullptr, 16);                               // t10
        WriteTex2DSrv(CpuAt(10), nullptr, DXGI_FORMAT_R8G8B8A8_UNORM);    // t11
        WriteBufSrv(CpuAt(11), nullptr, 16);                              // t12
        WriteBufSrv(CpuAt(12), nullptr, 16);                              // t13
        WriteTexUav(CpuAt(13), f.outTex, false);                          // u0
        WriteTexUav(CpuAt(14), f.beamOutTex, false);                      // u1

        TrackedRes tracked[16];
        int nt = 0;
        const D3D12_RESOURCE_STATES kSrv = D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE;
        const D3D12_RESOURCE_STATES kUav = D3D12_RESOURCE_STATE_UNORDERED_ACCESS;
        PushUnique(tracked, nt, 16, f.depthTex, kSrv);
        PushUnique(tracked, nt, 16, f.lightsBuf, kSrv);
        PushUnique(tracked, nt, 16, f.skipBuf, kSrv);
        PushUnique(tracked, nt, 16, f.coneBuf, kSrv);
        PushUnique(tracked, nt, 16, f.beamsBuf, kSrv);
        PushUnique(tracked, nt, 16, f.emTrisBuf, kSrv);
        PushUnique(tracked, nt, 16, f.emOwnersBuf, kSrv);
        PushUnique(tracked, nt, 16, f.emAliasBuf, kSrv);
        PushUnique(tracked, nt, 16, f.emActiveBuf, kSrv);
        PushUnique(tracked, nt, 16, f.outTex, kUav);
        PushUnique(tracked, nt, 16, f.beamOutTex, kUav);

        TransitionAll(cl, tracked, nt, false);

        ID3D12DescriptorHeap* heaps[] = { s_Heap };
        cl->SetDescriptorHeaps(1, heaps);
        cl->SetComputeRootSignature(s_RootSig);
        cl->SetPipelineState(s_VolPso);
        cl->SetComputeRootShaderResourceView(0, s_SceneTlasVA);
        cl->SetComputeRootConstantBufferView(1, cbVA);
        cl->SetComputeRootDescriptorTable(2, GpuAt(0));
        cl->SetComputeRootDescriptorTable(3, GpuAt(kSrvCount));
        cl->Dispatch((UINT)((f.width + 7) / 8), (UINT)((f.height + 7) / 8), 1);

        D3D12_RESOURCE_BARRIER uavB[2] = {};
        uavB[0].Type = D3D12_RESOURCE_BARRIER_TYPE_UAV; uavB[0].UAV.pResource = f.outTex;
        uavB[1].Type = D3D12_RESOURCE_BARRIER_TYPE_UAV; uavB[1].UAV.pResource = f.beamOutTex;
        cl->ResourceBarrier(2, uavB);

        TransitionAll(cl, tracked, nt, true);
        InterlockedIncrement64(&s_VolDispatches);
    }

    void DispatchPl(const PlRqFrame& f)
    {
        if (!s_SceneTlasVA || !f.outTex || f.gridX <= 0 || f.gridY <= 0 || f.gridZ <= 0) return;
        UnityGraphicsD3D12RecordingState rs = {};
        if (!s_Api.CommandRecordingState(&rs) || !rs.commandList) return;
        ID3D12GraphicsCommandList* cl = rs.commandList;

        int ring = s_DescCur;
        s_DescCur = (s_DescCur + 1) % kDescRing;

        // Upload CB
        static_assert(sizeof(PlRqFrame) - offsetof(PlRqFrame, invViewProj) <= kCbStride, "PlCB exceeds kCbStride");
        memcpy(s_CbMapped + (size_t)ring * kCbStride, f.invViewProj, sizeof(PlRqFrame) - offsetof(PlRqFrame, invViewProj));
        D3D12_GPU_VIRTUAL_ADDRESS cbVA = s_CbBuf->GetGPUVirtualAddress() + (UINT64)ring * kCbStride;

        D3D12_CPU_DESCRIPTOR_HANDLE cpuBase = s_Heap->GetCPUDescriptorHandleForHeapStart();
        D3D12_GPU_DESCRIPTOR_HANDLE gpuBase = s_Heap->GetGPUDescriptorHandleForHeapStart();
        SIZE_T slotOff = (SIZE_T)ring * kDescPerSlot * s_DescInc;
        auto CpuAt = [&](int idx) { D3D12_CPU_DESCRIPTOR_HANDLE h = { cpuBase.ptr + slotOff + (SIZE_T)idx * s_DescInc }; return h; };
        auto GpuAt = [&](int idx) { D3D12_GPU_DESCRIPTOR_HANDLE h = { gpuBase.ptr + slotOff + (UINT64)idx * s_DescInc }; return h; };

        WriteBufSrv(CpuAt(0), f.lightsBuf, 16);                         // t1
        WriteBufSrv(CpuAt(1), f.lightMasksBuf, 4);                      // t2
        WriteBufSrv(CpuAt(2), f.lightAliasBuf, 16);                     // t3
        WriteBufSrv(CpuAt(3), f.fillLightsBuf, 4);                      // t4
        WriteBufSrv(CpuAt(4), f.emTrisBuf, 16);                         // t5
        WriteBufSrv(CpuAt(5), f.emOwnersBuf, 16);                       // t6
        WriteBufSrv(CpuAt(6), f.emAliasBuf, 16);                        // t7
        WriteBufSrv(CpuAt(7), f.emActiveBuf, 4);                        // t8
        WriteBufSrv(CpuAt(8), f.cacheKeysBuf, 4);                       // t9
        WriteBufSrv(CpuAt(9), f.cacheResolvedBuf, 16);                  // t10
        WriteTex2DSrv(CpuAt(10), f.skyTex, DXGI_FORMAT_R8G8B8A8_UNORM); // t11
        WriteBufSrv(CpuAt(11), f.portalQuadsBuf, 16);                   // t12
        WriteBufSrv(CpuAt(12), f.portalTravelsBuf, 16);                 // t13
        WriteTexUav(CpuAt(13), f.outTex, true);                         // u0
        WriteTexUav(CpuAt(14), nullptr, false);                         // u1

        TrackedRes tracked[16];
        int nt = 0;
        const D3D12_RESOURCE_STATES kSrv = D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE;
        const D3D12_RESOURCE_STATES kUav = D3D12_RESOURCE_STATE_UNORDERED_ACCESS;
        PushUnique(tracked, nt, 16, f.lightsBuf, kSrv);
        PushUnique(tracked, nt, 16, f.lightMasksBuf, kSrv);
        PushUnique(tracked, nt, 16, f.lightAliasBuf, kSrv);
        PushUnique(tracked, nt, 16, f.fillLightsBuf, kSrv);
        PushUnique(tracked, nt, 16, f.emTrisBuf, kSrv);
        PushUnique(tracked, nt, 16, f.emOwnersBuf, kSrv);
        PushUnique(tracked, nt, 16, f.emAliasBuf, kSrv);
        PushUnique(tracked, nt, 16, f.emActiveBuf, kSrv);
        PushUnique(tracked, nt, 16, f.cacheKeysBuf, kSrv);
        PushUnique(tracked, nt, 16, f.cacheResolvedBuf, kSrv);
        PushUnique(tracked, nt, 16, f.skyTex, kSrv);
        PushUnique(tracked, nt, 16, f.portalQuadsBuf, kSrv);
        PushUnique(tracked, nt, 16, f.portalTravelsBuf, kSrv);
        PushUnique(tracked, nt, 16, f.outTex, kUav);

        TransitionAll(cl, tracked, nt, false);

        ID3D12DescriptorHeap* heaps[] = { s_Heap };
        cl->SetDescriptorHeaps(1, heaps);
        cl->SetComputeRootSignature(s_RootSig);
        cl->SetPipelineState(s_PlPso);
        cl->SetComputeRootShaderResourceView(0, s_SceneTlasVA);
        cl->SetComputeRootConstantBufferView(1, cbVA);
        cl->SetComputeRootDescriptorTable(2, GpuAt(0));
        cl->SetComputeRootDescriptorTable(3, GpuAt(kSrvCount));
        cl->Dispatch((UINT)((f.gridX + 3) / 4), (UINT)((f.gridY + 3) / 4), (UINT)((f.gridZ + 3) / 4));

        D3D12_RESOURCE_BARRIER uavB = {};
        uavB.Type = D3D12_RESOURCE_BARRIER_TYPE_UAV;
        uavB.UAV.pResource = f.outTex;
        cl->ResourceBarrier(1, &uavB);

        TransitionAll(cl, tracked, nt, true);
        InterlockedIncrement64(&s_PlDispatches);
    }
}

void RayQuery_OnRtasBuild(ID3D12GraphicsCommandList* cl, bool begin)
{
    if (begin)
    {
        EnsureBuildRtasHook(cl);
        InterlockedExchange(&s_InRtasBuild, 1);
    }
    else
    {
        InterlockedExchange(&s_InRtasBuild, 0);
    }
}

void RayQuery_OnDeviceEvent(UnityGfxDeviceEventType type)
{
    if (type == kUnityGfxDeviceEventShutdown)
    {
        RemoveBuildRtasHook();
        ReleaseResources();
        s_HaveApi = false;
        return;
    }
    if (type != kUnityGfxDeviceEventInitialize) return;
    ReleaseResources();
    s_HaveApi = false;
    IUnityInterfaces* u = Native_UnityInterfaces();
    if (!u) return;
    IUnityGraphics* g = u->Get<IUnityGraphics>();
    if (!g || g->GetRenderer() != kUnityGfxRendererD3D12) return;
    if (auto v7 = u->Get<IUnityGraphicsD3D12v7>())
    {
        s_Api = { v7->GetDevice, v7->ConfigureEvent, v7->CommandRecordingState };
        s_HaveApi = true;
    }
    else if (auto v6 = u->Get<IUnityGraphicsD3D12v6>())
    {
        s_Api = { v6->GetDevice, v6->ConfigureEvent, v6->CommandRecordingState };
        s_HaveApi = true;
    }
    s_V1 = u->Get<IUnityGraphicsD3D12>();
    if (!s_HaveApi) { SetStatus("IUnityGraphicsD3D12v6/v7 not available"); return; }

    UnityD3D12PluginEventConfig cfg = {};
    cfg.graphicsQueueAccess = kUnityD3D12GraphicsQueueAccess_DontCare;
    cfg.flags = kUnityD3D12EventConfigFlag_ModifiesCommandBuffersState;
    cfg.ensureActiveRenderTextureIsBound = false;
    s_Api.ConfigureEvent(kEventVol, &cfg);
    s_Api.ConfigureEvent(kEventPl, &cfg);
    cfg.flags = 0; // RTAS begin/end touch no command-buffer state
    s_Api.ConfigureEvent(kEventRtasBegin, &cfg);
    s_Api.ConfigureEvent(kEventRtasEnd, &cfg);
    SetStatus("D3D12 interfaces ready (v1 state query %s); waiting for first event", s_V1 ? "ok" : "none");
}

bool RayQuery_OnRenderEvent(int eventId, void* data)
{
    if (eventId < kEventBase || eventId > kEventRtasEnd) return false;
    if (!s_HaveApi) return true;
    if (eventId == kEventRtasBegin || eventId == kEventRtasEnd)
    {
        UnityGraphicsD3D12RecordingState rs = {};
        ID3D12GraphicsCommandList* cl = (s_Api.CommandRecordingState(&rs) && rs.commandList) ? rs.commandList : nullptr;
        RayQuery_OnRtasBuild(cl, eventId == kEventRtasBegin);
        return true;
    }
    if (!s_RtInit)
    {
        if (s_RtFailed || !RenderThreadInit()) return true;
    }
    int slot = (int)(intptr_t)data;
    if (slot < 0 || slot >= kFrameRing) return true;
    if (eventId == kEventVol)
    {
        VolRqFrame f = s_VolFrames[slot];
        DispatchVol(f);
    }
    else
    {
        PlRqFrame f = s_PlFrames[slot];
        DispatchPl(f);
    }
    return true;
}

void RayQuery_Unload()
{
    RemoveBuildRtasHook();
    ReleaseResources();
    s_HaveApi = false;
}

// ------------------------------------------------------------------ C API

ULTRART_API int UltraRT_RQ_GetEventBase() { return kEventBase; }
ULTRART_API int UltraRT_RQ_GetRingSize() { return kFrameRing; }
ULTRART_API int UltraRT_RQ_GetVolFrameSize() { return (int)sizeof(VolRqFrame); }
ULTRART_API int UltraRT_RQ_GetPlFrameSize() { return (int)sizeof(PlRqFrame); }
ULTRART_API int UltraRT_RQ_IsReady() { return s_HaveApi && !s_RtFailed ? 1 : 0; }
ULTRART_API int UltraRT_RQ_HasTlas() { return InterlockedCompareExchange64(&s_TlasCaptures, 0, 0) > 0 && s_SceneTlasVA != 0 ? 1 : 0; }
ULTRART_API long long UltraRT_RQ_GetVolDispatches() { return InterlockedCompareExchange64(&s_VolDispatches, 0, 0); }
ULTRART_API long long UltraRT_RQ_GetPlDispatches() { return InterlockedCompareExchange64(&s_PlDispatches, 0, 0); }

ULTRART_API const char* UltraRT_RQ_GetStatus()
{
    AcquireSRWLockShared(&s_Lock);
    memcpy(s_StatusOut, s_Status, sizeof s_Status);
    ReleaseSRWLockShared(&s_Lock);
    return s_StatusOut;
}

// "tid:blas/tlas ..., overlaps N" for the threads that recorded acceleration structure builds (diagnostics).
ULTRART_API const char* UltraRT_RQ_GetBuildThreads()
{
    int n = 0;
    s_BuildThreadsOut[0] = 0;
    for (int i = 0; i < kMaxBuildThreads && n < (int)sizeof s_BuildThreadsOut - 64; i++)
    {
        LONG tid = s_BuildThreadIds[i];
        if (tid == 0) break;
        n += snprintf(s_BuildThreadsOut + n, sizeof s_BuildThreadsOut - n, "%s%ld:%lld/%lld", i ? " " : "", (long)tid,
                      (long long)s_BuildThreadBlas[i], (long long)s_BuildThreadTlas[i]);
    }
    snprintf(s_BuildThreadsOut + n, sizeof s_BuildThreadsOut - n, "%soverlaps %lld", n ? ", " : "", (long long)s_BuildOverlaps);
    return s_BuildThreadsOut;
}

ULTRART_API void UltraRT_RQ_SetVolFrame(int slot, const VolRqFrame* f)
{
    if (slot < 0 || slot >= kFrameRing || !f) return;
    s_VolFrames[slot] = *f;
}

ULTRART_API void UltraRT_RQ_SetPlFrame(int slot, const PlRqFrame* f)
{
    if (slot < 0 || slot >= kFrameRing || !f) return;
    s_PlFrames[slot] = *f;
}
