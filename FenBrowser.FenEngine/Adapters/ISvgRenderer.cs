using System;
using System.Collections.Generic;
using SkiaSharp;

namespace FenBrowser.FenEngine.Adapters
{
    /// <summary>
    /// SVG rendering interface implemented by first-party backends.
    /// 
    /// RULE 3: SVG must be sandboxed. This adapter enforces limits.
    /// RULE 5: SVG rendering is first-party only. The seam admits no
    /// third-party or compatibility backend.
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
        /// WARNING: May be invalid after the renderer's parser state is released -
        /// use Bitmap property instead.
        /// </summary>
        public SKPicture Picture { get; set; }
        
        /// <summary>
        /// Pre-rendered bitmap (safe to use after the renderer's parser state is released).
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

        /// <summary>Stable, bounded categories explaining rejected resources.</summary>
        public IReadOnlyList<string> ResourceRejectionReasonCodes { get; set; } = Array.Empty<string>();

        /// <summary>Backend that produced the returned pixels.</summary>
        public SvgRendererBackend Backend { get; internal set; }

        /// <summary>
        /// True when the first-party renderer encountered a declared unsupported
        /// feature whose omission can change visible output. The first-party
        /// engine is the only backend, so this is terminal: no later stage can
        /// complete the render and the pixels are never admissible.
        /// </summary>
        public bool RequiresFallback { get; set; }

        /// <summary>
        /// True when a resource was omitted because it was disallowed, unsupported,
        /// malformed, or exceeded an admission limit. The decision is final and
        /// no alternative renderer may re-interpret the document to bypass it.
        /// </summary>
        public bool HadResourceRejection { get; set; }

        /// <summary>Upper bound for reason codes echoed into a rejection reason.</summary>
        public const int MaxRejectionDiagnosticCodes = 4;

        /// <summary>Upper bound for characters retained in a rejection reason.</summary>
        public const int MaxRejectionDiagnosticChars = 200;

        /// <summary>
        /// Fail-closed admission predicate for decoded SVG pixels, shared by every
        /// consumer of <see cref="SvgRenderResult"/> (image loader, target-process
        /// decode entry points, tooling).
        /// <para>
        /// Pixels are admissible only when the first-party engine produced them,
        /// the render succeeded, and they carry no signal that makes them
        /// non-authoritative. A backend value other than
        /// <see cref="SvgRendererBackend.FirstParty"/> fails closed before any
        /// other signal is consulted. Both a resource rejection (flag or reason
        /// codes) and a fallback requirement reject the result: the first-party
        /// engine is the only backend, so an unsupported feature can never be
        /// completed later and its pixels are never admissible.
        /// </para>
        /// </summary>
        public static bool IsAdmissible(SvgRenderResult result)
        {
            if (result == null)
            {
                return false;
            }

            if (!SvgRendererBackendPolicy.IsAdmissible(result.Backend))
            {
                return false;
            }

            if (!result.Success)
            {
                return false;
            }

            if (result.HadResourceRejection || HasReasonCodes(result.ResourceRejectionReasonCodes))
            {
                return false;
            }

            return !result.RequiresFallback;
        }

        /// <summary>
        /// Bounded, source-free explanation of why <paramref name="result"/> is
        /// inadmissible. Returns null when the result is admissible. Never
        /// includes SVG source and is safe to place in IPC metadata and logs.
        /// </summary>
        public static string DescribeRejection(SvgRenderResult result)
        {
            if (result == null)
            {
                return Truncate("SVG render produced no result", MaxRejectionDiagnosticChars);
            }

            if (!SvgRendererBackendPolicy.IsAdmissible(result.Backend))
            {
                return Truncate(
                    "SVG render backend is not the first-party engine: " +
                        SvgRendererBackendPolicy.Describe(result.Backend),
                    MaxRejectionDiagnosticChars);
            }

            if (result.HadResourceRejection || HasReasonCodes(result.ResourceRejectionReasonCodes))
            {
                return Truncate(
                    "SVG render rejected a resource: " +
                        JoinCodes(result.ResourceRejectionReasonCodes),
                    MaxRejectionDiagnosticChars);
            }

            if (result.RequiresFallback)
            {
                var codes = JoinCodes(result.FallbackReasonCodes);
                return Truncate(
                    "SVG render requires fallback: " +
                        (codes.Length == 0 ? "unsupported" : codes),
                    MaxRejectionDiagnosticChars);
            }

            if (!result.Success)
            {
                return Truncate(
                    string.IsNullOrWhiteSpace(result.ErrorMessage)
                        ? "SVG render failed"
                        : result.ErrorMessage,
                    MaxRejectionDiagnosticChars);
            }

            return null;
        }

        private static bool HasReasonCodes(IReadOnlyList<string> codes) =>
            codes != null && codes.Count > 0;

        private static string JoinCodes(IReadOnlyList<string> codes)
        {
            if (codes == null || codes.Count == 0)
            {
                return string.Empty;
            }

            int taken = Math.Min(codes.Count, MaxRejectionDiagnosticCodes);
            var parts = new string[taken];
            for (int i = 0; i < taken; i++)
            {
                var code = codes[i];
                parts[i] = string.IsNullOrWhiteSpace(code) ? "unspecified" : code.Trim();
            }

            string joined = string.Join(", ", parts);
            if (codes.Count > taken)
            {
                joined += $", +{codes.Count - taken} more";
            }

            return joined;
        }

        private static string Truncate(string value, int maxChars)
        {
            if (string.IsNullOrEmpty(value) || value.Length <= maxChars)
            {
                return value ?? string.Empty;
            }

            return value.Substring(0, maxChars) + "...";
        }

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
        /// Maximum SVG source length in UTF-16 code units admitted to the
        /// first-party renderer.
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
        /// Whether explicitly supplied external references may be resolved.
        /// False rejects them; true still requires a base URI, a trusted resolver,
        /// and a same-origin target. Default: false (disabled for security).
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
