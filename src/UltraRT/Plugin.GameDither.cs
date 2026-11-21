using UnityEngine;

namespace UltraRT
{
    // The game's own screen dither (docs/ultrakill-rendering.md, "Final post-process: dither"). ULTRAKILL/PostProcessV2,
    // the game's last full-screen pass, soft-light blends a 16x16 Bayer texture (PostProcessV2_Handler.ditherTexture, the
    // material's _Dither) over the whole frame:
    //     col = lerp(col, (1 - 2d) col^2 + 2 d col, saturate(_DitherStrength))  =  col + s col (1 - col) (2d - 1)
    // so a mid-grey pixel moves by up to +-s/4 (+-13 of 255 at the default s = 0.2), black and white not at all. The
    // strength is the global _DitherStrength: the game's Options > Graphics > Dithering slider (pref "dithering", default
    // 0.2; GraphicsSettings.SetDithering), which some levels override (ColorSchemeSetter.replaceDitherUserSetting). It is
    // a look, not an anti-banding dither: it does not scale with Color Compression, and in PC mode (no compression) it
    // still draws a 1-pixel checkerboard, which on a ray-traced image reads as a texture.
    //
    // While frames are traced it is off (the global holds a marker value, effectively 0); the game's value comes back
    // when tracing stops (F7, -ultrart-off, a failed trace) and when the plugin goes away. The user's prefs are never
    // written: only the shader global changes, and a value the game writes while we hold ours (the slider, a level's
    // override) is remembered and restored instead.
    public partial class Plugin
    {
        /// <summary>The game's dither while frames are traced (settings menu, [View] GameDither, -ultrart-game-dither on|off,
        /// bench plugin._gameDither=1). Off by default.</summary>
        private bool _gameDither;
        private static readonly int DitherStrengthId = Shader.PropertyToID("_DitherStrength");
        // Our value of the global while we hold it: not one the game writes (0 .. 1 from a slider, a level's amount), and
        // saturate()d to nothing in the shader.
        private const float DitherMarker = 1.0e-20f;
        private bool _ditherHeld;
        private float _gameDitherValue;
        /// <summary>Our own dither while the game's is off (UltraRTDither.hlsl): +-0.5 of an 8-bit step where our passes write
        /// float colour into the game's 8-bit target, against 1-step contours in dark gradients. -ultrart-output-dither
        /// on|off, bench plugin._outputDither=0.</summary>
        private bool _outputDither = true;
        private static readonly int OutDitherId = Shader.PropertyToID("_UltraRTOutDither");

        /// <summary>Every frame from UpdateParticles (rtTraced known there): holds or releases the game's dither.</summary>
        private void UpdateGameDither(bool traced)
        {
            float g = Shader.GetGlobalFloat(DitherStrengthId);
            bool hold = traced && !_gameDither;
            // Native float HDR (Plugin.Hdr.cs): the passes write float colour into a float target, nothing to dither.
            Shader.SetGlobalVector(OutDitherId, new Vector4(hold && _outputDither && !HdrNativeLive ? 1f : 0f, Time.frameCount & 63, 0f, 0f));
            if (hold)
            {
                if (g == DitherMarker) return;
                // First frame, or the game wrote a new value since (Options slider, a level's ColorSchemeSetter).
                if (!_ditherHeld || g != _gameDitherValue) Logger.LogInfo($"Game dither: off while traced (the game's strength {g:0.###})");
                _gameDitherValue = g;
                _ditherHeld = true;
                Shader.SetGlobalFloat(DitherStrengthId, DitherMarker);
            }
            else ReleaseGameDither();
        }

        private void ReleaseGameDither()
        {
            if (!_ditherHeld) return;
            _ditherHeld = false;
            // Only if it is still ours: a value the game wrote in the meantime is the newer one.
            if (Shader.GetGlobalFloat(DitherStrengthId) == DitherMarker) Shader.SetGlobalFloat(DitherStrengthId, _gameDitherValue);
            Logger.LogInfo($"Game dither: restored ({_gameDitherValue:0.###})");
        }

        private void OnDestroy()
        {
            ReleaseGameDither();
            Shader.SetGlobalVector(OutDitherId, Vector4.zero);
            PortalRasterSkip.Active = false; // the game's portal views render again (docs/portals.md, "Raster skip")
        }
    }
}
