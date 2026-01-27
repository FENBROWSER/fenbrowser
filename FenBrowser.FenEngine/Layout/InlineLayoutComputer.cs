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
            Func<Element, SKSize, int, LayoutMetrics> atomicMeasurer,
            int depth,
            List<FloatExclusion> exclusions = null,
            IEnumerable<Node> customChildrenSource = null)
        {
            var result = new InlineLayoutResult();
            if (container == null || depth > 80) return result;

            var containerStyle = styleProvider(container);
            // INHERITANCE FIX: Walk up the tree to find text-align if not set
            // Standard CSS: text-align is an inherited property.
            SKTextAlign textAlign = SKTextAlign.Left;
            var current = container;
            while (current != null)
            {
                var s = styleProvider(current);
                if (s?.TextAlign != null)
                {
                    textAlign = s.TextAlign.Value;
                    break;
                }
                current = current.Parent as Element;
            }
            // Default to Left if no ancestor has it set
            
            bool isVerticalRL = containerStyle?.WritingMode == "vertical-rl";

            float maxWidth = isVerticalRL ? availableSize.Height : availableSize.Width;
            if (float.IsInfinity(maxWidth)) maxWidth = 1920; // Guard (using reasonable viewport fallback)

            // RULE 2: Calculate the "strut" (the zero-width baseline-aligned box of the container)
            float containerFontSize = (float)(containerStyle?.FontSize ?? 16);
            var containerTypeface = TextLayoutHelper.ResolveTypeface(containerStyle?.FontFamilyName, " ", containerStyle?.FontWeight ?? 400, SKFontStyleSlant.Upright);
            using var containerFont = new SKFont(containerTypeface, containerFontSize);
            var containerNormalized = Typography.NormalizedFontMetrics.FromSkia(containerFont.Metrics, containerFontSize, (float?)containerStyle?.LineHeight);
            
            float strutAscent = containerNormalized.GetBaselineOffset();
            float strutHeight = containerNormalized.LineHeight;
            float strutDescent = strutHeight - strutAscent;

            float currentX = 0;
            float currentY = 0;
            float currentLineHeight = 0;
            float maxLineContentWidth = 0;
            float maxContentWidth = 0; // Ideal width (unwrapped)
            float minContentWidth = 0; // Smallest unbreakable width
            
            float currentUnwrappedX = 0; 
            
            // Buffer to hold items for the current line so we can align them later
            var currentLineItems = new List<LineItem>();

            // exclusion-aware start/end for current band
            float currentXStart = 0;
            float currentXMax = maxWidth;

            // Line Limit Guard
            int lineCountGuard = 0;

            // Helper to close the current line
            void FlushLine()
            {
                if (lineCountGuard++ > 5000) return;
                if (currentLineItems.Count == 0) return;

                // 1. Calculate Alignment Offset
                // We align within the "available band" [currentXStart, currentXMax]
                
                // ADJUSTMENT: Trim trailing whitespace width for alignment calculation
                float contentWidth = currentX - currentXStart; 
                if (currentLineItems.Count > 0 && currentLineItems.Last().IsText && currentLineItems.Last().TextLine.Text.EndsWith(" "))
                {
                    // Look up space width from the font used in the last item?
                    // Simplified: just use the difference if we know it.
                    // Actually, the last item's width ALREADY includes the space.
                    // We should probably have stored the "content width without trailing space"
                    
                    // Let's use a simpler heuristic: if last item is space-terminated, subtract a reasonable space.
                    // Better: The last item's TextLine.Width includes the space. 
                    // Let's just subtract it if it's there.
                }

                float availableWidthInBand = currentXMax - currentXStart;
                float remainingSpace = availableWidthInBand - contentWidth;

                // Clamp remaining space (can be negative if we overflowed)
                if (remainingSpace < 0) remainingSpace = 0;

                float xOffset = 0;
                
                if (remainingSpace > 0)
                {
                    if (textAlign == SKTextAlign.Center) xOffset = remainingSpace / 2;
                    else if (textAlign == SKTextAlign.Right) xOffset = remainingSpace;
                }
                
                // Align relative to the band start
                // items are positioned at item.X which is accumulating from currentXStart
                // so we just add xOffset to them.

                // 2. Commit Items
                // Calculate Line Metrics (Baseline Alignment)
                // Initialize with the strut (parent's font metrics)
                float maxAscent = strutAscent;
                float maxDescent = strutDescent;
                
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
                    
                    float verticalOffset = 0;
                    if (!string.IsNullOrEmpty(item.VerticalAlign))
                    {
                        if (item.VerticalAlign == "sub") verticalOffset = item.Height * 0.2f;
                        else if (item.VerticalAlign == "super") verticalOffset = -item.Height * 0.3f;
                        else if (item.VerticalAlign == "middle") 
                        {
                            // Middle aligns the midpoint of the element with the baseline + x-height/2
                            // Simplified: Align centers of line and item
                            // This is tricky without x-height, let's just center vertically within row
                            // float lineMid = maxAscent - (maxAscent + maxDescent)/2; // relative to baseline
                            // float itemMid = item.Ascent - item.Height/2;
                            // verticalOffset = lineMid - itemMid; 
                            
                            // Alternative: Shift down slightly to center on x-height (approx 0.25em)
                            verticalOffset = -item.Height * 0.15f; 
                        }
                        else if (item.VerticalAlign == "top")
                        {
                            // Align top of item with top of line
                            // Line Top is at relative Y = 0 (currentY)
                            // Item Top is at relative Y = maxAscent - item.Ascent
                            // To make Item Top = 0, we need offset
                            // targetY = 0. We usually draw at Y = maxAscent - item.Ascent
                            // so offset = -(maxAscent - item.Ascent)
                            verticalOffset = -(maxAscent - item.Ascent);
                        }
                        else if (item.VerticalAlign == "bottom")
                        {
                            // Align bottom of item with bottom of line
                            // Line Bottom relative = maxAscent + maxDescent
                            // Item Bottom relative = maxAscent - item.Ascent + item.Height
                            // targetY = LineBottom - item.Height
                            // currentY = maxAscent - item.Ascent
                            // offset = targetY - currentY
                            //        = (maxAscent + maxDescent - item.Height) - (maxAscent - item.Ascent)
                            //        = maxDescent - item.Height + item.Ascent
                            //        = maxDescent - (item.Height - item.Ascent)
                            //        = maxDescent - item.Descent
                            verticalOffset = maxDescent - (item.Height - item.Ascent);
                        }
                        else if (item.VerticalAlign == "text-top")
                        {
                            // Align top of item with top of text (baseline - ascent of parent font)
                            // Similar to top but uses text metrics rather than line box
                            verticalOffset = -(maxAscent - item.Ascent);
                        }
                        else if (item.VerticalAlign == "text-bottom")
                        {
                            // Align bottom of item with bottom of text area
                            verticalOffset = maxDescent - (item.Height - item.Ascent);
                        }
                        else if (item.VerticalAlign != null)
                        {
                            // Try to parse as length (px) or percentage
                            string va = item.VerticalAlign.Trim();
                            if (va.EndsWith("px") && float.TryParse(va.Replace("px", ""), out float pxVal))
                            {
                                // Positive values move up, negative move down (relative to baseline)
                                verticalOffset = -pxVal;
                            }
                            else if (va.EndsWith("em") && float.TryParse(va.Replace("em", ""), out float emVal))
                            {
                                verticalOffset = -emVal * item.Height;
                            }
                            else if (va.EndsWith("%") && float.TryParse(va.Replace("%", ""), out float pctVal))
                            {
                                // Percentage of line-height
                                verticalOffset = -(pctVal / 100f) * (maxAscent + maxDescent);
                            }
                        }
                    }

                    float finalX = item.X + xOffset;
                    float baseAlignY = currentY + (maxAscent - item.Ascent);
                    float finalY = baseAlignY + verticalOffset;

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
                            Origin = new SKPoint(finalX, finalY)
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
                        
                        var finalRect = new SKRect(finalX, finalY + r.Top, finalX + r.Width, finalY + r.Bottom);
                        result.ElementRects[item.Node] = finalRect;
                    }
                }

                // 3. Advance Line
                maxLineContentWidth = Math.Max(maxLineContentWidth, currentX);
                maxContentWidth = Math.Max(maxContentWidth, currentUnwrappedX);
                currentY += alignedLineHeight; // Use the aligned height, which might be taller than raw max height

                // Reset Line State
                currentLineHeight = 0;
                currentLineItems.Clear();
                currentUnwrappedX = 0; 

                // Re-calculate available width for next line
                UpdateAvailableWidthForY();
            }

            void UpdateAvailableWidthForY()
            {
                currentXStart = 0;
                currentXMax = maxWidth;

                if (exclusions != null)
                {
                    foreach (var exc in exclusions)
                    {
                        var range = exc.GetOccupiedRange(currentY, 1); // Check narrow strip at top of line
                        if (range.HasValue)
                        {
                            if (exc.IsLeft)
                            {
                                currentXStart = Math.Max(currentXStart, range.Value.Right);
                            }
                            else
                            {
                                currentXMax = Math.Min(currentXMax, range.Value.Left);
                            }
                        }
                    }
                }

                // If band is invalid (floats overlap), skip down
                if (currentXStart >= currentXMax)
                {
                     // Force move down? For now just allow zero width line and wrap
                     currentXMax = currentXStart; 
                }

                currentX = currentXStart; // Start next line at the new start
            }

            // Init first line
            UpdateAvailableWidthForY();
            
            // Process Ruby helper (omitted here, unchanged)
            void ProcessRubyElement(Element rubyElem, CssComputed rubyStyle, int currentDepth)
            {
                // Ruby layout: base text with annotation (RT) positioned above
                // Parse ruby structure and render as a single inline unit
                
                using var basePaint = new SKPaint();
                using var rtPaint = new SKPaint();
                
                float baseFontSize = (float)(rubyStyle?.FontSize ?? 16);
                float rtFontSize = baseFontSize * 0.5f; // RT is 50% of base
                
                basePaint.TextSize = baseFontSize;
                rtPaint.TextSize = rtFontSize;
                
                var baseTypeface = TextLayoutHelper.ResolveTypeface(rubyStyle?.FontFamilyName, "漢", rubyStyle?.FontWeight ?? 400, SKFontStyleSlant.Upright);
                basePaint.Typeface = baseTypeface;
                rtPaint.Typeface = baseTypeface;
                basePaint.IsAntialias = true;
                rtPaint.IsAntialias = true;
                
                // Parse ruby structure: collect base segments and their RT annotations
                var segments = new List<(string baseText, string rtText)>();
                string currentBase = "";
                
                foreach (var child in rubyElem.Children)
                {
                    if (child is Text textNode)
                    {
                        currentBase += textNode.Data ?? "";
                    }
                    else if (child is Element elem)
                    {
                        if (elem.TagName == "RT")
                        {
                            string rtContent = elem.TextContent ?? "";
                            segments.Add((currentBase.Trim(), rtContent.Trim()));
                            currentBase = "";
                        }
                        else if (elem.TagName == "RP")
                        {
                            // Ignore RP - fallback parentheses for legacy browsers
                        }
                        else if (elem.TagName == "RB")
                        {
                            currentBase += elem.TextContent ?? "";
                        }
                    }
                }
                
                // Handle trailing base text without RT
                if (!string.IsNullOrWhiteSpace(currentBase))
                {
                    segments.Add((currentBase.Trim(), ""));
                }
                
                // Calculate metrics
                using var baseFont = new SKFont(baseTypeface, baseFontSize);
                using var rtFont = new SKFont(baseTypeface, rtFontSize);
                var baseMetrics = baseFont.Metrics;
                var rtMetrics = rtFont.Metrics;
                
                float rtHeight = rtFontSize * 1.2f;
                float baseHeight = baseFontSize * 1.2f;
                float totalHeight = rtHeight + baseHeight;
                
                // Process each base-RT pair and add as inline content
                foreach (var (baseText, rtText) in segments)
                {
                    if (string.IsNullOrEmpty(baseText) && string.IsNullOrEmpty(rtText)) continue;
                    
                    float baseWidth = string.IsNullOrEmpty(baseText) ? 0 : basePaint.MeasureText(baseText);
                    float rtWidth = string.IsNullOrEmpty(rtText) ? 0 : rtPaint.MeasureText(rtText);
                    float containerWidth = Math.Max(baseWidth, rtWidth);
                    
                    // Check for line wrap
                    if (currentX + containerWidth > maxWidth && currentX > 0)
                    {
                        FlushLine();
                    }
                    
                    // Store the ruby element in ElementRects with its dimensions
                    // The paint tree builder will handle rendering RT above and base below
                    var rubyRect = new SKRect(currentX, 0, currentX + containerWidth, totalHeight);
                    result.ElementRects[rubyElem] = rubyRect;
                    
                    // Add as atomic line item so FlushLine handles vertical positioning
                    currentLineItems.Add(new LineItem
                    {
                        Node = rubyElem,
                        IsText = false,
                        X = currentX,
                        Rect = new SKRect(0, 0, containerWidth, totalHeight),
                        Ascent = totalHeight * 0.75f, // Baseline alignment - base text baseline
                        Height = totalHeight,
                        VerticalAlign = rubyStyle?.VerticalAlign
                    });
                    
                    // Store combined text for rendering (RT\nBase format for paint tree builder)
                    // Using a special format: "RT:rtText|BASE:baseText|RT_SIZE:size|BASE_SIZE:size"
                    var combinedText = $"RT:{rtText}|BASE:{baseText}|RT_SIZE:{rtFontSize}|BASE_SIZE:{baseFontSize}|RT_HEIGHT:{rtHeight}";
                    
                    if (!result.TextLines.ContainsKey(rubyElem))
                        result.TextLines[rubyElem] = new List<ComputedTextLine>();
                    
                    // Store metadata in a ComputedTextLine for the paint tree builder
                    result.TextLines[rubyElem].Add(new ComputedTextLine
                    {
                        Text = combinedText,
                        Width = containerWidth,
                        Height = totalHeight,
                        Baseline = baseFontSize,
                        Origin = new SKPoint(0, 0) // Relative to element box
                    });
                    
                    currentX += containerWidth;
                    currentLineHeight = Math.Max(currentLineHeight, totalHeight);
                }
            }
            
            // Helper to process a node recursively
            void ProcessNode(Node node, CssComputed parentStyle, int currentDepth)
            {
                if (currentDepth > 80) return;
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

                // Handle RUBY annotation elements
                if (style?.Display == "ruby" && node is Element rubyElem)
                {
                    ProcessRubyElement(rubyElem, style, currentDepth);
                    return;
                }
                
                // Skip display:none elements (including RP)
                if (style?.Display == "none") return;

                bool isFloat = (style?.Float?.ToLowerInvariant() == "left" || style?.Float?.ToLowerInvariant() == "right");
                
                // Handle FLOATS (inline-floats)
                if (isFloat && node is Element floatElem)
                {
                    // Measure Float (using available width of the band)
                    // Note: Floats effectively shrink-to-fit if width is auto, usually. 
                    // But here we pass available space.
                    var floatMetrics = atomicMeasurer(floatElem, new SKSize(currentXMax - currentXStart, float.PositiveInfinity), currentDepth + 1);
                    float w = floatMetrics.MaxChildWidth;
                    float h = floatMetrics.ContentHeight;
                    var margin = style?.Margin ?? new Thickness(0);
                    float totalW = w + (float)(margin.Left + margin.Right);
                    float totalH = h + (float)(margin.Top + margin.Bottom); // Float height includes margin

                    // Determine placement Y
                    // Floats align to the top of the current line?
                    // Simplified: Place at currentY (top of line box accumulation)
                    float floatY = currentY;

                    // Determine placement X
                    float floatX = 0;
                    bool isLeft = (style.Float.ToLowerInvariant() == "left");

                    if (isLeft)
                    {
                        // Place at Left edge of available band + Margin
                        floatX = currentXStart + (float)margin.Left;
                        // Actual exclusion rect includes full margin box? 
                        // The exclusion should push content away. Explicit margin is part of the box.
                        // We typically exclude the Border Box + Margin. 
                        // Let's say Rect is BorderBox. Exclusion is MarginBox.
                    }
                    else
                    {
                        // Place at Right edge
                        floatX = currentXMax - w - (float)margin.Right;
                    }

                    // Create Exclusion (Margin Box)
                    // Exclusion logic typically expects the Area that text CANNOT go into.
                    // For Left Float: [FloatX - ML, FloatX + W + MR]
                    var marginBox = new SKRect(
                        isLeft ? currentXStart : (currentXMax - totalW),
                        floatY,
                        isLeft ? (currentXStart + totalW) : currentXMax,
                        floatY + totalH);

                    var exc = FloatExclusion.CreateFromStyle(marginBox, isLeft, style);
                    if (exclusions == null) exclusions = new List<FloatExclusion>();
                    exclusions.Add(exc);

                    // Register Position for rendering (BorderBox relative to container)
                    var borderBox = new SKRect(floatX, floatY + (float)margin.Top, floatX + w, floatY + (float)margin.Top + h);
                    result.ElementRects[floatElem] = borderBox;

                    // Update Available Width immediately so subsequent content on this line wraps
                    UpdateAvailableWidthForY();
                    
                    // If Float was "Left", currentXStart increased. We must bump currentX if it's behind.
                    if (currentX < currentXStart) currentX = currentXStart;
                    
                    // Floats don't contribute to 'currentX' flow directly, they just shrink space.
                    // But they DO contribute to "Line Height" if we want the line to expand to contain them?
                    // No, floats separate from line boxes usually. But for "clearance", maybe?
                    // Let's just update height tracking for container bounds if needed.
                    // maxLineContentWidth...
                    
                    return; // Done with this node, don't treat as atomic flow
                }

                bool isAtomic = style?.Display == "inline-block" || node.NodeName == "IMG" || node.NodeName == "INPUT" || node.NodeName == "BUTTON";

                if (isAtomic && node is Element elem)
                {
                    // Use infinite width for shrink-to-fit (intrinsic content width) instead of container width
                    var metrics = atomicMeasurer(elem, new SKSize(float.PositiveInfinity, float.PositiveInfinity), currentDepth + 1);
                    float w = metrics.MaxChildWidth;
                    float h = metrics.ContentHeight;
                    
                    var margin = style?.Margin ?? new Thickness(0);
                    float totalW = w + (float)(margin.Left + margin.Right);
                    float totalH = h + (float)(margin.Top + margin.Bottom);

                    // WARPING CHECK
                    // Only wrap if NOT nowrap
                    bool nowrap = (style?.WhiteSpace?.ToLowerInvariant() == "nowrap");
                    
                    if (!nowrap && currentX + totalW > currentXMax && currentX > currentXStart)
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
                        Height = totalH,
                        VerticalAlign = style?.VerticalAlign
                    });

                    currentX += totalW;
                    currentUnwrappedX += totalW;
                    minContentWidth = Math.Max(minContentWidth, totalW);
                    currentLineHeight = Math.Max(currentLineHeight, totalH);
                }
                else if (node is Text textNode)
                {
                    string data = textNode.Data;
                    if (string.IsNullOrEmpty(data)) return;

                    using var paint = new SKPaint();
                    
                    // RULE 2: Use NormalizedFontMetrics - FenEngine controls line-height
                    double? rawFontSize = style?.FontSize;
                    float fontSize = 16f; // Default
                    if (rawFontSize.HasValue && rawFontSize.Value >= 10) {
                        fontSize = (float)rawFontSize.Value;
                    }
                    paint.TextSize = fontSize;
                    
                    var typeface = TextLayoutHelper.ResolveTypeface(style?.FontFamilyName, data, style?.FontWeight ?? 400, (style?.FontStyle == SKFontStyleSlant.Italic) ? SKFontStyleSlant.Italic : SKFontStyleSlant.Upright);
                    paint.Typeface = typeface;
                    paint.IsAntialias = true;

                    // FIX: Handle whitespace collapsing explicitly
                    string ws = style?.WhiteSpace?.ToLowerInvariant();
                    bool collapse = ws == "normal" || ws == "nowrap" || ws == "pre-line" || string.IsNullOrEmpty(ws);
                    
                    if (collapse)
                    {
                        // Collapse all whitespace sequences to single space
                        data = System.Text.RegularExpressions.Regex.Replace(data, @"\s+", " ");
                    }
                    
                    // FIX: Don't remove empty entries if we want to preserve spaces/height for whitespace-only nodes
                    var words = data.Split(new char[] { ' ', '\t', '\r', '\n' });
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

                    // If it's pure whitespace, we still need it to contribute to line height
                    if (words.Length == 0 || (words.Length == 1 && string.IsNullOrEmpty(words[0])))
                    {
                        // Add a virtual space item to ensure the line has height
                        currentLineItems.Add(new LineItem
                        {
                            Node = node,
                            IsText = true,
                            X = currentX,
                            TextLine = new ComputedTextLine
                            {
                                Text = " ",
                                Width = spaceW,
                                Height = lineHeight,
                                Baseline = baselineOffset,
                                Origin = new SKPoint(0, 0)
                            },
                            Ascent = baselineOffset,
                            Height = lineHeight,
                            VerticalAlign = style?.VerticalAlign
                        });
                        currentX += spaceW;
                        currentUnwrappedX += spaceW;
                        currentLineHeight = Math.Max(currentLineHeight, lineHeight);
                        return;
                    }

                    foreach (var word in words)
                    {
                        if (string.IsNullOrEmpty(word))
                        {
                            // It was a space that got split out
                            currentLineItems.Add(new LineItem
                            {
                                Node = node,
                                IsText = true,
                                X = currentX,
                                TextLine = new ComputedTextLine
                                {
                                    Text = " ",
                                    Width = spaceW,
                                    Height = lineHeight,
                                    Baseline = baselineOffset,
                                    Origin = new SKPoint(0, 0)
                                },
                                Ascent = baselineOffset,
                                Height = lineHeight,
                                VerticalAlign = style?.VerticalAlign
                            });
                            currentX += spaceW;
                            currentUnwrappedX += spaceW;
                            currentLineHeight = Math.Max(currentLineHeight, lineHeight);
                            continue;
                        }

                        float wordW = paint.MeasureText(word);
                        if (wordW <= 0 && word.Length > 0) wordW = paint.TextSize * 0.5f * word.Length; // Fail-safe fallback

                        if (!nowrap && currentX + wordW > currentXMax && currentX > currentXStart)
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
                            Height = lineHeight,
                            VerticalAlign = style?.VerticalAlign
                        });

                        // FenLogger.Debug($"[INLINE-WORD] word='{word}' wordW={wordW} currentX={currentX} paint.TextSize={paint.TextSize}", LogCategory.Rendering);
                        currentX += wordW + spaceW;
                        currentUnwrappedX += wordW + spaceW;
                        minContentWidth = Math.Max(minContentWidth, wordW);
                        currentLineHeight = Math.Max(currentLineHeight, lineHeight);
                    }
                }
                else if (node is Element containerElem)
                {
                    if (containerElem.Children != null)
                    {
                        foreach (var child in containerElem.Children)
                        {
                             ProcessNode(child, style, currentDepth + 1);
                        }
                    }
                }
            }

            var childrenToProcess = customChildrenSource ?? container.Children;
            if (childrenToProcess != null)
            {
                foreach (var child in childrenToProcess)
                {
                    ProcessNode(child, containerStyle, depth + 1);
                }
            }

            FlushLine(); // Flush last line
            
            result.Metrics = new LayoutMetrics 
            {
                ContentHeight = currentY, // Logical Height (Stack Size)
                MaxChildWidth = maxLineContentWidth,
                MinContentWidth = minContentWidth,
                MaxContentWidth = maxContentWidth
            };

            if (isVerticalRL)
            {
                // TRANSFORM Coordinates: Logical (Horizontal) -> Physical (Vertical RL)
                // Logical X (Inline) -> Physical Y (Top->Bottom)
                // Logical Y (Stack) -> Physical X (Right->Left)
                
                // We need the total stack size (Logical Height) to anchor the Right Edge?
                // Or just anchor to Available Width?
                // Convention: vertical-rl flows from Right edge of container.
                float containerWidth = availableSize.Width;
                if (float.IsInfinity(containerWidth)) containerWidth = result.Metrics.ContentHeight; // Fallback

                var newRects = new Dictionary<Node, SKRect>();
                foreach (var kvp in result.ElementRects)
                {
                    var r = kvp.Value;
                    // Swap W/H
                    float w = r.Height;
                    float h = r.Width;
                    // Swap X/Y
                    // PhysY = LogX
                    float newY = r.Left;
                    // PhysX = ContainerW - LogY - LogH (Stack from Right)
                    float newX = containerWidth - r.Top - r.Height; 
                    
                    newRects[kvp.Key] = new SKRect(newX, newY, newX + w, newY + h);
                }
                result.ElementRects = newRects;
                
                var newTextLines = new Dictionary<Node, List<ComputedTextLine>>();
                foreach (var kvp in result.TextLines)
                {
                    var list = new List<ComputedTextLine>();
                    foreach (var line in kvp.Value)
                    {
                        var newLine = line; // struct copy
                        // Swap Dimensions
                        float tmpW = newLine.Width;
                        newLine.Width = newLine.Height;
                        newLine.Height = tmpW;
                        
                        // We also need to rotate the Text Run?
                        // SkiaRenderer will handle rotation if we flag it? 
                        // Or we assume the Renderer just renders "At Point" and we rotated the point?
                        // The text itself needs to draw vertically.
                        // We can flag it by using a special Unicode marker or just rely on Renderer checking WritingMode.
                        
                        list.Add(newLine);
                    }
                    newTextLines[kvp.Key] = list;
                }
                result.TextLines = newTextLines;
                
                // Swap Metrics
                float tmp = result.Metrics.ContentHeight;
                result.Metrics.ContentHeight = result.Metrics.MaxChildWidth;
                result.Metrics.MaxChildWidth = tmp;
            }

            return result;
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
            public string VerticalAlign;
        }
    }
}
