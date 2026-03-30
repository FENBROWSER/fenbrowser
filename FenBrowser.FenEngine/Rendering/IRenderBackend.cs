using System;
using SkiaSharp;

namespace FenBrowser.FenEngine.Rendering
{
    /// <summary>
    /// Abstract rendering backend interface.
    /// </summary>
    public interface IRenderBackend
    {
        #region Primitives

        void DrawRect(SKRect rect, SKColor color, float opacity = 1f);
        void DrawRectStroke(SKRect rect, SKColor color, float strokeWidth, float opacity = 1f);
        void DrawRoundRect(SKRect rect, float radiusX, float radiusY, SKColor color, float opacity = 1f);
        void DrawPath(SKPath path, SKColor color, float opacity = 1f);
        void DrawRect(SKRect rect, SKShader shader, float opacity = 1f);
        void DrawRoundRect(SKRect rect, float radiusX, float radiusY, SKShader shader, float opacity = 1f);
        void DrawPath(SKPath path, SKShader shader, float opacity = 1f);

        #endregion

        #region Borders

        void DrawBorder(SKRect rect, BorderStyle border);

        #endregion

        #region Text

        void DrawGlyphRun(SKPoint origin, FenBrowser.FenEngine.Typography.GlyphRun glyphs, SKColor color, float opacity = 1f);
        void DrawText(string text, SKPoint origin, SKColor color, float fontSize, SKTypeface typeface, float opacity = 1f);

        #endregion

        #region Images

        void DrawImage(SKImage image, SKRect destRect, float opacity = 1f);
        void DrawImage(SKImage image, SKRect destRect, SKRect srcRect, float opacity = 1f);
        void DrawPicture(SKPicture picture, SKRect destRect, float opacity = 1f);

        #endregion

        #region Clipping & Layers

        void PushClip(SKRect clipRect);
        void PushClip(SKRect clipRect, float radiusX, float radiusY);
        void PushClip(SKPath clipPath);
        void PopClip();
        void PushLayer(float opacity);
        void PushTransform(SKMatrix transform);
        void PopLayer();
        void ApplyMask(SKImage mask, SKRect bounds);
        void PushFilter(SKImageFilter filter);
        void PopFilter();
        void ApplyBackdropFilter(SKRect bounds, SKImageFilter filter);

        #endregion

        #region Shadows

        void DrawBoxShadow(SKRect rect, float offsetX, float offsetY, float blurRadius, float spreadRadius, SKColor color);
        void DrawShadow(SKPath path, float offsetX, float offsetY, float blurRadius, SKColor color);
        void DrawInsetBoxShadow(SKRect rect, SKPoint[] borderRadius, float offsetX, float offsetY, float blurRadius, float spreadRadius, SKColor color);

        #endregion

        #region State

        int SaveDepth { get; }
        void Save();
        void Restore();
        void RestoreToSaveDepth(int saveDepth);
        void Clear(SKColor color);
        void ExecuteCustomPaint(Action<SKCanvas, SKRect> paintAction, SKRect bounds);

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
