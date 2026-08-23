using System.Diagnostics;
using SkiaSharp;
using FenBrowser.FenEngine.Svg;

namespace FenBrowser.FenEngine.Adapters
{
    /// <summary>
    /// First-party SVG renderer. Drop-in replacement for <see cref="SvgSkiaRenderer"/>
    /// behind the same <see cref="ISvgRenderer"/> seam (RULE 5).
    ///
    /// Contract parity with SvgSkiaRenderer:
    /// - Never throws; failures return Success=false + ErrorMessage.
    /// - Identical budget semantics and (byte-compatible) limit error messages:
    ///   source chars, element count, filter count, nesting depth, elapsed ms,
    ///   raster width/height/pixel caps before any bitmap allocation.
    /// - Bitmap is fully valid after return; ownership transfers to the caller.
    ///
    /// Improvements over the legacy adapter:
    /// - Picture remains valid after return (no SKSvg disposal hazard), so
    ///   ImageLoader's picture-fallback path is reliable again.
    /// - No regex passes over untrusted source; parsing is a single O(n) scan.
    /// - DOCTYPE/entity machinery does not exist, so XXE and entity-expansion
    ///   attacks are structurally impossible instead of stripped post-hoc.
    /// - use/gradient reference cycles are detected and bounded.
    /// </summary>
    public class FenSvgRenderer : ISvgRenderer
    {
        public SvgRenderResult Render(string svgContent)
        {
            return Render(svgContent, SvgRenderLimits.Default);
        }

        public SvgRenderResult Render(string svgContent, SvgRenderLimits limits)
        {
            if (string.IsNullOrWhiteSpace(svgContent))
            {
                return new SvgRenderResult
                {
                    Success = false,
                    ErrorMessage = "Empty SVG content"
                };
            }

            limits = NormalizeLimits(limits);

            // Source admission control (parity message).
            if (svgContent.Length > limits.MaxSourceChars)
            {
                return new SvgRenderResult
                {
                    Success = false,
                    ErrorMessage = $"SVG source length ({svgContent.Length}) exceeds limit ({limits.MaxSourceChars})"
                };
            }

            var stopwatch = Stopwatch.StartNew();

            try
            {
                if (!SvgRenderEngine.TryRender(
                        svgContent,
                        limits,
                        out var picture,
                        out float naturalWidth,
                        out float naturalHeight,
                        out string error, out int warningCount))
                {
                    return new SvgRenderResult
                    {
                        Success = false,
                        ErrorMessage = error ?? "Failed to parse SVG"
                    };
                }

                // S3/F3: the picture is an intermediate here. ImageLoader only
                // consumes Bitmap (and never disposes Picture), so returning it
                // would pin native memory until finalization. It is disposed at
                // every exit below; SvgRenderResult.Picture stays null by design.
                {
                    var cullRect = picture.CullRect;
                    if (!float.IsFinite(cullRect.Width) || !float.IsFinite(cullRect.Height) ||
                        !float.IsFinite(cullRect.Left) || !float.IsFinite(cullRect.Top))
                    {
                        picture.Dispose();
                        return new SvgRenderResult
                        {
                            Success = false,
                            ErrorMessage = "SVG produced non-finite raster bounds"
                        };
                    }

                    double rasterWidth = System.Math.Max(1d, System.Math.Ceiling(cullRect.Width));
                    double rasterHeight = System.Math.Max(1d, System.Math.Ceiling(cullRect.Height));
                    if (rasterWidth > limits.MaxRasterWidth || rasterHeight > limits.MaxRasterHeight ||
                        rasterWidth * rasterHeight > limits.MaxRasterPixels)
                    {
                        picture.Dispose();
                        return new SvgRenderResult
                        {
                            Success = false,
                            ErrorMessage =
                                $"SVG raster bounds {rasterWidth:0}x{rasterHeight:0} exceed browser limits " +
                                $"({limits.MaxRasterWidth}x{limits.MaxRasterHeight}, {limits.MaxRasterPixels} pixels)"
                        };
                    }

                    int bitmapWidth = checked((int)rasterWidth);
                    int bitmapHeight = checked((int)rasterHeight);

                    SKBitmap bitmap;
                    try
                    {
                        bitmap = new SKBitmap(bitmapWidth, bitmapHeight);
                    }
                    catch (System.OutOfMemoryException)
                    {
                        picture.Dispose();
                        return new SvgRenderResult
                        {
                            Success = false,
                            ErrorMessage = "SVG raster allocation refused (out of memory)"
                        };
                    }

                    // S4/F4: if rasterization throws, the bitmap must not leak.
                    try
                    {
                        using (var canvas = new SKCanvas(bitmap))
                        {
                            canvas.Clear(SKColors.Transparent);
                            // The picture is recorded with its viewport origin at
                            // (0,0), so no cull-rect translation is required.
                            canvas.DrawPicture(picture);
                        }
                    }
                    catch (System.Exception drawEx)
                    {
                        bitmap.Dispose();
                        picture.Dispose();
                        return new SvgRenderResult
                        {
                            Success = false,
                            ErrorMessage = $"SVG rasterization failed: {drawEx.Message}"
                        };
                    }

                    picture.Dispose();

                    FenBrowser.Core.EngineLogCompat.Debug(
                        $"[FenSvgRenderer] ok {bitmapWidth}x{bitmapHeight} " +
                        $"{stopwatch.ElapsedMilliseconds}ms warnings={warningCount}",
                        FenBrowser.Core.Logging.LogCategory.Rendering);

                    return new SvgRenderResult
                    {
                        Picture = null,
                        Bitmap = bitmap,
                        Width = cullRect.Width,
                        Height = cullRect.Height,
                        Success = true
                    };
                }
            }
            catch (System.Exception ex)
            {
                return new SvgRenderResult
                {
                    Success = false,
                    ErrorMessage = $"SVG render error: {ex.Message}"
                };
            }
        }

        private static SvgRenderLimits NormalizeLimits(SvgRenderLimits limits)
        {
            var defaults = SvgRenderLimits.Default;
            if (limits.MaxRecursionDepth <= 0) limits.MaxRecursionDepth = defaults.MaxRecursionDepth;
            if (limits.MaxFilterCount <= 0) limits.MaxFilterCount = defaults.MaxFilterCount;
            if (limits.MaxRenderTimeMs <= 0) limits.MaxRenderTimeMs = defaults.MaxRenderTimeMs;
            if (limits.MaxElementCount <= 0) limits.MaxElementCount = defaults.MaxElementCount;
            if (limits.MaxSourceChars <= 0) limits.MaxSourceChars = defaults.MaxSourceChars;
            if (limits.MaxRasterWidth <= 0) limits.MaxRasterWidth = defaults.MaxRasterWidth;
            if (limits.MaxRasterHeight <= 0) limits.MaxRasterHeight = defaults.MaxRasterHeight;
            if (limits.MaxRasterPixels <= 0) limits.MaxRasterPixels = defaults.MaxRasterPixels;
            if (limits.MaxDecodedImagePixels <= 0) limits.MaxDecodedImagePixels = defaults.MaxDecodedImagePixels;
            return limits;
        }
    }
}
