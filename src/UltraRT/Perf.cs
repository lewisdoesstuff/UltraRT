using System;
using System.Diagnostics;
using UnityEngine.LowLevel;
using UnityEngine.PlayerLoop;

namespace UltraRT
{
    /// <summary>
    /// CPU timing of the plugin's per-frame work by section, plus a main-thread breakdown of the whole frame from
    /// PlayerLoop markers (Unity's Profiler isn't available in the player build). Sections are accumulated in ticks and
    /// taken once per frame by the benchmark (Plugin.Bench.cs), which writes them per frame (frames_*.csv) and per mode.
    /// </summary>
    internal static class Perf
    {
        public enum Sec
        {
            Hierarchies,  // registering newly enabled enemies (Patches.PendingHierarchies)
            Rescan,       // the periodic safety rescan of every renderer in the scene
            Static,       // static-batch activation groups (StaticGroups.cs)
            Dynamic,      // dynamic proxies: activation, transforms, blend shapes
            Motion,       // motion-vector bookkeeping (slots, uploads, pose copies)
            Emissive,     // emissive triangle lights
            Lights,       // light gathering and packing
            LightSampling,// light alias tables / ReSTIR bindings
            // Main-thread time to record a pass's commands (its GPU and render-thread time are the timer slot of the same name):
            Cache,        // radiance cache update
            Particles,    // lit particles' lighting volume
            Denoise,      // denoiser, auto exposure; DLSS RR and the upsampler
            Volumetrics,  // sun shafts, halos, fog
            Vfx,          // bloom, haze, shockwaves
            Viewmodel,    // the weapon pass (recorded from the HUD camera, outside Plugin.Trace)
            Trace,        // all of Plugin.Trace (includes the sections above but Viewmodel)
            Count,
        }

        public static readonly string[] Names = { "hier", "rescan", "static", "dynamic", "motion", "emissive", "lights", "lightsamp", "cache", "particles", "denoise", "volumetrics", "vfx", "viewmodel", "trace" };

        private static readonly long[] Ticks = new long[(int)Sec.Count];
        public static readonly double TickMs = 1000.0 / Stopwatch.Frequency;

        public static long Now => Stopwatch.GetTimestamp();
        public static void Add(Sec s, long t0) => Ticks[(int)s] += Stopwatch.GetTimestamp() - t0;

        /// <summary>Milliseconds per section since the last call, then reset.</summary>
        public static void Take(float[] ms)
        {
            for (int i = 0; i < (int)Sec.Count; i++)
            {
                if (ms != null && i < ms.Length) ms[i] = (float)(Ticks[i] * TickMs);
                Ticks[i] = 0;
            }
        }

        // ---------------------------------------------------------------- main-thread frame breakdown

        /// <summary>
        /// Timestamps of the last completed frame: FrameStart = after TimeUpdate (Unity's wait for the previous present),
        /// RenderStart = PostLateUpdate begins (scripts, physics and animation done), RenderEnd = after the main thread
        /// finished rendering (culling, our Trace, command submission). Wait = the rest of the frame (render thread / GPU).
        /// </summary>
        public static long FrameStart, RenderStart, RenderEnd;
        /// <summary>Called on the main thread right before the frame's rendering starts and right after it ends (before
        /// the present): the GPU timer's whole-frame slot (Plugin.cs, SlotFrame).</summary>
        public static Action OnRenderStart, OnRenderEnd;
        private static long _curStart, _curRender;
        private static bool _installed;

        private struct MarkFrameStart { }
        private struct MarkRenderStart { }
        private struct MarkRenderEnd { }

        public static void InstallFrameMarkers()
        {
            if (_installed) return;
            var loop = PlayerLoop.GetCurrentPlayerLoop();
            if (loop.subSystemList == null) return;
            var subs = loop.subSystemList;
            for (int i = 0; i < subs.Length; i++)
            {
                if (subs[i].type == typeof(Initialization)) subs[i] = Insert(subs[i], 0, typeof(MarkFrameStart), () => _curStart = Stopwatch.GetTimestamp());
                else if (subs[i].type == typeof(PostLateUpdate))
                {
                    var s = Insert(subs[i], 0, typeof(MarkRenderStart), () => { _curRender = Stopwatch.GetTimestamp(); OnRenderStart?.Invoke(); });
                    int after = Array.FindIndex(s.subSystemList, x => x.type == typeof(PostLateUpdate.FinishFrameRendering));
                    if (after < 0) after = s.subSystemList.Length - 1;
                    subs[i] = Insert(s, after + 1, typeof(MarkRenderEnd), () =>
                    {
                        FrameStart = _curStart;
                        RenderStart = _curRender;
                        RenderEnd = Stopwatch.GetTimestamp();
                        OnRenderEnd?.Invoke();
                    });
                }
            }
            loop.subSystemList = subs;
            PlayerLoop.SetPlayerLoop(loop);
            _installed = true;
        }

        private static PlayerLoopSystem Insert(PlayerLoopSystem parent, int at, Type type, PlayerLoopSystem.UpdateFunction fn)
        {
            var list = parent.subSystemList ?? new PlayerLoopSystem[0];
            var n = new PlayerLoopSystem[list.Length + 1];
            at = Math.Max(0, Math.Min(at, list.Length));
            Array.Copy(list, 0, n, 0, at);
            n[at] = new PlayerLoopSystem { type = type, updateDelegate = fn };
            Array.Copy(list, at, n, at + 1, list.Length - at);
            parent.subSystemList = n;
            return parent;
        }

        /// <summary>Last frame: scripts (Update/LateUpdate/physics) and main-thread rendering, in ms (0 before the markers ran).</summary>
        public static void LastFrame(out float scriptsMs, out float renderMs)
        {
            scriptsMs = RenderStart > FrameStart && FrameStart != 0 ? (float)((RenderStart - FrameStart) * TickMs) : 0f;
            renderMs = RenderEnd > RenderStart && RenderStart != 0 ? (float)((RenderEnd - RenderStart) * TickMs) : 0f;
        }
    }
}
