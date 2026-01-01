using SkiaSharp;
using FenBrowser.Core;

namespace FenBrowser.FenEngine.Layout
{
    /// <summary>
    /// CSS Box Model storage for a single element.
    /// Represents the margin, border, padding, and content boxes.
    /// </summary>
    /// <summary>
    /// Represents a single line of laid-out text.
    /// </summary>
    public struct ComputedTextLine
    {
        public string Text;
        public SKPoint Origin; // Relative to ContentBox
        public float Width;
        public float Height;
        public float Baseline; // Ascent from Top
    }

    /// <summary>
    /// CSS Box Model storage for a single element.
    /// Represents the margin, border, padding, and content boxes.
    /// </summary>
    public class BoxModel
    {
        /// <summary>
        /// The margin box (outermost).
        /// </summary>
        public SKRect MarginBox;
        
        /// <summary>
        /// The border box.
        /// </summary>
        public SKRect BorderBox;
        
        /// <summary>
        /// The padding box.
        /// </summary>
        public SKRect PaddingBox;
        
        /// <summary>
        /// The content box (innermost).
        /// </summary>
        public SKRect ContentBox;
        
        /// <summary>
        /// Margin thickness.
        /// </summary>
        public Thickness Margin;
        
        /// <summary>
        /// Border thickness.
        /// </summary>
        public Thickness Border;
        
        /// <summary>
        /// Padding thickness.
        /// </summary>
        public Thickness Padding;
        
        /// <summary>
        /// Baseline offset for inline alignment.
        /// </summary>
        public float Baseline;

        /// <summary>
        /// Computed line height for text.
        /// </summary>
        public float LineHeight;

        /// <summary>
        /// Distance from baseline to top of content.
        /// </summary>
        public float Ascent;

        /// <summary>
        /// Distance from baseline to bottom of content.
        /// </summary>
        public float Descent;

        /// <summary>
        /// Transform for this element.
        /// </summary>
        public TransformParsed Transform;

        /// <summary>
        /// Computed text lines (if this is a text node).
        /// </summary>
        public List<ComputedTextLine> Lines;
        
        /// <summary>
        /// Creates a default box model.
        /// </summary>
        public BoxModel()
        {
            MarginBox = SKRect.Empty;
            BorderBox = SKRect.Empty;
            PaddingBox = SKRect.Empty;
            ContentBox = SKRect.Empty;
            Margin = new Thickness();
            Border = new Thickness();
            Padding = new Thickness();
            Baseline = 0;
            Lines = null;
        }
        
        /// <summary>
        /// Creates a box model from content box dimensions.
        /// </summary>
        public static BoxModel FromContentBox(float x, float y, float width, float height)
        {
            var box = new BoxModel();
            box.ContentBox = new SKRect(x, y, x + width, y + height);
            box.PaddingBox = box.ContentBox;
            box.BorderBox = box.ContentBox;
            box.MarginBox = box.ContentBox;
            return box;
        }
    }
}
