using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using System.Collections.Generic;

namespace FenBrowser.FenEngine.Rendering
{
    /// <summary>
    /// Converts the Render Tree into XAML visual elements.
    /// </summary>
    public static class Painter
    {
        public static Control Paint(RenderObject root)
        {
            if (root == null) return null;

            // We'll use a Canvas as the drawing surface for absolute positioning
            var canvas = new Canvas();
            
            // If the root has a size, set it on the canvas
            if (root.Bounds.Width > 0 && root.Bounds.Height > 0)
            {
                canvas.Width = root.Bounds.Width;
                canvas.Height = root.Bounds.Height;
            }

            PaintRecursive(root, canvas, 0, 0);

            return canvas;
        }

        private static void PaintRecursive(RenderObject node, Canvas canvas, double parentX, double parentY)
        {
            if (node == null) return;

            // Calculate absolute position
            double absX = parentX + node.Bounds.X;
            double absY = parentY + node.Bounds.Y;

            Control visual = null;

            if (node is RenderBox box)
            {
                // Draw Box (Background, Border)
                // We can use a Border control for this
                var border = new Border
                {
                    Width = node.Bounds.Width,
                    Height = node.Bounds.Height,
                    Background = node.Style?.Background,
                    BorderBrush = node.Style?.BorderBrush,
                    BorderThickness = node.Style?.BorderThickness ?? new Thickness(0),
                    CornerRadius = node.Style?.BorderRadius ?? new CornerRadius(0)
                };
                
                visual = border;
            }
            else if (node is RenderText textNode)
            {
                // Draw Text
                // RenderText doesn't have its own Style, it inherits from Parent
                var style = node.Style ?? node.Parent?.Style;

                var textBlock = new Avalonia.Controls.TextBlock
                {
                    Text = textNode.Text,
                    FontSize = style?.FontSize ?? 16,
                    Foreground = style?.Foreground ?? Brushes.Black,
                    FontFamily = style?.FontFamily ?? new Avalonia.Media.FontFamily("Segoe UI"),
                    FontWeight = style?.FontWeight ?? Avalonia.Media.FontWeight.Normal,
                    FontStyle = style?.FontStyle ?? Avalonia.Media.FontStyle.Normal,
                    // Layout handles wrapping by splitting lines (atomic) or we let XAML wrap if it fits?
                    // For now, we rely on our Layout to position things. 
                    // If we want internal wrapping for long text, we can set Wrap.
                    // But our LayoutInline assumes atomic boxes. 
                    // Let's set Wrap because RenderText.Layout assumes it can wrap.
                    TextWrapping = TextWrapping.Wrap 
                };
                
                // If we set Wrap, we must constrain the Width of the TextBlock
                if (node.Bounds.Width > 0)
                {
                    textBlock.Width = node.Bounds.Width;
                }
                
                visual = textBlock;
            }

            if (visual != null)
            {
                Canvas.SetLeft(visual, absX);
                Canvas.SetTop(visual, absY);
                canvas.Children.Add(visual);
            }

            // Recurse
            foreach (var child in node.Children)
            {
                PaintRecursive(child, canvas, absX, absY);
            }
        }
    }
}
