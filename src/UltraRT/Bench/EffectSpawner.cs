using System;
using BepInEx.Logging;
using UnityEngine;

namespace UltraRT.Bench
{
    /// <summary>
    /// Spawns combat effects in front of the camera for effect-light benchmarks (Scenario.Effects). Everything is the
    /// game's own prefabs: the revolver beam (muzzle light, line renderer, hit sparks), DefaultReferenceManager's
    /// explosions (made harmless) and its enemy projectile. Failures are logged once and the kind is skipped.
    /// </summary>
    internal static class EffectSpawner
    {
        public static ManualLogSource Log;
        private static int _failures;

        public static void Spawn(EffectSpec spec)
        {
            if (spec?.Kinds == null) return;
            var cc = MonoSingleton<CameraController>.Instance;
            if (cc == null) return;
            var cam = cc.transform;
            foreach (var kind in spec.Kinds)
            {
                try { SpawnOne((kind ?? "").ToLowerInvariant(), cam, Mathf.Max(0.5f, spec.Distance)); }
                catch (Exception e)
                {
                    if (_failures++ < 8) Log?.LogWarning($"EffectSpawner: '{kind}' failed: {e.GetType().Name}: {e.Message}");
                }
            }
        }

        private static Vector3 InFront(Transform cam, float distance)
        {
            var dir = cam.forward;
            if (Physics.Raycast(cam.position, dir, out var hit, distance + 0.5f, ~(1 << 2), QueryTriggerInteraction.Ignore))
                distance = Mathf.Max(0.5f, hit.distance - 0.75f);
            return cam.position + dir * distance;
        }

        private static void SpawnOne(string kind, Transform cam, float distance)
        {
            var refs = MonoSingleton<DefaultReferenceManager>.Instance;
            switch (kind)
            {
                case "revolver":
                {
                    Revolver rev = null;
                    foreach (var r in UnityEngine.Object.FindObjectsOfType<Revolver>(true))
                        if (r != null && r.revolverBeam != null) { rev = r; if (r.isActiveAndEnabled) break; }
                    if (rev == null) throw new InvalidOperationException("no Revolver with a beam prefab");
                    var go = UnityEngine.Object.Instantiate(rev.revolverBeam, cam.position, cam.rotation);
                    var beam = go.GetComponent<RevolverBeam>();
                    if (beam != null)
                    {
                        beam.sourceWeapon = rev.gameObject;
                        beam.alternateStartPoint = cam.position + cam.right * 0.35f - cam.up * 0.3f + cam.forward * 0.6f;
                    }
                    break;
                }
                case "explosion":
                case "superexplosion":
                {
                    var prefab = kind == "explosion" ? refs?.explosion : refs?.superExplosion;
                    if (prefab == null) throw new InvalidOperationException("no explosion prefab");
                    var go = UnityEngine.Object.Instantiate(prefab, InFront(cam, distance), Quaternion.identity);
                    foreach (var e in go.GetComponentsInChildren<Explosion>(true)) e.harmless = true;
                    break;
                }
                case "projectile":
                {
                    if (refs?.projectile == null) throw new InvalidOperationException("no projectile prefab");
                    UnityEngine.Object.Instantiate(refs.projectile, cam.position + cam.forward * Mathf.Min(2f, distance), cam.rotation);
                    break;
                }
                case "fire":
                case "firesimple":
                {
                    // FireObjectPool: "Fire" has a light, "FireSimple" only a flame sprite. Placed on the floor ahead.
                    var pool = MonoSingleton<FireObjectPool>.Instance;
                    if (pool == null) throw new InvalidOperationException("no FireObjectPool");
                    var go = pool.GetFire(kind == "firesimple");
                    var p = InFront(cam, distance);
                    if (Physics.Raycast(p, Vector3.down, out var floor, 10f, ~(1 << 2), QueryTriggerInteraction.Ignore)) p = floor.point;
                    // As Flammable.Burn does: unparent, scale to the burning object's size, switch the light on.
                    go.transform.SetParent(null, true);
                    go.transform.position = p;
                    go.transform.localScale = Vector3.one * 2f;
                    var light = go.GetComponent<Light>();
                    if (light != null) light.enabled = true;
                    break;
                }
                case "flash":
                    // Light only (no particles, so replays are identical): a revolver-like muzzle light, 20 / range 5, fading
                    // out over 0.2 s like RevolverBeam's light.
                    SpawnLight(cam.position + cam.right * 0.35f - cam.up * 0.3f + cam.forward * 0.6f, new Color(1f, 0.85f, 0.6f), 20f, 5f, 0f, 0.2f);
                    break;
                case "blast":
                    // Light only: an explosion-like light, 60 / range 10, rising over 0.05 s and fading over 0.4 s.
                    SpawnLight(InFront(cam, distance), new Color(1f, 0.55f, 0.2f), 60f, 10f, 0.05f, 0.4f);
                    break;
                default: throw new ArgumentException("unknown effect kind");
            }
        }

        private static GameObject _lightTemplate;

        /// <summary>Instantiates a light-only effect (through Object.Instantiate, so EffectLights sees it spawn like the game's).</summary>
        private static void SpawnLight(Vector3 pos, Color color, float peak, float range, float rise, float fall)
        {
            if (_lightTemplate == null)
            {
                _lightTemplate = new GameObject("UltraRT bench light");
                _lightTemplate.SetActive(false);
                UnityEngine.Object.DontDestroyOnLoad(_lightTemplate);
                var l = _lightTemplate.AddComponent<Light>();
                l.type = LightType.Point;
                l.shadows = LightShadows.None;
                _lightTemplate.AddComponent<BenchLightFade>();
            }
            var go = UnityEngine.Object.Instantiate(_lightTemplate, pos, Quaternion.identity);
            var light = go.GetComponent<Light>();
            light.color = color;
            light.range = range;
            light.intensity = rise > 0f ? 0f : peak;
            var fade = go.GetComponent<BenchLightFade>();
            fade.Peak = peak; fade.Rise = rise; fade.Fall = fall;
            go.SetActive(true);
        }
    }

    /// <summary>Intensity envelope of a bench light (game time, so a fixed capture clock replays it exactly).</summary>
    internal sealed class BenchLightFade : MonoBehaviour
    {
        public float Peak, Rise, Fall;
        private float _age;
        private Light _light;

        private void Update()
        {
            if (_light == null) _light = GetComponent<Light>();
            _age += Time.deltaTime;
            float k = _age < Rise ? _age / Rise : 1f - (_age - Rise) / Mathf.Max(Fall, 1e-3f);
            if (k <= 0f) { Destroy(gameObject); return; }
            _light.intensity = Peak * Mathf.Clamp01(k);
        }
    }
}
