using SkiaSharp;

namespace FenBrowser.FenEngine.Layout.Coordinates
{
    public enum InlineProgression
    {
        LeftToRight,
        RightToLeft
    }

    public enum BlockProgression
    {
        TopToBottom,
        RightToLeft, // vertical-rl
        LeftToRight  // vertical-lr
    }

    /// <summary>
    /// Represents a point in logical flow-relative coordinates.
    /// </summary>
    public struct LogicalPoint
    {
        public float Inline { get; set; }
        public float Block { get; set; }

        public LogicalPoint(float inline, float block)
        {
            Inline = inline;
            Block = block;
        }
    }

    /// <summary>
    /// Represents a size in logical flow-relative coordinates.
    /// </summary>
    public struct LogicalSize
    {
        public float Inline { get; set; }
        public float Block { get; set; }

        public LogicalSize(float inline, float block)
        {
            Inline = inline;
            Block = block;
        }

        public static readonly LogicalSize Empty = new LogicalSize(0, 0);
    }

    /// <summary>
    /// Represents a rectangle in logical flow-relative coordinates.
    /// </summary>
    public struct LogicalRect
    {
        public float InlineStart { get; set; }
        public float BlockStart { get; set; }
        public float InlineSize { get; set; }
        public float BlockSize { get; set; }

        public float InlineEnd => InlineStart + InlineSize;
        public float BlockEnd => BlockStart + BlockSize;

        public LogicalRect(float inlineStart, float blockStart, float inlineSize, float blockSize)
        {
            InlineStart = inlineStart;
            BlockStart = blockStart;
            InlineSize = inlineSize;
            BlockSize = blockSize;
        }

        public static LogicalRect FromPointAndSize(LogicalPoint location, LogicalSize size)
        {
            return new LogicalRect(location.Inline, location.Block, size.Inline, size.Block);
        }
    }

    /// <summary>
    /// Represents margins in logical flow-relative coordinates.
    /// </summary>
    public struct LogicalMargin
    {
        public float InlineStart { get; set; }
        public float InlineEnd { get; set; }
        public float BlockStart { get; set; }
        public float BlockEnd { get; set; }
        
        public float InlineSum => InlineStart + InlineEnd;
        public float BlockSum => BlockStart + BlockEnd;
    }
}
