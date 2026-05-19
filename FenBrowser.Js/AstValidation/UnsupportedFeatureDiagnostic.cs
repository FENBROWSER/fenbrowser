using FenBrowser.Js.Source;

namespace FenBrowser.Js.AstValidation;

public sealed record UnsupportedFeatureDiagnostic(string FeatureName, SourceSpan Span);
