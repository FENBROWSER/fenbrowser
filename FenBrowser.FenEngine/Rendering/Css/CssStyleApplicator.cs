using System;
using System.Collections.Generic;
using FenBrowser.Core;
using FenBrowser.Core.Css;
using SkiaSharp;

namespace FenBrowser.FenEngine.Rendering.Css
{
    /// <summary>
    /// Helper to apply CSS property values to typed CssComputed objects.
    /// Used by CSS Animations to update styles efficiently.
    /// </summary>
    public static class CssStyleApplicator
    {
        public static void ApplyProperty(CssComputed style, string property, string value)
        {
            if (style == null || string.IsNullOrEmpty(property)) return;

            // Always update the raw map so subsequent lookups (and transitions) see the new value
            style.Map[property] = value;

            // Apply to typed properties used by Layout/Paint
            switch (property.ToLowerInvariant())
            {
                case "opacity":
                    if (CssLoader.TryDouble(value, out double op)) style.Opacity = op;
                    break;
                    
                case "width":
                    if (CssLoader.TryPx(value, out double w)) style.Width = w;
                    else if (CssLoader.TryPercent(value, out double wp)) style.WidthPercent = wp;
                    break;
                    
                case "height":
                    if (CssLoader.TryPx(value, out double h)) style.Height = h;
                    else if (CssLoader.TryPercent(value, out double hp)) style.HeightPercent = hp;
                    break;
                    
                case "min-width":
                    if (CssLoader.TryPx(value, out double minw)) style.MinWidth = minw;
                    break;
                    
                case "max-width":
                    if (CssLoader.TryPx(value, out double maxw)) style.MaxWidth = maxw;
                    break;
                    
                case "min-height":
                    if (CssLoader.TryPx(value, out double minh)) style.MinHeight = minh;
                    else if (CssLoader.TryPercent(value, out double minhp)) style.MinHeightPercent = minhp;
                    break;
                    
                case "max-height":
                    if (CssLoader.TryPx(value, out double maxh)) style.MaxHeight = maxh;
                    else if (CssLoader.TryPercent(value, out double maxhp)) style.MaxHeightPercent = maxhp;
                    break;

                case "background-attachment":
                    style.BackgroundAttachment = value;
                    break;

                case "margin-top":
                case "margin-right":
                case "margin-bottom":
                case "margin-left":
                case "margin":
                    var m = style.Margin;
                    if (property == "margin") {
                        if (CssLoader.TryThickness(value, out var tm)) style.Margin = tm;
                    } else if (CssLoader.TryPx(value, out double mv)) {
                         if (property.EndsWith("-top")) style.Margin = new Thickness(m.Left, mv, m.Right, m.Bottom);
                         if (property.EndsWith("-right")) style.Margin = new Thickness(m.Left, m.Top, mv, m.Bottom);
                         if (property.EndsWith("-bottom")) style.Margin = new Thickness(m.Left, m.Top, m.Right, mv);
                         if (property.EndsWith("-left")) style.Margin = new Thickness(mv, m.Top, m.Right, m.Bottom);
                    }
                    break;

                case "padding-top":
                case "padding-right":
                case "padding-bottom":
                case "padding-left":
                case "padding":
                     var p = style.Padding;
                     if (property == "padding") {
                         if (CssLoader.TryThickness(value, out var tp)) style.Padding = tp;
                     } else if (CssLoader.TryPx(value, out double pv)) {
                         if (property.EndsWith("-top")) style.Padding = new Thickness(p.Left, pv, p.Right, p.Bottom);
                         if (property.EndsWith("-right")) style.Padding = new Thickness(p.Left, p.Top, pv, p.Bottom);
                         if (property.EndsWith("-bottom")) style.Padding = new Thickness(p.Left, p.Top, p.Right, pv);
                         if (property.EndsWith("-left")) style.Padding = new Thickness(pv, p.Top, p.Right, p.Bottom);
                     }
                     break;

                case "top": 
                    style.Top = null;
                    style.TopPercent = null;
                    if (string.Equals(value?.Trim(), "auto", StringComparison.OrdinalIgnoreCase)) break;
                    if (CssLoader.TryPx(value, out double topPx)) style.Top = topPx; 
                    else if (CssLoader.TryPercent(value, out double topPercent)) style.TopPercent = topPercent; 
                    break;
                case "left": 
                    style.Left = null;
                    style.LeftPercent = null;
                    if (string.Equals(value?.Trim(), "auto", StringComparison.OrdinalIgnoreCase)) break;
                    if (CssLoader.TryPx(value, out double leftPx)) style.Left = leftPx; 
                    else if (CssLoader.TryPercent(value, out double leftPercent)) style.LeftPercent = leftPercent; 
                    break;
                case "right": 
                    style.Right = null;
                    style.RightPercent = null;
                    if (string.Equals(value?.Trim(), "auto", StringComparison.OrdinalIgnoreCase)) break;
                    if (CssLoader.TryPx(value, out double rightPx)) style.Right = rightPx; 
                    else if (CssLoader.TryPercent(value, out double rightPercent)) style.RightPercent = rightPercent; 
                    break;
                case "bottom": 
                    style.Bottom = null;
                    style.BottomPercent = null;
                    if (string.Equals(value?.Trim(), "auto", StringComparison.OrdinalIgnoreCase)) break;
                    if (CssLoader.TryPx(value, out double bottomPx)) style.Bottom = bottomPx; 
                    else if (CssLoader.TryPercent(value, out double bottomPercent)) style.BottomPercent = bottomPercent; 
                    break;
                
                case "background-color":
                    style.BackgroundColor = CssLoader.TryColor(value);
                    break;
                    
                case "color":
                    style.ForegroundColor = CssLoader.TryColor(value);
                    break;

                case "border-color":
                    style.BorderBrushColor = CssLoader.TryColor(value) ?? SKColors.Black;
                    break;
                    
                case "border-width":
                    if (CssLoader.TryThickness(value, out var bt)) style.BorderThickness = bt;
                    break;

                case "transform":
                case "translate":
                case "rotate":
                case "scale":
                    style.Transform = ComposeEffectiveTransform(style.Map);
                    break;

                case "object-fit":
                    style.ObjectFit = value?.Trim()?.ToLowerInvariant();
                    break;

                case "object-position":
                    style.ObjectPosition = value?.Trim();
                    break;
                    
                case "font-size":
                     if (CssLoader.TryPx(value, out double fs)) style.FontSize = fs;
                     break;
                     
                // ACID2 FIX: Add visibility, display, position, overflow
                case "display":
                    style.Display = value;
                    break;
                    
                case "visibility":
                    style.Visibility = value;
                    break;
                    
                case "position":
                    style.Position = value;
                    break;
                    
                case "overflow":
                    style.Overflow = value;
                    break;
                    
                case "overflow-x":
                    style.OverflowX = value;
                    break;
                    
                case "overflow-y":
                    style.OverflowY = value;
                    break;
            }
        }

        private static string ComposeEffectiveTransform(IDictionary<string, string> map)
        {
            if (map == null)
            {
                return null;
            }

            var translate = NormalizeTransformLonghand(GetMapValue(map, "translate"), "translate");
            var rotate = NormalizeTransformLonghand(GetMapValue(map, "rotate"), "rotate");
            var scale = NormalizeTransformLonghand(GetMapValue(map, "scale"), "scale");
            var transform = GetMapValue(map, "transform")?.Trim();

            var segments = new List<string>(4);
            if (!string.IsNullOrEmpty(translate)) segments.Add(translate);
            if (!string.IsNullOrEmpty(rotate)) segments.Add(rotate);
            if (!string.IsNullOrEmpty(scale)) segments.Add(scale);
            if (!string.IsNullOrWhiteSpace(transform) &&
                !string.Equals(transform, "none", StringComparison.OrdinalIgnoreCase))
            {
                segments.Add(transform);
            }

            if (segments.Count > 0)
            {
                return string.Join(" ", segments);
            }

            return string.Equals(transform, "none", StringComparison.OrdinalIgnoreCase) ? "none" : null;
        }

        private static string NormalizeTransformLonghand(string value, string functionName)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            var trimmed = value.Trim();
            if (string.Equals(trimmed, "none", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            int openParenIndex = trimmed.IndexOf('(');
            if (openParenIndex > 0)
            {
                return trimmed;
            }

            return $"{functionName}({trimmed})";
        }

        private static string GetMapValue(IDictionary<string, string> map, string key)
        {
            if (map == null || string.IsNullOrEmpty(key))
            {
                return null;
            }

            if (map.TryGetValue(key, out var direct))
            {
                return direct;
            }

            foreach (var entry in map)
            {
                if (string.Equals(entry.Key, key, StringComparison.OrdinalIgnoreCase))
                {
                    return entry.Value;
                }
            }

            return null;
        }
    }
}
