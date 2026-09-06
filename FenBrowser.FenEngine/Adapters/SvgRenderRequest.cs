using System;

namespace FenBrowser.FenEngine.Adapters
{
    /// <summary>
    /// Immutable inputs for one SVG render. The renderer never performs ambient
    /// file or network I/O; an explicitly supplied trusted resolver owns access.
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
