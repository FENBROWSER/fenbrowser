using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using FenBrowser.Core.Css;
using SkiaSharp;

namespace FenBrowser.FenEngine.Layout
{
    internal static class LayoutStyleResolver
    {
        public static void NormalizeForLayout(CssComputed style)
        {
            if (style?.Map == null)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(style.Display) &&
                style.Map.TryGetValue("display", out var mappedDisplay))
            {
                style.Display = mappedDisplay;
            }

            if (string.IsNullOrWhiteSpace(style.Visibility) &&
                style.Map.TryGetValue("visibility", out var mappedVisibility))
            {
                style.Visibility = mappedVisibility;
            }

            if (string.IsNullOrWhiteSpace(style.Position) &&
                style.Map.TryGetValue("position", out var mappedPosition))
            {
                style.Position = mappedPosition;
            }

            if (string.IsNullOrWhiteSpace(style.FlexDirection) && style.Map.TryGetValue("flex-direction", out var mapFlexDir))
                style.FlexDirection = mapFlexDir;
            if (string.IsNullOrWhiteSpace(style.FlexWrap) && style.Map.TryGetValue("flex-wrap", out var mapFlexWrap))
                style.FlexWrap = mapFlexWrap;
            if (string.IsNullOrWhiteSpace(style.JustifyContent) && style.Map.TryGetValue("justify-content", out var mapJustify))
                style.JustifyContent = mapJustify;
            if (string.IsNullOrWhiteSpace(style.AlignItems) && style.Map.TryGetValue("align-items", out var mapAlign))
                style.AlignItems = mapAlign;
            if (string.IsNullOrWhiteSpace(style.AlignContent) && style.Map.TryGetValue("align-content", out var mapAlignContent))
                style.AlignContent = mapAlignContent;

            if (style.Overflow == null && style.Map.TryGetValue("overflow", out var mapOverflow))
                style.Overflow = mapOverflow;
            if (style.OverflowX == null && style.Map.TryGetValue("overflow-x", out var mapOverflowX))
                style.OverflowX = mapOverflowX;
            if (style.OverflowY == null && style.Map.TryGetValue("overflow-y", out var mapOverflowY))
                style.OverflowY = mapOverflowY;

            SyncAnchorCssProperties(style);
            SyncInset(style.Map, "left", style.Left, style.LeftPercent, value => style.Left = value, value => style.LeftPercent = value, expr => style.LeftAnchorExpression = expr);
            SyncInset(style.Map, "top", style.Top, style.TopPercent, value => style.Top = value, value => style.TopPercent = value, expr => style.TopAnchorExpression = expr);
            SyncInset(style.Map, "right", style.Right, style.RightPercent, value => style.Right = value, value => style.RightPercent = value, expr => style.RightAnchorExpression = expr);
            SyncInset(style.Map, "bottom", style.Bottom, style.BottomPercent, value => style.Bottom = value, value => style.BottomPercent = value, expr => style.BottomAnchorExpression = expr);
            SyncLogicalInsets(style);

            SyncAnchorSize(style.Map, "width", style.Width, style.WidthPercent, style.WidthExpression, value => style.Width = value, value => style.WidthPercent = value, value => style.WidthExpression = value, expr => style.WidthAnchorExpression = expr);
            SyncAnchorSize(style.Map, "height", style.Height, style.HeightPercent, style.HeightExpression, value => style.Height = value, value => style.HeightPercent = value, value => style.HeightExpression = value, expr => style.HeightAnchorExpression = expr);
            SyncSize(style.Map, "min-width", style.MinWidth, style.MinWidthPercent, style.MinWidthExpression, value => style.MinWidth = value, value => style.MinWidthPercent = value, value => style.MinWidthExpression = value);
            SyncSize(style.Map, "min-height", style.MinHeight, style.MinHeightPercent, style.MinHeightExpression, value => style.MinHeight = value, value => style.MinHeightPercent = value, value => style.MinHeightExpression = value);
            SyncSize(style.Map, "max-width", style.MaxWidth, style.MaxWidthPercent, style.MaxWidthExpression, value => style.MaxWidth = value, value => style.MaxWidthPercent = value, value => style.MaxWidthExpression = value, skipKeyword: "none");
            SyncSize(style.Map, "max-height", style.MaxHeight, style.MaxHeightPercent, style.MaxHeightExpression, value => style.MaxHeight = value, value => style.MaxHeightPercent = value, value => style.MaxHeightExpression = value, skipKeyword: "none");
        }

        private static void SyncAnchorCssProperties(CssComputed style)
        {
            if (string.IsNullOrWhiteSpace(style.AnchorName) && style.Map.TryGetValue("anchor-name", out var an))
                style.AnchorName = an;
            if (string.IsNullOrWhiteSpace(style.PositionAnchor) && style.Map.TryGetValue("position-anchor", out var pa))
                style.PositionAnchor = pa;
            if (string.IsNullOrWhiteSpace(style.AnchorScope) && style.Map.TryGetValue("anchor-scope", out var sc))
                style.AnchorScope = sc;
            if (string.IsNullOrWhiteSpace(style.PositionArea) && style.Map.TryGetValue("position-area", out var area))
                style.PositionArea = area;
            if (string.IsNullOrWhiteSpace(style.PositionTry) && style.Map.TryGetValue("position-try", out var pt))
                style.PositionTry = pt;
        }

        public static string GetEffectivePosition(CssComputed style)
        {
            NormalizeForLayout(style);

            if (style == null)
            {
                return null;
            }

            var position = style.Position;
            if (string.IsNullOrWhiteSpace(position) &&
                style.Map != null &&
                style.Map.TryGetValue("position", out var mappedPosition))
            {
                position = mappedPosition;
            }

            return string.IsNullOrWhiteSpace(position)
                ? null
                : position.Trim().ToLowerInvariant();
        }

        private static void SyncInset(
            IReadOnlyDictionary<string, string> map,
            string propertyName,
            double? absoluteValue,
            double? percentValue,
            Action<double> setAbsoluteValue,
            Action<double> setPercentValue,
            Action<string> setAnchorExpression = null)
        {
            if (absoluteValue.HasValue || percentValue.HasValue || map == null ||
                !map.TryGetValue(propertyName, out var rawValue) || string.IsNullOrWhiteSpace(rawValue))
            {
                return;
            }

            rawValue = rawValue.Trim();
            if (string.Equals(rawValue, "auto", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (IsAnchorFunction(rawValue) && setAnchorExpression != null)
            {
                setAnchorExpression(rawValue);
                return;
            }

            if (TryParsePercent(rawValue, out var percent))
            {
                setPercentValue(percent);
                return;
            }

            if (TryParseAbsoluteLength(rawValue, out var absolute))
            {
                setAbsoluteValue(absolute);
            }
        }

        private static void SyncAnchorSize(
            IReadOnlyDictionary<string, string> map,
            string propertyName,
            double? absoluteValue,
            double? percentValue,
            string expressionValue,
            Action<double> setAbsoluteValue,
            Action<double> setPercentValue,
            Action<string> setExpressionValue,
            Action<string> setAnchorExpression = null)
        {
            if (map == null || !map.TryGetValue(propertyName, out var rawValue) || string.IsNullOrWhiteSpace(rawValue))
            {
                return;
            }

            rawValue = rawValue.Trim();
            if (string.Equals(rawValue, "auto", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (IsAnchorSizeFunction(rawValue) && setAnchorExpression != null)
            {
                setAnchorExpression(rawValue);
                return;
            }

            if (!percentValue.HasValue && TryParsePercent(rawValue, out var percent))
            {
                setPercentValue(percent);
                return;
            }

            if (!absoluteValue.HasValue && TryParseAbsoluteLength(rawValue, out var absolute))
            {
                setAbsoluteValue(absolute);
                return;
            }

            if (string.IsNullOrWhiteSpace(expressionValue))
            {
                setExpressionValue(rawValue);
            }
        }

        private static void SyncSize(
            IReadOnlyDictionary<string, string> map,
            string propertyName,
            double? absoluteValue,
            double? percentValue,
            string expressionValue,
            Action<double> setAbsoluteValue,
            Action<double> setPercentValue,
            Action<string> setExpressionValue,
            string skipKeyword = "auto")
        {
            if (map == null || !map.TryGetValue(propertyName, out var rawValue) || string.IsNullOrWhiteSpace(rawValue))
            {
                return;
            }

            rawValue = rawValue.Trim();
            if (string.Equals(rawValue, skipKeyword, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (!percentValue.HasValue && TryParsePercent(rawValue, out var percent))
            {
                setPercentValue(percent);
                return;
            }

            if (!absoluteValue.HasValue && TryParseAbsoluteLength(rawValue, out var absolute))
            {
                setAbsoluteValue(absolute);
                return;
            }

            if (string.IsNullOrWhiteSpace(expressionValue))
            {
                setExpressionValue(rawValue);
            }
        }

        private static void SyncLogicalInsets(CssComputed style)
        {
            if (style?.Map == null)
            {
                return;
            }

            var writingMode = style.WritingMode?.Trim().ToLowerInvariant();
            if (!string.IsNullOrEmpty(writingMode) && writingMode != "horizontal-tb")
            {
                return;
            }

            bool isRtl = string.Equals(style.Direction, "rtl", StringComparison.OrdinalIgnoreCase);

            SyncInset(
                style.Map,
                isRtl ? "inset-inline-end" : "inset-inline-start",
                style.Left,
                style.LeftPercent,
                value => style.Left = value,
                value => style.LeftPercent = value,
                expr => style.LeftAnchorExpression = expr);

            SyncInset(
                style.Map,
                isRtl ? "inset-inline-start" : "inset-inline-end",
                style.Right,
                style.RightPercent,
                value => style.Right = value,
                value => style.RightPercent = value,
                expr => style.RightAnchorExpression = expr);

            SyncInset(style.Map, "inset-block-start", style.Top, style.TopPercent, value => style.Top = value, value => style.TopPercent = value, expr => style.TopAnchorExpression = expr);
            SyncInset(style.Map, "inset-block-end", style.Bottom, style.BottomPercent, value => style.Bottom = value, value => style.BottomPercent = value, expr => style.BottomAnchorExpression = expr);
        }

        private static bool TryParsePercent(string rawValue, out double percent)
        {
            percent = 0;
            if (!rawValue.EndsWith("%", StringComparison.Ordinal))
            {
                return false;
            }

            return double.TryParse(
                rawValue.Substring(0, rawValue.Length - 1).Trim(),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out percent);
        }

        private static bool TryParseAbsoluteLength(string rawValue, out double absolute)
        {
            absolute = 0;

            if (rawValue.EndsWith("px", StringComparison.OrdinalIgnoreCase))
            {
                rawValue = rawValue.Substring(0, rawValue.Length - 2).Trim();
            }

            return double.TryParse(rawValue, NumberStyles.Float, CultureInfo.InvariantCulture, out absolute);
        }

        private static readonly Regex AnchorFuncRegex = new Regex(
            @"^anchor\(\s*(--[\w-]+)?\s*([\w-]+)\s*\)$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex AnchorSizeFuncRegex = new Regex(
            @"^anchor-size\(\s*(--[\w-]+)?\s*([\w-]+)\s*\)$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public static bool IsAnchorFunction(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;
            return value.Trim().StartsWith("anchor(", StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsAnchorSizeFunction(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;
            return value.Trim().StartsWith("anchor-size(", StringComparison.OrdinalIgnoreCase);
        }

        public static bool HasAnyAnchorReferences(CssComputed style)
        {
            if (style == null) return false;
            return !string.IsNullOrWhiteSpace(style.PositionAnchor)
                || IsAnchorFunction(style.LeftAnchorExpression)
                || IsAnchorFunction(style.TopAnchorExpression)
                || IsAnchorFunction(style.RightAnchorExpression)
                || IsAnchorFunction(style.BottomAnchorExpression)
                || IsAnchorSizeFunction(style.WidthAnchorExpression)
                || IsAnchorSizeFunction(style.HeightAnchorExpression);
        }

        /// <summary>
        /// Parse an anchor() expression returning (anchorName, side) tuple.
        /// anchorName may be null if implicit (using position-anchor).
        /// </summary>
        public static (string anchorName, string side) ParseAnchorExpression(string expression, string defaultAnchor)
        {
            if (string.IsNullOrWhiteSpace(expression)) return (null, null);
            var m = AnchorFuncRegex.Match(expression.Trim());
            if (!m.Success) return (null, null);
            string name = m.Groups[1].Success ? m.Groups[1].Value.Trim() : null;
            string side = m.Groups[2].Value.Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(name)) name = defaultAnchor;
            return (name, side);
        }

        /// <summary>
        /// Parse an anchor-size() expression returning (anchorName, property) tuple.
        /// </summary>
        public static (string anchorName, string property) ParseAnchorSizeExpression(string expression, string defaultAnchor)
        {
            if (string.IsNullOrWhiteSpace(expression)) return (null, null);
            var m = AnchorSizeFuncRegex.Match(expression.Trim());
            if (!m.Success) return (null, null);
            string name = m.Groups[1].Success ? m.Groups[1].Value.Trim() : null;
            string prop = m.Groups[2].Value.Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(name)) name = defaultAnchor;
            return (name, prop);
        }

        public static float ResolveAnchorSide(SKRect anchorBox, string side)
        {
            if (string.IsNullOrEmpty(side)) return 0f;
            switch (side)
            {
                case "left": return anchorBox.Left;
                case "right": return anchorBox.Right;
                case "top": return anchorBox.Top;
                case "bottom": return anchorBox.Bottom;
                case "start": return anchorBox.Left;
                case "end": return anchorBox.Right;
                case "self-start": return anchorBox.Top;
                case "self-end": return anchorBox.Bottom;
                case "center":
                    return side switch
                    {
                        "center" => anchorBox.Left + anchorBox.Width / 2,
                        _ => anchorBox.Left
                    };
                default:
                    if (side.EndsWith("%") && float.TryParse(side.TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out var pct))
                        return anchorBox.Left + anchorBox.Width * (pct / 100f);
                    return 0f;
            }
        }

        public static float ResolveAnchorSideVertical(SKRect anchorBox, string side)
        {
            if (string.IsNullOrEmpty(side)) return 0f;
            switch (side)
            {
                case "top": return anchorBox.Top;
                case "bottom": return anchorBox.Bottom;
                case "left": return anchorBox.Left;
                case "right": return anchorBox.Right;
                case "start": return anchorBox.Top;
                case "end": return anchorBox.Bottom;
                case "self-start": return anchorBox.Top;
                case "self-end": return anchorBox.Bottom;
                case "center":
                    return side switch
                    {
                        "center" => anchorBox.Top + anchorBox.Height / 2,
                        _ => anchorBox.Top
                    };
                default:
                    if (side.EndsWith("%") && float.TryParse(side.TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out var pct))
                        return anchorBox.Top + anchorBox.Height * (pct / 100f);
                    return 0f;
            }
        }
    }
}
