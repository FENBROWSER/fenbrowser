using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using FenBrowser.Core.Css;
using FenBrowser.Core.Dom.V2;
using FenBrowser.FenEngine.Layout.Tree;
using FenBrowser.FenEngine.Typography;
using SkiaSharp;

namespace FenBrowser.FenEngine.Layout.Contexts
{
    /// <summary>
    /// Resolves the keyword values of width, min-width, max-width, height, min-height and
    /// max-height (CSS Sizing 3 §3.1 / §5 and CSS Sizing 4 §3: min-content, max-content,
    /// fit-content, fit-content(&lt;length-percentage&gt;) and stretch with its
    /// -webkit-fill-available / -moz-available aliases) to lengths before a box is laid
    /// out, so every formatting context sees plain sizes. Left unresolved they evaluated
    /// to 0 or to the containing block, and the flex layout's old zero-size rescue passes
    /// had been papering over the zeros.
    /// </summary>
    internal static class SizingKeywordResolver
    {
        private enum Keyword { None, MinContent, MaxContent, FitContent, FitContentLength, Stretch }

        // A box's min-/max-content width depends only on its contents, which one layout
        // call does not change, so each is measured once per top-level layout call.
        // Without this every nested keyword-sized box re-probed its subtree for each
        // probe of its ancestors - exponential in the nesting depth (a 33 s YouTube layout).
        [ThreadStatic] private static Dictionary<LayoutBox, float> t_maxContent;
        [ThreadStatic] private static Dictionary<LayoutBox, float> t_minContent;

        /// <summary>Forgets the measurements of the previous top-level layout call.</summary>
        public static void BeginLayoutPass()
        {
            t_maxContent?.Clear();
            t_minContent?.Clear();
        }

        private struct Saved
        {
            public double? Width, WidthPercent, MinWidth, MinWidthPercent, MaxWidth, MaxWidthPercent;
            public double? Height, HeightPercent, MinHeight, MinHeightPercent, MaxHeight, MaxHeightPercent;
            public string WidthExpression, MinWidthExpression, MaxWidthExpression;
            public string HeightExpression, MinHeightExpression, MaxHeightExpression;
        }

        public static bool TryLayout(FormattingContext context, LayoutBox box, LayoutState state, Action<LayoutBox, LayoutState> layoutCore)
        {
            var style = box?.ComputedStyle;
            if (style == null || box is TextLayoutBox ||
                context is TableFormattingContext || context is GridFormattingContext)
            {
                return false;
            }

            var width = Classify(style.WidthExpression, style.Width, style.WidthPercent);
            var minWidth = Classify(style.MinWidthExpression, style.MinWidth, style.MinWidthPercent);
            var maxWidth = Classify(style.MaxWidthExpression, style.MaxWidth, style.MaxWidthPercent);
            var height = Classify(style.HeightExpression, style.Height, style.HeightPercent);
            // min-/max-height content keywords are the box's content height, which the
            // block formatting context applies during its own layout; only stretch is
            // resolved up front.
            var minHeight = StretchOnly(Classify(style.MinHeightExpression, style.MinHeight, style.MinHeightPercent));
            var maxHeight = StretchOnly(Classify(style.MaxHeightExpression, style.MaxHeight, style.MaxHeightPercent));
            if (width == Keyword.None && minWidth == Keyword.None && maxWidth == Keyword.None &&
                height == Keyword.None && minHeight == Keyword.None && maxHeight == Keyword.None)
            {
                return false;
            }

            // Sizing properties do not apply to non-replaced inline boxes (CSS 2.1 §10.3.1).
            string display = style.Display?.Trim().ToLowerInvariant();
            if ((display == "inline" || display == "contents") &&
                !(box.SourceNode is Element element &&
                  ReplacedElementSizing.IsReplacedElementTag(element.TagName?.ToUpperInvariant() ?? string.Empty)))
            {
                return false;
            }

            var saved = Save(style);
            try
            {
                Resolve(box, style, saved, state, width, minWidth, maxWidth, height, minHeight, maxHeight);
                layoutCore(box, state);
            }
            finally
            {
                Restore(style, saved);
            }

            return true;
        }

        private static void Resolve(
            LayoutBox box, CssComputed style, Saved saved, LayoutState state,
            Keyword width, Keyword minWidth, Keyword maxWidth, Keyword height, Keyword minHeight, Keyword maxHeight)
        {
            bool borderBox = string.Equals(style.BoxSizing, "border-box", StringComparison.OrdinalIgnoreCase);
            float chromeW = (float)(style.Padding.Left + style.Padding.Right + style.BorderThickness.Left + style.BorderThickness.Right);
            float chromeH = (float)(style.Padding.Top + style.Padding.Bottom + style.BorderThickness.Top + style.BorderThickness.Bottom);
            float marginsW = (float)(style.Margin.Left + style.Margin.Right);
            float marginsH = (float)(style.Margin.Top + style.Margin.Bottom);

            // Every keyword is taken off the style first: the measurements below and the
            // layout must see plain sizes (or auto) only.
            if (width != Keyword.None) style.WidthExpression = null;
            if (minWidth != Keyword.None) style.MinWidthExpression = null;
            if (maxWidth != Keyword.None) style.MaxWidthExpression = null;
            if (height != Keyword.None) style.HeightExpression = null;
            if (minHeight != Keyword.None) style.MinHeightExpression = null;
            if (maxHeight != Keyword.None) style.MaxHeightExpression = null;

            // A flex item's cross size of `stretch` fills its flex line, which is what an
            // auto cross size under the default alignment does (CSS Sizing 4 §3.1).
            ResolveFlexItemAxes(box, out bool crossIsHorizontal, out bool isFlexItem);

            // A flex item's stretch-fit size is against its flex container's content box
            // (CSS Sizing 4 §3.1); the flex passes hand items their own forced size as
            // the available space and containing block.
            var flexContainer = isFlexItem ? box.Parent?.Geometry?.ContentBox : null;

            // --- inline axis --------------------------------------------------------
            float available = flexContainer is SKRect containerBox && containerBox.Width > 0f
                ? containerBox.Width
                : state.AvailableSize.Width;
            float stretchWidth = float.IsFinite(available) && available > 0f
                ? Math.Max(0f, available - marginsW - chromeW)
                : float.NaN;

            float MaxContent()
            {
                t_maxContent ??= new Dictionary<LayoutBox, float>();
                if (t_maxContent.TryGetValue(box, out float known)) return known;
                float measured;
                var widths = TakeWidths(style);
                try { measured = Math.Max(0f, MeasureContentOuterWidth(box, minContent: false) - chromeW - marginsW); }
                finally { PutWidths(style, widths); }
                t_maxContent[box] = measured;
                return measured;
            }
            float MinContent()
            {
                t_minContent ??= new Dictionary<LayoutBox, float>();
                if (t_minContent.TryGetValue(box, out float known)) return known;
                float measured;
                var widths = TakeWidths(style);
                try { measured = Math.Max(0f, MeasureContentOuterWidth(box, minContent: true) - chromeW - marginsW); }
                finally { PutWidths(style, widths); }
                t_minContent[box] = measured;
                return measured;
            }
            float FitContent(float limit) => float.IsFinite(limit)
                ? Math.Min(MaxContent(), Math.Max(MinContent(), limit))
                : MaxContent();

            // Content sizes are content-box measurements; style values follow box-sizing.
            double? AsStyleWidth(float contentWidth) => borderBox ? contentWidth + chromeW : contentWidth;

            float? WidthFor(Keyword keyword, string expression)
            {
                switch (keyword)
                {
                    case Keyword.MinContent: return MinContent();
                    case Keyword.MaxContent: return MaxContent();
                    case Keyword.FitContent:
                        return FitContent(float.IsFinite(available) ? available - marginsW - chromeW : float.PositiveInfinity);
                    case Keyword.FitContentLength:
                    {
                        float basis = state.ContainingBlockWidth > 0 ? state.ContainingBlockWidth : available;
                        float limit = LayoutHelper.EvaluateCssExpression(FitContentArgument(expression), basis,
                            state.ViewportWidth, state.ViewportHeight, (float)(style.FontSize ?? 16d));
                        if (borderBox) limit -= chromeW;
                        return FitContent(limit);
                    }
                    case Keyword.Stretch:
                        if (isFlexItem && crossIsHorizontal) return null;
                        return float.IsFinite(stretchWidth) ? stretchWidth : null;
                }
                return null;
            }

            if (width != Keyword.None && WidthFor(width, saved.WidthExpression) is float w)
                style.Width = AsStyleWidth(w);
            if (minWidth != Keyword.None && WidthFor(minWidth, saved.MinWidthExpression) is float minW)
                style.MinWidth = AsStyleWidth(minW);
            if (maxWidth != Keyword.None && WidthFor(maxWidth, saved.MaxWidthExpression) is float maxW)
                style.MaxWidth = AsStyleWidth(maxW);

            // --- block axis ---------------------------------------------------------
            float cbHeight = flexContainer is SKRect containerContent && containerContent.Height > 0f
                ? containerContent.Height
                : state.ContainingBlockHeight;
            float stretchHeight = float.IsFinite(cbHeight) && cbHeight > 0f
                ? Math.Max(0f, cbHeight - marginsH - chromeH)
                : float.NaN;
            bool heightStretchesWithLine = isFlexItem && !crossIsHorizontal;
            double? AsStyleHeight(float contentHeight) => borderBox ? contentHeight + chromeH : contentHeight;

            // height: the content keywords are its automatic size, already in force now
            // that the expression is gone.
            if (height == Keyword.Stretch && !heightStretchesWithLine && float.IsFinite(stretchHeight))
                style.Height = AsStyleHeight(stretchHeight);

            // min-/max-height: stretch take the containing block for flex items as well;
            // only the preferred cross size follows the flex line.
            float? HeightFor(Keyword keyword) =>
                keyword == Keyword.Stretch && float.IsFinite(stretchHeight) ? stretchHeight : null;

            if (HeightFor(minHeight) is float minH) style.MinHeight = AsStyleHeight(minH);
            if (HeightFor(maxHeight) is float maxH) style.MaxHeight = AsStyleHeight(maxH);
        }

        private static readonly SkiaFontService s_fontService = new();
        private static readonly Regex s_collapsibleWhitespace = new(@"\s+", RegexOptions.Compiled);

        /// <summary>
        /// A box's min-content or max-content contribution (CSS Sizing 3 §5.1) measured from
        /// its subtree without laying it out: text measured by the same font service the
        /// inline layout uses (its widest word, or its whole line), inline-level siblings
        /// side by side, block-level children and column flex items stacked, row flex items
        /// in a row. Laying the box out at an unbounded width answered the same question but
        /// cost a full subtree layout per keyword-sized box.
        /// </summary>
        internal static float MeasureContentOuterWidth(LayoutBox box, bool minContent)
        {
            if (box == null || box.IsOutOfFlow || box.Geometry == null)
            {
                return 0f;
            }

            var style = box.ComputedStyle;
            if (style?.Display?.Contains("none", StringComparison.OrdinalIgnoreCase) == true)
            {
                return 0f;
            }

            if (box is TextLayoutBox || box.SourceNode is Text)
            {
                return MeasureText(box, style, minContent);
            }

            if (style == null)
            {
                return MeasureChildren(box, null, minContent);
            }

            float padding = (float)(style.Padding.Left + style.Padding.Right + style.BorderThickness.Left + style.BorderThickness.Right);
            float margins = (float)(style.Margin.Left + style.Margin.Right);

            if (box.SourceNode is Element element &&
                ReplacedElementSizing.IsReplacedElementTag(element.TagName?.ToUpperInvariant() ?? string.Empty))
            {
                return Math.Max(0f, box.Geometry.MarginBox.Width);
            }

            float content;
            if (style.Width is double specified && specified >= 0d)
            {
                content = string.Equals(style.BoxSizing, "border-box", StringComparison.OrdinalIgnoreCase)
                    ? Math.Max(0f, (float)specified - padding)
                    : (float)specified;
            }
            else
            {
                content = MeasureChildren(box, style, minContent);
            }

            if (style.MaxWidth is double maxWidth && maxWidth >= 0d) content = Math.Min(content, (float)maxWidth);
            if (style.MinWidth is double minWidth && minWidth > 0d) content = Math.Max(content, (float)minWidth);
            return content + padding + margins;
        }

        private static float MeasureText(LayoutBox box, CssComputed style, bool minContent)
        {
            string text = (box as TextLayoutBox)?.TextContent ?? (box.SourceNode as Text)?.Data;
            if (string.IsNullOrEmpty(text) || style == null)
            {
                return 0f;
            }

            string whiteSpace = style.WhiteSpace?.Trim().ToLowerInvariant() ?? "normal";
            bool preserveSpaces = whiteSpace is "pre" or "pre-wrap" or "break-spaces";
            bool preserveBreaks = preserveSpaces || whiteSpace == "pre-line";
            bool wraps = whiteSpace is not ("nowrap" or "pre");

            string family = style.FontFamilyName ?? "sans-serif";
            float size = (float)(style.FontSize ?? 16d);
            int weight = style.FontWeight ?? 400;
            float Measure(string piece) => string.IsNullOrEmpty(piece) ? 0f : s_fontService.MeasureTextWidth(piece, family, size, weight);

            var lines = preserveBreaks ? text.Split('\n') : new[] { text };
            float widest = 0f;
            foreach (var rawLine in lines)
            {
                string line = preserveSpaces ? rawLine : s_collapsibleWhitespace.Replace(rawLine, " ");
                if (minContent && wraps)
                {
                    foreach (var word in line.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                    {
                        widest = Math.Max(widest, Measure(word));
                    }
                }
                else
                {
                    widest = Math.Max(widest, Measure(preserveSpaces ? line : line.Trim()));
                }
            }

            return widest;
        }

        private static float MeasureChildren(LayoutBox box, CssComputed style, bool minContent)
        {
            string display = style?.Display?.Trim().ToLowerInvariant() ?? string.Empty;
            if (display is "flex" or "inline-flex")
            {
                string direction = style.FlexDirection?.Trim().ToLowerInvariant() ?? "row";
                string wrap = style.FlexWrap?.Trim().ToLowerInvariant() ?? "nowrap";
                bool row = direction is "row" or "row-reverse";
                // A single-line row sums its items either way; a wrapping row's
                // min-content is its widest item (CSS Flexbox 1 §9.9.1).
                bool sum = row && (!minContent || wrap == "nowrap");
                float total = 0f;
                int items = 0;
                foreach (var child in box.Children)
                {
                    if (child == null || child.IsOutOfFlow) continue;
                    float width = MeasureContentOuterWidth(child, minContent);
                    total = sum ? total + width : Math.Max(total, width);
                    items++;
                }

                if (sum && items > 1)
                {
                    total += (float)Math.Max(0d, style.ColumnGap ?? style.Gap ?? 0d) * (items - 1);
                }

                return total;
            }

            // A block container. For max-content, runs of inline-level content (and
            // floats) sit on one line and block-level children each take their own; for
            // min-content every piece may break onto its own line unless wrapping is off.
            string whiteSpace = style?.WhiteSpace?.Trim().ToLowerInvariant() ?? "normal";
            bool joinInline = !minContent || whiteSpace is "nowrap" or "pre";
            float widest = 0f;
            float line = 0f;
            foreach (var child in box.Children)
            {
                if (child == null || child.IsOutOfFlow) continue;
                float width = MeasureContentOuterWidth(child, minContent);
                if (joinInline && IsInlineLevelOrFloat(child))
                {
                    line += width;
                }
                else
                {
                    widest = Math.Max(widest, Math.Max(line, width));
                    line = 0f;
                }
            }

            return Math.Max(widest, line);
        }

        private static bool IsInlineLevelOrFloat(LayoutBox box)
        {
            if (box is TextLayoutBox || box is InlineBox) return true;
            var style = box.ComputedStyle;
            string display = style?.Display?.Trim().ToLowerInvariant() ?? string.Empty;
            if (display.StartsWith("inline", StringComparison.Ordinal)) return true;
            string floatValue = style?.Float?.Trim().ToLowerInvariant();
            return floatValue is "left" or "right";
        }

        // The box's own width constraints are what is being resolved, so its content
        // contributions are measured without them.
        private static (double? W, double? WP, double? Min, double? MinP, double? Max, double? MaxP) TakeWidths(CssComputed style)
        {
            var taken = (style.Width, style.WidthPercent, style.MinWidth, style.MinWidthPercent, style.MaxWidth, style.MaxWidthPercent);
            style.Width = null; style.WidthPercent = null;
            style.MinWidth = null; style.MinWidthPercent = null;
            style.MaxWidth = null; style.MaxWidthPercent = null;
            return taken;
        }

        private static void PutWidths(CssComputed style, (double? W, double? WP, double? Min, double? MinP, double? Max, double? MaxP) taken)
        {
            style.Width = taken.W; style.WidthPercent = taken.WP;
            style.MinWidth = taken.Min; style.MinWidthPercent = taken.MinP;
            style.MaxWidth = taken.Max; style.MaxWidthPercent = taken.MaxP;
        }

        private static void ResolveFlexItemAxes(LayoutBox box, out bool crossIsHorizontal, out bool isFlexItem)
        {
            crossIsHorizontal = false;
            isFlexItem = false;
            var parentStyle = box.Parent?.ComputedStyle;
            string parentDisplay = parentStyle?.Display?.Trim().ToLowerInvariant();
            if (parentDisplay != "flex" && parentDisplay != "inline-flex")
            {
                return;
            }

            isFlexItem = true;
            string direction = parentStyle.FlexDirection?.Trim().ToLowerInvariant();
            crossIsHorizontal = direction == "column" || direction == "column-reverse";
        }

        /// <summary>True for stretch and its -webkit-fill-available / -moz-available aliases.</summary>
        internal static bool IsStretchKeyword(string expression) =>
            Classify(expression, null, null) == Keyword.Stretch;

        private static Keyword StretchOnly(Keyword keyword) => keyword == Keyword.Stretch ? keyword : Keyword.None;

        private static Keyword Classify(string expression, double? length, double? percent)
        {
            if (length.HasValue || percent.HasValue || string.IsNullOrWhiteSpace(expression))
            {
                return Keyword.None;
            }

            switch (expression.Trim().ToLowerInvariant())
            {
                case "min-content":
                case "-webkit-min-content":
                case "-moz-min-content":
                    return Keyword.MinContent;
                case "max-content":
                case "-webkit-max-content":
                case "-moz-max-content":
                    return Keyword.MaxContent;
                case "fit-content":
                case "-webkit-fit-content":
                case "-moz-fit-content":
                    return Keyword.FitContent;
                case "stretch":
                case "-webkit-fill-available":
                case "-moz-available":
                case "fill-available":
                    return Keyword.Stretch;
            }

            return LayoutHelper.IsContentBasedSizeKeyword(expression) ? Keyword.FitContentLength : Keyword.None;
        }

        private static string FitContentArgument(string expression)
        {
            string trimmed = expression.Trim();
            int open = trimmed.IndexOf('(');
            return open < 0 ? trimmed : trimmed.Substring(open + 1, trimmed.Length - open - 2);
        }

        private static Saved Save(CssComputed style) => new Saved
        {
            Width = style.Width, WidthPercent = style.WidthPercent, WidthExpression = style.WidthExpression,
            MinWidth = style.MinWidth, MinWidthPercent = style.MinWidthPercent, MinWidthExpression = style.MinWidthExpression,
            MaxWidth = style.MaxWidth, MaxWidthPercent = style.MaxWidthPercent, MaxWidthExpression = style.MaxWidthExpression,
            Height = style.Height, HeightPercent = style.HeightPercent, HeightExpression = style.HeightExpression,
            MinHeight = style.MinHeight, MinHeightPercent = style.MinHeightPercent, MinHeightExpression = style.MinHeightExpression,
            MaxHeight = style.MaxHeight, MaxHeightPercent = style.MaxHeightPercent, MaxHeightExpression = style.MaxHeightExpression,
        };

        private static void Restore(CssComputed style, Saved saved)
        {
            style.Width = saved.Width; style.WidthPercent = saved.WidthPercent; style.WidthExpression = saved.WidthExpression;
            style.MinWidth = saved.MinWidth; style.MinWidthPercent = saved.MinWidthPercent; style.MinWidthExpression = saved.MinWidthExpression;
            style.MaxWidth = saved.MaxWidth; style.MaxWidthPercent = saved.MaxWidthPercent; style.MaxWidthExpression = saved.MaxWidthExpression;
            style.Height = saved.Height; style.HeightPercent = saved.HeightPercent; style.HeightExpression = saved.HeightExpression;
            style.MinHeight = saved.MinHeight; style.MinHeightPercent = saved.MinHeightPercent; style.MinHeightExpression = saved.MinHeightExpression;
            style.MaxHeight = saved.MaxHeight; style.MaxHeightPercent = saved.MaxHeightPercent; style.MaxHeightExpression = saved.MaxHeightExpression;
        }
    }
}
