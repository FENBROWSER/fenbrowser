using System;
using FenBrowser.Core;
using FenBrowser.Core.Css;
using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Logging;
using FenBrowser.FenEngine.Layout.Contexts;
using FenBrowser.FenEngine.Layout.Tree;
using SkiaSharp;

namespace FenBrowser.FenEngine.Layout
{
    public static class LayoutPositioningLogic
    {
        public static void ResolvePositionedBox(
            LayoutBox box,
            LayoutBox containingBlock,
            BoxModel containerGeometry,
            LayoutState? state = null,
            bool collapsePositioningMarginsInFinalGeometry = false)
        {
            if (box?.ComputedStyle == null || box.Geometry == null || containerGeometry == null) return;

            var style = box.ComputedStyle;

            // For abs/fixed positioning, CSS uses the containing block's padding box.
            SKRect cbRect;
            var effectivePosition = LayoutStyleResolver.GetEffectivePosition(style);
            bool isFixed = string.Equals(effectivePosition, "fixed", StringComparison.OrdinalIgnoreCase);
            bool useViewportContainingBlock =
                isFixed &&
                state.HasValue &&
                state.Value.ViewportWidth > 0 &&
                state.Value.ViewportHeight > 0;

            if (useViewportContainingBlock)
            {
                cbRect = new SKRect(0, 0, state.Value.ViewportWidth, state.Value.ViewportHeight);
            }
            else if (isFixed)
            {
                // Fixed-position elements are viewport-anchored. If the viewport metrics are
                // temporarily unavailable, keep the origin at (0,0) and fall back to the
                // current layout bounds only for available size, never for document offsets.
                float fallbackWidth = state.HasValue && state.Value.ViewportWidth > 0
                    ? state.Value.ViewportWidth
                    : Math.Max(containerGeometry.PaddingBox.Width, containerGeometry.ContentBox.Width);
                float fallbackHeight = state.HasValue && state.Value.ViewportHeight > 0
                    ? state.Value.ViewportHeight
                    : Math.Max(containerGeometry.PaddingBox.Height, containerGeometry.ContentBox.Height);
                cbRect = new SKRect(0, 0, Math.Max(0f, fallbackWidth), Math.Max(0f, fallbackHeight));
            }
            else
            {
                cbRect = containerGeometry.PaddingBox;
                if (cbRect.Width <= 0 || cbRect.Height <= 0)
                {
                    cbRect = containerGeometry.ContentBox;
                }
            }

            float intrinsicWidth = box.Geometry.ContentBox.Width;
            float intrinsicHeight = box.Geometry.ContentBox.Height;
            EnsureIntrinsicSize(box, style, ref intrinsicWidth, ref intrinsicHeight);
            NormalizeIntrinsicSizeForAutoPositionedBox(box, style, cbRect, ref intrinsicWidth, ref intrinsicHeight);

            var cb = new ContainingBlock
            {
                Width = Math.Max(0f, cbRect.Width),
                Height = Math.Max(0f, cbRect.Height),
                X = cbRect.Left,
                Y = cbRect.Top,
                PaddingBox = cbRect
            };

            var solved = AbsolutePositionSolver.Solve(
                style,
                cb,
                Math.Max(0f, intrinsicWidth),
                Math.Max(0f, intrinsicHeight));

            if (isFixed && box.SourceNode is Element element)
            {
                DiagnosticPaths.AppendRootText(
                    "layout_engine_debug.txt",
                    $"[ResolvePositionedBox] <{element.TagName}> fixed cb=({cbRect.Left:F1},{cbRect.Top:F1} {cbRect.Width:F1}x{cbRect.Height:F1}) solved=({solved.X:F1},{solved.Y:F1} {solved.Width:F1}x{solved.Height:F1}) viewport=({(state.HasValue ? state.Value.ViewportWidth : 0):F1}x{(state.HasValue ? state.Value.ViewportHeight : 0):F1})\n");
            }

            float left = cbRect.Left + solved.X;
            float top = cbRect.Top + solved.Y;
            float width = Math.Max(0f, solved.Width);
            float height = Math.Max(0f, solved.Height);
            float previousContentLeft = box.Geometry.ContentBox.Left;
            float previousContentTop = box.Geometry.ContentBox.Top;

            box.Geometry.ContentBox = new SKRect(left, top, left + width, top + height);
            box.Geometry.Padding = style.Padding;
            box.Geometry.Border = style.BorderThickness;
            if (collapsePositioningMarginsInFinalGeometry &&
                (string.Equals(effectivePosition, "absolute", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(effectivePosition, "fixed", StringComparison.OrdinalIgnoreCase)))
            {
                // The positioned offsets already include the resolved margins. Keeping those
                // margins in the stored box model inflates the final MarginBox width/height
                // and breaks follow-up paint/layout consumers that treat MarginBox as the
                // element's visual extents after positioning.
                box.Geometry.Margin = new Thickness();
            }
            else
            {
                box.Geometry.Margin = new Thickness(
                    solved.MarginLeft,
                    solved.MarginTop,
                    solved.MarginRight,
                    solved.MarginBottom);
            }

            SyncBoxes(box.Geometry);

            float dx = box.Geometry.ContentBox.Left - previousContentLeft;
            float dy = box.Geometry.ContentBox.Top - previousContentTop;
            if ((Math.Abs(dx) > 0.01f || Math.Abs(dy) > 0.01f) && box.Children.Count > 0)
            {
                LayoutBoxOps.ShiftDescendants(box, dx, dy);
            }
        }

        private static void EnsureIntrinsicSize(LayoutBox box, CssComputed style, ref float width, ref float height)
        {
            if (!float.IsFinite(width) || width < 0f) width = 0f;
            if (!float.IsFinite(height) || height < 0f) height = 0f;

            if (style.Width.HasValue)
            {
                width = (float)style.Width.Value;
            }
            if (style.Height.HasValue)
            {
                height = (float)style.Height.Value;
            }

            if (box.SourceNode is not Element element)
            {
                if (height <= 0f && style.LineHeight.HasValue) height = (float)style.LineHeight.Value;
                return;
            }

            string tag = element.TagName?.ToUpperInvariant() ?? string.Empty;

            if (ReplacedElementSizing.IsReplacedElementTag(tag))
            {
                if (tag == "OBJECT" && ReplacedElementSizing.ShouldUseObjectFallbackContent(element))
                {
                    return;
                }

                float attrW = 0f;
                float attrH = 0f;
                ReplacedElementSizing.TryGetLengthAttribute(element, "width", out attrW);
                ReplacedElementSizing.TryGetLengthAttribute(element, "height", out attrH);

                float intrinsicW = width;
                float intrinsicH = height;
                if ((intrinsicW <= 0f || intrinsicH <= 0f) &&
                    ReplacedElementSizing.TryResolveIntrinsicSizeFromElement(tag, element, out float parsedIntrinsicW, out float parsedIntrinsicH))
                {
                    if (intrinsicW <= 0f) intrinsicW = parsedIntrinsicW;
                    if (intrinsicH <= 0f) intrinsicH = parsedIntrinsicH;
                }

                var resolved = ReplacedElementSizing.ResolveReplacedSize(
                    tag,
                    style,
                    new SKSize(float.PositiveInfinity, float.PositiveInfinity),
                    intrinsicW,
                    intrinsicH,
                    attrW,
                    attrH,
                    constrainAutoToAvailableWidth: false);

                width = resolved.Width;
                height = resolved.Height;
                return;
            }

            if (width <= 0f)
            {
                if (TryParseLengthAttribute(element, "width", out var attrW)) width = attrW;
                else if (tag == "INPUT") width = 150f;
                else if (tag == "TEXTAREA") width = 200f;
                else if (tag == "SELECT") width = 120f;
                else if (tag == "BUTTON")
                {
                    string label = LayoutHelper.GetRenderableTextContentTrimmed(element);
                    if (string.IsNullOrWhiteSpace(label)) label = "Button";
                    width = Math.Max(54f, 16f + (label.Length * 7f));
                }
            }

            if (height <= 0f)
            {
                if (TryParseLengthAttribute(element, "height", out var attrH)) height = attrH;
                else if (style.LineHeight.HasValue && style.LineHeight.Value > 0) height = (float)style.LineHeight.Value;
                else if (tag == "INPUT" || tag == "SELECT") height = 24f;
                else if (tag == "BUTTON") height = 36f;
                else if (tag == "TEXTAREA") height = 48f;
            }
        }

        private static void NormalizeIntrinsicSizeForAutoPositionedBox(
            LayoutBox box,
            CssComputed style,
            SKRect containingBlockRect,
            ref float intrinsicWidth,
            ref float intrinsicHeight)
        {
            if (style == null || box?.SourceNode is not Element element)
            {
                return;
            }

            bool hasAutoWidth = !style.Width.HasValue && !style.WidthPercent.HasValue && string.IsNullOrWhiteSpace(style.WidthExpression);
            bool hasAutoHeight = !style.Height.HasValue && !style.HeightPercent.HasValue && string.IsNullOrWhiteSpace(style.HeightExpression);
            if (!hasAutoWidth && !hasAutoHeight)
            {
                return;
            }

            string text = LayoutHelper.GetRenderableTextContentTrimmed(element);
            if (string.IsNullOrWhiteSpace(text))
            {
                return;
            }

            float fontSize = style.FontSize.HasValue && style.FontSize.Value > 0
                ? (float)style.FontSize.Value
                : 16f;
            float lineHeight = style.LineHeight.HasValue && style.LineHeight.Value > 0
                ? (float)style.LineHeight.Value
                : fontSize * 1.2f;
            float contentWidthEstimate = Math.Max(fontSize * 0.6f, text.Length * fontSize * 0.55f);
            float contentHeightEstimate = Math.Max(lineHeight, fontSize);

            if (hasAutoWidth)
            {
                float cbWidth = Math.Max(0f, containingBlockRect.Width);
                bool looksLikeBlockStretch = intrinsicWidth > 0f && (intrinsicWidth >= cbWidth * 0.70f || intrinsicWidth >= contentWidthEstimate * 2f);
                if (looksLikeBlockStretch || intrinsicWidth <= 0f)
                {
                    intrinsicWidth = contentWidthEstimate;
                }
            }

            if (hasAutoHeight)
            {
                float cbHeight = Math.Max(0f, containingBlockRect.Height);
                bool looksLikeBlockStretch = intrinsicHeight > 0f && (intrinsicHeight >= cbHeight * 0.70f || intrinsicHeight >= contentHeightEstimate * 2f);
                if (looksLikeBlockStretch || intrinsicHeight <= 0f)
                {
                    intrinsicHeight = contentHeightEstimate;
                }
            }
        }

        private static bool TryParseLengthAttribute(Element element, string name, out float value)
        {
            value = 0f;
            string raw = element.GetAttribute(name);
            if (string.IsNullOrWhiteSpace(raw)) return false;

            raw = raw.Trim();
            int i = 0;
            while (i < raw.Length)
            {
                char ch = raw[i];
                if ((ch >= '0' && ch <= '9') || ch == '.' || ch == '-') { i++; continue; }
                break;
            }

            if (i == 0) return false;

            if (!float.TryParse(raw.Substring(0, i), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out value))
            {
                value = 0f;
                return false;
            }

            value = Math.Max(0f, value);
            return true;
        }
        
        private static void SyncBoxes(BoxModel geometry)
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
