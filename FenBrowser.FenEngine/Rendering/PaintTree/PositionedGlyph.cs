using SkiaSharp;

namespace FenBrowser.FenEngine.Rendering
{
    /// <summary>
    /// Represents a single positioned glyph for text rendering.
    /// The renderer receives pre-shaped glyphs - it does NOT shape text.
    /// </summary>
    public readonly struct PositionedGlyph
    {
        /// <summary>
        /// The glyph ID from the font.
        /// </summary>
        public ushort GlyphId { get; init; }
        
        /// <summary>
        /// X position in document coordinates.
        /// </summary>
        public float X { get; init; }
        
        /// <summary>
        /// Y position (baseline) in document coordinates.
        /// </summary>
        public float Y { get; init; }
        
        public PositionedGlyph(ushort glyphId, float x, float y)
        {
            GlyphId = glyphId;
            X = x;
            Y = y;
        }
    }
}
