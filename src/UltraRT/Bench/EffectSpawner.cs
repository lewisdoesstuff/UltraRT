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

        private static readonly System.Reflection.MethodInfo RevolverShoot =
            typeof(Revolver).GetMethod("Shoot", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        private static int _shootFailures, _beamLogs, _shotgunLogs;
        private static readonly System.Reflection.MethodInfo ShotgunShoot =
            typeof(Shotgun).GetMethod("Shoot", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic, null, Type.EmptyTypes, null);

        /// <summary>Revolver.Shoot(1) on the held revolver: the shot exactly as the player fires it.</summary>
        private static bool ShootRevolver(Revolver rev)
        {
            if (RevolverShoot == null || _shootFailures > 3) return false;
            try
            {
                RevolverShoot.Invoke(rev, new object[] { 1 });
                if (_beamLogs++ < 2)
                {
                    var cam = MonoSingleton<CameraController>.Instance.transform;
                    Log?.LogInfo($"EffectSpawner: revolver shot by {GameControl.PathOf(rev.transform)}, barrel at {Local(cam, rev.gunBarrel != null ? rev.gunBarrel.transform.position : cam.position)} (camera space)");
                }
                return true;
            }
            catch (Exception e)
            {
                _shootFailures++;
                Log?.LogWarning($"EffectSpawner: Revolver.Shoot failed ({(e.InnerException ?? e).GetType().Name}: {(e.InnerException ?? e).Message}), spawning the beam instead");
                return false;
            }
        }

        private static Vector3 Local(Transform cam, Vector3 p) => cam.InverseTransformPoint(p);

        private static void LogBeam(GameObject go, Transform cam, Revolver rev)
        {
            var sb = new System.Text.StringBuilder($"EffectSpawner: revolver beam '{go.name}' at {Local(cam, go.transform.position)} (camera space), barrel {(rev.gunBarrel != null ? Local(cam, rev.gunBarrel.transform.position).ToString() : "-")}; lights:");
            foreach (var l in go.GetComponentsInChildren<Light>(true))
                sb.Append($" {l.name} {l.type} I {l.intensity:F1} range {l.range:F1} at {Local(cam, l.transform.position)} mask 0x{l.cullingMask:X8};");
            Log?.LogInfo(sb.ToString());
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
            if (kind.StartsWith("enemy:", StringComparison.OrdinalIgnoreCase)) { SpawnEnemy(kind.Substring(6), cam, distance); return; }
            switch (kind)
            {
                case "revolver":
                case "revolverbeam":
                {
                    Revolver rev = null;
                    foreach (var r in UnityEngine.Object.FindObjectsOfType<Revolver>(true))
                        if (r != null && r.revolverBeam != null) { rev = r; if (r.isActiveAndEnabled) break; }
                    if (rev == null) throw new InvalidOperationException("no Revolver with a beam prefab");
                    // The held revolver fires as the game fires it (Revolver.Shoot): the beam starts at the camera with the
                    // gun barrel as its start point, the muzzle flash sits at the barrel, the gun plays its recoil.
                    if (kind == "revolver" && rev.isActiveAndEnabled && ShootRevolver(rev)) break;
                    var go = UnityEngine.Object.Instantiate(rev.revolverBeam, cam.position, cam.rotation);
                    var beam = go.GetComponent<RevolverBeam>();
                    if (beam != null)
                    {
                        beam.sourceWeapon = rev.gameObject;
                        // As Revolver.Shoot: the barrel is where the beam and its muzzle flash start (a point right of and
                        // below the camera when the revolver isn't out).
                        beam.alternateStartPoint = rev.gunBarrel != null && rev.gunBarrel.activeInHierarchy
                            ? rev.gunBarrel.transform.position
                            : cam.position + cam.right * 0.35f - cam.up * 0.3f + cam.forward * 0.6f;
                    }
                    if (_beamLogs++ < 2) LogBeam(go, cam, rev);
                    break;
                }
                case "shotgun":
                {
                    // The held shotgun fires as the player fires it (Shotgun.Shoot): pellets, muzzle flashes at its shoot
                    // points (with their lights), recoil. Only while it is out ("Weapon": 2).
                    Shotgun sg = null;
                    foreach (var s in UnityEngine.Object.FindObjectsOfType<Shotgun>(false))
                        if (s != null && s.isActiveAndEnabled) { sg = s; break; }
                    if (sg == null) throw new InvalidOperationException("no shotgun out (scenario \"Weapon\": 2)");
                    if (ShotgunShoot == null) throw new InvalidOperationException("Shotgun.Shoot not found");
                    ShotgunShoot.Invoke(sg, null);
                    if (_shotgunLogs++ < 2) Log?.LogInfo($"EffectSpawner: shotgun shot by {GameControl.PathOf(sg.transform)} (variation {sg.variation})");
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
                case "bloodstain":
                {
                    // Blood stains straight into the game's stain buffer (as a splatter's drops do when they land), on
                    // the floor and the walls ahead: for the wet-blood look (docs/vfx.md), which needs fresh stains.
                    // (Never GoreZone.ResolveGoreZone on the camera: it reparents what it is given.)
                    GoreZone any = null;
                    foreach (var g in UnityEngine.Object.FindObjectsOfType<GoreZone>()) if (g != null && g.stains != null) { any = g; break; }
                    if (any == null)
                    {
                        // As the game does for gore with no zone (GoreZone.ResolveGoreZone): a zone of its own.
                        any = new GameObject("UltraRT bench gore zone").AddComponent<GoreZone>();
                        if (any.stains == null) any.stains = any.gameObject.AddComponent<BloodstainParent>();
                    }
                    int made = 0, hits = 0;
                    for (int k = 0; k < 9; k++)
                    {
                        var dir = Quaternion.AngleAxis((k % 3 - 1) * 14f, cam.up) * Quaternion.AngleAxis(8f + (k / 3) * 12f, cam.right) * cam.forward;
                        if (!Physics.Raycast(cam.position, dir, out var hit, 40f, ~((1 << 2) | (1 << 12) | (1 << 13)), QueryTriggerInteraction.Ignore)) continue;
                        hits++;
                        var gz = hit.collider != null ? hit.collider.GetComponentInParent<GoreZone>() : null;
                        if (gz == null || gz.stains == null) gz = any;
                        if (gz == null) continue;
                        gz.stains.CreateChild(hit.point, hit.normal, true, false);
                        made++;
                    }
                    if (made == 0) throw new InvalidOperationException($"no stain made ({hits} surfaces hit, gore zone {(any != null ? any.name : "none")})");
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
                case "holditem":
                {
                    // The nearest item that is not a torch (a skull, a soap bar) in the player's hand, as picking it up
                    // does: the held skull of the user's 2-4 pose (docs/scene-proxies.md, "Held items").
                    var fc = MonoSingleton<FistControl>.Instance;
                    if (fc == null || fc.currentPunch == null) throw new InvalidOperationException("no FistControl / punch");
                    if (fc.heldObject != null) break; // already held
                    ItemIdentifier best = null;
                    float bestD = float.MaxValue;
                    foreach (var it in UnityEngine.Object.FindObjectsOfType<ItemIdentifier>())
                    {
                        if (it == null || it.GetComponent<Torch>() != null) continue;
                        float d = (it.transform.position - cam.position).sqrMagnitude;
                        if (d < bestD) { bestD = d; best = it; }
                    }
                    if (best == null) throw new InvalidOperationException("no item in the level");
                    fc.currentPunch.ForceHold(best);
                    Log?.LogInfo($"EffectSpawner: holding item '{best.name}' layer {best.gameObject.layer} (was {Mathf.Sqrt(bestD):F1} m away)");
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
                case "parry":
                    // The parry light burst alone (CombatLights.cs), ~1.2 m ahead: the game's own parry would freeze the
                    // capture (0.25 s hit-stop) and white it out (the screen flash, with Options > parry flash on).
                    UltraRT.CombatLights.TriggerParry();
                    break;
                case "parrygame":
                {
                    // The game's own parry flash (TimeController.ParryFlash: its light at the player, the hit-stop, the white
                    // screen flash): the Harmony hook's path. Capture at least 0.4 s later to miss the white frame.
                    var tc = MonoSingleton<TimeController>.Instance;
                    if (tc == null) throw new InvalidOperationException("no TimeController");
                    tc.ParryFlash();
                    if (_parryLogs++ < 2)
                    {
                        var target = MonoSingleton<PlayerTracker>.Instance != null ? MonoSingleton<PlayerTracker>.Instance.GetTarget() : null;
                        var sb = new System.Text.StringBuilder("EffectSpawner: parry flash; lights at the player:");
                        if (target != null)
                            foreach (var l in target.GetComponentsInChildren<Light>(true))
                                sb.Append($" '{l.name}' {l.type} I {l.intensity:F1} range {l.range:F1} colour ({l.color.r:F2}, {l.color.g:F2}, {l.color.b:F2}) on {l.isActiveAndEnabled};");
                        Log?.LogInfo(sb.ToString());
                    }
                    break;
                }
                case "lightning":
                case "boltlight":
                {
                    // 5-2's decorative bolts: the one most in front of the camera. "lightning" starts it as the game does
                    // (FlashStart: the build-up, then the strike and the game's flash light buildupTime later, and the
                    // bolt light with it); "boltlight" only starts the bolt light, at once.
                    LightningStrikeDecorative_Animated best = null;
                    float bestDot = -2f;
                    foreach (var l in UnityEngine.Object.FindObjectsOfType<LightningStrikeDecorative_Animated>())
                    {
                        if (l == null) continue;
                        float dot = Vector3.Dot((l.transform.position - cam.position).normalized, cam.forward);
                        if (dot > bestDot) { bestDot = dot; best = l; }
                    }
                    if (best == null) throw new InvalidOperationException("no LightningStrikeDecorative_Animated in this level");
                    if (kind == "boltlight") UltraRT.CombatLights.ForcedBolts.Add(best);
                    else
                    {
                        if (FlashStart == null) throw new InvalidOperationException("LightningStrikeDecorative_Animated.FlashStart not found");
                        FlashStart.Invoke(best, null);
                    }
                    if (_boltLogs++ < 3)
                        Log?.LogInfo($"EffectSpawner: {kind} on '{GameControl.PathOf(best.transform)}' ({Vector3.Distance(best.transform.position, cam.position):F0} m, in front {bestDot:F2}), build-up {(BuildupTime != null ? BuildupTime.GetValue(best) : "?")} s");
                    break;
                }
                case "beamchain":
                {
                    // A revolver shot that bounces twice, built as the game builds ricochets and coin shots: one beam object
                    // per segment (RevolverBeam.fake: drawn and lit, no damage), from the gun to the wall ahead and off it.
                    Revolver rev = null;
                    foreach (var r in UnityEngine.Object.FindObjectsOfType<Revolver>(true))
                        if (r != null && r.revolverBeam != null) { rev = r; if (r.isActiveAndEnabled) break; }
                    if (rev == null) throw new InvalidOperationException("no Revolver with a beam prefab");
                    var p0 = cam.position + cam.right * 0.35f - cam.up * 0.3f + cam.forward * 0.6f;
                    var dir = cam.forward;
                    int mask = LayerMaskDefaults.Get(LMD.Environment);
                    var start = cam.position;
                    for (int seg = 0; seg < 3; seg++)
                    {
                        Vector3 p1;
                        Vector3 normal = Vector3.zero;
                        if (Physics.Raycast(start, dir, out var hit, seg == 0 ? 80f : 40f, mask, QueryTriggerInteraction.Ignore)) { p1 = hit.point; normal = hit.normal; }
                        else p1 = start + dir * (seg == 0 ? 80f : 40f);
                        FakeBeam(rev.revolverBeam, rev.gameObject, p0, p1);
                        if (normal == Vector3.zero) break;
                        dir = Vector3.Reflect(dir, normal);
                        p0 = p1;
                        start = p1 + normal * 0.05f;
                    }
                    break;
                }
                case "railbeam":
                {
                    // The railcannon's beam (its prefab, RevolverBeam type Railgun) as a fake shot from the gun to the wall ahead.
                    GameObject prefab = null, src = null;
                    foreach (var r in UnityEngine.Object.FindObjectsOfType<Railcannon>(true))
                        if (r != null && r.beam != null) { prefab = r.beam; src = r.gameObject; break; }
                    if (prefab == null) throw new InvalidOperationException("no Railcannon with a beam prefab (none in the save?)");
                    var p0 = cam.position + cam.right * 0.35f - cam.up * 0.3f + cam.forward * 0.6f;
                    var p1 = Physics.Raycast(cam.position, cam.forward, out var hit, 120f, LayerMaskDefaults.Get(LMD.Environment), QueryTriggerInteraction.Ignore)
                        ? hit.point : cam.position + cam.forward * 120f;
                    FakeBeam(prefab, src, p0, p1);
                    break;
                }
                default: throw new ArgumentException("unknown effect kind");
            }
        }

        private static int _parryLogs, _boltLogs;
        private static readonly System.Reflection.MethodInfo FlashStart =
            typeof(LightningStrikeDecorative_Animated).GetMethod("FlashStart", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
        private static readonly System.Reflection.FieldInfo BuildupTime =
            typeof(LightningStrikeDecorative_Animated).GetField("buildupTime", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

        /// <summary>One beam segment as Coin.SpawnBeam makes them: the beam prefab, its line set from a to b. fake = true:
        /// RevolverBeam.Start skips Shoot (no hit, no damage) and fades the line out as a real shot's.</summary>
        private static void FakeBeam(GameObject prefab, GameObject source, Vector3 a, Vector3 b)
        {
            var go = UnityEngine.Object.Instantiate(prefab, a, Quaternion.LookRotation(b - a));
            var rb = go.GetComponent<RevolverBeam>();
            if (rb == null) { UnityEngine.Object.Destroy(go); throw new InvalidOperationException($"'{prefab.name}' has no RevolverBeam"); }
            rb.fake = true;
            rb.sourceWeapon = source;
            var lr = go.GetComponent<LineRenderer>();
            if (lr == null) throw new InvalidOperationException($"'{prefab.name}' has no LineRenderer");
            lr.SetPosition(0, a);
            lr.SetPosition(1, b);
        }

        private static readonly System.Collections.Generic.Dictionary<string, GameObject> _benchEnemies = new System.Collections.Generic.Dictionary<string, GameObject>();

        /// <summary>"enemy:&lt;EnemyType&gt;" (e.g. enemy:Drone): a copy of one of the level's own enemies of that type, standing
        /// in front of the camera and facing it; further ones stand beside it. With the scenario's Enemies "blind" it idles.
        /// Spawned once per kind string and level load (write "enemy:Drone", "enemy:drone " for two). The level must
        /// contain the type (a run with -ultrart-enemy-survey lists them per level).</summary>
        private static void SpawnEnemy(string type, Transform cam, float distance)
        {
            if (_benchEnemies.TryGetValue(type, out var have) && have != null) return;
            foreach (var k in new System.Collections.Generic.List<string>(_benchEnemies.Keys)) if (_benchEnemies[k] == null) _benchEnemies.Remove(k);
            string key = type;
            type = type.Trim();
            EnemyIdentifier src = null;
            foreach (var e in Resources.FindObjectsOfTypeAll<EnemyIdentifier>())
            {
                if (e == null || !e.gameObject.scene.IsValid() || !string.Equals(e.enemyType.ToString(), type, StringComparison.OrdinalIgnoreCase)) continue;
                if (e.dead) continue;
                src = e;
                if (!e.gameObject.activeInHierarchy) break; // a dormant one is in its start state
            }
            if (src == null) throw new InvalidOperationException($"no enemy of type '{type}' in this level");
            var fwd = Vector3.ProjectOnPlane(cam.forward, Vector3.up).normalized;
            var right = Vector3.Cross(Vector3.up, fwd);
            int n = _benchEnemies.Count;
            var pos = cam.position + fwd * distance + right * (((n + 1) / 2) * 3f * ((n & 1) == 0 ? 1f : -1f));
            if (Physics.Raycast(pos + Vector3.up * 1f, Vector3.down, out var hit, 30f, LayerMask.GetMask("Environment", "Outdoors"), QueryTriggerInteraction.Ignore)) pos = hit.point;
            else pos += Vector3.down * 1.5f;
            var go = UnityEngine.Object.Instantiate(src.gameObject, pos, Quaternion.LookRotation(-fwd, Vector3.up));
            go.name = "UltraRT bench " + type;
            go.SetActive(true);
            _benchEnemies[key] = go;
            Log?.LogInfo($"Effect spawner: enemy '{type}' (a copy of '{src.name}') at ({pos.x:F1}, {pos.y:F1}, {pos.z:F1})");
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
