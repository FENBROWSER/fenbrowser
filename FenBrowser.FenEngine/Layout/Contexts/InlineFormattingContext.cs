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

namespace FenBrowser.FenEngine.Layout.Contexts
{
    public class InlineFormattingContext : FormattingContext
    {
        private static InlineFormattingContext _instance;
        public static InlineFormattingContext Instance => _instance ??= new InlineFormattingContext();

        // Font service for accurate text measurement
        private static readonly SkiaFontService _fontService = new SkiaFontService();
        // Line Box Structure
        private class LineBox
        {
            public float Width = 0;
            public float Height = 0;
            public float Baseline = 0;
            public float Ascent = 0;
            public float Descent = 0;
            public List<LayoutBox> Items = new List<LayoutBox>();

            public void IncludeMetrics(float ascent, float descent)
            {
                Ascent = Math.Max(Ascent, Math.Max(0f, ascent));
                Descent = Math.Max(Descent, Math.Max(0f, descent));
                Baseline = Ascent;
                Height = Math.Max(Height, Ascent + Descent);
            }
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

        private readonly record struct InlineTextMetrics(float Width, float LineHeight, float Baseline, float Descent);

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

        protected override void LayoutCore(LayoutBox box, LayoutState state)
        {
            if (box.Geometry == null)
            {
                box.Geometry = new BoxModel();
            }

            box.Geometry.Padding = box.ComputedStyle?.Padding ?? new Thickness();
            box.Geometry.Border = box.ComputedStyle?.BorderThickness ?? new Thickness();
            box.Geometry.Margin = box.ComputedStyle?.Margin ?? new Thickness();

            // Formatting contexts should always compute local geometry from a clean origin.
            // Repeated relayout passes otherwise preserve stale subtree offsets and can
            // leave descendants in a previous coordinate space while the parent is moved.
            LayoutBoxOps.ResetSubtreeToOrigin(box);

            if (box is TextLayoutBox leafTextBox)
            {
                LayoutLeafTextBox(leafTextBox, state);
                return;
            }

            if (TryLayoutReplacedInlineBox(box, state))
            {
                return;
            }

            bool widthUnconstrained = float.IsInfinity(state.AvailableSize.Width) || float.IsNaN(state.AvailableSize.Width);
            bool hasExplicitWidth =
                box.ComputedStyle?.Width.HasValue == true ||
                box.ComputedStyle?.WidthPercent.HasValue == true ||
                !string.IsNullOrEmpty(box.ComputedStyle?.WidthExpression);
            bool isShrinkToFitProbe = widthUnconstrained && !hasExplicitWidth;

            ResolveContextWidth(box, state);

            float contentLimit = isShrinkToFitProbe
                ? float.PositiveInfinity
                : box.Geometry.ContentBox.Width;
            // Robustness: Handle unconstrained width (shrink-to-fit root)
            if (float.IsInfinity(contentLimit)) contentLimit = isShrinkToFitProbe ? 1000000f : state.ViewportWidth;
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
                    var metrics = MeasureTextMetrics("Hg", textBox.ComputedStyle);
                    float lineHeight = metrics.LineHeight;
                    float baseline = metrics.Baseline;
                    float descent = metrics.Descent;

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
                        currentLine.IncludeMetrics(baseline, descent);
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
                    var pad = child.ComputedStyle?.Padding ?? new Thickness();
                    var brd = child.ComputedStyle?.BorderThickness ?? new Thickness();
                    var mar = child.ComputedStyle?.Margin ?? new Thickness();
                    float nonContentW = (float)(pad.Left + pad.Right + brd.Left + brd.Right + mar.Left + mar.Right);
                    float nonContentH = (float)(pad.Top + pad.Bottom + brd.Top + brd.Bottom + mar.Top + mar.Bottom);
                    float contentW = Math.Max(0f, childSize.Width - nonContentW);
                    float contentH = Math.Max(0f, childSize.Height - nonContentH);

                    child.Geometry.ContentBox = new SKRect(curX, 0, curX + contentW, contentH);
                    child.Geometry.Padding = pad;
                    child.Geometry.Border = brd;
                    child.Geometry.Margin = mar;
                    SyncBoxes(child.Geometry);

                    currentLine.Items.Add(child);
                    currentLine.Width = curX + childSize.Width;

                    float itemHeightForMetrics = Math.Max(0f, childSize.Height);
                    float itemBaselineForMetrics = ResolveInlineItemBaseline(child, itemHeightForMetrics);
                    ResolveInlineItemLineMetrics(
                        currentLine,
                        itemHeightForMetrics,
                        itemBaselineForMetrics,
                        child.ComputedStyle,
                        out float itemAscentForLine,
                        out float itemDescentForLine);
                    currentLine.IncludeMetrics(itemAscentForLine, itemDescentForLine);
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
                if (!isShrinkToFitProbe)
                {
                    if (textAlign == SKTextAlign.Center) xOffset = (contentLimit - line.Width) / 2f;
                    else if (textAlign == SKTextAlign.Right) xOffset = (contentLimit - line.Width);
                }

                if (xOffset < 0f)
                {
                    // Guard against overflow-induced negative offsets from probe widths.
                    xOffset = 0f;
                }

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
                    var line = lines[seg.LineIndex];
                    float lineY = lineYPositions[seg.LineIndex];
                    float lineXOffset = lineXOffsets[seg.LineIndex];

                    // Calculate position relative to parent's content area
                    float segX = seg.X + lineXOffset;
                    float segY = lineY + ComputeVerticalAlignOffset(line, seg.Height, seg.Baseline, textBox.ComputedStyle);

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
                LayoutBoxOps.PositionSubtree(textBox, minX, minY, state);

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

                var firstLine = textBox.Geometry.Lines[0];
                textBox.Geometry.LineHeight = firstLine.Height;
                textBox.Geometry.Baseline = firstLine.Origin.Y + firstLine.Baseline;
                textBox.Geometry.Ascent = textBox.Geometry.Baseline;
                textBox.Geometry.Descent = Math.Max(0f, firstLine.Height - firstLine.Baseline);
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
                    var placementState = state;

                    // Item's ContentBox.Left was set relative to start of line during measurement
                    // Apply text-align offset and position relative to parent's content area
                    float itemX = xOffset + item.Geometry.ContentBox.Left;

                    // Re-layout atomic inlines with final available size so descendants and
                    // intrinsic controls resolve to stable final geometry before placement.
                    if (ShouldRelayoutAtomicInline(item))
                    {
                        var itemState = state.Clone();
                        float itemW = Math.Max(0f, item.Geometry.ContentBox.Width);
                        if (!float.IsFinite(itemW) || itemW <= 0f)
                        {
                            itemW = Math.Max(0f, item.Geometry.BorderBox.Width);
                        }
                        if (!float.IsFinite(itemW) || itemW <= 0f)
                        {
                            itemW = Math.Max(0f, contentLimit);
                        }

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
                        ResetInlineProbeOrigin(item);
                        FormattingContext.Resolve(item).Layout(item, itemState);
                        placementState = itemState;
                    }

                    float itemHeight = item.Geometry.MarginBox.Height;
                    if (!float.IsFinite(itemHeight) || itemHeight <= 0f)
                    {
                        itemHeight = Math.Max(0f, item.Geometry.BorderBox.Height);
                    }
                    if (!float.IsFinite(itemHeight) || itemHeight <= 0f)
                    {
                        itemHeight = Math.Max(0f, item.Geometry.ContentBox.Height);
                    }
                    if (!float.IsFinite(itemHeight) || itemHeight <= 0f)
                    {
                        itemHeight = line.Height;
                    }

                    float itemBaseline = ResolveInlineItemBaseline(item, itemHeight);
                    float itemY = lineY + ComputeVerticalAlignOffset(line, itemHeight, itemBaseline, item.ComputedStyle);

                    LayoutBoxOps.PositionSubtree(item, itemX, itemY, placementState);
                }

                // If atomic inline descendants changed width during final re-layout,
                // keep line items monotonic to avoid visual overlaps.
                float runningRight = float.NegativeInfinity;
                foreach (var item in line.Items)
                {
                    float itemLeft = item.Geometry.MarginBox.Left;
                    if (float.IsFinite(runningRight) && itemLeft < runningRight)
                    {
                        LayoutBoxOps.PositionSubtree(item, runningRight, item.Geometry.MarginBox.Top, state);
                    }

                    if (float.IsFinite(item.Geometry.MarginBox.Right))
                    {
                        runningRight = Math.Max(runningRight, item.Geometry.MarginBox.Right);
                    }
                }
            }

            // Keep original children (don't replace with fragments)
            // Already done - we didn't modify box.Children

            float totalContentHeight = lineYPositions.Count > 0 ? lineYPositions[lineYPositions.Count - 1] + lines[lines.Count - 1].Height : 0;

            // Some inline formatting cases, especially atomic inline-blocks with numeric
            // vertical-align, can extend below the synthesized line stack. Clamp the
            // content height to the actual laid out descendant bottoms so following block
            // flow sees the full consumed height.
            float actualContentBottom = 0f;
            foreach (var child in flattenedChildren)
            {
                if (child?.Geometry == null)
                {
                    continue;
                }

                float bottom = child.Geometry.MarginBox.Bottom;
                if (!float.IsFinite(bottom))
                {
                    bottom = child.Geometry.BorderBox.Bottom;
                }

                if (!float.IsFinite(bottom))
                {
                    bottom = child.Geometry.ContentBox.Bottom;
                }

                if (float.IsFinite(bottom))
                {
                    actualContentBottom = Math.Max(actualContentBottom, bottom);
                }
            }

            // Final Height
            float finalContentHeight = Math.Max(0f, Math.Max(totalContentHeight, actualContentBottom));

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

            ApplyMinMaxConstraints(box.ComputedStyle, state, ref finalContentWidth, ref finalContentHeight);

            box.Geometry.ContentBox = new SKRect(
                box.Geometry.ContentBox.Left,
                box.Geometry.ContentBox.Top,
                box.Geometry.ContentBox.Left + finalContentWidth,
                box.Geometry.ContentBox.Top + finalContentHeight
            );

            if (lines.Count > 0)
            {
                box.Geometry.LineHeight = lines[0].Height;
                box.Geometry.Baseline = lines[0].Baseline;
                box.Geometry.Ascent = lines[0].Ascent;
                box.Geometry.Descent = lines[0].Descent;
            }
            
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
                tag != "INPUT" && tag != "TEXTAREA" && tag != "SELECT")
            {
                return false;
            }

            if (tag == "OBJECT" && ReplacedElementSizing.ShouldUseObjectFallbackContent(element))
            {
                return false;
            }

            if (!TryGetIntrinsicSize(box, state, out var intrinsic))
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

        private void LayoutLeafTextBox(TextLayoutBox textBox, LayoutState state)
        {
            if (textBox.Geometry == null)
            {
                textBox.Geometry = new BoxModel();
            }

            string text = NormalizeIsolatedText(textBox.TextContent);
            if (text.Length == 0)
            {
                ResetTextBoxGeometry(textBox);
                return;
            }

            var metrics = MeasureTextMetrics(text, textBox.ComputedStyle);
            float contentWidth = metrics.Width;
            float contentHeight = metrics.LineHeight;
            ApplyMinMaxConstraints(textBox.ComputedStyle, state, ref contentWidth, ref contentHeight);

            textBox.Geometry.ContentBox = new SKRect(0, 0, contentWidth, contentHeight);
            textBox.Geometry.Padding = new Thickness();
            textBox.Geometry.Border = new Thickness();
            textBox.Geometry.Margin = new Thickness();
            textBox.Geometry.Lines = new List<ComputedTextLine>
            {
                new ComputedTextLine
                {
                    Text = text,
                    Origin = new SKPoint(0, 0),
                    Width = contentWidth,
                    Height = contentHeight,
                    Baseline = Math.Min(contentHeight, Math.Max(0f, metrics.Baseline))
                }
            };

            textBox.Geometry.LineHeight = contentHeight;
            textBox.Geometry.Baseline = textBox.Geometry.Lines[0].Baseline;
            textBox.Geometry.Ascent = textBox.Geometry.Baseline;
            textBox.Geometry.Descent = Math.Max(0f, contentHeight - textBox.Geometry.Baseline);

            SyncBoxes(textBox.Geometry);
        }

        private SKSize MeasureInlineChild(LayoutBox child, LayoutState state)
        {
            if (TryMeasureAtomicInlineReplacedChild(child, state, out var replacedSize))
            {
                return replacedSize;
            }

            if (child is TextLayoutBox textBox)
            {
                string text = NormalizeIsolatedText(textBox.TextContent);
                if (text.Length == 0)
                {
                    return SKSize.Empty;
                }

                return MeasureString(text, textBox.ComputedStyle);
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
                            : (state.ContainingBlockWidth > 0 ? state.ContainingBlockWidth : state.ViewportWidth);
                        probeState.ContainingBlockHeight = probeHeight;
                        ResetInlineProbeOrigin(inlineBox);
                        FormattingContext.Resolve(inlineBox).Layout(inlineBox, probeState);

                        float probedWidth = inlineBox.Geometry.MarginBox.Width;
                        float probedHeight = inlineBox.Geometry.MarginBox.Height;
                        if (probedWidth > 0 && probedHeight >= 0)
                        {
                            return new SKSize(probedWidth, Math.Max(probedHeight, 0f));
                        }
                    }

                    float cw = (float)(inlineBox.ComputedStyle?.Width ?? 0);
                    float ch = (float)(inlineBox.ComputedStyle?.Height ?? 0);
                    if (cw <= 0 && TryGetIntrinsicSize(inlineBox, state, out var intrinsic))
                    {
                        cw = intrinsic.Width;
                        ch = Math.Max(ch, intrinsic.Height);
                    }

                    if (cw > 0 || ch > 0)
                    {
                        return AddNonContentSpacing(new SKSize(Math.Max(0f, cw), Math.Max(0f, ch)), inlineBox.ComputedStyle);
                    }

                    // Empty atomic inlines should size from their own chrome only.
                    return AddNonContentSpacing(SKSize.Empty, inlineBox.ComputedStyle);
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

                if (w <= 0 && TryGetIntrinsicSize(inlineBox, state, out var inlineIntrinsic))
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
                    : (state.ContainingBlockWidth > 0 ? state.ContainingBlockWidth : state.ViewportWidth);
                probeState.ContainingBlockHeight = probeHeight;
                ResetInlineProbeOrigin(child);
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

        private bool TryMeasureAtomicInlineReplacedChild(LayoutBox child, LayoutState state, out SKSize size)
        {
            size = SKSize.Empty;

            if (child?.SourceNode is not Element element)
            {
                return false;
            }

            string tag = element.TagName?.ToUpperInvariant() ?? string.Empty;
            if (!ReplacedElementSizing.IsReplacedElementTag(tag))
            {
                return false;
            }

            if (!TryGetIntrinsicSize(child, state, out var intrinsic))
            {
                return false;
            }

            float width = intrinsic.Width;
            float height = intrinsic.Height;
            ApplyMinMaxConstraints(child.ComputedStyle, state, ref width, ref height);

            size = AddNonContentSpacing(new SKSize(Math.Max(0f, width), Math.Max(0f, height)), child.ComputedStyle);
            return size.Width > 0f && size.Height >= 0f;
        }

        private static bool ShouldRelayoutAtomicInline(LayoutBox item)
        {
            if (item == null)
            {
                return false;
            }

            if (item.Children.Count > 0)
            {
                return true;
            }

            if (item.SourceNode is Element element)
            {
                string tag = element.TagName?.ToUpperInvariant() ?? string.Empty;
                return tag == "INPUT" ||
                       tag == "BUTTON" ||
                       tag == "SELECT" ||
                       tag == "TEXTAREA" ||
                       tag == "IMG" ||
                       tag == "SVG" ||
                       tag == "CANVAS" ||
                       tag == "IFRAME" ||
                       tag == "OBJECT" ||
                       tag == "VIDEO";
            }

            return false;
        }

        private static void ResetInlineProbeOrigin(LayoutBox box)
        {
            LayoutBoxOps.ResetSubtreeToOrigin(box);
        }

        private static string CollapseWhitespace(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }

            var builder = new System.Text.StringBuilder(text.Length);
            bool pendingCollapsibleSpace = false;

            foreach (char ch in text)
            {
                if (TextWhitespaceClassifier.IsCollapsibleWhitespaceChar(ch))
                {
                    pendingCollapsibleSpace = true;
                    continue;
                }

                if (pendingCollapsibleSpace)
                {
                    builder.Append(' ');
                    pendingCollapsibleSpace = false;
                }

                builder.Append(ch);
            }

            if (pendingCollapsibleSpace)
            {
                builder.Append(' ');
            }

            return builder.ToString();
        }

        private static string NormalizeIsolatedText(string text)
        {
            return CollapseWhitespace(text).Trim(' ');
        }

        private static float ComputeVerticalAlignOffset(LineBox line, float itemHeight, float itemAscent, CssComputed style)
        {
            float safeHeight = float.IsFinite(itemHeight) && itemHeight > 0f ? itemHeight : 0f;
            float safeAscent = float.IsFinite(itemAscent) && itemAscent >= 0f ? itemAscent : safeHeight;
            if (safeHeight > 0f)
            {
                safeAscent = Math.Min(safeAscent, safeHeight);
            }
            float safeDescent = Math.Max(0f, safeHeight - safeAscent);
            float lineAscent = Math.Max(0f, line?.Ascent ?? 0f);
            float lineDescent = Math.Max(0f, line?.Descent ?? 0f);
            float baseOffset = lineAscent - safeAscent;
            string verticalAlign = style?.VerticalAlign;

            if (string.IsNullOrWhiteSpace(verticalAlign))
            {
                return Math.Max(0f, baseOffset);
            }

            float verticalOffset = 0f;
            string value = verticalAlign.Trim().ToLowerInvariant();
            switch (value)
            {
                case "sub":
                    verticalOffset = safeHeight * 0.2f;
                    break;
                case "super":
                    verticalOffset = -safeHeight * 0.3f;
                    break;
                case "baseline":
                    verticalOffset = 0f;
                    break;
                case "middle":
                    verticalOffset = ((lineAscent + lineDescent - safeHeight) / 2f) - baseOffset;
                    break;
                case "top":
                case "text-top":
                    verticalOffset = -(lineAscent - safeAscent);
                    break;
                case "bottom":
                case "text-bottom":
                    verticalOffset = lineDescent - safeDescent;
                    break;
                default:
                    if (TryResolveNumericVerticalAlignShift(value, style, lineAscent + lineDescent, out var parsedShift))
                    {
                        verticalOffset = -parsedShift;
                    }
                    break;
            }

            float resolved = baseOffset + verticalOffset;
            if (string.Equals(value, "baseline", StringComparison.OrdinalIgnoreCase))
            {
                return Math.Max(0f, resolved);
            }

            return resolved;
        }

        private static void ResolveInlineItemLineMetrics(
            LineBox line,
            float itemHeight,
            float itemBaseline,
            CssComputed style,
            out float ascent,
            out float descent)
        {
            float safeHeight = float.IsFinite(itemHeight) && itemHeight > 0f ? itemHeight : 0f;
            float safeBaseline = float.IsFinite(itemBaseline) && itemBaseline >= 0f
                ? Math.Min(itemBaseline, safeHeight)
                : safeHeight;

            ascent = safeBaseline;
            descent = Math.Max(0f, safeHeight - safeBaseline);

            string verticalAlign = style?.VerticalAlign;
            if (string.IsNullOrWhiteSpace(verticalAlign))
            {
                return;
            }

            if (TryResolveNumericVerticalAlignShift(
                verticalAlign.Trim().ToLowerInvariant(),
                style,
                Math.Max(0f, line?.Ascent ?? 0f) + Math.Max(0f, line?.Descent ?? 0f),
                out float baselineRaise))
            {
                ascent = Math.Max(0f, ascent + baselineRaise);
                descent = Math.Max(0f, descent - baselineRaise);
            }
        }

        private static bool TryResolveNumericVerticalAlignShift(string value, CssComputed style, float lineHeight, out float shift)
        {
            shift = 0f;
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            float lengthBasis = ResolveVerticalAlignLengthBasis(style);

            if (value.EndsWith("px", StringComparison.OrdinalIgnoreCase) &&
                float.TryParse(value[..^2], NumberStyles.Float, CultureInfo.InvariantCulture, out var pxValue))
            {
                shift = pxValue;
                return true;
            }

            if (value.EndsWith("em", StringComparison.OrdinalIgnoreCase) &&
                float.TryParse(value[..^2], NumberStyles.Float, CultureInfo.InvariantCulture, out var emValue))
            {
                shift = emValue * lengthBasis;
                return true;
            }

            if (value.EndsWith("%", StringComparison.OrdinalIgnoreCase) &&
                float.TryParse(value[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var pctValue))
            {
                shift = (pctValue / 100f) * ResolveVerticalAlignPercentageBasis(style, lineHeight, lengthBasis);
                return true;
            }

            return false;
        }

        private static float ResolveVerticalAlignLengthBasis(CssComputed style)
        {
            float fontSize = (float)(style?.FontSize ?? 16.0);
            if (!float.IsFinite(fontSize) || fontSize <= 0f)
            {
                return 16f;
            }

            return fontSize;
        }

        private static float ResolveVerticalAlignPercentageBasis(CssComputed style, float lineHeight, float fallback)
        {
            float specifiedLineHeight = (float)(style?.LineHeight ?? 0.0);
            if (float.IsFinite(specifiedLineHeight) && specifiedLineHeight > 0f)
            {
                return specifiedLineHeight;
            }

            if (float.IsFinite(lineHeight) && lineHeight > 0f)
            {
                return lineHeight;
            }

            return fallback;
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

        private bool TryGetIntrinsicSize(LayoutBox box, LayoutState state, out SKSize size)
        {
            size = SKSize.Empty;

            if (box.SourceNode is not Element el)
            {
                return false;
            }

            string tag = el.TagName?.ToUpperInvariant() ?? string.Empty;
            float w = (float)(box.ComputedStyle?.Width ?? 0);
            float h = (float)(box.ComputedStyle?.Height ?? 0);
            float cbWidth = state.ContainingBlockWidth > 0 ? state.ContainingBlockWidth : state.ViewportWidth;
            float cbHeight = state.ContainingBlockHeight > 0 ? state.ContainingBlockHeight : state.ViewportHeight;
            if (w <= 0f && box.ComputedStyle?.WidthPercent.HasValue == true && cbWidth > 0f)
            {
                w = (float)(box.ComputedStyle.WidthPercent.Value / 100.0 * cbWidth);
            }

            if (h <= 0f && box.ComputedStyle?.HeightPercent.HasValue == true && cbHeight > 0f)
            {
                h = (float)(box.ComputedStyle.HeightPercent.Value / 100.0 * cbHeight);
            }

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
                    string label = LayoutHelper.GetRenderableTextContentTrimmed(el);
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
            else if (ReplacedElementSizing.IsReplacedElementTag(tag))
            {
                if (tag == "OBJECT" && ReplacedElementSizing.ShouldUseObjectFallbackContent(el))
                {
                    return false;
                }

                float attrW = 0f;
                float attrH = 0f;
                ReplacedElementSizing.TryGetLengthAttribute(el, "width", out attrW);
                ReplacedElementSizing.TryGetLengthAttribute(el, "height", out attrH);
                float intrinsicW = 0f;
                float intrinsicH = 0f;
                ReplacedElementSizing.TryResolveIntrinsicSizeFromElement(tag, el, out intrinsicW, out intrinsicH);

                var resolved = ReplacedElementSizing.ResolveReplacedSize(
                    tag,
                    box.ComputedStyle,
                    new SKSize(float.PositiveInfinity, float.PositiveInfinity),
                    intrinsicW,
                    intrinsicH,
                    attrW,
                    attrH,
                    constrainAutoToAvailableWidth: false);

                if (w <= 0) w = resolved.Width;
                if (h <= 0) h = resolved.Height;
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
            textBox.Geometry.LineHeight = 0f;
            textBox.Geometry.Baseline = 0f;
            textBox.Geometry.Ascent = 0f;
            textBox.Geometry.Descent = 0f;
            SyncBoxes(textBox.Geometry);
        }

        private static float ResolveInlineItemBaseline(LayoutBox item, float fallbackHeight)
        {
            if (item?.Geometry == null)
            {
                return fallbackHeight;
            }

            if (item.Geometry.Ascent > 0f)
            {
                float offset = item.Geometry.ContentBox.Top - item.Geometry.MarginBox.Top;
                return offset + item.Geometry.Ascent;
            }

            if (TryResolveInlineBaselineFromLines(item.Geometry, out var lineBaseline))
            {
                return lineBaseline;
            }

            if (TryResolveInlineBaselineFromDescendants(item, out var descendantBaseline))
            {
                return descendantBaseline;
            }

            return fallbackHeight;
        }

        private static bool TryResolveInlineBaselineFromLines(BoxModel geometry, out float baseline)
        {
            baseline = 0f;
            if (geometry?.Lines == null || geometry.Lines.Count == 0)
            {
                return false;
            }

            float contentOffset = geometry.ContentBox.Top - geometry.MarginBox.Top;
            var lastLine = geometry.Lines[geometry.Lines.Count - 1];
            baseline = contentOffset + lastLine.Origin.Y + lastLine.Baseline;
            return float.IsFinite(baseline) && baseline >= 0f;
        }

        private static bool TryResolveInlineBaselineFromDescendants(LayoutBox item, out float baseline)
        {
            baseline = 0f;
            if (item?.Children == null || item.Geometry == null)
            {
                return false;
            }

            float bestBaseline = float.MinValue;
            float itemTop = item.Geometry.MarginBox.Top;

            foreach (var child in item.Children)
            {
                if (child?.Geometry == null)
                {
                    continue;
                }

                float childBaseline;
                bool found = false;

                if (child.Geometry.Ascent > 0f)
                {
                    childBaseline = child.Geometry.ContentBox.Top - itemTop + child.Geometry.Ascent;
                    found = true;
                }
                else if (TryResolveInlineBaselineFromLines(child.Geometry, out childBaseline))
                {
                    childBaseline += child.Geometry.MarginBox.Top - itemTop;
                    found = true;
                }
                else if (TryResolveInlineBaselineFromDescendants(child, out childBaseline))
                {
                    childBaseline += child.Geometry.MarginBox.Top - itemTop;
                    found = true;
                }
                else
                {
                    childBaseline = 0f;
                }

                if (found && float.IsFinite(childBaseline))
                {
                    bestBaseline = Math.Max(bestBaseline, childBaseline);
                }
            }

            if (bestBaseline > float.MinValue)
            {
                baseline = bestBaseline;
                return true;
            }

            return false;
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
            var metrics = MeasureTextMetrics(text, style);
            return new SKSize(metrics.Width, metrics.LineHeight);
        }

        private InlineTextMetrics MeasureTextMetrics(string text, CssComputed style)
        {
            float fontSize = 16f;
            if (style?.FontSize != null) fontSize = (float)style.FontSize.Value;
            fontSize = Math.Max(fontSize, 10f);

            int fontWeight = style?.FontWeight ?? 400;
            string fontFamily = style?.FontFamilyName ?? "sans-serif";

            if (string.IsNullOrEmpty(text))
            {
                var emptyLineHeight = fontSize * 1.2f;
                var emptyBaseline = emptyLineHeight * 0.8f;
                return new InlineTextMetrics(fontSize * 0.35f, emptyLineHeight, emptyBaseline, Math.Max(0f, emptyLineHeight - emptyBaseline));
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

            float baseline = metrics.GetBaselineOffset();
            if (!float.IsFinite(baseline) || baseline <= 0f)
            {
                baseline = lineHeight * 0.8f;
            }

            baseline = Math.Min(lineHeight, baseline);
            float descent = Math.Max(0f, lineHeight - baseline);
            return new InlineTextMetrics(width, lineHeight, baseline, descent);
        }


        private void ResolveContextWidth(LayoutBox box, LayoutState state)
        {
            var widthResolution = LayoutConstraintResolver.ResolveWidth(state, "Inline.ResolveContextWidth", 800f);
            float rawAvailable = widthResolution.RawAvailable;
            bool widthUnconstrained = widthResolution.IsUnconstrained;
            float available = widthResolution.ResolvedAvailable;

            float finalW = available;
            
            var p = box.ComputedStyle?.Padding ?? new Thickness();
            var b = box.ComputedStyle?.BorderThickness ?? new Thickness();
            var m = box.ComputedStyle?.Margin ?? new Thickness();

            float used = (float)(p.Left + p.Right + b.Left + b.Right + m.Left + m.Right);
            finalW = widthUnconstrained ? Math.Max(0f, available - used) : Math.Max(0f, rawAvailable - used);

            if (box.ComputedStyle != null && box.ComputedStyle.Width.HasValue)
            {
                finalW = (float)box.ComputedStyle.Width.Value;
            }
            else if (box.ComputedStyle != null && box.ComputedStyle.WidthPercent.HasValue)
            {
                float cbWidth = state.ContainingBlockWidth > 0 ? state.ContainingBlockWidth : state.ViewportWidth;
                if (cbWidth > 0f)
                {
                    finalW = (float)(box.ComputedStyle.WidthPercent.Value / 100.0 * cbWidth);
                }
            }
            else if (box.ComputedStyle != null && !string.IsNullOrEmpty(box.ComputedStyle.WidthExpression))
            {
                float cbWidth = state.ContainingBlockWidth > 0 ? state.ContainingBlockWidth : state.ViewportWidth;
                finalW = LayoutHelper.EvaluateCssExpression(
                    box.ComputedStyle.WidthExpression,
                    cbWidth,
                    state.ViewportWidth,
                    state.ViewportHeight);
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


