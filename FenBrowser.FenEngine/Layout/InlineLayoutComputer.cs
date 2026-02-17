using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using SkiaSharp;
using FenBrowser.Core.Dom.V2;
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
                current = current.ParentElement;
            }
            // Default to Left if no ancestor has it set
            
            bool isVerticalRL = containerStyle?.WritingMode == "vertical-rl";

            float maxWidth = isVerticalRL ? availableSize.Height : availableSize.Width;
            if (float.IsInfinity(maxWidth)) maxWidth = 1920; // Guard (using reasonable viewport fallback)

            FenBrowser.Core.FenLogger.Info($"[INLINE-DEBUG] Compute Start. Node={container.GetHashCode()} Avail={availableSize.Width} MaxW={maxWidth} Container={container.TagName} TextAlign={textAlign}", FenBrowser.Core.Logging.LogCategory.Layout);

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
                
                // CRITICAL FIX: Guard against NaN/Infinity caused by layout overflows
                if (float.IsNaN(contentWidth) || float.IsInfinity(contentWidth))
                {
                    contentWidth = 0;
                    // Try to recover currentX from items if possible, or just reset to safe values behavior
                    if (currentLineItems.Count > 0)
                        contentWidth = currentLineItems.Sum(i => i.Rect.Width); // Fallback approximation
                }

                bool ellipsisRequested = string.Equals(containerStyle?.TextOverflow, "ellipsis", StringComparison.OrdinalIgnoreCase);
                float availableWidthInBand = currentXMax - currentXStart;
                if (float.IsInfinity(availableWidthInBand)) availableWidthInBand = maxWidth;

                // Apply text-overflow: ellipsis when content exceeds available band
                if (ellipsisRequested && contentWidth > availableWidthInBand)
                {
                    // Build paint using container font to measure ellipsis glyph
                    using var ellPaint = new SKPaint();
                    float ellFontSize = (float)(containerStyle?.FontSize ?? 16);
                    ellPaint.TextSize = Math.Max(10f, ellFontSize);
                    var ellTypeface = TextLayoutHelper.ResolveTypeface(containerStyle?.FontFamilyName, "…", containerStyle?.FontWeight ?? 400,
                        containerStyle?.FontStyle == SKFontStyleSlant.Italic ? SKFontStyleSlant.Italic : SKFontStyleSlant.Upright);
                    ellPaint.Typeface = ellTypeface;
                    ellPaint.IsAntialias = true;

                    float ellipsisWidth = ellPaint.MeasureText("…");
                    float allowedWidth = Math.Max(0, availableWidthInBand - ellipsisWidth);

                    // Trim items from the end until they fit into allowedWidth
                    for (int idx = currentLineItems.Count - 1; idx >= 0; idx--)
                    {
                        var item = currentLineItems[idx];
                        float itemStart = item.X - currentXStart;
                        float itemWidth = item.IsText ? item.TextLine.Width : item.Rect.Width;
                        float itemEnd = itemStart + itemWidth;

                        if (itemStart >= allowedWidth)
                        {
                            currentLineItems.RemoveAt(idx);
                            continue;
                        }

                        if (itemEnd > allowedWidth)
                        {
                            if (!item.IsText)
                            {
                                currentLineItems.RemoveAt(idx);
                                continue;
                            }

                            // Trim text to widthLimit
                            float widthLimit = allowedWidth - itemStart;
                            if (widthLimit <= 0)
                            {
                                currentLineItems.RemoveAt(idx);
                                continue;
                            }

                            // Recreate paint for this text node
                            var itemStyle = styleProvider(item.Node.ParentElement ?? container);
                            using var trimPaint = new SKPaint
                            {
                                TextSize = Math.Max(10f, (float)(itemStyle?.FontSize ?? containerStyle?.FontSize ?? 16)),
                                Typeface = TextLayoutHelper.ResolveTypeface(itemStyle?.FontFamilyName, item.TextLine.Text, itemStyle?.FontWeight ?? 400,
                                    itemStyle?.FontStyle == SKFontStyleSlant.Italic ? SKFontStyleSlant.Italic : SKFontStyleSlant.Upright),
                                IsAntialias = true
                            };

                            var sb = new System.Text.StringBuilder(item.TextLine.Text.Length);
                            float acc = 0;
                            foreach (var ch in item.TextLine.Text)
                            {
                                float cw = trimPaint.MeasureText(ch.ToString());
                                if (acc + cw > widthLimit) break;
                                acc += cw;
                                sb.Append(ch);
                            }

                            if (sb.Length == 0)
                            {
                                currentLineItems.RemoveAt(idx);
                                continue;
                            }

                            item.TextLine.Text = sb.ToString();
                            item.TextLine.Width = acc;
                            currentLineItems[idx] = item;
                        }
                    }

                    // Recompute content width after trimming
                    float trimmedWidth = currentLineItems.Count == 0
                        ? 0
                        : currentLineItems.Max(i => (i.X - currentXStart) + (i.IsText ? i.TextLine.Width : i.Rect.Width));
                    contentWidth = Math.Min(trimmedWidth + ellipsisWidth, availableWidthInBand);

                    // Append ellipsis as a text item on the container (or last text node)
                    var targetNode = currentLineItems.LastOrDefault(i => i.IsText)?.Node ?? container;
                    float ellX = currentXStart + Math.Max(allowedWidth, 0);

                    // Baseline/height taken from container font metrics
                    using var ellFont = new SKFont(ellPaint.Typeface, ellPaint.TextSize);
                    var ellMetrics = Typography.NormalizedFontMetrics.FromSkia(ellFont.Metrics, ellPaint.TextSize, (float?)containerStyle?.LineHeight);
                    float ellLineHeight = ellMetrics.LineHeight;
                    float ellAscent = ellMetrics.GetBaselineOffset();

                    currentLineItems.Add(new LineItem
                    {
                        Node = targetNode,
                        IsText = true,
                        X = ellX,
                        TextLine = new ComputedTextLine
                        {
                            Text = "…",
                            Width = ellipsisWidth,
                            Height = ellLineHeight,
                            Baseline = ellAscent,
                            Origin = new SKPoint(0, 0)
                        },
                        Ascent = ellAscent,
                        Height = ellLineHeight,
                        VerticalAlign = containerStyle?.VerticalAlign
                    });

                    // Prevent centering: treat the line as fully occupied
                    availableWidthInBand = Math.Max(ellipsisWidth, availableWidthInBand);
                }

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

                // availableWidthInBand is computed above; ensure non-negative
                float remainingSpace = availableWidthInBand - contentWidth;
                
                // Clamp remaining space (can be negative if we overflowed)
                if (remainingSpace < 0) remainingSpace = 0;
                if (float.IsNaN(remainingSpace)) remainingSpace = 0;

                float xOffset = 0;
                
                if (remainingSpace > 0)
                {
                    if (textAlign == SKTextAlign.Center) xOffset = remainingSpace / 2;
                    else if (textAlign == SKTextAlign.Right) xOffset = remainingSpace;
                }
                
                FenBrowser.Core.FenLogger.Info($"[INLINE-FLUSH] Node={container.GetHashCode()} Line {lineCountGuard} Y={currentY} ContentW={contentWidth} Avail={availableWidthInBand} Rem={remainingSpace} Offset={xOffset} Align={textAlign} CurX={currentX} Start={currentXStart}", FenBrowser.Core.Logging.LogCategory.Layout);
                
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
                
                FenBrowser.Core.FenLogger.Info($"[INLINE-STRUT] Line {lineCountGuard} StrutAsc={strutAscent} MaxAsc={maxAscent} ContentWidth={contentWidth} Items={currentLineItems.Count}", FenBrowser.Core.Logging.LogCategory.Layout);
                // 2. Commit Items to Result
                // COALESCING LOGIC: Merge adjacent text items into single runs to reduce PaintNode count
                // and fix potential rendering gaps.
                ComputedTextLine? pendingText = null;
                Node pendingNode = null;

                foreach (var item in currentLineItems)
                {
                    // Apply offset
                    float finalX = item.X + xOffset;
                    float baseAlignY = currentY + (maxAscent - item.Ascent);
                    float finalY = baseAlignY; // Start with baseline alignment

                    // Apply vertical-align adjustments
                    float verticalOffset = 0;
                    if (!string.IsNullOrEmpty(item.VerticalAlign))
                    {
                        if (item.VerticalAlign == "sub") verticalOffset = item.Height * 0.2f;
                        else if (item.VerticalAlign == "super") verticalOffset = -item.Height * 0.3f;
                        else if (item.VerticalAlign == "middle") 
                        {
                            verticalOffset = -item.Height * 0.15f; 
                        }
                        else if (item.VerticalAlign == "top")
                        {
                            verticalOffset = -(maxAscent - item.Ascent);
                        }
                        else if (item.VerticalAlign == "bottom")
                        {
                            verticalOffset = maxDescent - (item.Height - item.Ascent);
                        }
                        else if (item.VerticalAlign == "text-top")
                        {
                            verticalOffset = -(maxAscent - item.Ascent);
                        }
                        else if (item.VerticalAlign == "text-bottom")
                        {
                            verticalOffset = maxDescent - (item.Height - item.Ascent);
                        }
                        else if (item.VerticalAlign != null)
                        {
                            string va = item.VerticalAlign.Trim();
                            if (va.EndsWith("px") && float.TryParse(va.Replace("px", ""), out float pxVal))
                            {
                                verticalOffset = -pxVal;
                            }
                            else if (va.EndsWith("em") && float.TryParse(va.Replace("em", ""), out float emVal))
                            {
                                verticalOffset = -emVal * item.Height;
                            }
                            else if (va.EndsWith("%") && float.TryParse(va.Replace("%", ""), out float pctVal))
                            {
                                verticalOffset = -(pctVal / 100f) * (maxAscent + maxDescent);
                            }
                        }
                    }
                    finalY += verticalOffset;
                    
                    if (float.IsInfinity(finalY) || float.IsNaN(finalY)) finalY = currentY;
                    if (float.IsNaN(finalX) || float.IsInfinity(finalX)) finalX = 0;


                    if (item.IsText)
                    {
                        // Check if we can merge with pending
                        if (pendingText.HasValue && pendingNode == item.Node) 
                        {
                            // Merge
                            var p = pendingText.Value;
                            // Assume adjacency in source text corresponds to layout adjacency
                            // But here they are adjacent in list.
                             // Update width and text
                             string combinedText = p.Text + item.TextLine.Text; // Note: Spaces were added as separate items
                             float combinedWidth = (finalX + item.TextLine.Width) - p.Origin.X; // EndX - StartX
                             
                             pendingText = new ComputedTextLine 
                             {
                                 Text = combinedText,
                                 Width = combinedWidth,
                                 Height = Math.Max(p.Height, item.TextLine.Height),
                                 Baseline = p.Baseline, // Assume same baseline
                                 Origin = p.Origin
                             };
                        }
                        else
                        {
                            // Flush pending if exists (different node)
                            if (pendingText.HasValue && pendingNode != null)
                            {
                                if (!result.TextLines.ContainsKey(pendingNode))
                                    result.TextLines[pendingNode] = new List<ComputedTextLine>();
                                result.TextLines[pendingNode].Add(pendingText.Value);
                            }

                            // Start new pending
                            var newLine = item.TextLine;
                            // SKIA FIX: DrawText expects Baseline position, not Top.
                            // baseAlignY is the Top of the aligned item.
                            // Add Ascent to get Baseline. currentY + maxAscent is the consistent baseline for the line.
                            // But individual items might be aligned differently. 
                            // baseAlignY + item.Ascent gives the baseline for THIS item.
                            newLine.Origin = new SKPoint(finalX, baseAlignY + item.Ascent);
                            
                            pendingText = newLine;
                            pendingNode = item.Node;
                        }
                    }
                    else
                    {
                        var r = item.Rect;
                        var finalRect = new SKRect(finalX, finalY + r.Top, finalX + r.Width, finalY + r.Bottom);
                        result.ElementRects[item.Node] = finalRect;
                    }
                }

                // Flush final pending
                if (pendingText.HasValue && pendingNode != null)
                {
                    if (!result.TextLines.ContainsKey(pendingNode))
                        result.TextLines[pendingNode] = new List<ComputedTextLine>();
                    result.TextLines[pendingNode].Add(pendingText.Value);
                }

                // 3. Advance Line
                maxLineContentWidth = Math.Max(maxLineContentWidth, currentX);
                maxContentWidth = Math.Max(maxContentWidth, currentUnwrappedX);
                currentY += alignedLineHeight; // Use the aligned height
                
                // Ensure Y is finite
                if (float.IsInfinity(currentY)) currentY = 0; // Emergency reset

                // Reset Line State
                currentLineHeight = 0;
                currentLineItems.Clear();
                currentUnwrappedX = 0; 

                // Re-calculate available width for next line
                UpdateAvailableWidthForY();
                
                // CRITICAL FIX: Reset currentX to the start of the new line!
                currentX = currentXStart;
            }

            void UpdateAvailableWidthForY()
            {
                currentXStart = 0;
                currentXMax = maxWidth;

                // CRITICAL FIX: Ensure maxWidth is finite
                if (float.IsInfinity(currentXMax)) currentXMax = 1920; 

                if (exclusions != null)
                {
                    foreach (var exc in exclusions)
                    {
                        var range = exc.GetOccupiedRange(currentY, 1); // Check narrow strip at top of line
                        if (range.HasValue)
                        {
                            if (exc.IsLeft)
                            {
                                float r = range.Value.Right;
                                if (float.IsInfinity(r) || float.IsNaN(r)) r = currentXMax; // Clamp infinite float
                                currentXStart = Math.Max(currentXStart, r);
                            }
                            else
                            {
                                float l = range.Value.Left;
                                if (float.IsInfinity(l) || float.IsNaN(l)) l = 0; // Clamp infinite float
                                currentXMax = Math.Min(currentXMax, l);
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
                bool verticalRuby = string.Equals(containerStyle?.WritingMode, "vertical-rl", StringComparison.OrdinalIgnoreCase);
                
                // Process each base-RT pair and add as inline content
                foreach (var (baseText, rtText) in segments)
                {
                    if (string.IsNullOrEmpty(baseText) && string.IsNullOrEmpty(rtText)) continue;
                    
                    float baseWidth = string.IsNullOrEmpty(baseText) ? 0 : basePaint.MeasureText(baseText);
                    float rtWidth = string.IsNullOrEmpty(rtText) ? 0 : rtPaint.MeasureText(rtText);
                    float containerWidth = Math.Max(baseWidth, rtWidth);

                    if (verticalRuby)
                    {
                        // Swap axes: ruby stack flows along inline axis vertically
                        float containerHeight = containerWidth;
                        float containerWidthV = totalHeight;

                        if (currentX + containerWidthV > maxWidth && currentX > 0)
                        {
                            FlushLine();
                        }

                        var rubyRect = new SKRect(currentX, 0, currentX + containerWidthV, containerHeight);
                        result.ElementRects[rubyElem] = rubyRect;

                        currentLineItems.Add(new LineItem
                        {
                            Node = rubyElem,
                            IsText = false,
                            X = currentX,
                            Rect = new SKRect(0, 0, containerWidthV, containerHeight),
                            Ascent = containerHeight * 0.75f,
                            Height = containerHeight,
                            VerticalAlign = rubyStyle?.VerticalAlign
                        });

                        var combinedTextV = $"RT:{rtText}|BASE:{baseText}|RT_SIZE:{rtFontSize}|BASE_SIZE:{baseFontSize}|RT_HEIGHT:{rtHeight}|VERTICAL:1";
                        if (!result.TextLines.ContainsKey(rubyElem))
                            result.TextLines[rubyElem] = new List<ComputedTextLine>();

                        result.TextLines[rubyElem].Add(new ComputedTextLine
                        {
                            Text = combinedTextV,
                            Width = containerWidthV,
                            Height = containerHeight,
                            Baseline = containerHeight * 0.75f,
                            Origin = new SKPoint(0, 0)
                        });

                        currentX += containerWidthV;
                        currentLineHeight = Math.Max(currentLineHeight, containerHeight);
                    }
                    else
                    {
                        // Check for line wrap
                        if (currentX + containerWidth > maxWidth && currentX > 0)
                        {
                            FlushLine();
                        }
                        
                        var rubyRect = new SKRect(currentX, 0, currentX + containerWidth, totalHeight);
                        result.ElementRects[rubyElem] = rubyRect;
                        
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
                        
                        var combinedText = $"RT:{rtText}|BASE:{baseText}|RT_SIZE:{rtFontSize}|BASE_SIZE:{baseFontSize}|RT_HEIGHT:{rtHeight}";
                        
                        if (!result.TextLines.ContainsKey(rubyElem))
                            result.TextLines[rubyElem] = new List<ComputedTextLine>();
                        
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

                    // FIX: Ensure Half-Leading is applied to center text vertically in the line
                    float rawAscent = -skMetrics.Ascent; // Skia Ascent is negative
                    float rawDescent = skMetrics.Descent;
                    float rawContentHeight = rawAscent + rawDescent;
                    
                    if (lineHeight > rawContentHeight)
                    {
                        float leading = lineHeight - rawContentHeight;
                        float halfLeading = leading / 2f;
                        // Center the baseline: top spacing (half-leading) + ascent
                        baselineOffset = halfLeading + rawAscent;
                        FenBrowser.Core.FenLogger.Info($"[TEXT-ALIGN-DEBUG] '{(node is Text t ? t.Data.Trim() : node.NodeType.ToString())}' LH={lineHeight} ContentH={rawContentHeight} Ascent={rawAscent} Leading={leading} Half={halfLeading} Offset={baselineOffset}", FenBrowser.Core.Logging.LogCategory.Layout);
                    }
                    else
                    {
                        FenBrowser.Core.FenLogger.Info($"[TEXT-ALIGN-DEBUG] '{(node is Text t2 ? t2.Data.Trim() : node.NodeType.ToString())}' LH={lineHeight} <= ContentH={rawContentHeight} (No Leading) Offset={baselineOffset}", FenBrowser.Core.Logging.LogCategory.Layout);
                    }
                    
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

                    for (int i = 0; i < words.Length; i++)
                    {
                        var word = words[i];
                        
                        // 1. Add Word (if not empty)
                        if (!string.IsNullOrEmpty(word))
                        {
                            // Measure word character-by-character for accurate advance (HarfBuzz prep)
                            float wordW = 0f;
                            for (int charIdx = 0; charIdx < word.Length; charIdx++)
                            {
                                string charStr = word[charIdx].ToString();
                                float charW = paint.MeasureText(charStr);
                                if (charIdx > 0)
                                {
                                    string pair = word.Substring(charIdx - 1, 2);
                                    float pairW = paint.MeasureText(pair);
                                    float prevCharW = paint.MeasureText(word[charIdx - 1].ToString());
                                    wordW += (pairW - (prevCharW + charW));
                                }
                                wordW += charW;
                            }
                            if (wordW <= 0 && word.Length > 0) wordW = paint.TextSize * 0.5f * word.Length;

                            // Check Wrap for Word
                            if (!nowrap && currentX + wordW > currentXMax && currentX > currentXStart)
                            {
                                FenBrowser.Core.FenLogger.Info($"[INLINE-WRAP] Node={node.ParentNode?.GetHashCode()} Wrapping word '{word}' W={wordW} CurX={currentX} Max={currentXMax}", FenBrowser.Core.Logging.LogCategory.Layout);
                                FlushLine();
                            }

                            currentLineItems.Add(new LineItem
                            {
                                Node = node,
                                IsText = true,
                                X = currentX,
                                TextLine = new ComputedTextLine
                                {
                                    Text = word, // No forced space!
                                    Width = wordW,
                                    Height = lineHeight,
                                    Baseline = baselineOffset,
                                    Origin = new SKPoint(0, 0)
                                },
                                Ascent = baselineOffset,
                                Height = lineHeight,
                                VerticalAlign = style?.VerticalAlign
                            });
                            
                            FenBrowser.Core.FenLogger.Info($"[INLINE-WORD] word='{word}' W={wordW:F2} X={currentX:F2} LineH={lineHeight}", FenBrowser.Core.Logging.LogCategory.Layout);

                            currentX += wordW;
                            currentUnwrappedX += wordW;
                            minContentWidth = Math.Max(minContentWidth, wordW);
                            currentLineHeight = Math.Max(currentLineHeight, lineHeight);
                        }

                        // 2. Add Space (if this was a split separator)
                        if (i < words.Length - 1)
                        {
                             // FIX: Collapse leading whitespace at the start of the line
                             // If we are at the start (currentX == currentXStart), ignore this space.
                             if (Math.Abs(currentX - currentXStart) < 0.1f)
                             {
                                 continue;
                             }

                             // Check Wrap for Space (usually allowed to hang, but let's be strict for now or it disappears at EOL)
                             // If space doesn't fit, FlushLine. Next loop, it will be at Start of Line and skipped!
                             if (!nowrap && currentX + spaceW > currentXMax && currentX > currentXStart)
                             {
                                 FlushLine();
                                 continue; // Skip adding it because now we are at start of line
                             }

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
                        }
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


