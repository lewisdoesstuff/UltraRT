using System;
using System.Collections.Generic;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace UltraRT
{
    /// <summary>
    /// Short-lived lights for three combat moments the game lights weakly or not at all (docs/emissive-lights.md,
    /// "Combat lights"). Tunables are in <see cref="EffectLightSettings"/> (bench prefix fx.).
    ///  1. Beams. Every RevolverBeam (revolver, coin and ricochet segments, railcannon, Malicious Face, turret and other
    ///     enemy beams) and ContinuousBeam (Mindflayer) has a LineRenderer; the game lights only its start (a Light on the
    ///     beam). Here a chain of point lights spaced along each segment's world-space line lights the corridor the beam
    ///     crosses, coloured like the line, and fades with it. Only those two components count: LightPillar cones and other
    ///     decorative lines are never beams. Ricochets and coin shots spawn one beam object per segment, so every bounce
    ///     gets its own chain.
    ///  2. Parry. TimeController.ParryFlash (every parry: punches, projectiles, coins) gets a crisp burst ~1.2 m in front of
    ///     the camera, on unscaled time: bright through the game's 0.25 s hit-stop (Time.timeScale = 0), then a fast fade.
    ///  3. Lightning. 5-2's decorative bolts (LightningStrikeDecorative_Animated), the plain decorative bolts
    ///     (LightningStrikeDecorative: P-2's tunnel of blood) and explosive strikes (LightningStrikeExplosive: the
    ///     Ferryman's) get a brief bolt light along the bolt (blue-white, or the game flash's own hue), with a re-strike
    ///     flicker, on top of the game's own flash light.
    /// All three are packed after the effect lights (<see cref="EffectLights"/>), so they never take a slot from an
    /// explosion or a muzzle flash, and capped. Every envelope ends on real time too, so a paused game never keeps one on.
    /// </summary>
    internal sealed class CombatLights
    {
        public EffectLightSettings Settings;
        private readonly ManualLogSource _log;

        // ---------------------------------------------------------------- beams

        private sealed class Beam
        {
            public LineRenderer Lr;
            public RevolverBeam Rb;      // null: ContinuousBeam
            public ContinuousBeam Cb;
            public float Age, RealAge;   // game / real seconds since the beam was ready (RevolverBeam.Start ran)
            public float Width0;         // the line's width when ready
            public Color Hue;            // max channel 1
            public float Gain, RangeGain;
            public int Id;
            public bool Ready;
            public string Name;
        }

        private readonly List<Beam> _beams = new List<Beam>(32);
        private readonly HashSet<int> _beamIds = new HashSet<int>();
        private readonly List<LineRenderer> _tmpLines = new List<LineRenderer>(8);
        private readonly Vector3[] _pts = new Vector3[32];
        // 7-4 alone has 37 ContinuousBeams in its scene (the Brain's laser barriers).
        private const int MaxBeams = 256;

        // Line-light candidates this frame (sustained beams): squared distance to the camera, beam index << 5 | segment.
        private const int MaxLineCandidates = 512;
        private readonly float[] _lineDist = new float[MaxLineCandidates];
        private readonly int[] _lineKey = new int[MaxLineCandidates];

        /// <summary>The average colour of a texture (EffectLights.TexAverage): a sustained beam's hue when its material is
        /// white and the colour is in the texture (the Mindflayer's beam material, which 7-4's Brain barriers use too).</summary>
        public Func<Texture, Color> TexAverage;

        // ---------------------------------------------------------------- flashes (parry, lightning)

        private enum FlashKind : byte { Parry, Bolt }

        private struct Flash
        {
            public FlashKind Kind;
            public Vector3 A, B;   // parry: A only; bolt: base, top
            public Color Hue;
            public float Intensity, Range, Radius;
            public float Age, RealAge;
            public float Seed;
            public int Id;
            public bool Unshadowed; // a bolt inside the play space (EffectLightSettings.lightningInsideScenes: P-2's tunnel)
        }

        private readonly List<Flash> _flashes = new List<Flash>(8);
        private int _nextFlashId = 1;

        // Events from the Harmony hooks (game code), drained in Update.
        internal static readonly List<Vector3> PendingParries = new List<Vector3>(4);
        internal static readonly List<Vector3> PendingStrikes = new List<Vector3>(4); // explosive strikes: the ground point
        internal static readonly List<LightningStrikeDecorative_Animated> Armed = new List<LightningStrikeDecorative_Animated>(4);
        /// <summary>Plain decorative bolts whose FlashStart ran (the strike and the game's flash are immediate).</summary>
        internal static readonly List<LightningStrikeDecorative> PendingDecor = new List<LightningStrikeDecorative>(4);
        private readonly List<float> _armedAge = new List<float>(4);

        private static readonly AccessTools.FieldRef<LightningStrikeDecorative_Animated, Light> FlashField =
            SafeFieldRef<LightningStrikeDecorative_Animated, Light>("flash");
        private static readonly AccessTools.FieldRef<LightningStrikeDecorative_Animated, AnimatedTexture> AnimField =
            SafeFieldRef<LightningStrikeDecorative_Animated, AnimatedTexture>("lightningAnim");

        private static readonly AccessTools.FieldRef<LightningStrikeDecorative, Light> DecorFlashField =
            SafeFieldRef<LightningStrikeDecorative, Light>("flash");
        private static readonly AccessTools.FieldRef<LightningStrikeDecorative, SpriteRenderer> DecorSpriteField =
            SafeFieldRef<LightningStrikeDecorative, SpriteRenderer>("lightning");

        private static AccessTools.FieldRef<T, F> SafeFieldRef<T, F>(string name) where T : class
        {
            try { return AccessTools.FieldRefAccess<T, F>(name); }
            catch (Exception) { return null; }
        }

        // ---------------------------------------------------------------- state

        private int _sceneHandle = -1;
        private float _holdUntil = -1f, _holdStart = -1f;
        private int _logBeams, _logBolts, _logParries, _logSustained;
        private int _lastPacked;
        /// <summary>Lights packed in the last Append (beams + flashes).</summary>
        public int Packed => _lastPacked;
        public int ActiveBeams => _beams.Count;
        public int ActiveFlashes => _flashes.Count;

        public CombatLights(ManualLogSource log, EffectLightSettings settings) { _log = log; Settings = settings; }

        public void Clear()
        {
            _beams.Clear(); _beamIds.Clear(); _flashes.Clear(); _logSustained = 0;
            PendingParries.Clear(); PendingStrikes.Clear(); Armed.Clear(); _armedAge.Clear(); ForcedBolts.Clear(); PendingDecor.Clear(); ForcedDecor.Clear();
            _holdUntil = -1f; _holdStart = -1f;
        }

        // ---------------------------------------------------------------- spawns

        /// <summary>Every spawned hierarchy (EffectLights.ProcessSpawns): beams among them are tracked.</summary>
        public void OnSpawn(GameObject go)
        {
            if (Settings.beamLights <= 0f || go == null) return;
            go.GetComponentsInChildren(true, _tmpLines);
            foreach (var lr in _tmpLines)
            {
                if (lr == null) continue;
                var rb = lr.GetComponent<RevolverBeam>();
                if (rb != null) { AddBeam(lr, rb, null); continue; }
                var cb = lr.GetComponent<ContinuousBeam>();
                if (cb != null) AddBeam(lr, null, cb);
            }
        }

        private void CollectSceneBeams()
        {
            // Pre-placed enemies (a level's Mindflayer) carry their ContinuousBeam from the start; RevolverBeams in a scene
            // are prefab templates or spawned, never pre-placed live ones.
            int n = 0;
            foreach (var cb in UnityEngine.Object.FindObjectsOfType<ContinuousBeam>(true))
                if (cb != null && cb.gameObject.scene.IsValid()) { AddBeam(cb.GetComponent<LineRenderer>(), null, cb); n++; }
            if (n > 0) _log.LogInfo($"Combat lights: {n} ContinuousBeams in the scene (tracked {_beams.Count}, cap {MaxBeams})");
        }

        private void AddBeam(LineRenderer lr, RevolverBeam rb, ContinuousBeam cb)
        {
            if (lr == null || _beams.Count >= MaxBeams) return;
            int id = lr.GetInstanceID();
            if (!_beamIds.Add(id)) return;
            float gain = 1f, rangeGain = 1f;
            if (rb != null)
            {
                switch (rb.beamType)
                {
                    case BeamType.Railgun: gain = 2.5f; rangeGain = 1.5f; break;
                    case BeamType.MaliciousFace: gain = 2f; rangeGain = 1.3f; break;
                    case BeamType.Enemy: gain = 1.2f; break;
                }
                if (rb.strongAlt) gain *= 1.4f;
            }
            else gain = 1.2f; // Mindflayer: a thick sustained beam
            var hue = BeamHue(lr, cb != null ? TexAverage : null);
            _beams.Add(new Beam { Lr = lr, Rb = rb, Cb = cb, Id = id, Gain = gain, RangeGain = rangeGain, Hue = hue, Name = lr.name });
            if (cb != null && _logSustained++ < 12)
                _log.LogInfo($"Combat lights: sustained beam '{BoltPath(lr.transform)}' width {cb.originalWidth:F2}, hue ({hue.r:F2}, {hue.g:F2}, {hue.b:F2}), " +
                             $"material '{(lr.sharedMaterial != null ? lr.sharedMaterial.name : "-")}', active {lr.gameObject.activeInHierarchy}");
        }

        /// <summary>The line's colour: the gradient's average (rgb x alpha) x the material's tint, normalised to max 1. With
        /// texAverage (sustained beams), x the main texture's average colour as well (normalised first).</summary>
        private static Color BeamHue(LineRenderer lr, Func<Texture, Color> texAverage = null)
        {
            Color c = Color.white;
            bool fromTexture = false;
            try
            {
                var a = lr.startColor; var b = lr.endColor;
                c = new Color((a.r * a.a + b.r * b.a) * 0.5f, (a.g * a.a + b.g * b.a) * 0.5f, (a.b * a.a + b.b * b.a) * 0.5f, 1f);
                var m = lr.sharedMaterial;
                if (m != null)
                {
                    if (m.HasProperty("_TintColor")) c *= m.GetColor("_TintColor");
                    else if (m.HasProperty("_Color")) c *= m.GetColor("_Color");
                    if (m.HasProperty("_EmissiveColor")) { var e = m.GetColor("_EmissiveColor"); if (Mx(e) > 0.05f) c *= e; }
                    var tex = texAverage != null ? m.mainTexture : null;
                    if (tex != null)
                    {
                        // A beam texture is a white-hot core in a coloured glow: the average comes out pale ((1, 0.72,
                        // 0.87) for the Mindflayer's pink beam). The glow's colour is what lights the room: ^4 brings
                        // back its saturation ((1, 0.27, 0.57)), and no white is mixed in below.
                        var t = texAverage(tex);
                        float mt = Mx(t);
                        if (mt > 1e-3f)
                        {
                            t *= 1f / mt;
                            c *= new Color(Mathf.Pow(t.r, 4f), Mathf.Pow(t.g, 4f), Mathf.Pow(t.b, 4f), 1f);
                            fromTexture = true;
                        }
                    }
                }
            }
            catch (Exception) { c = Color.white; }
            float mx = Mx(c);
            if (mx < 1e-3f) return new Color(1f, 0.9f, 0.7f);
            c *= 1f / mx;
            // A beam's core is white-hot: keep a little white in the light so a red line doesn't paint the room pure red.
            return fromTexture ? c : Color.Lerp(c, Color.white, 0.15f);
        }

        private static float Mx(Color c) => Mathf.Max(c.r, Mathf.Max(c.g, c.b));

        // ---------------------------------------------------------------- triggers (hooks, bench)

        /// <summary>The parry point when the game gives none: ~1.2 m in front of the camera, short of a wall.</summary>
        internal static Vector3 ParryPoint(float ahead)
        {
            var cc = MonoSingleton<CameraController>.Instance;
            Transform cam = cc != null ? cc.transform : (Camera.main != null ? Camera.main.transform : null);
            if (cam == null) return Vector3.zero;
            float d = ahead;
            if (Physics.Raycast(cam.position, cam.forward, out var hit, ahead + 0.3f, LayerMaskDefaults.Get(LMD.Environment), QueryTriggerInteraction.Ignore))
                d = Mathf.Max(0.3f, hit.distance - 0.3f);
            return cam.position + cam.forward * d;
        }

        /// <summary>A parry burst at the default point (bench: the burst alone, without the game's hit-stop and white flash).</summary>
        internal static void TriggerParry() => PendingParries.Add(ParryPoint(1.2f));

        /// <summary>Bolt lights to start now for these decorative bolts (bench: the light alone, at the bolt's place).</summary>
        internal static readonly List<LightningStrikeDecorative_Animated> ForcedBolts = new List<LightningStrikeDecorative_Animated>(2);
        /// <summary>The same for plain decorative bolts (P-2's tunnel of blood).</summary>
        internal static readonly List<LightningStrikeDecorative> ForcedDecor = new List<LightningStrikeDecorative>(2);

        // ---------------------------------------------------------------- per frame

        /// <summary>Every frame (OnUpdate): drain hook events, age everything, watch armed bolts. Cheap when idle.</summary>
        public void Update()
        {
            int scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().handle;
            if (scene != _sceneHandle)
            {
                Clear();
                _sceneHandle = scene;
                if (Settings.beamLights > 0f) { try { CollectSceneBeams(); } catch (Exception e) { _log.LogWarning($"Combat lights: scene beams: {e.Message}"); } }
                try { LogSceneBolts(); } catch (Exception e) { _log.LogWarning($"Combat lights: scene bolts: {e.Message}"); }
            }
            float dt = Time.deltaTime, rdt = Time.unscaledDeltaTime;
            bool paused = Time.timeScale <= 0f;
            var s = Settings;

            // Decorative bolts: FlashStart arms them; the light comes when the game's flash does (the strike after the build-up).
            while (_armedAge.Count < Armed.Count) _armedAge.Add(0f);
            for (int i = Armed.Count - 1; i >= 0; i--)
            {
                var b = Armed[i];
                _armedAge[i] += rdt;
                bool drop = b == null || _armedAge[i] > 6f || s.lightningFlash <= 0f;
                if (!drop)
                {
                    var flash = FlashField != null ? FlashField(b) : null;
                    if (flash == null) drop = true;
                    else if (flash.intensity > 0f) { StartBolt(b); drop = true; }
                }
                if (drop) { Armed.RemoveAt(i); _armedAge.RemoveAt(i); }
            }

            // Age flashes: parries on real time (the hit-stop freezes game time), bolts on game time; both end on real time.
            for (int i = _flashes.Count - 1; i >= 0; i--)
            {
                var f = _flashes[i];
                f.RealAge += rdt;
                f.Age += f.Kind == FlashKind.Parry ? rdt : (paused ? 0f : dt);
                float life = f.Kind == FlashKind.Parry ? s.parryHold + s.parryFade * 2f : s.lightningFade * 2.5f;
                if (f.Age > life || f.RealAge > Mathf.Max(life * 2f, 1f)) { _flashes.RemoveAt(i); continue; }
                _flashes[i] = f;
            }

            // Age beams: game time (the line fades on game time), capped on real time.
            for (int i = _beams.Count - 1; i >= 0; i--)
            {
                var b = _beams[i];
                if (b.Lr == null || (b.Rb == null && b.Cb == null)) { RemoveBeam(i); continue; }
                if (b.Rb != null)
                {
                    if (!b.Ready)
                    {
                        // RevolverBeam.Start (which sets the line: Shoot / the coin's segment) assigns cc; before it the line
                        // still holds the prefab's points.
                        if (b.Rb.cc == null) { b.RealAge += rdt; if (b.RealAge > 1f) RemoveBeam(i); continue; }
                        b.Ready = true; b.Age = 0f; b.RealAge = 0f; b.Width0 = Mathf.Max(b.Lr.widthMultiplier, 1e-4f);
                        if (_logBeams++ < 10)
                        {
                            int n = b.Lr.positionCount;
                            float len = n >= 2 ? Vector3.Distance(Pt(b.Lr, 0), Pt(b.Lr, n - 1)) : 0f;
                            _log.LogInfo($"Combat lights: beam '{b.Name}' {b.Rb.beamType}{(b.Rb.strongAlt ? " strong" : "")}{(b.Rb.fake ? " fake" : "")} " +
                                         $"points {n}, length {len:F1} m, width {b.Width0:F2}, hue ({b.Hue.r:F2}, {b.Hue.g:F2}, {b.Hue.b:F2}), material '{(b.Lr.sharedMaterial != null ? b.Lr.sharedMaterial.name : "-")}'");
                        }
                        continue;
                    }
                    b.Age += paused ? 0f : dt;
                    b.RealAge += rdt;
                    if (b.Age > s.beamFade || b.RealAge > Mathf.Max(s.beamFade * 4f, 0.6f)) RemoveBeam(i);
                }
            }

            // New events last: they start at age 0 and are packed this frame.
            DrainPending();
        }

        /// <summary>Hook events into flashes (Update and Append: a parry the bench or a late script fires after Update still
        /// lights the frame it happens in).</summary>
        private void DrainPending()
        {
            var s = Settings;
            // Parries.
            for (int i = 0; i < PendingParries.Count; i++)
            {
                if (s.parryLight <= 0f) break;
                var p = PendingParries[i];
                if (p == Vector3.zero) continue;
                AddFlash(new Flash { Kind = FlashKind.Parry, A = p, B = p, Hue = new Color(s.parryColorR, s.parryColorG, s.parryColorB), Radius = 0.08f });
                if (_logParries++ < 6) _log.LogInfo($"Combat lights: parry burst at ({p.x:F1}, {p.y:F1}, {p.z:F1}), timeScale {Time.timeScale:F2}");
            }
            PendingParries.Clear();

            // Explosive strikes (the Ferryman's): a bolt standing on the strike point.
            for (int i = 0; i < PendingStrikes.Count; i++)
            {
                if (s.lightningFlash <= 0f) break;
                var p = PendingStrikes[i];
                AddFlash(new Flash { Kind = FlashKind.Bolt, A = p, B = p + Vector3.up * 40f, Hue = BoltHue, Radius = 0.3f, Seed = UnityEngine.Random.value });
                if (_logBolts++ < 8) _log.LogInfo($"Combat lights: explosive lightning strike at ({p.x:F1}, {p.y:F1}, {p.z:F1})");
            }
            PendingStrikes.Clear();

            foreach (var fb in ForcedBolts) if (fb != null) StartBolt(fb);
            ForcedBolts.Clear();
            foreach (var db in PendingDecor) if (db != null) StartDecorBolt(db);
            PendingDecor.Clear();
            foreach (var db in ForcedDecor) if (db != null) StartDecorBolt(db);
            ForcedDecor.Clear();
        }

        /// <summary>Once per scene: the level's decorative bolts, for the log (which kind, where, the game's flash).</summary>
        private void LogSceneBolts()
        {
            var plain = UnityEngine.Object.FindObjectsOfType<LightningStrikeDecorative>(true);
            var anim = UnityEngine.Object.FindObjectsOfType<LightningStrikeDecorative_Animated>(true);
            if (plain.Length == 0 && anim.Length == 0) return;
            var sb = new System.Text.StringBuilder($"Combat lights: decorative bolts in the scene: {anim.Length} animated, {plain.Length} plain");
            for (int i = 0; i < plain.Length && i < 6; i++)
            {
                var b = plain[i];
                var fl = DecorFlashField != null ? DecorFlashField(b) : null;
                var sr = DecorSpriteField != null ? DecorSpriteField(b) : null;
                var p = b.transform.position;
                sb.Append($"; '{BoltPath(b.transform)}' at ({p.x:F0}, {p.y:F0}, {p.z:F0})");
                if (fl != null) sb.Append($" flash {fl.type} range {fl.range:F0} colour ({fl.color.r:F2}, {fl.color.g:F2}, {fl.color.b:F2}) at ({fl.transform.position.x:F0}, {fl.transform.position.y:F0}, {fl.transform.position.z:F0})");
                if (sr != null) { var bb = sr.bounds; sb.Append($" sprite y {bb.min.y:F0}..{bb.max.y:F0} colour ({sr.color.r:F2}, {sr.color.g:F2}, {sr.color.b:F2})"); }
            }
            for (int i = 0; i < anim.Length && i < 8; i++)
            {
                var b = anim[i];
                var fl = FlashField != null ? FlashField(b) : null;
                var p = b.transform.position;
                sb.Append($"; animated '{BoltPath(b.transform)}' at ({p.x:F0}, {p.y:F0}, {p.z:F0})");
                if (fl != null) sb.Append($" flash '{BoltPath(fl.transform)}' shadows {fl.shadows}");
            }
            _log.LogInfo(sb.ToString());
        }

        private static string BoltPath(Transform t)
        {
            string p = t.name;
            for (var x = t.parent; x != null; x = x.parent) p = x.name + "/" + p;
            return p;
        }

        /// <summary>A plain decorative bolt (LightningStrikeDecorative.FlashStart: the strike and its flash at once): a bolt
        /// light along its sprite, in the hue of the game's flash light (P-2's tunnel of blood).</summary>
        private void StartDecorBolt(LightningStrikeDecorative bolt)
        {
            if (Settings.lightningFlash <= 0f) return;
            var sr = DecorSpriteField != null ? DecorSpriteField(bolt) : null;
            var fl = DecorFlashField != null ? DecorFlashField(bolt) : null;
            Vector3 a, top;
            if (sr != null)
            {
                var bb = sr.bounds;
                a = new Vector3(bb.center.x, bb.min.y, bb.center.z);
                top = new Vector3(bb.center.x, bb.max.y, bb.center.z);
            }
            else { a = bolt.transform.position; top = a + Vector3.up * 40f; }
            // The game flash's hue when it has one (a red flash stays red, with a little white); white-ish flashes keep the
            // storm's blue-white.
            Color hue = BoltHue;
            if (fl != null)
            {
                var c = fl.color;
                float mx = Mx(c), mn = Mathf.Min(c.r, Mathf.Min(c.g, c.b));
                if (mx > 1e-3f && (mx - mn) / mx > 0.25f) hue = Color.Lerp(c * (1f / mx), Color.white, 0.15f);
            }
            AddFlash(new Flash { Kind = FlashKind.Bolt, A = a, B = top, Hue = hue, Radius = 0.5f, Seed = UnityEngine.Random.value, Unshadowed = InsideScene() });
            if (_logBolts++ < 8)
                _log.LogInfo($"Combat lights: decorative bolt '{bolt.name}' base ({a.x:F0}, {a.y:F0}, {a.z:F0}) top y {top.y:F0}, hue ({hue.r:F2}, {hue.g:F2}, {hue.b:F2}); " +
                             (fl != null ? $"game flash {fl.type} I {fl.intensity:F1} range {fl.range:F0}" : "no game flash"));
        }

        private static readonly Color BoltHue = new Color(0.72f, 0.84f, 1f);

        private void StartBolt(LightningStrikeDecorative_Animated bolt)
        {
            if (Settings.lightningFlash <= 0f) return;
            Vector3 a, top;
            var anim = AnimField != null ? AnimField(bolt) : null;
            var r = anim != null ? anim.GetComponent<Renderer>() : null;
            if (r != null)
            {
                var bb = r.bounds;
                a = new Vector3(bb.center.x, bb.min.y, bb.center.z);
                top = new Vector3(bb.center.x, bb.max.y, bb.center.z);
            }
            else { a = bolt.transform.position; top = a + Vector3.up * 60f; }
            bool unshadowed = InsideScene();
            AddFlash(new Flash { Kind = FlashKind.Bolt, A = a, B = top, Hue = BoltHue, Radius = 0.5f, Seed = UnityEngine.Random.value, Unshadowed = unshadowed });
            if (_logBolts++ < 8)
            {
                var fl = FlashField != null ? FlashField(bolt) : null;
                _log.LogInfo($"Combat lights: lightning bolt '{bolt.name}' base ({a.x:F0}, {a.y:F0}, {a.z:F0}) top y {top.y:F0}{(unshadowed ? ", unshadowed" : "")}; " +
                             (fl != null ? $"game flash {fl.type} I {fl.intensity:F1} range {fl.range:F0} colour ({fl.color.r:F2}, {fl.color.g:F2}, {fl.color.b:F2}) at ({fl.transform.position.x:F0}, {fl.transform.position.y:F0}, {fl.transform.position.z:F0})" : "no game flash"));
            }
        }

        /// <summary>Bolts that strike inside the play space (P-2's tunnel of blood: the bolts stand in the tunnel, walled in
        /// by blood): unshadowed (the walls would keep them off the walkway) and x lightningInsideGain (5-2's bolt light
        /// rides on the game's own flash, which lights the pier; in the tunnel nothing else lights the strike).</summary>
        private bool InsideScene()
        {
            var list = Settings.lightningInsideScenes;
            if (string.IsNullOrEmpty(list)) return false;
            string scene;
            try { scene = SceneHelper.CurrentScene; } catch (Exception) { return false; } // "Level P-2" (the Unity scene's name is an asset id)
            foreach (var n in list.Split(','))
                if (n.Trim().Length > 0 && string.Equals(n.Trim(), scene, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private void AddFlash(Flash f)
        {
            f.Id = unchecked(0x6C1A0000 + (_nextFlashId++ & 0xFFFF) * 8);
            if (_flashes.Count >= 8) _flashes.RemoveAt(0);
            _flashes.Add(f);
        }

        private void RemoveBeam(int i)
        {
            _beamIds.Remove(_beams[i].Id);
            _beams.RemoveAt(i);
        }

        private static Vector3 Pt(LineRenderer lr, int i)
        {
            var p = lr.GetPosition(i);
            return lr.useWorldSpace ? p : lr.transform.TransformPoint(p);
        }

        /// <summary>A brief flash is lighting the frame (or just did): auto exposure holds still so it doesn't pump.</summary>
        public bool HoldExposure
        {
            get
            {
                float now = Time.realtimeSinceStartup;
                if (!Settings.flashHoldsExposure || now > _holdUntil) { _holdStart = -1f; return false; }
                if (_holdStart < 0f) _holdStart = now;
                // Sustained fire (a flash every frame) must not freeze the exposure for good: a second at most, then let go
                // until the flashes stop.
                return now - _holdStart < 1f;
            }
        }

        /// <summary>
        /// Packs this frame's beam and flash lights after <paramref name="count"/> (the scene and effect lights), at most
        /// beamMaxLights for beams and leaving <paramref name="reserve"/> slots for whatever is appended after.
        /// medium[i] (indexed like the packed list) gets each light's weight in the air (volumetrics).
        /// </summary>
        public int Append(Vector4[] data, uint[] masks, Light[] packed, int[] ids, float[] medium, int count, int max, int reserve, Vector3 camPos)
        {
            DrainPending();
            int first = count;
            var s = Settings;
            int cap = Mathf.Max(first, max - Mathf.Max(0, reserve));
            bool lit = false;
            try
            {
                // Flashes first: one parry or bolt matters more than the sixth light of a beam.
                foreach (var f in _flashes)
                {
                    if (f.Kind == FlashKind.Parry)
                    {
                        float env = f.Age <= s.parryHold ? 1f : Mathf.Exp(-(f.Age - s.parryHold) / Mathf.Max(s.parryFade / 3f, 1e-3f));
                        float k = env * s.parryLight * s.parryIntensity;
                        if (k < 0.02f || count >= cap) continue;
                        Pack(data, masks, packed, ids, medium, first, ref count, f.A, s.parryRange, f.Hue * k, f.Radius, f.Id, s.parryMedium);
                        lit = true;
                    }
                    else
                    {
                        // A strike and two re-strikes (lightning flickers), each decaying fast.
                        float tau = Mathf.Max(s.lightningFade / 3f, 1e-3f);
                        float t1 = 0.055f + 0.03f * f.Seed, t2 = 0.12f + 0.04f * f.Seed;
                        float env = Mathf.Exp(-f.Age / tau);
                        if (f.Age > t1) env = Mathf.Max(env, 0.75f * Mathf.Exp(-(f.Age - t1) / tau));
                        if (f.Age > t2) env = Mathf.Max(env, 0.45f * Mathf.Exp(-(f.Age - t2) / tau));
                        float k = env * s.lightningFlash * s.lightningIntensity * (f.Unshadowed ? Mathf.Max(0f, s.lightningInsideGain) : 1f);
                        if (k < 0.02f) continue;
                        // Brightness at the camera independent of the bolt's distance: the camera sits at a fixed fraction
                        // of the range (falloff 1 / (1 + 25 d^2 / r^2)), so the intensity sets what the player sees lit.
                        var mid = Vector3.Lerp(f.A, f.B, 0.35f);
                        float range = Mathf.Max(s.lightningMinRange, Vector3.Distance(mid, camPos) * s.lightningRangePerDistance);
                        const int n = 3;
                        for (int j = 0; j < n && count < cap; j++)
                        {
                            // The upper part of the bolt: the sprite's bounds of 5-2's bolts reach below the sea (base y -66).
                            float t = j == 0 ? 0.35f : j == 1 ? 0.6f : 0.85f;
                            Pack(data, masks, packed, ids, medium, first, ref count, Vector3.Lerp(f.A, f.B, t), range, f.Hue * (k / n), f.Radius, f.Id + j, s.lightningMedium, f.Unshadowed);
                        }
                        lit = true;
                    }
                }

                // Sustained beams as line lights, nearest first, ahead of the shot chains (they light the room for as long as
                // they are on).
                if (s.beamLights > 0f && s.beamLines)
                    PackLines(camPos, data, masks, packed, ids, medium, first, ref count, Mathf.Min(cap, count + Mathf.Max(0, s.lineMaxLights)));

                // Beams, newest first.
                if (s.beamLights > 0f)
                {
                    float cull = s.cullDistance;
                    int beamCap = Mathf.Min(cap, count + Mathf.Max(0, s.beamMaxLights));
                    for (int bi = _beams.Count - 1; bi >= 0 && count < beamCap; bi--)
                    {
                        var b = _beams[bi];
                        var lr = b.Lr;
                        if (b.Cb != null && s.beamLines) continue; // a line light (PackLines)
                        if (lr == null || !lr.enabled || !lr.gameObject.activeInHierarchy) continue;
                        float env;
                        if (b.Rb != null)
                        {
                            if (!b.Ready) continue;
                            float x = Mathf.Clamp01(b.Age / Mathf.Max(s.beamFade, 1e-3f));
                            env = (1f - x) * (1f - x) * Mathf.Clamp01(lr.widthMultiplier / b.Width0 * 1.5f);
                        }
                        else
                        {
                            if (b.Width0 <= 0f) b.Width0 = Mathf.Max(lr.widthMultiplier, 1e-4f);
                            if (lr.widthMultiplier > b.Width0) b.Width0 = lr.widthMultiplier;
                            env = Mathf.Clamp01(lr.widthMultiplier / b.Width0);
                        }
                        float k = env * b.Gain * s.beamLights * s.beamIntensity;
                        if (k < 0.02f) continue;
                        int np = Mathf.Min(lr.positionCount, _pts.Length);
                        if (np < 2) continue;
                        for (int i = 0; i < np; i++) _pts[i] = Pt(lr, i);
                        lit |= b.Rb != null;
                        for (int sg = 0; sg + 1 < np && count < beamCap; sg++)
                            PackSegment(b, sg, _pts[sg], _pts[sg + 1], k, camPos, cull, data, masks, packed, ids, medium, first, ref count, beamCap);
                    }
                }
            }
            catch (Exception e) { _log.LogWarning($"Combat lights: {e.Message}"); }
            if (lit) _holdUntil = Time.realtimeSinceStartup + Mathf.Max(0f, s.exposureHoldTail);
            _lastPacked = count - first;
            return count;
        }

        /// <summary>
        /// One line light per segment of every sustained beam (ContinuousBeam) that is on, within lineCull of the camera,
        /// the nearest lineMaxLights of them: the whole beam lights the walls, floor and ceiling evenly along its length,
        /// and the light follows the line as it moves (read every frame). Intensity follows the line's width (the game
        /// widens a beam in and narrows it out; Providence's pincer shrinks its beams at the end).
        /// </summary>
        private void PackLines(Vector3 camPos, Vector4[] data, uint[] masks, Light[] packed, int[] ids, float[] medium, int first, ref int count, int cap)
        {
            var s = Settings;
            float cull2 = s.lineCull * s.lineCull;
            int nc = 0;
            for (int bi = 0; bi < _beams.Count && nc < MaxLineCandidates; bi++)
            {
                var b = _beams[bi];
                if (b.Cb == null) continue;
                var lr = b.Lr;
                if (lr == null || !lr.enabled || !lr.gameObject.activeInHierarchy) continue;
                // originalWidth is set in the beam's Awake (0 until it is first switched on): fall back to the widest seen.
                if (lr.widthMultiplier > b.Width0) b.Width0 = lr.widthMultiplier;
                float w0 = LineWidth0(b);
                if (lr.widthMultiplier < w0 * 0.02f) continue;
                int np = Mathf.Min(lr.positionCount, _pts.Length);
                if (np < 2) continue;
                var a = Pt(lr, 0);
                for (int sg = 0; sg + 1 < np && nc < MaxLineCandidates; sg++)
                {
                    var c = Pt(lr, sg + 1);
                    float d2 = SegDist2(camPos, a, c);
                    if (d2 <= cull2)
                    {
                        _lineDist[nc] = d2;
                        _lineKey[nc] = (bi << 5) | sg;
                        nc++;
                    }
                    a = c;
                }
            }
            if (nc == 0) return;
            int before = count;
            if (nc > cap - count) Array.Sort(_lineDist, _lineKey, 0, nc);
            for (int k = 0; k < nc && count < cap; k++)
            {
                var b = _beams[_lineKey[k] >> 5];
                int sg = _lineKey[k] & 31;
                var lr = b.Lr;
                float w0 = LineWidth0(b);
                float env = Mathf.Clamp01(lr.widthMultiplier / w0);
                float kk = env * b.Gain * s.beamLights * s.lineIntensity;
                if (kk < 0.02f) continue;
                var pa = Pt(lr, sg);
                var pb = Pt(lr, sg + 1);
                if ((pb - pa).sqrMagnitude < 1e-4f) continue;
                float range = Mathf.Max(0.5f, s.lineRange * Mathf.Sqrt(Mathf.Max(w0, 0.05f) / 0.35f));
                LightOverrides.PackLine(pa, pb, 0f, 0f, 0f, out var centre, out var l2, out var l3);
                int j = count * 4;
                var col = b.Hue * kk;
                data[j] = new Vector4(centre.x, centre.y, centre.z, range);
                data[j + 1] = new Vector4(col.r, col.g, col.b, 0f);
                data[j + 2] = l2;
                data[j + 3] = l3;
                masks[count] = ~0u;
                packed[count] = null;
                ids[count] = unchecked(b.Id * 31 + sg * 7 + 0x3C000000);
                if (medium != null && count < medium.Length) medium[count] = s.lineMedium;
                count++;
            }
            float now = Time.realtimeSinceStartup;
            if (now >= _nextLineLog && _lineLogs < 20)
            {
                _nextLineLog = now + 10f; _lineLogs++;
                _log.LogInfo($"Combat lights: {count - before} line lights packed ({nc} beam segments within {s.lineCull:F0} m, " +
                             $"slots from {before}, cap {cap})");
            }
        }

        private float _nextLineLog;
        private int _lineLogs;

        private static float LineWidth0(Beam b) => Mathf.Max(b.Cb.originalWidth > 0f ? b.Cb.originalWidth : b.Width0, 1e-4f);

        /// <summary>Squared distance from p to the segment ab.</summary>
        private static float SegDist2(Vector3 p, Vector3 a, Vector3 b)
        {
            var ab = b - a;
            float l2 = ab.sqrMagnitude;
            float t = l2 > 1e-8f ? Mathf.Clamp01(Vector3.Dot(p - a, ab) / l2) : 0f;
            return (a + ab * t - p).sqrMagnitude;
        }

        private void PackSegment(Beam b, int sg, Vector3 a, Vector3 c, float k, Vector3 camPos, float cull,
                                 Vector4[] data, uint[] masks, Light[] packed, int[] ids, float[] medium, int first, ref int count, int cap)
        {
            var s = Settings;
            var d = c - a;
            float len = d.magnitude;
            if (len < 0.05f) return;
            var dir = d / len;
            // The part of the segment within the cull distance of the camera.
            var m = a - camPos;
            float bq = Vector3.Dot(m, dir), cq = m.sqrMagnitude - cull * cull;
            float disc = bq * bq - cq;
            if (disc <= 0f) return;
            float sq = Mathf.Sqrt(disc);
            float t0 = Mathf.Max(0f, -bq - sq), t1 = Mathf.Min(len, -bq + sq);
            // The player's own shot starts at the gun, where its muzzle light already is: skip the first metre and a half.
            if (sg == 0 && (a - camPos).sqrMagnitude < 4f) t0 = Mathf.Max(t0, Mathf.Min(1.5f, len * 0.5f));
            float lc = t1 - t0;
            if (lc <= 0.05f) return;
            int n = lc < 1f ? 1 : lc < 3f ? 2 : Mathf.Clamp(Mathf.CeilToInt(lc / Mathf.Max(s.beamSpacing, 0.5f)) + 1, 3, 6);
            float step = lc / n;
            float range = Mathf.Max(s.beamRange, step * 1.2f) * b.RangeGain;
            var col = b.Hue * k;
            for (int i = 0; i < n && count < cap; i++)
            {
                var p = a + dir * (t0 + (i + 0.5f) * step);
                int id = unchecked(b.Id * 31 + sg * 7 + i + 0x3B000000);
                Pack(data, masks, packed, ids, medium, first, ref count, p, range, col, 0.12f, id, s.beamMedium);
            }
        }

        private static void Pack(Vector4[] data, uint[] masks, Light[] packed, int[] ids, float[] medium, int first, ref int count,
                                 Vector3 p, float range, Color c, float radius, int id, float med, bool unshadowed = false)
        {
            int j = count * 4;
            data[j] = new Vector4(p.x, p.y, p.z, Mathf.Max(range, 0.1f));
            data[j + 1] = new Vector4(c.r, c.g, c.b, 0f);
            data[j + 2] = new Vector4(0f, -1f, 0f, Mathf.Max(radius, 0.01f)); // soft-shadow radius: thin beams, crisp shadows
            data[j + 3] = new Vector4(-1f, 1f, unshadowed ? 0.75f : 0f, 0f);  // point light, shadowed (0.75: unshadowed, not a fill)
            masks[count] = ~0u;
            packed[count] = null;
            ids[count] = id;
            if (medium != null && count < medium.Length) medium[count] = med;
            count++;
        }
    }

    /// <summary>
    /// Game hooks for <see cref="CombatLights"/>: guarded postfixes / prefixes that only record what happened (a parry, a
    /// lightning bolt's FlashStart, an explosive strike) and can never throw into game code.
    /// </summary>
    internal static class CombatHooks
    {
        /// <summary>Set by the plugin while parry or lightning lights are on; the hooks are no-ops otherwise.</summary>
        public static bool Parry, Lightning;
        public static int Parries, Strikes;
        public static string Hooked = "", Errors = "";

        public static void Patch(Harmony harmony)
        {
            Try(harmony, typeof(TimeController), "ParryFlash", nameof(ParryFlashPostfix), false, "parry");
            Try(harmony, typeof(LightningStrikeDecorative_Animated), "FlashStart", nameof(FlashStartPostfix), false, "bolt");
            Try(harmony, typeof(LightningStrikeExplosive), "Start", nameof(ExplosiveStartPrefix), true, "strike");
            Try(harmony, typeof(LightningStrikeDecorative), "FlashStart", nameof(DecorFlashStartPostfix), false, "decorative bolt");
        }

        private static void Try(Harmony harmony, Type type, string method, string hook, bool prefix, string label)
        {
            try
            {
                var m = AccessTools.Method(type, method);
                if (m == null) { Errors += $" {label}: {type.Name}.{method} not found;"; return; }
                var hm = new HarmonyMethod(typeof(CombatHooks).GetMethod(hook, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static));
                if (prefix) harmony.Patch(m, prefix: hm); else harmony.Patch(m, postfix: hm);
                Hooked += (Hooked.Length > 0 ? ", " : "") + label;
            }
            catch (Exception e) { Errors += $" {label}: {e.Message};"; }
        }

        private static void ParryFlashPostfix()
        {
            try
            {
                if (!Parry || CombatLights.PendingParries.Count >= 4) return;
                Parries++;
                CombatLights.PendingParries.Add(CombatLights.ParryPoint(1.2f));
            }
            catch (Exception) { /* never break a parry */ }
        }

        private static void FlashStartPostfix(LightningStrikeDecorative_Animated __instance)
        {
            try
            {
                if (!Lightning || __instance == null || CombatLights.Armed.Count >= 8 || CombatLights.Armed.Contains(__instance)) return;
                Strikes++;
                CombatLights.Armed.Add(__instance);
            }
            catch (Exception) { /* never break the storm */ }
        }

        private static void DecorFlashStartPostfix(LightningStrikeDecorative __instance)
        {
            try
            {
                if (!Lightning || __instance == null || CombatLights.PendingDecor.Count >= 4 || CombatLights.PendingDecor.Contains(__instance)) return;
                Strikes++;
                CombatLights.PendingDecor.Add(__instance);
            }
            catch (Exception) { /* never break the storm */ }
        }

        private static void ExplosiveStartPrefix(LightningStrikeExplosive __instance)
        {
            try
            {
                if (!Lightning || __instance == null || CombatLights.PendingStrikes.Count >= 4) return;
                Strikes++;
                CombatLights.PendingStrikes.Add(__instance.transform.position);
            }
            catch (Exception) { /* never break the strike */ }
        }
    }
}
