using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using BepInEx;
using UnityEngine;

namespace UltraRT
{
    /// <summary>
    /// The light classifier (docs/lighting-overrides.md, "Classifier"): sorts every level light into a class and writes the
    /// mechanical fixes as an override file plus a report. It never changes anything at run time by itself: the file goes to
    /// BepInEx\ultrart\lights\classified\, and a reviewed copy is shipped as data\lights\&lt;scene&gt;.json.
    /// Runs once per level with -ultrart-classify-lights (after the RT scene and the emissive triangles are built), or with
    /// Ctrl+Insert in game.
    /// </summary>
    public partial class Plugin
    {
        private bool _classifyLights;  // -ultrart-classify-lights
        private string _classifiedScene;

        private sealed class LightInfo
        {
            public Light L;
            public string Path, Class, Fix, Note;
            public float Confidence;
            public readonly List<string> Tags = new List<string>();
            public LightOverrideEntry Entry;
        }

        /// <summary>OnUpdate: run once per scene when asked (flag), or on Ctrl+Insert.</summary>
        private void LightClassifierUpdate()
        {
            if (_proxies == null || _emissive == null) return;
            bool hot = false;
            try { hot = Input.GetKeyDown(KeyCode.Insert) && CtrlHeld(); } catch (InvalidOperationException) { }
            string scene = SafeCurrentScene();
            if (hot || (_classifyLights && _classifiedScene != scene && _proxies.DeferredCount == 0 && Time.timeSinceLevelLoad > 4f))
            {
                _classifiedScene = scene;
                try { ClassifyLights(scene); }
                catch (Exception e) { Logger.LogError($"Light classifier failed: {e}"); }
            }
        }

        private static readonly string[] TorchWords = { "torch", "brazier", "fire", "candle", "flame", "campfire", "bonfire" };
        private static readonly string[] LampWords = { "lamp", "light", "lantern", "bulb", "chandelier", "sconce", "fluor", "neon", "fixture" };
        private static readonly string[] BallWords = { "sphere", "orb", "ball", "globe" };
        private static readonly string[] LampParentWords = { "lamp", "lantern", "bulb", "chandelier", "sconce", "fluor", "neon", "fixture" };
        private static readonly string[] WindowWords = { "window", "skylight" };
        private static readonly string[] ScreenWords = { "screen", "monitor", "tv", "display", "terminal" };
        private static readonly string[] TasteWords = { "secret", "boss", "arena", "final" };

        private static bool HasWord(string s, string[] words)
        {
            s = s.ToLowerInvariant();
            foreach (var w in words) if (s.Contains(w)) return true;
            return false;
        }

        private void ClassifyLights(string scene)
        {
            var sw = Stopwatch.StartNew();
            var player = MonoSingleton<NewMovement>.Instance != null ? MonoSingleton<NewMovement>.Instance.transform : null;
            var camT = _camera != null ? _camera.transform : null;

            // Emissive triangles by 2 m cell.
            var tris = _emissive.WorldTriangles();
            const float Cell = 2f;
            var grid = new Dictionary<Vector3Int, List<int>>();
            Vector3Int C(Vector3 p) => new Vector3Int(Mathf.FloorToInt(p.x / Cell), Mathf.FloorToInt(p.y / Cell), Mathf.FloorToInt(p.z / Cell));
            for (int i = 0; i < tris.Count; i++)
            {
                var c = C(tris[i].Centroid);
                if (!grid.TryGetValue(c, out var l)) grid[c] = l = new List<int>(4);
                l.Add(i);
            }
            List<int> TrisNear(Vector3 p, float r)
            {
                var res = new List<int>();
                var lo = C(p - Vector3.one * r);
                var hi = C(p + Vector3.one * r);
                for (int x = lo.x; x <= hi.x; x++)
                for (int y = lo.y; y <= hi.y; y++)
                for (int z = lo.z; z <= hi.z; z++)
                    if (grid.TryGetValue(new Vector3Int(x, y, z), out var l))
                        foreach (int i in l) if ((tris[i].Centroid - p).sqrMagnitude <= r * r) res.Add(i);
                return res;
            }

            // Renderers by 4 m cell, for fixture names (small ones only).
            var rgrid = new Dictionary<Vector3Int, List<Renderer>>();
            Vector3Int C4(Vector3 p) => new Vector3Int(Mathf.FloorToInt(p.x / 4f), Mathf.FloorToInt(p.y / 4f), Mathf.FloorToInt(p.z / 4f));
            foreach (var r in UnityEngine.Object.FindObjectsOfType<Renderer>(true))
            {
                if (r == null || !(r is MeshRenderer) || r.bounds.size.magnitude > 6f) continue;
                var c = C4(r.bounds.center);
                if (!rgrid.TryGetValue(c, out var l)) rgrid[c] = l = new List<Renderer>(4);
                l.Add(r);
            }
            List<Renderer> RenderersAround(Vector3 p, float pad)
            {
                var res = new List<Renderer>();
                var lo = C4(p - Vector3.one * 4f);
                var hi = C4(p + Vector3.one * 4f);
                for (int x = lo.x; x <= hi.x; x++)
                for (int y = lo.y; y <= hi.y; y++)
                for (int z = lo.z; z <= hi.z; z++)
                    if (rgrid.TryGetValue(new Vector3Int(x, y, z), out var l))
                        foreach (var r in l)
                        {
                            var b = r.bounds;
                            b.Expand(pad);
                            if (b.Contains(p)) res.Add(r);
                        }
                return res;
            }

            var infos = new List<LightInfo>();
            var dirs = new List<Vector3>();
            for (int i = 0; i < 26; i++)
            {
                var d = new Vector3(i % 3 - 1, i / 3 % 3 - 1, i / 9 - 1);
                if (d != Vector3.zero) dirs.Add(d.normalized);
            }
            int levelMask = ~(1 << 2); // everything but Ignore Raycast

            foreach (var l in _proxies.Lights.ToList())
            {
                if (l == null) continue;
                var info = new LightInfo { L = l, Path = LightOverrides.NormalizedPath(l.transform) };
                infos.Add(info);
                var t = l.transform;
                var p = t.position;
                var lc = l.color;
                float lum = (0.2126f * lc.r + 0.7152f * lc.g + 0.0722f * lc.b) * l.intensity;
                if (l.type == LightType.Directional)
                {
                    info.Class = -t.forward.y < -0.05f ? "fill-directional" : "sun";
                    info.Note = "directional lights are left alone";
                    continue;
                }
                if (l.type != LightType.Point && l.type != LightType.Spot) { info.Class = "other"; continue; }
                if (!_proxies.IsBuildLight(l) || (player != null && t.IsChildOf(player)) || (camT != null && t.IsChildOf(camT)))
                {
                    info.Class = "player/effect";
                    continue;
                }
                // Lights that belong to enemies, pickups or spawn effects move with them: never re-authored.
                if (l.GetComponentInParent<EnemyIdentifier>(true) != null || l.GetComponentInParent<ItemIdentifier>(true) != null ||
                    HasWord(info.Path, new[] { "spawneffect", "/enemies/", "projectile wave" }))
                {
                    info.Class = "enemy/item";
                    info.Note = "on an enemy, a pickup or a spawn effect: moves with it";
                    continue;
                }
                // Scripted: a game script on the light that drives it (flicker, fades, switches, 8-2's day/night), or an
                // animator above it. Helper components (IgnoreSceneOptimizer, CancellationTokenHelper) don't count.
                var scripts = l.GetComponents<MonoBehaviour>().Where(b => b != null).Select(b => b.GetType().Name)
                    .Where(n => HasWord(n, new[] { "flicker", "light", "blink", "pulse", "fade", "toggle", "switch", "day", "night", "color", "colour", "intensity", "anim", "lerp" })).ToList();
                bool animated = l.GetComponentInParent<Animator>() != null || l.GetComponentInParent<Animation>() != null;
                if (scripts.Count > 0) info.Tags.Add("scripted:" + string.Join("+", scripts.Distinct()));
                if (animated) info.Tags.Add("animated");
                if (!l.gameObject.activeInHierarchy) info.Tags.Add("off-at-load");
                bool taste = HasWord(info.Path, TasteWords) || scene.Contains("7-3") || scene.Contains("P-2");
                if (taste) info.Tags.Add("taste");
                if (_lavaLights.Contains(l)) { info.Class = "lava"; info.Note = "the lava system dims it (docs/lava.md)"; continue; }

                bool inFixture = _proxies.FixtureBounds.TryGetValue(l, out var fb);
                bool enclosed = _proxies.StaticEnclosed.Contains(l);
                var around = RenderersAround(p, 0.25f).Where(r => r.bounds.size.magnitude <= 6f).ToList();
                // Fixture kind from the renderers around the light and its parents' names (never the light's own name:
                // "Point Light" says nothing about a lamp; "light" only counts in renderer names).
                string names = string.Join(" ", around.Select(r => r.name + " " + string.Join(" ", r.sharedMaterials.Where(m => m != null).Select(m => m.name))));
                int slash = info.Path.LastIndexOf('/');
                string parents = slash > 0 ? info.Path.Substring(0, slash) : "";
                string all = names + " " + parents;
                string kind = HasWord(all, TorchWords) ? "torch" : HasWord(all, WindowWords) ? "window" : HasWord(all, ScreenWords) ? "screen"
                    : HasWord(names, LampWords) || HasWord(parents, LampParentWords) ? "lamp" : null;

                // Emissive triangles near the light: in its fixture, or within 1.2 m.
                var near = TrisNear(p, inFixture ? Mathf.Max(1.2f, fb.extents.magnitude + 0.3f) : 1.2f);
                float emArea = 0f, emFlux = 0f;
                var emN = Vector3.zero;
                var emC = Vector3.zero;
                Bounds emB = default;
                bool first = true;
                foreach (int k in near)
                {
                    var tr = tris[k];
                    var le = new Color(Mathf.Pow(tr.Le.r, 2.2f), Mathf.Pow(tr.Le.g, 2.2f), Mathf.Pow(tr.Le.b, 2.2f));
                    float fl = tr.Area * (0.2126f * le.r + 0.7152f * le.g + 0.0722f * le.b);
                    emArea += tr.Area;
                    emFlux += fl;
                    emN += tr.Normal * tr.Area;
                    emC += tr.Centroid * tr.Area;
                    var tb = new Bounds(tr.P0, Vector3.zero);
                    tb.Encapsulate(tr.P0 + tr.E1);
                    tb.Encapsulate(tr.P0 + tr.E2);
                    if (first) { emB = tb; first = false; } else emB.Encapsulate(tb);
                }
                if (emArea > 0f) emC /= emArea;

                // Clearance to level geometry (physics), for fill lights and lights poking through walls.
                float minHit = float.MaxValue;
                var away = Vector3.zero;
                int hits = 0;
                foreach (var d in dirs)
                {
                    if (Physics.Raycast(p, d, out var h, 3f, levelMask, QueryTriggerInteraction.Ignore) && h.collider != null && h.collider.bounds.size.magnitude > 4f)
                    {
                        hits++;
                        if (h.distance < minHit) minHit = h.distance;
                        if (h.distance < 0.3f) away -= d * (0.3f - h.distance);
                    }
                }
                bool insideWall = Physics.CheckSphere(p, 0.05f, levelMask, QueryTriggerInteraction.Ignore) &&
                                  Physics.OverlapSphere(p, 0.05f, levelMask, QueryTriggerInteraction.Ignore).Any(c => c.bounds.size.magnitude > 4f);

                // The game's light in relight (linear transport, light reference 0.3, exposure 1.5): radiant intensity
                // K r^2 (colour x intensity x 1.5)^2.2, K = g(0.3)^2.2 * 0.09. The emitter's: area x Le^2.2 x lightScale / pi.
                var cl = l.color * l.intensity * 1.5f;
                float lightI = 0.00673f * l.range * l.range * (0.2126f * Mathf.Pow(cl.r, 2.2f) + 0.7152f * Mathf.Pow(cl.g, 2.2f) + 0.0722f * Mathf.Pow(cl.b, 2.2f));
                float emI = emFlux * _emissiveSettings.lightScale / Mathf.PI;

                if (kind == "torch" || (inFixture && !enclosed && _fixtureLifts.ContainsKey(l) && kind == null))
                {
                    info.Class = "fixture:torch";
                    info.Note = _fixtureLifts.ContainsKey(l) ? "open fire: the fixture lift puts it above the bowl (docs/relight.md, Light fixtures)" : "open fire";
                    info.Confidence = 0.8f;
                }
                else if (inFixture || enclosed || (kind != null && around.Count > 0))
                {
                    info.Class = "fixture:" + (kind ?? "lamp");
                    if (enclosed && !inFixture) { info.Class += "/shade"; info.Note = "inside a static lamp shade: light escapes through its openings (left as is)"; info.Confidence = 0.6f; }
                    else if (emArea >= 0.004f && emArea <= 12f && kind != "window")
                        FixtureToArea(info, l, emC, emN, emB, emArea, emI, lightI);
                    else if (inFixture && !_fixtureLifts.ContainsKey(l) && HasWord(names, LampParentWords) && !HasWord(names, BallWords) &&
                             fb.size.y <= 1.2f * Mathf.Max(fb.size.x, fb.size.z) && p.y > fb.center.y - 0.25f * fb.size.y)
                        FixtureToDisc(info, l, fb); // a lamp housing named as such, light in its upper part: the opening below
                    else if (inFixture && HasWord(names + " " + parents, BallWords))
                    {
                        info.Class = "fixture:orb";
                        info.Note = "inside a glowing ball (statue orbs): left as is";
                        info.Confidence = 0.5f;
                    }
                    else { info.Note = "fixture without an emissive part; the lift handles it"; info.Confidence = 0.5f; }
                }
                else if (emArea > 0.004f)
                {
                    info.Class = "emissive";
                    info.Note = $"next to an emissive surface ({emArea:F2} m2; emitter / light intensity {emI / Mathf.Max(lightI, 1e-6f):F2})";
                    info.Confidence = 0.6f;
                    if (emI > 0.75f * lightI && lightI > 0f)
                    {
                        // Doubled: the surface already lights the room as an emissive light.
                        float s = Mathf.Clamp(1f - 0.5f * emI / lightI, 0.5f, 0.85f);
                        info.Entry = NewEntry(l, info);
                        info.Entry.Intensity = LightOverrides.Round(s);
                        info.Fix = $"dedupe: intensity x{s:F2} (the emissive surface emits {emI / lightI:F1}x the light)";
                    }
                }
                else if (kind != null)
                {
                    info.Class = "fixture:" + kind + "/named";
                    info.Note = "named like a fixture but no fixture geometry found around it: left as is";
                    info.Confidence = 0.4f;
                }
                else if (l.range >= 12f && lum <= 1.2f && minHit >= 1f && !info.Tags.Any(t => t.StartsWith("scripted")))
                {
                    info.Class = "fill";
                    float conf = Mathf.Clamp01(0.4f + (l.range - 12f) / 40f + (1.2f - lum) * 0.2f + (hits == 0 ? 0.1f : 0f));
                    info.Confidence = conf;
                    info.Note = $"no source within reach, range {l.range:F0} m, intensity {lum:F2}, {minHit:F1} m from walls";
                    if (taste) info.Note += "; taste call (mood lighting?): not dimmed";
                    else
                    {
                        // Relight and pathtrace both have bounce (radiance cache / real paths): the fake bounce is mostly double.
                        info.Entry = NewEntry(l, info);
                        float s = conf >= 0.7f ? 0.5f : 0.7f;
                        info.Entry.Intensity = s;
                        info.Fix = $"fill: intensity x{s:F2} (bounce covers it)";
                    }
                }
                else info.Class = "point";

                // Through a wall: inside level geometry or within 0.15 m of it, with open space on the other side.
                if (info.Entry == null || !info.Entry.OwnsGeometry)
                {
                    if ((insideWall || minHit < 0.15f) && away.sqrMagnitude > 1e-6f && !(inFixture || enclosed))
                    {
                        var nudge = away.normalized * Mathf.Clamp(0.3f - Mathf.Min(minHit, 0.3f) + 0.05f, 0.1f, 0.4f);
                        if (!Physics.CheckSphere(p + nudge, 0.05f, levelMask, QueryTriggerInteraction.Ignore))
                        {
                            info.Entry = info.Entry ?? NewEntry(l, info);
                            info.Entry.Offset = new[] { LightOverrides.Round(nudge.x), LightOverrides.Round(nudge.y), LightOverrides.Round(nudge.z) };
                            info.Fix = (info.Fix != null ? info.Fix + "; " : "") + $"wall: moved {nudge.magnitude:F2} m into the room";
                            info.Tags.Add("wall");
                            info.Confidence = Mathf.Max(info.Confidence, 0.6f);
                        }
                        else info.Tags.Add("wall:no-room");
                    }
                }
                if (info.Entry != null)
                {
                    info.Entry.Class = info.Class;
                    info.Entry.Confidence = LightOverrides.Round(info.Confidence);
                    info.Entry.Note = info.Fix + (info.Tags.Count > 0 ? " [" + string.Join(", ", info.Tags) + "]" : "");
                }
            }
            WriteClassification(scene, infos, tris.Count, sw.Elapsed.TotalMilliseconds);
        }

        private static LightOverrideEntry NewEntry(Light l, LightInfo info)
        {
            var p = l.transform.position;
            return new LightOverrideEntry
            {
                Path = info.Path, Name = LightOverrides.NormalizedName(l.name),
                Pos = new[] { LightOverrides.Round(p.x), LightOverrides.Round(p.y), LightOverrides.Round(p.z) },
                Type = l.type.ToString().ToLowerInvariant(), Source = "classifier",
            };
        }

        /// <summary>A lamp with a glowing part: the light becomes an area light the shape of that part.</summary>
        private void FixtureToArea(LightInfo info, Light l, Vector3 c, Vector3 nSum, Bounds b, float area, float emI, float lightI)
        {
            var p = l.transform.position;
            var e = info.Entry = NewEntry(l, info);
            float flat = nSum.magnitude / Mathf.Max(area, 1e-6f); // 1: one flat face; ~0: a bulb or a closed shell
            if (flat >= 0.6f)
            {
                var n = nSum.normalized;
                // Width along the bounds' longest in-plane axis.
                var axes = new[] { Vector3.right, Vector3.up, Vector3.forward };
                var ext = new[] { b.size.x, b.size.y, b.size.z };
                int best = -1;
                float bestE = -1f;
                for (int k = 0; k < 3; k++)
                {
                    float inPlane = 1f - Mathf.Abs(Vector3.Dot(axes[k], n));
                    if (inPlane < 0.5f) continue;
                    if (ext[k] > bestE) { bestE = ext[k]; best = k; }
                }
                var tan = best >= 0 ? (axes[best] - n * Vector3.Dot(axes[best], n)).normalized : LightOverrides.DefaultTangent(n);
                var bt = Vector3.Cross(n, tan);
                float w = Mathf.Abs(Vector3.Dot(b.size, new Vector3(Mathf.Abs(tan.x), Mathf.Abs(tan.y), Mathf.Abs(tan.z))));
                float h = Mathf.Abs(Vector3.Dot(b.size, new Vector3(Mathf.Abs(bt.x), Mathf.Abs(bt.y), Mathf.Abs(bt.z))));
                w = Mathf.Clamp(w, 0.05f, 4f);
                h = Mathf.Clamp(h, 0.05f, 4f);
                bool round = Mathf.Abs(w - h) < 0.15f * Mathf.Max(w, h) && area < 0.85f * w * h; // a disc covers pi/4 of its square
                e.Shape = round ? "disc" : "rect";
                e.Size = round ? new[] { LightOverrides.Round(Mathf.Max(w, h)), LightOverrides.Round(Mathf.Max(w, h)) } : new[] { LightOverrides.Round(w), LightOverrides.Round(h) };
                e.Normal = new[] { LightOverrides.Round(n.x), LightOverrides.Round(n.y), LightOverrides.Round(n.z) };
                e.Tangent = new[] { LightOverrides.Round(tan.x), LightOverrides.Round(tan.y), LightOverrides.Round(tan.z) };
                var o = c + n * 0.03f - p;
                e.Offset = new[] { LightOverrides.Round(o.x), LightOverrides.Round(o.y), LightOverrides.Round(o.z) };
                info.Fix = $"{e.Shape} {e.Size[0]:F2} x {e.Size[1]:F2} m on its glowing part (moved {o.magnitude:F2} m)";
            }
            else
            {
                float r = Mathf.Clamp(0.5f * Mathf.Min(b.size.x, Mathf.Min(b.size.y, b.size.z)), 0.04f, 0.5f);
                if (r < 0.05f) r = Mathf.Clamp(0.5f * b.size.magnitude / 1.732f, 0.04f, 0.5f);
                e.Shape = "sphere";
                e.Size = new[] { LightOverrides.Round(2f * r), LightOverrides.Round(2f * r) };
                var o = c - p;
                if (o.magnitude > 0.02f) e.Offset = new[] { LightOverrides.Round(o.x), LightOverrides.Round(o.y), LightOverrides.Round(o.z) };
                info.Fix = $"sphere r {r:F2} m at its glowing part";
            }
            info.Confidence = 0.7f;
            if (lightI > 0f && emI > 0.75f * lightI)
            {
                float s = Mathf.Clamp(1f - 0.5f * emI / lightI, 0.5f, 0.85f);
                e.Intensity = LightOverrides.Round(s);
                info.Fix += $"; dedupe: intensity x{s:F2} (its glowing part emits {emI / lightI:F1}x the light)";
                info.Confidence = 0.6f;
            }
        }

        /// <summary>A lamp housing with nothing glowing (or no emissive data): a disc / rect on the housing's bottom face.</summary>
        private void FixtureToDisc(LightInfo info, Light l, Bounds fb)
        {
            var p = l.transform.position;
            var e = info.Entry = NewEntry(l, info);
            float w = Mathf.Clamp(fb.size.x * 0.8f, 0.05f, 3f), h = Mathf.Clamp(fb.size.z * 0.8f, 0.05f, 3f);
            bool round = Mathf.Abs(w - h) < 0.15f * Mathf.Max(w, h);
            e.Shape = round ? "disc" : "rect";
            e.Size = new[] { LightOverrides.Round(round ? Mathf.Max(w, h) : w), LightOverrides.Round(round ? Mathf.Max(w, h) : h) };
            e.Normal = new[] { 0f, -1f, 0f };
            e.Tangent = new[] { 1f, 0f, 0f };
            var o = new Vector3(fb.center.x - p.x, fb.min.y - 0.03f - p.y, fb.center.z - p.z);
            e.Offset = new[] { LightOverrides.Round(o.x), LightOverrides.Round(o.y), LightOverrides.Round(o.z) };
            info.Fix = $"{e.Shape} {e.Size[0]:F2} m under its housing (moved {o.magnitude:F2} m)";
            info.Confidence = 0.45f;
        }

        private void WriteClassification(string scene, List<LightInfo> infos, int emissiveTris, double ms)
        {
            string dir = Path.Combine(Paths.BepInExRootPath, "ultrart", "lights", "classified");
            Directory.CreateDirectory(dir);
            var file = new LightOverrideFile
            {
                Scene = scene,
                Note = $"Light classifier output (UltraRT {DateTime.Now:yyyy-MM-dd}; docs/lighting-overrides.md). Mechanical fixes only; review before shipping.",
                Lights = infos.Where(i => i.Entry != null).Select(i => i.Entry).OrderBy(e => e.Path, StringComparer.Ordinal).ToList(),
            };
            string json = Path.Combine(dir, LightOverrides.FileName(scene));
            File.WriteAllText(json, LightOverrides.Serialize(file));

            var sb = new StringBuilder();
            sb.AppendLine($"# Light classification: {scene}");
            sb.AppendLine();
            sb.AppendLine($"{infos.Count} lights tracked, {emissiveTris} emissive triangles; {file.Lights.Count} fixes; {ms:F0} ms.");
            sb.AppendLine();
            sb.AppendLine("| Class | Lights | Fixed | Off at load | Scripted | Taste |");
            sb.AppendLine("|---|---|---|---|---|---|");
            foreach (var g in infos.GroupBy(i => i.Class ?? "?").OrderByDescending(g => g.Count()))
                sb.AppendLine($"| {g.Key} | {g.Count()} | {g.Count(i => i.Entry != null)} | {g.Count(i => i.Tags.Contains("off-at-load"))} | {g.Count(i => i.Tags.Any(t => t.StartsWith("scripted") || t == "animated"))} | {g.Count(i => i.Tags.Contains("taste"))} |");
            sb.AppendLine();
            sb.AppendLine("## Changes");
            sb.AppendLine();
            sb.AppendLine("| Light | Class | Change | Confidence | Tags |");
            sb.AppendLine("|---|---|---|---|---|");
            foreach (var i in infos.Where(i => i.Entry != null).OrderBy(i => i.Class).ThenBy(i => i.Path))
                sb.AppendLine($"| `{i.Path}` | {i.Class} | {i.Fix} | {i.Confidence:F2} | {string.Join(", ", i.Tags)} |");
            sb.AppendLine();
            sb.AppendLine("## Unchanged (notes)");
            sb.AppendLine();
            foreach (var i in infos.Where(i => i.Entry == null && i.Note != null && i.Class != "player/effect").OrderBy(i => i.Class).ThenBy(i => i.Path).Take(400))
                sb.AppendLine($"- `{i.Path}` ({i.Class}): {i.Note}{(i.Tags.Count > 0 ? " [" + string.Join(", ", i.Tags) + "]" : "")}");
            string report = Path.Combine(dir, scene + ".report.md");
            File.WriteAllText(report, sb.ToString());
            var counts = string.Join(", ", infos.GroupBy(i => i.Class ?? "?").OrderByDescending(g => g.Count()).Select(g => $"{g.Key} {g.Count()}"));
            Logger.LogInfo($"Light classifier '{scene}': {infos.Count} lights ({counts}); {file.Lights.Count} fixes -> {json} ({ms:F0} ms)");
            Toast($"Classified {infos.Count} lights: {file.Lights.Count} fixes -> {json}");
        }
    }
}
