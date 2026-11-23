using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace UltraRT.Editor
{
    /// <summary>
    /// Builds the shader bundle the BepInEx plugin loads at runtime.
    /// Batch mode: Unity.exe -batchmode -quit -projectPath unity/UltraRT.Shaders -executeMethod UltraRT.Editor.BuildBundles.Build [-ultrart-out DIR]
    /// </summary>
    public static class BuildBundles
    {
        public const string BundleName = "ultrart.shaders";

        [MenuItem("UltraRT/Build Shader Bundle")]
        public static void Build()
        {
            string outDir = "Build";
            var args = System.Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
                if (args[i] == "-ultrart-out") outDir = args[i + 1];
            Directory.CreateDirectory(outDir);

            // UltraRT requires Direct3D 12 (DXR); compiling D3D11 variants only bloats the bundle.
            PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.StandaloneWindows64, false);
            PlayerSettings.SetGraphicsAPIs(BuildTarget.StandaloneWindows64,
                new[] { GraphicsDeviceType.Direct3D12 });

            var build = new AssetBundleBuild
            {
                assetBundleName = BundleName,
                assetNames = new[]
                {
                    "Assets/UltraRT/Shaders/BloodWet.shader",
                    "Assets/UltraRT/Shaders/CacheResolve.compute",
                    "Assets/UltraRT/Shaders/CacheUpdate.raytrace",
                    "Assets/UltraRT/Shaders/Composite.shader",
                    "Assets/UltraRT/Shaders/DebugView.raytrace",
                    "Assets/UltraRT/Shaders/Displace.compute",
                    "Assets/UltraRT/Shaders/Denoise.compute",
                    "Assets/UltraRT/Shaders/EffectProxy.shader",
                    "Assets/UltraRT/Shaders/Exposure.compute",
                    "Assets/UltraRT/Shaders/LavaProxy.shader",
                    "Assets/UltraRT/Shaders/LightGrid.compute",
                    "Assets/UltraRT/Shaders/LitParticle.shader",
                    "Assets/UltraRT/Shaders/MotionCopy.compute",
                    "Assets/UltraRT/Shaders/ParticleLight.compute",
                    "Assets/UltraRT/Shaders/ParticleLight.raytrace",
                    "Assets/UltraRT/Shaders/PathTrace.raytrace",
                    "Assets/UltraRT/Shaders/PathTraceCount.raytrace",
                    "Assets/UltraRT/Shaders/Portal.shader",
                    "Assets/UltraRT/Shaders/Proxy.shader",
                    "Assets/UltraRT/Shaders/CutoutTrace.raytrace",
                    "Assets/UltraRT/Shaders/RayReconstruction.compute",
                    "Assets/UltraRT/Shaders/StainGrid.compute",
                    "Assets/UltraRT/Shaders/StainedGlassBake.shader",
                    "Assets/UltraRT/Shaders/SkyConvert.shader",
                    "Assets/UltraRT/Shaders/Reflections.compute",
                    "Assets/UltraRT/Shaders/TransparentOverlay.shader",
                    "Assets/UltraRT/Shaders/Upsample.compute",
                    "Assets/UltraRT/Shaders/VeilComposite.shader",
                    "Assets/UltraRT/Shaders/VeilProxy.shader",
                    "Assets/UltraRT/Shaders/VeilTrace.raytrace",
                    "Assets/UltraRT/Shaders/Vfx.shader",
                    "Assets/UltraRT/Shaders/Viewmodel.raytrace",
                    "Assets/UltraRT/Shaders/ViewmodelComposite.shader",
                    "Assets/UltraRT/Shaders/ViewmodelFilter.compute",
                    "Assets/UltraRT/Shaders/ViewmodelGBuffer.shader",
                    "Assets/UltraRT/Shaders/Volumetrics.compute",
                    "Assets/UltraRT/Shaders/Volumetrics.raytrace",
                    "Assets/UltraRT/Shaders/VolumetricsComposite.shader",
                },
            };

            var manifest = BuildPipeline.BuildAssetBundles(outDir, new[] { build },
                BuildAssetBundleOptions.ChunkBasedCompression | BuildAssetBundleOptions.StrictMode,
                BuildTarget.StandaloneWindows64);

            if (manifest == null)
            {
                Debug.LogError("[UltraRT] Bundle build failed");
                if (Application.isBatchMode) EditorApplication.Exit(1);
                return;
            }
            Debug.Log($"[UltraRT] Built {Path.GetFullPath(Path.Combine(outDir, BundleName))}");
        }
    }
}
