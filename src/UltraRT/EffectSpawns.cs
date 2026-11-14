using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace UltraRT
{
    /// <summary>
    /// Every GameObject the game instantiates (explosions, muzzle flashes, projectiles, sparks, gore...), queued for
    /// <see cref="EffectLights"/>, so effect lights and emitters are known on the frame they appear instead of at the next
    /// FindObjectsOfType scan. Observe only: the postfix appends to a list and never touches the clone.
    /// Covers the non-generic Object.Instantiate overloads, which every generic overload with a position or parent calls.
    /// The single-argument generic Instantiate&lt;T&gt;(T) calls native code directly and is not seen; those clones are
    /// still found by the regular light scan.
    /// </summary>
    internal static class EffectSpawns
    {
        /// <summary>Set by the plugin while something consumes <see cref="Pending"/>; the hook is a no-op otherwise.</summary>
        public static bool Enabled;
        public static readonly List<GameObject> Pending = new List<GameObject>(256);
        public const int MaxPending = 4096;
        public static int Dropped;

        internal static void OnSpawn(Object o)
        {
            if (!Enabled || ReferenceEquals(o, null)) return;
            GameObject go = o as GameObject;
            if (ReferenceEquals(go, null))
            {
                var c = o as Component;
                if (ReferenceEquals(c, null)) return;
                go = c.gameObject;
            }
            if (Pending.Count < MaxPending) Pending.Add(go);
            else Dropped++;
        }

        [HarmonyPatch]
        private static class InstantiatePatch
        {
            private static IEnumerable<MethodBase> TargetMethods() =>
                typeof(Object).GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .Where(m => m.Name == "Instantiate" && !m.IsGenericMethodDefinition && m.ReturnType == typeof(Object))
                    // (Object, Transform) only forwards to (Object, Transform, bool).
                    .Where(m => !(m.GetParameters().Length == 2 && m.GetParameters()[1].ParameterType == typeof(Transform)))
                    .Cast<MethodBase>();

            private static void Postfix(Object __result) => OnSpawn(__result);
        }
    }
}
