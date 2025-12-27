using SkiaSharp;

namespace FenBrowser.FenEngine.Layout
{
    /// <summary>
    /// Parsed transform value.
    /// Used by BoxModel and Renderer.
    /// </summary>
    public class TransformParsed
    {
        public float TranslateX { get; set; }
        public float TranslateY { get; set; }
        public float ScaleX { get; set; } = 1f;
        public float ScaleY { get; set; } = 1f;
        public float Rotate { get; set; } // degrees
        public float SkewX { get; set; }
        public float SkewY { get; set; }
    }
}
