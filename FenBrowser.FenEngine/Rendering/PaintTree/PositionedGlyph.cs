using SkiaSharp;
using System;

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

        public bool IsRenderable => GlyphId != 0 && float.IsFinite(X) && float.IsFinite(Y);
        
        public PositionedGlyph(ushort glyphId, float x, float y)
        {
            GlyphId = glyphId;
            X = float.IsFinite(x) ? x : 0f;
            Y = float.IsFinite(y) ? y : 0f;
        }

        public override string ToString() => $"glyph={GlyphId} @ ({X}, {Y})";
    }
}
