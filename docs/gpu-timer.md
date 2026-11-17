# GPU timer (UltraRT.Native)

Unity's `Recorder` returns 0 GPU time in this release player and `FrameTimingManager` is disabled, so UltraRT measures GPU time itself. A small native DLL writes D3D12 timestamp queries into Unity's own command list from plugin events, then resolves them into a readback buffer and reads them once Unity's frame fence says the frame is done.

| File | Role |
|---|---|
| `native/UltraRT.Native/GpuTimer.cpp` | Unity entry points, render events, query heap and readback ring, exported API |
| `native/UltraRT.Native/UnityLocator.cpp` | Finds Unity's internal "register native plugin" routine in UnityPlayer.dll (read-only scan) |
| `native/UltraRT.Native/LocatorTest.cpp` | Offline test: runs the scan on an image mapping of UnityPlayer.dll |
| `native/UltraRT.Native/build.ps1` | vcvars64 + cl/link, no CMake |
| `src/UltraRT/GpuTimer.cs` | Managed wrapper: explicit `LoadLibrary` + `GetProcAddress`, `CommandBuffer` helpers |

## Getting `IUnityInterfaces` from BepInEx/plugins

**What we do:** `UltraRT_Init()` (main thread) finds Unity's internal plugin-registration routine inside the already-loaded UnityPlayer.dll and calls it with our own `HMODULE`. Unity then does exactly what it does for a plugin it loaded from `ULTRAKILL_Data/Plugins/x86_64`. It looks up our exports, adds us to its plugin list, and calls `UnityPluginLoad(IUnityInterfaces*)`. It also calls `UnityPluginUnload` at shutdown. No files go into the game folder and nothing gets patched.

### Evidence (UnityPlayer.dll, Unity 2022.3.29f1 x64, as shipped with ULTRAKILL)

**(b) Exports.** `dumpbin /exports UnityPlayer.dll` lists a single export, `UnityMain`. Nothing exports the interface registry or a registration entry point.

**The internal loader.** Strings and their code references (RVAs; image base 0x180000000):

- `0x5C1E60`: **RegisterPlugin(HMODULE)**. It calls `GetProcAddress` on the module for `UnitySetGraphicsDevice`, `UnityRenderEvent`, `UnityGetAudioEffectDefinitions`, `UnityPluginLoad`, `UnityPluginUnload`, `UnityRenderingExtEvent`, `UnityRenderingExtQuery` and `UnityShaderCompilerExtEvent`, then calls `AddPlugin(h, &fns)` at `0x5C19F0`.
- `0x5C19F0`: **AddPlugin**.
  - Under a lock, it returns early if the `HMODULE` is already registered; otherwise it appends it to the plugin list.
  - If `UnityPluginLoad` exists and the caller is not the main thread, it logs `"Failed to load plugin. Plugin loading is only allowed on main thread."` and stops. Otherwise it calls `UnityPluginLoad(&s_UnityInterfaces)`, with `s_UnityInterfaces` at RVA `0x1D131E8`.
  - It then calls `UnitySetEventQueue` and the legacy `UnitySetGraphicsDevice` if they are exported.
- Callers of RegisterPlugin:
  - `0x5C39D6`, inside **FindAndLoadUnityPlugin** (`0x5C38D0`), which serves Mono's `mono_set_find_plugin_callback` (`[DllImport]` resolution).
  - `0x245D43`, where Unity `LoadLibrary`s its optional `"NVUnityPlugin"` and registers it the same way. So "LoadLibrary, then RegisterPlugin(HMODULE)" is a supported internal pattern, not an accident of the DllImport path.
  - `0xD1208C`.

**(a) Does `[DllImport]` from an arbitrary path trigger `UnityPluginLoad`?** FindAndLoadUnityPlugin passes the import name to a resolver (`0x5C2AF0`/`0x5C20F0`). The resolver searches the player's plugin locations, appends `.dll`/`.so`/`.lib`/`.dylib`/`.bundle`, and skips Mono's own `MonoBleedingEdge`/`EmbedRuntime` directories. It loads and registers the plugin **only when that resolver returns a path different from the requested name**. Otherwise it hands the name back to Mono, which `LoadLibrary`s the DLL itself, and `UnityPluginLoad` is never called. I did not finish proving whether an absolute path outside `_Data/Plugins` resolves, and testing it would mean launching the game. It doesn't matter for this design, which avoids `[DllImport]` for the native DLL altogether (see the next paragraph). Unity's issue tracker also documents cases where `UnityPluginLoad` is not called ([embedded plug-ins](https://issuetracker.unity3d.com/issues/windows-unitypluginload-is-not-called-when-the-native-plug-in-is-embedded), [32-bit Windows](https://issuetracker.unity3d.com/issues/unitypluginload-is-not-called-on-32-bit-windows)), so it is only guaranteed for plugins Unity loads itself from its plugin folders.

`GpuTimer.cs` loads the DLL with `LoadLibraryW(fullPath)`, so neither Mono nor the BepInEx search path is involved. That path definitely never triggers `UnityPluginLoad` on its own, and that is why `UltraRT_Init` registers the module explicitly.

**How the scan finds RegisterPlugin.** Nothing is hardcoded. The scanner looks for the only function that:

1. contains `lea r64,[rip+"UnityPluginLoad"]`,
2. starts at a 16-byte-aligned address after `int3` padding, no more than 0x100 bytes before that `lea`,
3. references `"UnitySetGraphicsDevice"` before that `lea`, and
4. references `"UnityPluginUnload"` within the next 0x80 bytes.

Exactly one candidate is required. Offline result:

```
> native\UltraRT.Native\build.ps1 -Test
register-plugin routine at RVA 0x5C1E60 (candidates=1, 47 ms)
first bytes: 40 53 48 83 EC 60 48 8D 15 13 47 3E 01 48 8B D9
```

That matches the disassembly above. The call runs under SEH. If Unity rejects it (wrong thread) or the scan fails (a different Unity version), `UltraRT_Init` returns 0 with a reason in `UltraRT_GetStatus()`, and the C# side carries on without GPU timings.

### Alternatives considered

- **(c) No interfaces: device from `GetNativeTexturePtr()`.** We can get an `ID3D12Device`, but we cannot get Unity's command list, which only `IUnityGraphicsD3D12v5+::CommandRecordingState` exposes. Timestamps from our own command list would also be misleading. The header says `kUnityD3D12EventConfigFlag_FlushCommandBuffers` is **not set by default**, so during an unconfigured plugin event Unity's recorded-but-unsubmitted work is still on the CPU side. A list we submitted to the queue at that point would run *before* the work it was meant to bracket. On a separate queue, timestamps measure nothing at all. We would also have to find Unity's queue by hooking `ExecuteCommandLists` vtables, which is more invasive than one call into Unity's own registration routine. Rejected.
- **(d) Loader DLL in `ULTRAKILL_Data/Plugins/x86_64`.** This is the documented route and works on any Unity version. Put a DLL there that exports `UnityPluginLoad` (it can simply be `UltraRT.Native.dll`), and trigger Unity's loader with a `[DllImport("UltraRT.Native")]` call from C#. Unity then resolves the DLL, registers it and calls `UnityPluginLoad`, and `UltraRT_Init` sees `IUnityInterfaces` already set and skips the scan. The cost is an install step inside the game's data folder. It is the fallback if a game update ever breaks the scan. Not installed.

## Native API

| Export | Thread | Meaning |
|---|---|---|
| `UnityPluginLoad` / `UnityPluginUnload` | Unity | Standard entry points; also used when Unity loads the DLL itself |
| `int UltraRT_Init()` | main | Registers with Unity if needed. Returns 1 when D3D12 interfaces are available |
| `GetRenderEventAndDataFunc()` | any | Callback for `CommandBuffer.IssuePluginEventAndData` |
| `int UltraRT_GetEventBase()` | any | Event IDs: base+0 = Begin(slot), base+1 = End(slot), base+2 = Resolve. Currently 91–93. Slot is passed as the data pointer |
| `int UltraRT_GetMaxSlots()` | any | 64 |
| `int UltraRT_GetTimings(float* outMs, int maxSlots)` | any | Newest completed frame. Slots not timed in it are -1. Returns 1 + the highest timed slot (0 before the first result) |
| `int UltraRT_GetCpuTimings(float* outMs, int maxSlots)` | any | The same frame: the render thread's own time (ms, `QueryPerformanceCounter`) between each slot's Begin and End events, i.e. what executing the commands recorded between them cost that thread. `GpuTimer.TryGetCpuMs`; bench `RenderThreadMs` (docs/performance.md, "Profiling") |
| `int UltraRT_IsReady()` | any | Query heap and readback buffer exist (created lazily on the first render event) |
| `const char* UltraRT_GetStatus()` | any | Diagnostic text |
| `long long UltraRT_GetResolvedFrameCount()` / `UltraRT_GetDroppedFrameCount()` | any | Use to spot stale results or ring overflow |

**Internals:**

- One `D3D12_QUERY_HEAP_TYPE_TIMESTAMP` of 8 frames × 64 slots × 2 queries, and a persistently mapped `READBACK` buffer of the same size.
- Begin/End run `EndQuery` on the command list returned by `IUnityGraphicsD3D12v7::CommandRecordingState` (v6 also works).
- Events are configured with `ConfigureEvent(id, {DontCare, flags = 0, ensureActiveRenderTextureIsBound = false})`, because `EndQuery`/`ResolveQueryData` change no bound state. That means no flush, no worker sync and no state invalidation. The first event of a session runs with Unity's defaults, which is harmless.
- Resolve calls `ResolveQueryData` only on contiguous runs of slots that got both a Begin and an End that frame, and records `GetNextFrameFenceValue()`.
- Each event, frames whose fence `GetFrameFence()->GetCompletedValue()` has passed are read, oldest first.
- Ticks are converted using `GetCommandQueue()->GetTimestampFrequency()`.

## Build

```powershell
.\native\UltraRT.Native\build.ps1          # -> native\UltraRT.Native\bin\UltraRT.Native.dll (+ .pdb)
.\native\UltraRT.Native\build.ps1 -Test    # also runs LocatorTest.exe read-only against the game's UnityPlayer.dll
```

- Toolchain: VS Build Tools 2026 (MSVC 14.51), Windows SDK 10.0.26100, Unity 2022.3.29f1 PluginAPI headers. Override the locations with `-VsDir` / `-PluginApi` / `-UnityPlayer`.
- Flags: `/O2 /MT /W4`, which builds clean at `/W4`.
- `/MT`: the DLL loads into a process we don't control. With `/MD` it would need the 14.5x VC++ redistributable, and an older `vcruntime140.dll` already in the process makes `LoadLibrary` fail. No CRT-owned memory crosses the DLL boundary, so a private CRT is safe. It costs about 100 KB; the DLL is 142 KB.
- Imports: `KERNEL32.dll` only. D3D12 is used through the COM pointers Unity hands over.

`scripts/build.ps1` builds the DLL (unless `-SkipNative` is given) and copies it to `BepInEx\plugins\UltraRT\` (`-NoDeploy` stages it instead). Copying fails while the game is running, because the loaded DLL is locked and can't be unloaded (see Caveats). The block warns and continues.

## Integration (Plugin.cs)

The timer is opt-in: `-ultrart-gputimer` enables it (`scripts\bench.ps1` passes it unless `-NoGpuTimer`), `-ultrart-gputimer-flags` / `ULTRART_GPUTIMER_FLAGS` set its flags. The same DLL also holds the DLSS Ray Reconstruction glue (`Dlss.cpp`, or `DlssStub.cpp` without the SDK): [dlss.md](dlss.md). The snippet below is the simplified first wiring; the current slot list is `Slot*` / `SlotNames` in `Plugin.cs` (slots up to 21).

**Deploy:** `UltraRT.Native.dll` goes **next to `UltraRT.dll`** in `BepInEx\plugins\UltraRT\`. It does not go in `ULTRAKILL_Data\Plugins`.

```csharp
private GpuTimer _gpu;
private CommandBuffer _cmdPost;
private const int SlotTotal = 0, SlotRtas = 1, SlotDispatch = 2, SlotBlit = 3;

// Awake(), after the supportsRayTracing check (main thread!):
var nativePath = Path.Combine(Path.GetDirectoryName(Info.Location) ?? "", GpuTimer.DllName);
if (GpuTimer.TryCreate(nativePath, out _gpu, out var gpuError)) Logger.LogInfo($"GPU timer: {_gpu.Status}");
else Logger.LogWarning($"GPU timer unavailable: {gpuError}");
_cmdPost = new CommandBuffer { name = "UltraRT Post" };

// Trace():
_cmd.Clear();
_gpu?.Begin(_cmd, SlotTotal);
_cmd.BeginSample("UltraRT.BuildRTAS");
_gpu?.Begin(_cmd, SlotRtas);
_cmd.BuildRayTracingAccelerationStructure(_proxies.Rtas);
_gpu?.End(_cmd, SlotRtas);
_cmd.EndSample("UltraRT.BuildRTAS");
// ... SetRayTracing* params unchanged ...
_cmd.BeginSample("UltraRT.DispatchRays");
_gpu?.Begin(_cmd, SlotDispatch);
_cmd.DispatchRays(shader, "RayGen", (uint)w, (uint)h, 1);
_gpu?.End(_cmd, SlotDispatch);
_cmd.EndSample("UltraRT.DispatchRays");
_gpu?.Begin(_cmd, SlotBlit);
Graphics.ExecuteCommandBuffer(_cmd);
Graphics.Blit(_output, (RenderTexture)null);   // not part of _cmd, so close the bracket in a second buffer
_cmdPost.Clear();
_gpu?.End(_cmdPost, SlotBlit);
_gpu?.End(_cmdPost, SlotTotal);
_gpu?.EndFrame(_cmdPost);                      // once per frame, after the last End
Graphics.ExecuteCommandBuffer(_cmdPost);

// Reading, e.g. in Measure() or an overlay:
if (_gpu != null && _gpu.TryGetMs(SlotDispatch, out var dispatchMs)) { /* ... */ }

// OnDestroy: _gpu?.Dispose(); _cmdPost?.Release();
```

`Graphics.ExecuteCommandBuffer`, `Graphics.Blit` and the second buffer all queue in order on the render thread. All timestamps go to the same direct queue, so a bracket split across two command lists is still valid.

Since 2026-11-17 the plugin resolves at the end of the frame instead of at the end of `Trace()`: `InstallFrameSlot`
(Plugin.cs) executes one command buffer right before the frame's rendering starts (Begin of the slot `frame`) and one
right after it (End, then Resolve), from the PlayerLoop markers of `Perf.cs`. Late passes (viewmodel, late
volumetrics) so land in the same frame as the trace, and `frame` is timed in every mode, `off` included.

## Caveats

- **Render-thread time** (`TryGetCpuMs`) is that thread's clock between the two events of a slot, valid because a
  command buffer runs on it as one piece with legacy graphics jobs (the bench default). For `frame` it is the span from
  the first to the last render command, waiting for the main thread included: not a cost.

- **Latency.** A value is the newest frame whose GPU work has completed, typically 2–4 frames old. Values are single-frame samples; average them yourself, for example over `Measure()`'s window. If no frames are issued (view off), `TryGetMs` keeps returning the last result. Compare `ResolvedFrames` between reads to detect that.
- **Ring overflow.** If the GPU falls more than 8 frames behind, the frame is skipped (`DroppedFrames`) rather than overwriting queries still in flight.
- **Attribution.** D3D12 timestamps are written when preceding work on the queue drains, with no implicit barrier. A Begin can therefore overlap the tail of earlier work, so tiny slots carry some noise. `Total` is the most robust number.
- **Graphics jobs.** Brackets cover work recorded on the render thread's command list, which is where `Graphics.ExecuteCommandBuffer` at `WaitForEndOfFrame` records. Work Unity records on worker threads in between is not bracketed.
- **Device removal.** The frame fence then reads `UINT64_MAX`. The timer logs `GetDeviceRemovedReason()` into `Status`, `IsReady` becomes false, and every later event is a no-op. A graphics device Shutdown/Initialize pair from Unity releases and recreates everything lazily.
- **Not DX12 / init failure.** `TryCreate` returns false with a reason and never throws. On DX11 the native DLL is not even loaded.
- **Unloading.** `Dispose()` only stops issuing events. The DLL stays loaded because Unity holds its `HMODULE` in the plugin list (and calls `UnityPluginUnload` at exit), and command buffers already queued may still call the callback.
- **Event IDs 91–93** are global for D3D12 event configuration. Change `kEventBase` in `GpuTimer.cpp` if another native plugin ever uses them; C# reads the base from the DLL.
- **Unity version.** The scan relies on string references, not fixed offsets, so it should survive patch releases. If it ever fails, `Status` says so; then use option (d).
