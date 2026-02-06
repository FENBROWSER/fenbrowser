using System;
using System.Collections.Generic;
using SkiaSharp;
using FenBrowser.Core.Css;
using FenBrowser.Core.Dom.V2;
using FenBrowser.Core;

namespace FenBrowser.FenEngine.Layout
{
    /// <summary>
    /// Handles text measurement, word wrapping, and line breaking.
    /// </summary>
    public static class TextLayoutComputer
    {
        private const float DefaultFontSize = 16f;

        public static (LayoutMetrics Metrics, List<ComputedTextLine> Lines) ComputeTextLayout(
            Node node, 
            CssComputed style, 
            SKSize availableSize, 
            float viewportWidth)
        {
            if (!(node is Text textNode) || string.IsNullOrEmpty(textNode.Data))
                return (new LayoutMetrics(), new List<ComputedTextLine>());

            // DEBUG: Trace text input - FORCE VISIBILITY
            if (textNode.Data != null && textNode.Data.IndexOf("Item", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                FenBrowser.Core.FenLogger.Info($"[TEXT-LAYOUT] Measuring '{textNode.Data.Replace("\n","\\n")}' AvailW={availableSize.Width} WS={style?.WhiteSpace} Font={style?.FontFamilyName}", FenBrowser.Core.Logging.LogCategory.Layout);
            }

            // 1. Setup Paint
            using var paint = new SKPaint();
            paint.TextSize = (float)(style?.FontSize ?? DefaultFontSize);
            paint.Typeface = TextLayoutHelper.ResolveTypeface(style?.FontFamilyName, textNode.Data, style?.FontWeight ?? 400, (style?.FontStyle == SKFontStyleSlant.Italic) ? SKFontStyleSlant.Italic : SKFontStyleSlant.Upright);
            paint.IsAntialias = true;

            var fm = paint.FontMetrics;
            float fontHeight = fm.Descent - fm.Ascent;
            
            // Standard browser "normal" line-height is typically around 1.2 * fontSize
            // Skia's fontHeight (Descent - Ascent) can be significantly larger for some fonts, 
            // causing excessive spacing. We enforce 1.2em as default to match Chrome/Edge.
            float fontSize = paint.TextSize;
            float lineHeight = fontSize * 1.2f; 

            if (style?.LineHeight.HasValue == true)
            {
                // If LineHeight is a multiplier (e.g. 1.5), multiply by fontSize (not fontHeight, to be consistent)
                // Or standard CSS says multiplier is relative to font-size.
                // But existing logic seemingly treated it relative to itself?
                // If value < 5.0, assume multiplier.
                lineHeight = (float)(style.LineHeight.Value < 5.0 ? style.LineHeight.Value * fontSize : style.LineHeight.Value);
            }

            // Recalculate leading to center the text content within the line box
            float leading = (lineHeight - fontHeight) / 2;
            float baselineOffset = -fm.Ascent + leading;

            // 2. Resolve White-Space Mode
            string ws = style?.WhiteSpace?.ToLowerInvariant() ?? "normal";
            bool collapseWhitespace = (ws == "normal" || ws == "nowrap" || ws == "pre-line");
            bool preserveNewlines = (ws == "pre" || ws == "pre-wrap" || ws == "pre-line");
            bool allowWrap = (ws == "normal" || ws == "pre-wrap" || ws == "pre-line");

            // 3. Tokenize
            var tokens = Tokenize(textNode.Data, collapseWhitespace, preserveNewlines);

            float maxLineWidth = availableSize.Width;
            if (float.IsInfinity(maxLineWidth)) maxLineWidth = 1000000;

            var lines = new List<ComputedTextLine>();
            float currentY = 0;
            
            var currentLineTokens = new List<TextToken>();
            float currentWidth = 0;
            
            float spaceWidth = paint.MeasureText(" ");

            void FlushLine()
            {
                if (currentLineTokens.Count == 0 && lines.Count > 0) return; // Don't flush empty unless it's a forced empty line
                
                // Construct string
                var sb = new System.Text.StringBuilder();
                for(int i=0; i<currentLineTokens.Count; i++)
                {
                    sb.Append(currentLineTokens[i].Text);
                }
                
                lines.Add(new ComputedTextLine
                {
                    Text = sb.ToString(),
                    Width = currentWidth,
                    Height = lineHeight,
                    Origin = new SKPoint(0, currentY),
                    Baseline = baselineOffset
                });
                
                currentLineTokens.Clear();
                currentWidth = 0;
                currentY += lineHeight;
            }

            foreach (var token in tokens)
            {
                if (token.IsNewline)
                {
                    // Forces a line break
                    FlushLine();
                    continue;
                }

                float tokenWidth;
                if (token.IsWhitespace && collapseWhitespace)
                {
                     // Collapsed whitespace is usually a single space
                     tokenWidth = spaceWidth;
                }
                else
                {
                    tokenWidth = paint.MeasureText(token.Text);
                }

                // Check Wrap
                bool shouldWrap = allowWrap && (currentWidth + tokenWidth > maxLineWidth) && currentLineTokens.Count > 0;
                
                if (shouldWrap)
                {
                    System.Console.WriteLine($"[TEXT-WRAP] Wrapping '{token.Text}' CurW={currentWidth} TokW={tokenWidth} MaxW={maxLineWidth}");
                    // If current token is a space and we are at EOL, we might ignore it or wrap it to next line (which becomes invisible trailing space)
                    // Standard: if wrap happens at space, space spills or disappears.
                    // Simplified: Flush then add.
                    FlushLine();
                }
                
                // Add Token
                // Special handling for collapsed whitespace token -> ensure it renders as " "
                if (collapseWhitespace && token.IsWhitespace)
                {
                    // If line is empty, leading whitespace might be ignored (in normal flow logic, strictly speaking yes, but simplified here)
                    // Also consecutive spaces are already collapsed by Tokenizer if collapseWhitespace=true?
                    // Actually let's assume Tokenizer handles collapsing contiguous spaces into one token.
                    currentLineTokens.Add(new TextToken { Text = " ", IsWhitespace = true });
                    currentWidth += spaceWidth;
                }
                else
                {
                    currentLineTokens.Add(token);
                    currentWidth += tokenWidth;
                }
            }
            
            // Flush last line
            if (currentLineTokens.Count > 0)
            {
                FlushLine();
            }

            // 4. Compute Metrics
            float finalWidth = 0;
            foreach (var line in lines) finalWidth = Math.Max(finalWidth, line.Width);

            // Alignment
            if (style?.TextAlign == SKTextAlign.Center || style?.TextAlign == SKTextAlign.Right)
            {
                for (int i = 0; i < lines.Count; i++)
                {
                    var line = lines[i];
                    float remaining = maxLineWidth - line.Width;
                    if (remaining > 0)
                    {
                        if (style.TextAlign == SKTextAlign.Center) line.Origin.X += remaining / 2;
                        else if (style.TextAlign == SKTextAlign.Right) line.Origin.X += remaining;
                        lines[i] = line;
                    }
                }
            }

            return (new LayoutMetrics 
            { 
                ContentHeight = currentY, 
                MaxChildWidth = finalWidth, 
                Baseline = baselineOffset 
            }, lines);
        }

        private struct TextToken
        {
            public string Text;
            public bool IsWhitespace;
            public bool IsNewline;
        }

        private static int _debugLogCount = 0;
        private static List<TextToken> Tokenize(string text, bool collapseWhitespace, bool preserveNewlines)
        {
             // DEBUG trace - UNCONDITIONAL
             if (_debugLogCount < 20) 
             {
                 _debugLogCount++;
                 System.Console.WriteLine($"[TOKENIZE-ALL] '{text.Replace("\n","\\n")}' Len={text.Length} Collapse={collapseWhitespace}");
             }
             
             var tokens = new List<TextToken>();
            if (string.IsNullOrEmpty(text)) return tokens;

            int i = 0;
            while (i < text.Length)
            {
                char c = text[i];
                
                if (c == '\r' || c == '\n')
                {
                    if (preserveNewlines)
                    {
                        tokens.Add(new TextToken { Text = "\n", IsNewline = true });
                    }
                    else if (collapseWhitespace)
                    {
                        // Treat as space (if previous wasn't space, or just loop to next char to merge)
                        // Look ahead to merge with spaces?
                        // Simplified: Treat as space.
                        // But we want to merge runs of whitespace+newlines into single space.
                    }
                    
                     // Consume sequence of newlines/whitespace if collapsing
                    if (collapseWhitespace)
                    {
                         // Check previous token. If space, ignore this. Else add space.
                         // FIX: Do not add space if this is the start of the text (strip leading whitespace)
                         if (tokens.Count > 0)
                         {
                             var last = tokens[tokens.Count - 1];
                             if (!last.IsWhitespace) 
                             {
                                 tokens.Add(new TextToken { Text = " ", IsWhitespace = true });
                             }
                         }
                    }
                    i++;
                }
                else if (char.IsWhiteSpace(c))
                {
                    // Tab or Space
                    // Tab or Space
                    if (collapseWhitespace)
                    {
                        // FIX: Do not add space if this is the start of the text (strip leading whitespace)
                        if (tokens.Count > 0)
                        {
                            var last = tokens[tokens.Count - 1];
                            if (!last.IsWhitespace)
                                tokens.Add(new TextToken { Text = " ", IsWhitespace = true });
                        }
                        i++;
                    }
                    else
                    {
                        // Preserve exact whitespace char
                        tokens.Add(new TextToken { Text = c.ToString(), IsWhitespace = true });
                        i++;
                    }
                }
                else
                {
                    // Word boundary
                    int start = i;
                    while (i < text.Length && !char.IsWhiteSpace(text[i]) && text[i] != '\r' && text[i] != '\n')
                    {
                        i++;
                    }
                    string word = text.Substring(start, i - start);
                    tokens.Add(new TextToken { Text = word, IsWhitespace = false });
                }
            }

            
            // FIX: Strip trailing whitespace (HTML spec: sequence of white space at end of block is removed)
            if (collapseWhitespace)
            {
                while (tokens.Count > 0 && tokens[tokens.Count - 1].IsWhitespace)
                {
                    tokens.RemoveAt(tokens.Count - 1);
                }
            }

            return tokens;
        }
    }
}

