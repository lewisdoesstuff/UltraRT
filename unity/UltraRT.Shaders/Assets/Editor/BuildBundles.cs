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

            // Ray tracing shaders only compile for D3D12; keep D3D11 so the rest still loads there.
            PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.StandaloneWindows64, false);
            PlayerSettings.SetGraphicsAPIs(BuildTarget.StandaloneWindows64,
                new[] { GraphicsDeviceType.Direct3D12, GraphicsDeviceType.Direct3D11 });

            var build = new AssetBundleBuild
            {
                assetBundleName = BundleName,
                assetNames = new[]
                {
                    "Assets/UltraRT/Shaders/Composite.shader",
                    "Assets/UltraRT/Shaders/DebugView.raytrace",
                    "Assets/UltraRT/Shaders/Denoise.compute",
                    "Assets/UltraRT/Shaders/PathTrace.raytrace",
                    "Assets/UltraRT/Shaders/Proxy.shader",
                    "Assets/UltraRT/Shaders/Reflections.compute",
                    "Assets/UltraRT/Shaders/UnpackHybrid.compute",
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
