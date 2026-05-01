using System;
using System.Globalization;
using FenBrowser.FenEngine.Layout.Tree;
using FenBrowser.FenEngine.Layout;
using SkiaSharp;
using FenBrowser.Core.Css;
using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Logging;

namespace FenBrowser.FenEngine.Layout.Contexts // Namespace matching usage
{
    public static class LayoutBoxOps
    {
        public static void ComputeBoxModelFromContent(LayoutBox box, float contentW, float contentH)
        {
            var p = box.Geometry.Padding;
            var b = box.Geometry.Border;
            var m = box.Geometry.Margin;
            
            float left = box.Geometry.ContentBox.Left;
            float top = box.Geometry.ContentBox.Top;
            float right = left + contentW;
            float bottom = top + contentH;

            box.Geometry.ContentBox = new SKRect(left, top, right, bottom);
            
            box.Geometry.PaddingBox = new SKRect(
                left - (float)p.Left,
                top - (float)p.Top,
                right + (float)p.Right,
                bottom + (float)p.Bottom
            );
            
            box.Geometry.BorderBox = new SKRect(
                left - (float)(p.Left + b.Left),
                top - (float)(p.Top + b.Top),
                right + (float)(p.Right + b.Right),
                bottom + (float)(p.Bottom + b.Bottom)
            );
            
            box.Geometry.MarginBox = new SKRect(
                left - (float)(p.Left + b.Left + m.Left),
                top - (float)(p.Top + b.Top + m.Top),
                right + (float)(p.Right + b.Right + m.Right),
                bottom + (float)(p.Bottom + b.Bottom + m.Bottom)
            );
        }

        public static void SetPosition(LayoutBox box, float x, float y)
        {
             // Align the outermost resolved box top-left to x,y.
             // Some layout paths populate ContentBox/BorderBox before MarginBox is
             // synchronized; anchoring exclusively on MarginBox then shifts descendants
             // too far to the right/bottom when the outer boxes are still empty.
             var anchor = GetPositionAnchor(box.Geometry);
             float dx = x - anchor.X;
             float dy = y - anchor.Y;
             
             ShiftBoxModel(box.Geometry, dx, dy);
        }

        public static void ResetSubtreeToOrigin(LayoutBox box)
        {
            if (box?.Geometry == null)
            {
                return;
            }

            float dx = -box.Geometry.ContentBox.Left;
            float dy = -box.Geometry.ContentBox.Top;
            ShiftBoxModel(box.Geometry, dx, dy);

            foreach (var child in box.Children)
            {
                ResetSubtreeToOrigin(child);
            }
        }

        public static void ShiftSubtree(LayoutBox box, float dx, float dy)
        {
            if (box?.Geometry == null)
            {
                return;
            }

            ShiftBoxModel(box.Geometry, dx, dy);

            foreach (var child in box.Children)
            {
                var childPosition = LayoutStyleResolver.GetEffectivePosition(child?.ComputedStyle);
                if (string.Equals(childPosition, "fixed", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                ShiftSubtree(child, dx, dy);
            }
        }

        public static void ShiftDescendants(LayoutBox box, float dx, float dy)
        {
            if (box == null)
            {
                return;
            }

            foreach (var child in box.Children)
            {
                var childPosition = LayoutStyleResolver.GetEffectivePosition(child?.ComputedStyle);
                if (string.Equals(childPosition, "fixed", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                ShiftSubtree(child, dx, dy);
            }
        }

        public static void PositionSubtree(LayoutBox box, float x, float y, LayoutState state)
        {
            if (box?.Geometry == null)
            {
                return;
            }

            var relativeOffset = ResolveRelativeOffset(box.ComputedStyle, state);
            float targetX = x + relativeOffset.X;
            float targetY = y + relativeOffset.Y;
            var anchor = GetPositionAnchor(box.Geometry);
            float dx = targetX - anchor.X;
            float dy = targetY - anchor.Y;

            ShiftSubtree(box, dx, dy);
        }

        public static void ShiftBoxModel(BoxModel model, float dx, float dy)
        {
            model.ContentBox = OffsetRect(model.ContentBox, dx, dy);
            model.PaddingBox = OffsetRect(model.PaddingBox, dx, dy);
            model.BorderBox = OffsetRect(model.BorderBox, dx, dy);
            model.MarginBox = OffsetRect(model.MarginBox, dx, dy);
        }
        
        private static SKRect OffsetRect(SKRect r, float dx, float dy)
        {
            return new SKRect(r.Left + dx, r.Top + dy, r.Right + dx, r.Bottom + dy);
        }

        private static SKPoint GetPositionAnchor(BoxModel model)
        {
            if (model == null)
            {
                return SKPoint.Empty;
            }

            if (HasResolvedRect(model.MarginBox))
            {
                return new SKPoint(model.MarginBox.Left, model.MarginBox.Top);
            }

            if (HasResolvedRect(model.BorderBox))
            {
                return new SKPoint(model.BorderBox.Left, model.BorderBox.Top);
            }

            if (HasResolvedRect(model.PaddingBox))
            {
                return new SKPoint(model.PaddingBox.Left, model.PaddingBox.Top);
            }

            return new SKPoint(model.ContentBox.Left, model.ContentBox.Top);
        }

        private static bool HasResolvedRect(SKRect rect)
        {
            return float.IsFinite(rect.Left) &&
                   float.IsFinite(rect.Top) &&
                   float.IsFinite(rect.Right) &&
                   float.IsFinite(rect.Bottom) &&
                   (Math.Abs(rect.Left) > 0.01f ||
                    Math.Abs(rect.Top) > 0.01f ||
                    Math.Abs(rect.Right) > 0.01f ||
                    Math.Abs(rect.Bottom) > 0.01f);
        }

        private static SKPoint ResolveRelativeOffset(CssComputed style, LayoutState state)
        {
            if (style == null || !string.Equals(LayoutStyleResolver.GetEffectivePosition(style), "relative", StringComparison.OrdinalIgnoreCase))
            {
                return SKPoint.Empty;
            }

            float cbWidth = state.ContainingBlockWidth;
            if (!float.IsFinite(cbWidth) || cbWidth <= 0f)
            {
                cbWidth = state.AvailableSize.Width;
            }
            if (!float.IsFinite(cbWidth) || cbWidth <= 0f)
            {
                cbWidth = state.ViewportWidth;
            }

            float cbHeight = state.ContainingBlockHeight;
            if (!float.IsFinite(cbHeight) || cbHeight <= 0f)
            {
                cbHeight = state.AvailableSize.Height;
            }
            if (!float.IsFinite(cbHeight) || cbHeight <= 0f)
            {
                cbHeight = state.ViewportHeight;
            }

            float dx = 0f;
            if (TryResolveInsetOffset(style, "left", cbWidth, out float leftOffset))
            {
                dx = leftOffset;
            }
            else if (TryResolveInsetOffset(style, "right", cbWidth, out float rightOffset))
            {
                dx = -rightOffset;
            }

            float dy = 0f;
            if (TryResolveInsetOffset(style, "top", cbHeight, out float topOffset))
            {
                dy = topOffset;
            }
            else if (TryResolveInsetOffset(style, "bottom", cbHeight, out float bottomOffset))
            {
                dy = -bottomOffset;
            }

            return new SKPoint(dx, dy);
        }

        private static bool TryResolveInsetOffset(CssComputed style, string side, float containingSize, out float offset)
        {
            offset = 0f;
            bool mapHasAuthoredInset = MapHasAuthoredInset(style?.Map, side);
            if (!HasExplicitInset(style, side))
            {
                return false;
            }

            if (TryResolveInsetFromPrimaryKey(style, side, containingSize, out offset))
            {
                return true;
            }

            if (TryResolveInsetFromLogicalKey(style, side, containingSize, out offset))
            {
                return true;
            }

            if (TryResolveInsetFromShorthand(style, "inset", side, containingSize, out offset))
            {
                return true;
            }

            string axisShorthand = side == "top" || side == "bottom" ? "inset-block" : "inset-inline";
            if (TryResolveInsetFromShorthand(style, axisShorthand, side, containingSize, out offset))
            {
                return true;
            }

            // If this side was explicitly authored but token parsing did not resolve
            // a concrete length (e.g. em/calc forms), use the computed typed projection.
            if (mapHasAuthoredInset && TryResolveInsetFromTypedProjection(style, side, containingSize, out offset))
            {
                return true;
            }

            // Fallback only for map-less synthetic styles (e.g. tests directly setting
            // CssComputed.Top/Left without authored map keys).
            if (style?.Map == null || style.Map.Count == 0)
            {
                return TryResolveInsetFromTypedProjection(style, side, containingSize, out offset);
            }

            return false;
        }

        private static bool TryResolveInsetFromTypedProjection(CssComputed style, string side, float containingSize, out float offset)
        {
            offset = 0f;
            if (style == null)
            {
                return false;
            }

            switch (side)
            {
                case "top":
                    if (style.Top.HasValue)
                    {
                        offset = (float)style.Top.Value;
                        return true;
                    }
                    break;
                case "right":
                    if (style.Right.HasValue)
                    {
                        offset = (float)style.Right.Value;
                        return true;
                    }
                    break;
                case "bottom":
                    if (style.Bottom.HasValue)
                    {
                        offset = (float)style.Bottom.Value;
                        return true;
                    }
                    break;
                case "left":
                    if (style.Left.HasValue)
                    {
                        offset = (float)style.Left.Value;
                        return true;
                    }
                    break;
            }

            return false;
        }

        private static bool TryResolveInsetFromPrimaryKey(CssComputed style, string side, float containingSize, out float offset)
        {
            offset = 0f;
            if (style?.Map == null || !style.Map.TryGetValue(side, out string raw))
            {
                return false;
            }

            return TryParseInsetValue(raw, containingSize, out offset);
        }

        private static bool TryResolveInsetFromLogicalKey(CssComputed style, string side, float containingSize, out float offset)
        {
            offset = 0f;
            if (style?.Map == null)
            {
                return false;
            }

            string logicalKey = side switch
            {
                "top" => "inset-block-start",
                "bottom" => "inset-block-end",
                "left" => "inset-inline-start",
                "right" => "inset-inline-end",
                _ => string.Empty
            };

            if (string.IsNullOrEmpty(logicalKey) || !style.Map.TryGetValue(logicalKey, out string raw))
            {
                return false;
            }

            return TryParseInsetValue(raw, containingSize, out offset);
        }

        private static bool TryResolveInsetFromShorthand(CssComputed style, string shorthandKey, string side, float containingSize, out float offset)
        {
            offset = 0f;
            if (style?.Map == null || !style.Map.TryGetValue(shorthandKey, out string raw) || string.IsNullOrWhiteSpace(raw))
            {
                return false;
            }

            string[] tokens = raw.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length == 0)
            {
                return false;
            }

            string selectedToken;
            if (string.Equals(shorthandKey, "inset-block", StringComparison.OrdinalIgnoreCase))
            {
                selectedToken = side switch
                {
                    "top" => tokens[0],
                    "bottom" => tokens.Length > 1 ? tokens[1] : tokens[0],
                    _ => string.Empty
                };
            }
            else if (string.Equals(shorthandKey, "inset-inline", StringComparison.OrdinalIgnoreCase))
            {
                selectedToken = side switch
                {
                    "left" => tokens[0],
                    "right" => tokens.Length > 1 ? tokens[1] : tokens[0],
                    _ => string.Empty
                };
            }
            else
            {
                selectedToken = side switch
                {
                    "top" => tokens[0],
                    "right" => tokens.Length == 1 ? tokens[0] : tokens.Length == 2 ? tokens[1] : tokens[1],
                    "bottom" => tokens.Length == 1 ? tokens[0] : tokens.Length == 2 ? tokens[0] : tokens.Length == 3 ? tokens[2] : tokens[2],
                    "left" => tokens.Length == 1 ? tokens[0] : tokens.Length == 2 ? tokens[1] : tokens.Length == 3 ? tokens[1] : tokens[3],
                    _ => string.Empty
                };
            }

            if (string.IsNullOrWhiteSpace(selectedToken))
            {
                return false;
            }

            return TryParseInsetValue(selectedToken, containingSize, out offset);
        }

        private static bool TryParseInsetValue(string raw, float containingSize, out float value)
        {
            value = 0f;
            if (string.IsNullOrWhiteSpace(raw))
            {
                return false;
            }

            string token = raw.Trim();
            if (token.Length == 0 ||
                token.Equals("auto", StringComparison.OrdinalIgnoreCase) ||
                token.Equals("initial", StringComparison.OrdinalIgnoreCase) ||
                token.Equals("inherit", StringComparison.OrdinalIgnoreCase) ||
                token.Equals("unset", StringComparison.OrdinalIgnoreCase) ||
                token.Equals("revert", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (token.EndsWith("px", StringComparison.OrdinalIgnoreCase))
            {
                string px = token.Substring(0, token.Length - 2).Trim();
                if (float.TryParse(px, NumberStyles.Float, CultureInfo.InvariantCulture, out float parsedPx))
                {
                    value = parsedPx;
                    return true;
                }
            }

            if (token.EndsWith("%", StringComparison.OrdinalIgnoreCase))
            {
                string percent = token.Substring(0, token.Length - 1).Trim();
                if (containingSize > 0f &&
                    float.TryParse(percent, NumberStyles.Float, CultureInfo.InvariantCulture, out float parsedPercent))
                {
                    value = containingSize * (parsedPercent / 100f);
                    return true;
                }
            }

            if (float.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out float rawNumber))
            {
                value = rawNumber;
                return true;
            }

            return false;
        }

        private static bool HasExplicitInset(CssComputed style, string side)
        {
            if (style == null || string.IsNullOrWhiteSpace(side))
            {
                return false;
            }

            bool mapHasAuthoredInset = MapHasAuthoredInset(style.Map, side);
            if (mapHasAuthoredInset)
            {
                return true;
            }

            // If author map exists but doesn't contain this inset, typed fields can be stale
            // from previous style states and must be ignored.
            if (style.Map != null && style.Map.Count > 0)
            {
                return false;
            }

            return side switch
            {
                "top" => style.Top.HasValue || style.TopPercent.HasValue,
                "right" => style.Right.HasValue || style.RightPercent.HasValue,
                "bottom" => style.Bottom.HasValue || style.BottomPercent.HasValue,
                "left" => style.Left.HasValue || style.LeftPercent.HasValue,
                _ => false
            };
        }

        private static bool MapHasAuthoredInset(System.Collections.Generic.IReadOnlyDictionary<string, string> map, string side)
        {
            if (map == null || map.Count == 0 || string.IsNullOrWhiteSpace(side))
            {
                return false;
            }

            if (map.ContainsKey(side) || map.ContainsKey("inset") || map.ContainsKey($"inset-{side}"))
            {
                return true;
            }

            return side switch
            {
                "top" => map.ContainsKey("inset-block-start"),
                "bottom" => map.ContainsKey("inset-block-end"),
                "left" => map.ContainsKey("inset-inline-start"),
                "right" => map.ContainsKey("inset-inline-end"),
                _ => false
            };
        }
    }
}
