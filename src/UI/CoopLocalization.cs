using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using BALLxPITLocalCoop.Core;
using HarmonyLib;
using I2.Loc;

namespace BALLxPITLocalCoop.UI
{
    /// <summary>
    /// Cached EN/FR localization synchronized with I2.Loc, without early empty-string poisoning
    /// or permanent global OnMissingTranslation leaks on unload.
    /// </summary>
    public static class CoopLocalization
    {
        private static readonly Dictionary<string, string> TranslationCache = new Dictionary<string, string>(StringComparer.Ordinal);
        private static readonly Dictionary<IntPtr, LanguageSourceData.MissingTranslationAction> OriginalMissingActions =
            new Dictionary<IntPtr, LanguageSourceData.MissingTranslationAction>();

        private static string _lastLangCode = string.Empty;
        private static string _lastLangName = string.Empty;
        private static bool _cachedIsFrench;
        private static bool _installed;

        public static bool IsFrench => _cachedIsFrench;

        public static string Text(string english, string french) =>
            _cachedIsFrench ? (french ?? english ?? string.Empty) : (english ?? string.Empty);

        public static void Install(Harmony harmony)
        {
            if (_installed || harmony == null)
            {
                return;
            }
            _installed = true;

            try
            {
                foreach (MethodInfo method in typeof(LanguageSourceData).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    if (method.Name == nameof(LanguageSourceData.TryGetTranslation))
                    {
                        HarmonyHelper.TryPatch(
                            harmony,
                            method,
                            prefix: typeof(CoopLocalization).GetMethod(nameof(OnTryGetTranslationPrefix), BindingFlags.Static | BindingFlags.NonPublic),
                            label: "LanguageSourceData.TryGetTranslation");
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Logger?.LogWarning($"Failed installing localization silencer: {ex.Message}");
            }

            RefreshLanguageState(force: true);
            SilenceSourceMissingWarnings();
        }

        public static void Shutdown()
        {
            try
            {
                var sources = LocalizationManager.Sources;
                if (sources != null)
                {
                    for (int i = 0; i < sources.Count; i++)
                    {
                        LanguageSourceData src = sources[i];
                        if (src != null && OriginalMissingActions.TryGetValue(src.Pointer, out var orig))
                        {
                            src.OnMissingTranslation = orig;
                        }
                    }
                }
            }
            catch { }

            OriginalMissingActions.Clear();
            TranslationCache.Clear();
            _lastLangCode = string.Empty;
            _lastLangName = string.Empty;
            _cachedIsFrench = false;
            _installed = false;
        }

        public static void Tick()
        {
            RefreshLanguageState(force: false);
        }

        private static void RefreshLanguageState(bool force)
        {
            string code = string.Empty;
            string name = string.Empty;
            try
            {
                code = LocalizationManager.CurrentLanguageCode ?? string.Empty;
                name = LocalizationManager.CurrentLanguage ?? string.Empty;
            }
            catch { }

            if (!force
                && string.Equals(code, _lastLangCode, StringComparison.Ordinal)
                && string.Equals(name, _lastLangName, StringComparison.Ordinal))
            {
                return;
            }

            _lastLangCode = code;
            _lastLangName = name;
            _cachedIsFrench =
                (!string.IsNullOrEmpty(code) && code.StartsWith("fr", StringComparison.OrdinalIgnoreCase))
                || (!string.IsNullOrEmpty(name)
                    && (name.IndexOf("french", StringComparison.OrdinalIgnoreCase) >= 0
                        || name.IndexOf("fran", StringComparison.OrdinalIgnoreCase) >= 0));
            TranslationCache.Clear();
            SilenceSourceMissingWarnings();
        }

        public static string Translate(string term)
        {
            if (string.IsNullOrWhiteSpace(term))
            {
                return string.Empty;
            }

            if (TranslationCache.TryGetValue(term, out string cached))
            {
                return cached;
            }

            string result = string.Empty;
            bool sourcesReady = false;
            try
            {
                var sources = LocalizationManager.Sources;
                sourcesReady = sources != null && sources.Count > 0;
                if (sourcesReady
                    && LocalizationManager.TryGetTranslation(term, out string translated, FixForRTL: true, 0, ignoreRTLnumbers: true, applyParameters: false, null, null, true)
                    && IsValidTranslation(translated))
                {
                    result = translated;
                }
            }
            catch { }

            // Only cache non-empty results or once I2 sources are populated (prevents early empty-string poisoning).
            if (sourcesReady || result.Length > 0)
            {
                TranslationCache[term] = result;
            }
            return result;
        }

        public static bool IsValidTranslation(string value)
        {
            return !string.IsNullOrWhiteSpace(value)
                && value.IndexOf("Missing Translation", StringComparison.OrdinalIgnoreCase) < 0
                && !string.Equals(value.Trim(), "defaultchar", StringComparison.OrdinalIgnoreCase);
        }

        public static string PrettifyEnumName(string enumName)
        {
            if (string.IsNullOrWhiteSpace(enumName))
            {
                return string.Empty;
            }
            string raw = enumName.StartsWith("k", StringComparison.Ordinal) && enumName.Length > 1 && char.IsUpper(enumName[1])
                ? enumName.Substring(1)
                : enumName;
            var sb = new StringBuilder(raw.Length + 6);
            for (int i = 0; i < raw.Length; i++)
            {
                char ch = raw[i];
                if (ch == '_')
                {
                    sb.Append(' ');
                    continue;
                }
                if (i > 0 && char.IsUpper(ch) && !char.IsUpper(raw[i - 1]) && raw[i - 1] != ' ')
                {
                    sb.Append(' ');
                }
                sb.Append(ch);
            }
            return sb.ToString().Trim();
        }

        public static string StageName(LevelInfo info)
        {
            if (info == null)
            {
                return Text("Unknown Stage", "Niveau inconnu");
            }
            try
            {
                string slugName = Translate(info.GetNameSlug());
                if (IsValidTranslation(slugName))
                {
                    return slugName;
                }
            }
            catch { }
            try
            {
                string direct = info.GetNameTranslation(false);
                if (IsValidTranslation(direct))
                {
                    return direct;
                }
            }
            catch { }
            if (IsValidTranslation(info.Name) && !info.Name.Contains("_"))
            {
                return info.Name;
            }
            return PrettifyEnumName(info.Type.ToString());
        }

        private static void OnTryGetTranslationPrefix(LanguageSourceData __instance)
        {
            if (!CoopMode.Enabled || __instance == null)
            {
                return;
            }
            if (__instance.OnMissingTranslation == LanguageSourceData.MissingTranslationAction.ShowWarning)
            {
                IntPtr ptr = __instance.Pointer;
                if (ptr != IntPtr.Zero && !OriginalMissingActions.ContainsKey(ptr))
                {
                    OriginalMissingActions[ptr] = __instance.OnMissingTranslation;
                }
                __instance.OnMissingTranslation = LanguageSourceData.MissingTranslationAction.Fallback;
            }
        }

        private static void SilenceSourceMissingWarnings()
        {
            try
            {
                var sources = LocalizationManager.Sources;
                if (sources == null)
                {
                    return;
                }
                for (int i = 0; i < sources.Count; i++)
                {
                    LanguageSourceData src = sources[i];
                    if (src != null && src.OnMissingTranslation == LanguageSourceData.MissingTranslationAction.ShowWarning)
                    {
                        IntPtr ptr = src.Pointer;
                        if (ptr != IntPtr.Zero && !OriginalMissingActions.ContainsKey(ptr))
                        {
                            OriginalMissingActions[ptr] = src.OnMissingTranslation;
                        }
                        src.OnMissingTranslation = LanguageSourceData.MissingTranslationAction.Fallback;
                    }
                }
            }
            catch { }
        }
    }
}
