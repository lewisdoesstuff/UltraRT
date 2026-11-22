// UltraRT.Native: guards Unity 2022.3.29f1's BLAS cache against a null scratch buffer (docs/scene-proxies.md,
// "Native crash in the BLAS cache (6-1, 2026-11-22)").
//
// GfxDeviceD3D12::AllocBottomLevelRayTracingAccelerationStructure keeps each cached BLAS as two entries of
// RayTracingHeap's Hash128 map: the result buffer under the geometry's key K, and its scratch buffer under
// SpookyHash(K, 1). Two lookups of the cached path take the result when K is found but never create a scratch that is
// missing: they store a null BufferD3D12* and pass it to BufferD3D12::AccessBuffer, which reads [null + 0x60] on the
// render thread (EXCEPTION_ACCESS_VIOLATION reading 0x60, UnityPlayer+0x92FBD6; the 6-1 Small Room crash).
//   path A (skinned refit, BuildData+0x60 set): 0x9933C1 stores {null} when the scratch key is missing.
//   path B (lookup):                           0x9935B5 / 0x9935E1 jump to 0x9935F5, which stores {null}.
// Unity's own fallback sits right after path B's cached branch: 0x993602 calls RayTracingHeap::FindOrCreateResource
// for the scratch slot, which (the slot being keyed) looks the scratch key up and creates and maps it when missing.
//
// The patch (RVAs of 2022.3.29f1 for reference; the code finds the bytes by signature and changes nothing on a mismatch):
//   1. 0x9932CB  0F 84 79 01 00 00 (je pathB)            -> 90 E9 79 01 00 00 (jmp pathB): path A is never taken; path B
//      does the same lookups plus a size check, and with the build flags already chosen (PERFORM_UPDATE for a skinned
//      BLAS that exists) the refit is unchanged.
//   2. 0x9935B5  74 3E (je 0x9935F5) -> 74 4B (je 0x993602): scratch key not in the map -> FindOrCreateResource.
//   3. 0x9935E1  74 12 (je 0x9935F5) -> 74 1F (je 0x993602): same, second (iterator) lookup.
// At both jumps r12 still holds the geometry desc and r14 the scratch slot ({null, derive 1, keyed}), which is what the
// code at 0x993602 expects (it is reached the same way from the uncached branch).

#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <stdint.h>
#include <stdio.h>
#include <string.h>

#define ULTRART_API extern "C" __declspec(dllexport)

namespace
{
    // 0x9932B8: mov r15,[rbp+0xD8]; mov rsi,[r14+0x4848]; cmp byte [r15+0x60],0; je pathB (rel32 0x179)
    const uint8_t kSite1[] = {
        0x4C, 0x8B, 0xBD, 0xD8, 0x00, 0x00, 0x00, 0x49, 0x8B, 0xB6, 0x48, 0x48, 0x00, 0x00,
        0x41, 0x80, 0x7F, 0x60, 0x00, 0x0F, 0x84, 0x79, 0x01, 0x00, 0x00 };
    constexpr int kSite1Je = 19;          // offset of 0F 84
    constexpr int kSite1Next = 25;        // the instruction after the je
    constexpr int32_t kSite1Rel = 0x179;  // je target = next + rel = path B
    // Path B's first instruction: movups xmm6,[r15+8]
    const uint8_t kPathB[] = { 0x41, 0x0F, 0x10, 0x77, 0x08 };

    // 0x9935B2: cmp rax,rbx; je +3E; lea rdx,[rbp-0x70]; lea rcx,[rsi+0x20]; call lookup (rel32 wildcard); mov rdx,rax;
    // cmp rax,rbx; jae +12; nop; cmp dword [rdx],-2; jb +9; add rdx,0x20; cmp rdx,rbx; jb -0E; cmp rbx,rdx; je +12;
    // movzx eax,byte [rbp-0x47]; mov rcx,[rdx+0x18]; or [rcx+9],al; mov rax,[rdx+0x18]; movups xmm6,[rax];
    // mov r12,[rbp+0xC0]; movups [r14],xmm6; jmp +2B;  (0x993602:) movups xmm0,[r12+0x20]; mov r12,[rbp+0xC0]
    const int16_t kSite2[] = {
        0x48, 0x3B, 0xC3, 0x74, 0x3E, 0x48, 0x8D, 0x55, 0x90, 0x48, 0x8D, 0x4E, 0x20, 0xE8, -1, -1, -1, -1,
        0x48, 0x8B, 0xD0, 0x48, 0x3B, 0xC3, 0x73, 0x12, 0x0F, 0x1F, 0x40, 0x00, 0x83, 0x3A, 0xFE, 0x72, 0x09,
        0x48, 0x83, 0xC2, 0x20, 0x48, 0x3B, 0xD3, 0x72, 0xF2, 0x48, 0x3B, 0xDA, 0x74, 0x12, 0x0F, 0xB6, 0x45, 0xB9,
        0x48, 0x8B, 0x4A, 0x18, 0x08, 0x41, 0x09, 0x48, 0x8B, 0x42, 0x18, 0x0F, 0x10, 0x30, 0x4C, 0x8B, 0xA5, 0xC0,
        0x00, 0x00, 0x00, 0x41, 0x0F, 0x11, 0x36, 0xEB, 0x2B, 0x41, 0x0F, 0x10, 0x44, 0x24, 0x20, 0x4C, 0x8B, 0xA5,
        0xC0, 0x00, 0x00, 0x00 };
    constexpr int kSite2Je1 = 4;     // rel8 of the first je (3E -> 4B)
    constexpr int kSite2Je2 = 48;    // rel8 of the second je (12 -> 1F)
    constexpr int kSite2Fallback = 80; // 0x993602, the FindOrCreateResource block
    constexpr ptrdiff_t kSite2FromSite1 = 0x2FA; // both in the same function: 0x9935B2 - 0x9932B8

    char s_Status[256] = "not run";
    int s_State = 0; // 0 not run, 1 patched (or already), -1 not applied

    bool Matches(const uint8_t* p, const int16_t* sig, size_t n)
    {
        for (size_t i = 0; i < n; i++)
            if (sig[i] >= 0 && p[i] != (uint8_t)sig[i]) return false;
        return true;
    }

    struct Text { const uint8_t* data; size_t size; };

    bool FindText(HMODULE mod, Text& t)
    {
        auto base = reinterpret_cast<const uint8_t*>(mod);
        auto dos = reinterpret_cast<const IMAGE_DOS_HEADER*>(base);
        if (dos->e_magic != IMAGE_DOS_SIGNATURE) return false;
        auto nt = reinterpret_cast<const IMAGE_NT_HEADERS64*>(base + dos->e_lfanew);
        if (nt->Signature != IMAGE_NT_SIGNATURE) return false;
        auto sec = IMAGE_FIRST_SECTION(nt);
        for (int i = 0; i < nt->FileHeader.NumberOfSections; i++)
        {
            if (!(sec[i].Characteristics & IMAGE_SCN_MEM_EXECUTE)) continue;
            if (memcmp(sec[i].Name, ".text", 5) != 0) continue;
            t.data = base + sec[i].VirtualAddress;
            t.size = sec[i].Misc.VirtualSize;
            return true;
        }
        return false;
    }

    bool WriteCode(uint8_t* at, const uint8_t* bytes, size_t n)
    {
        DWORD old = 0;
        if (!VirtualProtect(at, n, PAGE_EXECUTE_READWRITE, &old)) return false;
        memcpy(at, bytes, n);
        VirtualProtect(at, n, old, &old);
        FlushInstructionCache(GetCurrentProcess(), at, n);
        return true;
    }
}

// Applies the patch once (idempotent). Returns 1 patched (or already patched), 0 not applied (see the status text).
// Call before any ray tracing acceleration structure is built: the bytes change while no thread runs that code.
ULTRART_API int UltraRT_PatchBlasScratch()
{
    if (s_State == 1) return 1;
    HMODULE mod = GetModuleHandleW(L"UnityPlayer.dll");
    if (!mod) { snprintf(s_Status, sizeof s_Status, "UnityPlayer.dll not loaded"); s_State = -1; return 0; }
    Text t = {};
    if (!FindText(mod, t)) { snprintf(s_Status, sizeof s_Status, "no .text section"); s_State = -1; return 0; }

    const size_t n1 = sizeof kSite1, n2 = sizeof kSite2 / sizeof kSite2[0];
    uint8_t* site1 = nullptr;
    int hits1 = 0, patched1 = 0;
    for (size_t i = 0; i + n1 <= t.size; i++)
    {
        const uint8_t* p = t.data + i;
        if (p[0] != kSite1[0] || p[1] != kSite1[1]) continue;
        if (memcmp(p, kSite1, kSite1Je) != 0) continue;
        bool orig = memcmp(p, kSite1, n1) == 0;
        bool done = p[kSite1Je] == 0x90 && p[kSite1Je + 1] == 0xE9 && memcmp(p + kSite1Je + 2, kSite1 + kSite1Je + 2, 4) == 0;
        if (!orig && !done) continue;
        hits1++;
        patched1 += done ? 1 : 0;
        site1 = const_cast<uint8_t*>(p);
    }
    if (hits1 != 1)
    {
        snprintf(s_Status, sizeof s_Status, "signature 1 found %d times (expected once: Unity 2022.3.29f1 only); not patched", hits1);
        s_State = -1;
        return 0;
    }
    uint8_t* site2 = site1 + kSite2FromSite1;
    if (site2 + n2 > t.data + t.size) { snprintf(s_Status, sizeof s_Status, "signature 2 out of range; not patched"); s_State = -1; return 0; }
    bool orig2 = Matches(site2, kSite2, n2);
    // Already patched: the two rel8 bytes changed, the rest matches.
    int16_t sig2p[sizeof kSite2 / sizeof kSite2[0]];
    memcpy(sig2p, kSite2, sizeof kSite2);
    sig2p[kSite2Je1] = 0x4B;
    sig2p[kSite2Je2] = 0x1F;
    bool done2 = Matches(site2, sig2p, n2);
    if (!orig2 && !done2) { snprintf(s_Status, sizeof s_Status, "signature 2 does not match at site 1 + 0x2FA; not patched"); s_State = -1; return 0; }
    // The jumps must land where the code says they do.
    const uint8_t* pathB = site1 + kSite1Next + kSite1Rel;
    if (memcmp(pathB, kPathB, sizeof kPathB) != 0) { snprintf(s_Status, sizeof s_Status, "path B not at the je target; not patched"); s_State = -1; return 0; }
    if (site2 + kSite2Je1 + 1 + 0x4B != site2 + kSite2Fallback || site2 + kSite2Je2 + 1 + 0x1F != site2 + kSite2Fallback)
    { snprintf(s_Status, sizeof s_Status, "fallback offsets inconsistent; not patched"); s_State = -1; return 0; }

    if (patched1 && done2)
    {
        snprintf(s_Status, sizeof s_Status, "already patched at UnityPlayer+0x%llX / +0x%llX",
                 (unsigned long long)(site1 + kSite1Je - (uint8_t*)mod), (unsigned long long)(site2 - (uint8_t*)mod));
        s_State = 1;
        return 1;
    }
    const uint8_t j1[] = { 0x90, 0xE9 };
    const uint8_t r1 = 0x4B, r2 = 0x1F;
    bool ok = (patched1 || WriteCode(site1 + kSite1Je, j1, sizeof j1)) &&
              (done2 || (WriteCode(site2 + kSite2Je1, &r1, 1) && WriteCode(site2 + kSite2Je2, &r2, 1)));
    if (!ok) { snprintf(s_Status, sizeof s_Status, "VirtualProtect failed (%lu); not patched", GetLastError()); s_State = -1; return 0; }
    snprintf(s_Status, sizeof s_Status, "patched UnityPlayer+0x%llX (refit path -> lookup path) and +0x%llX/+0x%llX (missing scratch -> FindOrCreateResource)",
             (unsigned long long)(site1 + kSite1Je - (uint8_t*)mod), (unsigned long long)(site2 + kSite2Je1 - 1 - (uint8_t*)mod),
             (unsigned long long)(site2 + kSite2Je2 - 1 - (uint8_t*)mod));
    s_State = 1;
    return 1;
}

ULTRART_API const char* UltraRT_PatchBlasScratchStatus() { return s_Status; }
