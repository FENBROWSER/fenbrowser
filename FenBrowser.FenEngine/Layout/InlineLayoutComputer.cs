using System;
using System.Collections.Generic;
using SkiaSharp;
using FenBrowser.Core.Dom;
using FenBrowser.Core.Css;
using FenBrowser.Core;
using FenBrowser.Core.Logging;

namespace FenBrowser.FenEngine.Layout
{
    public class InlineLayoutResult
    {
        public LayoutMetrics Metrics;
        // Where each child element should be placed relative to the container
        public Dictionary<Node, SKRect> ElementRects; 
        // Computed lines for any text nodes encountered
        public Dictionary<Node, List<ComputedTextLine>> TextLines;

        public InlineLayoutResult()
        {
            ElementRects = new Dictionary<Node, SKRect>();
            TextLines = new Dictionary<Node, List<ComputedTextLine>>();
        }
    }

    /// <summary>
    /// Computes layout for an Inline Formatting Context (IFC).
    /// </summary>
    public static class InlineLayoutComputer
    {
        public static InlineLayoutResult Compute(
            Element container, 
            SKSize availableSize, 
            Func<Node, CssComputed> styleProvider,
            Func<Element, SKSize, LayoutMetrics> atomicMeasurer)
        {
            var result = new InlineLayoutResult();
            if (container == null) return result;

            var containerStyle = styleProvider(container);
            var textAlign = containerStyle?.TextAlign ?? SKTextAlign.Left;

            float maxWidth = availableSize.Width;
            if (float.IsInfinity(maxWidth)) maxWidth = 100000; // Guard

            float currentX = 0;
            float currentY = 0;
            float currentLineHeight = 0;
            float maxLineContentWidth = 0;
            
            // Buffer to hold items for the current line so we can align them later
            var currentLineItems = new List<LineItem>();

            // Helper to close the current line
            void FlushLine()
            {
                if (currentLineItems.Count == 0) return;

                // 1. Calculate Alignment Offset
                float contentWidth = currentX; // Usage so far in the line
                float remainingSpace = maxWidth - contentWidth;
                float xOffset = 0;
                
                if (remainingSpace > 0)
                {
                    if (textAlign == SKTextAlign.Center) xOffset = remainingSpace / 2;
                    else if (textAlign == SKTextAlign.Right) xOffset = remainingSpace;
                }

                // 2. Commit Items
                // Calculate Line Metrics (Baseline Alignment)
                float maxAscent = 0;
                float maxDescent = 0;
                
                foreach(var item in currentLineItems)
                {
                    maxAscent = Math.Max(maxAscent, item.Ascent);
                    float descent = item.Height - item.Ascent;
                    maxDescent = Math.Max(maxDescent, descent);
                }
                
                float alignedLineHeight = maxAscent + maxDescent;

                foreach (var item in currentLineItems)
                {
                    // Apply offset and current Y logic
                    // Align to baseline: Y = currentY + (maxAscent - item.Ascent)
                    // This puts all baselines at the same vertical position (currentY + maxAscent)
                    
                    float finalX = item.X + xOffset;
                    float alignY = currentY + (maxAscent - item.Ascent);

                    if (item.IsText)
                    {
                        var textLine = item.TextLine;
                        // Update Origin
                        textLine = new ComputedTextLine
                        {
                            Text = textLine.Text,
                            Width = textLine.Width,
                            Height = textLine.Height,
                            Baseline = textLine.Baseline,
                            Origin = new SKPoint(finalX, alignY)
                        };

                        if (!result.TextLines.ContainsKey(item.Node))
                            result.TextLines[item.Node] = new List<ComputedTextLine>();
                            
                        result.TextLines[item.Node].Add(textLine);
                    }
                    else
                    {
                        // Atomic Element
                        var r = item.Rect;
                        // Rect.Top includes margin-top. 
                        // If item.Ascent included MarginTop, then alignY puts the 'Baseline' at the correct spot.
                        // Atomic Baseline = Content Bottom.
                        // Item.Rect = {0, MarginTop, W, TotalHeight}
                        // Draw at finalX, finalY + Rect.Top?
                        // No, element rect should be offset by alignY.
                        
                        var finalRect = new SKRect(finalX, alignY + r.Top, finalX + r.Width, alignY + r.Bottom);
                        result.ElementRects[item.Node] = finalRect;
                    }
                }

                // 3. Advance Line
                maxLineContentWidth = Math.Max(maxLineContentWidth, currentX);
                currentY += alignedLineHeight; // Use the aligned height, which might be taller than raw max height
                currentX = 0;
                currentLineHeight = 0;
                currentLineItems.Clear();
            }
            
            
            // Helper to process a node recursively
            void ProcessNode(Node node, CssComputed parentStyle)
            {
                var style = styleProvider(node) ?? parentStyle;

                // Handle BR explicitly
                if (node.NodeName == "BR")
                {
                    if (currentLineItems.Count > 0)
                    {
                        FlushLine();
                    }
                    else
                    {
                         // Empty line, advance manually
                         float fontSize = (float)(style?.FontSize ?? 16);
                         float lh = fontSize * 1.2f; // simple approx
                         currentY += lh;
                    }
                    return;
                }

                bool isAtomic = style?.Display == "inline-block" || node.NodeName == "IMG" || node.NodeName == "INPUT" || node.NodeName == "BUTTON";

                if (isAtomic && node is Element elem)
                {
                    // Use infinite width for shrink-to-fit (intrinsic content width) instead of container width
                    var metrics = atomicMeasurer(elem, new SKSize(float.PositiveInfinity, float.PositiveInfinity));
                    float w = metrics.MaxChildWidth;
                    float h = metrics.ContentHeight;
                    
                    var margin = style?.Margin ?? new Thickness(0);
                    float totalW = w + (float)(margin.Left + margin.Right);
                    float totalH = h + (float)(margin.Top + margin.Bottom);

                    // WARP CHECK
                    // Only wrap if NOT nowrap
                    bool nowrap = (style?.WhiteSpace?.ToLowerInvariant() == "nowrap");
                    
                    if (!nowrap && currentX + totalW > maxWidth && currentX > 0)
                    {
                        FlushLine();
                    }

                    // Buffer Item
                    // Ascent = MarginTop + Height (Bottom of content box)
                    float ascent = (float)margin.Top + h;

                    currentLineItems.Add(new LineItem 
                    { 
                        Node = node, 
                        IsText = false, 
                        X = currentX + (float)margin.Left, // Base X position
                        Rect = new SKRect(0, (float)margin.Top, w, h + (float)(margin.Top + margin.Bottom)), // Store relative dims
                        Ascent = ascent,
                        Height = totalH
                    });

                    currentX += totalW;
                    currentLineHeight = Math.Max(currentLineHeight, totalH);
                }
                else if (node is Text textNode)
                {
                    if (string.IsNullOrEmpty(textNode.Data)) return;

                    using var paint = new SKPaint();
                    
                    // RULE 2: Use NormalizedFontMetrics - FenEngine controls line-height
                    double? rawFontSize = style?.FontSize;
                    float fontSize = 16f; // Default
                    if (rawFontSize.HasValue && rawFontSize.Value >= 10) {
                        fontSize = (float)rawFontSize.Value;
                    }
                    paint.TextSize = fontSize;
                    
                    var typeface = TextLayoutHelper.ResolveTypeface(style?.FontFamilyName, textNode.Data, style?.FontWeight ?? 400, (style?.FontStyle == SKFontStyleSlant.Italic) ? SKFontStyleSlant.Italic : SKFontStyleSlant.Upright);
                    paint.Typeface = typeface;
                    paint.IsAntialias = true;

                    var words = textNode.Data.Split(new char[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                    float spaceW = paint.MeasureText(" ");
                    if (spaceW <= 0) spaceW = paint.TextSize * 0.25f;
                    
                    // RULE 2: Use NormalizedFontMetrics instead of raw SKFontMetrics
                    using var font = new SKFont(typeface, fontSize);
                    var skMetrics = font.Metrics;
                    var metrics = Typography.NormalizedFontMetrics.FromSkia(skMetrics, fontSize, (float?)style?.LineHeight);
                    
                    // FenEngine controls line-height through NormalizedFontMetrics
                    float lineHeight = metrics.LineHeight;
                    float baselineOffset = metrics.GetBaselineOffset();
                    
                    // WRAP CHECK
                    bool nowrap = (style?.WhiteSpace?.ToLowerInvariant() == "nowrap");

                    foreach (var word in words)
                    {
                        float wordW = paint.MeasureText(word);
                        if (wordW <= 0 && word.Length > 0) wordW = paint.TextSize * 0.5f * word.Length; // Fail-safe fallback

                        if (!nowrap && currentX + wordW > maxWidth && currentX > 0)
                        {
                            FlushLine();
                        }

                        currentLineItems.Add(new LineItem
                        {
                            Node = node,
                            IsText = true,
                            X = currentX,
                            TextLine = new ComputedTextLine
                            {
                                Text = word + " ",
                                Width = wordW + spaceW,
                                Height = lineHeight,
                                Baseline = baselineOffset,
                                Origin = new SKPoint(0, 0) // Placeholder
                            },
                            Ascent = baselineOffset,
                            Height = lineHeight
                        });

                        FenLogger.Debug($"[INLINE-WORD] word='{word}' wordW={wordW} currentX={currentX} paint.TextSize={paint.TextSize}", LogCategory.Rendering);
                        currentX += wordW + spaceW;
                        currentLineHeight = Math.Max(currentLineHeight, lineHeight);
                    }
                }
                else if (node is Element containerElem)
                {
                    if (containerElem.Children != null)
                    {
                        foreach (var child in containerElem.Children)
                        {
                             ProcessNode(child, style);
                        }
                    }
                }
            }

            if (container.Children != null)
            {
                foreach (var child in container.Children)
                {
                    ProcessNode(child, styleProvider(container));
                }
            }

            FlushLine(); // Flush last line

            return new InlineLayoutResult
            {
                Metrics = new LayoutMetrics { ContentHeight = currentY, MaxChildWidth = maxLineContentWidth },
                ElementRects = result.ElementRects,
                TextLines = result.TextLines
            };
        }

        private class LineItem
        {
            public Node Node;
            public bool IsText;
            public float X; // Relative to line start
            public ComputedTextLine TextLine;
            public SKRect Rect; // For atomic elements
            public float Ascent; // Distance from top to baseline
            public float Height; // Total height
        }
    }
}
