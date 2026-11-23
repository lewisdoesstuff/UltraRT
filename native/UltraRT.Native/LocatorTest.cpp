// Offline check of the UnityPlayer.dll signature scan. Maps the DLL as an image resource (no code runs,
// no imports resolved), runs the locator and prints the RVA it found.
//   LocatorTest.exe "<ULTRAKILL_DIR>\UnityPlayer.dll" [expectedRvaHex]
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <stdio.h>
#include <stdlib.h>
#include "UnityLocator.h"

int wmain(int argc, wchar_t** argv)
{
    if (argc < 2) { fwprintf(stderr, L"usage: %s <UnityPlayer.dll> [expectedRvaHex]\n", argv[0]); return 2; }
    HMODULE h = LoadLibraryExW(argv[1], nullptr, LOAD_LIBRARY_AS_IMAGE_RESOURCE);
    if (!h) { fwprintf(stderr, L"LoadLibraryEx failed: %lu\n", GetLastError()); return 2; }
    // Resource mappings tag the handle's low bits; the image starts at the aligned address.
    auto base = reinterpret_cast<const uint8_t*>(reinterpret_cast<ULONG_PTR>(h) & ~(ULONG_PTR)0xFFFF);

    DWORD t0 = GetTickCount();
    UnityLocateResult r = LocateUnityRegisterPlugin(base);
    DWORD ms = GetTickCount() - t0;
    if (!r.function) { printf("FAIL: %s (candidates=%d)\n", r.error, r.candidates); return 1; }
    printf("register-plugin routine at RVA 0x%X (candidates=%d, %lu ms)\nfirst bytes:", r.rva, r.candidates, ms);
    for (int i = 0; i < 16; i++) printf(" %02X", r.function[i]);
    printf("\n");
    if (argc >= 3)
    {
        unsigned long expected = wcstoul(argv[2], nullptr, 16);
        if (expected != r.rva) { printf("FAIL: expected RVA 0x%lX\n", expected); return 1; }
        printf("OK: matches expected RVA\n");
    }
    FreeLibrary(h);
    return 0;
}
