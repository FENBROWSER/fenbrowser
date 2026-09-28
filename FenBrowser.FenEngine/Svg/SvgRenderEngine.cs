using System.Collections.Generic;
using System.Diagnostics;
using SkiaSharp;
using FenBrowser.FenEngine.Adapters;

namespace FenBrowser.FenEngine.Svg
{
    /// <summary>
    /// First-party SVG rendering pipeline: parsed tree -> styled draw -> SKPicture.
    ///
    /// Stage order follows engine convention:
    /// 1. source/DOM (sandboxed parser)
    /// 2. geometry (viewport, viewBox, transforms, shapes, paths)
    /// 3. paint (fills, strokes, gradients, group opacity)
    /// 4. raster preparation happens in <see cref="Adapters.FenSvgRenderer"/>.
    ///
    /// Threading: one engine instance PER RENDER CALL. Nothing static is mutated,
    /// so concurrent renders are safe.
    /// </summary>
    internal sealed partial class SvgRenderEngine
    {
        private readonly SvgParsedDocument _doc;
        private readonly SvgParseReport _report;
        private readonly SvgRenderLimits _limits;
        private readonly SvgRenderResources _resources;
        private readonly int _resourceDepth;
        private readonly System.Uri _baseUri;
        private readonly ISvgResourceResolver _resourceResolver;
        private readonly double _documentTimeSeconds;
        private readonly int _maxActiveLayers;
        private readonly int _maxReferenceDepth;
        private int _elementsVisited;

        /// <summary>Independent render-side recursion guard (S2/F2): spans the
        /// element tree AND use/symbol expansion, which multiply parser depth.</summary>
        private int _depth;

        /// <summary>Live SaveLayer count (S1/F1): each layer allocates a
        /// full-viewport surface; unbounded nesting is a memory bomb that the
        /// raster caps cannot see.</summary>
        private int _activeLayers;

        /// <summary>Per-render paint-server memoization (P0.3): stops arrays and
        /// userSpace shaders are rebuilt once per (server, currentColor) instead
        /// of once per referencing shape. Lifetime == this engine instance.</summary>
        internal Dictionary<SvgElement, CachedGradient> ShaderCache =
            new Dictionary<SvgElement, CachedGradient>();

        private const int TimeCheckMask = 0x3F; // check every 64 elements
        private const float DefaultFontSize = 16f;
        private const int MaxRenderDepth = 256;   // independent of parser budget

        private SvgRenderEngine(
            SvgParsedDocument doc,
            SvgRenderLimits limits,
            SvgRenderResources resources,
            int resourceDepth,
            System.Uri baseUri,
            ISvgResourceResolver resourceResolver,
            double documentTimeSeconds)
        {
            _doc = doc;
            _report = doc.Report;
            _limits = limits;
            _resources = resources;
            _resourceDepth = resourceDepth;
            _baseUri = baseUri;
            _resourceResolver = resourceResolver;
            _documentTimeSeconds = documentTimeSeconds;
            _maxActiveLayers = limits.MaxActiveLayers;
            _maxReferenceDepth = limits.MaxReferenceDepth;
        }

        public static bool TryRender(
            string source,
            SvgRenderLimits limits,
            System.Uri baseUri,
            ISvgResourceResolver resourceResolver,
            double documentTimeSeconds,
            out SKPicture picture,
            out float width,
            out float height,
            out string error,
            out IReadOnlyList<string> warnings,
            out IReadOnlyList<string> fallbackReasonCodes,
            out IReadOnlyList<string> resourceRejectionReasonCodes,
            out bool requiresFallback,
            out bool resourceRejected)
        {
            using var resources = new SvgRenderResources(limits);
            return TryRender(
                source, limits, baseUri, resourceResolver, documentTimeSeconds,
                out picture, out width, out height, out error, out warnings,
                out fallbackReasonCodes, out resourceRejectionReasonCodes,
                out requiresFallback, out resourceRejected, resources);
        }

        internal static bool TryRender(
            string source,
            SvgRenderLimits limits,
            System.Uri baseUri,
            ISvgResourceResolver resourceResolver,
            double documentTimeSeconds,
            out SKPicture picture,
            out float width,
            out float height,
            out string error,
            out IReadOnlyList<string> warnings,
            out IReadOnlyList<string> fallbackReasonCodes,
            out IReadOnlyList<string> resourceRejectionReasonCodes,
            out bool requiresFallback,
            out bool resourceRejected,
            SvgRenderResources resources)
        {
            picture = null;
            width = 0f;
            height = 0f;
            error = null;
            warnings = System.Array.Empty<string>();
            fallbackReasonCodes = System.Array.Empty<string>();
            resourceRejectionReasonCodes = System.Array.Empty<string>();
            requiresFallback = false;
            resourceRejected = false;

            if (resources == null)
            {
                using var owned = new SvgRenderResources(limits);
                return TryRender(
                    source, limits, baseUri, resourceResolver, documentTimeSeconds,
                    out picture, out width, out height, out error, out warnings,
                    out fallbackReasonCodes, out resourceRejectionReasonCodes,
                    out requiresFallback, out resourceRejected, owned);
            }

            return TryRenderInternal(
                source, limits, resources, 0, baseUri, resourceResolver,
                documentTimeSeconds,
                out picture, out width, out height, out _, out error, out warnings,
                out fallbackReasonCodes, out resourceRejectionReasonCodes,
                out requiresFallback, out resourceRejected);
        }

        private static bool TryRenderInternal(
            string source,
            SvgRenderLimits limits,
            SvgRenderResources resources,
            int resourceDepth,
            System.Uri baseUri,
            ISvgResourceResolver resourceResolver,
            double documentTimeSeconds,
            out SKPicture picture,
            out float width,
            out float height,
            out string rootPreserveAspectRatio,
            out string error,
            out IReadOnlyList<string> warnings,
            out IReadOnlyList<string> fallbackReasonCodes,
            out IReadOnlyList<string> resourceRejectionReasonCodes,
            out bool requiresFallback,
            out bool resourceRejected)
        {
            picture = null;
            width = 0f;
            height = 0f;
            rootPreserveAspectRatio = null;
            error = null;
            warnings = System.Array.Empty<string>();
            fallbackReasonCodes = System.Array.Empty<string>();
            resourceRejectionReasonCodes = System.Array.Empty<string>();
            requiresFallback = false;
            resourceRejected = false;

            if (!SvgMarkupParser.TryParse(source, limits, out var doc, out var fatalReason))
            {
                error = fatalReason;
                return false;
            }
            rootPreserveAspectRatio = doc.Root.GetAttribute("preserveAspectRatio");

            var engine = new SvgRenderEngine(
                doc, limits, resources, resourceDepth, baseUri, resourceResolver,
                documentTimeSeconds);
            try
            {
                engine.ApplySmilSnapshot(doc.Root);
                engine.RenderRoot(out picture, out width, out height);
                warnings = engine._report.Warnings.ToArray();
                fallbackReasonCodes = engine._report.FallbackReasonCodes.ToArray();
                resourceRejectionReasonCodes = engine._report.ResourceRejectionReasonCodes.ToArray();
                requiresFallback = engine._report.UnsupportedFeatureIgnored;
                resourceRejected = engine._report.ResourceRejected;
                return true;
            }
            catch (SvgTimeBudgetExceededException)
            {
                picture = null;
                warnings = engine._report.Warnings.ToArray();
                fallbackReasonCodes = engine._report.FallbackReasonCodes.ToArray();
                resourceRejectionReasonCodes = engine._report.ResourceRejectionReasonCodes.ToArray();
                requiresFallback = engine._report.UnsupportedFeatureIgnored;
                resourceRejected = engine._report.ResourceRejected;
                error = $"SVG render exceeded time limit ({limits.MaxRenderTimeMs}ms, size={source.Length / 1024}KB)";
                return false;
            }
            catch (SvgSandboxViolationException ex)
            {
                // Budget violations surface with their bare parity message, not
                // the generic "SVG render error:" prefix (F10).
                picture = null;
                warnings = engine._report.Warnings.ToArray();
                fallbackReasonCodes = engine._report.FallbackReasonCodes.ToArray();
                resourceRejectionReasonCodes = engine._report.ResourceRejectionReasonCodes.ToArray();
                requiresFallback = engine._report.UnsupportedFeatureIgnored;
                resourceRejected = engine._report.ResourceRejected;
                error = ex.Message;
                return false;
            }
        }

        private void CheckTime()
        {
            if ((_elementsVisited & TimeCheckMask) == 0 && _resources.IsExpired)
            {
                throw new SvgTimeBudgetExceededException();
            }
        }

        private void CheckDeadline()
        {
            if (_resources.IsExpired)
            {
                throw new SvgTimeBudgetExceededException();
            }
        }

        // -------------------------------------------------------------- viewport

        private void ResolveRootCssViewportSize(SvgElement root, ref float width, ref float height)
        {
            if (root.GetCascadedPresentationProperty("width") == null &&
                root.GetCascadedPresentationProperty("height") == null)
                return;
            ResolveGeometryFontContext(root, out float fontSize, out float rootFontSize);
            var viewport = new ViewportContext(width, height);
            if (TryResolveGeometryLength(
                    root, "width", viewport, width, fontSize, rootFontSize, out float cssWidth))
            {
                if (cssWidth >= 0f) width = cssWidth;
            }
            else
                RejectsUnresolvedGeometrySizing(
                    "root <svg>", "width", root.GetPresentationProperty("width"));
            if (TryResolveGeometryLength(
                    root, "height", viewport, height, fontSize, rootFontSize, out float cssHeight))
            {
                if (cssHeight >= 0f) height = cssHeight;
            }
            else
                RejectsUnresolvedGeometrySizing(
                    "root <svg>", "height", root.GetPresentationProperty("height"));
        }

        private static bool IsSubstitutableRootViewportLength(string value) =>
            !IsSpecifiedViewportLength(value) ||
            SvgValues.TryParseLength(value.AsSpan(), out _, out _);

        private bool TryResolveRootViewportLength(
            string raw,
            string property,
            out float value)
        {
            if (TryResolveViewportLength(raw, 0f, out value)) return true;
            if (string.IsNullOrWhiteSpace(raw) ||
                raw.Length > SvgMarkupParser.MaxAttributeValueChars ||
                IsSubstitutableRootViewportLength(raw) ||
                SvgCssLengthEvaluator.HasViewportUnitDimension(raw))
                return false;
            RejectsIntrinsicReplacedSizing("root <svg>", property, raw);
            return false;
        }

        private void RenderRoot(out SKPicture picture, out float width, out float height)
        {
            var root = _doc.Root;

            string rootWidth = root.GetAttribute("width");
            string rootHeight = root.GetAttribute("height");
            bool hasRootWidth = TryResolveRootViewportLength(rootWidth, "width", out width);
            bool hasRootHeight = TryResolveRootViewportLength(rootHeight, "height", out height);
            if (!hasRootWidth) width = 0f;
            if (!hasRootHeight) height = 0f;

            bool hasViewBox = TryParseViewBox(
                root.GetAttribute("viewBox"),
                out float vbX, out float vbY, out float vbW, out float vbH,
                out bool viewBoxDisablesRendering);

            if (!hasRootWidth && hasViewBox) width = vbW;
            if (!hasRootHeight && hasViewBox) height = vbH;
            if (!hasRootWidth && !hasViewBox) width = 300f;
            if (!hasRootHeight && !hasViewBox) height = 150f;
            if (!SvgValues.IsFinite(width) || width < 0f) width = hasRootWidth ? width : 300f;
            if (!SvgValues.IsFinite(height) || height < 0f) height = hasRootHeight ? height : 150f;

            // Absolute upper bound independent of caller limits: a viewport this
            // large can never pass raster admission below anyway.
            width = System.Math.Min(width, 32767f);
            height = System.Math.Min(height, 32767f);

            // Apply author CSS after the intrinsic viewport is known so media
            // queries and percentage-aware values see the replaced-element size.
            _report.TextAreasOnOneLine = _limits.LayOutTextAreasOnOneLine;
            SvgCssCascade.Apply(_doc, width, _report, CheckDeadline);
            ResolveRootCssViewportSize(root, ref width, ref height);
            width = System.Math.Min(width, 32767f);
            height = System.Math.Min(height, 32767f);

            using var recorder = new SKPictureRecorder();
            var canvas = recorder.BeginRecording(new SKRect(0f, 0f, width, height));

            var viewport = new ViewportContext(width, height);
            var inherited = new InheritedStyle();

            // The root <svg> participates in inheritance (e.g. fill="blue" on the
            // root cascades to children) - legacy adapter behavior preserved.
            var rootStyle = inherited.ResolveOverrides(root, _report);

            using (new CanvasState(canvas))
            {
                // Clip FIRST, under the identity CTM, so the clip is the raw
                // device viewport. Clipping after the viewBox transform would
                // transform the clip rectangle too (e.g. a large-negative
                // vbY would push the clip entirely off-content).
                canvas.ClipRect(new SKRect(0f, 0f, width, height));
                if (viewBoxDisablesRendering)
                {
                    picture = recorder.EndRecording();
                    return;
                }
                if (IsDisplayNone(root) || !rootStyle.Visibility)
                {
                    picture = recorder.EndRecording();
                    return;
                }
                ReportUnsupportedMaskHref(root);
                if (!ApplyViewportTransform(canvas, viewport, hasViewBox, vbX, vbY, vbW, vbH, root.GetAttribute("preserveAspectRatio")))
                {
                    picture = recorder.EndRecording();
                    return;
                }
                ApplyCssZoom(root, canvas);
                var userViewport = hasViewBox
                    ? new ViewportContext(vbW, vbH)
                    : viewport;
                if (width <= 0f || height <= 0f)
                {
                    picture = recorder.EndRecording();
                    return;
                }
                ApplyClipPath(root, canvas, userViewport, inherited);
                DrawWithEffects(root, canvas, userViewport, () =>
                {
                    bool layered = TryBeginGroupOpacity(root, canvas, out var layerPaint);
                    try
                    {
                        DrawChildren(root, canvas, userViewport, rootStyle);
                    }
                    finally
                    {
                        if (layered)
                        {
                            canvas.Restore();
                            _activeLayers--;
                            layerPaint.Dispose();
                        }
                    }
                });
            }

            picture = recorder.EndRecording();
        }

    }

    internal sealed class SvgRenderResources : System.IDisposable
    {
        private readonly long _maxCumulativeBytes;
        private readonly int _maxResourceCount;
        private readonly long _maxCumulativeDecodedPixels;
        private readonly Dictionary<string, SKImage> _decodedImages =
            new Dictionary<string, SKImage>(System.StringComparer.Ordinal);
        private long _cumulativeBytes;
        private long _cumulativeDecodedPixels;
        private int _resourceCount;
        private readonly long _maxFilterWorkUnits;
        private long _filterWorkUnits;
        private bool _disposed;

        public SvgRenderResources(SvgRenderLimits limits, Stopwatch clock = null)
        {
            _maxCumulativeBytes = System.Math.Max(1, limits.MaxCumulativeResourceBytes);
            _maxResourceCount = System.Math.Max(1, limits.MaxResourceCount);
            _maxCumulativeDecodedPixels = System.Math.Max(1, limits.MaxCumulativeDecodedImagePixels);
            MaxDecodedImageBytes = System.Math.Max(1, limits.MaxDecodedImageBytes);
            MaxDecodedImagePixels = System.Math.Max(1, limits.MaxDecodedImagePixels);
            MaxRasterDimension = System.Math.Max(1, limits.MaxRasterWidth);
            _maxFilterWorkUnits = limits.MaxFilterWorkUnits > 0
                ? limits.MaxFilterWorkUnits
                : SvgRenderLimits.Default.MaxFilterWorkUnits;
            DeadlineMs = limits.MaxRenderTimeMs > 0 ? limits.MaxRenderTimeMs : long.MaxValue;
            Clock = clock ?? Stopwatch.StartNew();
        }

        public Stopwatch Clock { get; }
        public long DeadlineMs { get; }
        public int MaxDecodedImageBytes { get; }
        public long MaxDecodedImagePixels { get; }
        public int MaxRasterDimension { get; }

        public long ElapsedMs => Clock.ElapsedMilliseconds;

        public bool IsExpired => ElapsedMs > DeadlineMs;

        public int DecodedImageCount => _decodedImages.Count;

        public long CumulativeDecodedPixels => _cumulativeDecodedPixels;

        public long CumulativeResourceBytes => _cumulativeBytes;

        public int ResourceCount => _resourceCount;

        public long FilterWorkUnits => _filterWorkUnits;

        public long MaxFilterWorkUnits => _maxFilterWorkUnits;

        /// <summary>
        /// Charges estimated filter work against the per-render budget shared with
        /// nested documents. Returns false, charging nothing, when it would exceed it.
        /// </summary>
        public bool TryChargeFilterWork(long units)
        {
            if (units < 0 || _disposed) return false;
            if (units > _maxFilterWorkUnits - _filterWorkUnits) return false;
            _filterWorkUnits += units;
            return true;
        }

        public bool TryAdmit(int bytes)
        {
            if (bytes < 0 || _disposed || _resourceCount >= _maxResourceCount ||
                _cumulativeBytes + bytes > _maxCumulativeBytes) return false;
            _cumulativeBytes += bytes;
            _resourceCount++;
            return true;
        }

        public bool TryAdmitDecodedPixels(long pixels)
        {
            if (pixels <= 0 || _disposed ||
                _cumulativeDecodedPixels + pixels > _maxCumulativeDecodedPixels) return false;
            _cumulativeDecodedPixels += pixels;
            return true;
        }

        public bool TryBorrowDecodedImage(
            byte[] payload,
            out SKImage image,
            out string key)
        {
            key = null;
            image = null;
            if (_disposed || payload == null || payload.Length == 0 ||
                _decodedImages.Count == 0) return false;
            key = ImageKey(payload);
            return _decodedImages.TryGetValue(key, out image);
        }

        public void RetainDecodedImage(byte[] payload, SKImage image)
        {
            if (image == null || _disposed) return;
            _decodedImages[ImageKey(payload)] = image;
        }

        private static string ImageKey(byte[] payload) =>
            System.Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(payload));

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var entry in _decodedImages)
            {
                try
                {
                    entry.Value?.Dispose();
                }
                catch (System.Exception)
                {
                }
            }
            _decodedImages.Clear();
        }
    }

    internal sealed class SvgTimeBudgetExceededException : System.Exception
    {
    }
}
