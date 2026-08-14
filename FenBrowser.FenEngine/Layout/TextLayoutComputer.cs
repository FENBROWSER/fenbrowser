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
        private const int MaxCacheableTextChars = 64 * 1024;
        private const float FallbackMaxWidth = 1_000_000f;

        private static readonly ConcurrentDictionary<TextLayoutCacheKey, (LayoutMetrics Metrics, List<ComputedTextLine> Lines)>
            s_textLayoutCache = new();
        private static readonly object s_cacheMutationLock = new();

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

            var text = textNode.Data;
            var fontSize = (float)(style?.FontSize ?? DefaultFontSize);
            var resolvedSlant = style?.FontStyle ?? SKFontStyleSlant.Upright;
            var resolvedWeight = style?.FontWeight ?? 400;
            var resolvedTypeface = TextLayoutHelper.ResolveTypeface(
                style?.FontFamilyName,
                text,
                resolvedWeight,
                resolvedSlant);

            using var font = new SKFont(resolvedTypeface, fontSize);
            var normalizedMetrics = NormalizedFontMetrics.FromSkia(
                font.Metrics,
                fontSize,
                style?.LineHeight.HasValue == true ? (float?)style.LineHeight.Value : null);

            var lineHeight = normalizedMetrics.LineHeight;
            var baselineOffset = normalizedMetrics.GetBaselineOffset();
            var maxLineWidth = ResolveMaxLineWidth(availableSize.Width, viewportWidth);
            var whiteSpaceMode = NormalizeWhiteSpaceMode(style?.WhiteSpace);
            var cacheable = text.Length <= MaxCacheableTextChars;

            var cacheKey = new TextLayoutCacheKey(
                text,
                style?.FontFamilyName ?? string.Empty,
                fontSize,
                resolvedWeight,
                resolvedSlant,
                maxLineWidth,
                style?.LineHeight.HasValue == true ? (float?)style.LineHeight.Value : null,
                whiteSpaceMode);

            if (cacheable && s_textLayoutCache.TryGetValue(cacheKey, out var cached))
            {
                // Alignment is intentionally applied AFTER the cache lookup. It changes
                // only line origins, not shaping/wrapping/metrics, so storing aligned
                // lines poisoned otherwise-identical cache entries across text-align
                // values and unnecessarily fragmented the cache when align was part of
                // the key. Keep cached geometry in its natural left-origin form.
                var cachedLines = CloneLines(cached.Lines);
                ApplyHorizontalAlignment(cachedLines, maxLineWidth, style?.TextAlign);
                return (cached.Metrics, cachedLines);
            }

            var collapseWhitespace = whiteSpaceMode is "normal" or "nowrap" or "pre-line";
            var preserveNewlines = whiteSpaceMode is "pre" or "pre-wrap" or "pre-line";
            var allowWrap = whiteSpaceMode is "normal" or "pre-wrap" or "pre-line";

            var tokens = Tokenize(text, collapseWhitespace, preserveNewlines);

            var lines = new List<ComputedTextLine>();
            var currentLineTokens = new List<TextToken>();
            var currentY = 0f;
            var currentWidth = 0f;
            var minContentWidth = 0f;
            var spaceWidth = font.MeasureText(" ");

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
                    : font.MeasureText(token.Text);

                // Min-content sizing previously re-measured every non-whitespace token
                // after line construction, doubling expensive Skia text measurements on
                // ordinary paragraphs. The width is already known here.
                if (!token.IsWhitespace && !string.IsNullOrEmpty(token.Text))
                {
                    minContentWidth = Math.Max(minContentWidth, tokenWidth);
                }

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

            var metrics = new LayoutMetrics
            {
                ContentHeight = currentY,
                ActualHeight = currentY,
                MaxChildWidth = finalWidth,
                MinContentWidth = minContentWidth,
                MaxContentWidth = finalWidth,
                Baseline = baselineOffset
            };

            // Cache only natural, unaligned line geometry. Large text nodes are not
            // retained globally: a few huge editor/log/preformatted nodes should not
            // pin hundreds of megabytes of source strings and line copies just because
            // layout happened to touch them once.
            if (cacheable)
            {
                AddToCache(cacheKey, metrics, lines);
            }

            ApplyHorizontalAlignment(lines, maxLineWidth, style?.TextAlign);
            return (metrics, lines);
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

        private static string NormalizeWhiteSpaceMode(string whiteSpace)
        {
            if (string.IsNullOrWhiteSpace(whiteSpace))
            {
                return "normal";
            }

            return whiteSpace.Trim().ToLowerInvariant();
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
                else
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
            var cachedLines = CloneLines(lines);

            // All cache mutations go through this lock while reads remain lock-free.
            // The previous Count/check/remove/add sequence raced across layout threads:
            // several writers could over-evict and then still grow past the advertised
            // limit. This keeps the global retention bound real without serializing hot
            // cache hits or the expensive text measurement itself.
            lock (s_cacheMutationLock)
            {
                if (!s_textLayoutCache.ContainsKey(key))
                {
                    while (s_textLayoutCache.Count >= MaxTextLayoutCacheSize)
                    {
                        var removed = false;
                        foreach (var existingKey in s_textLayoutCache.Keys)
                        {
                            if (s_textLayoutCache.TryRemove(existingKey, out _))
                            {
                                removed = true;
                                break;
                            }
                        }

                        if (!removed)
                        {
                            break;
                        }
                    }
                }

                s_textLayoutCache[key] = (metrics, cachedLines);
            }
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
