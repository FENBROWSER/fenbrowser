using System;
using System.Globalization;
using FenBrowser.Core.Css;
using FenBrowser.Core.Dom.V2;
using SkiaSharp;

namespace FenBrowser.FenEngine.Layout
{
    /// <summary>
    /// Centralized sizing rules for replaced elements so all layout contexts
    /// (block/inline/flex/positioned) resolve the same used dimensions.
    /// </summary>
    public static class ReplacedElementSizing
    {
        public static bool IsReplacedElementTag(string tagUpper)
        {
            if (string.IsNullOrEmpty(tagUpper)) return false;
            return tagUpper is "IMG" or "SVG" or "CANVAS" or "VIDEO" or "IFRAME" or "EMBED" or "OBJECT";
        }

        public static SKSize GetFallbackSize(string tagUpper)
        {
            return tagUpper switch
            {
                "IMG" => new SKSize(300f, 150f),
                "SVG" => new SKSize(300f, 150f),
                "CANVAS" => new SKSize(300f, 150f),
                "VIDEO" => new SKSize(300f, 150f),
                "IFRAME" => new SKSize(300f, 150f),
                "EMBED" => new SKSize(300f, 150f),
                "OBJECT" => new SKSize(300f, 150f),
                _ => SKSize.Empty
            };
        }

        public static bool TryGetLengthAttribute(Element element, string attributeName, out float value)
        {
            value = 0f;
            if (element == null) return false;

            string raw = element.GetAttribute(attributeName);
            if (string.IsNullOrWhiteSpace(raw)) return false;

            raw = raw.Trim();
            int numericChars = 0;
            while (numericChars < raw.Length)
            {
                char ch = raw[numericChars];
                if ((ch >= '0' && ch <= '9') || ch == '.' || ch == '-') { numericChars++; continue; }
                break;
            }

            if (numericChars == 0) return false;

            if (!float.TryParse(raw.Substring(0, numericChars), NumberStyles.Float, CultureInfo.InvariantCulture, out value))
            {
                value = 0f;
                return false;
            }

            value = Math.Max(0f, value);
            return true;
        }

        public static bool TryParseSvgViewBoxSize(Element element, out float width, out float height)
        {
            width = 0f;
            height = 0f;
            if (element == null) return false;

            string viewBox = element.GetAttribute("viewBox") ?? element.GetAttribute("viewbox");
            if (string.IsNullOrWhiteSpace(viewBox)) return false;

            var parts = viewBox.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 4) return false;

            if (!float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float vbW)) return false;
            if (!float.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out float vbH)) return false;

            width = Math.Abs(vbW);
            height = Math.Abs(vbH);
            return width > 0f && height > 0f;
        }

        public static SKSize ResolveReplacedSize(
            string tagUpper,
            CssComputed style,
            SKSize availableSize,
            float intrinsicWidth,
            float intrinsicHeight,
            float attributeWidth,
            float attributeHeight,
            bool constrainAutoToAvailableWidth)
        {
            float width = 0f;
            float height = 0f;

            bool hasSpecifiedWidth = TryResolveSpecifiedWidth(style, availableSize, out float specifiedWidth);
            bool hasSpecifiedHeight = TryResolveSpecifiedHeight(style, availableSize, out float specifiedHeight);
            bool hasAttributeWidth = attributeWidth > 0f;
            bool hasAttributeHeight = attributeHeight > 0f;

            if (hasSpecifiedWidth) width = Math.Max(0f, specifiedWidth);
            if (hasSpecifiedHeight) height = Math.Max(0f, specifiedHeight);

            if (!hasSpecifiedWidth && hasAttributeWidth) width = attributeWidth;
            if (!hasSpecifiedHeight && hasAttributeHeight) height = attributeHeight;

            var fallback = GetFallbackSize(tagUpper);
            float ratio = ResolveAspectRatio(style, intrinsicWidth, intrinsicHeight, attributeWidth, attributeHeight, fallback);

            if ((hasSpecifiedWidth || width > 0f) && !hasSpecifiedHeight && height <= 0f)
            {
                height = ratio > 0f ? width / ratio : fallback.Height;
            }
            else if ((hasSpecifiedHeight || height > 0f) && !hasSpecifiedWidth && width <= 0f)
            {
                width = ratio > 0f ? height * ratio : fallback.Width;
            }
            else if (width <= 0f && height <= 0f)
            {
                if (intrinsicWidth > 0f && intrinsicHeight > 0f)
                {
                    width = intrinsicWidth;
                    height = intrinsicHeight;
                }
                else if (hasAttributeWidth && hasAttributeHeight)
                {
                    width = attributeWidth;
                    height = attributeHeight;
                }
                else
                {
                    width = fallback.Width;
                    height = fallback.Height;
                }
            }
            else
            {
                if (width <= 0f) width = ratio > 0f && height > 0f ? height * ratio : fallback.Width;
                if (height <= 0f) height = ratio > 0f && width > 0f ? width / ratio : fallback.Height;
            }

            if (constrainAutoToAvailableWidth &&
                !hasSpecifiedWidth &&
                !hasAttributeWidth &&
                float.IsFinite(availableSize.Width) &&
                availableSize.Width > 0f &&
                width > availableSize.Width)
            {
                float scale = availableSize.Width / width;
                width = availableSize.Width;
                height = Math.Max(0f, height * scale);
            }

            if (!float.IsFinite(width) || width < 0f) width = 0f;
            if (!float.IsFinite(height) || height < 0f) height = 0f;

            return new SKSize(width, height);
        }

        private static bool TryResolveSpecifiedWidth(CssComputed style, SKSize availableSize, out float width)
        {
            width = 0f;
            if (style == null) return false;

            if (style.Width.HasValue)
            {
                width = (float)style.Width.Value;
                return true;
            }

            if (style.WidthPercent.HasValue &&
                float.IsFinite(availableSize.Width) &&
                availableSize.Width > 0f)
            {
                width = (float)(style.WidthPercent.Value / 100.0 * availableSize.Width);
                return true;
            }

            return false;
        }

        private static bool TryResolveSpecifiedHeight(CssComputed style, SKSize availableSize, out float height)
        {
            height = 0f;
            if (style == null) return false;

            if (style.Height.HasValue)
            {
                height = (float)style.Height.Value;
                return true;
            }

            if (style.HeightPercent.HasValue &&
                float.IsFinite(availableSize.Height) &&
                availableSize.Height > 0f)
            {
                height = (float)(style.HeightPercent.Value / 100.0 * availableSize.Height);
                return true;
            }

            return false;
        }

        private static float ResolveAspectRatio(
            CssComputed style,
            float intrinsicWidth,
            float intrinsicHeight,
            float attributeWidth,
            float attributeHeight,
            SKSize fallback)
        {
            if (style?.AspectRatio.HasValue == true && style.AspectRatio.Value > 0)
            {
                return (float)style.AspectRatio.Value;
            }

            if (intrinsicWidth > 0f && intrinsicHeight > 0f)
            {
                return intrinsicWidth / intrinsicHeight;
            }

            if (attributeWidth > 0f && attributeHeight > 0f)
            {
                return attributeWidth / attributeHeight;
            }

            if (fallback.Width > 0f && fallback.Height > 0f)
            {
                return fallback.Width / fallback.Height;
            }

            return 0f;
        }
    }
}
