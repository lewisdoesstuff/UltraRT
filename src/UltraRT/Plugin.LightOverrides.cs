using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using BepInEx;
using UltraRT.Bench;
using UnityEngine;

namespace UltraRT
{
    /// <summary>
    /// Per-level light overrides and the in-game light editor (docs/lighting-overrides.md).
    ///  - GatherLights asks <see cref="LightOverrideFor"/> for every scene light and applies the entry to the packed data
    ///    only (the Light components are never touched, so the game's flicker, triggers and day/night keep working: the
    ///    entry scales whatever the light is this frame).
    ///  - Added lights (area lights on emissive surfaces, sky portals) go after the scene lights.
    ///  - Applied in the relight look only (relight, pathtrace, or a forced relight); pt-dn keeps the game's
    ///    lights, whose image they composite against. -ultrart-nolightoverrides or the menu switch turn them off.
    /// </summary>
    public partial class Plugin
    {
        private bool _lightOverrides = true; // -ultrart-nolightoverrides; config [Lights] Overrides; Keypad * in the editor (session)
        private bool _lightsPreview;         // -ultrart-lights-preview: read the classifier's output instead of the shipped files
        private LightOverrides _lightOv;
        private int _ovCacheVersion = -1;
        private readonly Dictionary<Light, (string path, Vector3 pos)> _lightIdentity = new Dictionary<Light, (string, Vector3)>();
        private bool _ovActiveThisFrame;
        public int OverriddenLightCount { get; private set; }
        public int AddedLightCount { get; private set; }

        /// <summary>ResetScene: a reloaded level has new Light objects.</summary>
        private void LightOverridesReset()
        {
            _lightIdentity.Clear();
            _resolved.Clear();
            _addBind.Clear();
            _addBindVersion = -1;
            _editSel = null;
        }

        /// <summary>The settings menu's "Light editor" group (SettingsMenu.InitSettings).</summary>
        private void LightEditorSettings(SettingsMenu m)
        {
            m.Group = "Light editor (per-level light overrides)";
            m.Bool("Lights", "Overrides", "Per-level light overrides (relight, pathtrace)", () => _lightOverrides, v => { _lightOverrides = v; ResetLightHistories(); }, "-ultrart-nolightoverrides");
            m.Bool("Lights", "Editor", "Light editor  [Insert]", () => _editorOn, v => { if (v != _editorOn) SetLightEditor(v); }).SessionOnly();
            // The selected light (Keypad 5 / 4 / 6 in game). No config entries: the values live in the override JSON.
            m.Float("Lights", "SelIntensity", "Selected light: intensity", 0f, 4f, 2, () => ParamValue(0), v => { if (_editSel != null && Mathf.Abs(v - ParamValue(0)) > 1e-4f) SetParam(0, v); }).SessionOnly();
            m.Float("Lights", "SelRed", "Selected light: red", 0f, 2f, 2, () => ParamValue(1), v => { if (_editSel != null && Mathf.Abs(v - ParamValue(1)) > 1e-4f) SetParam(1, v); }).SessionOnly();
            m.Float("Lights", "SelGreen", "Selected light: green", 0f, 2f, 2, () => ParamValue(2), v => { if (_editSel != null && Mathf.Abs(v - ParamValue(2)) > 1e-4f) SetParam(2, v); }).SessionOnly();
            m.Float("Lights", "SelBlue", "Selected light: blue", 0f, 2f, 2, () => ParamValue(3), v => { if (_editSel != null && Mathf.Abs(v - ParamValue(3)) > 1e-4f) SetParam(3, v); }).SessionOnly();
            m.Float("Lights", "SelRange", "Selected light: range", 0.1f, 4f, 2, () => ParamValue(4), v => { if (_editSel != null && Mathf.Abs(v - ParamValue(4)) > 1e-4f) SetParam(4, v); }).SessionOnly();
            m.Bool("Lights", "SelShadows", "Selected light: shadows", () => ParamValue(5) > 0.5f, v => { if (_editSel != null && v != ParamValue(5) > 0.5f) SetParam(5, v ? 1f : 0f); }).SessionOnly();
            m.Bool("Lights", "SelDisabled", "Selected light: disabled", () => ParamValue(6) > 0.5f, v => { if (_editSel != null && v != ParamValue(6) > 0.5f) SetParam(6, v ? 1f : 0f); }).SessionOnly();
            m.Choice("Lights", "SelShape", "Selected light: shape", () => (AreaShapeChoice)Mathf.Clamp((int)ParamValue(7), 0, 3),
                v => { if (_editSel != null && (int)v != (int)ParamValue(7)) SetParam(7, (int)v); }, v => v.ToString(), v => v.ToString()).SessionOnly();
            m.Float("Lights", "SelWidth", "Selected light: width (m)", 0.01f, 4f, 2, () => ParamValue(8), v => { if (_editSel != null && Mathf.Abs(v - ParamValue(8)) > 1e-4f) SetParam(8, v); }).SessionOnly();
            m.Float("Lights", "SelHeight", "Selected light: height (m)", 0.01f, 4f, 2, () => ParamValue(9), v => { if (_editSel != null && Mathf.Abs(v - ParamValue(9)) > 1e-4f) SetParam(9, v); }).SessionOnly();
            m.Action("Light editor: select the light at the crosshair", () => { if (_camera != null) { _editorOn = true; SelectAimedLight(); } });
            m.Action("Light editor: next light", () => { if (_camera != null) { _editorOn = true; CycleLight(+1); } });
            m.Action("Light editor: save", () => SaveLightEdits(true));
        }

        internal enum AreaShapeChoice { Point, Sphere, Rect, Disc }

        private void EnsureLightOverrides()
        {
            if (_lightOv == null)
            {
                string shipped = Path.Combine(Path.GetDirectoryName(Info.Location) ?? "", "lights");
                string user = Path.Combine(Paths.BepInExRootPath, "ultrart", "lights");
                // -ultrart-lights-preview: the classifier's output (lights\classified) stands in for the shipped files.
                if (_lightsPreview) shipped = Path.Combine(user, "classified");
                _lightOv = new LightOverrides(Logger, shipped, user);
            }
            string scene = SafeCurrentScene();
            if (_lightOv.Scene != scene)
            {
                _lightIdentity.Clear();
                _lightOv.Load(scene);
                _editSel = null;
            }
            else _lightOv.CheckReload();
            _aeSettings.levelBrightness = _lightOverrides ? _lightOv.LevelRtBrightness : 0f; // the level's "exposure" block
            _aeSettings.levelFollowGame = _lightOverrides ? _lightOv.LevelFollowGame : 0f;
            _aeSettings.levelFollowGameUp = _lightOverrides ? _lightOv.LevelFollowGameUp : 0f;
            SyncResolvedLights(); // Plugin.LightClasses.cs
            SyncEmitterScales();
        }

        /// <summary>The light's identity: normalised hierarchy path and the position it had when first seen.</summary>
        private (string path, Vector3 pos) LightIdentity(Light l)
        {
            if (!_lightIdentity.TryGetValue(l, out var id))
            {
                id = (LightOverrides.NormalizedPath(l.transform), l.transform.position);
                _lightIdentity[l] = id;
            }
            return id;
        }

        /// <summary>GatherLights, before the loop: are overrides applied this frame?</summary>
        private void BeginLightOverrides()
        {
            EnsureLightOverrides();
            _ovActiveThisFrame = _lightOverrides && _relightActive;
            OverriddenLightCount = 0;
            AddedLightCount = 0;
        }

        /// <summary>The editor's solo view: only the selected light (and the sun/fills) stays on.</summary>
        private bool SoloHides(Light l) => _editorOn && _editSolo && _editSel != null && l.type != LightType.Directional && !ReferenceEquals(_editSel.Light, l);

        /// <summary>
        /// Applies what was resolved for the light just packed at index <paramref name="k"/> (class policy, rule, entry,
        /// review switches: colour, range, reach, shape, shadows, its share in the air). The position (offset, no fixture
        /// lift) was handled before packing.
        /// </summary>
        private void ApplyResolved(LightResolved r, Light l, int k)
        {
            int i = k * 4;
            var l0 = _lightData[i];
            var c = _lightData[i + 1];
            var l2 = _lightData[i + 2];
            var l3 = _lightData[i + 3];
            float s = r.Intensity * r.Toggle;
            c.x *= r.Color.x * s; c.y *= r.Color.y * s; c.z *= r.Color.z * s;
            bool local = l0.w != 0f;
            if (local && r.Range != 1f) l0.w = Mathf.Max(l0.w * r.Range, 1e-4f);
            // Reach, in quarters, in the colour's w above the player-light flag (UltraRTLights.hlsl, LightReach):
            // w = flag + 2 x (4 - reach x 4).
            if (local) c.w = 2f * Mathf.Round((1f - Mathf.Clamp01(r.Reach)) * 4f);
            _lightMedium[k] = r.Medium;
            // l3.z: 0 shadowed, 0.75 unshadowed, 1 unshadowed fill (relight's fill cut removes it).
            if (r.Shadows.HasValue) l3.z = r.Shadows.Value ? 0f : (l3.z > 0.9f ? l3.z : 0.75f);
            var e = r.Entry;
            int shape = local && e != null ? LightOverrides.ShapeCode(e.Shape) : 0;
            if (shape != 0)
            {
                var fwd = l.transform.forward;
                var n = LightOverrides.Vec(e.Normal, l.type == LightType.Spot ? fwd : Vector3.down);
                var t = LightOverrides.Vec(e.Tangent, LightOverrides.DefaultTangent(n.normalized));
                float sx = e.Size != null && e.Size.Length > 0 ? e.Size[0] : 0.3f;
                float sy = e.Size != null && e.Size.Length > 1 ? e.Size[1] : sx;
                LightOverrides.PackArea(shape, n, t, 0.5f * sx, 0.5f * sy, l3.z, l3.w, out l2, out l3);
            }
            else if (local && e != null && l.type == LightType.Spot)
            {
                // A spot re-aimed ("normal") or widened ("angle"): 6-2's window spots along the game's beam meshes.
                if (e.Normal != null && e.Normal.Length >= 3)
                {
                    var n = LightOverrides.Vec(e.Normal, l.transform.forward).normalized;
                    l2 = new Vector4(n.x, n.y, n.z, l2.w);
                }
                if (e.Angle.HasValue) SpotCone(e.Angle.Value, ref l3);
            }
            _lightData[i] = l0;
            _lightData[i + 1] = c;
            _lightData[i + 2] = l2;
            _lightData[i + 3] = l3;
            if (r.Changes) OverriddenLightCount++;
        }

        // ---------------------------------------------------------------- added lights

        private readonly Dictionary<string, Renderer> _addBind = new Dictionary<string, Renderer>();
        private int _addBindVersion = -1;
        private string _addBindScene;
        private float _nextAddBindScan;

        /// <summary>GatherLights, after the scene lights: the overrides' added lights.</summary>
        private void AppendAddedLights()
        {
            if (!_ovActiveThisFrame || _lightOv.Added.Count == 0) return;
            ResolveAddBinds();
            foreach (var a in _lightOv.Added)
            {
                if (_lightCount >= MaxLights) break;
                if (a.Disabled == true) continue;
                if (_editorOn && _editSolo && _editSel != null && !ReferenceEquals(_editSel.Added, a)) continue;
                if (!string.IsNullOrEmpty(a.Bind))
                {
                    if (!_addBind.TryGetValue(a.Bind, out var r) || r == null || !r.enabled || !r.gameObject.activeInHierarchy) continue;
                }
                PackAdded(a, _lightCount);
                _lightCount++;
                AddedLightCount++;
            }
        }

        /// <summary>A spot's cone in the packed light (Plugin.GatherLights): x cos(half angle), y 1 / (cos(quarter) - cos(half)).</summary>
        private static void SpotCone(float angleDeg, ref Vector4 l3)
        {
            float a = Mathf.Clamp(angleDeg, 1f, 179f) * Mathf.Deg2Rad;
            float cosHalf = Mathf.Cos(a * 0.5f), cosQuarter = Mathf.Cos(a * 0.25f);
            l3.x = cosHalf;
            l3.y = 1f / Mathf.Max(cosQuarter - cosHalf, 1e-4f);
        }

        private void PackAdded(LightAddEntry a, int k)
        {
            int i = k * 4;
            var p = a.Position;
            float range = Mathf.Max(a.Range ?? 10f, 0.05f);
            var col = a.Color != null && a.Color.Length >= 3 ? new Vector3(a.Color[0], a.Color[1], a.Color[2]) : Vector3.one;
            col *= a.Intensity ?? 1f;
            _lightData[i] = new Vector4(p.x, p.y, p.z, range);
            _lightData[i + 1] = new Vector4(col.x, col.y, col.z, 0f);
            if (string.Equals(a.Kind, "spot", System.StringComparison.OrdinalIgnoreCase))
            {
                var d = LightOverrides.Vec(a.Normal, Vector3.down).normalized;
                _lightData[i + 2] = new Vector4(d.x, d.y, d.z, 0f);
                var s3 = new Vector4(0f, 0f, a.Shadows == false ? 0.75f : 0f, 0f);
                SpotCone(a.Angle ?? 30f, ref s3);
                _lightData[i + 3] = s3;
            }
            else
            {
            int shape = Mathf.Max(LightOverrides.ShapeCode(a.Shape), 1);
            var n = LightOverrides.Vec(a.Normal, Vector3.down);
            var t = LightOverrides.Vec(a.Tangent, LightOverrides.DefaultTangent(n.normalized));
            float sx = a.Size != null && a.Size.Length > 0 ? a.Size[0] : 0.5f;
            float sy = a.Size != null && a.Size.Length > 1 ? a.Size[1] : sx;
            LightOverrides.PackArea(shape, n, t, 0.5f * sx, 0.5f * sy, a.Shadows == false ? 0.75f : 0f, 0f, out var l2, out var l3);
            _lightData[i + 2] = l2;
            _lightData[i + 3] = l3;
            }
            uint mask = 0xFFFFFFFFu;
            if (!string.IsNullOrEmpty(a.Mask)) uint.TryParse(a.Mask, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out mask);
            _lightMasks[k] = mask;
            _packedLights[k] = null;
            _lightIds[k] = unchecked((int)(0x40000000u | ((uint)a.Id.GetHashCode() & 0x3FFFFFFFu)));
        }

        /// <summary>Bound renderers by normalised path (looked up when the set or the scene changes, then every 2 s for misses).</summary>
        private void ResolveAddBinds()
        {
            bool stale = _addBindVersion != _lightOv.Version || _addBindScene != _lightOv.Scene;
            bool missing = false;
            foreach (var a in _lightOv.Added)
                if (!string.IsNullOrEmpty(a.Bind) && (!_addBind.TryGetValue(a.Bind, out var r) || r == null)) { missing = true; break; }
            if (!stale && !(missing && Time.realtimeSinceStartup >= _nextAddBindScan)) return;
            _addBindVersion = _lightOv.Version;
            _addBindScene = _lightOv.Scene;
            _nextAddBindScan = Time.realtimeSinceStartup + 2f;
            var want = new HashSet<string>(_lightOv.Added.Where(a => !string.IsNullOrEmpty(a.Bind)).Select(a => a.Bind));
            if (want.Count == 0) return;
            if (stale) _addBind.Clear();
            var names = new HashSet<string>(want.Select(w => w.Substring(w.LastIndexOf('/') + 1)));
            foreach (var r in UnityEngine.Object.FindObjectsOfType<Renderer>(true))
            {
                if (r == null || !names.Contains(LightOverrides.NormalizedName(r.name))) continue;
                var path = LightOverrides.NormalizedPath(r.transform);
                if (!want.Contains(path)) continue;
                // Prefer the instance that is drawn (a checkpoint clone over its disabled original).
                if (!_addBind.TryGetValue(path, out var old) || old == null || (!old.gameObject.activeInHierarchy && r.gameObject.activeInHierarchy)) _addBind[path] = r;
            }
        }

        // ---------------------------------------------------------------- editor

        private sealed class EditTarget
        {
            public Light Light;
            public LightAddEntry Added;
            public bool IsAdded => Added != null;
        }

        private bool _editorOn, _editSolo, _editShowAll = true;
        private bool _editorBenchShow; // bench variant plugin._editorBenchShow=1 (with plugin._editorOn=1): draw the editor in a screenshot
        private EditTarget _editSel;
        private int _editParam;
        private float _editSaveAt = -1f, _editRepeatAt;
        private KeyCode _editRepeatKey;
        private static readonly string[] EditParams =
            { "Intensity", "Red", "Green", "Blue", "Range", "Shadows", "Disabled", "Shape", "Width", "Height", "Move X", "Move Y", "Move Z",
              "Reach (1 relight falloff, 0 capped at the game's)", "Air only (no light on surfaces)", "Air (haze strength)" };

        private static bool KeyPressed(KeyCode k)
        {
            try { return Input.GetKeyDown(k); } catch (InvalidOperationException) { return false; }
        }

        private static bool KeyHeld(KeyCode k)
        {
            try { return Input.GetKey(k); } catch (InvalidOperationException) { return false; }
        }

        /// <summary>OnUpdate: Insert toggles the editor; numpad keys edit while it is on.</summary>
        private void LightEditorUpdate()
        {
            if (_editSaveAt > 0f && Time.realtimeSinceStartup >= _editSaveAt) SaveLightEdits(false);
            if (BenchSafety.Active || !_keysLive) return; // no keys while the console or a text field has the keyboard
            if (KeyPressed(KeyCode.Insert) && !CtrlHeld()) SetLightEditor(!_editorOn); // Ctrl+Insert: the classifier
            if (!_editorOn || _camera == null) return;
            if (_lightOv == null) return;
            if (CtrlHeld())
            {
                // Review switches for the selected light's class (Plugin.LightClasses.cs).
                string cls = _editSel != null && !_editSel.IsAdded && _editSel.Light != null ? ResolvedFor(_editSel.Light).Class : null;
                if (KeyPressed(KeyCode.KeypadDivide) && cls != null)
                {
                    _classSolo = _classSolo == null ? cls : null;
                    _toggleVersion++;
                    Toast(_classSolo != null ? $"Only lights of class '{cls}' (Ctrl+Keypad /)" : "Class solo off");
                }
                if (KeyPressed(KeyCode.Keypad0) && cls != null)
                {
                    bool off = ClassToggleFor(cls) > 0f;
                    SetClassToggle(cls, off ? 0f : 1f);
                    Toast($"Class '{cls}' {(off ? "off" : "on")} (Ctrl+Keypad 0)");
                }
                if (KeyPressed(KeyCode.KeypadPeriod)) { ClearLightToggles(); Toast("Class switches cleared"); }
                return;
            }
            if (KeyPressed(KeyCode.Keypad5)) SelectAimedLight();
            if (KeyPressed(KeyCode.Keypad6)) CycleLight(+1);
            if (KeyPressed(KeyCode.Keypad4)) CycleLight(-1);
            if (KeyPressed(KeyCode.Keypad8)) _editParam = (_editParam + EditParams.Length - 1) % EditParams.Length;
            if (KeyPressed(KeyCode.Keypad2)) _editParam = (_editParam + 1) % EditParams.Length;
            if (KeyPressed(KeyCode.KeypadMultiply)) { _lightOverrides = !_lightOverrides; Toast($"Light overrides {(_lightOverrides ? "on" : "off")} (Keypad *)"); ResetLightHistories(); }
            if (KeyPressed(KeyCode.KeypadDivide)) { _editSolo = !_editSolo; Toast($"Solo {(_editSolo ? "on: only the selected light" : "off")} (Keypad /)"); ResetLightHistories(); }
            if (KeyPressed(KeyCode.KeypadPeriod)) { _editShowAll = !_editShowAll; }
            if (KeyPressed(KeyCode.KeypadEnter)) SaveLightEdits(true);
            if (KeyPressed(KeyCode.Keypad0)) ResetSelected(ShiftHeld());
            if (KeyPressed(KeyCode.Keypad9)) AimSelected();
            if (KeyPressed(KeyCode.Keypad7)) RotateSelected(ShiftHeld() ? -15f : 15f);
            if (KeyPressed(KeyCode.Keypad1)) AddLightAtCrosshair();
            if (KeyPressed(KeyCode.Keypad3)) DeleteSelectedAdded();
            int dir = 0;
            foreach (var (k, d) in new[] { (KeyCode.KeypadPlus, 1), (KeyCode.KeypadMinus, -1) })
            {
                if (KeyPressed(k)) { dir = d; _editRepeatKey = k; _editRepeatAt = Time.realtimeSinceStartup + 0.35f; }
                else if (_editRepeatKey == k && KeyHeld(k) && Time.realtimeSinceStartup >= _editRepeatAt) { dir = d; _editRepeatAt = Time.realtimeSinceStartup + 0.05f; }
            }
            if (dir != 0) StepParam(_editParam, dir, ShiftHeld() ? 0.2f : 1f);
        }

        private void SetLightEditor(bool on)
        {
            _editorOn = on;
            if (!on) { _editSolo = false; ClearLightToggles(); if (_editSaveAt > 0f) SaveLightEdits(false); }
            else if (_editSel == null) SelectAimedLight();
            Toast(on ? "Light editor on (Insert). Numpad: 5 aim-select, 4/6 cycle, 8/2 parameter, +/- change, Enter save" : "Light editor off");
            Logger.LogInfo($"Light editor {(on ? "on" : "off")}");
        }

        private void ResetLightHistories() { _lightSampling?.Reset(); _denoiser?.Reset(); _radianceCache?.Reset(); }

        /// <summary>Editable lights: the active point/spot/directional lights (not the player's own or spawned effects) and the added lights.</summary>
        private List<(EditTarget t, float dist)> EditCandidates()
        {
            var eye = _camera.transform.position;
            var list = new List<(EditTarget, float)>();
            foreach (var l in _activeLights)
            {
                if (l == null || !l.isActiveAndEnabled || l.intensity <= 0f) continue;
                if (_proxies != null && !_proxies.IsBuildLight(l)) continue; // spawned (muzzle flashes, pickups)
                float d = l.type == LightType.Directional ? 1e6f : Vector3.Distance(l.transform.position, eye);
                if (d > 80f && l.type != LightType.Directional) continue;
                if (d < PlayerLightEye) continue; // the player's light at the eye
                list.Add((new EditTarget { Light = l }, d));
            }
            if (_lightOv != null)
                foreach (var a in _lightOv.Added)
                {
                    float d = Vector3.Distance(a.Position, eye);
                    if (d <= 80f) list.Add((new EditTarget { Added = a }, d));
                }
            list.Sort((x, y) => x.Item2.CompareTo(y.Item2));
            return list;
        }

        private static bool SameTarget(EditTarget a, EditTarget b) =>
            a != null && b != null && (a.IsAdded ? b.IsAdded && a.Added.Id == b.Added.Id : ReferenceEquals(a.Light, b.Light));

        private void SelectAimedLight()
        {
            var cam = _camera.transform;
            EditTarget best = null;
            float bestScore = float.MaxValue;
            foreach (var (t, d) in EditCandidates())
            {
                if (!t.IsAdded && t.Light.type == LightType.Directional) continue;
                var p = TargetPosition(t);
                var v = p - cam.position;
                float ang = Vector3.Angle(cam.forward, v);
                if (ang > 12f) continue;
                float score = ang + 0.05f * v.magnitude;
                if (score < bestScore) { bestScore = score; best = t; }
            }
            if (best != null) { _editSel = best; Toast($"Selected {TargetName(best)}"); }
            else Toast("No light under the crosshair (Keypad 4/6 cycles nearby lights)");
        }

        private void CycleLight(int d)
        {
            var list = EditCandidates();
            if (list.Count == 0) { _editSel = null; return; }
            int at = list.FindIndex(x => SameTarget(x.t, _editSel));
            at = at < 0 ? (d > 0 ? 0 : list.Count - 1) : (at + d + list.Count) % list.Count;
            _editSel = list[at].t;
        }

        private Vector3 TargetPosition(EditTarget t)
        {
            if (t.IsAdded) return t.Added.Position;
            var e = EffectiveEntry(t.Light);
            return t.Light.transform.position + (e != null ? LightOverrides.Vec(e.Offset, Vector3.zero) : Vector3.zero);
        }

        private static string TargetName(EditTarget t) => t.IsAdded ? $"added '{t.Added.Id}'" : $"'{t.Light.name}' ({t.Light.type})";

        /// <summary>The entry that applies to a light (even when overrides are switched off), or null.</summary>
        private LightOverrideEntry EffectiveEntry(Light l)
        {
            if (_lightOv == null || l == null) return null;
            var id = LightIdentity(l);
            return _lightOv.Find(id.path, l.name, id.pos);
        }

        private LightOverrideEntry EditEntryFor(Light l)
        {
            var id = LightIdentity(l);
            return _lightOv.EditEntry(id.path, l.name, id.pos, l.type.ToString().ToLowerInvariant());
        }

        private void Edited()
        {
            _lightOv.Touched();
            _editSaveAt = Time.realtimeSinceStartup + 2f;
            _lightSampling?.Reset();
        }

        private void SaveLightEdits(bool announce)
        {
            _editSaveAt = -1f;
            if (_lightOv == null) return;
            var path = _lightOv.SaveUser();
            if (path != null)
            {
                Logger.LogInfo($"Light overrides saved: {path} ({_lightOv.UserCount} entries)");
                if (announce) Toast($"Saved {_lightOv.UserCount} light overrides to {path}");
            }
        }

        private void ResetSelected(bool toGame)
        {
            if (_editSel == null) return;
            if (_editSel.IsAdded) { Toast("Keypad 3 deletes an added light"); return; }
            var id = LightIdentity(_editSel.Light);
            var key = LightOverrides.KeyOf(id.path, id.pos);
            if (toGame) { _lightOv.ResetToGame(EditEntryFor(_editSel.Light)); Toast("Reset to the game's values (Shift+Keypad 0)"); }
            else
            {
                var u = _lightOv.UserEntry(key) ?? _lightOv.UserEntry(EffectiveEntry(_editSel.Light)?.Key ?? "");
                bool removed = u != null && _lightOv.ResetUser(u.Key);
                Toast(removed ? "Your edit removed (shipped values, if any, apply again)" : "No edit of yours on this light (Shift+Keypad 0: back to the game's values)");
            }
            Edited();
        }

        private void AimSelected()
        {
            if (_editSel == null) return;
            var f = _camera.transform.forward;
            if (_editSel.IsAdded) { var a = _lightOv.EditAdded(_editSel.Added); _editSel.Added = a; a.Normal = Arr(f); }
            else { var e = EditEntryFor(_editSel.Light); e.Normal = Arr(f); }
            Toast("Emission direction = view direction");
            Edited();
        }

        private void RotateSelected(float deg)
        {
            if (_editSel == null) return;
            if (_editSel.IsAdded)
            {
                var a = _lightOv.EditAdded(_editSel.Added); _editSel.Added = a;
                var n = LightOverrides.Vec(a.Normal, Vector3.down).normalized;
                a.Tangent = Arr(Quaternion.AngleAxis(deg, n) * LightOverrides.Vec(a.Tangent, LightOverrides.DefaultTangent(n)));
            }
            else
            {
                var e = EditEntryFor(_editSel.Light);
                var n = LightOverrides.Vec(e.Normal, _editSel.Light.type == LightType.Spot ? _editSel.Light.transform.forward : Vector3.down).normalized;
                e.Tangent = Arr(Quaternion.AngleAxis(deg, n) * LightOverrides.Vec(e.Tangent, LightOverrides.DefaultTangent(n)));
            }
            Edited();
        }

        private void AddLightAtCrosshair()
        {
            var cam = _camera.transform;
            Vector3 p, n;
            if (Physics.Raycast(cam.position, cam.forward, out var hit, 200f, ~0, QueryTriggerInteraction.Ignore)) { p = hit.point + hit.normal * 0.02f; n = hit.normal; }
            else { p = cam.position + cam.forward * 3f; n = -cam.forward; }
            var a = new LightAddEntry
            {
                Id = $"user-{DateTime.Now:HHmmss}-{UnityEngine.Random.Range(0, 1000):D3}",
                Kind = "area", Pos = Arr(p), Shape = "rect", Size = new[] { 0.5f, 0.5f }, Normal = Arr(n),
                Color = new[] { 1f, 1f, 1f }, Intensity = 1f, Range = 8f, Note = "added in the editor",
            };
            _lightOv.AddUserLight(a);
            _editSel = new EditTarget { Added = a };
            Toast("Added a 0.5 m rect light at the crosshair (Keypad 3 deletes it)");
            Edited();
        }

        private void DeleteSelectedAdded()
        {
            if (_editSel == null || !_editSel.IsAdded) { Toast("Select an added light first"); return; }
            if (_lightOv.RemoveUserAdded(_editSel.Added.Id)) Toast($"Deleted '{_editSel.Added.Id}'");
            else
            {
                var a = _lightOv.EditAdded(_editSel.Added);
                a.Disabled = true;
                Toast($"Shipped light '{a.Id}' disabled (your file)");
            }
            _editSel = null;
            Edited();
        }

        private static float[] Arr(Vector3 v) => new[] { LightOverrides.Round(v.x), LightOverrides.Round(v.y), LightOverrides.Round(v.z) };

        /// <summary>The selected light's parameter value (for the HUD and the settings menu).</summary>
        private float ParamValue(int p)
        {
            if (_editSel == null) return p == 7 ? 0f : p >= 10 || p == 6 ? 0f : 1f;
            if (_editSel.IsAdded)
            {
                var a = _editSel.Added;
                switch (p)
                {
                    case 0: return a.Intensity ?? 1f;
                    case 1: case 2: case 3: return a.Color != null && a.Color.Length >= 3 ? a.Color[p - 1] : 1f;
                    case 4: return a.Range ?? 10f;
                    case 5: return a.Shadows == false ? 0f : 1f;
                    case 6: return a.Disabled == true ? 1f : 0f;
                    case 7: return Mathf.Max(LightOverrides.ShapeCode(a.Shape), 1);
                    case 8: return a.Size != null && a.Size.Length > 0 ? a.Size[0] : 0.5f;
                    case 9: return a.Size != null && a.Size.Length > 1 ? a.Size[1] : a.Size != null && a.Size.Length > 0 ? a.Size[0] : 0.5f;
                    case 13: case 15: return 1f;
                    case 14: return 0f;
                    default: return a.Position[p - 10];
                }
            }
            var e = EffectiveEntry(_editSel.Light);
            var rs = ResolvedFor(_editSel.Light); // the class's policy shows where the entry says nothing
            switch (p)
            {
                case 0: return rs.Intensity;
                case 1: case 2: case 3: return rs.Color[p - 1];
                case 4: return rs.Range;
                case 5: return rs.Shadows == false ? 0f : 1f;
                case 6: return rs.Disabled ? 1f : 0f;
                case 13: return rs.Reach;
                case 14: return rs.Surfaces ? 0f : 1f;
                case 15: return rs.Medium;
                case 7: return LightOverrides.ShapeCode(e?.Shape);
                case 8: return e?.Size != null && e.Size.Length > 0 ? e.Size[0] : 0.3f;
                case 9: return e?.Size != null && e.Size.Length > 1 ? e.Size[1] : e?.Size != null && e.Size.Length > 0 ? e.Size[0] : 0.3f;
                default: return e != null ? LightOverrides.Vec(e.Offset, Vector3.zero)[p - 10] : 0f;
            }
        }

        private void SetParam(int p, float v)
        {
            if (_editSel == null || _lightOv == null) return;
            if (_editSel.IsAdded)
            {
                var a = _lightOv.EditAdded(_editSel.Added);
                _editSel.Added = a;
                switch (p)
                {
                    case 0: a.Intensity = Mathf.Max(0f, v); break;
                    case 1: case 2: case 3: a.Color = a.Color != null && a.Color.Length >= 3 ? a.Color : new[] { 1f, 1f, 1f }; a.Color[p - 1] = Mathf.Max(0f, v); break;
                    case 4: a.Range = Mathf.Max(0.1f, v); break;
                    case 5: a.Shadows = v > 0.5f ? (bool?)null : false; break;
                    case 6: a.Disabled = v > 0.5f ? true : (bool?)null; break;
                    case 7: a.Shape = LightOverrides.ShapeNames[Mathf.Clamp(Mathf.RoundToInt(v), 1, 3)]; break;
                    case 8: case 9:
                        var sz = a.Size != null && a.Size.Length >= 2 ? a.Size : new[] { ParamValue(8), ParamValue(9) };
                        sz[p - 8] = Mathf.Max(0.01f, v); a.Size = sz; break;
                    case 13: case 14: case 15: break; // scene lights only
                    default:
                        var pos = a.Position; pos[p - 10] = v; a.Pos = Arr(pos); break;
                }
            }
            else
            {
                var l = _editSel.Light;
                var e = EditEntryFor(l);
                switch (p)
                {
                    case 0: e.Intensity = Mathf.Max(0f, v); break;
                    case 1: case 2: case 3:
                    {
                        var rc = ResolvedFor(l).Color;
                        e.Color = e.Color != null && e.Color.Length >= 3 ? e.Color : new[] { rc.x, rc.y, rc.z };
                        e.Color[p - 1] = Mathf.Max(0f, v);
                        break;
                    }
                    case 4: e.Range = Mathf.Max(0.05f, v); break;
                    case 5: e.Shadows = v > 0.5f; break;
                    case 6: e.Disabled = v > 0.5f; break;
                    case 13: e.Reach = Mathf.Round(Mathf.Clamp01(v) * 4f) / 4f; break;
                    case 14: e.Surfaces = v <= 0.5f; break;
                    case 15: e.Medium = Mathf.Max(0f, v); break;
                    case 7:
                        int code = Mathf.Clamp(Mathf.RoundToInt(v), 0, 3);
                        e.Shape = code == 0 ? null : LightOverrides.ShapeNames[code];
                        if (code != 0 && e.Size == null) ShapeDefaults(l, e, code);
                        break;
                    case 8: case 9:
                        var sz = e.Size != null && e.Size.Length >= 2 ? e.Size : new[] { ParamValue(8), ParamValue(9) };
                        sz[p - 8] = Mathf.Max(0.01f, v); e.Size = sz; break;
                    default:
                        var o = LightOverrides.Vec(e.Offset, Vector3.zero); o[p - 10] = v; e.Offset = Arr(o); break;
                }
                e.Source = "user";
            }
            Edited();
        }

        /// <summary>A light switched to an area shape: size and place it like its fixture, if it has one (the lamp's bottom face).</summary>
        private void ShapeDefaults(Light l, LightOverrideEntry e, int shape)
        {
            if (_proxies != null && _proxies.FixtureBounds.TryGetValue(l, out var b))
            {
                float w = Mathf.Clamp(b.size.x, 0.05f, 4f), h = Mathf.Clamp(b.size.z, 0.05f, 4f);
                e.Size = shape == 2 ? new[] { LightOverrides.Round(w), LightOverrides.Round(h) } : new[] { LightOverrides.Round(Mathf.Min(w, h)), LightOverrides.Round(Mathf.Min(w, h)) };
                if (shape >= 2 && e.Offset == null)
                {
                    // Just below the housing, facing down.
                    var p = l.transform.position;
                    e.Offset = Arr(new Vector3(b.center.x - p.x, b.min.y - 0.03f - p.y, b.center.z - p.z));
                }
                e.Tangent = Arr(Vector3.right);
            }
            else e.Size = shape == 1 ? new[] { 0.3f, 0.3f } : new[] { 0.5f, 0.5f };
        }

        private void StepParam(int p, int dir, float fine)
        {
            if (_editSel == null) { Toast("No light selected (Keypad 5 aims, 4/6 cycle)"); return; }
            float v = ParamValue(p);
            switch (p)
            {
                case 0: case 4:
                    v = Mathf.Max(v, 0.01f) * Mathf.Pow(1f + 0.1f * fine, dir); break;
                case 1: case 2: case 3: v = Mathf.Max(0f, v + 0.05f * fine * dir); break;
                case 5: case 6: case 14: v = v > 0.5f ? 0f : 1f; break;
                case 13: v = Mathf.Clamp01(v + 0.25f * dir); break;
                case 15: v = Mathf.Max(0f, v + 0.1f * fine * dir); break;
                case 7: v = ((int)v + dir + 4) % 4; if (_editSel.IsAdded && v == 0) v = dir > 0 ? 1 : 3; break;
                case 8: case 9: v = Mathf.Max(0.01f, v + 0.05f * fine * dir); break;
                default: v += 0.05f * fine * dir; break;
            }
            SetParam(p, v);
        }

        // ---------------------------------------------------------------- gizmos (OnGUI)

        private GUIStyle _editStyle, _editSmall;

        private void DrawLightEditor()
        {
            if (!_editorOn || _camera == null || (BenchSafety.Active && !_editorBenchShow)) return;
            if (_editSel == null && _editorBenchShow && Event.current.type == EventType.Layout) CycleLight(+1); // bench: the nearest light
            if (_editStyle == null)
            {
                _editStyle = new GUIStyle(GUI.skin.label) { fontSize = 17, richText = true, normal = { textColor = Color.white } };
                _editSmall = new GUIStyle(GUI.skin.label) { fontSize = 13, richText = true, normal = { textColor = new Color(1f, 1f, 1f, 0.85f) } };
            }
            var prevColor = GUI.color;
            try
            {
                if (_editShowAll)
                    foreach (var (t, d) in EditCandidates())
                    {
                        if (d > 40f || SameTarget(t, _editSel)) continue;
                        if (!t.IsAdded && t.Light.type == LightType.Directional) continue;
                        var p = TargetPosition(t);
                        if (!Project(p, out var sp)) continue;
                        var rs = t.IsAdded ? null : ResolvedFor(t.Light);
                        bool has = t.IsAdded || rs.Changes;
                        bool hidden = rs != null && (rs.Disabled || rs.Intensity * rs.Toggle <= 0f);
                        var col = t.IsAdded ? new Color(0.4f, 1f, 1f, 0.8f) : hidden ? new Color(1f, 0.35f, 0.35f, 0.8f) : has ? new Color(1f, 0.8f, 0.2f, 0.8f) : new Color(1f, 1f, 1f, 0.55f);
                        Cross(sp, 6f, col);
                        GUI.color = col;
                        GUI.Label(new Rect(sp.x + 6, sp.y - 8, 420, 20), t.IsAdded ? t.Added.Id : $"{t.Light.name}  [{rs.Class}{(has ? ": " + rs.PolicyText() : "")}]", _editSmall);
                    }
                if (_editSel != null && (_editSel.IsAdded || _editSel.Light != null)) DrawSelected();
                DrawEditorPanel();
            }
            finally { GUI.color = prevColor; }
        }

        private void DrawSelected()
        {
            var t = _editSel;
            var sel = new Color(0.3f, 1f, 0.3f, 1f);
            Vector3 p = TargetPosition(t);
            int shape;
            Vector3 n, tan;
            float sx, sy, range;
            if (t.IsAdded)
            {
                var a = t.Added;
                shape = Mathf.Max(LightOverrides.ShapeCode(a.Shape), 1);
                n = LightOverrides.Vec(a.Normal, Vector3.down).normalized;
                tan = LightOverrides.Vec(a.Tangent, LightOverrides.DefaultTangent(n));
                sx = ParamValue(8); sy = ParamValue(9); range = a.Range ?? 10f;
            }
            else
            {
                var l = t.Light;
                var e = EffectiveEntry(l);
                shape = l.type == LightType.Directional ? 0 : LightOverrides.ShapeCode(e?.Shape);
                n = LightOverrides.Vec(e?.Normal, l.type == LightType.Spot ? l.transform.forward : Vector3.down).normalized;
                tan = LightOverrides.Vec(e?.Tangent, LightOverrides.DefaultTangent(n));
                sx = ParamValue(8); sy = ParamValue(9); range = l.range * (e?.Range ?? 1f);
                if (e != null && e.HasOffset) Line3(l.transform.position, p, new Color(1f, 0.5f, 0.2f, 0.9f)); // from the game's position
                if (l.type == LightType.Spot && shape == 0)
                {
                    // Spot cone: 8 lines at the cone's edge, out to the range.
                    var f = l.transform.forward;
                    var u = LightOverrides.DefaultTangent(f);
                    var v = Vector3.Cross(f, u);
                    float ang = l.spotAngle * 0.5f * Mathf.Deg2Rad;
                    for (int k = 0; k < 8; k++)
                    {
                        float phi = k * Mathf.PI / 4f;
                        var dirK = (f * Mathf.Cos(ang) + (u * Mathf.Cos(phi) + v * Mathf.Sin(phi)) * Mathf.Sin(ang)).normalized;
                        Line3(p, p + dirK * range, new Color(1f, 1f, 0.3f, 0.5f));
                    }
                }
            }
            if (!t.IsAdded && t.Light.type == LightType.Directional) return;
            tan = (tan - n * Vector3.Dot(tan, n)).normalized;
            if (tan.sqrMagnitude < 0.5f) tan = LightOverrides.DefaultTangent(n);
            var bt = Vector3.Cross(n, tan);
            if (shape == 2)
            {
                var a0 = p + tan * (0.5f * sx) + bt * (0.5f * sy);
                var a1 = p - tan * (0.5f * sx) + bt * (0.5f * sy);
                var a2 = p - tan * (0.5f * sx) - bt * (0.5f * sy);
                var a3 = p + tan * (0.5f * sx) - bt * (0.5f * sy);
                Line3(a0, a1, sel); Line3(a1, a2, sel); Line3(a2, a3, sel); Line3(a3, a0, sel);
                Line3(a0, a2, new Color(0.3f, 1f, 0.3f, 0.4f));
            }
            else if (shape == 3 || shape == 1)
            {
                float r = 0.5f * sx;
                var ax = shape == 1 ? new[] { (tan, bt), (tan, n), (bt, n) } : new[] { (tan, bt) };
                foreach (var (u, v) in ax)
                    for (int k = 0; k < 24; k++)
                    {
                        float a0 = k * Mathf.PI / 12f, a1 = (k + 1) * Mathf.PI / 12f;
                        Line3(p + (u * Mathf.Cos(a0) + v * Mathf.Sin(a0)) * r, p + (u * Mathf.Cos(a1) + v * Mathf.Sin(a1)) * r, sel);
                    }
            }
            if (shape >= 2) Line3(p, p + n * 0.5f, new Color(0.3f, 0.6f, 1f, 1f)); // emission direction
            if (Project(p, out var sp))
            {
                Cross(sp, 10f, sel);
                // The range as a circle around the light (screen-space radius at the light's depth); the game's light fades out at 2x.
                float depth = Vector3.Dot(p - _camera.transform.position, _camera.transform.forward);
                if (depth > 0.1f)
                {
                    float px = range / (depth * Mathf.Tan(_camera.fieldOfView * 0.5f * Mathf.Deg2Rad)) * (Screen.height * 0.5f);
                    if (px < Screen.width * 2f) Circle2(sp, px, new Color(0.3f, 1f, 0.3f, 0.55f));
                }
            }
        }

        private void DrawEditorPanel()
        {
            var sb = new StringBuilder();
            sb.Append("<b>UltraRT light editor</b>  ");
            sb.Append(_lightOverrides ? (_relightActive ? "<color=#8f8>overrides on</color>" : "<color=#fa4>overrides on (relight / pathtrace only)</color>") : "<color=#f66>overrides OFF (Keypad *)</color>");
            if (_editSolo) sb.Append("  <color=#ff8>SOLO</color>");
            if (LightTogglesActive)
                sb.Append("  <color=#ff8>classes: " + (_classSolo != null ? "only " + _classSolo + " " : "") +
                          string.Join(" ", _classToggles.Select(kv => kv.Key + (kv.Value <= 0f ? " off" : " x" + kv.Value.ToString("0.##", CultureInfo.InvariantCulture)))) + "</color>");
            sb.Append($"\n{_lightOv?.Scene}: {_lightOv?.EffectiveCount ?? 0} overrides ({_lightOv?.ShippedCount ?? 0} shipped, {_lightOv?.UserCount ?? 0} yours), {_lightOv?.Added.Count ?? 0} added; this frame {OverriddenLightCount} changed, {AddedLightCount} added on, {_lightCount} packed");
            if (_editSel == null) sb.Append("\n\nNo light selected: Keypad 5 selects the light at the crosshair, 4/6 cycle nearby lights.");
            else
            {
                var t = _editSel;
                if (t.IsAdded)
                {
                    var a = t.Added;
                    sb.Append($"\n\n<b>{a.Id}</b>  (added {a.Kind ?? "area"} light, {a.Origin}{(a.Bind != null ? ", bound to " + a.Bind : "")})");
                    if (a.Note != null) sb.Append($"\n<i>{a.Note}</i>");
                }
                else
                {
                    var l = t.Light;
                    var id = LightIdentity(l);
                    var e = EffectiveEntry(l);
                    sb.Append($"\n\n<b>{l.name}</b>  {l.type}, {Vector3.Distance(l.transform.position, _camera.transform.position):F1} m away\n<size=12>{id.path}</size>");
                    sb.Append($"\ngame: colour ({l.color.r:F2}, {l.color.g:F2}, {l.color.b:F2}) x {l.intensity:F2}, range {l.range:F1} m{(l.type == LightType.Spot ? $", spot {l.spotAngle:F0} deg" : "")}");
                    var rs = ResolvedFor(l);
                    sb.Append($"\nclass <b>{rs.Class}</b>{(rs.Rule != "" ? $" (rule: {rs.Rule})" : " (no rule)")}; RT: {rs.PolicyText()}{(rs.Toggle != 1f ? $"; switch x{rs.Toggle:0.##}" : "")}");
                    if (e != null)
                    {
                        sb.Append($"\nentry: {e.Origin}{(e.Source != null ? ", " + e.Source : "")}{(e.Class != null ? ", class " + e.Class : "")}{(e.Confidence.HasValue ? $", confidence {e.Confidence:F2}" : "")}");
                        if (e.Note != null) sb.Append($"\n<i>{e.Note}</i>");
                    }
                    else sb.Append("\nno override (the game's values)");
                }
                sb.Append('\n');
                for (int p = 0; p < EditParams.Length; p++)
                {
                    string val = ParamText(p);
                    sb.Append(p == _editParam ? $"\n<color=#8f8><b>> {EditParams[p]}: {val}</b></color>" : $"\n   {EditParams[p]}: {val}");
                }
            }
            sb.Append("\n\n<size=12>Numpad: 5 aim-select  4/6 prev/next  8/2 parameter  +/- change (Shift fine)  7 rotate rect  9 face view direction\n" +
                      "0 drop your edit (Shift: game values)  1 add rect light at crosshair  3 delete added  / solo  * overrides on/off  . labels  Enter save\n" +
                      "Ctrl + /  only this light's class   Ctrl + 0  this class off / on   Ctrl + .  clear class switches</size>");
            var text = sb.ToString();
            var size = _editStyle.CalcSize(new GUIContent(text));
            float w = Mathf.Min(Mathf.Max(size.x, 560f) + 24f, Screen.width * 0.6f);
            float h = _editStyle.CalcHeight(new GUIContent(text), w - 20f) + 16f;
            var r = new Rect(20, Screen.height - h - 40, w, h);
            GUI.color = new Color(0f, 0f, 0f, 0.6f);
            GUI.DrawTexture(r, Texture2D.whiteTexture);
            GUI.color = Color.white;
            GUI.Label(new Rect(r.x + 10, r.y + 8, r.width - 20, r.height - 16), text, _editStyle);
        }

        private string ParamText(int p)
        {
            float v = ParamValue(p);
            bool added = _editSel != null && _editSel.IsAdded;
            switch (p)
            {
                case 0: return added ? v.ToString("F2") : $"x{v:F2}";
                case 1: case 2: case 3: return added ? v.ToString("F2") : $"x{v:F2}";
                case 4: return added ? $"{v:F1} m" : $"x{v:F2}";
                case 5: return v > 0.5f ? "on" : "off";
                case 6: case 14: return v > 0.5f ? "yes" : "no";
                case 13: return v.ToString("F2");
                case 15: return $"x{v:F2}";
                case 7: return LightOverrides.ShapeNames[Mathf.Clamp((int)v, 0, 3)];
                case 8: case 9: return $"{v:F2} m";
                default: return added ? $"{v:F2}" : $"{v:+0.00;-0.00;0.00} m";
            }
        }

        private bool Project(Vector3 world, out Vector2 screen)
        {
            var v = _camera.WorldToViewportPoint(world);
            screen = new Vector2(v.x * Screen.width, (1f - v.y) * Screen.height);
            return v.z > 0.05f;
        }

        private void Line3(Vector3 a, Vector3 b, Color c)
        {
            // Clip against a plane just in front of the camera.
            var cam = _camera.transform;
            float da = Vector3.Dot(a - cam.position, cam.forward) - 0.1f, db = Vector3.Dot(b - cam.position, cam.forward) - 0.1f;
            if (da < 0f && db < 0f) return;
            if (da < 0f) a = Vector3.Lerp(a, b, da / (da - db));
            else if (db < 0f) b = Vector3.Lerp(a, b, da / (da - db));
            Project(a, out var sa);
            Project(b, out var sb);
            Line2(sa, sb, c);
        }

        private static void Line2(Vector2 a, Vector2 b, Color c, float width = 2f)
        {
            var d = b - a;
            float len = d.magnitude;
            if (len < 0.5f || len > 20000f) return;
            var m = GUI.matrix;
            GUIUtility.RotateAroundPivot(Mathf.Atan2(d.y, d.x) * Mathf.Rad2Deg, a);
            GUI.color = c;
            GUI.DrawTexture(new Rect(a.x, a.y - width * 0.5f, len, width), Texture2D.whiteTexture);
            GUI.matrix = m;
        }

        private static void Cross(Vector2 p, float s, Color c)
        {
            Line2(p - new Vector2(s, 0), p + new Vector2(s, 0), c);
            Line2(p - new Vector2(0, s), p + new Vector2(0, s), c);
        }

        private static void Circle2(Vector2 p, float r, Color c)
        {
            const int n = 48;
            for (int k = 0; k < n; k++)
            {
                float a0 = k * 2f * Mathf.PI / n, a1 = (k + 1) * 2f * Mathf.PI / n;
                Line2(p + new Vector2(Mathf.Cos(a0), Mathf.Sin(a0)) * r, p + new Vector2(Mathf.Cos(a1), Mathf.Sin(a1)) * r, c, 1.5f);
            }
        }
    }
}
