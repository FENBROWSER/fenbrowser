using System;

namespace FenBrowser.FenEngine.Adapters
{
    /// <summary>
    /// Immutable inputs for one SVG render. The renderer never performs ambient
    /// file or network I/O. External references are rejected unless explicitly
    /// enabled; an explicitly supplied trusted resolver then owns access.
    /// </summary>
    public sealed class SvgRenderRequest
    {
        public SvgRenderRequest(string content, SvgRenderLimits limits)
        {
            Content = content;
            Limits = limits;
        }

        public string Content { get; }
        public SvgRenderLimits Limits { get; }
        public Uri? BaseUri { get; init; }
        public ISvgResourceResolver? ResourceResolver { get; init; }
        /// <summary>
        /// Deterministic SVG document time sampled by declarative animations.
        /// The default is the initial document time of zero seconds.
        /// </summary>
        public double DocumentTimeSeconds { get; init; }
        /// <summary>
        /// Short caller label for <see cref="SvgDiagnostics"/> events, such as
        /// "image" or "inline-svg". Never part of rendering; sanitized and bounded
        /// before it is logged.
        /// </summary>
        public string? DiagnosticSource { get; init; }
        /// <summary>
        /// The zoom-and-pan transform of a standalone SVG document (SVG 2 §5.1.1,
        /// SVGSVGElement currentScale and currentTranslate), applied in viewport
        /// space when the picture is rasterized. Null is the identity.
        /// </summary>
        public SvgZoomAndPan? ZoomAndPan { get; init; }
    }

    /// <summary>
    /// A zoom-and-pan transform: content maps to <c>translate + scale * point</c> in
    /// viewport space.
    /// </summary>
    public readonly record struct SvgZoomAndPan(float Scale, float TranslateX, float TranslateY)
    {
        /// <summary>Bound on |scale|, so a script cannot request an absurd magnification.</summary>
        public const float MaxScale = 10_000f;

        /// <summary>Bound on the translation, in CSS pixels.</summary>
        public const float MaxTranslate = 1_000_000f;

        public bool IsIdentity => Scale == 1f && TranslateX == 0f && TranslateY == 0f;

        public bool IsValid =>
            float.IsFinite(Scale) && Math.Abs(Scale) <= MaxScale &&
            float.IsFinite(TranslateX) && Math.Abs(TranslateX) <= MaxTranslate &&
            float.IsFinite(TranslateY) && Math.Abs(TranslateY) <= MaxTranslate;
    }

    public enum SvgResourceKind
    {
        Image = 0,
        SvgDocument = 1,
        Stylesheet = 2,
        Font = 3
    }

    public readonly record struct SvgResolvedResource(
        Uri Uri,
        string ContentType,
        ReadOnlyMemory<byte> Content);

    /// <summary>
    /// Trusted bridge to caller-authorized resources. Implementations must not
    /// return data for a URI other than the exact requested URI.
    /// </summary>
    public interface ISvgResourceResolver
    {
        bool TryResolve(
            Uri absoluteUri,
            SvgResourceKind kind,
            out SvgResolvedResource resource,
            out string error);
    }
}
