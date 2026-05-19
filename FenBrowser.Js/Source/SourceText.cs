namespace FenBrowser.Js.Source;

public sealed class SourceText
{
    public SourceText(string text, string? path = null)
    {
        Text = text ?? string.Empty;
        Path = path;
    }

    public string Text { get; }

    public string? Path { get; }

    public int Length => Text.Length;
}
