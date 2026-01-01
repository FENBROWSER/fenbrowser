using SkiaSharp;

namespace FenBrowser.FenEngine.Typography
{
    /// <summary>
    /// Represents a positioned glyph for rendering.
    /// FenEngine creates these, renderer just draws them.
    /// </summary>
    public struct PositionedGlyph
    {
        /// <summary>
        /// Glyph ID from the font.
        /// </summary>
        public ushort GlyphId;
        
        /// <summary>
        /// X position relative to run origin.
        /// </summary>
        public float X;
        
        /// <summary>
        /// Y position relative to run origin (baseline).
        /// </summary>
        public float Y;
        
        /// <summary>
        /// Advance width to next glyph.
        /// </summary>
        public float AdvanceX;
    }
    
    /// <summary>
    /// A run of shaped glyphs ready for rendering.
    /// Created by IFontService.ShapeText(), consumed by IRenderBackend.DrawGlyphRun().
    /// 
    /// RULE 1: This is the OUTPUT of shaping. Layout decisions are made BEFORE this.
    /// </summary>
    public class GlyphRun
    {
        /// <summary>
        /// The positioned glyphs in this run.
        /// </summary>
        public PositionedGlyph[] Glyphs { get; set; }
        
        /// <summary>
        /// The typeface used for this run.
        /// </summary>
        public SKTypeface Typeface { get; set; }
        
        /// <summary>
        /// Font size for rendering.
        /// </summary>
        public float FontSize { get; set; }
        
        /// <summary>
        /// Total width of the run.
        /// </summary>
        public float Width { get; set; }
        
        /// <summary>
        /// Font metrics for baseline alignment.
        /// </summary>
        public NormalizedFontMetrics Metrics { get; set; }
        
        /// <summary>
        /// The original text (for debugging/accessibility).
        /// </summary>
        public string SourceText { get; set; }
        
        /// <summary>
        /// Number of glyphs in this run.
        /// </summary>
        public int Count => Glyphs?.Length ?? 0;
    }
}
