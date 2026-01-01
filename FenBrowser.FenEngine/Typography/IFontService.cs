using SkiaSharp;

namespace FenBrowser.FenEngine.Typography
{
    /// <summary>
    /// Font service interface. Wraps all font operations.
    /// 
    /// RULE 1: This service MEASURES and SHAPES text.
    /// It does NOT decide line breaks, line height, or layout.
    /// FenEngine makes those decisions.
    /// </summary>
    public interface IFontService
    {
        /// <summary>
        /// Get normalized font metrics for a font configuration.
        /// The returned LineHeight is calculated by FenEngine based on CSS rules.
        /// </summary>
        /// <param name="fontFamily">Font family name (e.g., "Arial", "sans-serif")</param>
        /// <param name="fontSize">Font size in pixels</param>
        /// <param name="fontWeight">CSS font-weight (100-900)</param>
        /// <param name="cssLineHeight">CSS line-height value (null = "normal")</param>
        /// <returns>Normalized metrics where FenEngine controls line height</returns>
        NormalizedFontMetrics GetMetrics(
            string fontFamily, 
            float fontSize, 
            int fontWeight = 400,
            float? cssLineHeight = null);
        
        /// <summary>
        /// Measure the width of text WITHOUT shaping (fast path for ASCII).
        /// </summary>
        /// <param name="text">Text to measure</param>
        /// <param name="fontFamily">Font family</param>
        /// <param name="fontSize">Font size</param>
        /// <param name="fontWeight">Font weight</param>
        /// <returns>Width in pixels</returns>
        float MeasureTextWidth(
            string text, 
            string fontFamily, 
            float fontSize, 
            int fontWeight = 400);
        
        /// <summary>
        /// Shape text into positioned glyphs using HarfBuzz.
        /// Use this for complex scripts and final rendering.
        /// </summary>
        /// <param name="text">Text to shape</param>
        /// <param name="fontFamily">Font family</param>
        /// <param name="fontSize">Font size</param>
        /// <param name="fontWeight">Font weight</param>
        /// <returns>Glyph run ready for rendering</returns>
        GlyphRun ShapeText(
            string text, 
            string fontFamily, 
            float fontSize, 
            int fontWeight = 400);
        
        /// <summary>
        /// Resolve a font family to an actual typeface.
        /// Handles fallback chains (e.g., "Arial, sans-serif").
        /// </summary>
        /// <param name="fontFamily">Font family specification</param>
        /// <param name="fontWeight">Font weight</param>
        /// <param name="fontStyle">Font style (normal, italic)</param>
        /// <returns>Resolved typeface</returns>
        SKTypeface ResolveTypeface(
            string fontFamily, 
            int fontWeight = 400, 
            SKFontStyleSlant fontStyle = SKFontStyleSlant.Upright);
    }
}
