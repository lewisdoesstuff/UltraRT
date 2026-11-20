using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace UltraRT
{
    /// <summary>Interactive water ripples (docs/reflections.md, "Interactive ripples").</summary>
    public sealed class WaterImpulseSettings
    {
        /// <summary>-ultrart-water-impulses X / bench water.impulses / [Effects] InteractiveWaterRipples: multiplier on
        /// every ring's slope (1 = as tuned, 0 = off: no rings, the shaders run exactly as before).</summary>
        public float impulses = 1f;
        /// <summary>Sources: the game's water splashes (player, enemies, projectiles, gibs: Water.TrySpawnSplash), wakes of
        /// whatever wades at the surface (Water.UpdateContinuousSplash), explosions near a water surface.</summary>
        public bool splashes = true, wakes = true, explosions = true;
        /// <summary>Enemies' splashes and wakes (the player's and projectiles' always count).</summary>
        public bool enemies = true;
        /// <summary>Rings further than this from the camera are not made (m).</summary>
        public float maxDistance = 80f;
        /// <summary>Bench water.testRipple=1: one ring frozen at testAge (s) on the water surface in front of the camera, so
        /// static poses show the same ring in every frame and every mode.</summary>
        public bool testRipple;
        public float testAge = 0.65f, testAmplitude = 0.25f;
        /// <summary>Bench water.testRings=N (1-16): N test rings on a spiral around the first (the cost of a full buffer).</summary>
        public int testRings = 1;
    }

    /// <summary>
    /// Observe-only hooks on the game's water (Water.cs in Assembly-CSharp): every splash it spawns from its pool (not
    /// instantiated, so the spawn hooks never see them) and the continuous splash of whatever wades at the surface.
    /// Optional: without them only explosions make rings.
    /// </summary>
    internal static class WaterSplashHooks
    {
        public struct Event
        {
            public bool Wake, Player, Enemy, Big;
            public Vector3 Position, Velocity;
            public float Size, Time;
        }

        public static readonly List<Event> Pending = new List<Event>(64);
        public static bool Collect;
        public static bool Hooked;
        public static string HookError;
        private static readonly Dictionary<int, float> LastWake = new Dictionary<int, float>();

        public static void Patch(Harmony harmony)
        {
            try
            {
                var splash = AccessTools.Method(typeof(Water), "TrySpawnSplash", new[] { typeof(WaterObject) });
                var cont = AccessTools.Method(typeof(Water), "UpdateContinuousSplash", new[] { typeof(WaterObject), typeof(Vector3), typeof(bool) });
                if (splash == null) { HookError = "Water.TrySpawnSplash not found"; return; }
                harmony.Patch(splash, postfix: new HarmonyMethod(typeof(WaterSplashHooks).GetMethod(nameof(SplashPostfix), BindingFlags.NonPublic | BindingFlags.Static)));
                if (cont != null) harmony.Patch(cont, postfix: new HarmonyMethod(typeof(WaterSplashHooks).GetMethod(nameof(ContinuousPostfix), BindingFlags.NonPublic | BindingFlags.Static)));
                else HookError = "Water.UpdateContinuousSplash not found (no wakes)";
                Hooked = true;
            }
            catch (Exception e) { HookError = e.Message; }
        }

        public static void Clear() { Pending.Clear(); LastWake.Clear(); }

        // As TrySpawnSplash decides: big for a fast fall (or layer 11) with mass, small for anything not kinematic, none
        // otherwise. The underwater controller's trigger (the camera) is not a body in the water.
        private static void SplashPostfix(WaterObject wObj)
        {
            try
            {
                if (!Collect || wObj == null || wObj.IsUWC || Pending.Count >= 64) return;
                var rb = wObj.Rb;
                if (rb == null) return;
                var v = rb.velocity;
                bool big = (v.y < -25f || wObj.Layer == 11) && rb.mass >= 1f && wObj.Layer != 10 && wObj.Layer != 9;
                if (!big && rb.isKinematic) return;
                Pending.Add(new Event
                {
                    Player = wObj.IsPlayer, Enemy = wObj.IsEnemy, Big = big, Position = rb.transform.position, Velocity = v,
                    Size = wObj.Col != null ? wObj.Col.bounds.size.magnitude : 1f, Time = UnityEngine.Time.time,
                });
            }
            catch (Exception) { /* never break the game's water */ }
        }

        // Every FixedUpdate for the player and enemies at the surface; rate-limited here per body (the consumer spaces
        // the rings by speed).
        private static void ContinuousPostfix(WaterObject wObj, Vector3 surfacePoint, bool isSubmerged)
        {
            try
            {
                if (!Collect || isSubmerged || wObj == null || wObj.IsUWC || Pending.Count >= 64) return;
                var rb = wObj.Rb;
                if (rb == null) return;
                var v = rb.velocity;
                if (v.x * v.x + v.z * v.z < 2.25f) return; // under 1.5 m/s: no wake
                int id = rb.GetInstanceID();
                float now = UnityEngine.Time.time;
                float interval = Mathf.Lerp(0.2f, 0.15f, Mathf.Clamp01(new Vector2(v.x, v.z).magnitude / 20f));
                if (LastWake.TryGetValue(id, out var last) && now - last < interval && now >= last) return;
                if (LastWake.Count > 256) LastWake.Clear();
                LastWake[id] = now;
                Pending.Add(new Event
                {
                    Wake = true, Player = wObj.IsPlayer, Enemy = wObj.IsEnemy, Position = surfacePoint, Velocity = v,
                    Size = wObj.Col != null ? wObj.Col.bounds.size.magnitude : 1f, Time = now,
                });
            }
            catch (Exception) { /* never break the game's water */ }
        }
    }

    /// <summary>
    /// Interactive water ripples (docs/reflections.md, "Interactive ripples"; UltraRTWater.hlsl, WaterImpulseRing):
    /// expanding rings on the water from splashes, wakes and explosions, in the reflection normal and the ripple caustics.
    /// Up to 16 rings live at once; each frame they go to the shaders as (centre x, z, age, slope) and (surface y, speed,
    /// wavelength, decay), with the age computed here (Time.time, game time: pauses and hitstop freeze the rings, and the
    /// shader never sees a clock that wraps). Plugin.cs has one-line hooks: UpdateWaterImpulses (per traced frame, after
    /// the ripple-caustic planes), WaterImpulseOnSpawn (EffectLights' spawn queue), the bench variant target "water".
    /// </summary>
    public partial class Plugin
    {
        private const int MaxWaterRings = 16;
        private static readonly int WaterImpulseId = Shader.PropertyToID("_UltraRTWaterImpulse");
        private static readonly int WaterImpulsesId = Shader.PropertyToID("_UltraRTWaterImpulses");
        private static readonly int WaterImpulses2Id = Shader.PropertyToID("_UltraRTWaterImpulses2");

        private struct WaterRing { public float X, Z, Y, Born, Amp, Speed, Lambda, Decay; }

        private readonly WaterImpulseSettings _waterImpulseSettings = new WaterImpulseSettings();
        private readonly WaterRing[] _waterRings = new WaterRing[MaxWaterRings];
        private int _waterRingCount, _waterRingsUploaded = -1, _waterRingLogs;
        private float _waterRingNextLog;
        private readonly Vector4[] _waterRingA = new Vector4[MaxWaterRings], _waterRingB = new Vector4[MaxWaterRings];
        private readonly List<Explosion> _waterExplosions = new List<Explosion>(4);
        private SceneProxies _waterRingProxies;
        private bool _waterTestLogged;

        /// <summary>Per traced frame (after UpdateRippleCaustics): new rings from the game's splashes, the bench's test ring,
        /// and the live rings into the globals the water shaders read.</summary>
        private void UpdateWaterImpulses()
        {
            var s = _waterImpulseSettings;
            bool on = s.impulses > 0f && _proxies != null && _camera != null;
            WaterSplashHooks.Collect = on;
            if (!ReferenceEquals(_waterRingProxies, _proxies))
            {
                _waterRingProxies = _proxies;
                _waterRingCount = 0;
                _waterRingLogs = 0;
                _waterTestLogged = false;
                WaterSplashHooks.Clear();
                if (_proxies != null)
                    Logger.LogInfo($"Water ripples: strength {s.impulses:F2}, game splash hooks {(WaterSplashHooks.Hooked ? "on" : "off")}{(WaterSplashHooks.HookError != null ? " (" + WaterSplashHooks.HookError + ")" : "")}{(s.testRipple ? ", test ring" : "")}");
            }
            float now = Time.time;
            int n = 0;
            if (on)
            {
                var cam = _camera.transform.position;
                foreach (var e in WaterSplashHooks.Pending)
                    if (now - e.Time < 0.5f && now >= e.Time) WaterRingFromSplash(e, cam, now);
                WaterSplashHooks.Pending.Clear();

                // The bench's frozen test ring first (it is never evicted).
                if (s.testRipple && TestRingPosition(out var tp))
                    for (int j = 0; j < Mathf.Clamp(s.testRings, 1, MaxWaterRings); j++, n++)
                    {
                        float a = 2.4f * j, rad = 1.6f * Mathf.Sqrt(j);
                        _waterRingA[n] = new Vector4(tp.x + rad * Mathf.Cos(a), tp.z + rad * Mathf.Sin(a), Mathf.Max(s.testAge, 0f), s.testAmplitude * s.impulses);
                        _waterRingB[n] = new Vector4(tp.y, 2.0f, 0.7f, 1.6f);
                    }
                for (int i = _waterRingCount - 1; i >= 0; i--)
                {
                    var r = _waterRings[i];
                    float age = now - r.Born;
                    if (age < 0f || age > 5f * r.Decay || r.Amp * Mathf.Exp(-age / r.Decay) < 0.004f) _waterRings[i] = _waterRings[--_waterRingCount];
                }
                for (int i = 0; i < _waterRingCount && n < MaxWaterRings; i++, n++)
                {
                    var r = _waterRings[i];
                    _waterRingA[n] = new Vector4(r.X, r.Z, now - r.Born, r.Amp * s.impulses);
                    _waterRingB[n] = new Vector4(r.Y, r.Speed, r.Lambda, r.Decay);
                }
            }
            else WaterSplashHooks.Pending.Clear();
            if (n == 0 && _waterRingsUploaded == 0) return;
            for (int i = n; i < MaxWaterRings; i++) { _waterRingA[i] = Vector4.zero; _waterRingB[i] = Vector4.zero; }
            // Immediate globals: read by PathTrace.raytrace (RayGen) and the water overlays later this frame.
            Shader.SetGlobalVectorArray(WaterImpulsesId, _waterRingA);
            Shader.SetGlobalVectorArray(WaterImpulses2Id, _waterRingB);
            Shader.SetGlobalVector(WaterImpulseId, new Vector4(n, 0f, 0f, 0f));
            _waterRingsUploaded = n;
        }

        /// <summary>The bench's test ring: where the view, tilted down by 0 to 21 degrees (above the gun), meets a ripple water surface from
        /// above; of those, the hit nearest to 6 m from the camera (close enough to read, far enough to see whole).</summary>
        private bool TestRingPosition(out Vector3 p)
        {
            p = Vector3.zero;
            var t = _camera.transform;
            float best = float.MaxValue;
            int bestDeg = -1;
            for (int deg = 0; deg <= 21; deg += 3)
            {
                var d = Quaternion.AngleAxis(deg, t.right) * t.forward;
                if (!_proxies.WaterAlongRay(t.position, d.normalized, 3f, 40f, out var q)) continue;
                float score = Mathf.Abs(Vector3.Distance(q, t.position) - 6f);
                if (score < best) { best = score; p = q; bestDeg = deg; }
            }
            if (!_waterTestLogged)
            {
                _waterTestLogged = true;
                if (bestDeg >= 0) Logger.LogInfo(FormattableString.Invariant($"Water ripples: test ring at ({p.x:F2}, {p.y:F2}, {p.z:F2}), {Vector3.Distance(p, t.position):F1} m from the camera, view tilted {bestDeg} deg"));
                else Logger.LogWarning("Water ripples: test ring: no water surface in front of the camera");
            }
            return bestDeg >= 0;
        }

        private void WaterRingFromSplash(WaterSplashHooks.Event e, Vector3 cam, float now)
        {
            var s = _waterImpulseSettings;
            if (e.Wake ? !s.wakes : !s.splashes) return;
            if (e.Enemy && !s.enemies) return;
            if ((e.Position - cam).sqrMagnitude > s.maxDistance * s.maxDistance) return;
            float size = Mathf.Clamp(e.Size, 0.2f, 4f);
            float sizeF = Mathf.Clamp(size / 2.5f, 0.3f, 1.2f); // V1's collider: about 1.1
            if (!_proxies.WaterSurfaceAt(e.Position, e.Wake ? 1.5f : size + 2f, 1f, out float y)) y = e.Wake ? e.Position.y : float.NaN;
            if (float.IsNaN(y)) return; // not over a water surface we trace
            float hs = new Vector2(e.Velocity.x, e.Velocity.z).magnitude;
            if (e.Wake)
            {
                float amp = Mathf.Clamp(0.025f + 0.003f * hs, 0.025f, 0.09f) * sizeF * (e.Enemy ? 0.7f : 1f);
                AddWaterRing(e.Position.x, e.Position.z, y, now, amp, 1.5f, 0.5f, 0.9f, cam, null);
            }
            else
            {
                float impact = Mathf.Abs(e.Velocity.y) + 0.25f * hs;
                float amp = Mathf.Clamp(0.05f + 0.006f * impact, 0.05f, 0.24f) * sizeF;
                if (e.Big) amp = Mathf.Max(amp, 0.2f * sizeF);
                float lambda = Mathf.Clamp(0.4f + 0.12f * size, 0.45f, 0.9f);
                AddWaterRing(e.Position.x, e.Position.z, y, now, amp, Mathf.Min(1.8f + 0.02f * impact, 2.6f), lambda, 1.6f, cam,
                             e.Player ? (e.Big ? "big splash (V1)" : "splash (V1)") : e.Enemy ? "splash (enemy)" : "splash");
            }
        }

        /// <summary>EffectLights' spawn queue (with Plugin.Vfx's shockwaves): an explosion within 4.5 m of a water surface
        /// (and over it, give or take its size) rings the water.</summary>
        private void WaterImpulseOnSpawn(GameObject go)
        {
            var s = _waterImpulseSettings;
            if (s.impulses <= 0f || !s.explosions || _proxies == null || _camera == null || go == null) return;
            go.GetComponentsInChildren(true, _waterExplosions);
            if (_waterExplosions.Count == 0) return;
            float size = 0f;
            foreach (var e in _waterExplosions) if (e != null) size = Mathf.Max(size, e.maxSize);
            _waterExplosions.Clear();
            if (size <= 0f) return;
            var p = go.transform.position;
            var cam = _camera.transform.position;
            if ((p - cam).sqrMagnitude > s.maxDistance * s.maxDistance) return;
            if (!_proxies.WaterSurfaceAt(p, 4.5f, Mathf.Min(size, 6f), out float y)) return;
            float near = 1f - 0.6f * Mathf.Clamp01(Mathf.Abs(p.y - y) / 4.5f);
            float amp = Mathf.Clamp(0.12f + 0.015f * size, 0.12f, 0.3f) * near;
            AddWaterRing(p.x, p.z, y, Time.time, amp, 2.8f, Mathf.Clamp(0.6f + 0.06f * size, 0.7f, 1.4f), 2.2f, cam,
                         FormattableString.Invariant($"explosion '{go.name}' size {size:F1}"));
        }

        /// <summary>Into the ring buffer; when it is full, the ring with the least left (slope now, less with distance) goes,
        /// unless the new one has even less.</summary>
        private void AddWaterRing(float x, float z, float y, float born, float amp, float speed, float lambda, float decay, Vector3 cam, string what)
        {
            int cap = MaxWaterRings - (_waterImpulseSettings.testRipple ? Mathf.Clamp(_waterImpulseSettings.testRings, 1, MaxWaterRings) : 0);
            if (cap <= 0) return;
            float now = Time.time;
            float Weight(float a, float b0, float d0, float rx, float rz) =>
                a * Mathf.Exp(-(now - b0) / d0) / (1f + Mathf.Sqrt((rx - cam.x) * (rx - cam.x) + (rz - cam.z) * (rz - cam.z)) / 30f);
            int slot = _waterRingCount;
            if (_waterRingCount >= cap)
            {
                slot = -1;
                float least = Weight(amp, born, decay, x, z);
                for (int i = 0; i < _waterRingCount; i++)
                {
                    var r = _waterRings[i];
                    float w = Weight(r.Amp, r.Born, r.Decay, r.X, r.Z);
                    if (w < least) { least = w; slot = i; }
                }
                if (slot < 0) return;
            }
            else _waterRingCount++;
            _waterRings[slot] = new WaterRing { X = x, Z = z, Y = y, Born = born, Amp = amp, Speed = speed, Lambda = lambda, Decay = decay };
            if (what != null && (_waterRingLogs < 20 || Time.realtimeSinceStartup >= _waterRingNextLog))
            {
                _waterRingLogs++;
                _waterRingNextLog = Time.realtimeSinceStartup + 2f;
                Logger.LogInfo(FormattableString.Invariant($"Water ripples: {what} at ({x:F1}, {y:F2}, {z:F1}) slope {amp:F3}, wavelength {lambda:F2} m, {_waterRingCount} ring(s)"));
            }
        }
    }
}
