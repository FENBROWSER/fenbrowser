using System;
using FenBrowser.FenEngine.Layout;
using FenBrowser.FenEngine.Rendering;
using SkiaSharp;

namespace FenBrowser.FenEngine.Svg
{
    /// <summary>
    /// Resolves SVG text through the browser font system. Headless Linux builds
    /// without a system font manager may supply one trusted process-level font
    /// file through FEN_SVG_FONT_PATH. SVG source can never select a file path.
    /// <para>
    /// A run that declares a language is resolved through a typeface installed
    /// for that language before the platform character fallback, so the same
    /// script authored under different language tags does not collapse onto a
    /// single fallback face. The language only decides runs the platform
    /// default cannot draw on its own, so Latin content under a language tag
    /// keeps the Latin face.
    /// </para>
    /// </summary>
    internal static class SvgTypefaceResolver
    {
        /// <summary>
        /// Bound on the comma separated <c>font-family</c> entries inspected per
        /// run, so a pathological list cannot drive unbounded family probing.
        /// </summary>
        private const int MaxFamilyEntries = 8;

        /// <summary>
        /// Bound on the language tag length inspected per run. The longest tag
        /// the language table distinguishes is far shorter than this.
        /// </summary>
        private const int MaxLanguageTagChars = 35;

        private static readonly Lazy<SKTypeface> ConfiguredFallback =
            new(LoadConfiguredFallback, isThreadSafe: true);

        private static readonly string[] JapaneseFamilies =
        {
            "Yu Gothic", "MS Gothic", "Meiryo", "Hiragino Sans", "Noto Sans CJK JP"
        };

        private static readonly string[] KoreanFamilies =
        {
            "Malgun Gothic", "Apple SD Gothic Neo", "Noto Sans CJK KR"
        };

        private static readonly string[] SimplifiedChineseFamilies =
        {
            "Microsoft YaHei", "SimHei", "SimSun", "Noto Sans CJK SC", "PingFang SC"
        };

        private static readonly string[] TraditionalChineseFamilies =
        {
            "Microsoft JhengHei", "PMingLiU", "MingLiU", "Noto Sans CJK TC", "PingFang TC"
        };

        public static SKTypeface Resolve(
            string family,
            string text,
            string language,
            int weight,
            SKFontStyleSlant slant)
        {
            if (TryResolveNamedFamily(family, text, weight, slant, out SKTypeface named))
            {
                return named;
            }

            if (IsGenericFamilyList(family))
            {
                var substituted = TextLayoutHelper.ResolveTypeface(family, text, weight, slant);
                if (Supports(substituted, text)) return substituted;
            }

            var configured = ConfiguredFallback.Value;
            if (configured != null && Supports(configured, text))
            {
                return configured;
            }

            SKTypeface languageTypeface = ResolveLanguageTypeface(language, text, weight, slant);
            if (languageTypeface != null) return languageTypeface;

            var resolved = TextLayoutHelper.ResolveTypeface(family, text, weight, slant);
            return Supports(resolved, text) ? resolved : null;
        }

        private static bool IsGenericFamilyList(string family)
        {
            if (string.IsNullOrWhiteSpace(family)) return false;
            foreach (string candidate in family.Split(','))
            {
                string token = candidate.Trim().Trim('"', '\'');
                if (token.Length != 0 && !IsGenericFamily(token)) return false;
            }
            return true;
        }

        /// <summary>
        /// Resolves the first author-named, non-generic family that is actually
        /// installed and covers the run. A generic keyword is not a family, so a
        /// list that only carries generics falls through to the platform
        /// substitution the browser would perform.
        /// </summary>
        private static bool TryResolveNamedFamily(
            string family,
            string text,
            int weight,
            SKFontStyleSlant slant,
            out SKTypeface typeface)
        {
            typeface = null;
            if (string.IsNullOrWhiteSpace(family)) return false;

            int inspected = 0;
            ReadOnlySpan<char> remaining = family.AsSpan().Trim();
            while (!remaining.IsEmpty && inspected < MaxFamilyEntries)
            {
                int comma = remaining.IndexOf(',');
                ReadOnlySpan<char> entry = comma < 0 ? remaining : remaining.Slice(0, comma);
                remaining = comma < 0 ? ReadOnlySpan<char>.Empty : remaining.Slice(comma + 1);
                inspected++;

                string clean = NormalizeFamilyName(entry);
                if (clean.Length == 0 || IsGenericFamily(clean)) continue;
                SKTypeface candidate = ResolveInstalledFamily(clean, text, weight, slant);
                if (candidate != null)
                {
                    typeface = candidate;
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Resolves one family name to an installed typeface that covers the run,
        /// or null when the family is absent or cannot draw the run. Skia answers
        /// an unknown family with its default face, so the resolved family name is
        /// what separates an installed family from a miss. A family registered
        /// from an <c>@font-face</c> rule is trusted under the name it was
        /// registered with, because its face can legitimately carry a different
        /// family name.
        /// </summary>
        private static SKTypeface ResolveInstalledFamily(
            string family,
            string text,
            int weight,
            SKFontStyleSlant slant)
        {
            SKTypeface registered = FontRegistry.TryResolve(family, weight, slant);
            if (registered != null) return Supports(registered, text) ? registered : null;

            SKTypeface system = TextLayoutHelper.ResolveTypeface(family, text, weight, slant);
            if (system == null || !IsRequestedFamily(system, family)) return null;
            return Supports(system, text) ? system : null;
        }

        private static SKTypeface ResolveLanguageTypeface(
            string language,
            string text,
            int weight,
            SKFontStyleSlant slant)
        {
            string[] families = LanguageFamilies(language);
            if (families == null) return null;

            // The tag only steers runs the platform default cannot draw on its
            // own. Latin content under a language tag keeps the Latin face, so a
            // language never re-shapes text the default already renders.
            if (Supports(SKTypeface.Default, text)) return null;

            foreach (string family in families)
            {
                SKTypeface candidate = ResolveInstalledFamily(family, text, weight, slant);
                if (candidate != null) return candidate;
            }
            return null;
        }

        /// <summary>
        /// Maps a language tag onto the installed family preferences that carry
        /// its script, most specific first. A tag the table does not distinguish
        /// resolves to nothing so the run keeps the platform default.
        /// </summary>
        private static string[] LanguageFamilies(string language)
        {
            if (string.IsNullOrWhiteSpace(language)) return null;

            ReadOnlySpan<char> tag = language.AsSpan().Trim();
            if (tag.Length > MaxLanguageTagChars) tag = tag.Slice(0, MaxLanguageTagChars);

            int separator = tag.IndexOfAny('-', '_');
            ReadOnlySpan<char> primary = separator < 0 ? tag : tag.Slice(0, separator);
            ReadOnlySpan<char> rest = separator < 0
                ? ReadOnlySpan<char>.Empty
                : tag.Slice(separator + 1);

            if (primary.Equals("ja", StringComparison.OrdinalIgnoreCase)) return JapaneseFamilies;
            if (primary.Equals("ko", StringComparison.OrdinalIgnoreCase)) return KoreanFamilies;
            if (primary.Equals("zh", StringComparison.OrdinalIgnoreCase))
            {
                return IsTraditionalChinese(rest) ? TraditionalChineseFamilies : SimplifiedChineseFamilies;
            }
            return null;
        }

        /// <summary>
        /// True for the script subtags and regions that identify traditional
        /// Chinese. A bare <c>zh</c> has no script subtag, so it follows the
        /// simplified-first order the table declares.
        /// </summary>
        private static bool IsTraditionalChinese(ReadOnlySpan<char> subtag) =>
            subtag.Equals("Hant", StringComparison.OrdinalIgnoreCase) ||
            subtag.Equals("TW", StringComparison.OrdinalIgnoreCase) ||
            subtag.Equals("HK", StringComparison.OrdinalIgnoreCase) ||
            subtag.Equals("MO", StringComparison.OrdinalIgnoreCase);

        private static string NormalizeFamilyName(ReadOnlySpan<char> entry)
        {
            ReadOnlySpan<char> value = entry.Trim();
            while (value.Length >= 2 && (value[0] == '\'' || value[0] == '"') && value[value.Length - 1] == value[0])
            {
                value = value.Slice(1, value.Length - 2).Trim();
            }
            return value.ToString();
        }

        private static bool IsGenericFamily(string family) =>
            family.Equals("serif", StringComparison.OrdinalIgnoreCase) ||
            family.Equals("sans-serif", StringComparison.OrdinalIgnoreCase) ||
            family.Equals("monospace", StringComparison.OrdinalIgnoreCase) ||
            family.Equals("cursive", StringComparison.OrdinalIgnoreCase) ||
            family.Equals("fantasy", StringComparison.OrdinalIgnoreCase) ||
            family.Equals("system-ui", StringComparison.OrdinalIgnoreCase) ||
            family.Equals("ui-serif", StringComparison.OrdinalIgnoreCase) ||
            family.Equals("ui-sans-serif", StringComparison.OrdinalIgnoreCase) ||
            family.Equals("ui-monospace", StringComparison.OrdinalIgnoreCase) ||
            family.Equals("ui-rounded", StringComparison.OrdinalIgnoreCase) ||
            family.Equals("math", StringComparison.OrdinalIgnoreCase) ||
            family.Equals("emoji", StringComparison.OrdinalIgnoreCase) ||
            family.Equals("fangsong", StringComparison.OrdinalIgnoreCase) ||
            family.Equals("inherit", StringComparison.OrdinalIgnoreCase) ||
            family.Equals("initial", StringComparison.OrdinalIgnoreCase);

        private static bool IsRequestedFamily(SKTypeface typeface, string family)
        {
            string actual = typeface?.FamilyName;
            return !string.IsNullOrWhiteSpace(actual) &&
                   actual.Trim().Equals(family, StringComparison.OrdinalIgnoreCase);
        }

        private static SKTypeface LoadConfiguredFallback()
        {
            string path = Environment.GetEnvironmentVariable(
                Adapters.SvgRendererConfiguration.FontFallbackPathEnvironmentVariable);
            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }
            try
            {
                return SKTypeface.FromFile(path);
            }
            catch
            {
                return null;
            }
        }

        private static bool Supports(SKTypeface typeface, string text)
        {
            if (typeface == null) return false;
            using var font = new SKFont(typeface);
            return font.ContainsGlyphs(text);
        }
    }
}
