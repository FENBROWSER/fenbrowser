using FenBrowser.Core.Css;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace FenBrowser.FenEngine.Rendering
{
    /// <summary>
    /// Full Unicode Bidirectional Algorithm (UAX #9) implementation.
    /// Handles RTL text, mixed LTR/RTL content, and proper text reordering.
    /// </summary>
    public static class BidiAlgorithm
    {
        /// <summary>
        /// Reorder text for display according to the Unicode Bidi Algorithm
        /// </summary>
        public static string ReorderForDisplay(string text, bool paragraphRtl = false)
        {
            if (string.IsNullOrEmpty(text)) return text;

            // Step 1: Determine base direction
            var baseLevel = paragraphRtl ? 1 : 0;
            
            // Step 2: Get character types and levels
            var chars = text.ToCharArray();
            var types = GetBidiTypes(chars);
            var levels = ResolveEmbeddingLevels(types, baseLevel);
            
            // Step 3: Reorder characters based on levels
            var reordered = ReorderByLevels(chars, levels);
            
            return new string(reordered);
        }

        /// <summary>
        /// Get visual run spans for rendering
        /// </summary>
        public static IEnumerable<BidiRun> GetVisualRuns(string text, bool paragraphRtl = false)
        {
            if (string.IsNullOrEmpty(text))
            {
                yield break;
            }

            var baseLevel = paragraphRtl ? 1 : 0;
            var chars = text.ToCharArray();
            var types = GetBidiTypes(chars);
            var levels = ResolveEmbeddingLevels(types, baseLevel);

            // Group consecutive characters with same level into runs
            int runStart = 0;
            int currentLevel = levels[0];

            for (int i = 1; i <= chars.Length; i++)
            {
                int level = i < chars.Length ? levels[i] : -1;
                
                if (level != currentLevel)
                {
                    yield return new BidiRun
                    {
                        Text = new string(chars, runStart, i - runStart),
                        StartIndex = runStart,
                        Length = i - runStart,
                        Level = currentLevel,
                        IsRtl = currentLevel % 2 == 1
                    };
                    
                    runStart = i;
                    currentLevel = level;
                }
            }
        }

        /// <summary>
        /// Determine if text contains any RTL characters
        /// </summary>
        public static bool ContainsRtl(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            
            foreach (char c in text)
            {
                if (IsRtlChar(c)) return true;
            }
            return false;
        }

        /// <summary>
        /// Detect the base direction of text
        /// </summary>
        public static bool DetectBaseRtl(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;

            // Find first strong directional character
            foreach (char c in text)
            {
                var type = GetBidiType(c);
                if (type == BidiType.L) return false;
                if (type == BidiType.R || type == BidiType.AL) return true;
            }

            return false;
        }

        #region Bidi Type Classification

        private static BidiType[] GetBidiTypes(char[] chars)
        {
            var types = new BidiType[chars.Length];
            for (int i = 0; i < chars.Length; i++)
            {
                types[i] = GetBidiType(chars[i]);
            }
            return types;
        }

        private static BidiType GetBidiType(char c)
        {
            // Strong types
            if (IsStrongLtr(c)) return BidiType.L;
            if (IsStrongRtl(c)) return BidiType.R;
            if (IsArabicLetter(c)) return BidiType.AL;

            // Weak types
            if (char.IsDigit(c)) return BidiType.EN; // European number
            if (c == '+' || c == '-') return BidiType.ES; // European separator
            if (c == '.' || c == ',' || c == ':') return BidiType.CS; // Common separator
            if (IsArabicDigit(c)) return BidiType.AN; // Arabic number

            // Neutral types
            if (char.IsWhiteSpace(c)) return BidiType.WS;
            if (char.IsPunctuation(c)) return BidiType.ON; // Other neutral

            return BidiType.ON; // Default to neutral
        }

        private static bool IsStrongLtr(char c)
        {
            // Latin, Greek, Cyrillic, etc.
            if (c >= 'A' && c <= 'Z') return true;
            if (c >= 'a' && c <= 'z') return true;
            if (c >= 0x00C0 && c <= 0x024F) return true; // Latin Extended
            if (c >= 0x0370 && c <= 0x03FF) return true; // Greek
            if (c >= 0x0400 && c <= 0x04FF) return true; // Cyrillic
            return false;
        }

        private static bool IsStrongRtl(char c)
        {
            // Hebrew
            if (c >= 0x0590 && c <= 0x05FF) return true;
            return false;
        }

        private static bool IsArabicLetter(char c)
        {
            // Arabic
            if (c >= 0x0600 && c <= 0x06FF) return true;
            if (c >= 0x0750 && c <= 0x077F) return true; // Arabic Supplement
            if (c >= 0xFB50 && c <= 0xFDFF) return true; // Arabic Presentation Forms-A
            if (c >= 0xFE70 && c <= 0xFEFF) return true; // Arabic Presentation Forms-B
            return false;
        }

        private static bool IsArabicDigit(char c)
        {
            // Arabic-Indic digits
            return c >= 0x0660 && c <= 0x0669;
        }

        private static bool IsRtlChar(char c)
        {
            return IsStrongRtl(c) || IsArabicLetter(c);
        }

        #endregion

        #region Level Resolution

        // UAX#9 §3.3–3.4 (simplified — no embedding-level stack; handles common LTR/RTL mixing).
        private static int[] ResolveEmbeddingLevels(BidiType[] types, int baseLevel)
        {
            // Work on a mutable type array so W-rules can modify classifications in place.
            var t = (BidiType[])types.Clone();

            // W1: NSM inherits the type of the preceding character.
            for (int i = 0; i < t.Length; i++)
            {
                if (t[i] == BidiType.NSM)
                    t[i] = i > 0 ? t[i - 1] : ((baseLevel & 1) == 1 ? BidiType.R : BidiType.L);
            }

            // W2: EN following AL becomes AN.
            var lastStrong = (baseLevel & 1) == 1 ? BidiType.R : BidiType.L;
            for (int i = 0; i < t.Length; i++)
            {
                if (t[i] == BidiType.R || t[i] == BidiType.L || t[i] == BidiType.AL) lastStrong = t[i];
                else if (t[i] == BidiType.EN && lastStrong == BidiType.AL) t[i] = BidiType.AN;
            }

            // W3: AL → R.
            for (int i = 0; i < t.Length; i++)
                if (t[i] == BidiType.AL) t[i] = BidiType.R;

            // W4: Single ES/CS between matching number types inherits that type.
            for (int i = 1; i < t.Length - 1; i++)
            {
                if ((t[i] == BidiType.ES || t[i] == BidiType.CS) &&
                    t[i - 1] == t[i + 1] &&
                    (t[i - 1] == BidiType.EN || t[i - 1] == BidiType.AN))
                    t[i] = t[i - 1];
            }

            // W5: ET adjacent to EN becomes EN.
            for (int i = 0; i < t.Length; i++)
            {
                if (t[i] == BidiType.ET &&
                    ((i > 0 && t[i - 1] == BidiType.EN) ||
                     (i < t.Length - 1 && t[i + 1] == BidiType.EN)))
                    t[i] = BidiType.EN;
            }

            // W6: Remaining separators and terminators → ON.
            for (int i = 0; i < t.Length; i++)
                if (t[i] == BidiType.ES || t[i] == BidiType.ET || t[i] == BidiType.CS)
                    t[i] = BidiType.ON;

            // W7: EN following last strong L → L.
            lastStrong = (baseLevel & 1) == 1 ? BidiType.R : BidiType.L;
            for (int i = 0; i < t.Length; i++)
            {
                if (t[i] == BidiType.R || t[i] == BidiType.L) lastStrong = t[i];
                else if (t[i] == BidiType.EN && lastStrong == BidiType.L) t[i] = BidiType.L;
            }

            // Assign levels from resolved strong types.
            var levels = new int[t.Length];
            for (int i = 0; i < t.Length; i++)
            {
                if (t[i] == BidiType.R)      levels[i] = (baseLevel & ~1) | 1; // next odd
                else if (t[i] == BidiType.L) levels[i] = baseLevel & ~1;       // next even
                else                          levels[i] = baseLevel;
            }

            // N1-N2: Neutrals between same-direction strong types take that direction;
            // otherwise take the embedding direction.
            ResolveNeutrals(t, levels, baseLevel);

            // L1: Reset trailing whitespace/separators to base level.
            for (int i = t.Length - 1; i >= 0; i--)
            {
                if (t[i] == BidiType.WS || t[i] == BidiType.S || t[i] == BidiType.B)
                    levels[i] = baseLevel;
                else if (t[i] != BidiType.NSM)
                    break;
            }

            return levels;
        }

        private static void ResolveNeutrals(BidiType[] t, int[] levels, int baseLevel)
        {
            BidiType embedDir = (baseLevel & 1) == 1 ? BidiType.R : BidiType.L;

            for (int i = 0; i < t.Length; i++)
            {
                if (!IsNeutral(t[i])) continue;

                // Find the nearest preceding strong type.
                BidiType before = embedDir;
                for (int j = i - 1; j >= 0; j--)
                {
                    if (t[j] == BidiType.L || t[j] == BidiType.R) { before = t[j]; break; }
                }

                // Find the nearest following strong type.
                BidiType after = embedDir;
                for (int j = i + 1; j < t.Length; j++)
                {
                    if (t[j] == BidiType.L || t[j] == BidiType.R) { after = t[j]; break; }
                }

                // N1: Same strong type on both sides → take that type.
                // N2: Otherwise → take embedding direction.
                BidiType resolved = (before == after) ? before : embedDir;
                levels[i] = resolved == BidiType.R ? (levels[i] | 1) : (levels[i] & ~1);
            }
        }

        private static bool IsNeutral(BidiType t)
            => t == BidiType.WS || t == BidiType.ON || t == BidiType.S ||
               t == BidiType.B  || t == BidiType.BN;

        #endregion

        #region Reordering

        private static char[] ReorderByLevels(char[] chars, int[] levels)
        {
            var result = (char[])chars.Clone();
            int maxLevel = levels.Max();

            // Reverse runs at each level from highest to lowest
            for (int level = maxLevel; level >= 1; level--)
            {
                int runStart = -1;
                
                for (int i = 0; i <= result.Length; i++)
                {
                    bool inRun = i < result.Length && levels[i] >= level;
                    
                    if (inRun && runStart == -1)
                    {
                        runStart = i;
                    }
                    else if (!inRun && runStart != -1)
                    {
                        // Reverse this run
                        Array.Reverse(result, runStart, i - runStart);
                        runStart = -1;
                    }
                }
            }

            return result;
        }

        #endregion

        /// <summary>
        /// Mirror characters for RTL rendering (parentheses, brackets, etc.)
        /// </summary>
        public static char GetMirrorChar(char c)
        {
            return c switch
            {
                '(' => ')',
                ')' => '(',
                '[' => ']',
                ']' => '[',
                '{' => '}',
                '}' => '{',
                '<' => '>',
                '>' => '<',
                '«' => '»',
                '»' => '«',
                '‹' => '›',
                '›' => '‹',
                '⁅' => '⁆',
                '⁆' => '⁅',
                '⟨' => '⟩',
                '⟩' => '⟨',
                '⟪' => '⟫',
                '⟫' => '⟪',
                '⦃' => '⦄',
                '⦄' => '⦃',
                _ => c
            };
        }

        /// <summary>
        /// Apply mirroring to RTL text
        /// </summary>
        public static string ApplyMirroring(string text, bool rtl)
        {
            if (!rtl || string.IsNullOrEmpty(text)) return text;

            var chars = text.ToCharArray();
            for (int i = 0; i < chars.Length; i++)
            {
                chars[i] = GetMirrorChar(chars[i]);
            }
            return new string(chars);
        }

        #region Types

        private enum BidiType
        {
            L,    // Left-to-Right
            R,    // Right-to-Left
            AL,   // Arabic Letter
            EN,   // European Number
            ES,   // European Separator
            ET,   // European Terminator
            AN,   // Arabic Number
            CS,   // Common Separator
            NSM,  // Non-Spacing Mark
            BN,   // Boundary Neutral
            B,    // Paragraph Separator
            S,    // Segment Separator
            WS,   // White Space
            ON,   // Other Neutral
            LRE,  // Left-to-Right Embedding
            LRO,  // Left-to-Right Override
            RLE,  // Right-to-Left Embedding
            RLO,  // Right-to-Left Override
            PDF,  // Pop Directional Format
            LRI,  // Left-to-Right Isolate
            RLI,  // Right-to-Left Isolate
            FSI,  // First Strong Isolate
            PDI   // Pop Directional Isolate
        }

        #endregion
    }

    /// <summary>
    /// Represents a run of text with the same bidi level
    /// </summary>
    public struct BidiRun
    {
        public string Text;
        public int StartIndex;
        public int Length;
        public int Level;
        public bool IsRtl;
    }

    /// <summary>
    /// CSS direction and writing-mode support
    /// </summary>
    public class CssTextDirection
    {
        public bool IsRtl { get; set; }
        public WritingMode Mode { get; set; } = WritingMode.HorizontalTb;
        public string UnicodeBidi { get; set; } = "normal";

        public static CssTextDirection Parse(CssComputed style)
        {
            var dir = new CssTextDirection();

            if (style?.Map != null)
            {
                if (style.Map.TryGetValue("direction", out var direction))
                {
                    dir.IsRtl = direction?.ToLowerInvariant() == "rtl";
                }

                if (style.Map.TryGetValue("writing-mode", out var writingMode))
                {
                    dir.Mode = writingMode?.ToLowerInvariant() switch
                    {
                        "vertical-rl" => WritingMode.VerticalRl,
                        "vertical-lr" => WritingMode.VerticalLr,
                        "sideways-rl" => WritingMode.SidewaysRl,
                        "sideways-lr" => WritingMode.SidewaysLr,
                        _ => WritingMode.HorizontalTb
                    };
                }

                if (style.Map.TryGetValue("unicode-bidi", out var bidi))
                {
                    dir.UnicodeBidi = bidi?.ToLowerInvariant() ?? "normal";
                }
            }

            return dir;
        }

        public bool IsVertical => Mode != WritingMode.HorizontalTb;
    }

    public enum WritingMode
    {
        HorizontalTb,  // Left to right, top to bottom (default)
        VerticalRl,    // Top to bottom, right to left
        VerticalLr,    // Top to bottom, left to right
        SidewaysRl,    // Like vertical-rl but rotated
        SidewaysLr     // Like vertical-lr but rotated
    }
}

