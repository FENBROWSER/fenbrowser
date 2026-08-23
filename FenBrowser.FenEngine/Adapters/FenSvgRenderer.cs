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
    /// - Native picture state is kept internal; only an independently owned
    ///   bitmap crosses the adapter boundary.
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
                    ErrorMessage = "Empty SVG content",
                    Backend = SvgRendererBackend.FirstParty
                };
            }

            limits = SvgRenderLimits.Normalize(limits);

            // Source admission control (parity message).
            if (svgContent.Length > limits.MaxSourceChars)
            {
                return new SvgRenderResult
                {
                    Success = false,
                    ErrorMessage = $"SVG source length ({svgContent.Length}) exceeds limit ({limits.MaxSourceChars})",
                    Backend = SvgRendererBackend.FirstParty
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
                        out string error, out var warnings, out bool requiresFallback))
                {
                    return new SvgRenderResult
                    {
                        Success = false,
                        ErrorMessage = error ?? "Failed to parse SVG",
                        Warnings = warnings,
                        Backend = SvgRendererBackend.FirstParty,
                        RequiresFallback = requiresFallback
                    };
                }

                // The picture is strictly an intermediate. A using boundary makes
                // every failure path deterministic, including unexpected native
                // allocation and logging failures.
                using (picture)
                {
                    var cullRect = picture.CullRect;
                    if (!float.IsFinite(cullRect.Width) || !float.IsFinite(cullRect.Height) ||
                        !float.IsFinite(cullRect.Left) || !float.IsFinite(cullRect.Top))
                    {
                        return new SvgRenderResult
                        {
                            Success = false,
                            ErrorMessage = "SVG produced non-finite raster bounds",
                            Warnings = warnings,
                            Backend = SvgRendererBackend.FirstParty,
                            RequiresFallback = requiresFallback
                        };
                    }

                    double rasterWidth = System.Math.Max(1d, System.Math.Ceiling(cullRect.Width));
                    double rasterHeight = System.Math.Max(1d, System.Math.Ceiling(cullRect.Height));
                    if (rasterWidth > limits.MaxRasterWidth || rasterHeight > limits.MaxRasterHeight ||
                        rasterWidth * rasterHeight > limits.MaxRasterPixels)
                    {
                        return new SvgRenderResult
                        {
                            Success = false,
                            ErrorMessage =
                                $"SVG raster bounds {rasterWidth:0}x{rasterHeight:0} exceed browser limits " +
                                $"({limits.MaxRasterWidth}x{limits.MaxRasterHeight}, {limits.MaxRasterPixels} pixels)",
                            Warnings = warnings,
                            Backend = SvgRendererBackend.FirstParty,
                            RequiresFallback = requiresFallback
                        };
                    }

                    int bitmapWidth = checked((int)rasterWidth);
                    int bitmapHeight = checked((int)rasterHeight);

                    var bitmap = new SKBitmap();
                    var bitmapInfo = new SKImageInfo(
                        bitmapWidth,
                        bitmapHeight,
                        SKColorType.Bgra8888,
                        SKAlphaType.Premul);
                    if (!bitmap.TryAllocPixels(bitmapInfo))
                    {
                        bitmap.Dispose();
                        return new SvgRenderResult
                        {
                            Success = false,
                            ErrorMessage = "SVG raster allocation refused",
                            Warnings = warnings,
                            Backend = SvgRendererBackend.FirstParty,
                            RequiresFallback = requiresFallback
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
                        return new SvgRenderResult
                        {
                            Success = false,
                            ErrorMessage = $"SVG rasterization failed: {drawEx.Message}",
                            Warnings = warnings,
                            Backend = SvgRendererBackend.FirstParty,
                            RequiresFallback = requiresFallback
                        };
                    }

                    FenBrowser.Core.EngineLogCompat.Debug(
                        $"[FenSvgRenderer] ok {bitmapWidth}x{bitmapHeight} " +
                        $"{stopwatch.ElapsedMilliseconds}ms warnings={warnings.Count}",
                        FenBrowser.Core.Logging.LogCategory.Rendering);

                    return new SvgRenderResult
                    {
                        Picture = null,
                        Bitmap = bitmap,
                        Width = cullRect.Width,
                        Height = cullRect.Height,
                        Success = true,
                        Warnings = warnings,
                        Backend = SvgRendererBackend.FirstParty,
                        RequiresFallback = requiresFallback
                    };
                }
            }
            catch (System.Exception ex)
            {
                return new SvgRenderResult
                {
                    Success = false,
                    ErrorMessage = $"SVG render error: {ex.Message}",
                    Backend = SvgRendererBackend.FirstParty
                };
            }
        }

    }
}
