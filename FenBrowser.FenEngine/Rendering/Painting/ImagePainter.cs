using FenBrowser.Core.Css;
using FenBrowser.Core.Dom.V2;
using System;
using System.Collections.Concurrent;
using System.IO;
using FenBrowser.Core;
using FenBrowser.Core.Logging;
using SkiaSharp;

namespace FenBrowser.FenEngine.Rendering.Painting
{
    /// <summary>
    /// Paints images and SVG content with object-fit support.
    /// </summary>
    public class ImagePainter
    {
        private readonly ConcurrentDictionary<string, SKBitmap> _imageCache;

        public ImagePainter()
        {
            _imageCache = new ConcurrentDictionary<string, SKBitmap>();
        }

        /// <summary>
        /// Paint an image element.
        /// </summary>
        public void PaintImage(SKCanvas canvas, Element element, SKRect box, CssComputed style, SKBitmap bitmap = null)
        {
            // Get image source
            string src = null;
            if (element.Attr != null && element.Attr.TryGetValue("src", out var srcAttr))
            {
                src = srcAttr;
            }
            if (bitmap == null && !string.IsNullOrEmpty(src))
            {
                bitmap = GetCachedImage(src);
            }

            if (bitmap == null)
            {
                // Draw placeholder
                PaintImagePlaceholder(canvas, box, element);
                return;
            }

            // Apply object-fit
            var destRect = CalculateDestRect(box, bitmap.Width, bitmap.Height, style);

            // Clip to box
            canvas.Save();
            canvas.ClipRect(box);

            canvas.DrawBitmap(bitmap, destRect);

            canvas.Restore();
        }

        /// <summary>
        /// Calculate destination rectangle based on object-fit.
        /// </summary>
        private SKRect CalculateDestRect(SKRect box, int imgWidth, int imgHeight, CssComputed style)
        {
            var objectFit = style?.ObjectFit?.ToLowerInvariant() ?? "fill";

            float boxW = box.Width;
            float boxH = box.Height;
            float imgAspect = (float)imgWidth / imgHeight;
            float boxAspect = boxW / boxH;

            float destW, destH, destX, destY;

            switch (objectFit)
            {
                case "contain":
                    // Fit entire image, maintain aspect ratio
                    if (imgAspect > boxAspect)
                    {
                        destW = boxW;
                        destH = boxW / imgAspect;
                    }
                    else
                    {
                        destH = boxH;
                        destW = boxH * imgAspect;
                    }
                    destX = box.Left + (boxW - destW) / 2;
                    destY = box.Top + (boxH - destH) / 2;
                    break;

                case "cover":
                    // Cover entire box, maintain aspect ratio, may crop
                    if (imgAspect > boxAspect)
                    {
                        destH = boxH;
                        destW = boxH * imgAspect;
                    }
                    else
                    {
                        destW = boxW;
                        destH = boxW / imgAspect;
                    }
                    destX = box.Left + (boxW - destW) / 2;
                    destY = box.Top + (boxH - destH) / 2;
                    break;

                case "none":
                    // Natural size, centered
                    destW = imgWidth;
                    destH = imgHeight;
                    destX = box.Left + (boxW - destW) / 2;
                    destY = box.Top + (boxH - destH) / 2;
                    break;

                case "scale-down":
                    // Like contain, but never upscale
                    if (imgWidth <= boxW && imgHeight <= boxH)
                    {
                        destW = imgWidth;
                        destH = imgHeight;
                    }
                    else if (imgAspect > boxAspect)
                    {
                        destW = boxW;
                        destH = boxW / imgAspect;
                    }
                    else
                    {
                        destH = boxH;
                        destW = boxH * imgAspect;
                    }
                    destX = box.Left + (boxW - destW) / 2;
                    destY = box.Top + (boxH - destH) / 2;
                    break;

                case "fill":
                default:
                    // Stretch to fill
                    destX = box.Left;
                    destY = box.Top;
                    destW = boxW;
                    destH = boxH;
                    break;
            }

            return new SKRect(destX, destY, destX + destW, destY + destH);
        }

        /// <summary>
        /// Paint placeholder when image is not available.
        /// </summary>
        private void PaintImagePlaceholder(SKCanvas canvas, SKRect box, Element element)
        {
            using var bgPaint = new SKPaint
            {
                Color = new SKColor(240, 240, 240),
                Style = SKPaintStyle.Fill
            };
            canvas.DrawRect(box, bgPaint);

            using var borderPaint = new SKPaint
            {
                Color = new SKColor(200, 200, 200),
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 1
            };
            canvas.DrawRect(box, borderPaint);

            // Draw alt text if available
            string alt = "[Image]";
            if (element.Attr != null && element.Attr.TryGetValue("alt", out var altText))
            {
                alt = altText;
            }
            using var textPaint = new SKPaint
            {
                Color = SKColors.Gray,
                TextSize = 12,
                IsAntialias = true
            };

            float textWidth = textPaint.MeasureText(alt);
            float x = box.Left + (box.Width - textWidth) / 2;
            float y = box.Top + box.Height / 2 + 4;
            canvas.DrawText(alt, x, y, textPaint);
        }

        /// <summary>
        /// Get cached image or null.
        /// </summary>
        public SKBitmap GetCachedImage(string url)
        {
            return _imageCache.TryGetValue(url, out var bitmap) ? bitmap : null;
        }

        /// <summary>
        /// Cache a loaded image.
        /// </summary>
        public void CacheImage(string url, SKBitmap bitmap)
        {
            _imageCache[url] = bitmap;
        }

        /// <summary>
        /// Load image from data URI.
        /// </summary>
        public SKBitmap LoadFromDataUri(string dataUri)
        {
            try
            {
                if (!dataUri.StartsWith("data:image")) return null;

                int commaIndex = dataUri.IndexOf(',');
                if (commaIndex < 0) return null;

                string base64 = dataUri.Substring(commaIndex + 1);
                byte[] imageData = Convert.FromBase64String(base64);

                using var stream = new MemoryStream(imageData);
                return SKBitmap.Decode(stream);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Clear image cache.
        /// </summary>
        public void ClearCache()
        {
            foreach (var kvp in _imageCache)
            {
                kvp.Value?.Dispose();
            }
            _imageCache.Clear();
        }
    }
}



