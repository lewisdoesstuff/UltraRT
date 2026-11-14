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
                case "blood":
                {
                    // A body-sized blood splatter from the game's gore pool (spray particles, drops, stains where it lands).
                    var bsm = MonoSingleton<BloodsplatterManager>.Instance;
                    if (bsm == null) throw new InvalidOperationException("no BloodsplatterManager");
                    var go = bsm.GetGore(GoreType.Body);
                    if (go == null) throw new InvalidOperationException("no gore");
                    go.transform.position = InFront(cam, distance);
                    go.transform.rotation = Quaternion.LookRotation(-cam.forward, Vector3.up); // spray toward the camera
                    go.SetActive(true);
                    // The pool activates it (and its splatter starts playing) before we move it: restart at the new place.
                    foreach (var ps in go.GetComponentsInChildren<ParticleSystem>(true)) { ps.Clear(true); ps.Play(true); }
                    break;
                }
                case "dust":
                {
                    // The landing dust puff (NewMovement.impactDust), on the floor ahead.
                    var nm = MonoSingleton<NewMovement>.Instance;
                    if (nm == null || nm.impactDust == null) throw new InvalidOperationException("no impact dust prefab");
                    var p = InFront(cam, distance);
                    if (Physics.Raycast(p, Vector3.down, out var floor, 10f, ~(1 << 2), QueryTriggerInteraction.Ignore)) p = floor.point;
                    UnityEngine.Object.Instantiate(nm.impactDust, p, Quaternion.identity);
                    break;
                }
                default: throw new ArgumentException("unknown effect kind");
            }
        }
    }
}
