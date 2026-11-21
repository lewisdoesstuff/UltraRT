// Shared between the GPU timer (GpuTimer.cpp, owns the Unity entry points) and DLSS Ray Reconstruction (Dlss.cpp).
#pragma once

#include "IUnityInterface.h"
#include "IUnityGraphics.h"

// GpuTimer.cpp: the interfaces Unity handed to UnityPluginLoad (null before UltraRT_Init).
IUnityInterfaces* Native_UnityInterfaces();

// Dlss.cpp: called from GpuTimer.cpp's device callback / render event / unload.
void Dlss_OnDeviceEvent(UnityGfxDeviceEventType type);
bool Dlss_OnRenderEvent(int eventId, void* data); // true if the event was a DLSS event
void Dlss_Unload();

// Hdr.cpp (HDR10 swapchain proxy, docs/hdr.md): called from GpuTimer.cpp's device callback / render event.
void Hdr_OnDeviceEvent(UnityGfxDeviceEventType type);
bool Hdr_OnRenderEvent(int eventId, void* data); // true if the event was the HDR frame event

// RayQuery.cpp (D3D12 cs_6_5 inline RayQuery bridge, Wave 3): called from GpuTimer.cpp.
struct ID3D12GraphicsCommandList;
void RayQuery_OnDeviceEvent(UnityGfxDeviceEventType type);
bool RayQuery_OnRenderEvent(int eventId, void* data);
void RayQuery_OnRtasBuild(ID3D12GraphicsCommandList* cl, bool begin);
void RayQuery_Unload();
