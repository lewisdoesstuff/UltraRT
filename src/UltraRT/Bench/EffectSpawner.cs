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
        /// <summary>"whiplash": seconds the button is held (the hook flies out until released).</summary>
        public static float WhiplashHold = 0.25f;
        private static int _failures;

        public static void Spawn(EffectSpec spec, bool periodic = false)
        {
            var kinds = periodic && spec?.PeriodicKinds != null ? spec.PeriodicKinds : spec?.Kinds;
            if (kinds == null) return;
            var cc = MonoSingleton<CameraController>.Instance;
            if (cc == null) return;
            var cam = cc.transform;
            foreach (var kind in kinds)
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
                case "coin":
                case "coinfront":
                {
                    // The revolver's coin, hovering (kinematic): "coin" 1.5 m behind the camera (seen in reflections ahead),
                    // "coinfront" at Distance in front.
                    GameObject prefab = null;
                    foreach (var r in UnityEngine.Object.FindObjectsOfType<Revolver>(true))
                        if (r != null && r.coin != null) { prefab = r.coin; break; }
                    if (prefab == null) throw new InvalidOperationException("no Revolver with a coin prefab");
                    var p = kind == "coin" ? cam.position - cam.forward * 1.5f + cam.up * 0.2f : InFront(cam, distance);
                    var go = UnityEngine.Object.Instantiate(prefab, p, cam.rotation);
                    foreach (var rb in go.GetComponentsInChildren<Rigidbody>(true)) { rb.isKinematic = true; rb.useGravity = false; }
                    break;
                }
                case "opendoors":
                    // The game's own FinalDoor.Open() on every final door (lit frame materials swapped in, door light on):
                    // tests the runtime material sync (docs/scene-proxies.md) without playing the level to that point.
                    foreach (var fd in UnityEngine.Object.FindObjectsOfType<FinalDoor>()) if (fd != null && !fd.aboutToOpen) fd.Open();
                    break;
                case "flash":
                    // Light only (no particles, so replays are identical): a revolver-like muzzle light, 20 / range 5, fading
                    // out over 0.2 s like RevolverBeam's light.
                    SpawnLight(cam.position + cam.right * 0.35f - cam.up * 0.3f + cam.forward * 0.6f, new Color(1f, 0.85f, 0.6f), 20f, 5f, 0f, 0.2f);
                    break;
                case "blast":
                    // Light only: an explosion-like light, 60 / range 10, rising over 0.05 s and fading over 0.4 s.
                    SpawnLight(InFront(cam, distance), new Color(1f, 0.55f, 0.2f), 60f, 10f, 0.05f, 0.4f);
                    break;
                case "holdtorch":
                {
                    // Puts the level's torch (4-3) in the player's hand as picking it up does (Punch.ForceHold): Torch.Update
                    // then keeps its light at the player. Recordings hold the camera path only, not the item.
                    var fc = MonoSingleton<FistControl>.Instance;
                    if (fc == null || fc.currentPunch == null) throw new InvalidOperationException("no FistControl / punch");
                    if (fc.heldObject != null && fc.heldObject.GetComponent<Torch>() != null) break; // already held
                    Torch best = null;
                    float bestD = float.MaxValue;
                    foreach (var t in UnityEngine.Object.FindObjectsOfType<Torch>(true))
                    {
                        if (t == null) continue;
                        float d = (t.transform.position - cam.position).sqrMagnitude;
                        if (d < bestD) { bestD = d; best = t; }
                    }
                    if (best == null) throw new InvalidOperationException("no Torch in the level");
                    var itid = best.GetComponent<ItemIdentifier>();
                    if (itid == null) throw new InvalidOperationException("Torch without ItemIdentifier");
                    if (!best.gameObject.activeSelf) best.gameObject.SetActive(true);
                    fc.currentPunch.ForceHold(itid);
                    Log?.LogInfo($"EffectSpawner: holding torch '{best.name}' (was {Mathf.Sqrt(bestD):F1} m away)");
                    break;
                }
                case "lightbrazier":
                {
                    // As the held torch does when it hits something (Torch.HitWith -> Flammable.Burn(4)): the nearest
                    // flammable within 6 m of the camera, once (4-3's brazier, whose special activation opens the door).
                    Flammable best = null;
                    float bestD = 144f;
                    foreach (var f in UnityEngine.Object.FindObjectsOfType<Flammable>())
                    {
                        if (f == null || f.enemyOnly || f.burning || _lit.Contains(f) || f.transform.IsChildOf(cam) || f.gameObject.layer == 13 || f.gameObject.layer == 22) continue; // not the held torch
                        float d = (f.transform.position - cam.position).sqrMagnitude;
                        if (d < bestD) { bestD = d; best = f; }
                    }
                    if (best == null) break; // not there yet (12 m)
                    _lit.Add(best);
                    best.Burn(4f);
                    Log?.LogInfo($"EffectSpawner: lit '{best.name}' (special {best.specialFlammable}) {Mathf.Sqrt(bestD):F1} m away");
                    break;
                }
                case "whiplashlong":
                    // Held for a second: the hook (and a held torch) stays out at its farthest before coming back.
                    BenchWhiplash.Press(BenchWhiplash.Action.Hook, 1f);
                    break;
                case "whiplash":
                    // Presses the whiplash (R) as the player would: InputSource.Hook performed next frame, held for
                    // WhiplashHold seconds of game time, then released (HookArm pulls back). With a held item (4-3's
                    // torch) the item flies out on the hook and its light with it (Torch.Update stops pinning it to the
                    // player while hooked). Recordings hold the camera path only, not these inputs.
                    BenchWhiplash.Press(BenchWhiplash.Action.Hook, WhiplashHold);
                    break;
                case "melee":
                    // Presses melee (the punch) for one frame, as the player would: with the torch held, a hit lights what
                    // it hits (Torch.HitWith / HitSurface), e.g. 4-3's brazier that opens the door.
                    BenchWhiplash.Press(BenchWhiplash.Action.Punch, 0.05f);
                    break;
                default: throw new ArgumentException("unknown effect kind");
            }
        }

        private static GameObject _lightTemplate;
        private static readonly System.Collections.Generic.HashSet<Flammable> _lit = new System.Collections.Generic.HashSet<Flammable>();

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

    /// <summary>Simulated whiplash button (EffectSpawner "whiplash"): sets the game's InputActionState for Hook.</summary>
    internal sealed class BenchWhiplash : MonoBehaviour
    {
        public enum Action { Hook, Punch }
        private static BenchWhiplash _instance;
        private Action _held;
        private static readonly System.Reflection.PropertyInfo PerformedFrame = typeof(InputActionState).GetProperty("PerformedFrame");
        private static readonly System.Reflection.PropertyInfo PerformedTime = typeof(InputActionState).GetProperty("PerformedTime");
        private static readonly System.Reflection.PropertyInfo IsPressed = typeof(InputActionState).GetProperty("IsPressed");
        private float _release = -1f;

        private static InputActionState State(Action a)
        {
            var src = MonoSingleton<InputManager>.Instance?.InputSource;
            return src == null ? null : a == Action.Hook ? src.Hook : src.Punch;
        }

        public static void Press(Action a, float hold)
        {
            var h = State(a);
            if (h == null) throw new InvalidOperationException("no InputManager / Hook action");
            if (_instance == null)
            {
                var go = new GameObject("UltraRT bench whiplash");
                DontDestroyOnLoad(go);
                _instance = go.AddComponent<BenchWhiplash>();
            }
            // Next frame: HookArm.Update sees WasPerformedThisFrame whether it runs before or after us.
            PerformedFrame.SetValue(h, Time.frameCount + 1);
            PerformedTime.SetValue(h, Time.time);
            IsPressed.SetValue(h, true);
            if (_instance._release >= 0f && _instance._held != a) { var prev = State(_instance._held); if (prev != null) IsPressed.SetValue(prev, false); }
            _instance._held = a;
            _instance._release = Time.time + Mathf.Max(0.02f, hold);
        }

        private void Update()
        {
            if (_release < 0f || Time.time < _release) return;
            _release = -1f;
            var h = State(_held);
            if (h != null) IsPressed.SetValue(h, false);
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
