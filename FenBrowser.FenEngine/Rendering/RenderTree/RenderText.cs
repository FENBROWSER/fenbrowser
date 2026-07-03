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

            using (var font = new SKFont(style?.FontFamily ?? SKTypeface.Default, (float)(style?.FontSize ?? 16.0)))
            {
                // Simple measurement (no wrapping support yet)
                float width = font.MeasureText(Text);
                float height = font.Spacing;

                Bounds = SKRect.Create(0, 0, width, height);
            }
        }
    }
}
