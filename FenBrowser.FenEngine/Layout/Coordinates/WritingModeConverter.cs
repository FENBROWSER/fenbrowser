using SkiaSharp;
using FenBrowser.Core.Css;
using FenBrowser.Core;

namespace FenBrowser.FenEngine.Layout.Coordinates
{
    public static class WritingModeConverter
    {
        public static LogicalSize ToLogical(SKSize physicalSize, string writingMode)
        {
            if (writingMode == "vertical-rl" || writingMode == "vertical-lr")
            {
                return new LogicalSize(physicalSize.Height, physicalSize.Width);
            }
            return new LogicalSize(physicalSize.Width, physicalSize.Height);
        }

        public static SKSize ToPhysical(LogicalSize logicalSize, string writingMode)
        {
            if (writingMode == "vertical-rl" || writingMode == "vertical-lr")
            {
                return new SKSize(logicalSize.Block, logicalSize.Inline);
            }
            return new SKSize(logicalSize.Inline, logicalSize.Block);
        }

        public static SKRect ToPhysicalRect(LogicalRect logicalRect, SKSize containerPhysicalSize, string writingMode)
        {
            // For horizontal-tb (default):
            // InlineStart -> Left
            // BlockStart -> Top
            // InlineSize -> Width
            // BlockSize -> Height
            
            if (string.IsNullOrEmpty(writingMode) || writingMode == "horizontal-tb")
            {
                return new SKRect(
                    logicalRect.InlineStart,
                    logicalRect.BlockStart,
                    logicalRect.InlineStart + logicalRect.InlineSize,
                    logicalRect.BlockStart + logicalRect.BlockSize
                );
            }

            if (writingMode == "vertical-rl")
            {
                // vertical-rl:
                // Block flows Right to Left (width of container matters here!)
                // Inline flows Top to Bottom
                //
                // BlockStart=0 => Right edge of container
                // Block increases => moves Left
                // InlineStart=0 => Top edge
                // Inline increases => moves Down
                
                // Physical X = ContainerWidth - BlockStart - BlockSize
                // Physical Y = InlineStart
                // Physical W = BlockSize
                // Physical H = InlineSize
                
                float x = containerPhysicalSize.Width - logicalRect.BlockStart - logicalRect.BlockSize;
                float y = logicalRect.InlineStart;
                
                return new SKRect(
                    x,
                    y,
                    x + logicalRect.BlockSize,
                    y + logicalRect.InlineSize
                );
            }

            // Fallback
            return new SKRect(
                    logicalRect.InlineStart,
                    logicalRect.BlockStart,
                    logicalRect.InlineStart + logicalRect.InlineSize,
                    logicalRect.BlockStart + logicalRect.BlockSize
                );
        }
        
        public static LogicalMargin ToLogicalMargin(Thickness physicalMargin, string writingMode)
        {
             if (writingMode == "vertical-rl")
             {
                 // Top -> InlineStart
                 // Right -> BlockStart
                 // Bottom -> InlineEnd
                 // Left -> BlockEnd
                 return new LogicalMargin 
                 {
                     InlineStart = (float)physicalMargin.Top,
                     BlockStart = (float)physicalMargin.Right,
                     InlineEnd = (float)physicalMargin.Bottom,
                     BlockEnd = (float)physicalMargin.Left
                 };
             }
             
             // Horizontal
             return new LogicalMargin
             {
                 InlineStart = (float)physicalMargin.Left,
                 BlockStart = (float)physicalMargin.Top,
                 InlineEnd = (float)physicalMargin.Right,
                 BlockEnd = (float)physicalMargin.Bottom
             };
        }
    }
}
