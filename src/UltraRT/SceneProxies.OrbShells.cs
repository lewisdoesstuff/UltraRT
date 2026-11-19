using System.Collections.Generic;
using System.Text.RegularExpressions;
using UnityEngine;

namespace UltraRT
{
    /// <summary>
    /// White statue orbs (docs/scene-proxies.md, "White orbs"). A cerberus statue's orb is a lit ball (Cerb_Apple) inside a
    /// glow shell, Sphere_N, with the orb's point light under it. Most shells are the game's additive ExplosionSphere, so
    /// the ball shows through a soft glow; some (1-4's boss door, 3 - Main Hall) are the opaque white unlit "Light"
    /// material instead, and the orb reads as a flat white ball (user, playtest of 2026-11-18: "should look translucent"
    /// like the orange ones).
    ///
    /// While frames are traced, such a shell is drawn as a white additive glow (a copy of its material with the game's
    /// additive blend, tinted with the orb light's colour), and it is kept out of the RT scene, as the ExplosionSphere
    /// shells are (transparent): the ball inside is what rays see, and the orb counts as an orb fixture
    /// (<see cref="InGlowShell"/>). The original material comes back when tracing stops. -ultrart-noorbshells: off.
    /// </summary>
    internal sealed partial class SceneProxies
    {
        public static bool OrbShells = true;
        /// <summary>The shell's additive colour: the orb light's colour (normalised to its largest channel) times this.
        /// Bench plugin._orbShellGain.</summary>
        public static float OrbShellGain = 0.35f;

        private sealed class OrbShell
        {
            public Renderer Renderer;
            public Material[] Original;
            public Material[] Swapped;
            public Color Tint;
        }

        private static readonly Regex ShellName = new Regex(@"^Sphere_\d+$");
        private readonly List<OrbShell> _orbShells = new List<OrbShell>();
        private readonly HashSet<Renderer> _orbShellRenderers = new HashSet<Renderer>();
        private bool _orbShellsOn;
        private float _orbShellGainApplied = -1f;
        public int OrbShellCount => _orbShells.Count;

        /// <summary>An opaque "Light" sphere with a point light under it, beside a Cerb_Apple ball: the shell of a white orb.</summary>
        private static bool IsOpaqueOrbShell(Renderer r, out Light light)
        {
            light = null;
            if (!OrbShells || r == null || !(r is MeshRenderer) || !ShellName.IsMatch(r.name)) return false;
            var m = r.sharedMaterial;
            if (m == null || m.name.Replace(" (Instance)", "") != "Light" || m.renderQueue >= 2500) return false;
            light = r.GetComponentInChildren<Light>(true);
            if (light == null || light.type != LightType.Point) return false;
            var parent = r.transform.parent;
            if (parent == null) return false;
            foreach (var other in parent.GetComponentsInChildren<Renderer>(true))
                if (other != r && other.name == "Cerb_Apple") return true;
            return false;
        }

        /// <summary>ProcessMeshRenderer: a white orb's shell is noted and left out of the RT scene.</summary>
        private bool NoteOrbShell(Renderer r)
        {
            if (_orbShellRenderers.Contains(r)) return true;
            if (!IsOpaqueOrbShell(r, out var light)) return false;
            var c = light.color;
            float mx = Mathf.Max(c.r, Mathf.Max(c.g, c.b), 1e-3f);
            var shell = new OrbShell { Renderer = r, Original = r.sharedMaterials, Tint = new Color(c.r / mx, c.g / mx, c.b / mx, 1f) };
            _orbShells.Add(shell);
            _orbShellRenderers.Add(r);
            _log.LogInfo($"White orb shell: '{GameControlPath(r.transform)}' (light colour {c}), drawn as a glow while traced");
            if (_orbShellsOn) ApplyShell(shell, true);
            return true;
        }

        /// <summary>Every frame from the plugin: the glow while frames are traced, the game's material otherwise.</summary>
        public void SetOrbShells(bool on)
        {
            if (_orbShells.Count == 0) { _orbShellsOn = on; return; }
            bool gainChanged = on && _orbShellGainApplied != OrbShellGain;
            if (on == _orbShellsOn && !gainChanged) return;
            _orbShellsOn = on;
            if (gainChanged) foreach (var s in _orbShells) s.Swapped = null;
            _orbShellGainApplied = on ? OrbShellGain : _orbShellGainApplied;
            for (int i = _orbShells.Count - 1; i >= 0; i--)
            {
                var s = _orbShells[i];
                if (s.Renderer == null) { _orbShells.RemoveAt(i); continue; }
                ApplyShell(s, on);
            }
        }

        private void ApplyShell(OrbShell s, bool on)
        {
            if (s.Renderer == null) return;
            if (!on) { s.Renderer.sharedMaterials = s.Original; return; }
            if (s.Swapped == null)
            {
                s.Swapped = new Material[s.Original.Length];
                for (int i = 0; i < s.Original.Length; i++)
                {
                    var src = s.Original[i];
                    if (src == null) continue;
                    var m = new Material(src) { name = src.name + " [UltraRT orb shell]", renderQueue = 3000 };
                    // The game's additive blend, as on its ExplosionSphere shells (ULTRAKILL/Master: TRANSPARENCY, One One).
                    m.EnableKeyword("TRANSPARENCY");
                    m.DisableKeyword("_ZWRITE_ON");
                    if (m.HasProperty("_ZWrite")) m.SetFloat("_ZWrite", 0f);
                    if (m.HasProperty("_BlendMode")) m.SetFloat("_BlendMode", 3f);
                    if (m.HasProperty("_SrcBlend")) m.SetFloat("_SrcBlend", 1f);
                    if (m.HasProperty("_DstBlend")) m.SetFloat("_DstBlend", 1f);
                    if (m.HasProperty("_Color")) m.SetColor("_Color", new Color(s.Tint.r * OrbShellGain, s.Tint.g * OrbShellGain, s.Tint.b * OrbShellGain, 1f));
                    s.Swapped[i] = m;
                }
            }
            s.Renderer.sharedMaterials = s.Swapped;
        }

        /// <summary>Scene teardown: every shell back to the game's material.</summary>
        private void RestoreOrbShells()
        {
            foreach (var s in _orbShells) if (s.Renderer != null) s.Renderer.sharedMaterials = s.Original;
            _orbShells.Clear();
            _orbShellRenderers.Clear();
            _orbShellsOn = false;
        }
    }
}
