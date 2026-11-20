using System.Reflection;
using UnityEngine;
using UnityEngine.Rendering;

namespace UltraRT
{
    // The game's enemy outlines (docs/ultrakill-rendering.md, "Enemy outlines"). PostProcessV2_Handler.SetupOutlines puts a
    // command buffer "Outlines" on the main camera at AfterEverything that reads the RG16 outline mask (reusableBufferA,
    // written as an MRT target by the game's shaders) and multiplies the frame by black on a 1-pixel ring around every masked
    // pixel (ULTRAKILL/OutlinePx "Composite1Px": a pixel is masked when mask.r > 0.999 or mask.r + mask.g > 1), or a wider
    // ring with the Enemy Simplifier on and an outline thickness above 1. It draws over the traced image as over the game's.
    //
    // For trailer shots both images go without them: the buffer is taken off the camera while off (every frame, because
    // SetupOutlines puts it back on pref changes and camera re-inits) and the game's SetupOutlines restores it. The user's
    // prefs ("outlineThickness", "simplifyEnemies") are never written.
    public partial class Plugin
    {
        /// <summary>The game's enemy outlines drawn (true, the game's behaviour) or taken off the camera (false):
        /// -ultrart-enemy-outlines on|off, bench scenario field "EnemyOutlines", bench plugin._enemyOutlines=0.</summary>
        private bool _enemyOutlines = true;
        /// <summary>The command-line value, which a scenario without "EnemyOutlines" goes back to.</summary>
        private bool _enemyOutlinesDefault = true;
        private bool _outlinesRemoved;
        private static readonly FieldInfo PpOutlineCb = typeof(PostProcessV2_Handler).GetField("outlineCB", BindingFlags.NonPublic | BindingFlags.Instance);

        /// <summary>Every frame (UpdateParticles): keeps the game's outline buffer off the camera while outlines are off.</summary>
        private void UpdateEnemyOutlines()
        {
            if (_enemyOutlines && !_outlinesRemoved) return;
            var pp = MonoSingleton<PostProcessV2_Handler>.Instance;
            if (pp == null || pp.mainCam == null) return;
            if (!_enemyOutlines)
            {
                if (PpOutlineCb?.GetValue(pp) is CommandBuffer cb)
                {
                    pp.mainCam.RemoveCommandBuffer(CameraEvent.AfterEverything, cb);
                    if (!_outlinesRemoved) Logger.LogInfo("Enemy outlines: off (the game's outline buffer is off the camera)");
                    _outlinesRemoved = true;
                }
                return;
            }
            // Back on: the game's own setup adds the buffer again for its current prefs.
            _outlinesRemoved = false;
            try { pp.SetupOutlines(); Logger.LogInfo("Enemy outlines: on again (the game's SetupOutlines)"); }
            catch (System.Exception e) { Logger.LogWarning($"Enemy outlines: SetupOutlines: {e.Message}"); }
        }
    }
}
