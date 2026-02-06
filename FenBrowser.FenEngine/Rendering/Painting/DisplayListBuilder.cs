using System;
using System.Collections.Generic;
using SkiaSharp;
using FenBrowser.Core.Dom.V2; // For node references if needed

namespace FenBrowser.FenEngine.Rendering
{
    /// <summary>
    /// A fluent builder for creating DisplayLists.
    /// Abstracts the creation of RenderCommands.
    /// </summary>
    public class DisplayListBuilder
    {
        private readonly List<RenderCommand> _commands = new List<RenderCommand>();
        
        // State tracking (optional, can be expanded for optimizations like culling)
        private int _saveCount = 0;

        public DisplayList Build()
        {
            // Return a new DisplayList with the accumulated commands
            // We pass a copy or transfer ownership? Transfer is more efficient.
            var list = new DisplayList(new List<RenderCommand>(_commands));
            _commands.Clear();
            _saveCount = 0;
            return list;
        }

        public void DrawRect(SKRect rect, SKColor color, float opacity = 1f)
        {
            _commands.Add(new DrawRectCommand
            {
                Rect = rect,
                Bounds = rect,
                Color = color,
                Opacity = opacity,
                Style = SKPaintStyle.Fill
            });
        }

        public void DrawRectStroke(SKRect rect, SKColor color, float strokeWidth, float opacity = 1f)
        {
            _commands.Add(new DrawRectCommand
            {
                Rect = rect,
                Bounds = rect,
                Color = color,
                Opacity = opacity,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = strokeWidth
            });
        }

        public void DrawRoundRect(SKRect rect, float rx, float ry, SKColor color, float opacity = 1f)
        {
            _commands.Add(new DrawRoundRectCommand
            {
                Rect = rect,
                Bounds = rect,
                RadiusX = rx,
                RadiusY = ry,
                Color = color,
                Opacity = opacity,
                Style = SKPaintStyle.Fill
            });
        }

        public void DrawRoundRectStroke(SKRect rect, float rx, float ry, SKColor color, float strokeWidth, float opacity = 1f)
        {
            _commands.Add(new DrawRoundRectCommand
            {
                Rect = rect,
                Bounds = rect,
                RadiusX = rx,
                RadiusY = ry,
                Color = color,
                Opacity = opacity,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = strokeWidth
            });
        }

        public void DrawComplexRoundRect(SKRect rect, float[] radii, SKColor color, float opacity = 1f)
        {
            _commands.Add(new DrawComplexRoundRectCommand
            {
                Rect = rect,
                Bounds = rect,
                Radii = radii,
                Color = color,
                Opacity = opacity,
                Style = SKPaintStyle.Fill
            });
        }

        public void DrawComplexRoundRectStroke(SKRect rect, float[] radii, SKColor color, float strokeWidth, float opacity = 1f)
        {
            _commands.Add(new DrawComplexRoundRectCommand
            {
                Rect = rect,
                Bounds = rect,
                Radii = radii,
                Color = color,
                Opacity = opacity,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = strokeWidth
            });
        }

        public void DrawBoxShadow(SKRect box, float[] borderRadius, SKColor color, float offsetX, float offsetY, float blurRadius, float spreadRadius, float opacity = 1f)
        {
            var shadowRect = new SKRect(
                box.Left + offsetX - spreadRadius - blurRadius,
                box.Top + offsetY - spreadRadius - blurRadius,
                box.Right + offsetX + spreadRadius + blurRadius,
                box.Bottom + offsetY + spreadRadius + blurRadius
            );

            _commands.Add(new DrawBoxShadowCommand
            {
                Box = box,
                Bounds = shadowRect,
                BorderRadius = borderRadius,
                Color = color,
                OffsetX = offsetX,
                OffsetY = offsetY,
                BlurRadius = blurRadius,
                SpreadRadius = spreadRadius,
                Opacity = opacity
            });
        }

        public void DrawText(string text, float x, float y, SKColor color, float fontSize, SKTypeface typeface, float opacity = 1f)
        {
            if (string.IsNullOrEmpty(text)) return;
            
            // Approximate bounds (since we don't have the shaper/measure here)
            // Use 0.6 * fontSize as average char width
            float approxWidth = text.Length * fontSize * 0.6f;
            SKRect approxBounds = new SKRect(x, y - fontSize, x + approxWidth, y + (fontSize * 0.2f));

            _commands.Add(new DrawTextCommand
            {
                Text = text,
                Bounds = approxBounds,
                X = x,
                Y = y,
                Color = color,
                FontSize = fontSize,
                Typeface = typeface,
                Opacity = opacity
            });
        }

        public void DrawImage(SKBitmap bitmap, SKRect dest, SKRect? src = null, float opacity = 1f)
        {
            if (bitmap == null) return;

            _commands.Add(new DrawImageCommand
            {
                Bitmap = bitmap,
                Bounds = dest,
                DestRect = dest,
                SourceRect = src,
                Opacity = opacity
            });
        }

        public void PushClip(SKRect clipRect)
        {
            Save();
            _commands.Add(new ClipRectCommand
            {
                ClipRect = clipRect,
                Operation = SKClipOperation.Intersect
            });
        }
        
        public void PushClipRoundRect(SKRect clipRect, float rx, float ry)
        {
            Save();
            _commands.Add(new ClipRoundRectCommand
            {
                ClipRect = clipRect,
                RadiusX = rx,
                RadiusY = ry,
                Operation = SKClipOperation.Intersect
            });
        }

        public void PopClip()
        {
            Restore();
        }

        public void Save()
        {
            _commands.Add(new SaveCommand());
            _saveCount++;
        }

        public void SaveLayer(float opacity = 1f)
        {
            _commands.Add(new SaveLayerCommand { Opacity = opacity });
            _saveCount++;
        }
        
        public void SaveLayer(SKRect bounds, float opacity = 1f)
        {
            _commands.Add(new SaveLayerCommand { LayerBounds = bounds, Opacity = opacity });
            _saveCount++;
        }

        public void Restore()
        {
            if (_saveCount > 0)
            {
                _commands.Add(new RestoreCommand());
                _saveCount--;
            }
            // Else logging warning? Mismatched save/restore
        }

        public void Translate(float dx, float dy)
        {
            if (dx == 0 && dy == 0) return;
            _commands.Add(new TranslateCommand { Dx = dx, Dy = dy });
        }

        public void Concat(SKMatrix matrix)
        {
            if (matrix.IsIdentity) return;
            _commands.Add(new MatrixCommand { Matrix = matrix });
        }

        /// <summary>
        /// Directly add a custom render command.
        /// </summary>
        public void AddCommand(RenderCommand command)
        {
            if (command != null)
                _commands.Add(command);
        }
    }
}

