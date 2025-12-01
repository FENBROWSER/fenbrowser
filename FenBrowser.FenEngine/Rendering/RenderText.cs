using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using System.Collections.Generic;

namespace FenBrowser.FenEngine.Rendering
{
    /// <summary>
    /// Represents a text node.
    /// </summary>
    public class RenderText : RenderObject
    {
        public string Text { get; set; }

        private static TextBlock _measureBlock;

        public override void Layout(Size availableSize)
        {
            if (string.IsNullOrEmpty(Text))
            {
                Bounds = new Rect(0, 0, 0, 0);
                return;
            }

            if (_measureBlock == null)
            {
                _measureBlock = new TextBlock();
            }

            var style = Style ?? Parent?.Style;
            
            _measureBlock.Text = Text;
            _measureBlock.FontSize = style?.FontSize ?? 16.0;
            _measureBlock.FontWeight = style?.FontWeight ?? FontWeight.Normal;
            _measureBlock.FontStyle = style?.FontStyle ?? FontStyle.Normal;
            if (style?.FontFamily != null) _measureBlock.FontFamily = style.FontFamily;
            else _measureBlock.FontFamily = new FontFamily("Segoe UI");

            // Handle wrapping
            if (availableSize.Width < double.PositiveInfinity)
            {
                 _measureBlock.TextWrapping = TextWrapping.Wrap;
                 _measureBlock.Width = availableSize.Width;
            }
            else
            {
                 _measureBlock.TextWrapping = TextWrapping.NoWrap;
                 _measureBlock.Width = double.NaN; // Auto
            }

                 _measureBlock.Measure(new Size(double.IsInfinity(availableSize.Width) ? double.PositiveInfinity : availableSize.Width, double.PositiveInfinity));
            
            // Add a tiny buffer to avoid rounding errors causing wrap in the final renderer
            // Bounds = new /* Windows. */Foundation.Rect(0, 0, _measureBlock.DesiredSize.Width + 1, _measureBlock.DesiredSize.Height); // Foundation namespace not available in Avalonia
        }
    }
}
