using System;
using System.Collections.Generic;
using System.Globalization;
using FenBrowser.Core.Css;

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

            SyncInset(style.Map, "left", style.Left, style.LeftPercent, value => style.Left = value, value => style.LeftPercent = value);
            SyncInset(style.Map, "top", style.Top, style.TopPercent, value => style.Top = value, value => style.TopPercent = value);
            SyncInset(style.Map, "right", style.Right, style.RightPercent, value => style.Right = value, value => style.RightPercent = value);
            SyncInset(style.Map, "bottom", style.Bottom, style.BottomPercent, value => style.Bottom = value, value => style.BottomPercent = value);
            SyncLogicalInsets(style);

            SyncSize(style.Map, "width", style.Width, style.WidthPercent, style.WidthExpression, value => style.Width = value, value => style.WidthPercent = value, value => style.WidthExpression = value);
            SyncSize(style.Map, "height", style.Height, style.HeightPercent, style.HeightExpression, value => style.Height = value, value => style.HeightPercent = value, value => style.HeightExpression = value);
            SyncSize(style.Map, "min-width", style.MinWidth, style.MinWidthPercent, style.MinWidthExpression, value => style.MinWidth = value, value => style.MinWidthPercent = value, value => style.MinWidthExpression = value);
            SyncSize(style.Map, "min-height", style.MinHeight, style.MinHeightPercent, style.MinHeightExpression, value => style.MinHeight = value, value => style.MinHeightPercent = value, value => style.MinHeightExpression = value);
            SyncSize(style.Map, "max-width", style.MaxWidth, style.MaxWidthPercent, style.MaxWidthExpression, value => style.MaxWidth = value, value => style.MaxWidthPercent = value, value => style.MaxWidthExpression = value, skipKeyword: "none");
            SyncSize(style.Map, "max-height", style.MaxHeight, style.MaxHeightPercent, style.MaxHeightExpression, value => style.MaxHeight = value, value => style.MaxHeightPercent = value, value => style.MaxHeightExpression = value, skipKeyword: "none");
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
            Action<double> setPercentValue)
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
                value => style.LeftPercent = value);

            SyncInset(
                style.Map,
                isRtl ? "inset-inline-start" : "inset-inline-end",
                style.Right,
                style.RightPercent,
                value => style.Right = value,
                value => style.RightPercent = value);

            SyncInset(style.Map, "inset-block-start", style.Top, style.TopPercent, value => style.Top = value, value => style.TopPercent = value);
            SyncInset(style.Map, "inset-block-end", style.Bottom, style.BottomPercent, value => style.Bottom = value, value => style.BottomPercent = value);
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
    }
}
