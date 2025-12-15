using System;
using System.Collections.Generic;
using System.Text;

namespace FenBrowser.FenEngine.Rendering
{
    /// <summary>
    /// Implements the Unicode Bidirectional Algorithm (UBA) for proper text rendering
    /// of mixed LTR/RTL content (e.g., English with Hebrew, Arabic, etc.)
    /// 
    /// This is a simplified implementation covering the most common cases:
    /// - Base direction detection (from CSS or content)
    /// - Character-level directional classification
    /// - Reordering for display
    /// </summary>
    public static class BidiResolver
    {
        /// <summary>
        /// Bidi character types based on Unicode Bidi algorithm
        /// </summary>
        public enum BidiClass
        {
            L,   // Left-to-Right (Latin, ASCII)
            R,   // Right-to-Left (Hebrew, Arabic)
            AL,  // Arabic Letter
            EN,  // European Number
            ES,  // European Separator
            ET,  // European Terminator
            AN,  // Arabic Number
            CS,  // Common Separator
            NSM, // Non-Spacing Mark
            BN,  // Boundary Neutral
            B,   // Paragraph Separator
            S,   // Segment Separator
            WS,  // Whitespace
            ON,  // Other Neutral
            LRE, // Left-to-Right Embedding
            LRO, // Left-to-Right Override
            RLE, // Right-to-Left Embedding
            RLO, // Right-to-Left Override
            PDF, // Pop Directional Formatting
            LRI, // Left-to-Right Isolate
            RLI, // Right-to-Left Isolate
            FSI, // First Strong Isolate
            PDI  // Pop Directional Isolate
        }

        /// <summary>
        /// A run of text with consistent direction
        /// </summary>
        public class BidiRun
        {
            public string Text { get; set; }
            public int Start { get; set; }
            public int Length { get; set; }
            public int Level { get; set; } // Even = LTR, Odd = RTL
            public bool IsRtl => (Level & 1) == 1;
        }

        /// <summary>
        /// Resolve bidirectional text into runs ordered for visual display.
        /// </summary>
        /// <param name="text">The logical text string</param>
        /// <param name="baseDirection">Base direction from CSS (ltr/rtl)</param>
        /// <returns>Ordered runs for visual display</returns>
        public static List<BidiRun> Resolve(string text, string baseDirection = "ltr")
        {
            var runs = new List<BidiRun>();
            if (string.IsNullOrEmpty(text)) return runs;

            // Determine base embedding level
            int baseLevel = baseDirection?.ToLowerInvariant() == "rtl" ? 1 : 0;

            // Classify each character
            var types = new BidiClass[text.Length];
            var levels = new int[text.Length];
            
            for (int i = 0; i < text.Length; i++)
            {
                types[i] = GetBidiClass(text[i]);
                levels[i] = baseLevel;
            }

            // Simplified rule: Apply implicit resolution
            // X1-X9: Handle explicit embeddings (simplified - just track level)
            // W1-W7: Weak type resolution (simplified)
            // N1-N2: Neutral resolution
            
            ResolveWeakTypes(types, levels, baseLevel);
            ResolveNeutralTypes(types, levels, baseLevel);
            
            // Assign final levels based on resolved types
            for (int i = 0; i < text.Length; i++)
            {
                var type = types[i];
                if (type == BidiClass.R || type == BidiClass.AL)
                {
                    levels[i] = baseLevel | 1; // Make odd (RTL)
                }
                else if (type == BidiClass.L)
                {
                    levels[i] = baseLevel & ~1; // Make even (LTR)
                }
                else if (type == BidiClass.EN || type == BidiClass.AN)
                {
                    // Numbers maintain base level direction for positioning
                    // but the characters themselves are always displayed LTR
                    levels[i] = baseLevel;
                }
            }

            // L1: Reset trailing whitespace to base level
            for (int i = text.Length - 1; i >= 0; i--)
            {
                if (types[i] == BidiClass.WS || types[i] == BidiClass.S || types[i] == BidiClass.B)
                {
                    levels[i] = baseLevel;
                }
                else if (types[i] != BidiClass.NSM)
                {
                    break;
                }
            }

            // L2: Create runs from levels
            if (text.Length > 0)
            {
                int currentLevel = levels[0];
                int runStart = 0;

                for (int i = 1; i <= text.Length; i++)
                {
                    int level = (i < text.Length) ? levels[i] : -1;
                    
                    if (level != currentLevel)
                    {
                        runs.Add(new BidiRun
                        {
                            Text = text.Substring(runStart, i - runStart),
                            Start = runStart,
                            Length = i - runStart,
                            Level = currentLevel
                        });
                        runStart = i;
                        currentLevel = level;
                    }
                }
            }

            // L2: Reorder runs for display
            // Reverse runs at each level from highest to lowest
            int maxLevel = 0;
            foreach (var run in runs)
            {
                if (run.Level > maxLevel) maxLevel = run.Level;
            }

            for (int level = maxLevel; level > 0; level--)
            {
                int start = -1;
                for (int i = 0; i <= runs.Count; i++)
                {
                    bool atLevel = (i < runs.Count && runs[i].Level >= level);
                    
                    if (atLevel && start < 0)
                    {
                        start = i;
                    }
                    else if (!atLevel && start >= 0)
                    {
                        // Reverse runs[start..i-1]
                        int end = i - 1;
                        while (start < end)
                        {
                            var temp = runs[start];
                            runs[start] = runs[end];
                            runs[end] = temp;
                            start++;
                            end--;
                        }
                        start = -1;
                    }
                }
            }

            // Reverse characters within RTL runs
            foreach (var run in runs)
            {
                if (run.IsRtl)
                {
                    char[] chars = run.Text.ToCharArray();
                    Array.Reverse(chars);
                    run.Text = new string(chars);
                }
            }

            return runs;
        }

        /// <summary>
        /// Get the Bidi class of a character (simplified classification)
        /// </summary>
        private static BidiClass GetBidiClass(char c)
        {
            // ASCII control characters
            if (c < 0x0020) return BidiClass.BN;
            
            // ASCII letters and most punctuation are LTR
            if (c >= 0x0041 && c <= 0x005A) return BidiClass.L; // A-Z
            if (c >= 0x0061 && c <= 0x007A) return BidiClass.L; // a-z
            
            // Latin Extended-A/B and other European letters
            if (c >= 0x00C0 && c <= 0x024F) return BidiClass.L;
            
            // ASCII digits are European Numbers
            if (c >= 0x0030 && c <= 0x0039) return BidiClass.EN;
            
            // Spaces
            if (c == ' ' || c == '\t') return BidiClass.WS;
            
            // Line separators
            if (c == '\n' || c == '\r') return BidiClass.B;
            
            // Hebrew block
            if (c >= 0x0590 && c <= 0x05FF) return BidiClass.R;
            
            // Arabic block
            if (c >= 0x0600 && c <= 0x06FF) return BidiClass.AL;
            if (c >= 0x0750 && c <= 0x077F) return BidiClass.AL;
            if (c >= 0x08A0 && c <= 0x08FF) return BidiClass.AL;
            
            // Arabic Extended-A
            if (c >= 0x08A0 && c <= 0x08FF) return BidiClass.AL;
            
            // Arabic-Indic digits
            if (c >= 0x0660 && c <= 0x0669) return BidiClass.AN;
            if (c >= 0x06F0 && c <= 0x06F9) return BidiClass.EN; // Extended Arabic-Indic
            
            // Common punctuation
            if (c == '.' || c == ',' || c == ';' || c == ':') return BidiClass.CS;
            if (c == '+' || c == '-') return BidiClass.ES;
            if (c == '%' || c == '$' || c == '#') return BidiClass.ET;
            
            // Bidi control characters
            if (c == 0x200E) return BidiClass.L; // LRM
            if (c == 0x200F) return BidiClass.R; // RLM
            if (c == 0x202A) return BidiClass.LRE;
            if (c == 0x202B) return BidiClass.RLE;
            if (c == 0x202C) return BidiClass.PDF;
            if (c == 0x202D) return BidiClass.LRO;
            if (c == 0x202E) return BidiClass.RLO;
            if (c == 0x2066) return BidiClass.LRI;
            if (c == 0x2067) return BidiClass.RLI;
            if (c == 0x2068) return BidiClass.FSI;
            if (c == 0x2069) return BidiClass.PDI;
            
            // Default: Other Neutral
            return BidiClass.ON;
        }

        /// <summary>
        /// Simplified weak type resolution (W1-W7)
        /// </summary>
        private static void ResolveWeakTypes(BidiClass[] types, int[] levels, int baseLevel)
        {
            // W1: NSM adjacent to strong type takes that type
            for (int i = 0; i < types.Length; i++)
            {
                if (types[i] == BidiClass.NSM)
                {
                    if (i > 0)
                        types[i] = types[i - 1];
                    else
                        types[i] = (baseLevel & 1) == 1 ? BidiClass.R : BidiClass.L;
                }
            }

            // W2: EN after AL becomes AN
            BidiClass lastStrong = (baseLevel & 1) == 1 ? BidiClass.R : BidiClass.L;
            for (int i = 0; i < types.Length; i++)
            {
                if (types[i] == BidiClass.R || types[i] == BidiClass.L || types[i] == BidiClass.AL)
                {
                    lastStrong = types[i];
                }
                else if (types[i] == BidiClass.EN && lastStrong == BidiClass.AL)
                {
                    types[i] = BidiClass.AN;
                }
            }

            // W3: AL -> R
            for (int i = 0; i < types.Length; i++)
            {
                if (types[i] == BidiClass.AL)
                    types[i] = BidiClass.R;
            }

            // W4: Single ES/CS between EN/EN or AN/AN takes that number type
            for (int i = 1; i < types.Length - 1; i++)
            {
                if ((types[i] == BidiClass.ES || types[i] == BidiClass.CS) &&
                    types[i - 1] == types[i + 1] &&
                    (types[i - 1] == BidiClass.EN || types[i - 1] == BidiClass.AN))
                {
                    types[i] = types[i - 1];
                }
            }

            // W5: ET adjacent to EN becomes EN
            for (int i = 0; i < types.Length; i++)
            {
                if (types[i] == BidiClass.ET)
                {
                    if ((i > 0 && types[i - 1] == BidiClass.EN) ||
                        (i < types.Length - 1 && types[i + 1] == BidiClass.EN))
                    {
                        types[i] = BidiClass.EN;
                    }
                }
            }

            // W6: Remaining separators and terminators become ON
            for (int i = 0; i < types.Length; i++)
            {
                if (types[i] == BidiClass.ES || types[i] == BidiClass.ET || 
                    types[i] == BidiClass.CS)
                {
                    types[i] = BidiClass.ON;
                }
            }

            // W7: EN after L becomes L
            lastStrong = (baseLevel & 1) == 1 ? BidiClass.R : BidiClass.L;
            for (int i = 0; i < types.Length; i++)
            {
                if (types[i] == BidiClass.R || types[i] == BidiClass.L)
                {
                    lastStrong = types[i];
                }
                else if (types[i] == BidiClass.EN && lastStrong == BidiClass.L)
                {
                    types[i] = BidiClass.L;
                }
            }
        }

        /// <summary>
        /// Simplified neutral type resolution (N1-N2)
        /// </summary>
        private static void ResolveNeutralTypes(BidiClass[] types, int[] levels, int baseLevel)
        {
            // N1: Neutrals between strong types of same direction take that direction
            // N2: Remaining neutrals take embedding direction
            
            for (int i = 0; i < types.Length; i++)
            {
                if (IsNeutral(types[i]))
                {
                    // Find preceding and following strong types
                    BidiClass before = (baseLevel & 1) == 1 ? BidiClass.R : BidiClass.L;
                    BidiClass after = (baseLevel & 1) == 1 ? BidiClass.R : BidiClass.L;
                    
                    for (int j = i - 1; j >= 0; j--)
                    {
                        if (types[j] == BidiClass.L || types[j] == BidiClass.R)
                        {
                            before = types[j];
                            break;
                        }
                    }
                    
                    for (int j = i + 1; j < types.Length; j++)
                    {
                        if (types[j] == BidiClass.L || types[j] == BidiClass.R)
                        {
                            after = types[j];
                            break;
                        }
                    }
                    
                    // N1: If surrounded by same direction, take that direction
                    if (before == after)
                    {
                        types[i] = before;
                    }
                    else
                    {
                        // N2: Take embedding direction
                        types[i] = (baseLevel & 1) == 1 ? BidiClass.R : BidiClass.L;
                    }
                }
            }
        }

        /// <summary>
        /// Check if a bidi class is neutral
        /// </summary>
        private static bool IsNeutral(BidiClass c)
        {
            return c == BidiClass.WS || c == BidiClass.ON || c == BidiClass.S || 
                   c == BidiClass.B || c == BidiClass.BN;
        }

        /// <summary>
        /// Detect the dominant text direction from content
        /// </summary>
        public static string DetectDirection(string text)
        {
            if (string.IsNullOrEmpty(text)) return "ltr";

            int ltrCount = 0;
            int rtlCount = 0;

            foreach (char c in text)
            {
                var bidiClass = GetBidiClass(c);
                if (bidiClass == BidiClass.L) ltrCount++;
                else if (bidiClass == BidiClass.R || bidiClass == BidiClass.AL) rtlCount++;
            }

            // Return dominant direction
            return rtlCount > ltrCount ? "rtl" : "ltr";
        }
    }
}
