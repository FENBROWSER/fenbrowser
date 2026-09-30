using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using SkiaSharp;
using FenBrowser.Core.Cache;
using FenBrowser.Core.Logging;

namespace FenBrowser.FenEngine.Layout
{
    public class TextLine
    {
        public string Text;
        public float Width;
        public float Y;
    }

    public static class TextLayoutHelper
    {
        private static readonly string[] s_sansSerifFallbacks =
        {
            "Segoe UI",
            "Arial",
            "Helvetica",
            "Roboto",
            "Open Sans"
        };
        private const int MaxSystemTypefaceCacheEntries = 64;
        private static readonly BoundedLruCache<SystemTypefaceCacheKey, SKTypeface> s_systemTypefaceCache =
            new(MaxSystemTypefaceCacheEntries, long.MaxValue, static (_, _) => 256);

        private readonly record struct SystemTypefaceCacheKey(
            string Family,
            int Weight,
            SKFontStyleSlant Slant);

        /// <summary>
        /// Resolves the appropriate Typeface based on font-family, weight, slant and content.
        /// </summary>
        public static SKTypeface ResolveTypeface(string fontFamily, string text, int weight = 400, SKFontStyleSlant slant = SKFontStyleSlant.Upright)
        {
            return ResolveTypefaceInternal(fontFamily, text, weight, slant);
        }

        private static SKTypeface ResolveTypefaceInternal(
            string fontFamily,
            string text,
            int weight,
            SKFontStyleSlant slant)
        {
            if (!string.IsNullOrEmpty(fontFamily))
            {
                var families = fontFamily.Split(',');
                foreach (var f in families)
                {
                    var clean = f.Trim().Trim('\'', '"');

                    if (clean.Equals("sans-serif", StringComparison.OrdinalIgnoreCase))
                        clean = "Segoe UI";
                    else if (clean.Equals("serif", StringComparison.OrdinalIgnoreCase))
                        clean = "Georgia";
                    else if (clean.Equals("monospace", StringComparison.OrdinalIgnoreCase))
                        clean = "Consolas";

                    var tf = FenBrowser.FenEngine.Rendering.FontRegistry.TryResolve(clean, weight, slant);
                    if (tf != null && SupportsCharacters(tf, text)) return tf;

                    if (TryGetCachedSystemTypeface(clean, weight, slant, out var cachedSystemTypeface) &&
                        SupportsCharacters(cachedSystemTypeface, text))
                    {
                        return cachedSystemTypeface;
                    }

                    // CSS Fonts 4 §5: a family that is not installed is skipped for the next
                    // one in the list. SKTypeface.FromFamilyName never reports a miss - it
                    // hands back the default family at the default weight - so an unknown
                    // first family ("Mona Sans VF" on github.com) painted every weight as
                    // Segoe UI Regular while layout measured the real Semibold.
                    var systemTf = SKFontManager.Default.MatchFamily(
                        clean,
                        new SKFontStyle((SKFontStyleWeight)weight, SKFontStyleWidth.Normal, slant));
                    if (TryPublishCreatedSystemTypeface(clean, weight, slant, text, systemTf, out var resolvedSystemTypeface))
                    {
                        return resolvedSystemTypeface;
                    }
                }
            }

            foreach (var fallbackFont in s_sansSerifFallbacks)
            {
                if (TryGetCachedSystemTypeface(fallbackFont, weight, slant, out var cachedFallbackTypeface) &&
                    SupportsCharacters(cachedFallbackTypeface, text))
                {
                    return cachedFallbackTypeface;
                }

                var tf = SKTypeface.FromFamilyName(
                    fallbackFont,
                    (SKFontStyleWeight)weight,
                    SKFontStyleWidth.Normal,
                    slant);
                if (TryPublishCreatedSystemTypeface(fallbackFont, weight, slant, text, tf, out var resolvedFallbackTypeface))
                {
                    return resolvedFallbackTypeface;
                }
            }

            if (!string.IsNullOrEmpty(text))
            {
                foreach (var c in text)
                {
                    if (c <= 255) continue;

                    var matched = SKFontManager.Default.MatchCharacter(c);
                    if (matched != null &&
                        TryPublishCreatedSystemTypeface(null, weight, slant, c.ToString(), matched, out var resolvedMatchedTypeface))
                    {
                        FenBrowser.Core.EngineLogCompat.Debug(
                            $"[FONT-MATCH] Resolved typeface for non-ASCII char '{c}' (0x{(int)c:X4}): {resolvedMatchedTypeface.FamilyName}",
                            FenBrowser.Core.Logging.LogCategory.Rendering);
                        return resolvedMatchedTypeface;
                    }
                }
            }

            if (!string.IsNullOrEmpty(text))
            {
                foreach (var c in text)
                {
                    if (c <= 0x2000 || c >= 0x3000) continue;

                    var matched = SKFontManager.Default.MatchCharacter(c);
                    if (matched != null &&
                        TryPublishCreatedSystemTypeface(null, weight, slant, c.ToString(), matched, out var resolvedSymbolTypeface))
                    {
                        return resolvedSymbolTypeface;
                    }
                }

                foreach (var c in text)
                {
                    if (char.IsWhiteSpace(c)) continue;

                    var matched = SKFontManager.Default.MatchCharacter(c);
                    if (matched != null &&
                        TryPublishCreatedSystemTypeface(null, weight, slant, c.ToString(), matched, out var resolvedCharacterTypeface))
                    {
                        return resolvedCharacterTypeface;
                    }
                    break;
                }
            }

            var segoe = SKTypeface.FromFamilyName("Segoe UI");
            if (TryPublishCreatedSystemTypeface("Segoe UI", weight, slant, text, segoe, out var resolvedSegoe))
            {
                return resolvedSegoe;
            }

            var arial = SKTypeface.FromFamilyName("Arial");
            if (TryPublishCreatedSystemTypeface("Arial", weight, slant, text, arial, out var resolvedArial))
            {
                return resolvedArial;
            }

            return SKTypeface.Default;
        }

        private static bool TryGetCachedSystemTypeface(
            string family,
            int weight,
            SKFontStyleSlant slant,
            out SKTypeface typeface)
        {
            var key = new SystemTypefaceCacheKey(family.ToLowerInvariant(), weight, slant);
            return s_systemTypefaceCache.TryGetValue(key, out typeface);
        }

        private static bool TryPublishCreatedSystemTypeface(
            string family,
            int weight,
            SKFontStyleSlant slant,
            string text,
            SKTypeface candidate,
            out SKTypeface resolved)
        {
            resolved = null;
            if (candidate == null) return false;

            try
            {
                var candidateFamily = candidate.FamilyName;
                if (string.IsNullOrWhiteSpace(candidateFamily) || !SupportsCharacters(candidate, text))
                {
                    return false;
                }

                if (ReferenceEquals(candidate, SKTypeface.Default))
                {
                    resolved = candidate;
                    return true;
                }

                var cacheFamily = string.IsNullOrWhiteSpace(family) ? candidateFamily : family;
                var key = new SystemTypefaceCacheKey(cacheFamily.ToLowerInvariant(), weight, slant);
                if (s_systemTypefaceCache.TryGetValue(key, out var cached))
                {
                    if (!SupportsCharacters(cached, text)) return false;
                    resolved = cached;
                    return true;
                }

                s_systemTypefaceCache.Set(key, candidate);
                resolved = candidate;
                return true;
            }
            finally
            {
                if (resolved == null || !ReferenceEquals(resolved, candidate))
                {
                    DisposeCreatedSystemTypeface(candidate);
                }
            }
        }

        private static void DisposeCreatedSystemTypeface(SKTypeface typeface)
        {
            if (typeface == null || ReferenceEquals(typeface, SKTypeface.Default)) return;
            try
            {
                typeface.Dispose();
            }
            catch (System.Exception)
            {
            }
        }

        private static bool SupportsCharacters(SKTypeface tf, string text)
        {
            if (string.IsNullOrEmpty(text) || tf == null) return true;
            var requiresGlyphCheck = false;
            foreach (var c in text)
            {
                if (c <= 127) continue;
                requiresGlyphCheck = true;
                break;
            }

            if (!requiresGlyphCheck) return true;

            using var font = new SKFont(tf);
            foreach (var c in text)
            {
                if (!char.IsWhiteSpace(c) && font.GetGlyph(c) == 0) return false;
            }
            return true;
        }

        /// <summary>
        /// Wrap text into multiple lines based on available width
        /// Supports word-break: break-all (break anywhere), keep-all, break-word
        /// </summary>
        public static List<TextLine> WrapText(string text, SKFont font, float maxWidth, string whiteSpace, string hyphens = "none", string wordBreak = "normal")
        {
            var lines = new List<TextLine>();
            if (string.IsNullOrEmpty(text)) return lines;

            bool useHyphens = hyphens == "auto" || hyphens == "manual";
            bool breakAll = wordBreak == "break-all";

            bool preserveNewlines = whiteSpace == "pre" || whiteSpace == "pre-wrap" || whiteSpace == "pre-line";
            bool collapseSpaces = whiteSpace != "pre" && whiteSpace != "pre-wrap";

            if (collapseSpaces)
            {
                text = Regex.Replace(text, @"\s+", " ");
                text = text.Trim();
            }

            var paragraphs = preserveNewlines ? text.Split('\n') : new[] { text };

            foreach (var paragraph in paragraphs)
            {
                var words = paragraph.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (words.Length == 0)
                {
                    if (preserveNewlines) lines.Add(new TextLine { Text = "", Width = 0, Y = lines.Count });
                    continue;
                }

                string currentLine = "";
                float currentWidth = 0;

                foreach (var word in words)
                {
                    string testLine = string.IsNullOrEmpty(currentLine) ? word : currentLine + " " + word;
                    float testWidth = font.MeasureText(testLine);

                    if (testWidth <= maxWidth || string.IsNullOrEmpty(currentLine))
                    {
                        currentLine = testLine;
                        currentWidth = testWidth;
                    }
                    else
                    {
                        lines.Add(new TextLine { Text = currentLine, Width = currentWidth, Y = lines.Count });
                        currentLine = word;
                        currentWidth = font.MeasureText(word);

                        if (currentWidth > maxWidth)
                        {
                            var brokenLines = BreakLongWord(word, font, maxWidth, useHyphens);
                            for (int i = 0; i < brokenLines.Count - 1; i++)
                            {
                                lines.Add(new TextLine { Text = brokenLines[i].Text, Width = brokenLines[i].Width, Y = lines.Count });
                            }
                            if (brokenLines.Count > 0)
                            {
                                var last = brokenLines[brokenLines.Count - 1];
                                currentLine = last.Text;
                                currentWidth = last.Width;
                            }
                        }
                    }
                }

                if (!string.IsNullOrEmpty(currentLine))
                {
                    lines.Add(new TextLine { Text = currentLine, Width = currentWidth, Y = lines.Count });
                }
            }

            return lines;
        }

        /// <summary>
        /// Break a long word that exceeds maxWidth into multiple lines
        /// </summary>
        public static List<TextLine> BreakLongWord(string word, SKFont font, float maxWidth, bool useHyphens = false)
        {
            var lines = new List<TextLine>();
            string remaining = word;
            float hyphenWidth = useHyphens ? font.MeasureText("-") : 0;

            while (!string.IsNullOrEmpty(remaining))
            {
                int breakPoint = remaining.Length;
                float width = font.MeasureText(remaining);

                if (width <= maxWidth)
                {
                    lines.Add(new TextLine { Text = remaining, Width = width, Y = 0 });
                    break;
                }

                float effectiveMaxWidth = useHyphens ? maxWidth - hyphenWidth : maxWidth;
                int low = 1, high = remaining.Length;
                while (low < high)
                {
                    int mid = (low + high + 1) / 2;
                    width = font.MeasureText(remaining.Substring(0, mid));
                    if (width <= effectiveMaxWidth) low = mid;
                    else high = mid - 1;
                }

                if (low == 0) low = 1;

                var part = remaining.Substring(0, low);
                if (useHyphens && remaining.Length > low)
                {
                    part = part + "-";
                }
                lines.Add(new TextLine { Text = part, Width = font.MeasureText(part), Y = 0 });
                remaining = remaining.Substring(low);
            }

            return lines;
        }
    }
}
