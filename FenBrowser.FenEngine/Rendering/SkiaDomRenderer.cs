using SkiaSharp;
using FenBrowser.Core;
using FenBrowser.Core.Logging;
using System.Collections.Generic;
using System.Threading.Tasks;
using System;

namespace FenBrowser.FenEngine.Rendering
{
    /// <summary>
    /// A new experimental renderer built from scratch using SkiaSharp.
    /// This bypasses Avalonia's high-level layout system to give us pixel-perfect control.
    /// </summary>
    public class SkiaDomRenderer
    {
        private const float DefaultFontSize = 16f;

        // Box Model storage
        private class BoxModel
        {
            public SKRect MarginBox;
            public SKRect BorderBox;
            public SKRect PaddingBox;
            public SKRect ContentBox;
            public Avalonia.Thickness Margin;
            public Avalonia.Thickness Border;
            public Avalonia.Thickness Padding;
        }

        private readonly Dictionary<LiteElement, BoxModel> _boxes = new Dictionary<LiteElement, BoxModel>();
        private Dictionary<LiteElement, CssComputed> _styles;

        public SkiaDomRenderer() { }

        public void Render(LiteElement root, SKCanvas canvas, Dictionary<LiteElement, CssComputed> styles, SKRect viewport)
        {
            _styles = styles;
            _boxes.Clear();
            
            // Draw background only in the strict viewport of the control
            using (var paint = new SKPaint { Color = SKColors.White })
            {
                canvas.DrawRect(viewport, paint);
            }

            if (root == null) return;

            // 1. Layout Pass
            // Use viewport width for layout constraints
            float initialWidth = viewport.Width;
            if (initialWidth <= 0) initialWidth = 1920; // Fallback

            try
            {
                ComputeLayout(root, 0, 0, initialWidth);

                // 2. Paint Pass
                DrawLayout(root, canvas);
            }
            catch (Exception)
            {
                 // Ignore render errors to prevent crash
            }
        }

        private void ComputeLayout(LiteElement node, float x, float y, float availableWidth)
        {
            // Get styles
            CssComputed style = null;
            if (_styles != null) _styles.TryGetValue(node, out style);

            // VISIBILITY CHECK
            if (ShouldHide(node, style)) return;

            // Apply User Agent (UA) styles for inputs if missing
            ApplyUserAgentStyles(node, ref style);

            // Calculate Box Model
            var box = new BoxModel();
            
            // Extract CSS values (default to 0)
            box.Margin = style?.Margin ?? new Avalonia.Thickness(0);
            box.Border = style?.BorderThickness ?? new Avalonia.Thickness(0);
            box.Padding = style?.Padding ?? new Avalonia.Thickness(0);

            // Width calculation
            float marginLeft = (float)box.Margin.Left;
            float marginRight = (float)box.Margin.Right;
            float borderLeft = (float)box.Border.Left;
            float borderRight = (float)box.Border.Right;
            float paddingLeft = (float)box.Padding.Left;
            float paddingRight = (float)box.Padding.Right;

            // Content width = available - (margin + border + padding)
            float contentWidth = availableWidth - (marginLeft + marginRight + borderLeft + borderRight + paddingLeft + paddingRight);
            if (contentWidth < 0) contentWidth = 0;

            // Explicit width override
            if (style?.Width.HasValue == true)
            {
                contentWidth = (float)style.Width.Value;
            }

            // Position (relative to parent content box, passed as x,y)
            float currentX = x + marginLeft;
            float currentY = y + (float)box.Margin.Top;

            // Heights - initially 0, computed after children
            box.MarginBox = new SKRect(x, y, x + availableWidth, y); 
            box.BorderBox = new SKRect(currentX, currentY, currentX + borderLeft + contentWidth + borderRight, currentY); 
            
            box.PaddingBox = new SKRect(
                box.BorderBox.Left + borderLeft, 
                box.BorderBox.Top + (float)box.Border.Top,
                box.BorderBox.Right - borderRight, 
                box.BorderBox.Top + (float)box.Border.Top);
            
            box.ContentBox = new SKRect(
                box.PaddingBox.Left + paddingLeft,
                box.PaddingBox.Top + (float)box.Padding.Top,
                box.PaddingBox.Right - paddingRight,
                box.PaddingBox.Top + (float)box.Padding.Top);


            // Process Children
            float childY = box.ContentBox.Top;
            
            // REPLACED ELEMENTS INTROSPECTION
            // If this is a leaf node (or replaced element like input/img), we need to give it size
            string tag = node.Tag?.ToUpperInvariant();
            bool isReplaced = tag == "IMG" || tag == "INPUT" || tag == "BUTTON" || tag == "TEXTAREA" || tag == "SELECT";
            
            if (isReplaced)
            {
                // Default intrinsic sizes if not specified by CSS
                float intrinsicHeight = 0;
                // float intrinsicWidth = 0; // Unused for now

                if (tag == "INPUT" || tag == "BUTTON" || tag == "SELECT") intrinsicHeight = 22;
                if (tag == "TEXTAREA") intrinsicHeight = 40;
                if (tag == "IMG") 
                {
                    intrinsicHeight = 50; // Placeholder
                    // TODO: Fetch actual image size
                }
                
                // If CSS didn't set height, use intrinsic
                if (!style.Height.HasValue && intrinsicHeight > 0)
                {
                   childY += intrinsicHeight;
                }
            }

            if (node.Children != null)
            {
                foreach (var child in node.Children)
                {
                    ComputeLayout(child, box.ContentBox.Left, childY, contentWidth);
                    
                    if (_boxes.TryGetValue(child, out var childBox))
                    {
                        float childHeightWithMargin = childBox.MarginBox.Height;
                        childY += childHeightWithMargin;
                    }
                }
            }

            // TEXT CONTENT
            if (node.IsText)
            {
                // Measure text
                using (var paint = new SKPaint())
                {
                    paint.TextSize = style?.FontSize != null ? (float)style.FontSize.Value : DefaultFontSize;
                    
                    var text = node.Text ?? "";
                    var bounds = new SKRect();
                    paint.MeasureText(text, ref bounds);
                    
                    // Simple text flow (no wrapping yet)
                    float textHeight = bounds.Height + 5; 
                    childY += textHeight;
                }
            }

            // Finalize Heights
            float contentHeight = childY - box.ContentBox.Top;
            if (style?.Height.HasValue == true) contentHeight = (float)style.Height.Value;

            box.ContentBox.Bottom = box.ContentBox.Top + contentHeight;
            box.PaddingBox.Bottom = box.ContentBox.Bottom + (float)box.Padding.Bottom;
            box.BorderBox.Bottom = box.PaddingBox.Bottom + (float)box.Border.Bottom;
            box.MarginBox.Bottom = box.BorderBox.Bottom + (float)box.Margin.Bottom;

            _boxes[node] = box;
        }

        private void DrawLayout(LiteElement node, SKCanvas canvas)
        {
            if (!_boxes.TryGetValue(node, out var box)) return;
            
            CssComputed style = null;
            if (_styles != null) _styles.TryGetValue(node, out style);

            // 1. Draw Background
            // SAFE ACCESS: Use BackgroundColor struct, do NOT access Brush objects on render thread
            if (style?.BackgroundColor.HasValue == true)
            {
                var c = style.BackgroundColor.Value;
                // Skia color from Avalonia color
                using (var paint = new SKPaint { Color = new SKColor(c.R, c.G, c.B, c.A) })
                {
                    canvas.DrawRect(box.BorderBox, paint); 
                }
            }

            // 2. Draw Borders
            if (box.Border.Left > 0 || box.Border.Top > 0 || box.Border.Right > 0 || box.Border.Bottom > 0)
            {
                using (var paint = new SKPaint { Style = SKPaintStyle.Stroke })
                {
                    paint.Color = SKColors.Black; 
                    // SAFE ACCESS: Use BorderBrushColor struct
                    if (style?.BorderBrushColor.HasValue == true)
                    {
                         var c = style.BorderBrushColor.Value;
                         paint.Color = new SKColor(c.R, c.G, c.B, c.A);
                    }

                    float strokeWidth = (float)Math.Max(box.Border.Top, Math.Max(box.Border.Left, box.Border.Bottom));
                    if (strokeWidth < 1) strokeWidth = 1;
                    paint.StrokeWidth = strokeWidth;
                    
                    canvas.DrawRect(box.BorderBox, paint);
                }
            }

            // 3. Draw Text
            if (node.IsText && !string.IsNullOrWhiteSpace(node.Text))
            {
                using (var paint = new SKPaint())
                {
                    paint.TextSize = style?.FontSize != null ? (float)style.FontSize.Value : DefaultFontSize;
                    paint.Color = SKColors.Black; 
                    paint.IsAntialias = true;

                    // SAFE ACCESS: Use ForegroundColor struct
                    if (style?.ForegroundColor.HasValue == true)
                    {
                        var c = style.ForegroundColor.Value;
                        paint.Color = new SKColor(c.R, c.G, c.B, c.A);
                    }
                    
                    canvas.DrawText(node.Text, box.ContentBox.Left, box.ContentBox.Bottom - 5, paint); 
                }
            }
            
            // 3.5 Draw Replaced Elements Helpers
            string tag = node.Tag?.ToUpperInvariant();
            if (tag == "INPUT" || tag == "TEXTAREA")
            {
                // Draw a debug border/background if the element is otherwise invisible
                // Real browsers rely on UA stylesheet. We simulate it here.
                if (box.Border.Top == 0)
                {
                    using (var paint = new SKPaint { Style = SKPaintStyle.Stroke, Color = SKColors.Gray, StrokeWidth = 1 })
                    {
                        canvas.DrawRect(box.BorderBox, paint);
                    }
                }
                // Placeholder text if empty?
            }
            if (tag == "IMG")
            {
                 // Draw placeholder X
                 using (var paint = new SKPaint { Style = SKPaintStyle.Stroke, Color = SKColors.LightGray })
                 {
                    canvas.DrawRect(box.ContentBox, paint);
                    canvas.DrawLine(box.ContentBox.Left, box.ContentBox.Top, box.ContentBox.Right, box.ContentBox.Bottom, paint);
                    canvas.DrawLine(box.ContentBox.Right, box.ContentBox.Top, box.ContentBox.Left, box.ContentBox.Bottom, paint);
                 }
            }

            // 4. Recurse
            if (node.Children != null)
            {
                foreach (var child in node.Children)
                {
                    DrawLayout(child, canvas);
                }
            }
        }
        private void ApplyUserAgentStyles(LiteElement node, ref CssComputed style)
        {
            if (node == null) return;
            string tag = node.Tag?.ToUpperInvariant();

            if (tag == "INPUT" || tag == "TEXTAREA" || tag == "BUTTON" || tag == "SELECT")
            {
                if (style == null) style = new CssComputed();

                // 1. Force Background if missing or transparent
                // Note: SkiaColor transparent is 0 (alpha 0)
                bool hasBackground = style.BackgroundColor.HasValue && style.BackgroundColor.Value.A > 0;
                
                if (!hasBackground)
                {
                    // Default to white for inputs, light gray for buttons
                    style.BackgroundColor = (tag == "BUTTON") ? Avalonia.Media.Colors.LightGray : Avalonia.Media.Colors.White;
                }

                // 2. Force Border if missing
                if (style.BorderThickness.Top == 0 && style.BorderThickness.Left == 0)
                {
                    style.BorderThickness = new Avalonia.Thickness(1);
                    style.BorderBrushColor = Avalonia.Media.Colors.Gray;
                }
                
                // 3. Force Padding if missing
                if (style.Padding.Top == 0 && style.Padding.Left == 0)
                {
                    style.Padding = new Avalonia.Thickness(2);
                }
            }
        }

        private bool ShouldHide(LiteElement node, CssComputed style)
        {
            if (node == null) return true;
            
            // 1. Tag Filtering
            string tag = node.Tag?.ToUpperInvariant();
            if (tag == "HEAD" || tag == "SCRIPT" || tag == "STYLE" || tag == "META" || tag == "LINK" || tag == "TITLE" || tag == "NOSCRIPT")
                return true;

            // 2. CSS Display: None
            if (style != null && string.Equals(style.Display, "none", StringComparison.OrdinalIgnoreCase))
                return true;

            return false;
        }
    }
}
