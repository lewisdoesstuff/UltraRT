// Built instead of Dlss.cpp when the DLSS SDK is not in lib\dlss (scripts\fetch-dlss.ps1): DLSS Ray Reconstruction is
// then reported unavailable and every DLSS event is ignored.
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include "Native.h"

#define ULTRART_API extern "C" __declspec(dllexport)

void Dlss_OnDeviceEvent(UnityGfxDeviceEventType) {}
bool Dlss_OnRenderEvent(int eventId, void*) { return eventId >= 94 && eventId <= 96; }
void Dlss_Unload() {}

ULTRART_API int UltraRT_Dlss_Configure(const wchar_t*, const wchar_t*) { return 0; }
ULTRART_API int UltraRT_Dlss_GetEventBase() { return 94; }
ULTRART_API int UltraRT_Dlss_GetRingSize() { return 0; }
ULTRART_API int UltraRT_Dlss_GetFrameSize() { return 0; }
ULTRART_API void UltraRT_Dlss_SetFrame(int, const void*) {}
ULTRART_API int UltraRT_Dlss_GetState() { return -1; }
ULTRART_API long long UltraRT_Dlss_GetEvalCount() { return 0; }
ULTRART_API const char* UltraRT_Dlss_GetStatus() { return "UltraRT.Native was built without the DLSS SDK (run scripts\fetch-dlss.ps1, rebuild)"; }
ULTRART_API int UltraRT_Dlss_TakeLog(char* out, int size) { if (out && size > 0) out[0] = 0; return 0; }
