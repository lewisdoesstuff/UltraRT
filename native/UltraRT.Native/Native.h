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
