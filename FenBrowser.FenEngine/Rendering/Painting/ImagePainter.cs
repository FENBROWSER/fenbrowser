using FenBrowser.Core.Css;
using FenBrowser.Core.Dom.V2;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using FenBrowser.Core.Logging;
using SkiaSharp;

namespace FenBrowser.FenEngine.Rendering.Painting
{
    /// <summary>
    /// Paints images and SVG content with object-fit support. Cached native bitmaps
    /// are owned by this painter and disposed deterministically.
    /// </summary>
    public sealed class ImagePainter : IDisposable
    {
        private const int MaxCachedImages = 512;
        private const int MaxDataUriCharacters = 32 * 1024 * 1024;

        private readonly object _cacheLock = new();
        private readonly Dictionary<string, SKBitmap> _imageCache = new(StringComparer.Ordinal);
        private bool _disposed;

        public void PaintImage(SKCanvas canvas, Element element, SKRect box, CssComputed style, SKBitmap bitmap = null)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ArgumentNullException.ThrowIfNull(canvas);
            ArgumentNullException.ThrowIfNull(element);

            string src = element.GetAttribute("src") as string;
            if (bitmap != null)
            {
                PaintBitmap(canvas, element, box, style, bitmap);
                return;
            }

            if (string.IsNullOrEmpty(src))
            {
                PaintImagePlaceholder(canvas, box, element);
                return;
            }

            // Keep the cache lock through Skia's draw call. CacheImage/ClearCache own
            // and dispose cached SKBitmap objects, so returning a bare reference and
            // releasing the lock before painting allowed another thread to dispose the
            // native bitmap while DrawBitmap was still consuming it.
            lock (_cacheLock)
            {
                if (_disposed)
                    throw new ObjectDisposedException(nameof(ImagePainter));

                if (!_imageCache.TryGetValue(src, out var cached) || cached == null)
                {
                    PaintImagePlaceholder(canvas, box, element);
                    return;
                }

                PaintBitmap(canvas, element, box, style, cached);
            }
        }

        private static void PaintBitmap(SKCanvas canvas, Element element, SKRect box, CssComputed style, SKBitmap bitmap)
        {
            if (bitmap == null || bitmap.Width <= 0 || bitmap.Height <= 0 || box.Width <= 0 || box.Height <= 0)
            {
                PaintImagePlaceholder(canvas, box, element);
                return;
            }

            var destRect = CalculateDestRect(box, bitmap.Width, bitmap.Height, style);
            canvas.Save();
            try
            {
                canvas.ClipRect(box);
                canvas.DrawBitmap(bitmap, destRect, SKSamplingOptions.Default);
            }
            finally
            {
                canvas.Restore();
            }
        }

        private static SKRect CalculateDestRect(SKRect box, int imgWidth, int imgHeight, CssComputed style)
        {
            if (imgWidth <= 0 || imgHeight <= 0 || box.Width <= 0 || box.Height <= 0)
                return SKRect.Empty;

            var objectFit = style?.ObjectFit?.ToLowerInvariant() ?? "fill";

            float boxW = box.Width;
            float boxH = box.Height;
            float imgAspect = (float)imgWidth / imgHeight;
            float boxAspect = boxW / boxH;

            float destW, destH, destX, destY;

            switch (objectFit)
            {
                case "contain":
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
                    destW = imgWidth;
                    destH = imgHeight;
                    destX = box.Left + (boxW - destW) / 2;
                    destY = box.Top + (boxH - destH) / 2;
                    break;

                case "scale-down":
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
                    destX = box.Left;
                    destY = box.Top;
                    destW = boxW;
                    destH = boxH;
                    break;
            }

            // object-position applies to replaced content for contain/cover/none/
            // scale-down. The old cover exclusion forced every cropped image to center.
            if (!string.IsNullOrWhiteSpace(style?.ObjectPosition) && objectFit != "fill")
            {
                var parts = style.ObjectPosition.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                float posX = 0.5f;
                float posY = 0.5f;

                if (parts.Length > 0) posX = ParsePositionFraction(parts[0], 0.5f);
                if (parts.Length > 1) posY = ParsePositionFraction(parts[1], 0.5f);

                destX = box.Left + (boxW - destW) * posX;
                destY = box.Top + (boxH - destH) * posY;
            }

            return new SKRect(destX, destY, destX + destW, destY + destH);
        }

        private static float ParsePositionFraction(string part, float defaultValue)
        {
            if (string.Equals(part, "left", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(part, "top", StringComparison.OrdinalIgnoreCase)) return 0f;
            if (string.Equals(part, "center", StringComparison.OrdinalIgnoreCase)) return 0.5f;
            if (string.Equals(part, "right", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(part, "bottom", StringComparison.OrdinalIgnoreCase)) return 1f;

            if (part.EndsWith('%') &&
                float.TryParse(part.AsSpan(0, part.Length - 1), NumberStyles.Float, CultureInfo.InvariantCulture, out var pct))
            {
                return pct / 100f;
            }

            return defaultValue;
        }

        private static void PaintImagePlaceholder(SKCanvas canvas, SKRect box, Element element)
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

            string alt = element.GetAttribute("alt") as string ?? "[Image]";
            using var font = new SKFont(SKTypeface.Default, 12);
            using var textPaint = new SKPaint
            {
                Color = SKColors.Gray,
                IsAntialias = true
            };

            float textWidth = font.MeasureText(alt);
            float x = box.Left + (box.Width - textWidth) / 2;
            float y = box.Top + box.Height / 2 + 4;
            canvas.DrawText(alt, x, y, SKTextAlign.Left, font, textPaint);
        }

        public SKBitmap GetCachedImage(string url)
        {
            if (string.IsNullOrEmpty(url))
                return null;

            lock (_cacheLock)
            {
                if (_disposed)
                    return null;
                return _imageCache.TryGetValue(url, out var bitmap) ? bitmap : null;
            }
        }

        /// <summary>
        /// Transfers ownership of <paramref name="bitmap"/> to this cache.
        /// </summary>
        public void CacheImage(string url, SKBitmap bitmap)
        {
            if (string.IsNullOrEmpty(url))
                throw new ArgumentException("Image cache URL is required.", nameof(url));
            ArgumentNullException.ThrowIfNull(bitmap);

            lock (_cacheLock)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);

                if (_imageCache.TryGetValue(url, out var existing))
                {
                    if (ReferenceEquals(existing, bitmap))
                        return;
                    existing?.Dispose();
                    _imageCache[url] = bitmap;
                    return;
                }

                if (_imageCache.Count >= MaxCachedImages)
                {
                    // This helper has no request-cache metadata/LRU clock. Prefer a
                    // deterministic hard memory bound over unbounded native retention;
                    // the higher-level resource cache is responsible for durable reuse.
                    DisposeCacheLocked();
                }

                _imageCache.Add(url, bitmap);
            }
        }

        public SKBitmap LoadFromDataUri(string dataUri)
        {
            if (string.IsNullOrWhiteSpace(dataUri) || dataUri.Length > MaxDataUriCharacters)
                return null;
            if (!dataUri.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase))
                return null;

            try
            {
                int commaIndex = dataUri.IndexOf(',');
                if (commaIndex < 0)
                    return null;

                var metadata = dataUri.AsSpan(0, commaIndex);
                if (metadata.IndexOf(";base64".AsSpan(), StringComparison.OrdinalIgnoreCase) < 0)
                {
                    // Do not reinterpret a percent-encoded data URL as base64. The
                    // caller can route non-base64 data URLs through the canonical URL
                    // decoder instead of silently decoding the wrong bytes here.
                    return null;
                }

                string base64 = dataUri.Substring(commaIndex + 1);
                if (base64.Length > MaxDataUriCharacters)
                    return null;

                byte[] imageData = Convert.FromBase64String(base64);
                using var stream = new MemoryStream(imageData, writable: false);
                return SKBitmap.Decode(stream);
            }
            catch (FormatException)
            {
                return null;
            }
            catch (ArgumentException)
            {
                return null;
            }
        }

        public void ClearCache()
        {
            lock (_cacheLock)
            {
                if (_disposed)
                    return;
                DisposeCacheLocked();
            }
        }

        private void DisposeCacheLocked()
        {
            foreach (var bitmap in _imageCache.Values)
            {
                bitmap?.Dispose();
            }
            _imageCache.Clear();
        }

        public void Dispose()
        {
            lock (_cacheLock)
            {
                if (_disposed)
                    return;

                _disposed = true;
                DisposeCacheLocked();
            }
        }
    }
}
