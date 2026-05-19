using FenBrowser.Js.Source;

namespace FenBrowser.Js.Diagnostics;

public readonly record struct Diagnostic(
    string Code,
    string Message,
    DiagnosticSeverity Severity,
    SourceSpan Span);
