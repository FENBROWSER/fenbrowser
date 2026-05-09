    private static readonly ConcurrentDictionary<string, (LayoutMetrics Metrics, List<ComputedTextLine> Lines)> _textLayoutCache = new();
using System.Collections.Concurrent;
using System;
using System.Collections.Generic;
using SkiaSharp;
using FenBrowser.Core.Css;
using FenBrowser.Core.Dom.V2;
using FenBrowser.Core;
using FenBrowser.FenEngine.Typography;

namespace FenBrowser.FenEngine.Layout
{
    /// <summary>
    /// Handles text measurement, word wrapping, and line breaking.
    /// </summary>
    public static class TextLayoutComputer
    {
        private const float DefaultFontSize = 16f;

            // Text layout cache for repeated measurements
    private static readonly ConcurrentDictionary<TextLayoutCacheKey, (LayoutMetrics Metrics, List<ComputedTextLine> Lines)> s_textLayoutCache = new();
    private const int MaxTextLayoutCacheSize = 5000;

    private readonly record struct TextLayoutCacheKey(
        string Text,
        string FontFamily,
        float FontSize,
        int FontWeight,
        SKFontStyleSlant FontStyle,
        float AvailableWidth,
        float? LineHeight,
        string WhiteSpaceMode
    );public static (LayoutMetrics Metrics, List<ComputedTextLine> Lines) ComputeTextLayout(
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
                FenBrowser.Core.EngineLogCompat.Info($"[TEXT-LAYOUT] Measuring '{textNode.Data.Replace("\n","\\n")}' AvailW={availableSize.Width} WS={style?.WhiteSpace} Font={style?.FontFamilyName}", FenBrowser.Core.Logging.LogCategory.Layout);
            }

            // 1. Setup Paint
            using var paint = new SKPaint();
            paint.TextSize = (float)(style?.FontSize ?? DefaultFontSize);
            paint.Typeface = TextLayoutHelper.ResolveTypeface(style?.FontFamilyName, textNode.Data, style?.FontWeight ?? 400, (style?.FontStyle == SKFontStyleSlant.Italic) ? SKFontStyleSlant.Italic : SKFontStyleSlant.Upright);
            paint.IsAntialias = true;

            float fontSize = paint.TextSize;
            using var font = new SKFont(paint.Typeface, fontSize);
            var normalizedMetrics = NormalizedFontMetrics.FromSkia(
                font.Metrics,
                fontSize,
                style?.LineHeight.HasValue == true ? (float?)style.LineHeight.Value : null);
            float lineHeight = normalizedMetrics.LineHeight;
            float baselineOffset = normalizedMetrics.GetBaselineOffset();

            // Check cache
        string cacheKey = $"{textNode?.Data}_{style?.FontFamilyName}_{style?.FontSize}_{availableSize.Width}";
        if (_textLayoutCache.TryGetValue(cacheKey, out var cached))
        {
            return cached;
        }        // 2. Resolve White-Space Mode
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

                    // Cache result
        string cacheKey = $"{textNode?.Data}_{style?.FontFamilyName}_{style?.FontSize}_{availableSize.Width}";
        var result = (metrics: new LayoutMetrics
        {
            ContentHeight = currentY,
            MaxChildWidth = finalWidth,
            Baseline = baselineOffset
        }, lines: lines);
        if (lines != null && lines.Count > 0)
        {
            _textLayoutCache.TryAdd(cacheKey, result);
        }
        return result;