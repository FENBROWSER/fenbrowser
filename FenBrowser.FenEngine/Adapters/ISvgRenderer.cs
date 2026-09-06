using System;
using System.Collections.Generic;
using SkiaSharp;

namespace FenBrowser.FenEngine.Adapters
{
    /// <summary>
    /// SVG rendering interface implemented by first-party, compatibility, or
    /// composite backends.
    /// 
    /// RULE 3: SVG must be sandboxed. This adapter enforces limits.
    /// RULE 5: If Svg.Skia disappears, we only replace this implementation.
    /// </summary>
    public interface ISvgRenderer
    {
        /// <summary>Render a document with an optional trusted resource context.</summary>
        SvgRenderResult Render(SvgRenderRequest request);

        /// <summary>
        /// Render SVG pixels with explicit safety limits.
        /// </summary>
        /// <param name="svgContent">SVG XML content</param>
        /// <param name="limits">Rendering limits for sandboxing</param>
        /// <returns>An owned, disposable result describing success or failure.</returns>
        SvgRenderResult Render(string svgContent, SvgRenderLimits limits);
        
        /// <summary>
        /// Render SVG with default (safe) limits.
        /// </summary>
        SvgRenderResult Render(string svgContent);
    }
    
    /// <summary>
    /// Result of SVG rendering.
    /// </summary>
    public sealed class SvgRenderResult : IDisposable
    {
        /// <summary>
        /// The rendered picture (null if failed).
        /// WARNING: May be invalid after SKSvg disposal - use Bitmap property instead.
        /// </summary>
        public SKPicture Picture { get; set; }
        
        /// <summary>
        /// Pre-rendered bitmap (safe to use after SKSvg disposal).
        /// This is the preferred way to access the rendered SVG.
        /// </summary>
        public SKBitmap Bitmap { get; set; }
        
        /// <summary>
        /// Natural width of the SVG.
        /// </summary>
        public float Width { get; set; }
        
        /// <summary>
        /// Natural height of the SVG.
        /// </summary>
        public float Height { get; set; }
        
        /// <summary>
        /// Whether rendering succeeded.
        /// </summary>
        public bool Success { get; set; }
        
        /// <summary>
        /// Error message if failed.
        /// </summary>
        public string ErrorMessage { get; set; }

        /// <summary>
        /// Bounded diagnostics produced while parsing or rendering. Messages never
        /// include the source document and are safe to surface in debug telemetry.
        /// </summary>
        public IReadOnlyList<string> Warnings { get; set; } = Array.Empty<string>();

        /// <summary>Stable, bounded categories explaining compatibility routing.</summary>
        public IReadOnlyList<string> FallbackReasonCodes { get; set; } = Array.Empty<string>();

        /// <summary>Stable, bounded categories explaining rejected embedded resources.</summary>
        public IReadOnlyList<string> ResourceRejectionReasonCodes { get; set; } = Array.Empty<string>();

        /// <summary>Backend that produced the returned pixels.</summary>
        public SvgRendererBackend Backend { get; set; }

        /// <summary>
        /// True when the first-party renderer encountered a declared unsupported
        /// feature whose omission can change visible output.
        /// </summary>
        public bool RequiresFallback { get; set; }

        /// <summary>
        /// True when an embedded resource was omitted because it was unsupported,
        /// malformed, or exceeded an admission limit. Hybrid routing must not
        /// bypass that decision through the less observable legacy parser.
        /// </summary>
        public bool HadResourceRejection { get; set; }

        /// <summary>True when a composite renderer returned legacy-rendered pixels.</summary>
        public bool UsedLegacyFallback { get; set; }

        /// <summary>
        /// Transfers bitmap ownership to the caller. The detached bitmap will not
        /// be disposed when this result is disposed.
        /// </summary>
        public SKBitmap DetachBitmap()
        {
            var bitmap = Bitmap;
            Bitmap = null;
            return bitmap;
        }

        /// <summary>
        /// Transfers picture ownership to the caller. New renderers should prefer
        /// returning a bitmap because pictures may be tied to parser-owned state.
        /// </summary>
        public SKPicture DetachPicture()
        {
            var picture = Picture;
            Picture = null;
            return picture;
        }

        public void Dispose()
        {
            Bitmap?.Dispose();
            Bitmap = null;
            Picture?.Dispose();
            Picture = null;
        }
    }
    
    /// <summary>
    /// Safety limits for SVG rendering.
    /// 
    /// RULE 3: SVG must be sandboxed deliberately.
    /// These limits prevent DoS attacks and stability issues.
    /// </summary>
    public struct SvgRenderLimits
    {
        /// <summary>
        /// Maximum recursion depth for nested elements.
        /// </summary>
        public int MaxRecursionDepth { get; set; }
        
        /// <summary>
        /// Maximum number of filter effects.
        /// </summary>
        public int MaxFilterCount { get; set; }
        
        /// <summary>
        /// Maximum render time in milliseconds. This is an elapsed-time guard after
        /// parsing; source/raster admission limits provide the pre-allocation boundary.
        /// </summary>
        public int MaxRenderTimeMs { get; set; }
        
        /// <summary>
        /// Maximum total element count.
        /// </summary>
        public int MaxElementCount { get; set; }

        /// <summary>
        /// Maximum SVG source length in UTF-16 code units admitted to Svg.Skia.
        /// </summary>
        public int MaxSourceChars { get; set; }

        /// <summary>
        /// Maximum decoded raster width/height used for the pre-rendered bitmap.
        /// </summary>
        public int MaxRasterWidth { get; set; }
        public int MaxRasterHeight { get; set; }

        /// <summary>
        /// Maximum decoded raster pixel count. This is the primary memory-bomb guard;
        /// BGRA32 consumes roughly four bytes per admitted pixel before native overhead.
        /// </summary>
        public long MaxRasterPixels { get; set; }
        
        /// <summary>
        /// Maximum decoded pixel count per embedded raster image (data URI).
        /// Independent of the document raster budget so one oversized bitmap
        /// cannot consume the whole page allocation.
        /// </summary>
        public long MaxDecodedImagePixels { get; set; }

        /// <summary>Maximum decoded bytes admitted for one embedded data URI.</summary>
        public int MaxDecodedImageBytes { get; set; }

        /// <summary>Maximum encoded bytes admitted across all nested resources.</summary>
        public int MaxCumulativeResourceBytes { get; set; }

        /// <summary>Maximum number of admitted embedded or resolved resources.</summary>
        public int MaxResourceCount { get; set; }

        /// <summary>Maximum simultaneous full-surface opacity layers.</summary>
        public int MaxActiveLayers { get; set; }

        /// <summary>Maximum use, gradient, and clip reference-chain depth.</summary>
        public int MaxReferenceDepth { get; set; }

        /// <summary>
        /// Whether to allow external references (xlink:href to external URLs).
        /// Default: false (DISABLED for security)
        /// </summary>
        public bool AllowExternalReferences { get; set; }
        
        /// <summary>
        /// Get default safe limits.
        /// </summary>
        public static SvgRenderLimits Default => new SvgRenderLimits
        {
            MaxRecursionDepth = 32,
            MaxFilterCount = 16,
            MaxRenderTimeMs = 250,
            MaxElementCount = 50000,
            MaxSourceChars = 8 * 1024 * 1024,
            MaxRasterWidth = 8192,
            MaxRasterHeight = 8192,
            MaxRasterPixels = 16L * 1024 * 1024,
            MaxDecodedImagePixels = 16L * 1024 * 1024,
            MaxDecodedImageBytes = 8 * 1024 * 1024,
            MaxCumulativeResourceBytes = 32 * 1024 * 1024,
            MaxResourceCount = 64,
            MaxActiveLayers = 8,
            MaxReferenceDepth = 32,
            AllowExternalReferences = false
        };
        
        /// <summary>
        /// Get strict limits for untrusted content.
        /// </summary>
        public static SvgRenderLimits Strict => new SvgRenderLimits
        {
            MaxRecursionDepth = 16,
            MaxFilterCount = 5,
            MaxRenderTimeMs = 50,
            MaxElementCount = 5000,
            MaxSourceChars = 2 * 1024 * 1024,
            MaxRasterWidth = 4096,
            MaxRasterHeight = 4096,
            MaxRasterPixels = 8L * 1024 * 1024,
            MaxDecodedImagePixels = 8L * 1024 * 1024,
            MaxDecodedImageBytes = 2 * 1024 * 1024,
            MaxCumulativeResourceBytes = 8 * 1024 * 1024,
            MaxResourceCount = 32,
            MaxActiveLayers = 4,
            MaxReferenceDepth = 16,
            AllowExternalReferences = false
        };

        /// <summary>
        /// Fills omitted values and applies non-bypassable process-safety caps.
        /// Callers may tighten limits but cannot request unbounded native memory,
        /// recursion, or reference expansion.
        /// </summary>
        public static SvgRenderLimits Normalize(SvgRenderLimits limits)
        {
            var defaults = Default;
            if (limits.MaxRecursionDepth <= 0) limits.MaxRecursionDepth = defaults.MaxRecursionDepth;
            if (limits.MaxFilterCount <= 0) limits.MaxFilterCount = defaults.MaxFilterCount;
            if (limits.MaxRenderTimeMs <= 0) limits.MaxRenderTimeMs = defaults.MaxRenderTimeMs;
            if (limits.MaxElementCount <= 0) limits.MaxElementCount = defaults.MaxElementCount;
            if (limits.MaxSourceChars <= 0) limits.MaxSourceChars = defaults.MaxSourceChars;
            if (limits.MaxRasterWidth <= 0) limits.MaxRasterWidth = defaults.MaxRasterWidth;
            if (limits.MaxRasterHeight <= 0) limits.MaxRasterHeight = defaults.MaxRasterHeight;
            if (limits.MaxRasterPixels <= 0) limits.MaxRasterPixels = defaults.MaxRasterPixels;
            if (limits.MaxDecodedImagePixels <= 0) limits.MaxDecodedImagePixels = defaults.MaxDecodedImagePixels;
            if (limits.MaxDecodedImageBytes <= 0) limits.MaxDecodedImageBytes = defaults.MaxDecodedImageBytes;
            if (limits.MaxCumulativeResourceBytes <= 0)
                limits.MaxCumulativeResourceBytes = defaults.MaxCumulativeResourceBytes;
            if (limits.MaxResourceCount <= 0) limits.MaxResourceCount = defaults.MaxResourceCount;
            if (limits.MaxActiveLayers <= 0) limits.MaxActiveLayers = defaults.MaxActiveLayers;
            if (limits.MaxReferenceDepth <= 0) limits.MaxReferenceDepth = defaults.MaxReferenceDepth;

            limits.MaxRecursionDepth = Math.Min(limits.MaxRecursionDepth, 512);
            limits.MaxFilterCount = Math.Min(limits.MaxFilterCount, 1_000);
            limits.MaxRenderTimeMs = Math.Min(limits.MaxRenderTimeMs, 30_000);
            limits.MaxElementCount = Math.Min(limits.MaxElementCount, 250_000);
            limits.MaxSourceChars = Math.Min(limits.MaxSourceChars, 32 * 1024 * 1024);
            limits.MaxRasterWidth = Math.Min(limits.MaxRasterWidth, 32_768);
            limits.MaxRasterHeight = Math.Min(limits.MaxRasterHeight, 32_768);
            limits.MaxRasterPixels = Math.Min(limits.MaxRasterPixels, 64L * 1024 * 1024);
            limits.MaxDecodedImagePixels = Math.Min(limits.MaxDecodedImagePixels, 64L * 1024 * 1024);
            limits.MaxDecodedImageBytes = Math.Min(limits.MaxDecodedImageBytes, 32 * 1024 * 1024);
            limits.MaxCumulativeResourceBytes = Math.Min(
                limits.MaxCumulativeResourceBytes, 64 * 1024 * 1024);
            limits.MaxResourceCount = Math.Min(limits.MaxResourceCount, 512);
            limits.MaxActiveLayers = Math.Min(limits.MaxActiveLayers, 16);
            limits.MaxReferenceDepth = Math.Min(limits.MaxReferenceDepth, 64);
            return limits;
        }
    }
}
