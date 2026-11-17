using System.Collections.Generic;
using Game.SceneFlow;

namespace BlockBuilder
{
    /// <summary>
    /// Names the game shows for prefabs ("Assets.NAME[prefab]" in the active language), with a
    /// readable fallback for prefabs that have none (most growables): underscores become spaces.
    /// Cached per language; <see cref="Locale"/> changes when the player switches language.
    /// </summary>
    internal static class PrefabNames
    {
        private static readonly Dictionary<string, string> s_Cache = new Dictionary<string, string>();
        private static string s_Locale;

        /// <summary>Active language id ("" before the game is up).</summary>
        public static string Locale => GameManager.instance?.localizationManager?.activeLocaleId ?? string.Empty;

        public static string Of(string prefab)
        {
            if (string.IsNullOrEmpty(prefab))
            {
                return string.Empty;
            }

            string locale = Locale;
            if (locale != s_Locale)
            {
                s_Cache.Clear();
                s_Locale = locale;
            }

            if (s_Cache.TryGetValue(prefab, out var name))
            {
                return name;
            }

            name = null;
            try
            {
                var dictionary = GameManager.instance?.localizationManager?.activeDictionary;
                if (dictionary != null && dictionary.TryGetValue($"Assets.NAME[{prefab}]", out var found) && !string.IsNullOrWhiteSpace(found))
                {
                    name = found.Trim();
                }
            }
            catch
            {
                // Missing dictionary during loading: fall back below and retry after a language change.
            }

            name ??= Readable(prefab);
            s_Cache[prefab] = name;
            return name;
        }

        /// <summary>"EU_ResidentialLow01_L1_3x4" → "EU ResidentialLow01 L1 3x4".</summary>
        internal static string Readable(string prefab) => prefab.Replace('_', ' ').Trim();
    }
}
