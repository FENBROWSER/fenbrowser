using System.Diagnostics;
using SkiaSharp;
using FenBrowser.FenEngine.Svg;

namespace FenBrowser.FenEngine.Adapters
{
    /// <summary>
    /// First-party SVG renderer: the only SVG backend in the product, behind the
    /// same <see cref="ISvgRenderer"/> seam (RULE 5).
    ///
    /// Renderer contract:
    /// - Never throws; failures return Success=false + ErrorMessage.
    /// - Identical budget semantics and (byte-compatible) limit error messages:
    ///   source chars, element count, filter count, nesting depth, elapsed ms,
    ///   raster width/height/pixel caps before any bitmap allocation.
    /// - Bitmap is fully valid after return; ownership transfers to the caller.
    ///
    /// Complete-renderer contract: a Success=true result is authoritative and
    /// complete. When the engine reports RequiresFallback, a resource rejection,
    /// or an unresolved resource, the result is failed closed - Success=false
    /// with no Bitmap and no Picture - so this backend can never hand out
    /// partial pixels. Routing signals are preserved so callers can explain the
    /// rejection through SvgRenderResult.DescribeRejection.
    ///
    /// Structural guarantees:
    /// - Native picture state is kept internal; only an independently owned
    ///   bitmap crosses the adapter boundary.
    /// - No regex passes over untrusted source; parsing is a single O(n) scan.
    /// - DOCTYPE/entity machinery does not exist, so XXE and entity-expansion
    ///   attacks are structurally impossible instead of stripped post-hoc.
    /// - use/gradient reference cycles are detected and bounded.
    /// </summary>
    public class FenSvgRenderer : ISvgRenderer
    {
        internal const int MaxResultDiagnosticChars = 256;
        internal const int MaxResultDiagnosticEntries = 32;

        public SvgRenderResult Render(string svgContent)
        {
            return Render(svgContent, SvgRenderLimits.Default);
        }

        public SvgRenderResult Render(string svgContent, SvgRenderLimits limits)
        {
            return Render(new SvgRenderRequest(svgContent, limits));
        }

        public SvgRenderResult Render(SvgRenderRequest request)
        {
            SvgRenderResult result = RenderCore(request);
            EnforceCompleteRender(result);
            BoundResultDiagnostics(result);
            return result;
        }

        private static void EnforceCompleteRender(SvgRenderResult result)
        {
            if (result == null || SvgRenderResult.IsAdmissible(result))
            {
                return;
            }

            string reason = SvgRenderResult.DescribeRejection(result);
            result.Dispose();
            result.Success = false;
            result.Width = 0f;
            result.Height = 0f;
            result.ErrorMessage = string.IsNullOrWhiteSpace(reason)
                ? "SVG render is incomplete and was rejected"
                : reason;
        }

        private static bool HasReasonCodes(IReadOnlyList<string> codes) =>
            codes != null && codes.Count > 0;

        private static SvgRenderResult RejectIncompleteRender(
            bool requiresFallback,
            bool resourceRejected,
            IReadOnlyList<string> warnings,
            IReadOnlyList<string> fallbackReasonCodes,
            IReadOnlyList<string> resourceRejectionReasonCodes)
        {
            if (!requiresFallback && !resourceRejected &&
                !HasReasonCodes(fallbackReasonCodes) &&
                !HasReasonCodes(resourceRejectionReasonCodes))
            {
                return null;
            }

            var rejected = new SvgRenderResult
            {
                Success = false,
                Warnings = warnings,
                FallbackReasonCodes = fallbackReasonCodes,
                ResourceRejectionReasonCodes = resourceRejectionReasonCodes,
                Backend = SvgRendererBackend.FirstParty,
                RequiresFallback = requiresFallback,
                HadResourceRejection = resourceRejected
            };
            string reason = SvgRenderResult.DescribeRejection(rejected);
            rejected.ErrorMessage = string.IsNullOrWhiteSpace(reason)
                ? "SVG render is incomplete and was rejected"
                : reason;
            return rejected;
        }

        private static void BoundResultDiagnostics(SvgRenderResult result)
        {
            if (result == null)
            {
                return;
            }

            if (!string.IsNullOrEmpty(result.ErrorMessage))
            {
                result.ErrorMessage = SvgDiagnosticText.Bounded(result.ErrorMessage, MaxResultDiagnosticChars);
            }

            result.Warnings = BoundResultDiagnosticList(result.Warnings);
            result.FallbackReasonCodes = BoundResultDiagnosticList(result.FallbackReasonCodes);
            result.ResourceRejectionReasonCodes = BoundResultDiagnosticList(result.ResourceRejectionReasonCodes);
        }

        private static IReadOnlyList<string> BoundResultDiagnosticList(IReadOnlyList<string> values)
        {
            if (values == null || values.Count == 0)
            {
                return System.Array.Empty<string>();
            }

            int count = System.Math.Min(values.Count, MaxResultDiagnosticEntries);
            var bounded = new string[count];
            for (int i = 0; i < count; i++)
            {
                bounded[i] = SvgDiagnosticText.Bounded(values[i], MaxResultDiagnosticChars);
            }
            return bounded;
        }

        private SvgRenderResult RenderCore(SvgRenderRequest request)
        {
            if (request == null)
                return new SvgRenderResult { Success = false, ErrorMessage = "SVG render request is null", Backend = SvgRendererBackend.FirstParty };
            string svgContent = request.Content;
            SvgRenderLimits limits = request.Limits;
            if (string.IsNullOrWhiteSpace(svgContent))
            {
                return new SvgRenderResult
                {
                    Success = false,
                    ErrorMessage = "Empty SVG content",
                    Backend = SvgRendererBackend.FirstParty
                };
            }
            if (!double.IsFinite(request.DocumentTimeSeconds) ||
                request.DocumentTimeSeconds < 0d || request.DocumentTimeSeconds > 1_000_000_000d)
            {
                return new SvgRenderResult
                {
                    Success = false,
                    ErrorMessage = "SVG document time is outside the supported finite range",
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
            SKPicture picture = null;
            SKBitmap bitmap = null;

            try
            {
                if (!SvgRenderEngine.TryRender(
                        svgContent,
                        limits,
                        request.BaseUri,
                        request.ResourceResolver,
                        request.DocumentTimeSeconds,
                        out picture,
                        out float naturalWidth,
                        out float naturalHeight,
                        out string error, out var warnings,
                        out var fallbackReasonCodes, out var resourceRejectionReasonCodes,
                        out bool requiresFallback, out bool resourceRejected))
                {
                    return new SvgRenderResult
                    {
                        Success = false,
                        ErrorMessage = error ?? "Failed to parse SVG",
                        Warnings = warnings,
                        FallbackReasonCodes = fallbackReasonCodes,
                        ResourceRejectionReasonCodes = resourceRejectionReasonCodes,
                        Backend = SvgRendererBackend.FirstParty,
                        RequiresFallback = requiresFallback,
                        HadResourceRejection = resourceRejected
                    };
                }

                SvgRenderResult incomplete = RejectIncompleteRender(
                    requiresFallback, resourceRejected, warnings,
                    fallbackReasonCodes, resourceRejectionReasonCodes);
                if (incomplete != null)
                {
                    return incomplete;
                }

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
                            FallbackReasonCodes = fallbackReasonCodes,
                            ResourceRejectionReasonCodes = resourceRejectionReasonCodes,
                            Backend = SvgRendererBackend.FirstParty,
                            RequiresFallback = requiresFallback,
                            HadResourceRejection = resourceRejected
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
                            FallbackReasonCodes = fallbackReasonCodes,
                            ResourceRejectionReasonCodes = resourceRejectionReasonCodes,
                            Backend = SvgRendererBackend.FirstParty,
                            RequiresFallback = requiresFallback,
                            HadResourceRejection = resourceRejected
                        };
                    }

                    int bitmapWidth = checked((int)rasterWidth);
                    int bitmapHeight = checked((int)rasterHeight);

                    bitmap = new SKBitmap();
                    var bitmapInfo = new SKImageInfo(
                        bitmapWidth,
                        bitmapHeight,
                        SKColorType.Bgra8888,
                        SKAlphaType.Premul);
                    if (!bitmap.TryAllocPixels(bitmapInfo))
                    {
                        return new SvgRenderResult
                        {
                            Success = false,
                            ErrorMessage = "SVG raster allocation refused",
                            Warnings = warnings,
                            FallbackReasonCodes = fallbackReasonCodes,
                            ResourceRejectionReasonCodes = resourceRejectionReasonCodes,
                            Backend = SvgRendererBackend.FirstParty,
                            RequiresFallback = requiresFallback,
                            HadResourceRejection = resourceRejected
                        };
                    }

                    try
                    {
                        using (var canvas = new SKCanvas(bitmap))
                        {
                            canvas.Clear(SKColors.Transparent);
                            canvas.DrawPicture(picture);
                        }
                    }
                    catch (System.Exception drawEx)
                    {
                        return new SvgRenderResult
                        {
                            Success = false,
                            ErrorMessage = $"SVG rasterization failed: {drawEx.Message}",
                            Warnings = warnings,
                            FallbackReasonCodes = fallbackReasonCodes,
                            ResourceRejectionReasonCodes = resourceRejectionReasonCodes,
                            Backend = SvgRendererBackend.FirstParty,
                            RequiresFallback = requiresFallback,
                            HadResourceRejection = resourceRejected
                        };
                    }

                    FenBrowser.Core.EngineLogCompat.Debug(
                        $"[FenSvgRenderer] ok {bitmapWidth}x{bitmapHeight} " +
                        $"{stopwatch.ElapsedMilliseconds}ms warnings={warnings.Count}",
                        FenBrowser.Core.Logging.LogCategory.Rendering);

                    var result = new SvgRenderResult
                    {
                        Picture = null,
                        Bitmap = bitmap,
                        Width = cullRect.Width,
                        Height = cullRect.Height,
                        Success = true,
                        Warnings = warnings,
                        FallbackReasonCodes = fallbackReasonCodes,
                        ResourceRejectionReasonCodes = resourceRejectionReasonCodes,
                        Backend = SvgRendererBackend.FirstParty,
                        RequiresFallback = requiresFallback,
                        HadResourceRejection = resourceRejected
                    };
                    bitmap = null;
                    return result;
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
            finally
            {
                try
                {
                    bitmap?.Dispose();
                }
                catch (System.Exception)
                {
                }

                try
                {
                    picture?.Dispose();
                }
                catch (System.Exception)
                {
                }
            }
        }

    }
}
