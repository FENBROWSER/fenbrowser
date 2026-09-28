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
    /// - Raster width and height are hard caps and are never rescaled to fit.
    ///   A document whose geometry is inside both caps but over the raster
    ///   pixel budget is instead rasterized into a proportionally smaller
    ///   surface that keeps at least half the natural size on each axis. The
    ///   reduction is reported, never hidden: SvgRenderResult keeps the natural
    ///   Width/Height and publishes SvgRenderResult.RasterScaleX/RasterScaleY
    ///   (plus SvgRenderResult.IsDownscaled and a bounded warning) so a caller
    ///   composites the smaller bitmap at the right size. Anything needing a
    ///   larger reduction fails closed with its own message.
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

        /// <summary>
        /// Smallest uniform scale applied to a document that only exceeds the
        /// raster pixel budget. The clamped surface keeps at least half the
        /// natural size on each axis, so a document is either rendered whole or
        /// reduced within a fixed bound, never shrunk into a token surface.
        /// </summary>
        internal const double MinRasterClampScale = 0.5d;

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
            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            SvgRenderResult result = RenderCore(request);
            EnforceCompleteRender(result);
            BoundResultDiagnostics(result);
            SvgDiagnostics.RecordRender(
                request, result, System.Diagnostics.Stopwatch.GetElapsedTime(started));
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
            result.RasterScaleX = 1f;
            result.RasterScaleY = 1f;
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

            var resources = new SvgRenderResources(limits);
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
                        out bool requiresFallback, out bool resourceRejected,
                        resources))
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
                    if (!RasterPlan.TryCreate(
                            rasterWidth, rasterHeight, limits,
                            out RasterPlan rasterPlan, out RasterRefusal refusal))
                    {
                        return new SvgRenderResult
                        {
                            Success = false,
                            ErrorMessage = DescribeRasterRefusal(
                                refusal, rasterWidth, rasterHeight, limits),
                            Warnings = warnings,
                            FallbackReasonCodes = fallbackReasonCodes,
                            ResourceRejectionReasonCodes = resourceRejectionReasonCodes,
                            Backend = SvgRendererBackend.FirstParty,
                            RequiresFallback = requiresFallback,
                            HadResourceRejection = resourceRejected
                        };
                    }

                    int bitmapWidth = rasterPlan.Width;
                    int bitmapHeight = rasterPlan.Height;

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

                    if (resources.IsExpired)
                    {
                        return DeadlineExceeded(
                            limits, svgContent.Length, warnings, fallbackReasonCodes,
                            resourceRejectionReasonCodes, requiresFallback, resourceRejected);
                    }

                    try
                    {
                        using (var canvas = new SKCanvas(bitmap))
                        {
                            canvas.Clear(SKColors.Transparent);
                            DrawRasterSurface(canvas, picture, rasterPlan, cullRect);
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

                    if (resources.IsExpired)
                    {
                        return DeadlineExceeded(
                            limits, svgContent.Length, warnings, fallbackReasonCodes,
                            resourceRejectionReasonCodes, requiresFallback, resourceRejected);
                    }

                    // Admitted renders are reported by SvgDiagnostics (verbose mode), so
                    // the render path never initializes logging on its own.
                    var result = new SvgRenderResult
                    {
                        Picture = null,
                        Bitmap = bitmap,
                        Width = cullRect.Width,
                        Height = cullRect.Height,
                        RasterScaleX = (float)rasterPlan.ScaleX,
                        RasterScaleY = (float)rasterPlan.ScaleY,
                        Success = true,
                        Warnings = rasterPlan.IsClamped
                            ? WithRasterClampWarning(
                                warnings, rasterWidth, rasterHeight, bitmapWidth, bitmapHeight)
                            : warnings,
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

                try
                {
                    resources.Dispose();
                }
                catch (System.Exception)
                {
                }
            }
        }

        private static SvgRenderResult DeadlineExceeded(
            SvgRenderLimits limits,
            int sourceChars,
            System.Collections.Generic.IReadOnlyList<string> warnings,
            System.Collections.Generic.IReadOnlyList<string> fallbackReasonCodes,
            System.Collections.Generic.IReadOnlyList<string> resourceRejectionReasonCodes,
            bool requiresFallback,
            bool resourceRejected) =>
            new SvgRenderResult
            {
                Success = false,
                ErrorMessage =
                    $"SVG render exceeded time limit ({limits.MaxRenderTimeMs}ms, size={sourceChars / 1024}KB)",
                Warnings = warnings,
                FallbackReasonCodes = fallbackReasonCodes,
                ResourceRejectionReasonCodes = resourceRejectionReasonCodes,
                Backend = SvgRendererBackend.FirstParty,
                RequiresFallback = requiresFallback,
                HadResourceRejection = resourceRejected
            };

        private static string DescribeRasterRefusal(
            RasterRefusal refusal,
            double rasterWidth,
            double rasterHeight,
            SvgRenderLimits limits)
        {
            if (refusal == RasterRefusal.ReductionTooLarge)
            {
                return
                    $"SVG raster bounds {rasterWidth:0}x{rasterHeight:0} are inside the raster size " +
                    $"caps ({limits.MaxRasterWidth}x{limits.MaxRasterHeight}) but cannot fit the " +
                    $"raster pixel budget ({limits.MaxRasterPixels} pixels) within the " +
                    $"{MinRasterClampScale:0.##}x minimum reduction";
            }

            return
                $"SVG raster bounds {rasterWidth:0}x{rasterHeight:0} exceed browser limits " +
                $"({limits.MaxRasterWidth}x{limits.MaxRasterHeight}, {limits.MaxRasterPixels} pixels)";
        }

        /// <summary>
        /// Paints a recorded picture into the admitted surface, mapping it as
        /// <c>p' = S * (p - cullRect.Origin)</c>. SKMatrix.CreateScaleTranslation
        /// composes as T*S, so the clamped translation is pre-multiplied by the
        /// scale; the unscaled -left/-top would shift the frame by
        /// <c>origin * (S - 1)</c>.
        /// </summary>
        internal static void DrawRasterSurface(
            SKCanvas canvas,
            SKPicture picture,
            RasterPlan plan,
            SKRect cullRect)
        {
            if (canvas == null || picture == null)
            {
                return;
            }

            if (!plan.IsClamped)
            {
                canvas.DrawPicture(picture);
                return;
            }

            SKMatrix matrix = CreateClampedDrawMatrix(plan, cullRect);
            canvas.DrawPicture(picture, in matrix);
        }

        internal static SKMatrix CreateClampedDrawMatrix(RasterPlan plan, SKRect cullRect) =>
            SKMatrix.CreateScaleTranslation(
                (float)plan.ScaleX,
                (float)plan.ScaleY,
                (float)(-cullRect.Left * plan.ScaleX),
                (float)(-cullRect.Top * plan.ScaleY));

        private static System.Collections.Generic.IReadOnlyList<string> WithRasterClampWarning(
            System.Collections.Generic.IReadOnlyList<string> warnings,
            double rasterWidth,
            double rasterHeight,
            int clampedWidth,
            int clampedHeight)
        {
            int retained = warnings == null ? 0 : warnings.Count;
            int keep = System.Math.Min(retained, MaxResultDiagnosticEntries - 1);
            var bounded = new System.Collections.Generic.List<string>(keep + 1);
            for (int i = 0; i < keep; i++)
            {
                bounded.Add(warnings[i]);
            }

            bounded.Add(
                $"SVG raster clamped to {clampedWidth}x{clampedHeight} pixels; natural bounds " +
                $"{rasterWidth:0}x{rasterHeight:0} exceed the raster pixel budget");
            return bounded;
        }

        /// <summary>Why <see cref="RasterPlan.TryCreate"/> refused a raster.</summary>
        internal enum RasterRefusal
        {
            /// <summary>Admitted.</summary>
            None = 0,

            /// <summary>
            /// The natural raster is outside <see cref="SvgRenderLimits.MaxRasterWidth"/>
            /// or <see cref="SvgRenderLimits.MaxRasterHeight"/>. Hard cap: never rescaled.
            /// </summary>
            PerAxisCap = 1,

            /// <summary>
            /// The natural raster is inside both per-axis caps but over
            /// <see cref="SvgRenderLimits.MaxRasterPixels"/>, and fitting it would
            /// need a reduction below <see cref="MinRasterClampScale"/>.
            /// </summary>
            ReductionTooLarge = 2
        }

        /// <summary>
        /// The admitted raster surface for one document. The per-axis caps are
        /// consulted before any reduction, so they are never met by rescaling.
        /// </summary>
        internal readonly struct RasterPlan
        {
            private RasterPlan(int width, int height, double scaleX, double scaleY, bool isClamped)
            {
                Width = width;
                Height = height;
                ScaleX = scaleX;
                ScaleY = scaleY;
                IsClamped = isClamped;
            }

            public int Width { get; }

            public int Height { get; }

            public double ScaleX { get; }

            public double ScaleY { get; }

            public bool IsClamped { get; }

            /// <summary>
            /// Resolves the natural raster against the caps. A plan only ever
            /// exists when the admitted integer surface satisfies every cap and
            /// keeps at least <see cref="MinRasterClampScale"/> on each axis, so
            /// the caller allocates exactly <see cref="Width"/> x
            /// <see cref="Height"/> pixels and never more than the pixel budget.
            /// </summary>
            public static bool TryCreate(
                double rasterWidth,
                double rasterHeight,
                SvgRenderLimits limits,
                out RasterPlan plan,
                out RasterRefusal refusal)
            {
                plan = default;
                refusal = RasterRefusal.None;

                if (!(rasterWidth >= 1d) || !(rasterHeight >= 1d))
                {
                    refusal = RasterRefusal.PerAxisCap;
                    return false;
                }

                if (rasterWidth > limits.MaxRasterWidth || rasterHeight > limits.MaxRasterHeight)
                {
                    refusal = RasterRefusal.PerAxisCap;
                    return false;
                }

                if (rasterWidth * rasterHeight <= limits.MaxRasterPixels)
                {
                    if (!TryAdmitSurface(
                            rasterWidth, rasterHeight, limits,
                            out int naturalWidth, out int naturalHeight))
                    {
                        refusal = RasterRefusal.PerAxisCap;
                        return false;
                    }

                    plan = new RasterPlan(naturalWidth, naturalHeight, 1d, 1d, false);
                    return true;
                }

                double scale = System.Math.Sqrt(limits.MaxRasterPixels / (rasterWidth * rasterHeight));
                if (!(scale >= MinRasterClampScale))
                {
                    refusal = RasterRefusal.ReductionTooLarge;
                    return false;
                }

                double clampedWidth = System.Math.Max(
                    System.Math.Floor(rasterWidth * scale),
                    System.Math.Ceiling(rasterWidth * MinRasterClampScale));
                double clampedHeight = System.Math.Max(
                    System.Math.Floor(rasterHeight * scale),
                    System.Math.Ceiling(rasterHeight * MinRasterClampScale));
                if (clampedWidth * clampedHeight > limits.MaxRasterPixels)
                {
                    clampedHeight = System.Math.Min(
                        clampedHeight,
                        System.Math.Floor(limits.MaxRasterPixels / clampedWidth));
                    clampedWidth = System.Math.Min(
                        clampedWidth,
                        System.Math.Floor(limits.MaxRasterPixels / clampedHeight));
                }

                if (!TryAdmitSurface(
                        clampedWidth, clampedHeight, limits,
                        out int width, out int height))
                {
                    refusal = RasterRefusal.ReductionTooLarge;
                    return false;
                }

                double scaleX = width / rasterWidth;
                double scaleY = height / rasterHeight;
                if (scaleX < MinRasterClampScale || scaleY < MinRasterClampScale)
                {
                    refusal = RasterRefusal.ReductionTooLarge;
                    return false;
                }

                plan = new RasterPlan(width, height, scaleX, scaleY, true);
                return true;
            }

            private static bool TryAdmitSurface(
                double width,
                double height,
                SvgRenderLimits limits,
                out int admittedWidth,
                out int admittedHeight)
            {
                admittedWidth = 0;
                admittedHeight = 0;
                if (double.IsNaN(width) || double.IsNaN(height) ||
                    !(width >= 1d) || !(height >= 1d) ||
                    width > int.MaxValue || height > int.MaxValue)
                {
                    return false;
                }

                int candidateWidth = (int)width;
                int candidateHeight = (int)height;
                if (candidateWidth > limits.MaxRasterWidth ||
                    candidateHeight > limits.MaxRasterHeight ||
                    (long)candidateWidth * candidateHeight > limits.MaxRasterPixels)
                {
                    return false;
                }

                admittedWidth = candidateWidth;
                admittedHeight = candidateHeight;
                return true;
            }
        }
    }
}
