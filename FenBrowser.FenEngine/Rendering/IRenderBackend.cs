using SkiaSharp;

namespace FenBrowser.FenEngine.Rendering
{
    /// <summary>
    /// Abstract rendering backend interface.
    /// 
    /// RULE 4: No OpenGL/Skia types escape this layer.
    /// All rendering goes through this interface.
    /// 
    /// Benefits:
    /// - Testing: HeadlessRenderBackend for unit tests
    /// - Portability: Swap Skia for Direct2D without touching layout
    /// - Future-proof: macOS deprecated OpenGL; we're ready
    /// </summary>
    public interface IRenderBackend
    {
        #region Primitives
        
        /// <summary>
        /// Draw a filled rectangle.
        /// </summary>
        void DrawRect(SKRect rect, SKColor color, float opacity = 1f);
        
        /// <summary>
        /// Draw a rectangle outline (stroke).
        /// </summary>
        void DrawRectStroke(SKRect rect, SKColor color, float strokeWidth, float opacity = 1f);
        
        /// <summary>
        /// Draw a rounded rectangle.
        /// </summary>
        /// <summary>
        /// Draw a rounded rectangle.
        /// </summary>
        void DrawRoundRect(SKRect rect, float radiusX, float radiusY, SKColor color, float opacity = 1f);
        
        /// <summary>
        /// Draw a filled path.
        /// Essential for complex shapes (non-uniform border radius, SVG paths).
        /// </summary>
        void DrawPath(SKPath path, SKColor color, float opacity = 1f);
        
        /// <summary>
        /// Draw a filled rectangle with a shader (gradient).
        /// </summary>
        void DrawRect(SKRect rect, SKShader shader, float opacity = 1f);

        /// <summary>
        /// Draw a rounded rectangle with a shader.
        /// </summary>
        void DrawRoundRect(SKRect rect, float radiusX, float radiusY, SKShader shader, float opacity = 1f);
        
        /// <summary>
        /// Draw a filled path with a shader.
        /// </summary>
        void DrawPath(SKPath path, SKShader shader, float opacity = 1f);

        #endregion
        
        #region Borders
        
        /// <summary>
        /// Draw CSS-style borders (potentially different on each side).
        /// </summary>
        void DrawBorder(SKRect rect, BorderStyle border);
        
        #endregion
        
        #region Text
        
        /// <summary>
        /// Draw a glyph run at a position.
        /// The run was created by IFontService.ShapeText().
        /// 
        /// RULE 1: We just draw. We don't decide layout.
        /// </summary>
        void DrawGlyphRun(SKPoint origin, FenBrowser.FenEngine.Typography.GlyphRun glyphs, SKColor color, float opacity = 1f);
        
        /// <summary>
        /// Simple text drawing for fallback cases.
        /// </summary>
        void DrawText(string text, SKPoint origin, SKColor color, float fontSize, SKTypeface typeface, float opacity = 1f);
        
        #endregion
        
        #region Images
        
        /// <summary>
        /// Draw an image.
        /// </summary>
        void DrawImage(SKImage image, SKRect destRect, float opacity = 1f);
        
        /// <summary>
        /// Draw an SKPicture (for SVG).
        /// </summary>
        void DrawPicture(SKPicture picture, SKRect destRect, float opacity = 1f);
        
        #endregion
        
        #region Clipping & Layers
        
        /// <summary>
        /// Push a clip rectangle. All subsequent drawing is clipped.
        /// </summary>
        void PushClip(SKRect clipRect);
        
        /// <summary>
        /// Push a rounded clip.
        /// </summary>
        void PushClip(SKRect clipRect, float radiusX, float radiusY);
        
        /// <summary>
        /// Push a complex path clip.
        /// </summary>
        void PushClip(SKPath clipPath);
        
        /// <summary>
        /// Pop the current clip, restoring the previous clip state.
        /// </summary>
        void PopClip();
        
        /// <summary>
        /// Push an opacity layer. All subsequent drawing has this opacity applied.
        /// </summary>
        void PushLayer(float opacity);
        
        /// <summary>
        /// Push a transform layer.
        /// </summary>
        void PushTransform(SKMatrix transform);
        
        /// <summary>
        /// Pop the current layer/transform.
        /// </summary>
        void PopLayer();
        
        /// <summary>
        /// Apply an image as a mask to the current layer using DstIn blend mode.
        /// Caller must ensure a layer is active.
        /// </summary>
        void ApplyMask(SKImage mask, SKRect bounds);
        
        #endregion
        
        #region Shadows
        
        /// <summary>
        /// Draw a box shadow for a rect.
        /// </summary>
        void DrawBoxShadow(SKRect rect, float offsetX, float offsetY, float blurRadius, float spreadRadius, SKColor color);
        
        /// <summary>
        /// Draw a shadow for a path (e.g. rounded rect).
        /// </summary>
        void DrawShadow(SKPath path, float offsetX, float offsetY, float blurRadius, SKColor color);
        
        #endregion
        
        #region State
        
        /// <summary>
        /// Save the current state (clip, transform).
        /// </summary>
        void Save();
        
        /// <summary>
        /// Restore the previous state.
        /// </summary>
        void Restore();
        
        /// <summary>
        /// Clear the canvas with a color.
        /// </summary>
        void Clear(SKColor color);
        
        #endregion
    }
    
    /// <summary>
    /// CSS-style border specification.
    /// </summary>
    public struct BorderStyle
    {
        public float TopWidth;
        public float RightWidth;
        public float BottomWidth;
        public float LeftWidth;
        
        public SKColor TopColor;
        public SKColor RightColor;
        public SKColor BottomColor;
        public SKColor LeftColor;
        
        public SKPoint TopLeftRadius;
        public SKPoint TopRightRadius;
        public SKPoint BottomRightRadius;
        public SKPoint BottomLeftRadius;
        
        public static BorderStyle Uniform(float width, SKColor color, float radius = 0)
        {
            var r = new SKPoint(radius, radius);
            return new BorderStyle
            {
                TopWidth = width,
                RightWidth = width,
                BottomWidth = width,
                LeftWidth = width,
                TopColor = color,
                RightColor = color,
                BottomColor = color,
                LeftColor = color,
                TopLeftRadius = r,
                TopRightRadius = r,
                BottomRightRadius = r,
                BottomLeftRadius = r
            };
        }
    }
}
