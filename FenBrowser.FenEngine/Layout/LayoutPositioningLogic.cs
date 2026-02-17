using System;
using FenBrowser.Core;
using FenBrowser.Core.Css;
using FenBrowser.Core.Dom.V2;
using FenBrowser.FenEngine.Layout.Tree;
using SkiaSharp;

namespace FenBrowser.FenEngine.Layout
{
    public static class LayoutPositioningLogic
    {
        public static void ResolvePositionedBox(LayoutBox box, LayoutBox containingBlock, BoxModel containerGeometry)
        {
            if (box?.ComputedStyle == null || box.Geometry == null || containerGeometry == null) return;

            var style = box.ComputedStyle;

            // For abs/fixed positioning, CSS uses the containing block's padding box.
            var cbRect = containerGeometry.PaddingBox;
            if (cbRect.Width <= 0 || cbRect.Height <= 0)
            {
                cbRect = containerGeometry.ContentBox;
            }

            float intrinsicWidth = box.Geometry.ContentBox.Width;
            float intrinsicHeight = box.Geometry.ContentBox.Height;
            EnsureIntrinsicSize(box, style, ref intrinsicWidth, ref intrinsicHeight);

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

            float left = cbRect.Left + solved.X;
            float top = cbRect.Top + solved.Y;
            float width = Math.Max(0f, solved.Width);
            float height = Math.Max(0f, solved.Height);

            box.Geometry.ContentBox = new SKRect(left, top, left + width, top + height);
            box.Geometry.Padding = style.Padding;
            box.Geometry.Border = style.BorderThickness;
            box.Geometry.Margin = new Thickness(
                solved.MarginLeft,
                solved.MarginTop,
                solved.MarginRight,
                solved.MarginBottom);

            SyncBoxes(box.Geometry);
        }

        private static void EnsureIntrinsicSize(LayoutBox box, CssComputed style, ref float width, ref float height)
        {
            if (!float.IsFinite(width) || width < 0f) width = 0f;
            if (!float.IsFinite(height) || height < 0f) height = 0f;

            if (style.Width.HasValue && style.Width.Value > 0)
            {
                width = (float)style.Width.Value;
            }
            if (style.Height.HasValue && style.Height.Value > 0)
            {
                height = (float)style.Height.Value;
            }

            if (box.SourceNode is not Element element)
            {
                if (height <= 0f && style.LineHeight.HasValue) height = (float)style.LineHeight.Value;
                return;
            }

            string tag = element.TagName?.ToUpperInvariant() ?? string.Empty;

            if (width <= 0f)
            {
                if (TryParseLengthAttribute(element, "width", out var attrW)) width = attrW;
                else if (tag == "IMG") width = 300f;
                else if (tag == "SVG" || tag == "CANVAS") width = 24f;
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
                else if (tag == "IMG") height = 150f;
                else if (tag == "SVG" || tag == "CANVAS") height = 24f;
                else if (tag == "INPUT" || tag == "SELECT") height = 24f;
                else if (tag == "BUTTON") height = 36f;
                else if (tag == "TEXTAREA") height = 48f;
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
