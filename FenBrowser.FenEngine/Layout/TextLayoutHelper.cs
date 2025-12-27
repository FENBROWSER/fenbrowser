using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using SkiaSharp;
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
        /// <summary>
        /// Resolves the appropriate Typeface based on font-family, weight, slant and content.
        /// </summary>
        public static SKTypeface ResolveTypeface(string fontFamily, string text, int weight = 400, SKFontStyleSlant slant = SKFontStyleSlant.Upright)
        {
            // Default Sans-Serif fallback chain
            string[] sansSerifFallbacks = { "Segoe UI", "Arial", "Helvetica", "Roboto", "Open Sans" };
            
            // 1. Try FontRegistry (user-defined or already loaded)
            if (!string.IsNullOrEmpty(fontFamily))
            {
                var families = fontFamily.Split(',');
                foreach (var f in families)
                {
                    var clean = f.Trim().Trim('\'', '"');
                    
                    // Map generic font families to actual fonts
                    if (clean.Equals("sans-serif", StringComparison.OrdinalIgnoreCase))
                        clean = "Segoe UI";
                    else if (clean.Equals("serif", StringComparison.OrdinalIgnoreCase))
                        clean = "Georgia";
                    else if (clean.Equals("monospace", StringComparison.OrdinalIgnoreCase))
                        clean = "Consolas";
                    
                    var tf = FenBrowser.FenEngine.Rendering.FontRegistry.TryResolve(clean, weight, slant);
                    if (tf != null && SupportsCharacters(tf, text)) return tf;

                    // Fallback to Skia system font matching
                    var systemTf = SKTypeface.FromFamilyName(clean, (SKFontStyleWeight)weight, SKFontStyleWidth.Normal, slant);
                    if (systemTf != null && !string.IsNullOrEmpty(systemTf.FamilyName) && SupportsCharacters(systemTf, text)) 
                    {
                         return systemTf;
                    }
                }
            }

            // 2. Try Sans-Serif fallback chain first (before character matching)
            foreach (var fallbackFont in sansSerifFallbacks)
            {
                var tf = SKTypeface.FromFamilyName(fallbackFont, (SKFontStyleWeight)weight, SKFontStyleWidth.Normal, slant);
                if (tf != null && !string.IsNullOrEmpty(tf.FamilyName) && SupportsCharacters(tf, text))
                    return tf;
            }

            // 3. Character matching for non-Latin scripts (Indian languages, etc.)
            if (!string.IsNullOrEmpty(text))
            {
                foreach (var c in text)
                {
                    // If we find a non-ASCII character, it's a good candidate for specialized fallback
                    if (c > 255) 
                    {
                        var matched = SKFontManager.Default.MatchCharacter(c);
                        if (matched != null) 
                        {
                            FenBrowser.Core.FenLogger.Debug($"[FONT-MATCH] Resolved typeface for non-ASCII char '{c}' (0x{(int)c:X4}): {matched.FamilyName}");
                            return matched;
                        }
                    }
                }
            }

            // 4. Character matching - check ALL characters for best font match (important for symbols like ✓)
            if (!string.IsNullOrEmpty(text))
            {
                // Check for symbol characters that need special fonts
                foreach (var c in text)
                {
                    // Check if this is a symbol character that might need special font
                    if (c > 0x2000 && c < 0x3000) // Symbol ranges
                    {
                        var matched = SKFontManager.Default.MatchCharacter(c);
                        if (matched != null) return matched;
                    }
                }
                
                // Fallback character matching for other characters
                foreach (var c in text)
                {
                     if (!char.IsWhiteSpace(c))
                     {
                         var matched = SKFontManager.Default.MatchCharacter(c);
                         if (matched != null) return matched;
                         break;
                     }
                }
            }

            // 5. Ultimate Fallback
            return SKTypeface.FromFamilyName("Segoe UI") ?? SKTypeface.FromFamilyName("Arial") ?? SKTypeface.Default;
        }

        private static bool SupportsCharacters(SKTypeface tf, string text)
        {
            if (string.IsNullOrEmpty(text) || tf == null) return true;
            foreach (var c in text)
            {
                if (c > 127 && tf.GetGlyph(c) == 0) return false;
            }
            return true;
        }

        /// <summary>
        /// Wrap text into multiple lines based on available width
        /// Supports word-break: break-all (break anywhere), keep-all, break-word
        /// </summary>
        public static List<TextLine> WrapText(string text, SKPaint paint, float maxWidth, string whiteSpace, string hyphens = "none", string wordBreak = "normal")
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
                    float testWidth = paint.MeasureText(testLine);

                    if (testWidth <= maxWidth || string.IsNullOrEmpty(currentLine))
                    {
                        currentLine = testLine;
                        currentWidth = testWidth;
                    }
                    else
                    {
                        lines.Add(new TextLine { Text = currentLine, Width = currentWidth, Y = lines.Count });
                        currentLine = word;
                        currentWidth = paint.MeasureText(word);

                        if (currentWidth > maxWidth)
                        {
                            var brokenLines = BreakLongWord(word, paint, maxWidth, useHyphens);
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
        public static List<TextLine> BreakLongWord(string word, SKPaint paint, float maxWidth, bool useHyphens = false)
        {
            var lines = new List<TextLine>();
            string remaining = word;
            float hyphenWidth = useHyphens ? paint.MeasureText("-") : 0;

            while (!string.IsNullOrEmpty(remaining))
            {
                int breakPoint = remaining.Length;
                float width = paint.MeasureText(remaining);

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
                    width = paint.MeasureText(remaining.Substring(0, mid));
                    if (width <= effectiveMaxWidth) low = mid;
                    else high = mid - 1;
                }

                if (low == 0) low = 1;

                var part = remaining.Substring(0, low);
                if (useHyphens && remaining.Length > low)
                {
                    part = part + "-";
                }
                lines.Add(new TextLine { Text = part, Width = paint.MeasureText(part), Y = 0 });
                remaining = remaining.Substring(low);
            }

            return lines;
        }
    }
}
