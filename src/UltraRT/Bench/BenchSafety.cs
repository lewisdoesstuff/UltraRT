using System.Reflection;
using HarmonyLib;

namespace UltraRT.Bench
{
    /// <summary>
    /// While a benchmark runs (cheats on, forced level loads, noclip camera), nothing may reach the user's save,
    /// preferences or Steam stats. Cheats alone do NOT stop saves (bestiary, money, secrets, ranks all still write),
    /// so we skip the underlying writers. Patches are installed only when a benchmark is requested.
    /// </summary>
    internal static class BenchSafety
    {
        public static bool Active;

        private static readonly (string type, string method)[] Targets =
        {
            ("GameProgressSaver", "WriteFile"),          // every save-file writer funnels through this
            ("GameProgressSaver", "WipeSlot"),
            ("GameProgressSaver", "ResetBestCyber"),
            ("GameProgressSaver", "MergeRootWithSlotOne"),
            ("PrefsManager", "CommitPrefs"),             // Prefs.json (cheat state, settings)
            ("SteamController", "AddToStatInt"),
            ("SteamController", "UpdateTimeInSandbox"),
            ("LeaderboardController", "SubmitLevelScore"),
            ("LeaderboardController", "SubmitCyberGrindScore"),
        };

        public static int Install(Harmony harmony, BepInEx.Logging.ManualLogSource log)
        {
            var prefix = new HarmonyMethod(typeof(BenchSafety).GetMethod(nameof(SkipWhenActive), BindingFlags.Static | BindingFlags.NonPublic));
            int n = 0;
            foreach (var (type, method) in Targets)
            {
                var m = AccessTools.Method(AccessTools.TypeByName(type), method);
                if (m == null) { log.LogError($"BenchSafety: {type}.{method} not found; refusing to benchmark"); return -1; }
                harmony.Patch(m, prefix: prefix);
                n++;
            }
            return n;
        }

        // Returning false skips the original method.
        private static bool SkipWhenActive() => !Active;
    }
}
