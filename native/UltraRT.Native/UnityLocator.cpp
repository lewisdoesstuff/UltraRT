#include "UnityLocator.h"

#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <string.h>

// In UnityPlayer.dll (2022.3.29f1, RVAs for reference only; nothing below hardcodes them):
//
//   0x5C1E60  RegisterPlugin(HMODULE h):
//       struct { fn* p[8]; } f;
//       f.p[0] = GetProcAddress(h, "UnitySetGraphicsDevice");
//       f.p[1] = GetProcAddress(h, "UnityRenderEvent");
//       f.p[2] = GetProcAddress(h, "UnityGetAudioEffectDefinitions");
//       f.p[3] = GetProcAddress(h, "UnityPluginLoad");
//       f.p[4] = GetProcAddress(h, "UnityPluginUnload");
//       ...                    "UnityRenderingExtEvent", "UnityRenderingExtQuery", "UnityShaderCompilerExtEvent"
//       AddPlugin(h, &f);      // 0x5C19F0: dedups by HMODULE, requires the main thread,
//                              //           calls f.p[3](&s_UnityInterfaces)
//
// Called from FindAndLoadUnityPlugin (the Mono find-plugin callback) and when Unity loads "NVUnityPlugin".
// We find it by its string references: one `lea r64,[rip+"UnityPluginLoad"]` whose enclosing function starts
// shortly before with a `lea` of "UnitySetGraphicsDevice" and continues with one of "UnityPluginUnload".

namespace
{
    struct Section
    {
        const uint8_t* data;
        uint32_t rva;
        uint32_t size;
        bool exec;
    };

    constexpr int kMaxSections = 64;
    constexpr int kMaxHits = 16;

    struct Image
    {
        const uint8_t* base;
        Section sections[kMaxSections];
        int count;
    };

    bool ParseImage(const uint8_t* base, Image& img)
    {
        img.base = base;
        img.count = 0;
        auto dos = reinterpret_cast<const IMAGE_DOS_HEADER*>(base);
        if (dos->e_magic != IMAGE_DOS_SIGNATURE) return false;
        auto nt = reinterpret_cast<const IMAGE_NT_HEADERS64*>(base + dos->e_lfanew);
        if (nt->Signature != IMAGE_NT_SIGNATURE || nt->FileHeader.Machine != IMAGE_FILE_MACHINE_AMD64) return false;
        auto sec = IMAGE_FIRST_SECTION(nt);
        for (int i = 0; i < nt->FileHeader.NumberOfSections && img.count < kMaxSections; i++)
        {
            uint32_t size = sec[i].Misc.VirtualSize;
            if (sec[i].SizeOfRawData < size) size = sec[i].SizeOfRawData; // tail beyond raw data is zero-fill
            if (size == 0) continue;
            img.sections[img.count++] = {
                base + sec[i].VirtualAddress, sec[i].VirtualAddress, size,
                (sec[i].Characteristics & IMAGE_SCN_MEM_EXECUTE) != 0 };
        }
        return img.count > 0;
    }

    // All RVAs of the NUL-terminated string `s` that also start a string (preceded by NUL), in data sections.
    int FindStrings(const Image& img, const char* s, uint32_t* out, int max)
    {
        const size_t n = strlen(s) + 1; // include the terminator
        int found = 0;
        for (int i = 0; i < img.count; i++)
        {
            const Section& sec = img.sections[i];
            if (sec.exec || sec.size < n) continue;
            for (uint32_t off = 1; off + n <= sec.size; off++)
            {
                if (sec.data[off] != (uint8_t)s[0] || sec.data[off - 1] != 0) continue;
                if (memcmp(sec.data + off, s, n) == 0 && found < max) out[found++] = sec.rva + off;
            }
        }
        return found;
    }

    bool Contains(const uint32_t* list, int count, uint32_t v)
    {
        for (int i = 0; i < count; i++)
            if (list[i] == v) return true;
        return false;
    }

    // If code[off] is `lea r64, [rip+disp32]` (REX.W 8D /r, mod=00 rm=101) return its target RVA, else 0.
    uint32_t LeaTarget(const Section& sec, uint32_t off)
    {
        if (off + 7 > sec.size) return 0;
        const uint8_t* p = sec.data + off;
        if ((p[0] != 0x48 && p[0] != 0x4C) || p[1] != 0x8D || (p[2] & 0xC7) != 0x05) return 0;
        int32_t disp;
        memcpy(&disp, p + 3, 4);
        return (uint32_t)((int64_t)sec.rva + off + 7 + disp);
    }

    bool HasLeaTo(const Section& sec, uint32_t from, uint32_t to, const uint32_t* targets, int count)
    {
        for (uint32_t off = from; off < to; off++)
        {
            uint32_t t = LeaTarget(sec, off);
            if (t && Contains(targets, count, t)) return true;
        }
        return false;
    }
}

UnityLocateResult LocateUnityRegisterPlugin(const uint8_t* imageBase)
{
    UnityLocateResult r = {};
    Image img;
    if (!imageBase || !ParseImage(imageBase, img)) { r.error = "UnityPlayer.dll: bad PE image"; return r; }

    uint32_t load[kMaxHits], setDevice[kMaxHits], unload[kMaxHits];
    int nLoad = FindStrings(img, "UnityPluginLoad", load, kMaxHits);
    int nSetDevice = FindStrings(img, "UnitySetGraphicsDevice", setDevice, kMaxHits);
    int nUnload = FindStrings(img, "UnityPluginUnload", unload, kMaxHits);
    if (!nLoad || !nSetDevice || !nUnload) { r.error = "UnityPlayer.dll: plugin export names not found"; return r; }

    for (int i = 0; i < img.count; i++)
    {
        const Section& sec = img.sections[i];
        if (!sec.exec) continue;
        for (uint32_t off = 0; off + 7 <= sec.size; off++)
        {
            uint32_t t = LeaTarget(sec, off);
            if (!t || !Contains(load, nLoad, t)) continue;

            // Function start: MSVC aligns functions to 16 bytes and pads with int3 (0xCC).
            uint32_t start = 0;
            for (uint32_t back = 0; back <= 0x100 && back < off; back++)
            {
                uint32_t s = off - back;
                if (((sec.rva + s) & 15) == 0 && sec.data[s - 1] == 0xCC) { start = s; break; }
            }
            if (!start) continue;
            if (!HasLeaTo(sec, start, off, setDevice, nSetDevice)) continue;
            uint32_t end = off + 0x80 < sec.size ? off + 0x80 : sec.size;
            if (!HasLeaTo(sec, off + 7, end, unload, nUnload)) continue;

            if (r.candidates++ == 0)
            {
                r.function = sec.data + start;
                r.rva = sec.rva + start;
            }
        }
    }

    if (r.candidates != 1)
    {
        r.error = r.candidates == 0 ? "UnityPlayer.dll: plugin registration routine not found"
                                    : "UnityPlayer.dll: plugin registration routine is ambiguous";
        r.function = nullptr;
        r.rva = 0;
    }
    return r;
}
