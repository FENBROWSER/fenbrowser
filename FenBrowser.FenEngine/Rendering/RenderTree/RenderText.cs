using SkiaSharp;
using FenBrowser.Core;
using System.Collections.Generic;

namespace FenBrowser.FenEngine.Rendering
{
    /// <summary>
    /// Represents a text node.
    /// </summary>
    public class RenderText : RenderObject
    {
        public string Text { get; set; }

        public override void Layout(SKSize availableSize)
        {
            if (string.IsNullOrEmpty(Text))
            {
                Bounds = SKRect.Empty;
                return;
            }

            var style = Style ?? Parent?.Style;
            
            using (var paint = new SKPaint())
            {
                paint.TextSize = (float)(style?.FontSize ?? 16.0);
                paint.Typeface = style?.FontFamily ?? SKTypeface.Default;
                paint.IsAntialias = true;

                // Simple measurement (no wrapping support yet)
                float width = paint.MeasureText(Text);
                float height = paint.FontSpacing;

                Bounds = SKRect.Create(0, 0, width, height);
            }
        }
    }
}
