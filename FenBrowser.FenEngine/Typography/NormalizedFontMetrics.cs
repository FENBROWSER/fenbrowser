using SkiaSharp;

namespace FenBrowser.FenEngine.Typography
{
    /// <summary>
    /// CSS-semantic font metrics. FenEngine calculates these, Skia only informs.
    /// This is the authoritative source for all layout decisions involving text.
    /// 
    /// RULE 2: Skia metrics inform, but FenEngine decides.
    /// </summary>
    public struct NormalizedFontMetrics
    {
        /// <summary>
        /// Distance from baseline to top of tallest glyph (positive value).
        /// Used for CSS baseline alignment.
        /// </summary>
        public float Ascent;
        
        /// <summary>
        /// Distance from baseline to bottom of lowest glyph (positive value).
        /// </summary>
        public float Descent;
        
        /// <summary>
        /// The "strut" height for line boxes.
        /// FenEngine calculates this based on CSS line-height property.
        /// This is NOT Skia's suggestion - WE decide.
        /// </summary>
        public float LineHeight;
        
        /// <summary>
        /// Height of lowercase 'x'. Used for CSS 'ex' units.
        /// </summary>
        public float XHeight;
        
        /// <summary>
        /// Size of 1em. Typically equals font-size. Used for CSS 'em' units.
        /// </summary>
        public float EmSize;
        
        /// <summary>
        /// Leading (extra space between lines beyond ascent+descent).
        /// </summary>
        public float Leading;
        
        /// <summary>
        /// Total content height (Ascent + Descent). Does NOT include leading.
        /// </summary>
        public float ContentHeight => Ascent + Descent;
        
        /// <summary>
        /// Create normalized metrics from raw Skia metrics.
        /// This is where we take control away from Skia.
        /// </summary>
        public static NormalizedFontMetrics FromSkia(SKFontMetrics skMetrics, float fontSize, float? cssLineHeight = null)
        {
            // Skia's ascent is negative (above baseline), we normalize to positive
            float rawAscent = -skMetrics.Ascent;
            float rawDescent = skMetrics.Descent;
            float rawLeading = skMetrics.Leading;
            
            // CSS line-height: if not specified, use "normal" (typically 1.2)
            float lineHeight;
            if (cssLineHeight.HasValue)
            {
                if (cssLineHeight.Value < 3) // It's a multiplier like 1.5
                    lineHeight = cssLineHeight.Value * fontSize;
                else // It's a pixel value
                    lineHeight = cssLineHeight.Value;
            }
            else
            {
                // "normal" line-height: typically 1.2 of font size
                // We decide this, NOT Skia
                lineHeight = fontSize * 1.2f;
            }
            
            // Ensure line height is at least as tall as content
            float contentHeight = rawAscent + rawDescent;
            if (lineHeight < contentHeight)
                lineHeight = contentHeight;
            
            return new NormalizedFontMetrics
            {
                Ascent = rawAscent,
                Descent = rawDescent,
                LineHeight = lineHeight,
                XHeight = skMetrics.XHeight > 0 ? skMetrics.XHeight : fontSize * 0.5f,
                EmSize = fontSize,
                Leading = lineHeight - contentHeight
            };
        }
        
        /// <summary>
        /// Calculate baseline offset for vertical centering within line box.
        /// </summary>
        public float GetBaselineOffset()
        {
            // Center the text content within the line height
            return (Leading / 2f) + Ascent;
        }
    }
}
