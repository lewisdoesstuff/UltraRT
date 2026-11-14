using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace UltraRT
{
    /// <summary>
    /// Game hooks. Kept minimal: we only observe, never change game behaviour.
    /// </summary>
    internal static class Patches
    {
        /// <summary>Hierarchies enabled since the last frame, to be registered with the RT scene.</summary>
        public static readonly List<GameObject> PendingHierarchies = new List<GameObject>();

        /// <summary>
        /// Objects the game instantiated since the last frame (projectiles, explosions with lights, props, room clones), so
        /// they are registered right away instead of waiting for a scene-wide rescan. Only collected while an RT scene
        /// exists (<see cref="CollectSpawns"/>); capped, and the plugin falls back to a rescan if it overflows.
        /// </summary>
        public static readonly List<GameObject> PendingSpawns = new List<GameObject>();
        public static bool CollectSpawns;
        public static bool SpawnsOverflowed;
        public const int MaxPendingSpawns = 4096;
        public static int SpawnCount;

        public static void Apply(string id)
        {
            var harmony = new Harmony(id);
            harmony.PatchAll(typeof(Patches).Assembly);
            // Instantiate<T>(T) is generic and goes straight to native code (gibs, fire pools, BulkInstantiate use it).
            // Mono shares one body for every reference-type T, so patching one instantiation covers them all. Optional:
            // without it the periodic rescan still finds those objects.
            try
            {
                var generic = typeof(Object).GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .First(m => m.Name == "Instantiate" && m.IsGenericMethodDefinition && m.GetParameters().Length == 1);
                harmony.Patch(generic.MakeGenericMethod(typeof(Object)),
                    postfix: new HarmonyMethod(typeof(Patches).GetMethod(nameof(GenericInstantiatePostfix), BindingFlags.NonPublic | BindingFlags.Static)));
                GenericInstantiateHooked = true;
            }
            catch (System.Exception e) { GenericInstantiateError = e.Message; }
        }

        public static bool GenericInstantiateHooked;
        public static string GenericInstantiateError;

        private static void GenericInstantiatePostfix(object __result) => Spawned(__result as Object);

        private static void Spawned(Object result)
        {
            if (!CollectSpawns || result == null) return;
            GameObject go = result as GameObject;
            if (go == null && result is Component c) go = c.gameObject;
            if (go == null) return;
            SpawnCount++;
            if (PendingSpawns.Count >= MaxPendingSpawns) { SpawnsOverflowed = true; return; }
            PendingSpawns.Add(go);
        }

        /// <summary>
        /// Every non-generic UnityEngine.Object.Instantiate overload. The generic overloads with a position or parent call
        /// these; only the generic Instantiate&lt;T&gt;(T) goes straight to native code and is not seen (the rescan catches it).
        /// </summary>
        [HarmonyPatch]
        private static class InstantiatePatch
        {
            private static IEnumerable<MethodBase> TargetMethods() =>
                typeof(Object).GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .Where(m => m.Name == "Instantiate" && !m.IsGenericMethodDefinition && m.ReturnType == typeof(Object));

            private static void Postfix(Object __result) => Spawned(__result);
        }

        /// <summary>Dormant enemies waking up and freshly spawned ones (Cyber Grind waves) both pass through OnEnable.</summary>
        [HarmonyPatch(typeof(EnemyIdentifier), "OnEnable")]
        private static class EnemyEnablePatch
        {
            private static void Postfix(EnemyIdentifier __instance)
            {
                if (__instance != null) PendingHierarchies.Add(__instance.gameObject);
            }
        }

        /// <summary>Record baked level geometry's original materials before they are swapped for the atlas (OriginalMaterials.cs).</summary>
        [HarmonyPatch(typeof(StaticSceneOptimizer), "SetupMeshes")]
        private static class StaticSceneOptimizerSetupMeshesPatch
        {
            private static void Prefix(StaticSceneOptimizer __instance)
            {
                try { if (__instance != null) OriginalMaterials.Capture(__instance.staticMRends); }
                catch (System.Exception) { /* never break the game's level setup */ }
            }
        }
    }
}
