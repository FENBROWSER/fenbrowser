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
using System.Text;

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

        private readonly record struct BalancedTextLine(string Text, float Width);

        private readonly record struct InlineWrapperFragment(int LineIndex, float XStart, float Width, float Height);

        private readonly record struct InlineTextMetrics(float Width, float LineHeight, float Baseline, float Descent);

        // Per-style font metrics cache. lineHeight/baseline/descent depend only on
        // the computed style, not on the measured text, so we avoid the LRU lookup
        // + string-keyed dictionary in SkiaFontService for every text box. Holding
        // weak refs prevents the cache from outliving the styles themselves.
        private sealed class StyleFontInfo
        {
            public string FontFamily;
            public float FontSize;
            public int FontWeight;
            public float LineHeight;
            public float Baseline;
            public float Descent;
        }

        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<CssComputed, StyleFontInfo> s_styleFontCache = new();

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

            // Anonymous blocks wrap inline runs inside block containers and must not
            // carry the parent's padding/border/margin — they are purely grouping wrappers
            // per CSS 2.1 §9.2.1.1. Inheriting e.g. a parent's 14px padding + 6px border
            // would add ~20px of spurious space around every text run.
            bool isAnonymousBlock = box is AnonymousBlockBox;
            if (isAnonymousBlock)
            {
                box.Geometry.Padding = new Thickness();
                box.Geometry.Border = new Thickness();
                box.Geometry.Margin = new Thickness();
            }
            else
            {
                box.Geometry.Padding = box.ComputedStyle?.Padding ?? new Thickness();
                box.Geometry.Border = box.ComputedStyle?.BorderThickness ?? new Thickness();
                box.Geometry.Margin = box.ComputedStyle?.Margin ?? new Thickness();
            }

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

            // Vertical writing mode (writing-mode: vertical-rl / vertical-lr):
            // text flows top-to-bottom in columns that advance right-to-left (rl)
            // or left-to-right (lr). Text-only containers take a dedicated
            // vertical layout path; mixed content falls back to horizontal flow.
            if (IsVerticalWritingMode(box.ComputedStyle?.WritingMode) &&
                TryLayoutVerticalTextContent(box, state))
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

            float contentLimit = ResolveLineContentLimit(box, state, isShrinkToFitProbe, isAnonymousBlock);
            // Robustness: Handle unconstrained width (shrink-to-fit root)
            if (float.IsInfinity(contentLimit)) contentLimit = isShrinkToFitProbe ? 1000000f : state.ViewportWidth;
            if (float.IsNaN(contentLimit)) contentLimit = 800f; // Safe fallback

            float startX = (float)box.Geometry.Padding.Left + (float)box.Geometry.Border.Left;
            float startY = (float)box.Geometry.Padding.Top + (float)box.Geometry.Border.Top;

            // Float avoidance: when this IFC participates in an ancestor BFC with
            // active floats, each line box must shorten around float intrusions
            // (CSS 2.1 §9.5). The FloatManager and origin offsets are threaded
            // through LayoutState by the parent BFC.
            bool hasFloatAvoidance = state.FloatManager != null && state.FloatManager.HasFloats;
            float floatOriginX = state.FloatManager != null
                ? state.FloatOriginX + startX
                : 0f;

            var lines = new List<LineBox>();
            var currentLine = new LineBox();
            lines.Add(currentLine);

            // Collect out-of-flow children before flattening — they must be
            // laid out separately (same pattern as BlockFormattingContext).
            var outOfFlow = new List<LayoutBox>();
            foreach (var child in box.Children)
            {
                if (child.IsOutOfFlow)
                {
                    outOfFlow.Add(child);
                }
            }

            // Flatten inline tree to get all text and atomic inlines
            var flattenedChildren = new List<LayoutBox>();
            FlattenInlineChildren(box, flattenedChildren);

            // Track line segments per original TextLayoutBox
            var textBoxLines = new Dictionary<TextLayoutBox, List<TextLineInfo>>();
            var nonTextChildren = new List<LayoutBox>();

            float curX = 0;
            bool previousEndedWithSpace = true;

            // CSS 2.1 §9.5: each line box must shorten around floats in the
            // ancestor BFC. Compute the float-adjusted content limit and the
            // left-edge offset for the first line.
            float effectiveContentLimit = contentLimit;
            float floatLineStartAdjust = 0f;
            if (hasFloatAvoidance)
            {
                float accumulatedLineHeights = 0f;
                var (floatStartX, floatLimit) = ResolveFloatAdjustedLineSpace(
                    state.FloatManager,
                    state.FloatOriginY + startY + accumulatedLineHeights,
                    GetStyleFontInfo(box.ComputedStyle ?? new FenBrowser.Core.Css.CssComputed()).LineHeight,
                    floatOriginX,
                    contentLimit,
                    state.ContainingBlockWidth);
                floatLineStartAdjust = floatStartX;
                effectiveContentLimit = floatLimit;
                curX = floatStartX;
            }

            // Local helper: recompute float-adjusted line space when advancing to
            // the next line. accumulatedLineHeights is captured from the outer scope
            // and updated as lines are committed.
            float accumulatedLineHeightsForFloat = 0f;
            System.Action recomputeFloatAdjustedLine = () =>
            {
                if (!hasFloatAvoidance) return;
                accumulatedLineHeightsForFloat += currentLine.Height;
                var (floatStartX, floatLimit) = ResolveFloatAdjustedLineSpace(
                    state.FloatManager,
                    state.FloatOriginY + startY + accumulatedLineHeightsForFloat,
                    Math.Max(1f, GetStyleFontInfo(box.ComputedStyle ?? new FenBrowser.Core.Css.CssComputed()).LineHeight),
                    floatOriginX,
                    contentLimit,
                    state.ContainingBlockWidth);
                floatLineStartAdjust = floatStartX;
                effectiveContentLimit = floatLimit;
            };

            // --- Inline fragmentation support (LAYOUT-002) ---
            // Styled inline wrappers (display:inline) no longer act as atomic units.
            // Their descendant text leaves participate in word flow, and wrapper
            // geometry is derived afterwards from the fragments they contributed.
            var wrapperFragmentOrder = new List<LayoutBox>();
            var wrapperFragments = new Dictionary<LayoutBox, List<InlineWrapperFragment>>();
            var finalizedWrapperRects = new Dictionary<LayoutBox, SKRect>();
            var activeWrapperStack = new List<LayoutBox>();

            void RecordWrapperFragment(LayoutBox wrapperBox, int lineIndex, float xStart, float width, float height)
            {
                if (wrapperBox == null) return;
                if (!wrapperFragments.TryGetValue(wrapperBox, out var list))
                {
                    list = new List<InlineWrapperFragment>();
                    wrapperFragments[wrapperBox] = list;
                    wrapperFragmentOrder.Add(wrapperBox);
                }
                list.Add(new InlineWrapperFragment(lineIndex, xStart, width, height));
            }

            bool HasDescendantTextBox(LayoutBox container)
            {
                foreach (var nested in container.Children)
                {
                    if (nested.IsOutOfFlow) continue;
                    if (nested is TextLayoutBox) return true;
                    if (HasDescendantTextBox(nested)) return true;
                }
                return false;
            }

            bool IsFragmentableInlineWrapper(LayoutBox candidate)
            {
                if (!(candidate.SourceNode is FenBrowser.Core.Dom.V2.Element el)) return false;
                var display = candidate.ComputedStyle?.Display?.ToLowerInvariant();
                if (display != "inline") return false;

                // Replaced/media/form elements never fragment.
                switch (el.TagName?.ToUpperInvariant())
                {
                    case "OBJECT":
                        // HTML §4.8.7: an <object> showing its fallback content is not a
                        // replaced element — it is an ordinary inline box whose fallback
                        // children flow in the surrounding line (width/height don't apply).
                        // Acid2's eyes are three nested objects: two fallbacks around an
                        // image object that must right-align in the line as one inline run.
                        return ReplacedElementSizing.ShouldUseObjectFallbackContent(el) &&
                               candidate.Children.Any(static child => !child.IsOutOfFlow);
                    case "IMG": case "CANVAS": case "IFRAME": case "EMBED":
                    case "INPUT": case "TEXTAREA": case "SELECT": case "BUTTON": case "SVG":
                    case "VIDEO": case "AUDIO": case "PICTURE":
                        return false;
                }

                return HasDescendantTextBox(candidate);
            }

            void SyncWrapperFragmentsForTextBox(TextLayoutBox tb, int segmentCountBefore)
            {
                if (activeWrapperStack.Count == 0) return;
                var segments = textBoxLines[tb];
                for (int i = segmentCountBefore; i < segments.Count; i++)
                {
                    var segment = segments[i];
                    for (int w = 0; w < activeWrapperStack.Count; w++)
                    {
                        RecordWrapperFragment(activeWrapperStack[w], segment.LineIndex, segment.X, segment.Width, segment.Height);
                    }
                }
            }

            void InsertForcedLineBreak(LayoutBox brBox)
            {
                // ECMA/HTML: <br> always forces a line break regardless of
                // white-space mode. Use the inherited font metrics so
                // consecutive <br><br> produces a visible blank line.
                var brInfo = GetStyleFontInfo(brBox.ComputedStyle ?? box.ComputedStyle);
                currentLine.Height = Math.Max(currentLine.Height, brInfo.LineHeight);
                currentLine.IncludeMetrics(brInfo.Baseline, brInfo.Descent);
                currentLine = new LineBox();
                lines.Add(currentLine);
                curX = 0;
                previousEndedWithSpace = true;
            }

            void PlaceAtomicInline(LayoutBox atomicChild)
            {
                nonTextChildren.Add(atomicChild);
                SKSize childSize = MeasureInlineChild(atomicChild, state);
                if (curX + childSize.Width > effectiveContentLimit && curX > 0)
                {
                    recomputeFloatAdjustedLine();
                    currentLine = new LineBox();
                    lines.Add(currentLine);
                    curX = floatLineStartAdjust;
                }

                if (atomicChild.Geometry == null) atomicChild.Geometry = new BoxModel();
                var pad = atomicChild.ComputedStyle?.Padding ?? new Thickness();
                var brd = atomicChild.ComputedStyle?.BorderThickness ?? new Thickness();
                var mar = atomicChild.ComputedStyle?.Margin ?? new Thickness();
                float nonContentW = (float)(pad.Left + pad.Right + brd.Left + brd.Right + mar.Left + mar.Right);
                float nonContentH = (float)(pad.Top + pad.Bottom + brd.Top + brd.Bottom + mar.Top + mar.Bottom);
                float contentW = Math.Max(0f, childSize.Width - nonContentW);
                float contentH = Math.Max(0f, childSize.Height - nonContentH);

                atomicChild.Geometry.ContentBox = new SKRect(curX, 0, curX + contentW, contentH);
                atomicChild.Geometry.Padding = pad;
                atomicChild.Geometry.Border = brd;
                atomicChild.Geometry.Margin = mar;
                LayoutBoxOps.SyncBoxes(atomicChild.Geometry);

                for (int w = 0; w < activeWrapperStack.Count; w++)
                {
                    RecordWrapperFragment(
                        activeWrapperStack[w],
                        lines.Count - 1,
                        atomicChild.Geometry.ContentBox.Left,
                        childSize.Width,
                        childSize.Height);
                }

                currentLine.Items.Add(atomicChild);
                currentLine.Width = curX + childSize.Width;

                float itemHeightForMetrics = Math.Max(0f, childSize.Height);
                float itemBaselineForMetrics = ResolveInlineItemBaseline(atomicChild, itemHeightForMetrics);
                if (IsLineRelativeVerticalAlign(atomicChild.ComputedStyle?.VerticalAlign))
                {
                    // CSS 2.1 §10.8.1: `top`/`bottom` boxes don't take part in the
                    // baseline-relative line height; they are aligned to the finished
                    // line box and only enlarge it when taller (Acid2's eye objects sit
                    // on the bottom of a 2em line without pushing its baseline down).
                    currentLine.Height = Math.Max(currentLine.Height, Math.Max(0f, itemHeightForMetrics));
                }
                else
                {
                    ResolveInlineItemLineMetrics(
                        currentLine,
                        itemHeightForMetrics,
                        itemBaselineForMetrics,
                        atomicChild.ComputedStyle,
                        out float itemAscentForLine,
                        out float itemDescentForLine);
                    currentLine.IncludeMetrics(itemAscentForLine, itemDescentForLine);
                }
                curX += childSize.Width;
                previousEndedWithSpace = false;
            }

            void LayoutTextBoxInFlow(TextLayoutBox textBox)
            {
                // Ruby annotation (RT) text is painted by the ruby handler
                // above the base run; it must not consume inline space here.
                if (IsRubyAnnotationTextNode(textBox))
                {
                    ResetTextBoxGeometry(textBox);
                    return;
                }

                int segmentCountBefore = textBoxLines.TryGetValue(textBox, out var existingSegments)
                    ? existingSegments.Count
                    : 0;

                string rawText = (textBox.SourceNode as Text)?.Data ?? "";
                string wsMode = (textBox.ComputedStyle?.WhiteSpace ?? box.ComputedStyle?.WhiteSpace ?? "normal").Trim().ToLowerInvariant();
                bool wsPreservesNewlines = wsMode == "pre" || wsMode == "pre-wrap" || wsMode == "pre-line";
                bool wsPreservesSpaces = wsMode == "pre" || wsMode == "pre-wrap";

                // CSS white-space: when newlines are preserved, treat each '\n'
                // as a forced line break and lay out each segment independently.
                var textSegments = wsPreservesNewlines
                    ? rawText.Replace("\r\n", "\n").Split('\n')
                    : new[] { rawText };

                if (!textBoxLines.ContainsKey(textBox))
                    textBoxLines[textBox] = new List<TextLineInfo>();

                for (int segIdx = 0; segIdx < textSegments.Length; segIdx++)
                {
                    if (segIdx > 0)
                    {
                        // Forced line break from the preceding '\n'.
                        var segInfo = GetStyleFontInfo(textBox.ComputedStyle);
                        currentLine.Height = Math.Max(currentLine.Height, segInfo.LineHeight);
                        currentLine.IncludeMetrics(segInfo.Baseline, segInfo.Descent);
                        recomputeFloatAdjustedLine();
                        currentLine = new LineBox();
                        lines.Add(currentLine);
                        curX = floatLineStartAdjust;
                        previousEndedWithSpace = true;
                    }

                    string fullText = textSegments[segIdx];
                    fullText = wsPreservesSpaces ? fullText : CollapseWhitespace(fullText);
                    if (fullText.Length == 0)
                    {
                        if (!wsPreservesNewlines)
                        {
                            ResetTextBoxGeometry(textBox);
                        }
                        continue;
                    }

                    // Collapse adjacent whitespace across inline text nodes and
                    // suppress leading line whitespace (skipped when CSS preserves spaces).
                    if (!wsPreservesSpaces)
                    {
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
                            continue;
                        }
                    }

                    bool suppressSoftWrap = UsesNoWrapWhiteSpace(textBox.ComputedStyle ?? box.ComputedStyle);

                    // Per-style metrics (lineHeight/baseline/descent) only — no probe
                    // text needed; resolved+cached on first sight of the style.
                    var info = GetStyleFontInfo(textBox.ComputedStyle);
                    float lineHeight = info.LineHeight;
                    float baseline = info.Baseline;
                    float descent = info.Descent;

                    if (suppressSoftWrap)
                    {
                        float segmentWidth = MeasureString(fullText, textBox.ComputedStyle).Width;
                        if (curX + segmentWidth > effectiveContentLimit && curX > 0)
                        {
                            currentLine.Height = Math.Max(currentLine.Height, lineHeight);
                            recomputeFloatAdjustedLine();
                            currentLine = new LineBox();
                            lines.Add(currentLine);
                            curX = floatLineStartAdjust;
                        }

                        textBoxLines[textBox].Add(new TextLineInfo
                        {
                            Text = fullText,
                            X = curX,
                            Width = segmentWidth,
                            Height = lineHeight,
                            Baseline = baseline,
                            LineIndex = lines.Count - 1
                        });

                        currentLine.Width = curX + segmentWidth;
                        currentLine.IncludeMetrics(baseline, descent);
                        curX += segmentWidth;
                        previousEndedWithSpace = fullText.EndsWith(" ", StringComparison.Ordinal);
                        continue;
                    }

                    // FAST PATH: if the entire collapsed text fits on the remaining
                    // space of the current line, emit it as a single segment.
                    if (!isShrinkToFitProbe)
                    {
                        float wholeWidth = MeasureString(fullText, textBox.ComputedStyle).Width;
                        if (curX + wholeWidth <= effectiveContentLimit + 0.5f)
                        {
                            textBoxLines[textBox].Add(new TextLineInfo
                            {
                                Text = fullText,
                                X = curX,
                                Width = wholeWidth,
                                Height = lineHeight,
                                Baseline = baseline,
                                LineIndex = lines.Count - 1
                            });
                            currentLine.Width = curX + wholeWidth;
                            currentLine.IncludeMetrics(baseline, descent);
                            curX += wholeWidth;
                            previousEndedWithSpace = fullText.EndsWith(" ", StringComparison.Ordinal);
                            continue;
                        }
                    }

                    // WORD FLOW - track segments for this textBox
                    // Wrapping is a property of the inline formatting container. The
                    // generated text-box style does not carry non-inherited values such
                    // as text-wrap-style, so consult the container first.
                    var textWrapStyle = box.ComputedStyle ?? textBox.ComputedStyle;
                    if (curX <= 0.5f &&
                        UsesBalancedTextWrap(textWrapStyle) &&
                        TryBuildBalancedTextLines(fullText, textWrapStyle, contentLimit, out var balancedLines))
                    {
                        for (int balancedIdx = 0; balancedIdx < balancedLines.Count; balancedIdx++)
                        {
                            var balancedLine = balancedLines[balancedIdx];
                            textBoxLines[textBox].Add(new TextLineInfo
                            {
                                Text = balancedLine.Text,
                                X = 0f,
                                Width = balancedLine.Width,
                                Height = lineHeight,
                                Baseline = baseline,
                                LineIndex = lines.Count - 1
                            });

                            currentLine.Width = balancedLine.Width;
                            currentLine.IncludeMetrics(baseline, descent);
                            curX = balancedLine.Width;

                            if (balancedIdx < balancedLines.Count - 1)
                            {
                                currentLine.Height = Math.Max(currentLine.Height, lineHeight);
                                currentLine = new LineBox();
                                lines.Add(currentLine);
                                curX = 0f;
                            }
                        }

                        previousEndedWithSpace = fullText.EndsWith(" ", StringComparison.Ordinal);
                        continue;
                    }

                    int startIdx = 0;
                    int currentLineStartIdx = 0;
                    float currentLineStartX = curX;
                    bool allowBreakAnywhere = AllowsBreakAnywhere(textBox.ComputedStyle) ||
                                               AllowsBreakAnywhere(box.ComputedStyle);

                    while (startIdx < fullText.Length)
                    {
                        // Find next soft wrap boundary. Browsers allow a normal
                        // line break after hyphens, e.g. "background-color".
                        int endIdx = allowBreakAnywhere
                            ? FindNextCodePointEnd(fullText, startIdx)
                            : FindNextSoftWrapEnd(fullText, startIdx);
                        string word = fullText.Substring(startIdx, endIdx - startIdx);
                        float wordWidth = MeasureString(word, textBox.ComputedStyle).Width;

                        if (curX + wordWidth > effectiveContentLimit && curX > 0)
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
                            recomputeFloatAdjustedLine();
                            currentLine = new LineBox();
                            lines.Add(currentLine);
                            // CSS 2.1 §9.5: the next line box starts past whatever float
                            // intrudes at its own vertical position.
                            curX = floatLineStartAdjust;
                            currentLineStartIdx = startIdx;
                            currentLineStartX = curX;
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
                } // end for textSegments

                if (textBoxLines[textBox].Count == 0)
                {
                    ResetTextBoxGeometry(textBox);
                }

                SyncWrapperFragmentsForTextBox(textBox, segmentCountBefore);
            }

            void LayoutInlineSubtreeFragments(LayoutBox container)
            {
                foreach (var nested in container.Children)
                {
                    state.Deadline?.Check();

                    if (nested.IsOutOfFlow) continue;

                    if (nested is TextLayoutBox nestedTextBox)
                    {
                        LayoutTextBoxInFlow(nestedTextBox);
                        continue;
                    }

                    if (nested.SourceNode is Element nestedAnnotation &&
                        IsRubyAnnotationElement(nestedAnnotation))
                    {
                        ResetInlineSubtreeGeometry(nested);
                        continue;
                    }

                    if (nested.SourceNode is Element nestedBr &&
                        string.Equals(nestedBr.TagName, "BR", StringComparison.OrdinalIgnoreCase))
                    {
                        InsertForcedLineBreak(nested);
                        continue;
                    }

                    if (IsFragmentableInlineWrapper(nested))
                    {
                        LayoutFragmentableInlineWrapper(nested);
                        continue;
                    }

                    PlaceAtomicInline(nested);
                }
            }

            void LayoutFragmentableInlineWrapper(LayoutBox wrapperBox)
            {
                activeWrapperStack.Add(wrapperBox);
                try
                {
                    LayoutInlineSubtreeFragments(wrapperBox);
                }
                finally
                {
                    activeWrapperStack.RemoveAt(activeWrapperStack.Count - 1);
                }
            }

            foreach (var child in flattenedChildren)
            {
                state.Deadline?.Check();

                if (child is TextLayoutBox textBox)
                {
                    LayoutTextBoxInFlow(textBox);
                    continue;
                }

                // Ruby annotation (RT/RTC) wrappers are painted above the base
                // run by the ruby handler; they consume no inline space and
                // must not be measured or placed as atomic inlines.
                if (child.SourceNode is Element annotationElement &&
                    IsRubyAnnotationElement(annotationElement))
                {
                    ResetInlineSubtreeGeometry(child);
                    continue;
                }

                if (child.SourceNode is Element brEl &&
                    string.Equals(brEl.TagName, "BR", StringComparison.OrdinalIgnoreCase))
                {
                    InsertForcedLineBreak(child);
                    continue;
                }

                // Fragmentable styled inline: recurse so descendant text leaves
                // flow across line boxes instead of the wrapper acting atomically.
                if (IsFragmentableInlineWrapper(child))
                {
                    LayoutFragmentableInlineWrapper(child);
                    continue;
                }

                // Atomic Inline (inline-block, images, inputs, etc.)
                PlaceAtomicInline(child);
            }

            // CSS text-overflow: ellipsis (CSS Overflow 3 §5). After line construction,
            // truncate overflowing lines and append the ellipsis glyph (U+2026).
            // Only applies when overflow is hidden/clip/scroll/auto — visible overflow
            // does not trigger ellipsis.
            ApplyTextOverflow(
                box, lines, textBoxLines, contentLimit, isShrinkToFitProbe,
                hasFloatAvoidance ? effectiveContentLimit : contentLimit);

            // Remove any empty segments left by text-overflow truncation
            foreach (var kvp in textBoxLines)
            {
                kvp.Value.RemoveAll(seg =>
                    string.IsNullOrEmpty(seg.Text) && seg.Width <= 0f);
            }

            // CSS 2.1 §10.8.1: every line box starts with the block container's strut —
            // a zero-width inline box carrying the block's own font and line-height.
            // A line of smaller-font content is therefore never shorter than the
            // strut, and its baseline sits where the block's text would (an <img> in a
            // 20em-font cell hangs from that tall baseline; Acid2 lines 12 and 14).
            // Lines that never received content stay empty so the block stays 0 tall.
            var strut = GetStyleFontInfo(box.ComputedStyle ?? new FenBrowser.Core.Css.CssComputed());
            var lineHasContent = new bool[lines.Count];
            for (int li = 0; li < lines.Count; li++)
            {
                lineHasContent[li] = lines[li].Items.Count > 0 || lines[li].Height > 0f;
            }
            foreach (var kvp in textBoxLines)
            {
                foreach (var seg in kvp.Value)
                {
                    if (seg.LineIndex >= 0 && seg.LineIndex < lineHasContent.Length)
                    {
                        lineHasContent[seg.LineIndex] = true;
                    }
                }
            }
            for (int li = 0; li < lines.Count; li++)
            {
                if (lineHasContent[li])
                {
                    lines[li].IncludeMetrics(strut.Baseline, strut.Descent);
                }
            }

            // Calculate line Y positions
            var textAlign = box.ComputedStyle?.TextAlign ?? SKTextAlign.Left;
            float curY = 0;
            var lineYPositions = new List<float>(lines.Count);
            var lineXOffsets = new List<float>(lines.Count);

            foreach (var line in lines)
            {
                float xOffset = 0;
                if (!isShrinkToFitProbe)
                {
                    float alignLimit = hasFloatAvoidance ? effectiveContentLimit : contentLimit;
                    if (textAlign == SKTextAlign.Center) xOffset = (alignLimit - line.Width) / 2f;
                    else if (textAlign == SKTextAlign.Right) xOffset = (alignLimit - line.Width);
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
                textBox.Geometry.Lines = new List<ComputedTextLine>(segments.Count);

                float minX = float.MaxValue, minY = float.MaxValue;
                float maxX = float.MinValue, maxY = float.MinValue;
                float maxSegmentWidth = 0f;
                int firstLineIndex = segments[0].LineIndex;
                bool singleLineSegmentSet = true;

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
                    maxSegmentWidth = Math.Max(maxSegmentWidth, seg.Width);
                    if (seg.LineIndex != firstLineIndex)
                    {
                        singleLineSegmentSet = false;
                    }

                    var segParent = (textBox.SourceNode as Text)?.ParentElement;
                    string segParentClass = segParent?.ClassName ?? string.Empty;
                    string segGrandParentClass = segParent?.ParentElement?.ClassName ?? string.Empty;
                    bool isGoogleSignInSegment =
                        segParentClass.IndexOf("gb_0", StringComparison.OrdinalIgnoreCase) >= 0 &&
                        segGrandParentClass.IndexOf("gb_A", StringComparison.OrdinalIgnoreCase) >= 0;
                    if (isGoogleSignInSegment)
                    {
                        FenBrowser.Core.EngineLogCompat.Info(
                            $"[GOOGLE-SIGNIN-INLINE] phase=extents pcls={segParentClass} gpcls={segGrandParentClass} text='{seg.Text}' segX={segX:F1} segY={segY:F1} segW={seg.Width:F1} lineXOffset={lineXOffset:F1} minX={minX:F1} maxX={maxX:F1} contentLimit={contentLimit:F1}",
                            FenBrowser.Core.Logging.LogCategory.Layout);
                    }
                }

                float sideBearingSlack = ComputeInlineTextSideBearingSlack(textBox, segments, singleLineSegmentSet, minX, maxX, maxSegmentWidth);
                if (sideBearingSlack > 0f)
                {
                    minX -= sideBearingSlack;
                    maxX += sideBearingSlack;
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
                LayoutBoxOps.SyncBoxes(textBox.Geometry);

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

                    var lineParent = (textBox.SourceNode as Text)?.ParentElement;
                    string lineParentClass = lineParent?.ClassName ?? string.Empty;
                    string lineGrandParentClass = lineParent?.ParentElement?.ClassName ?? string.Empty;
                    bool isGoogleSignInLineSegment =
                        lineParentClass.IndexOf("gb_0", StringComparison.OrdinalIgnoreCase) >= 0 &&
                        lineGrandParentClass.IndexOf("gb_A", StringComparison.OrdinalIgnoreCase) >= 0;
                    if (isGoogleSignInLineSegment)
                    {
                        FenBrowser.Core.EngineLogCompat.Info(
                            $"[GOOGLE-SIGNIN-INLINE] phase=lines pcls={lineParentClass} gpcls={lineGrandParentClass} text='{seg.Text}' relX={relX:F1} relY={relY:F1} segX={seg.X:F1} lineXOffset={lineXOffset:F1} boxW={boxWidth:F1} minX={minX:F1}",
                            FenBrowser.Core.Logging.LogCategory.Layout);
                    }
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
                        // Add slop only for text-bearing inline items to absorb
                        // probe's whole-string MeasureString and the per-word
                        // measurements during re-layout — without it short labels
                        // spuriously wrap to two lines because per-word widths
                        // can sum to a hair more than the cached probe width.
                        bool needsTextProbeGuard = TryMeasureInlineLabelContent(item, out _, out _);
                        // The available width must be the item's outer (margin-box) size:
                        // the inner layout subtracts padding/border from it again, so using
                        // the content-box width here makes padded controls (buttons) wrap.
                        float itemW = MathF.Ceiling(Math.Max(0f, item.Geometry.MarginBox.Width)) + (needsTextProbeGuard ? 2f : 0f);
                        if (!float.IsFinite(itemW) || itemW <= 0f)
                        {
                            itemW = Math.Max(0f, item.Geometry.BorderBox.Width);
                        }
                        if (!float.IsFinite(itemW) || itemW <= 0f)
                        {
                            itemW = Math.Max(0f, contentLimit);
                        }

                        float itemH = Math.Max(0f, item.Geometry.MarginBox.Height);
                        if (!float.IsFinite(itemH) || itemH <= 0f)
                        {
                            itemH = Math.Max(0f, item.Geometry.BorderBox.Height);
                        }
                        if (!float.IsFinite(itemH) || itemH <= 0f)
                        {
                            itemH = Math.Max(0f, item.Geometry.ContentBox.Height);
                        }
                        if (!float.IsFinite(itemH) || itemH <= 0f)
                        {
                            itemH = Math.Max(1f, line.Height);
                        }
                        if ((!float.IsFinite(itemH) || itemH <= 0f) &&
                            float.IsFinite(state.AvailableSize.Height) &&
                            state.AvailableSize.Height > 0f)
                        {
                            itemH = state.AvailableSize.Height;
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
                        float shiftX = runningRight - itemLeft;
                        if (float.IsFinite(shiftX) && shiftX > 0f)
                        {
                            // Keep Y unchanged so relative-inset adjustments are not re-applied.
                            LayoutBoxOps.ShiftSubtree(item, shiftX, 0f);
                        }
                    }

                    if (float.IsFinite(item.Geometry.MarginBox.Right))
                    {
                        runningRight = Math.Max(runningRight, item.Geometry.MarginBox.Right);
                    }
                }
            }

            // Derive geometry for fragmented inline wrappers from their recorded
            // fragments. Deepest-first so parents can union finalized children.
            for (int wrapperIdx = wrapperFragmentOrder.Count - 1; wrapperIdx >= 0; wrapperIdx--)
            {
                var wrapperBox = wrapperFragmentOrder[wrapperIdx];
                if (!wrapperFragments.TryGetValue(wrapperBox, out var fragments) || fragments.Count == 0)
                {
                    continue;
                }

                float minX = float.MaxValue, minY = float.MaxValue;
                float maxX = float.MinValue, maxY = float.MinValue;
                foreach (var fragment in fragments)
                {
                    int lineIdx = Math.Min(fragment.LineIndex, lines.Count - 1);
                    float lineY = lineIdx >= 0 && lineIdx < lineYPositions.Count ? lineYPositions[lineIdx] : 0f;
                    float lineXOffset = lineIdx >= 0 && lineIdx < lineXOffsets.Count ? lineXOffsets[lineIdx] : 0f;
                    minX = Math.Min(minX, fragment.XStart + lineXOffset);
                    maxX = Math.Max(maxX, fragment.XStart + lineXOffset + fragment.Width);
                    minY = Math.Min(minY, lineY);
                    maxY = Math.Max(maxY, lineY + Math.Max(0f, fragment.Height));
                }

                // Union any finalized descendant wrappers (nested styled inlines).
                foreach (var nestedPair in finalizedWrapperRects)
                {
                    if (!ReferenceEquals(nestedPair.Key.Parent, wrapperBox)) continue;
                    minX = Math.Min(minX, nestedPair.Value.Left);
                    minY = Math.Min(minY, nestedPair.Value.Top);
                    maxX = Math.Max(maxX, nestedPair.Value.Right);
                    maxY = Math.Max(maxY, nestedPair.Value.Bottom);
                }

                if (maxX <= minX || maxY <= minY)
                {
                    continue;
                }

                if (wrapperBox.Geometry == null) wrapperBox.Geometry = new BoxModel();
                wrapperBox.Geometry.Padding = wrapperBox.ComputedStyle?.Padding ?? new Thickness();
                wrapperBox.Geometry.Border = wrapperBox.ComputedStyle?.BorderThickness ?? new Thickness();
                wrapperBox.Geometry.Margin = wrapperBox.ComputedStyle?.Margin ?? new Thickness();
                wrapperBox.Geometry.ContentBox = new SKRect(0f, 0f, maxX - minX, maxY - minY);
                LayoutBoxOps.SyncBoxes(wrapperBox.Geometry);

                // Move only the wrapper itself: its descendants (text leaves,
                // atomic items, nested wrappers) were already positioned at
                // absolute IFC coordinates by the earlier passes above.
                float preMoveLeft = wrapperBox.Geometry.MarginBox.Left;
                float preMoveTop = wrapperBox.Geometry.MarginBox.Top;
                LayoutBoxOps.PositionSubtree(wrapperBox, minX, minY, state);
                float appliedDx = wrapperBox.Geometry.MarginBox.Left - preMoveLeft;
                float appliedDy = wrapperBox.Geometry.MarginBox.Top - preMoveTop;
                if (MathF.Abs(appliedDx) > 0.001f || MathF.Abs(appliedDy) > 0.001f)
                {
                    LayoutBoxOps.ShiftDescendants(wrapperBox, -appliedDx, -appliedDy);
                }

                finalizedWrapperRects[wrapperBox] = new SKRect(minX, minY, maxX, maxY);
            }

            // Keep original children (don't replace with fragments)
            // Already done - we didn't modify box.Children

            float totalContentHeight = lineYPositions.Count > 0 ? lineYPositions[lineYPositions.Count - 1] + lines[lines.Count - 1].Height : 0;

            // Some inline formatting cases, especially atomic inline-blocks with numeric
            // vertical-align, can extend below the synthesized line stack. Clamp the
            // content height to the actual laid out descendant bottoms so following block
            // flow sees the full consumed height.
            float actualContentRight = 0f;
            float actualContentBottom = 0f;
            foreach (var child in flattenedChildren)
            {
                if (child?.Geometry == null)
                {
                    continue;
                }

                float right = child.Geometry.MarginBox.Right;
                if (!float.IsFinite(right))
                {
                    right = child.Geometry.BorderBox.Right;
                }

                if (!float.IsFinite(right))
                {
                    right = child.Geometry.ContentBox.Right;
                }

                if (float.IsFinite(right))
                {
                    actualContentRight = Math.Max(actualContentRight, right);
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
            
            // SHRINK-TO-FIT: only unconstrained probes should adopt measured line width.
            // In finite layout, block containers must keep their resolved content width
            // and let long inline content overflow/wrap instead of widening the container.
            // Inline-level atomic boxes (inline-block / inline-flex / inline-grid /
            // inline-table) are always shrink-to-fit per CSS, even when re-laid out
            // with a finite available width — they must size to their intrinsic content.
            float finalContentWidth = box.Geometry.ContentBox.Width;
            if (box.ComputedStyle != null && !box.ComputedStyle.Width.HasValue)
            {
                string display = box.ComputedStyle.Display?.ToLowerInvariant() ?? string.Empty;
                bool isInlineAtomic = display == "inline-block" || display == "inline-flex" ||
                                      display == "inline-grid" || display == "inline-table";
                if (float.IsInfinity(state.AvailableSize.Width) || isInlineAtomic)
                {
                    finalContentWidth = Math.Max(maxLineWidth, actualContentRight);
                }
            }

            if (!isAnonymousBlock &&
                TryResolveExplicitContentHeight(box.ComputedStyle, state, out float explicitContentHeight))
            {
                finalContentHeight = explicitContentHeight;
            }

            if (isAnonymousBlock)
            {
                finalContentWidth = Math.Max(0f, finalContentWidth);
                finalContentHeight = Math.Max(0f, finalContentHeight);
            }
            else
            {
                ApplyMinMaxConstraints(box.ComputedStyle, state, ref finalContentWidth, ref finalContentHeight);
            }

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
            
            LayoutBoxOps.SyncBoxes(box.Geometry);

            // Layout out-of-flow children (absolute / fixed).
            // Same three-pass pattern as BlockFormattingContext §OOF:
            //   Pass 1 – intrinsic measurement (infinite available size)
            //   Solve   – ResolvePositionedBox computes abs geometry from insets + intrinsic
            //   Pass 2 – re-layout with resolved box size
            //   Solve   – re-apply position (child layout may have clobbered geometry)
            foreach (var oof in outOfFlow)
            {
                var oofContext = FormattingContext.Resolve(oof);

                // Pass 1: intrinsic measurement (auto-size shrink-to-fit signal).
                var intrinsicState = state.Clone();
                intrinsicState.AvailableSize = new SKSize(float.PositiveInfinity, float.PositiveInfinity);
                intrinsicState.ContainingBlockWidth = box.Geometry.ContentBox.Width;
                intrinsicState.ContainingBlockHeight = box.Geometry.ContentBox.Height;
                oofContext.Layout(oof, intrinsicState);

                // Solve abs/fixed geometry from intrinsic size and insets.
                LayoutPositioningLogic.ResolvePositionedBox(oof, box, box.Geometry, state);

                // Pass 2: layout contents using resolved box size.
                var resolvedWidth = Math.Max(0f, oof.Geometry.ContentBox.Width);
                var resolvedHeight = Math.Max(0f, oof.Geometry.ContentBox.Height);
                var resolvedOuterWidth = Math.Max(resolvedWidth, oof.Geometry.MarginBox.Width);
                var resolvedOuterHeight = Math.Max(resolvedHeight, oof.Geometry.MarginBox.Height);
                var resolvedState = new LayoutState(
                    new SKSize(resolvedOuterWidth, resolvedOuterHeight),
                    resolvedOuterWidth,
                    resolvedOuterHeight,
                    state.ViewportWidth,
                    state.ViewportHeight,
                    state.Deadline);
                oofContext.Layout(oof, resolvedState);

                // Re-apply final absolute position after child layout potentially touched geometry.
                LayoutPositioningLogic.ResolvePositionedBox(
                    oof,
                    box,
                    box.Geometry,
                    state,
                    collapsePositioningMarginsInFinalGeometry: true);
            }
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

            bool nativeCheckboxOrRadio = ReplacedElementSizing.IsNativeCheckboxOrRadio(element);
            float contentWidth = intrinsic.Width;
            float contentHeight = intrinsic.Height;
            ApplyMinMaxConstraints(box.ComputedStyle, state, ref contentWidth, ref contentHeight);

            float left = box.Geometry.ContentBox.Left;
            float top = box.Geometry.ContentBox.Top;
            box.Geometry.ContentBox = new SKRect(left, top, left + contentWidth, top + contentHeight);
            box.Geometry.Padding = nativeCheckboxOrRadio ? new Thickness() : (box.ComputedStyle?.Padding ?? new Thickness());
            box.Geometry.Border = nativeCheckboxOrRadio ? new Thickness() : (box.ComputedStyle?.BorderThickness ?? new Thickness());
            box.Geometry.Margin = box.ComputedStyle?.Margin ?? new Thickness();
            LayoutBoxOps.SyncBoxes(box.Geometry);
            return true;
        }

        private static int FindNextSoftWrapEnd(string text, int startIdx)
        {
            int nextSpace = text.IndexOf(' ', startIdx);
            int nextHyphen = text.IndexOf('-', startIdx);

            // Prefer normal whitespace boundaries. A hyphen is only a fallback
            // break when there is no later space in the token; otherwise labels
            // such as "background-color:" split as "background-" / "color:" even
            // when the whole hyphenated token fits on the next line.
            if (nextSpace >= 0)
            {
                return nextSpace + 1;
            }

            if (nextHyphen >= 0)
            {
                nextHyphen += 1;
            }

            return nextHyphen > startIdx ? nextHyphen : text.Length;
        }

        private static bool AllowsBreakAnywhere(CssComputed style)
        {
            return string.Equals(style?.LineBreak, "anywhere", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(style?.OverflowWrap, "anywhere", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(style?.WordBreak, "break-all", StringComparison.OrdinalIgnoreCase);
        }

        private static int FindNextCodePointEnd(string text, int startIdx)
        {
            int endIdx = startIdx + 1;
            if (endIdx < text.Length && char.IsHighSurrogate(text[startIdx]) && char.IsLowSurrogate(text[endIdx]))
            {
                endIdx++;
            }

            return endIdx;
        }

        private bool TryBuildBalancedTextLines(
            string text,
            CssComputed style,
            float contentLimit,
            out List<BalancedTextLine> balancedLines)
        {
            balancedLines = null;

            if (string.IsNullOrEmpty(text) ||
                !float.IsFinite(contentLimit) ||
                contentLimit <= 0f ||
                text.IndexOf(' ') < 0)
            {
                return false;
            }

            var tokenStarts = new List<int>();
            var tokenEnds = new List<int>();
            int startIdx = 0;
            while (startIdx < text.Length)
            {
                int endIdx = FindNextSoftWrapEnd(text, startIdx);
                if (endIdx <= startIdx)
                {
                    return false;
                }

                tokenStarts.Add(startIdx);
                tokenEnds.Add(endIdx);
                startIdx = endIdx;
            }

            int tokenCount = tokenStarts.Count;
            if (tokenCount < 2 || tokenCount > 80)
            {
                return false;
            }

            var tokenWidths = new float[tokenCount];
            for (int i = 0; i < tokenCount; i++)
            {
                tokenWidths[i] = MeasureString(
                    text.Substring(tokenStarts[i], tokenEnds[i] - tokenStarts[i]),
                    style).Width;
            }

            int targetLineCount = CountGreedyWrappedLines(tokenWidths, contentLimit);
            if (targetLineCount <= 1 || targetLineCount > 6)
            {
                return false;
            }

            var rangeWidthCache = new Dictionary<(int Start, int End), float>();
            var candidateBreaks = new int[targetLineCount - 1];
            var candidateWidths = new float[targetLineCount];
            int[] bestBreaks = null;
            float[] bestWidths = null;
            float bestMaxWidth = float.PositiveInfinity;
            float bestVariance = float.PositiveInfinity;

            Search(0, 0);

            if (bestBreaks == null || bestWidths == null)
            {
                return false;
            }

            balancedLines = new List<BalancedTextLine>(targetLineCount);
            int tokenStart = 0;
            for (int lineIndex = 0; lineIndex < targetLineCount; lineIndex++)
            {
                int tokenEnd = lineIndex < bestBreaks.Length ? bestBreaks[lineIndex] : tokenCount;
                int textStart = tokenStarts[tokenStart];
                int textEnd = tokenEnds[tokenEnd - 1];
                balancedLines.Add(new BalancedTextLine(
                    text.Substring(textStart, textEnd - textStart),
                    bestWidths[lineIndex]));
                tokenStart = tokenEnd;
            }

            return true;

            void Search(int tokenStartIndex, int lineIndex)
            {
                int remainingLines = targetLineCount - lineIndex;
                int remainingTokens = tokenCount - tokenStartIndex;
                if (remainingTokens < remainingLines)
                {
                    return;
                }

                if (remainingLines == 1)
                {
                    float finalWidth = MeasureRangeWidth(tokenStartIndex, tokenCount);
                    if (!RangeFits(finalWidth, tokenStartIndex, tokenCount))
                    {
                        return;
                    }

                    candidateWidths[lineIndex] = finalWidth;
                    EvaluateCandidate();
                    return;
                }

                int maxEnd = tokenCount - (remainingLines - 1);
                for (int tokenEndIndex = tokenStartIndex + 1; tokenEndIndex <= maxEnd; tokenEndIndex++)
                {
                    float width = MeasureRangeWidth(tokenStartIndex, tokenEndIndex);
                    if (!RangeFits(width, tokenStartIndex, tokenEndIndex))
                    {
                        if (tokenEndIndex > tokenStartIndex + 1)
                        {
                            break;
                        }

                        continue;
                    }

                    candidateBreaks[lineIndex] = tokenEndIndex;
                    candidateWidths[lineIndex] = width;
                    Search(tokenEndIndex, lineIndex + 1);
                }
            }

            void EvaluateCandidate()
            {
                float maxWidth = 0f;
                float sum = 0f;
                for (int i = 0; i < candidateWidths.Length; i++)
                {
                    float width = candidateWidths[i];
                    maxWidth = Math.Max(maxWidth, width);
                    sum += width;
                }

                float mean = sum / candidateWidths.Length;
                float variance = 0f;
                for (int i = 0; i < candidateWidths.Length; i++)
                {
                    float delta = candidateWidths[i] - mean;
                    variance += delta * delta;
                }

                if (maxWidth < bestMaxWidth - 0.5f ||
                    (Math.Abs(maxWidth - bestMaxWidth) <= 0.5f && variance < bestVariance))
                {
                    bestMaxWidth = maxWidth;
                    bestVariance = variance;
                    bestBreaks = (int[])candidateBreaks.Clone();
                    bestWidths = (float[])candidateWidths.Clone();
                }
            }

            float MeasureRangeWidth(int tokenStartIndex, int tokenEndIndex)
            {
                var key = (tokenStartIndex, tokenEndIndex);
                if (rangeWidthCache.TryGetValue(key, out float cachedWidth))
                {
                    return cachedWidth;
                }

                int textStart = tokenStarts[tokenStartIndex];
                int textEnd = tokenEnds[tokenEndIndex - 1];
                float width = MeasureString(text.Substring(textStart, textEnd - textStart), style).Width;
                rangeWidthCache[key] = width;
                return width;
            }

            bool RangeFits(float width, int tokenStartIndex, int tokenEndIndex)
            {
                return width <= contentLimit + 0.5f || tokenEndIndex == tokenStartIndex + 1;
            }
        }

        private static int CountGreedyWrappedLines(IReadOnlyList<float> tokenWidths, float contentLimit)
        {
            int lines = 1;
            float curX = 0f;

            for (int i = 0; i < tokenWidths.Count; i++)
            {
                float width = tokenWidths[i];
                if (curX + width > contentLimit && curX > 0f)
                {
                    lines++;
                    curX = 0f;
                }

                curX += width;
            }

            return lines;
        }

        private static bool UsesBalancedTextWrap(CssComputed style)
        {
            if (style?.Map == null)
            {
                return false;
            }

            return MapKeywordEquals(style.Map, "text-wrap-style", "balance") ||
                   MapKeywordEquals(style.Map, "text-wrap", "balance");
        }

        private static bool MapKeywordEquals(
            IReadOnlyDictionary<string, string> map,
            string propertyName,
            string keyword)
        {
            return map.TryGetValue(propertyName, out var value) &&
                   string.Equals(value?.Trim(), keyword, StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsVerticalWritingMode(string writingMode)
        {
            return writingMode == "vertical-rl" || writingMode == "vertical-lr";
        }

        /// <summary>
        /// Lays out a text-only inline container in a vertical writing mode.
        /// Text flows top-to-bottom in columns; columns advance right-to-left
        /// (vertical-rl) or left-to-right (vertical-lr). A column wraps when its
        /// character run would exceed the available height. Each column is a
        /// single ComputedTextLine (the paint path rotates the run 90 degrees).
        /// Returns false when the container has content the vertical path does
        /// not handle, so the caller falls back to horizontal flow.
        /// </summary>
        private bool TryLayoutVerticalTextContent(LayoutBox box, LayoutState state)
        {
            if (box?.Geometry == null || box.ComputedStyle == null)
            {
                return false;
            }

            // Only text (and plain inline wrappers) are handled here. Atomic
            // inlines, floats, and out-of-flow boxes fall back to the
            // horizontal path.
            foreach (var child in box.Children)
            {
                if (child == null || child.IsOutOfFlow)
                {
                    continue;
                }

                if (child is TextLayoutBox)
                {
                    continue;
                }

                string display = child.ComputedStyle?.Display?.ToLowerInvariant() ?? "inline";
                if (display != "inline" && display != "contents")
                {
                    return false;
                }
            }

            var info = GetStyleFontInfo(box.ComputedStyle);
            float columnWidth = info.LineHeight;
            float maxColumnHeight = ResolveVerticalColumnHeight(box, state);

            string writingMode = box.ComputedStyle.WritingMode;
            bool rightToLeft = writingMode == "vertical-rl";

            // Each column accumulates characters; a column is emitted when the
            // next character would exceed the available height. All text in a
            // column becomes one line so the renderer can rotate it as a run.
            var columns = new List<string>();
            var currentColumn = new System.Text.StringBuilder();
            float currentColumnWidth = 0f;

            foreach (var child in box.Children)
            {
                if (child is not TextLayoutBox textBox)
                {
                    continue;
                }

                string text = (textBox.SourceNode as Text)?.Data ?? string.Empty;
                text = CollapseWhitespace(text);
                if (text.Length == 0)
                {
                    ResetTextBoxGeometry(textBox);
                    continue;
                }

                foreach (char ch in text)
                {
                    if (char.IsWhiteSpace(ch) && ch != ' ')
                    {
                        continue;
                    }

                    float chWidth = MeasureString(ch.ToString(), box.ComputedStyle).Width;
                    if (currentColumn.Length > 0 && currentColumnWidth + chWidth > maxColumnHeight)
                    {
                        columns.Add(currentColumn.ToString());
                        currentColumn.Clear();
                        currentColumnWidth = 0f;
                    }

                    currentColumn.Append(ch);
                    currentColumnWidth += chWidth;
                }
            }

            if (currentColumn.Length > 0)
            {
                columns.Add(currentColumn.ToString());
            }

            if (columns.Count == 0)
            {
                return false;
            }

            float contentWidth = Math.Max(1f, columns.Count * columnWidth);
            float contentHeight = maxColumnHeight;

            box.Geometry.ContentBox = new SKRect(
                box.Geometry.ContentBox.Left,
                box.Geometry.ContentBox.Top,
                box.Geometry.ContentBox.Left + contentWidth,
                box.Geometry.ContentBox.Top + contentHeight);

            // Place each column as one line of one text box. All text in this
            // container shares one TextLayoutBox geometry whose lines carry the
            // per-column origins.
            TextLayoutBox firstTextBox = box.Children.OfType<TextLayoutBox>().FirstOrDefault();
            foreach (var textBox in box.Children.OfType<TextLayoutBox>())
            {
                ResetTextBoxGeometry(textBox);
            }

            if (firstTextBox == null)
            {
                return false;
            }

            if (firstTextBox.Geometry == null)
            {
                firstTextBox.Geometry = new BoxModel();
            }

            var lines = new List<ComputedTextLine>(columns.Count);
            for (int i = 0; i < columns.Count; i++)
            {
                float columnX = rightToLeft
                    ? contentWidth - (i + 1) * columnWidth
                    : i * columnWidth;

                lines.Add(new ComputedTextLine
                {
                    Text = columns[i],
                    Origin = new SKPoint(columnX, 0f),
                    Width = columnWidth,
                    Height = info.LineHeight,
                    Baseline = info.Baseline
                });
            }

            firstTextBox.Geometry.Lines = lines;
            firstTextBox.Geometry.ContentBox = new SKRect(0f, 0f, contentWidth, contentHeight);
            firstTextBox.Geometry.Padding = new Thickness();
            firstTextBox.Geometry.Border = new Thickness();
            firstTextBox.Geometry.Margin = new Thickness();
            LayoutBoxOps.SyncBoxes(firstTextBox.Geometry);
            LayoutBoxOps.PositionSubtree(firstTextBox, 0f, 0f, state);

            firstTextBox.Geometry.LineHeight = info.LineHeight;
            firstTextBox.Geometry.Baseline = info.Baseline;
            firstTextBox.Geometry.Ascent = info.Baseline;
            firstTextBox.Geometry.Descent = Math.Max(0f, info.LineHeight - info.Baseline);

            box.Geometry.LineHeight = info.LineHeight;
            box.Geometry.Baseline = info.Baseline;
            box.Geometry.Ascent = info.Baseline;
            box.Geometry.Descent = info.Descent;
            LayoutBoxOps.SyncBoxes(box.Geometry);

            return true;
        }

        private static float ResolveVerticalColumnHeight(LayoutBox box, LayoutState state)
        {
            float height = state.AvailableSize.Height;
            if (float.IsNaN(height) || float.IsInfinity(height) || height <= 0f)
            {
                height = state.ViewportHeight;
            }

            if (float.IsNaN(height) || float.IsInfinity(height) || height <= 0f)
            {
                height = box.Geometry?.ContentBox.Height ?? 600f;
            }

            return Math.Max(1f, height);
        }

        private static bool IsRubyAnnotationTextNode(TextLayoutBox textBox)
        {
            if (textBox?.SourceNode is not Text textNode ||
                textNode.ParentElement == null)
            {
                return false;
            }

            // RT/RTC inside a RUBY marks annotation text: painted above the base
            // by the ruby handler, excluded from inline flow.
            var ancestor = textNode.ParentElement;
            while (ancestor != null)
            {
                string tag = ancestor.TagName?.ToUpperInvariant() ?? string.Empty;
                if (tag == "RUBY")
                {
                    return false;
                }

                if (tag == "RT" || tag == "RTC")
                {
                    return true;
                }

                ancestor = ancestor.ParentElement;
            }

            return false;
        }

        private static bool IsRubyAnnotationElement(Element element)
        {
            // True for an RT/RTC element nested (directly or transitively)
            // inside a RUBY element.
            var ancestor = element.ParentElement;
            while (ancestor != null)
            {
                string tag = ancestor.TagName?.ToUpperInvariant() ?? string.Empty;
                if (tag == "RUBY")
                {
                    return false;
                }

                if (tag == "RT" || tag == "RTC")
                {
                    return true;
                }

                ancestor = ancestor.ParentElement;
            }

            return false;
        }

        private static void ResetInlineSubtreeGeometry(LayoutBox box)
        {
            if (box == null)
            {
                return;
            }

            box.Geometry = new BoxModel();
            box.Geometry.Padding = new Thickness();
            box.Geometry.Border = new Thickness();
            box.Geometry.Margin = new Thickness();
            box.Geometry.Lines = new List<ComputedTextLine>();
            LayoutBoxOps.SyncBoxes(box.Geometry);

            foreach (var child in box.Children)
            {
                ResetInlineSubtreeGeometry(child);
            }
        }
        private void LayoutLeafTextBox(TextLayoutBox textBox, LayoutState state)
        {
            if (textBox.Geometry == null)
            {
                textBox.Geometry = new BoxModel();
            }

            // Ruby annotation (RT) text is rendered by the ruby handler above
            // the base run; it never occupies inline space on its own.
            if (IsRubyAnnotationTextNode(textBox))
            {
                ResetTextBoxGeometry(textBox);
                return;
            }

            string text = NormalizeIsolatedText(textBox.TextContent);
            if (text.Length == 0)
            {
                ResetTextBoxGeometry(textBox);
                return;
            }

            var metrics = MeasureTextMetrics(text, textBox.ComputedStyle);

            // A leaf text run laid out as a box in its own right -- a bare text node
            // that became a flex or grid item -- still has to break at its available
            // width; CSS Display 3 wraps such a run in an anonymous block container.
            // Measuring the whole run as one line is only correct when nothing
            // constrains it, as in a shrink-to-fit probe.
            float wrapWidth = ResolveLeafTextWrapWidth(textBox, state);
            var wrappedLines = wrapWidth > 0 && metrics.Width > wrapWidth && AllowsLeafTextWrapping(textBox)
                ? WrapLeafText(text, textBox.ComputedStyle, wrapWidth)
                : null;

            if (wrappedLines != null && wrappedLines.Count > 1)
            {
                LayoutWrappedLeafTextBox(textBox, state, wrappedLines, metrics);
                return;
            }

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

            LayoutBoxOps.SyncBoxes(textBox.Geometry);
        }

        /// <summary>
        /// Stacks pre-broken lines into a leaf text box, following the same origin
        /// convention as the in-flow path: every line origin is relative to the box
        /// content box and advances by one line height.
        /// </summary>
        private void LayoutWrappedLeafTextBox(
            TextLayoutBox textBox,
            LayoutState state,
            List<string> lineTexts,
            InlineTextMetrics metrics)
        {
            float lineHeight = metrics.LineHeight;
            float baseline = Math.Min(lineHeight, Math.Max(0f, metrics.Baseline));

            var lines = new List<ComputedTextLine>(lineTexts.Count);
            float widest = 0f;
            for (int i = 0; i < lineTexts.Count; i++)
            {
                float lineWidth = MeasureTextMetrics(lineTexts[i], textBox.ComputedStyle).Width;
                widest = Math.Max(widest, lineWidth);
                lines.Add(new ComputedTextLine
                {
                    Text = lineTexts[i],
                    Origin = new SKPoint(0, i * lineHeight),
                    Width = lineWidth,
                    Height = lineHeight,
                    Baseline = baseline
                });
            }

            float contentWidth = widest;
            float contentHeight = lineHeight * lineTexts.Count;
            ApplyMinMaxConstraints(textBox.ComputedStyle, state, ref contentWidth, ref contentHeight);

            textBox.Geometry.ContentBox = new SKRect(0, 0, contentWidth, contentHeight);
            textBox.Geometry.Padding = new Thickness();
            textBox.Geometry.Border = new Thickness();
            textBox.Geometry.Margin = new Thickness();
            textBox.Geometry.Lines = lines;

            textBox.Geometry.LineHeight = lineHeight;
            textBox.Geometry.Baseline = baseline;
            textBox.Geometry.Ascent = baseline;
            textBox.Geometry.Descent = Math.Max(0f, lineHeight - baseline);

            LayoutBoxOps.SyncBoxes(textBox.Geometry);
        }

        /// <summary>
        /// The width a standalone leaf text run must break at, or 0 when nothing
        /// constrains it. An explicit width wins, because that is how a flex
        /// container hands a shrunk item its resolved main size.
        /// </summary>
        private static float ResolveLeafTextWrapWidth(TextLayoutBox textBox, LayoutState state)
        {
            var style = textBox.ComputedStyle;
            if (style?.Width.HasValue == true)
            {
                float explicitWidth = (float)style.Width.Value;
                if (float.IsFinite(explicitWidth) && explicitWidth > 0)
                {
                    return explicitWidth;
                }
            }

            float available = state.AvailableSize.Width;
            if (float.IsFinite(available) && available > 0)
            {
                return available;
            }

            float containing = state.ContainingBlockWidth;
            return float.IsFinite(containing) && containing > 0 ? containing : 0f;
        }

        /// <summary>
        /// CSS Text 3: only the wrapping white-space modes break lines.
        /// </summary>
        private static bool AllowsLeafTextWrapping(TextLayoutBox textBox)
        {
            string mode = (textBox.ComputedStyle?.WhiteSpace ?? "normal").Trim().ToLowerInvariant();
            return mode != "nowrap" && mode != "pre";
        }

        /// <summary>
        /// Greedy word wrap using the same measurement as the rest of this context,
        /// so a standalone run breaks where the in-flow path would break it. A single
        /// word wider than the line overflows rather than splitting, which is what
        /// <c>overflow-wrap: normal</c> requires.
        /// </summary>
        private List<string> WrapLeafText(string text, CssComputed style, float maxWidth)
        {
            var lines = new List<string>();
            var current = new StringBuilder();

            foreach (var word in text.Split(' '))
            {
                if (word.Length == 0)
                {
                    continue;
                }

                if (current.Length == 0)
                {
                    current.Append(word);
                    continue;
                }

                int lengthBeforeWord = current.Length;
                current.Append(' ').Append(word);
                if (MeasureTextMetrics(current.ToString(), style).Width > maxWidth)
                {
                    current.Length = lengthBeforeWord;
                    lines.Add(current.ToString());
                    current.Clear();
                    current.Append(word);
                }
            }

            if (current.Length > 0)
            {
                lines.Add(current.ToString());
            }

            return lines;
        }

        private SKSize MeasureInlineChild(LayoutBox child, LayoutState state)
        {
            if (TryMeasureAtomicInlineReplacedChild(child, state, out var replacedSize))
            {
                return replacedSize;
            }

            if (child is TextLayoutBox textBox)
            {
                // Use the same whitespace-collapsed (not trimmed) text the word-flow
                // path will process, so the aggregated width agrees with the sum
                // produced during re-layout. NormalizeIsolatedText trims edge
                // whitespace; using it here can leave the aggregated width short
                // of the word-flow total by the trimmed spaces' widths and trigger
                // spurious wraps.
                string collapsed = CollapseWhitespace(textBox.TextContent ?? string.Empty);
                if (collapsed.Length == 0)
                {
                    return SKSize.Empty;
                }

                if (collapsed.IndexOf(' ') >= 0 &&
                    !UsesNoWrapWhiteSpace(textBox.ComputedStyle ?? child.ComputedStyle))
                {
                    float totalWidth = 0f;
                    float maxHeight = 0f;
                    int startIdx = 0;
                    while (startIdx < collapsed.Length)
                    {
                        int nextSpace = collapsed.IndexOf(' ', startIdx);
                        int endIdx = nextSpace == -1 ? collapsed.Length : nextSpace + 1;
                        string word = collapsed.Substring(startIdx, endIdx - startIdx);
                        var wordSize = MeasureString(word, textBox.ComputedStyle);
                        totalWidth += wordSize.Width;
                        maxHeight = Math.Max(maxHeight, wordSize.Height);
                        startIdx = endIdx;
                    }
                    return new SKSize(totalWidth, maxHeight);
                }

                return MeasureString(collapsed, textBox.ComputedStyle);
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
                        float nonContentWidth = GetNonContentWidth(inlineBox.ComputedStyle);
                        bool hasRenderableLabel = TryMeasureInlineLabelContent(inlineBox, out _, out _);
                        // Ensure the height is at least one line-height for
                        // inline-block elements that contain text (status pills,
                        // buttons, etc.).  The infinite-width probe can collapse
                        // the content height when children re-flow narrower than
                        // their natural single-line width.
                        float minLineHeight = 0f;
                        if (hasRenderableLabel)
                        {
                            minLineHeight = GetStyleFontInfo(inlineBox.ComputedStyle).LineHeight;
                            minLineHeight += (float)(inlineBox.ComputedStyle?.Padding.Top ?? 0)
                                          + (float)(inlineBox.ComputedStyle?.Padding.Bottom ?? 0)
                                          + (float)(inlineBox.ComputedStyle?.BorderThickness.Top ?? 0)
                                          + (float)(inlineBox.ComputedStyle?.BorderThickness.Bottom ?? 0);
                            probedHeight = Math.Max(probedHeight, Math.Max(1f, minLineHeight));
                        }
                        bool collapsedToChromeOnly = hasRenderableLabel && probedWidth <= nonContentWidth + 0.5f;
                        if (probedWidth > 0 && probedHeight > 0 && !collapsedToChromeOnly)
                        {
                            // Second probe with finite width to get correct height.
                            float fitWidth = probedWidth;
                            float contentW = fitWidth - nonContentWidth;
                            if (contentW > 0.5f)
                            {
                                var fitState = state.Clone();
                                fitState.AvailableSize = new SKSize(fitWidth, Math.Max(probedHeight, minLineHeight * 2f));
                                fitState.ContainingBlockWidth = fitWidth;
                                ResetInlineProbeOrigin(inlineBox);
                                FormattingContext.Resolve(inlineBox).Layout(inlineBox, fitState);
                                float fitHeight = inlineBox.Geometry.MarginBox.Height;
                                if (fitHeight > 0f) probedHeight = Math.Max(probedHeight, fitHeight);
                            }
                            return new SKSize(probedWidth, Math.Max(probedHeight, 0f));
                        }
                    }

                    if (TryMeasureInlineLabelContent(inlineBox, out float labelContentWidth, out float labelContentHeight))
                    {
                        float fallbackWidth = labelContentWidth;
                        float fallbackHeight = labelContentHeight;
                        ApplyMinMaxConstraints(inlineBox.ComputedStyle, state, ref fallbackWidth, ref fallbackHeight);
                        return AddNonContentSpacing(new SKSize(Math.Max(0f, fallbackWidth), Math.Max(0f, fallbackHeight)), inlineBox);
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
                        return AddNonContentSpacing(new SKSize(Math.Max(0f, cw), Math.Max(0f, ch)), inlineBox);
                    }

                    // Empty atomic inlines should size from their own chrome only.
                    return AddNonContentSpacing(SKSize.Empty, inlineBox);
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
                    h = GetStyleFontInfo(inlineBox.ComputedStyle).LineHeight;
                }

                return AddNonContentSpacing(new SKSize(Math.Max(0f, w), h), inlineBox);
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
                    return AddNonContentSpacing(new SKSize(w, h), child);
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

            size = AddNonContentSpacing(new SKSize(Math.Max(0f, width), Math.Max(0f, height)), child);
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

        private bool TryMeasureInlineLabelContent(LayoutBox box, out float contentWidth, out float contentHeight)
        {
            contentWidth = 0f;
            contentHeight = 0f;

            if (box?.SourceNode is not Element element)
            {
                return false;
            }

            string label = LayoutHelper.GetRenderableTextContentTrimmed(element);
            if (string.IsNullOrWhiteSpace(label))
            {
                return false;
            }

            var labelSize = MeasureString(label, box.ComputedStyle);
            float lineHeight = Math.Max(0f, GetStyleFontInfo(box.ComputedStyle).LineHeight);
            contentWidth = Math.Max(0f, labelSize.Width);
            contentHeight = Math.Max(lineHeight, Math.Max(0f, labelSize.Height));
            return contentWidth > 0f || contentHeight > 0f;
        }

        private static float GetNonContentWidth(CssComputed style)
        {
            var p = style?.Padding ?? new Thickness();
            var b = style?.BorderThickness ?? new Thickness();
            var m = style?.Margin ?? new Thickness();
            return (float)(p.Left + p.Right + b.Left + b.Right + m.Left + m.Right);
        }

        private static void ResetInlineProbeOrigin(LayoutBox box)
        {
            LayoutBoxOps.ResetSubtreeToOrigin(box);
        }

        internal static string CollapseWhitespace(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }

            bool previousWasCollapsible = false;
            bool requiresNormalization = false;
            for (int i = 0; i < text.Length; i++)
            {
                char ch = text[i];
                if (!TextWhitespaceClassifier.IsCollapsibleWhitespaceChar(ch))
                {
                    previousWasCollapsible = false;
                    continue;
                }

                if (ch != ' ' || previousWasCollapsible)
                {
                    requiresNormalization = true;
                    break;
                }

                previousWasCollapsible = true;
            }

            if (!requiresNormalization)
            {
                return text;
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

        private static bool UsesNoWrapWhiteSpace(CssComputed style)
        {
            return string.Equals(style?.WhiteSpace?.Trim(), "nowrap", StringComparison.OrdinalIgnoreCase);
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
                case "middle":
                    verticalOffset = ((lineAscent + lineDescent - safeHeight) / 2f) - baseOffset;
                    break;
                case "top":
                    verticalOffset = -(lineAscent - safeAscent);
                    break;
                case "bottom":
                    // Bottom of the finished line box, which may exceed ascent+descent
                    // when a line-relative box is the tallest thing on the line.
                    verticalOffset = Math.Max(lineAscent + lineDescent, line?.Height ?? 0f) - safeHeight - baseOffset;
                    break;
                case "text-top":
                    verticalOffset = -(lineAscent - safeAscent);
                    break;
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

        private static bool IsLineRelativeVerticalAlign(string verticalAlign)
        {
            var value = verticalAlign?.Trim().ToLowerInvariant();
            return value == "top" || value == "bottom";
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

        private static SKSize AddNonContentSpacing(SKSize content, LayoutBox box)
        {
            if (box?.SourceNode is Element element && ReplacedElementSizing.IsNativeCheckboxOrRadio(element))
            {
                var m = box.ComputedStyle?.Margin ?? new Thickness();
                float totalW = content.Width + (float)(m.Left + m.Right);
                float totalH = content.Height + (float)(m.Top + m.Bottom);
                return new SKSize(Math.Max(0f, totalW), Math.Max(0f, totalH));
            }

            return AddNonContentSpacing(content, box?.ComputedStyle);
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
            float cbWidth = LayoutBoxOps.ResolvePercentageBaseWidth(box, state);
            float cbHeight = LayoutBoxOps.ResolvePercentageBaseHeight(box, state);
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

                if (type == "checkbox" || type == "radio")
                {
                    if (w <= 0) w = ReplacedElementSizing.NativeCheckboxRadioSize;
                    if (h <= 0) h = ReplacedElementSizing.NativeCheckboxRadioSize;
                }
                else if (type == "submit" || type == "button" || type == "reset")
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
                var intrinsic = ReplacedElementSizing.TextareaIntrinsicContentSize(el, box.ComputedStyle);
                if (w <= 0) w = intrinsic.Width;
                if (h <= 0) h = intrinsic.Height;
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
            float fontSize = (float)(style.FontSize > 0 ? style.FontSize : 16.0);

            float minW = 0f;
            if (style.MinWidth.HasValue) minW = (float)style.MinWidth.Value;
            else if (style.MinWidthPercent.HasValue) minW = (float)(style.MinWidthPercent.Value / 100.0 * cbWidth);
            else if (!string.IsNullOrEmpty(style.MinWidthExpression))
                minW = LayoutHelper.EvaluateCssExpression(style.MinWidthExpression, cbWidth, state.ViewportWidth, state.ViewportHeight, fontSize);

            float maxW = float.PositiveInfinity;
            if (style.MaxWidth.HasValue) maxW = (float)style.MaxWidth.Value;
            else if (style.MaxWidthPercent.HasValue) maxW = (float)(style.MaxWidthPercent.Value / 100.0 * cbWidth);
            else if (!string.IsNullOrEmpty(style.MaxWidthExpression))
                maxW = LayoutHelper.EvaluateCssExpression(style.MaxWidthExpression, cbWidth, state.ViewportWidth, state.ViewportHeight, fontSize);

            float minH = 0f;
            if (style.MinHeight.HasValue) minH = (float)style.MinHeight.Value;
            else if (style.MinHeightPercent.HasValue) minH = (float)(style.MinHeightPercent.Value / 100.0 * cbHeight);
            else if (!string.IsNullOrEmpty(style.MinHeightExpression))
                minH = LayoutHelper.EvaluateCssExpression(style.MinHeightExpression, cbHeight, state.ViewportWidth, state.ViewportHeight, fontSize);

            float maxH = float.PositiveInfinity;
            if (style.MaxHeight.HasValue) maxH = (float)style.MaxHeight.Value;
            else if (style.MaxHeightPercent.HasValue) maxH = (float)(style.MaxHeightPercent.Value / 100.0 * cbHeight);
            else if (!string.IsNullOrEmpty(style.MaxHeightExpression))
                maxH = LayoutHelper.EvaluateCssExpression(style.MaxHeightExpression, cbHeight, state.ViewportWidth, state.ViewportHeight, fontSize);

            if (string.Equals(style.BoxSizing, "border-box", StringComparison.OrdinalIgnoreCase))
            {
                float horizontalChrome = GetPaddingBorderWidth(style);
                float verticalChrome = GetPaddingBorderHeight(style);

                float borderBoxWidth = Math.Max(0f, width + horizontalChrome);
                float borderBoxHeight = Math.Max(0f, height + verticalChrome);

                borderBoxWidth = Math.Max(minW, Math.Min(borderBoxWidth, maxW));
                borderBoxHeight = Math.Max(minH, Math.Min(borderBoxHeight, maxH));

                width = Math.Max(0f, borderBoxWidth - horizontalChrome);
                height = Math.Max(0f, borderBoxHeight - verticalChrome);
                return;
            }

            width = Math.Max(minW, Math.Min(width, maxW));
            height = Math.Max(minH, Math.Min(height, maxH));
        }

        private static bool TryResolveExplicitContentHeight(CssComputed style, LayoutState state, out float height)
        {
            height = 0f;
            if (style == null)
            {
                return false;
            }

            if (style.Height.HasValue)
            {
                height = (float)style.Height.Value;
            }
            else if (style.HeightPercent.HasValue)
            {
                float basis = ResolveDefiniteContainingBlockHeight(state);
                if (!float.IsFinite(basis) || basis <= 0f)
                {
                    return false;
                }

                height = (float)(style.HeightPercent.Value / 100.0 * basis);
            }
            else if (!string.IsNullOrEmpty(style.HeightExpression))
            {
                float basis = ResolveDefiniteContainingBlockHeight(state);
                if (!float.IsFinite(basis) || basis <= 0f)
                {
                    basis = state.ViewportHeight;
                }

                height = LayoutHelper.EvaluateCssExpression(
                    style.HeightExpression,
                    basis,
                    state.ViewportWidth,
                    state.ViewportHeight,
                    (float)(style.FontSize > 0 ? style.FontSize : 16.0));
            }
            else
            {
                return false;
            }

            if (!float.IsFinite(height))
            {
                return false;
            }

            if (string.Equals(style.BoxSizing, "border-box", StringComparison.OrdinalIgnoreCase))
            {
                height = Math.Max(0f, height - GetPaddingBorderHeight(style));
            }

            return true;
        }

        private static float ResolveDefiniteContainingBlockHeight(LayoutState state)
        {
            float basis = state.ContainingBlockHeight;
            return float.IsFinite(basis) && basis > 0f ? basis : float.NaN;
        }

        private static float GetPaddingBorderWidth(CssComputed style)
        {
            var padding = style?.Padding ?? new Thickness();
            var border = style?.BorderThickness ?? new Thickness();
            return (float)(padding.Left + padding.Right + border.Left + border.Right);
        }

        private static float GetPaddingBorderHeight(CssComputed style)
        {
            var padding = style?.Padding ?? new Thickness();
            var border = style?.BorderThickness ?? new Thickness();
            return (float)(padding.Top + padding.Bottom + border.Top + border.Bottom);
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
            LayoutBoxOps.SyncBoxes(textBox.Geometry);
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

                string display = child.ComputedStyle?.Display;
                if (!string.IsNullOrWhiteSpace(display) &&
                    !display.Equals("inline", StringComparison.OrdinalIgnoreCase) &&
                    !display.Equals("inline-block", StringComparison.OrdinalIgnoreCase))
                {
                    // A block-level child establishes its own formatting context.
                    // Its descendant line boxes are not line boxes of this
                    // inline-block, whose fallback baseline is its bottom margin edge.
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

        // Resolve and cache the per-style font metrics (lineHeight, baseline, descent,
        // resolved fontFamily/Size/Weight). All InlineTextMetrics callers reuse these
        // to avoid the LRU lookup in SkiaFontService for every measurement.
        private StyleFontInfo GetStyleFontInfo(CssComputed style)
        {
            if (style != null && s_styleFontCache.TryGetValue(style, out var cached))
            {
                return cached;
            }

            float fontSize = 16f;
            if (style?.FontSize != null) fontSize = (float)style.FontSize.Value;
            // Layout metrics must match paint, which renders the true computed size.
            // The historic 10px floor made sub-10px text (small print, brand links)
            // lay out wider than it paints, breaking centering and fit.
            fontSize = Math.Max(fontSize, 0.5f);

            int fontWeight = style?.FontWeight ?? 400;
            string fontFamily = style?.FontFamilyName ?? "sans-serif";

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

            var info = new StyleFontInfo
            {
                FontFamily = fontFamily,
                FontSize = fontSize,
                FontWeight = fontWeight,
                LineHeight = lineHeight,
                Baseline = baseline,
                Descent = descent,
            };

            if (style != null)
            {
                s_styleFontCache.AddOrUpdate(style, info);
            }
            return info;
        }

        private InlineTextMetrics MeasureTextMetrics(string text, CssComputed style)
        {
            var info = GetStyleFontInfo(style);

            if (string.IsNullOrEmpty(text))
            {
                // Width estimate for empty/whitespace probes — historic behavior.
                return new InlineTextMetrics(info.FontSize * 0.35f, info.LineHeight, info.Baseline, info.Descent);
            }

            float width = _fontService.MeasureTextWidth(text, info.FontFamily, info.FontSize, info.FontWeight);
            if (width <= 0)
            {
                width = Math.Max(info.FontSize * Math.Max(1, text.Length) * 0.35f, info.FontSize * 0.4f);
            }
            return new InlineTextMetrics(width, info.LineHeight, info.Baseline, info.Descent);
        }

        // Preserve a small side-bearing budget for very tight single-line runs.
        // Some fonts have negative/positive glyph overhang relative to advance width.
        // Without this, the first/last glyph can get clipped by a near-equal inline box.
        private static float ComputeInlineTextSideBearingSlack(
            TextLayoutBox textBox,
            List<TextLineInfo> segments,
            bool singleLineSegmentSet,
            float minX,
            float maxX,
            float maxSegmentWidth)
        {
            if (!singleLineSegmentSet || segments.Count == 0)
            {
                return 0f;
            }

            int totalChars = 0;
            foreach (var seg in segments)
            {
                totalChars += seg.Text?.Length ?? 0;
            }

            if (totalChars <= 0 || totalChars > 32)
            {
                return 0f;
            }

            float boxWidth = Math.Max(0f, maxX - minX);
            if (boxWidth <= 0f || maxSegmentWidth <= 0f)
            {
                return 0f;
            }

            // Trigger only for tight width envelopes where clipping risk is real.
            if (boxWidth > maxSegmentWidth + 0.75f)
            {
                return 0f;
            }

            float fontSize = (float)(textBox.ComputedStyle?.FontSize ?? 16.0);
            float slack;
            if (totalChars <= 12 && boxWidth <= 56f)
            {
                // Extra guard for short action labels where shaping/font fallback
                // can paint wider than the measured advance.
                slack = fontSize * 0.24f;
            }
            else
            {
                slack = fontSize * 0.09f;
            }
            if (!float.IsFinite(slack) || slack <= 0f)
            {
                return 0f;
            }

            return totalChars <= 12
                ? Math.Clamp(slack, 2f, 6f)
                : Math.Clamp(slack, 1f, 2f);
        }


        private void ResolveContextWidth(LayoutBox box, LayoutState state)
        {
            var widthResolution = LayoutConstraintResolver.ResolveWidth(state, "Inline.ResolveContextWidth", 800f);
            float rawAvailable = widthResolution.RawAvailable;
            bool widthUnconstrained = widthResolution.IsUnconstrained;
            float available = widthResolution.ResolvedAvailable;

            float finalW = available;

            bool isAnonymousBlock = box is AnonymousBlockBox;

            // Snapshot the style once. LayoutBox.ComputedStyle re-reads the box store on
            // every access, and flex sizing swaps Width/WidthPercent/WidthExpression on a
            // style in place while it lays a forced-size item out, so re-reading the
            // property between a HasValue check and the matching .Value read can observe
            // two different styles and throw.
            var style = isAnonymousBlock ? null : box.ComputedStyle;
            var p = style?.Padding ?? new Thickness();
            var b = style?.BorderThickness ?? new Thickness();
            var m = style?.Margin ?? new Thickness();

            float used = (float)(p.Left + p.Right + b.Left + b.Right + m.Left + m.Right);
            finalW = widthUnconstrained ? Math.Max(0f, available - used) : Math.Max(0f, rawAvailable - used);

            double? specifiedWidth = style?.Width;
            double? specifiedWidthPercent = style?.WidthPercent;
            string specifiedWidthExpression = style?.WidthExpression;

            if (specifiedWidth.HasValue)
            {
                finalW = (float)specifiedWidth.Value;
            }
            else if (specifiedWidthPercent.HasValue)
            {
                float cbWidth = LayoutBoxOps.ResolvePercentageBaseWidth(box, state);
                if (cbWidth > 0f)
                {
                    finalW = (float)(specifiedWidthPercent.Value / 100.0 * cbWidth);
                }
            }
            else if (!string.IsNullOrEmpty(specifiedWidthExpression))
            {
                float cbWidth = state.ContainingBlockWidth > 0 ? state.ContainingBlockWidth : state.ViewportWidth;
                finalW = LayoutHelper.EvaluateCssExpression(
                    specifiedWidthExpression,
                    cbWidth,
                    state.ViewportWidth,
                    state.ViewportHeight);
            }

            // box-sizing: border-box — the specified width includes padding+border,
            // so subtract them to get the content width.
            if (style != null &&
                string.Equals(style.BoxSizing, "border-box", StringComparison.OrdinalIgnoreCase) &&
                (specifiedWidth.HasValue || specifiedWidthPercent.HasValue ||
                 !string.IsNullOrEmpty(specifiedWidthExpression)))
            {
                float horizontalChrome = (float)(p.Left + p.Right + b.Left + b.Right);
                finalW = Math.Max(0f, finalW - horizontalChrome);
            }

            // CSS 2.1 §10.3.3: a block-level box with a specified width and `auto`
            // horizontal margins takes the remaining containing-block width as
            // margin (both auto → centred). Floats, out-of-flow and inline-level
            // boxes resolve auto margins to 0 and are left alone here.
            if (style != null &&
                !widthUnconstrained &&
                (style.MarginLeftAuto || style.MarginRightAuto) &&
                (specifiedWidth.HasValue || specifiedWidthPercent.HasValue || !string.IsNullOrEmpty(specifiedWidthExpression)) &&
                IsBlockLevelForAutoMargins(box, style))
            {
                float horizontalChrome = (float)(p.Left + p.Right + b.Left + b.Right);
                float marginLeft = style.MarginLeftAuto ? 0f : (float)m.Left;
                float marginRight = style.MarginRightAuto ? 0f : (float)m.Right;
                float remaining = rawAvailable - finalW - horizontalChrome - marginLeft - marginRight;
                if (style.MarginLeftAuto && style.MarginRightAuto)
                {
                    marginLeft = marginRight = Math.Max(0f, remaining / 2f);
                }
                else if (style.MarginLeftAuto)
                {
                    marginLeft = Math.Max(0f, remaining);
                }
                else
                {
                    marginRight = Math.Max(0f, remaining);
                }
                m = new Thickness(marginLeft, m.Top, marginRight, m.Bottom);
            }

            float left = box.Geometry.ContentBox.Left;
            float top = box.Geometry.ContentBox.Top;
            box.Geometry.ContentBox = new SKRect(left, top, left + finalW, top);

            box.Geometry.Padding = p;
            box.Geometry.Border = b;
            box.Geometry.Margin = m;

            LayoutBoxOps.SyncBoxes(box.Geometry);
        }

        private static bool IsBlockLevelForAutoMargins(LayoutBox box, CssComputed style)
        {
            if (box.IsOutOfFlow)
            {
                return false;
            }

            var floatValue = style.Float?.Trim().ToLowerInvariant();
            if (floatValue == "left" || floatValue == "right")
            {
                return false;
            }

            var display = style.Display?.Trim().ToLowerInvariant();
            return display is null or "block" or "flow-root" or "list-item";
        }

        private static float ResolveLineContentLimit(LayoutBox box, LayoutState state, bool isShrinkToFitProbe, bool isAnonymousBlock)
        {
            float contentWidth = box.Geometry?.ContentBox.Width ?? 0f;
            if (isAnonymousBlock || box.ComputedStyle == null)
            {
                return contentWidth;
            }

            if (isShrinkToFitProbe)
            {
                float intrinsicProbeWidth = float.PositiveInfinity;
                float ignoredHeight = Math.Max(0f, box.Geometry?.ContentBox.Height ?? 0f);
                ApplyMinMaxConstraints(box.ComputedStyle, state, ref intrinsicProbeWidth, ref ignoredHeight);

                return float.IsFinite(intrinsicProbeWidth)
                    ? Math.Max(0f, intrinsicProbeWidth)
                    : float.PositiveInfinity;
            }

            float constrainedWidth = contentWidth;
            float constrainedHeight = Math.Max(0f, box.Geometry?.ContentBox.Height ?? 0f);
            ApplyMinMaxConstraints(box.ComputedStyle, state, ref constrainedWidth, ref constrainedHeight);
            return constrainedWidth;
        }

        /// <summary>
        /// Computes the effective line width after subtracting float intrusions
        /// (CSS 2.1 §9.5). Returns (adjustedStartX, adjustedContentLimit) where
        /// adjustedStartX may be > 0 when a left float pushes the line rightward,
        /// and adjustedContentLimit is the remaining inline space after both
        /// left and right float intrusions are accounted for.
        /// </summary>
        private static (float startX, float contentLimit) ResolveFloatAdjustedLineSpace(
            FloatManager floatManager,
            float lineBfcY,
            float lineEstimatedHeight,
            float floatOriginX,
            float contentLimit,
            float containerWidth)
        {
            if (floatManager == null || !floatManager.HasFloats)
            {
                return (0f, contentLimit);
            }

            var space = floatManager.GetAvailableSpace(lineBfcY, Math.Max(1f, lineEstimatedHeight), containerWidth);
            float bfcLineLeft = floatOriginX;
            float bfcLineRight = floatOriginX + contentLimit;

            float leftIntrusion = Math.Max(0f, space.LeftOffset - bfcLineLeft);
            float rightIntrusion = Math.Max(0f, bfcLineRight - (containerWidth - space.RightOffset));

            // The line runs from the left intrusion to the right intrusion. Callers
            // advance curX from startX, so the limit is the line's RIGHT EDGE in the
            // same coordinate space, not the remaining width.
            float adjustedStartX = leftIntrusion;
            float adjustedLimit = Math.Max(adjustedStartX, contentLimit - rightIntrusion);

            return (adjustedStartX, adjustedLimit);
        }

        /// <summary>
        /// Applies CSS text-overflow: ellipsis (CSS Overflow 3 §5) to lines that
        /// exceed the content limit. Truncates text segments and appends the
        /// ellipsis glyph (U+2026) at the truncation point. Only active when
        /// overflow is non-visible (hidden/scroll/auto/clip).
        /// </summary>
        private void ApplyTextOverflow(
            LayoutBox container,
            List<LineBox> lines,
            Dictionary<TextLayoutBox, List<TextLineInfo>> textBoxLines,
            float contentLimit,
            bool isShrinkToFitProbe,
            float effectiveLineLimit)
        {
            if (isShrinkToFitProbe || lines.Count == 0 || textBoxLines.Count == 0)
            {
                return;
            }

            string overflow = container.ComputedStyle?.Overflow?.Trim().ToLowerInvariant() ?? "visible";
            string textOverflow = container.ComputedStyle?.TextOverflow?.Trim().ToLowerInvariant() ?? "clip";

            // text-overflow only applies when overflow is non-visible per spec
            if (overflow == "visible" || string.IsNullOrEmpty(textOverflow) || textOverflow == "clip")
            {
                return;
            }

            bool isEllipsis = textOverflow == "ellipsis" || textOverflow == "ellipsis-word";
            if (!isEllipsis)
            {
                return;
            }

            float lineLimit = effectiveLineLimit;
            if (lineLimit <= 0f || float.IsInfinity(lineLimit))
            {
                lineLimit = contentLimit;
            }

            if (lineLimit <= 0f || float.IsInfinity(lineLimit))
            {
                return;
            }

            // Measure the ellipsis glyph once. Use the font from the first text box
            // on each line (the line's dominant font).
            const string ellipsisChar = "…"; // …

            for (int lineIdx = 0; lineIdx < lines.Count; lineIdx++)
            {
                var line = lines[lineIdx];
                if (line.Width <= lineLimit + 0.5f || line.Items.Count == 0)
                {
                    continue;
                }

                // Find the primary font for this line from its text items
                CssComputed lineFontStyle = null;
                foreach (var item in line.Items)
                {
                    if (item is TextLayoutBox textBox && textBox.ComputedStyle != null)
                    {
                        lineFontStyle = textBox.ComputedStyle;
                        break;
                    }
                }

                if (lineFontStyle == null)
                {
                    continue;
                }

                float ellipsisWidth = MeasureString(ellipsisChar, lineFontStyle).Width;
                if (ellipsisWidth <= 0f)
                {
                    continue;
                }

                float availableForText = lineLimit - ellipsisWidth;
                if (availableForText <= 0f)
                {
                    // Line is too narrow — replace entire content with ellipsis
                    TruncateLineToEllipsisOnly(textBoxLines, lineIdx, ellipsisChar, ellipsisWidth);
                    line.Width = ellipsisWidth;
                    continue;
                }

                // Walk the line's text segments in document order and truncate
                // from the end until the remaining text + ellipsis fits.
                float truncateAt = availableForText;

                // Group segments by TextLayoutBox for truncation
                var lineSegments = new List<(TextLayoutBox textBox, TextLineInfo segment, int globalIdx)>();
                foreach (var kvp in textBoxLines)
                {
                    foreach (var seg in kvp.Value)
                    {
                        if (seg.LineIndex == lineIdx)
                        {
                            lineSegments.Add((kvp.Key, seg, 0));
                        }
                    }
                }

                // Sort by X position (document order)
                lineSegments.Sort((a, b) => a.segment.X.CompareTo(b.segment.X));

                // Find truncation point: first segment that exceeds the limit
                bool didTruncate = false;
                float runningWidth = 0f;
                for (int i = 0; i < lineSegments.Count; i++)
                {
                    var seg = lineSegments[i].segment;
                    if (runningWidth + seg.Width > truncateAt)
                    {
                        // Truncate this segment and remove all subsequent ones
                        float remaining = truncateAt - runningWidth;
                        if (remaining > 0f && seg.Text.Length > 1)
                        {
                            // Try to fit partial text
                            string truncatedText = TruncateTextToFit(
                                seg.Text, lineFontStyle, remaining);
                            seg.Text = truncatedText;
                            seg.Width = MeasureString(truncatedText, lineFontStyle).Width;
                            runningWidth += seg.Width;
                        }
                        else
                        {
                            // Remove this segment entirely — mark width as 0
                            // and text as empty; it will be filtered out downstream.
                            seg.Width = 0f;
                            seg.Text = string.Empty;
                        }

                        // Mark all subsequent segments on this line as removed
                        for (int j = i + 1; j < lineSegments.Count; j++)
                        {
                            lineSegments[j].segment.Width = 0f;
                            lineSegments[j].segment.Text = string.Empty;
                        }

                        didTruncate = true;
                        break;
                    }

                    runningWidth += seg.Width;
                }

                if (didTruncate)
                {
                    // Add ellipsis as a new segment at the truncation point.
                    // Attach it to the last visible TextLayoutBox on this line.
                    TextLayoutBox anchor = null;
                    for (int i = lineSegments.Count - 1; i >= 0; i--)
                    {
                        if (lineSegments[i].segment.Width > 0f)
                        {
                            anchor = lineSegments[i].textBox;
                            break;
                        }
                    }

                    if (anchor == null && lineSegments.Count > 0)
                    {
                        anchor = lineSegments[0].textBox;
                    }

                    if (anchor != null)
                    {
                        var ellipsisInfo = new TextLineInfo
                        {
                            Text = ellipsisChar,
                            X = runningWidth,
                            Width = ellipsisWidth,
                            Height = 0f, // will be resolved by line metrics
                            Baseline = 0f,
                            LineIndex = lineIdx
                        };

                        if (!textBoxLines.TryGetValue(anchor, out var anchorSegs))
                        {
                            anchorSegs = new List<TextLineInfo>();
                            textBoxLines[anchor] = anchorSegs;
                        }

                        anchorSegs.Add(ellipsisInfo);
                        runningWidth += ellipsisWidth;
                    }

                    line.Width = Math.Min(lineLimit, runningWidth);
                }
            }
        }

        private void TruncateLineToEllipsisOnly(
            Dictionary<TextLayoutBox, List<TextLineInfo>> textBoxLines,
            int lineIdx,
            string ellipsisChar,
            float ellipsisWidth)
        {
            // Remove all text segments on this line
            foreach (var kvp in textBoxLines)
            {
                kvp.Value.RemoveAll(seg => seg.LineIndex == lineIdx);
            }

            // Find a text box to carry the ellipsis
            TextLayoutBox anchor = null;
            foreach (var kvp in textBoxLines)
            {
                if (kvp.Value.Count > 0 || kvp.Key.ComputedStyle != null)
                {
                    anchor = kvp.Key;
                    break;
                }
            }

            if (anchor != null)
            {
                var ellipsisInfo = new TextLineInfo
                {
                    Text = ellipsisChar,
                    X = 0f,
                    Width = ellipsisWidth,
                    Height = 0f,
                    Baseline = 0f,
                    LineIndex = lineIdx
                };

                if (textBoxLines.TryGetValue(anchor, out var segs))
                {
                    segs.Add(ellipsisInfo);
                }
                else
                {
                    textBoxLines[anchor] = new List<TextLineInfo> { ellipsisInfo };
                }
            }
        }

        /// <summary>
        /// Truncates text to fit within the given pixel width by removing characters
        /// from the end and appending the ellipsis. Uses binary search for efficiency.
        /// </summary>
        private string TruncateTextToFit(string text, CssComputed style, float maxWidth)
        {
            if (string.IsNullOrEmpty(text) || maxWidth <= 0f)
            {
                return string.Empty;
            }

            // Quick check: does the full text fit?
            float fullWidth = MeasureString(text, style).Width;
            if (fullWidth <= maxWidth)
            {
                return text;
            }

            // Binary search for the longest prefix that fits
            int lo = 1;
            int hi = text.Length;
            while (lo < hi)
            {
                int mid = (lo + hi + 1) / 2;
                string prefix = text.Substring(0, mid);
                float prefixWidth = MeasureString(prefix, style).Width;
                if (prefixWidth <= maxWidth)
                {
                    lo = mid;
                }
                else
                {
                    hi = mid - 1;
                }
            }

            return text.Substring(0, lo);
        }

    }
}


