using SkiaSharp;
using FenBrowser.FenEngine.Layout;

namespace FenBrowser.FenEngine.Adapters
{
    /// <summary>
    /// SkiaSharp-based implementation of ITextMeasurer.
    /// This is a simple implementation that doesn't use RichTextKit.
    /// 
    /// RULE 5: If we need RichTextKit, create RichTextKitMeasurer as an alternative.
    /// </summary>
    public class SkiaTextMeasurer : ITextMeasurer
    {
        public float MeasureWidth(string text, string fontFamily, float fontSize, int fontWeight = 400)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            
            var typeface = TextLayoutHelper.ResolveTypeface(fontFamily, text, fontWeight, SKFontStyleSlant.Upright);
            
            using var paint = new SKPaint
            {
                TextSize = fontSize,
                Typeface = typeface,
                IsAntialias = true
            };
            
            return paint.MeasureText(text);
        }
        
        public float GetLineHeight(string fontFamily, float fontSize, int fontWeight = 400)
        {
            var typeface = TextLayoutHelper.ResolveTypeface(fontFamily, "", fontWeight, SKFontStyleSlant.Upright);
            
            using var font = new SKFont(typeface, fontSize);
            var metrics = font.Metrics;
            
            // FenEngine's standard line-height: 1.2x font size
            // This is WE decide, not what Skia suggests
            return fontSize * 1.2f;
        }
    }
}
