using System.Collections.Generic;
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

        public static void Apply(string id) => new Harmony(id).PatchAll(typeof(Patches).Assembly);

        /// <summary>Dormant enemies waking up and freshly spawned ones (Cyber Grind waves) both pass through OnEnable.</summary>
        [HarmonyPatch(typeof(EnemyIdentifier), "OnEnable")]
        private static class EnemyEnablePatch
        {
            private static void Postfix(EnemyIdentifier __instance)
            {
                if (__instance != null) PendingHierarchies.Add(__instance.gameObject);
            }
        }
    }
}
