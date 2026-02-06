using System;
using System.Collections.Generic;
using System.Linq;
using FenBrowser.FenEngine.Layout.Tree;
using SkiaSharp;
using FenBrowser.Core.Css;
using FenBrowser.Core;
using FenBrowser.Core.Dom.V2;
using FenBrowser.FenEngine.Layout;
using FenBrowser.FenEngine.Typography;
using System.Globalization;
using System.Text.RegularExpressions;

namespace FenBrowser.FenEngine.Layout.Contexts
{
    public class InlineFormattingContext : FormattingContext
    {
        private static InlineFormattingContext _instance;
        public static InlineFormattingContext Instance => _instance ??= new InlineFormattingContext();

        // Font service for accurate text measurement
        private static readonly SkiaFontService _fontService = new SkiaFontService();
        private static readonly Regex CollapsibleWhitespace = new Regex("\\s+", RegexOptions.Compiled);

        // Line Box Structure
        private class LineBox
        {
            public float Width = 0;
            public float Height = 0;
            public float Baseline = 0;
            public List<LayoutBox> Items = new List<LayoutBox>();
        }

        // Track text lines for each original TextLayoutBox
        private class TextLineInfo
        {
            public string Text;
            public float X;      // Position within line
            public float Width;
            public float Height;
            public float Baseline;
            public int LineIndex; // Which line this segment is on
        }

        // Flatten inline tree to get all text boxes and atomic inlines in document order
        private void FlattenInlineChildren(LayoutBox box, List<LayoutBox> result)
        {
            foreach (var child in box.Children)
            {
                if (child.IsOutOfFlow) continue;
                // Keep inline element wrappers in flow so styled inlines
                // (anchors, controls, icon wrappers) retain geometry.
                result.Add(child);
            }
        }

        public override void Layout(LayoutBox box, LayoutState state)
        {
            if (TryLayoutReplacedInlineBox(box, state))
            {
                return;
            }

            ResolveContextWidth(box, state);

            float contentLimit = box.Geometry.ContentBox.Width;
            // Robustness: Handle unconstrained width (shrink-to-fit root)
            if (float.IsInfinity(contentLimit)) contentLimit = state.ViewportWidth;
            if (float.IsNaN(contentLimit)) contentLimit = 800f; // Safe fallback

            float startX = (float)box.Geometry.Padding.Left + (float)box.Geometry.Border.Left;
            float startY = (float)box.Geometry.Padding.Top + (float)box.Geometry.Border.Top;

            var lines = new List<LineBox>();
            var currentLine = new LineBox();
            lines.Add(currentLine);

            // Flatten inline tree to get all text and atomic inlines
            var flattenedChildren = new List<LayoutBox>();
            FlattenInlineChildren(box, flattenedChildren);

            // Track line segments per original TextLayoutBox
            var textBoxLines = new Dictionary<TextLayoutBox, List<TextLineInfo>>();
            var nonTextChildren = new List<LayoutBox>();

            float curX = 0;
            bool previousEndedWithSpace = true;

            foreach (var child in flattenedChildren)
            {
                state.Deadline?.Check();

                if (child is TextLayoutBox textBox)
                {
                    string fullText = (textBox.SourceNode as Text)?.Data ?? "";
                    fullText = CollapseWhitespace(fullText);
                    if (fullText.Length == 0)
                    {
                        ResetTextBoxGeometry(textBox);
                        continue;
                    }

                    // Collapse adjacent whitespace across inline text nodes and
                    // suppress leading line whitespace.
                    if (previousEndedWithSpace && fullText[0] == ' ')
                    {
                        fullText = fullText.Substring(1);
                    }
                    if (curX <= 0f && fullText.Length > 0 && fullText[0] == ' ')
                    {
                        fullText = fullText.TrimStart(' ');
                    }
                    if (fullText.Length == 0)
                    {
                        ResetTextBoxGeometry(textBox);
                        continue;
                    }

                    if (!textBoxLines.ContainsKey(textBox))
                        textBoxLines[textBox] = new List<TextLineInfo>();

                    // Measure once for height/baseline
                    var metrics = MeasureString("Hg", textBox.ComputedStyle);
                    float lineHeight = metrics.Height;
                    float baseline = lineHeight * 0.8f; // Approximate baseline

                    // WORD FLOW - track segments for this textBox
                    int startIdx = 0;
                    int currentLineStartIdx = 0;
                    float currentLineStartX = curX;

                    while (startIdx < fullText.Length)
                    {
                        // Find next word break
                        int nextSpace = fullText.IndexOf(' ', startIdx);
                        int endIdx = (nextSpace == -1) ? fullText.Length : nextSpace + 1;
                        string word = fullText.Substring(startIdx, endIdx - startIdx);
                        float wordWidth = MeasureString(word, textBox.ComputedStyle).Width;

                        if (curX + wordWidth > contentLimit && curX > 0)
                        {
                            // Save segment for current line before breaking
                            if (startIdx > currentLineStartIdx)
                            {
                                string segmentText = fullText.Substring(currentLineStartIdx, startIdx - currentLineStartIdx);
                                float segmentWidth = MeasureString(segmentText, textBox.ComputedStyle).Width;
                                textBoxLines[textBox].Add(new TextLineInfo
                                {
                                    Text = segmentText,
                                    X = currentLineStartX,
                                    Width = segmentWidth,
                                    Height = lineHeight,
                                    Baseline = baseline,
                                    LineIndex = lines.Count - 1
                                });
                            }

                            currentLine.Height = Math.Max(currentLine.Height, lineHeight);
                            currentLine = new LineBox();
                            lines.Add(currentLine);
                            curX = 0;
                            currentLineStartIdx = startIdx;
                            currentLineStartX = 0;
                        }

                        currentLine.Width = curX + wordWidth;
                        currentLine.Height = Math.Max(currentLine.Height, lineHeight);
                        curX += wordWidth;
                        startIdx = endIdx;
                    }

                    // Save final segment
                    if (startIdx > currentLineStartIdx)
                    {
                        string segmentText = fullText.Substring(currentLineStartIdx, startIdx - currentLineStartIdx);
                        float segmentWidth = MeasureString(segmentText, textBox.ComputedStyle).Width;
                        textBoxLines[textBox].Add(new TextLineInfo
                        {
                            Text = segmentText,
                            X = currentLineStartX,
                            Width = segmentWidth,
                            Height = lineHeight,
                            Baseline = baseline,
                            LineIndex = lines.Count - 1
                        });
                    }

                    previousEndedWithSpace = fullText.EndsWith(" ", StringComparison.Ordinal);
                }
                else
                {
                    // Atomic Inline (inline-block, images, inputs, etc.)
                    nonTextChildren.Add(child);
                    SKSize childSize = MeasureInlineChild(child, state);
                    if (curX + childSize.Width > contentLimit && curX > 0)
                    {
                        currentLine = new LineBox();
                        lines.Add(currentLine);
                        curX = 0;
                    }

                    if (child.Geometry == null) child.Geometry = new BoxModel();
                    child.Geometry.ContentBox = new SKRect(curX, 0, curX + childSize.Width, childSize.Height);
                    SyncBoxes(child.Geometry);

                    currentLine.Items.Add(child);
                    currentLine.Width = curX + childSize.Width;
                    currentLine.Height = Math.Max(currentLine.Height, childSize.Height);
                    curX += childSize.Width;
                    previousEndedWithSpace = false;
                }
            }

            // Calculate line Y positions
            var textAlign = box.ComputedStyle?.TextAlign ?? SKTextAlign.Left;
            float curY = 0;
            var lineYPositions = new List<float>();
            var lineXOffsets = new List<float>();

            foreach (var line in lines)
            {
                float xOffset = 0;
                if (textAlign == SKTextAlign.Center) xOffset = (contentLimit - line.Width) / 2f;
                else if (textAlign == SKTextAlign.Right) xOffset = (contentLimit - line.Width);

                lineYPositions.Add(curY);
                lineXOffsets.Add(xOffset);
                curY += line.Height;
            }

            // Populate Lines on each original TextLayoutBox and position them
            // Per CSS 2.1 Section 9.4.2: Line boxes are stacked with no vertical separation
            foreach (var kvp in textBoxLines)
            {
                var textBox = kvp.Key;
                var segments = kvp.Value;

                if (segments.Count == 0)
                {
                    ResetTextBoxGeometry(textBox);
                    continue;
                }

                // Ensure Geometry exists
                if (textBox.Geometry == null) textBox.Geometry = new BoxModel();

                // Initialize Lines list
                textBox.Geometry.Lines = new List<ComputedTextLine>();

                float minX = float.MaxValue, minY = float.MaxValue;
                float maxX = float.MinValue, maxY = float.MinValue;

                foreach (var seg in segments)
                {
                    float lineY = lineYPositions[seg.LineIndex];
                    float lineXOffset = lineXOffsets[seg.LineIndex];

                    // Calculate position relative to parent's content area
                    float segX = seg.X + lineXOffset;
                    float segY = lineY;

                    minX = Math.Min(minX, segX);
                    minY = Math.Min(minY, segY);
                    maxX = Math.Max(maxX, segX + seg.Width);
                    maxY = Math.Max(maxY, segY + seg.Height);
                }

                // TextLayoutBox doesn't have margin/padding/border, so content dimensions = box dimensions
                float boxWidth = maxX - minX;
                float boxHeight = maxY - minY;

                // Set up box model with proper dimensions
                // Content starts at origin (0,0), we'll position with SetPosition
                textBox.Geometry.ContentBox = new SKRect(0, 0, boxWidth, boxHeight);
                textBox.Geometry.Padding = new Thickness();
                textBox.Geometry.Border = new Thickness();
                textBox.Geometry.Margin = new Thickness();
                SyncBoxes(textBox.Geometry);

                // Position the TextLayoutBox relative to parent's content area
                // (children are positioned relative to parent content box, not border box)
                LayoutBoxOps.SetPosition(textBox, minX, minY);

                // Now populate Lines with origins relative to the TextLayoutBox's ContentBox
                foreach (var seg in segments)
                {
                    float lineY = lineYPositions[seg.LineIndex];
                    float lineXOffset = lineXOffsets[seg.LineIndex];

                    // Origin relative to TextLayoutBox's ContentBox
                    float relX = seg.X + lineXOffset - minX;
                    float relY = lineY - minY;

                    textBox.Geometry.Lines.Add(new ComputedTextLine
                    {
                        Text = seg.Text,
                        Origin = new SKPoint(relX, relY),
                        Width = seg.Width,
                        Height = seg.Height,
                        Baseline = seg.Baseline
                    });
                }
            }

            // Position non-text children (atomic inlines)
            // Per CSS 2.1: Inline-level boxes are laid out horizontally within line boxes
            foreach (var line in lines)
            {
                int lineIdx = lines.IndexOf(line);
                float lineY = lineYPositions[lineIdx];
                float xOffset = lineXOffsets[lineIdx];

                foreach (var item in line.Items)
                {
                    // Item's ContentBox.Left was set relative to start of line during measurement
                    // Apply text-align offset and position relative to parent's content area
                    float itemX = xOffset + item.Geometry.ContentBox.Left;
                    float itemY = lineY;

                    // If an atomic inline was probed under a different available width, re-layout it
                    // with its final measured width so descendants (e.g., CENTER > INPUT) are consistent.
                    if (item.Children.Count > 0)
                    {
                        var itemState = state.Clone();
                        float itemW = Math.Max(0f, item.Geometry.ContentBox.Width);
                        float itemH = item.Geometry.ContentBox.Height;
                        if (!float.IsFinite(itemH) || itemH <= 0f)
                        {
                            itemH = float.IsFinite(state.AvailableSize.Height) && state.AvailableSize.Height > 0
                                ? state.AvailableSize.Height
                                : state.ViewportHeight;
                        }

                        itemState.AvailableSize = new SKSize(itemW, itemH);
                        itemState.ContainingBlockWidth = itemW;
                        itemState.ContainingBlockHeight = itemH;
                        FormattingContext.Resolve(item).Layout(item, itemState);
                    }

                    LayoutBoxOps.SetPosition(item, itemX, itemY);
                }
            }

            // Keep original children (don't replace with fragments)
            // Already done - we didn't modify box.Children

            float totalContentHeight = lineYPositions.Count > 0 ? lineYPositions[lineYPositions.Count - 1] + lines[lines.Count - 1].Height : 0;

            // Final Height
            float finalContentHeight = Math.Max(0f, totalContentHeight);

            float maxLineWidth = 0f;
            foreach (var line in lines) maxLineWidth = Math.Max(maxLineWidth, line.Width);
            
            // SHRINK-TO-FIT: If width was auto and available was infinity, we shrink to widest line.
            // Also guard against relayouts that pass a finite available width that is smaller than
            // the line width; keep the box wide enough to contain the measured text.
            float finalContentWidth = box.Geometry.ContentBox.Width;
            if (box.ComputedStyle != null && !box.ComputedStyle.Width.HasValue)
            {
                if (float.IsInfinity(state.AvailableSize.Width))
                {
                    finalContentWidth = maxLineWidth;
                }
                else if (maxLineWidth > finalContentWidth)
                {
                    finalContentWidth = maxLineWidth;
                }
            }

            if (box.ComputedStyle != null && box.ComputedStyle.Height.HasValue)
                finalContentHeight = (float)box.ComputedStyle.Height.Value;

            box.Geometry.ContentBox = new SKRect(
                box.Geometry.ContentBox.Left,
                box.Geometry.ContentBox.Top,
                box.Geometry.ContentBox.Left + finalContentWidth,
                box.Geometry.ContentBox.Top + finalContentHeight
            );
            
            SyncBoxes(box.Geometry);
        }

        private bool TryLayoutReplacedInlineBox(LayoutBox box, LayoutState state)
        {
            if (box.SourceNode is not Element element)
            {
                return false;
            }

            string tag = element.TagName?.ToUpperInvariant() ?? string.Empty;
            if (tag != "IMG" && tag != "SVG" && tag != "CANVAS" && tag != "IFRAME" && tag != "OBJECT" && tag != "VIDEO" &&
                tag != "INPUT" && tag != "TEXTAREA" && tag != "BUTTON" && tag != "SELECT")
            {
                return false;
            }

            if (!TryGetIntrinsicSize(box, out var intrinsic))
            {
                return false;
            }

            if (box.Geometry == null)
            {
                box.Geometry = new BoxModel();
            }

            float contentWidth = intrinsic.Width;
            float contentHeight = intrinsic.Height;
            ApplyMinMaxConstraints(box.ComputedStyle, state, ref contentWidth, ref contentHeight);

            float left = box.Geometry.ContentBox.Left;
            float top = box.Geometry.ContentBox.Top;
            box.Geometry.ContentBox = new SKRect(left, top, left + contentWidth, top + contentHeight);
            box.Geometry.Padding = box.ComputedStyle?.Padding ?? new Thickness();
            box.Geometry.Border = box.ComputedStyle?.BorderThickness ?? new Thickness();
            box.Geometry.Margin = box.ComputedStyle?.Margin ?? new Thickness();
            SyncBoxes(box.Geometry);
            return true;
        }

        private SKSize MeasureInlineChild(LayoutBox child, LayoutState state)
        {
            if (child is TextLayoutBox textBox)
            {
                return MeasureString(textBox.TextContent, textBox.ComputedStyle);
            }
            else if (child is InlineBox inlineBox)
            {
                // Check if it's an atomic inline (inline-block etc)
                string display = ResolveDisplay(inlineBox);
                if (display == "none")
                {
                    return SKSize.Empty;
                }

                if (display != "inline")
                {
                    // Atomic inlines establish their own formatting context.
                    // Probe with unconstrained width so inline-blocks can shrink-to-fit
                    // and produce real geometry for descendants.
                    if (inlineBox.Geometry != null)
                    {
                        var probeState = state.Clone();
                        float probeHeight = float.IsFinite(state.AvailableSize.Height) && state.AvailableSize.Height > 0
                            ? state.AvailableSize.Height
                            : state.ViewportHeight;
                        probeState.AvailableSize = new SKSize(float.PositiveInfinity, probeHeight);
                        probeState.ContainingBlockWidth = float.IsFinite(state.AvailableSize.Width) && state.AvailableSize.Width > 0
                            ? state.AvailableSize.Width
                            : state.ViewportWidth;
                        probeState.ContainingBlockHeight = probeHeight;
                        FormattingContext.Resolve(inlineBox).Layout(inlineBox, probeState);

                        float probedWidth = inlineBox.Geometry.MarginBox.Width;
                        float probedHeight = inlineBox.Geometry.MarginBox.Height;
                        if (probedWidth > 0 && probedHeight >= 0)
                        {
                            return new SKSize(probedWidth, Math.Max(probedHeight, 0f));
                        }
                    }

                    float cw = (float)(inlineBox.ComputedStyle?.Width ?? 0);
                    float ch = (float)(inlineBox.ComputedStyle?.Height ?? 20);
                    if (cw <= 0 && TryGetIntrinsicSize(inlineBox, out var intrinsic))
                    {
                        cw = intrinsic.Width;
                        ch = Math.Max(ch, intrinsic.Height);
                    }

                    if (cw > 0) return AddNonContentSpacing(new SKSize(cw, ch), inlineBox.ComputedStyle);
                }

                // Normal inline (span) - aggregate children
                float w = 0;
                float h = 0;
                foreach (var c in inlineBox.Children)
                {
                    var sz = MeasureInlineChild(c, state);
                    w += sz.Width;
                    h = Math.Max(h, sz.Height);
                }

                if (w <= 0 && TryGetIntrinsicSize(inlineBox, out var inlineIntrinsic))
                {
                    w = inlineIntrinsic.Width;
                    h = Math.Max(h, inlineIntrinsic.Height);
                }

                if (h <= 0f)
                {
                    h = MeasureString("Hg", inlineBox.ComputedStyle).Height;
                }

                return AddNonContentSpacing(new SKSize(Math.Max(0f, w), h), inlineBox.ComputedStyle);
            }
            
            // Handle IMG, INPUT, SVG or other elements that result in generic LayoutBox
            if (child.Children.Count > 0)
            {
                var probeState = state.Clone();
                float probeHeight = float.IsFinite(state.AvailableSize.Height) && state.AvailableSize.Height > 0
                    ? state.AvailableSize.Height
                    : state.ViewportHeight;
                probeState.AvailableSize = new SKSize(float.PositiveInfinity, probeHeight);
                probeState.ContainingBlockWidth = float.IsFinite(state.AvailableSize.Width) && state.AvailableSize.Width > 0
                    ? state.AvailableSize.Width
                    : state.ViewportWidth;
                probeState.ContainingBlockHeight = probeHeight;
                FormattingContext.Resolve(child).Layout(child, probeState);

                float probedWidth = child.Geometry.MarginBox.Width;
                float probedHeight = child.Geometry.MarginBox.Height;
                if (probedWidth > 0 && probedHeight >= 0)
                {
                    return new SKSize(probedWidth, Math.Max(probedHeight, 0f));
                }
            }

            if (child.SourceNode is FenBrowser.Core.Dom.V2.Element el)
            {
                string t = el.TagName?.ToUpperInvariant();
                if (t == "IMG" || t == "INPUT" || t == "TEXTAREA" || t == "SVG" || t == "BUTTON" || t == "SELECT")
                {
                    float w = (float)(child.ComputedStyle?.Width ?? 0);
                    float h = (float)(child.ComputedStyle?.Height ?? 0);
                    
                    // Fallbacks if not specified
                    if (w <= 0) 
                    {
                        if (TryGetLengthAttribute(el, "width", out var attrWidth)) w = attrWidth;
                        else if (t == "INPUT") w = 150;
                        else if (t == "SVG") w = 24;
                        else if (t == "BUTTON") w = 60;
                        else if (t == "IMG") w = 300;
                        else w = 20;
                    }
                    if (h <= 0)
                    {
                        if (TryGetLengthAttribute(el, "height", out var attrHeight)) h = attrHeight;
                        else if (t == "INPUT") h = 24;
                        else if (t == "SVG") h = 24;
                        else if (t == "BUTTON") h = 24;
                        else if (t == "IMG") h = 150;
                        else h = 20;
                    }
                    ApplyMinMaxConstraints(child.ComputedStyle, state, ref w, ref h);
                    return AddNonContentSpacing(new SKSize(w, h), child.ComputedStyle);
                }
            }

            return new SKSize(10,10); // Minimal fallback for unknown items
        }

        private static string CollapseWhitespace(string text)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            return CollapsibleWhitespace.Replace(text, " ");
        }

        private static SKSize AddNonContentSpacing(SKSize content, CssComputed style)
        {
            var p = style?.Padding ?? new Thickness();
            var b = style?.BorderThickness ?? new Thickness();
            var m = style?.Margin ?? new Thickness();

            float totalW = content.Width + (float)(p.Left + p.Right + b.Left + b.Right + m.Left + m.Right);
            float totalH = content.Height + (float)(p.Top + p.Bottom + b.Top + b.Bottom + m.Top + m.Bottom);
            return new SKSize(Math.Max(0f, totalW), Math.Max(0f, totalH));
        }

        private string ResolveDisplay(LayoutBox box)
        {
            if (box.SourceNode is Element element)
            {
                if (element.HasAttribute("hidden"))
                {
                    return "none";
                }

                string tag = element.TagName?.ToUpperInvariant() ?? string.Empty;
                if (tag == "INPUT")
                {
                    string typeValue = element.GetAttribute("type")?.Trim();
                    if (string.Equals(typeValue, "hidden", StringComparison.OrdinalIgnoreCase))
                    {
                        return "none";
                    }
                }
            }

            string display = box.ComputedStyle?.Display?.Trim().ToLowerInvariant();
            if (!string.IsNullOrEmpty(display)) return display;

            if (box.SourceNode is Element elementNode)
            {
                string tag = elementNode.TagName?.ToUpperInvariant() ?? string.Empty;
                return tag switch
                {
                    "INPUT" or "SELECT" or "TEXTAREA" or "BUTTON" => "inline-block",
                    "IMG" or "SVG" or "CANVAS" or "IFRAME" or "OBJECT" or "A" or "SPAN" => "inline",
                    _ => "inline"
                };
            }

            return "inline";
        }

        private bool TryGetIntrinsicSize(LayoutBox box, out SKSize size)
        {
            size = SKSize.Empty;

            if (box.SourceNode is not Element el)
            {
                return false;
            }

            string tag = el.TagName?.ToUpperInvariant() ?? string.Empty;
            float w = (float)(box.ComputedStyle?.Width ?? 0);
            float h = (float)(box.ComputedStyle?.Height ?? 0);
            var padding = box.ComputedStyle?.Padding ?? new Thickness();
            bool hasPadding = padding.Left > 0 || padding.Right > 0 || padding.Top > 0 || padding.Bottom > 0;
            float defaultPaddingComp = hasPadding ? 0f : 24f;

            if (tag == "INPUT")
            {
                string type = (el.GetAttribute("type") ?? string.Empty).Trim().ToLowerInvariant();
                if (type == "hidden")
                {
                    size = SKSize.Empty;
                    return true;
                }

                if (type == "submit" || type == "button" || type == "reset")
                {
                    string label = el.GetAttribute("value");
                    if (string.IsNullOrWhiteSpace(label)) label = "Button";
                    if (w <= 0) w = MeasureString(label, box.ComputedStyle).Width + defaultPaddingComp;
                    if (h <= 0) h = 28f;
                }
                else
                {
                    if (w <= 0) w = 150f;
                    if (h <= 0) h = 24f;
                }
            }
            else if (tag == "BUTTON")
            {
                if (w <= 0)
                {
                    string label = el.TextContent?.Trim();
                    if (string.IsNullOrWhiteSpace(label)) label = "Button";
                    w = MeasureString(label, box.ComputedStyle).Width + defaultPaddingComp;
                }
                if (h <= 0) h = 28f;
            }
            else if (tag == "TEXTAREA")
            {
                if (w <= 0) w = 200f;
                if (h <= 0) h = 48f;
            }
            else if (tag == "SELECT")
            {
                if (w <= 0) w = 120f;
                if (h <= 0) h = 24f;
            }
            else if (tag == "IMG")
            {
                if (w <= 0 && TryGetLengthAttribute(el, "width", out var attrW)) w = attrW;
                if (h <= 0 && TryGetLengthAttribute(el, "height", out var attrH)) h = attrH;
                if (w <= 0) w = 300f;
                if (h <= 0) h = 150f;
            }
            else if (tag == "SVG")
            {
                if (w <= 0 && TryGetLengthAttribute(el, "width", out var attrW)) w = attrW;
                if (h <= 0 && TryGetLengthAttribute(el, "height", out var attrH)) h = attrH;
                if (w <= 0) w = 24f;
                if (h <= 0) h = 24f;
            }
            else if (tag == "CANVAS")
            {
                if (w <= 0 && TryGetLengthAttribute(el, "width", out var attrW)) w = attrW;
                if (h <= 0 && TryGetLengthAttribute(el, "height", out var attrH)) h = attrH;
                if (w <= 0) w = 300f;
                if (h <= 0) h = 150f;
            }
            else
            {
                return false;
            }

            size = new SKSize(Math.Max(0f, w), Math.Max(0f, h));
            return true;
        }

        private static void ApplyMinMaxConstraints(CssComputed style, LayoutState state, ref float width, ref float height)
        {
            if (style == null)
            {
                width = Math.Max(0f, width);
                height = Math.Max(0f, height);
                return;
            }

            float cbWidth = state.ContainingBlockWidth > 0 ? state.ContainingBlockWidth : state.ViewportWidth;
            float cbHeight = state.ContainingBlockHeight > 0 ? state.ContainingBlockHeight : state.ViewportHeight;

            float minW = 0f;
            if (style.MinWidth.HasValue) minW = (float)style.MinWidth.Value;
            else if (style.MinWidthPercent.HasValue) minW = (float)(style.MinWidthPercent.Value / 100.0 * cbWidth);
            else if (!string.IsNullOrEmpty(style.MinWidthExpression))
                minW = LayoutHelper.EvaluateCssExpression(style.MinWidthExpression, cbWidth, state.ViewportWidth, state.ViewportHeight);

            float maxW = float.PositiveInfinity;
            if (style.MaxWidth.HasValue) maxW = (float)style.MaxWidth.Value;
            else if (style.MaxWidthPercent.HasValue) maxW = (float)(style.MaxWidthPercent.Value / 100.0 * cbWidth);
            else if (!string.IsNullOrEmpty(style.MaxWidthExpression))
                maxW = LayoutHelper.EvaluateCssExpression(style.MaxWidthExpression, cbWidth, state.ViewportWidth, state.ViewportHeight);

            float minH = 0f;
            if (style.MinHeight.HasValue) minH = (float)style.MinHeight.Value;
            else if (style.MinHeightPercent.HasValue) minH = (float)(style.MinHeightPercent.Value / 100.0 * cbHeight);
            else if (!string.IsNullOrEmpty(style.MinHeightExpression))
                minH = LayoutHelper.EvaluateCssExpression(style.MinHeightExpression, cbHeight, state.ViewportWidth, state.ViewportHeight);

            float maxH = float.PositiveInfinity;
            if (style.MaxHeight.HasValue) maxH = (float)style.MaxHeight.Value;
            else if (style.MaxHeightPercent.HasValue) maxH = (float)(style.MaxHeightPercent.Value / 100.0 * cbHeight);
            else if (!string.IsNullOrEmpty(style.MaxHeightExpression))
                maxH = LayoutHelper.EvaluateCssExpression(style.MaxHeightExpression, cbHeight, state.ViewportWidth, state.ViewportHeight);

            width = Math.Max(minW, Math.Min(width, maxW));
            height = Math.Max(minH, Math.Min(height, maxH));
        }

        private void ResetTextBoxGeometry(TextLayoutBox textBox)
        {
            if (textBox.Geometry == null)
            {
                textBox.Geometry = new BoxModel();
            }

            float x = textBox.Geometry.ContentBox.Left;
            float y = textBox.Geometry.ContentBox.Top;
            textBox.Geometry.ContentBox = new SKRect(x, y, x, y);
            textBox.Geometry.Padding = new Thickness();
            textBox.Geometry.Border = new Thickness();
            textBox.Geometry.Margin = new Thickness();
            textBox.Geometry.Lines = new List<ComputedTextLine>();
            SyncBoxes(textBox.Geometry);
        }

        private static bool TryGetLengthAttribute(Element element, string attributeName, out float value)
        {
            value = 0f;
            string raw = element.GetAttribute(attributeName);
            if (string.IsNullOrWhiteSpace(raw))
            {
                return false;
            }

            raw = raw.Trim();
            int numericChars = 0;
            while (numericChars < raw.Length)
            {
                char ch = raw[numericChars];
                if ((ch >= '0' && ch <= '9') || ch == '.' || ch == '-')
                {
                    numericChars++;
                    continue;
                }

                break;
            }

            if (numericChars == 0)
            {
                return false;
            }

            string numeric = raw.Substring(0, numericChars);
            if (!float.TryParse(numeric, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
            {
                return false;
            }

            value = Math.Max(0f, value);
            return true;
        }

        private SKSize MeasureString(string text, CssComputed style)
        {
            float fontSize = 16f;
            if (style?.FontSize != null) fontSize = (float)style.FontSize.Value;
            fontSize = Math.Max(fontSize, 10f);

            int fontWeight = style?.FontWeight ?? 400;
            string fontFamily = style?.FontFamilyName ?? "sans-serif";

            if (string.IsNullOrEmpty(text))
            {
                return new SKSize(fontSize * 0.35f, fontSize * 1.2f);
            }

            float width = _fontService.MeasureTextWidth(text, fontFamily, fontSize, fontWeight);
            if (width <= 0)
            {
                width = Math.Max(fontSize * Math.Max(1, text.Length) * 0.35f, fontSize * 0.4f);
            }

            float? lineHeightOverride = style?.LineHeight.HasValue == true ? (float)style.LineHeight.Value : null;
            var metrics = _fontService.GetMetrics(fontFamily, fontSize, fontWeight, lineHeightOverride);
            float lineHeight = metrics.LineHeight;
            if (lineHeight <= 0)
            {
                lineHeight = fontSize * 1.25f;
            }

            return new SKSize(width, lineHeight);
        }


        private void ResolveContextWidth(LayoutBox box, LayoutState state)
        {
            float rawAvailable = state.AvailableSize.Width;
            bool widthUnconstrained = float.IsInfinity(rawAvailable) || float.IsNaN(rawAvailable);
            float available = widthUnconstrained ? state.ViewportWidth : rawAvailable;
            if (float.IsInfinity(available) || float.IsNaN(available) || available <= 0)
                available = 800f;

            float finalW = available;
            
            var p = box.ComputedStyle?.Padding ?? new Thickness();
            var b = box.ComputedStyle?.BorderThickness ?? new Thickness();
            var m = box.ComputedStyle?.Margin ?? new Thickness();

            float used = (float)(p.Left + p.Right + b.Left + b.Right + m.Left + m.Right);
            finalW = widthUnconstrained ? available : Math.Max(0f, rawAvailable - used);

            if (box.ComputedStyle != null && box.ComputedStyle.Width.HasValue)
            {
                finalW = (float)box.ComputedStyle.Width.Value;
            }

            float left = box.Geometry.ContentBox.Left;
            float top = box.Geometry.ContentBox.Top;
            box.Geometry.ContentBox = new SKRect(left, top, left + finalW, top);
            
            box.Geometry.Padding = p;
            box.Geometry.Border = b;
            box.Geometry.Margin = m;
            
            SyncBoxes(box.Geometry);
        }
        
        private void SyncBoxes(BoxModel geometry)
        {
             var cb = geometry.ContentBox;
            var p = geometry.Padding;
            var b = geometry.Border;
            var m = geometry.Margin;
            
            geometry.PaddingBox = new SKRect(
                cb.Left - (float)p.Left,
                cb.Top - (float)p.Top,
                cb.Right + (float)p.Right,
                cb.Bottom + (float)p.Bottom);
                
            geometry.BorderBox = new SKRect(
                geometry.PaddingBox.Left - (float)b.Left,
                geometry.PaddingBox.Top - (float)b.Top,
                geometry.PaddingBox.Right + (float)b.Right,
                geometry.PaddingBox.Bottom + (float)b.Bottom);
                
            geometry.MarginBox = new SKRect(
                geometry.BorderBox.Left - (float)m.Left,
                geometry.BorderBox.Top - (float)m.Top,
                geometry.BorderBox.Right + (float)m.Right,
                geometry.BorderBox.Bottom + (float)m.Bottom);
        }
    }
}


