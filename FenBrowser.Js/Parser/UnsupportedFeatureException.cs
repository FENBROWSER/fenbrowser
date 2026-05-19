using FenBrowser.Js.AstValidation;
using FenBrowser.Js.Source;

namespace FenBrowser.Js.Parser;

public sealed class UnsupportedFeatureException : Exception
{
    public UnsupportedFeatureException(string featureName, FeatureSupportLevel level, SourceSpan span)
        : base($"Unsupported feature '{featureName}' at {span.Line}:{span.Column} ({level}).")
    {
        FeatureName = featureName;
        Level = level;
        Span = span;
    }

    public string FeatureName { get; }

    public FeatureSupportLevel Level { get; }

    public SourceSpan Span { get; }
}
