using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;
using FenBrowser.Core.Css;
using FenBrowser.Core.Dom.V2;
using FenBrowser.FenEngine.Typography;
using SkiaSharp;

namespace FenBrowser.FenEngine.Layout
{
    /// <summary>
    /// Handles text measurement, tokenization, wrapping, and line layout.
    /// </summary>
    public static class TextLayoutComputer
    {
        private const float DefaultFontSize = 16f;
        private const int MaxTextLayoutCacheSize = 5000;
        private const float FallbackMaxWidth = 1_000_000f;

        private static readonly ConcurrentDictionary<TextLayoutCacheKey, (LayoutMetrics Metrics, List<ComputedTextLine> Lines)>
            s_textLayoutCache = new();

        private readonly record struct TextLayoutCacheKey(
            string Text,
            string FontFamily,
            float FontSize,
            int FontWeight,
            SKFontStyleSlant FontStyle,
            float AvailableWidth,
            float? LineHeight,
            string WhiteSpaceMode);

        private readonly record struct TextToken(string Text, bool IsWhitespace = false, bool IsNewline = false);

        public static (LayoutMetrics Metrics, List<ComputedTextLine> Lines) ComputeTextLayout(
            Node node,
            CssComputed style,
            SKSize availableSize,
            float viewportWidth)
        {
            if (node is not Text textNode || string.IsNullOrEmpty(textNode.Data))
            {
                return (new LayoutMetrics(), new List<ComputedTextLine>());
            }

            using var paint = new SKPaint
            {
                TextSize = (float)(style?.FontSize ?? DefaultFontSize),
                IsAntialias = true
            };

            var resolvedSlant = style?.FontStyle ?? SKFontStyleSlant.Upright;
            var resolvedWeight = style?.FontWeight ?? 400;
            paint.Typeface = TextLayoutHelper.ResolveTypeface(
                style?.FontFamilyName,
                textNode.Data,
                resolvedWeight,
                resolvedSlant);

            var fontSize = paint.TextSize;
            using var font = new SKFont(paint.Typeface, fontSize);
            var normalizedMetrics = NormalizedFontMetrics.FromSkia(
                font.Metrics,
                fontSize,
                style?.LineHeight.HasValue == true ? (float?)style.LineHeight.Value : null);

            var lineHeight = normalizedMetrics.LineHeight;
            var baselineOffset = normalizedMetrics.GetBaselineOffset();

            var maxLineWidth = ResolveMaxLineWidth(availableSize.Width, viewportWidth);

            var whiteSpaceMode = (style?.WhiteSpace ?? "normal").ToLowerInvariant();
            var cacheKey = new TextLayoutCacheKey(
                textNode.Data,
                style?.FontFamilyName ?? string.Empty,
                paint.TextSize,
                resolvedWeight,
                resolvedSlant,
                maxLineWidth,
                style?.LineHeight.HasValue == true ? (float?)style.LineHeight.Value : null,
                whiteSpaceMode);

            if (s_textLayoutCache.TryGetValue(cacheKey, out var cached))
            {
                return (cached.Metrics, CloneLines(cached.Lines));
            }

            var collapseWhitespace = whiteSpaceMode is "normal" or "nowrap" or "pre-line";
            var preserveNewlines = whiteSpaceMode is "pre" or "pre-wrap" or "pre-line";
            var allowWrap = whiteSpaceMode is "normal" or "pre-wrap" or "pre-line";

            var tokens = Tokenize(textNode.Data, collapseWhitespace, preserveNewlines);

            var lines = new List<ComputedTextLine>();
            var currentLineTokens = new List<TextToken>();
            var currentY = 0f;
            var currentWidth = 0f;
            var spaceWidth = paint.MeasureText(" ");

            void FlushLine(bool forceEmptyLine = false)
            {
                if (!forceEmptyLine && currentLineTokens.Count == 0)
                {
                    return;
                }

                var lineTextBuilder = new StringBuilder();
                for (var i = 0; i < currentLineTokens.Count; i++)
                {
                    lineTextBuilder.Append(currentLineTokens[i].Text);
                }

                lines.Add(new ComputedTextLine
                {
                    Text = lineTextBuilder.ToString(),
                    Width = currentWidth,
                    Height = lineHeight,
                    Origin = new SKPoint(0, currentY),
                    Baseline = baselineOffset
                });

                currentLineTokens.Clear();
                currentWidth = 0f;
                currentY += lineHeight;
            }

            foreach (var token in tokens)
            {
                if (token.IsNewline)
                {
                    FlushLine(forceEmptyLine: true);
                    continue;
                }

                var tokenWidth = token.IsWhitespace && collapseWhitespace
                    ? spaceWidth
                    : paint.MeasureText(token.Text);

                var shouldWrap = allowWrap &&
                                 currentLineTokens.Count > 0 &&
                                 currentWidth + tokenWidth > maxLineWidth;
                if (shouldWrap)
                {
                    FlushLine();
                }

                if (token.IsWhitespace && collapseWhitespace)
                {
                    if (currentLineTokens.Count == 0)
                    {
                        continue;
                    }

                    if (currentLineTokens[^1].IsWhitespace)
                    {
                        continue;
                    }

                    currentLineTokens.Add(new TextToken(" ", IsWhitespace: true));
                    currentWidth += spaceWidth;
                }
                else
                {
                    currentLineTokens.Add(token);
                    currentWidth += tokenWidth;
                }
            }

            if (currentLineTokens.Count > 0)
            {
                FlushLine();
            }

            var finalWidth = 0f;
            foreach (var line in lines)
            {
                finalWidth = Math.Max(finalWidth, line.Width);
            }

            ApplyHorizontalAlignment(lines, maxLineWidth, style?.TextAlign);

            var minContentWidth = 0f;
            foreach (var token in tokens)
            {
                if (token.IsWhitespace || token.IsNewline || string.IsNullOrEmpty(token.Text))
                {
                    continue;
                }

                minContentWidth = Math.Max(minContentWidth, paint.MeasureText(token.Text));
            }

            var metrics = new LayoutMetrics
            {
                ContentHeight = currentY,
                ActualHeight = currentY,
                MaxChildWidth = finalWidth,
                MinContentWidth = minContentWidth,
                MaxContentWidth = finalWidth,
                Baseline = baselineOffset
            };

            var result = (metrics, lines);
            AddToCache(cacheKey, metrics, lines);
            return result;
        }

        private static float ResolveMaxLineWidth(float availableWidth, float viewportWidth)
        {
            if (!float.IsNaN(availableWidth) &&
                !float.IsInfinity(availableWidth) &&
                availableWidth > 0f)
            {
                return availableWidth;
            }

            if (!float.IsNaN(viewportWidth) &&
                !float.IsInfinity(viewportWidth) &&
                viewportWidth > 0f)
            {
                return viewportWidth;
            }

            return FallbackMaxWidth;
        }

        private static void ApplyHorizontalAlignment(List<ComputedTextLine> lines, float maxLineWidth, SKTextAlign? align)
        {
            if (align is not (SKTextAlign.Center or SKTextAlign.Right))
            {
                return;
            }

            for (var i = 0; i < lines.Count; i++)
            {
                var line = lines[i];
                var remaining = maxLineWidth - line.Width;
                if (remaining <= 0f)
                {
                    continue;
                }

                if (align == SKTextAlign.Center)
                {
                    line.Origin.X += remaining / 2f;
                }
                else if (align == SKTextAlign.Right)
                {
                    line.Origin.X += remaining;
                }

                lines[i] = line;
            }
        }

        private static void AddToCache(
            TextLayoutCacheKey key,
            LayoutMetrics metrics,
            List<ComputedTextLine> lines)
        {
            if (s_textLayoutCache.Count >= MaxTextLayoutCacheSize)
            {
                foreach (var existingKey in s_textLayoutCache.Keys)
                {
                    s_textLayoutCache.TryRemove(existingKey, out _);
                    break;
                }
            }

            s_textLayoutCache[key] = (metrics, CloneLines(lines));
        }

        private static List<ComputedTextLine> CloneLines(List<ComputedTextLine> lines)
        {
            return lines == null ? new List<ComputedTextLine>() : new List<ComputedTextLine>(lines);
        }

        private static List<TextToken> Tokenize(string text, bool collapseWhitespace, bool preserveNewlines)
        {
            var tokens = new List<TextToken>();
            if (string.IsNullOrEmpty(text))
            {
                return tokens;
            }

            var current = new StringBuilder();
            var currentIsWhitespace = false;

            void FlushCurrent()
            {
                if (current.Length == 0)
                {
                    return;
                }

                tokens.Add(new TextToken(current.ToString(), IsWhitespace: currentIsWhitespace));
                current.Clear();
            }

            for (var i = 0; i < text.Length; i++)
            {
                var ch = text[i];

                if (ch == '\r')
                {
                    continue;
                }

                if (ch == '\n')
                {
                    FlushCurrent();
                    currentIsWhitespace = false;

                    if (preserveNewlines)
                    {
                        tokens.Add(new TextToken(string.Empty, IsNewline: true));
                    }
                    else if (collapseWhitespace)
                    {
                        if (tokens.Count > 0 && !tokens[^1].IsWhitespace && !tokens[^1].IsNewline)
                        {
                            tokens.Add(new TextToken(" ", IsWhitespace: true));
                        }
                    }
                    else
                    {
                        tokens.Add(new TextToken(" ", IsWhitespace: true));
                    }

                    continue;
                }

                if (IsWhitespace(ch))
                {
                    if (collapseWhitespace)
                    {
                        FlushCurrent();
                        currentIsWhitespace = false;

                        if (tokens.Count > 0 && !tokens[^1].IsWhitespace && !tokens[^1].IsNewline)
                        {
                            tokens.Add(new TextToken(" ", IsWhitespace: true));
                        }
                    }
                    else
                    {
                        if (!currentIsWhitespace)
                        {
                            FlushCurrent();
                            currentIsWhitespace = true;
                        }

                        current.Append(ch);
                    }

                    continue;
                }

                if (currentIsWhitespace)
                {
                    FlushCurrent();
                    currentIsWhitespace = false;
                }

                current.Append(ch);
            }

            FlushCurrent();

            if (collapseWhitespace)
            {
                while (tokens.Count > 0 && tokens[^1].IsWhitespace)
                {
                    tokens.RemoveAt(tokens.Count - 1);
                }
            }

            return tokens;
        }

        private static bool IsWhitespace(char ch)
        {
            return ch is ' ' or '\t' or '\f' or '\v';
        }
    }
}
