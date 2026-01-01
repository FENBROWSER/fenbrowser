using System;
using System.Collections.Generic;
using SkiaSharp;
using FenBrowser.Core.Css;
using FenBrowser.Core.Dom;
using FenBrowser.Core;

namespace FenBrowser.FenEngine.Layout
{
    /// <summary>
    /// Handles text measurement, word wrapping, and line breaking.
    /// </summary>
    public static class TextLayoutComputer
    {
        private const float DefaultFontSize = 16f;

        /// <summary>
        /// Computes the layout for a text node, wrapping lines as needed.
        /// Returns the metrics (Width, Height) and populates the BoxModel's Lines property if provided boxes are mutable.
        /// But since BoxModel is created by caller, we return a list of lines.
        /// </summary>
        public static (LayoutMetrics Metrics, List<ComputedTextLine> Lines) ComputeTextLayout(
            Node node, 
            CssComputed style, 
            SKSize availableSize, 
            float viewportWidth)
        {
            if (!(node is Text textNode) || string.IsNullOrWhiteSpace(textNode.Data))
                return (new LayoutMetrics(), new List<ComputedTextLine>());

            // 1. Setup Paint
            using var paint = new SKPaint();
            paint.TextSize = (float)(style?.FontSize ?? DefaultFontSize);
            // Use TextLayoutHelper from existing code or resolve manually
            paint.Typeface = TextLayoutHelper.ResolveTypeface(style?.FontFamilyName, textNode.Data, style?.FontWeight ?? 400, (style?.FontStyle == SKFontStyleSlant.Italic) ? SKFontStyleSlant.Italic : SKFontStyleSlant.Upright);
            paint.IsAntialias = true;

            var fm = paint.FontMetrics;
            float fontHeight = fm.Descent - fm.Ascent;
            float lineHeight = fontHeight; // Natural line height
            // Apply line-height CSS if present
            if (style?.LineHeight.HasValue == true)
            {
                if (style.LineHeight.Value < 5.0)
                    lineHeight *= (float)style.LineHeight.Value;
                else
                    lineHeight = (float)style.LineHeight.Value;
            }

            // FIX: Implement "Half-Leading" vertical distribution
            // This centers the text within the line-height.
            float leading = (lineHeight - fontHeight) / 2;
            float baselineOffset = -fm.Ascent + leading;

            string text = textNode.Data;
            // Collapse whitespace (basic standard text handling)
            // Note: 'white-space: pre' would skip this. Assuming 'normal' for now.
            var words = SplitIntoWords(text);
            
            float maxLineWidth = availableSize.Width;
            if (float.IsInfinity(maxLineWidth)) maxLineWidth = viewportWidth; // Fallback to viewport if infinite

            var lines = new List<ComputedTextLine>();
            float currentY = 0;
            
            // 2. Greedy Line Breaking
            // Accumulate words into lines
            var currentLineWords = new List<string>();
            float currentWidth = 0;
            float spaceWidth = paint.MeasureText(" ");

            foreach (var word in words)
            {
                float wordWidth = paint.MeasureText(word);
                // DIAGNOSTIC: Trace text width
                if (word.StartsWith("About") || word.StartsWith("Store"))
                {
                     /* [PERF-REMOVED] */
                }
                
                // If adding this word exceeds width (and it's not the first word), break line
                if (currentLineWords.Count > 0 && (currentWidth + spaceWidth + wordWidth) > maxLineWidth)
                {
                    // Flush current line
                    lines.Add(CreateLine(currentLineWords, currentWidth, currentY, lineHeight, baselineOffset));
                    
                    // Reset for new line
                    currentLineWords.Clear();
                    currentLineWords.Add(word);
                    currentWidth = wordWidth;
                    currentY += lineHeight;
                }
                else
                {
                    if (currentLineWords.Count > 0) currentWidth += spaceWidth;
                    currentLineWords.Add(word);
                    currentWidth += wordWidth;
                }
            }
            
            // Flush last line
            if (currentLineWords.Count > 0)
            {
                lines.Add(CreateLine(currentLineWords, currentWidth, currentY, lineHeight, baselineOffset));
                currentY += lineHeight;
            }

            // 3. Compute Metrics
            float finalWidth = 0;
            foreach (var line in lines) finalWidth = Math.Max(finalWidth, line.Width);
            
            // Handle Alignment (Post-process)
            if (style?.TextAlign == SKTextAlign.Center || style?.TextAlign == SKTextAlign.Right)
            {
                // Re-adjust Origins
                for (int i = 0; i < lines.Count; i++)
                {
                    var line = lines[i];
                    float remaining = maxLineWidth - line.Width;
                    if (remaining > 0)
                    {
                        if (style.TextAlign == SKTextAlign.Center) line.Origin.X += remaining / 2;
                        else if (style.TextAlign == SKTextAlign.Right) line.Origin.X += remaining;
                        lines[i] = line; // Struct update
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

        private static List<string> SplitIntoWords(string text)
        {
            var list = new List<string>();
            if (string.IsNullOrEmpty(text)) return list;
            
            // Naive split by whitespace. 
            // Better: Preserve trailing punctuation attached to words.
            // Even better: Use BreakIterator (too complex for now).
            // Basic approach: Split by space, tab, newline.
            var parts = text.Split(new char[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            return new List<string>(parts);
        }

        private static ComputedTextLine CreateLine(List<string> words, float width, float y, float height, float baseline)
        {
            return new ComputedTextLine
            {
                Text = string.Join(" ", words),
                Width = width,
                Height = height,
                Origin = new SKPoint(0, y), // X is 0 relative to content box initially
                Baseline = baseline
            };
        }
    }
}
