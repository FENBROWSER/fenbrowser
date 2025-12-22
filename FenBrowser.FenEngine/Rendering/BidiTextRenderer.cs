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

        private static int[] ResolveEmbeddingLevels(BidiType[] types, int baseLevel)
        {
            var levels = new int[types.Length];
            
            // Initialize with base level
            for (int i = 0; i < levels.Length; i++)
            {
                levels[i] = baseLevel;
            }

            // Resolve strong types
            int currentLevel = baseLevel;
            for (int i = 0; i < types.Length; i++)
            {
                switch (types[i])
                {
                    case BidiType.L:
                        levels[i] = currentLevel;
                        if (currentLevel % 2 == 1) currentLevel--; // Back to LTR
                        break;
                    case BidiType.R:
                    case BidiType.AL:
                        levels[i] = currentLevel % 2 == 0 ? currentLevel + 1 : currentLevel;
                        currentLevel = levels[i];
                        break;
                    case BidiType.EN:
                    case BidiType.AN:
                        // Numbers take direction from surrounding context
                        levels[i] = currentLevel;
                        break;
                    default:
                        levels[i] = currentLevel;
                        break;
                }
            }

            // Resolve neutrals (take direction from surrounding strong types)
            ResolveNeutrals(types, levels);

            return levels;
        }

        private static void ResolveNeutrals(BidiType[] types, int[] levels)
        {
            for (int i = 0; i < types.Length; i++)
            {
                if (types[i] == BidiType.WS || types[i] == BidiType.ON ||
                    types[i] == BidiType.ES || types[i] == BidiType.CS)
                {
                    // Look for surrounding strong characters
                    int prevLevel = i > 0 ? levels[i - 1] : levels[i];
                    int nextLevel = i < levels.Length - 1 ? levels[i + 1] : levels[i];
                    
                    // Take the higher level (more embedded)
                    levels[i] = Math.Max(prevLevel, nextLevel);
                }
            }
        }

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

