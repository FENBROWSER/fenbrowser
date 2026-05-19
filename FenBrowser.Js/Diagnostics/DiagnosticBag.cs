namespace FenBrowser.Js.Diagnostics;

public sealed class DiagnosticBag
{
    private readonly List<Diagnostic> _items = new();

    public IReadOnlyList<Diagnostic> Items => _items;

    public bool HasErrors => _items.Any(d => d.Severity is DiagnosticSeverity.Error or DiagnosticSeverity.Fatal);

    public void Add(Diagnostic diagnostic) => _items.Add(diagnostic);

    public void Clear() => _items.Clear();
}
