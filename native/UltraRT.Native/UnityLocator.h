// Finds UnityPlayer.dll's internal "register native plugin" routine, the one Unity itself runs right after it
// LoadLibrary()s a plugin from <Game>_Data/Plugins: it GetProcAddress()es UnityPluginLoad & co. on the module
// handle, adds the plugin to Unity's plugin list and calls UnityPluginLoad(IUnityInterfaces*).
// See docs/gpu-timer.md for the reverse-engineering notes.
#pragma once
#include <stdint.h>

struct UnityLocateResult
{
    const uint8_t* function;   // address of the routine, or null
    uint32_t rva;              // its RVA inside UnityPlayer.dll (for logging)
    int candidates;            // how many functions matched (must be exactly 1)
    const char* error;         // static string when function == null
};

// imageBase: an image-mapped UnityPlayer.dll (the loaded module, or a LOAD_LIBRARY_AS_IMAGE_RESOURCE mapping).
// Read-only: never executes or writes anything.
UnityLocateResult LocateUnityRegisterPlugin(const uint8_t* imageBase);

// Signature of the located routine (x64: the only calling convention).
typedef void (*UnityRegisterPluginFn)(void* hmodule);
