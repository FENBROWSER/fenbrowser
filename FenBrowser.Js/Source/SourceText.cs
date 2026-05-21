namespace FenBrowser.Js.Source;

public sealed class SourceText
{
    private readonly int[] _lineStarts;

    public SourceText(string text, string? path = null)
    {
        Text = text ?? string.Empty;
        Path = path;
        _lineStarts = BuildLineStarts(Text);
    }

    public string Text { get; }

    public string? Path { get; }

    public int Length => Text.Length;

    public IReadOnlyList<int> LineStarts => _lineStarts;

    public (int Line, int Column) GetLineColumn(int position)
    {
        if (position < 0 || position > Text.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(position));
        }

        var index = Array.BinarySearch(_lineStarts, position);
        var lineIndex = index >= 0 ? index : ~index - 1;
        return (lineIndex + 1, position - _lineStarts[lineIndex] + 1);
    }

    private static int[] BuildLineStarts(string text)
    {
        var starts = new List<int> { 0 };
        for (var i = 0; i < text.Length; i++)
        {
            if (!IsLineTerminatorAt(text, i, out var width))
            {
                continue;
            }

            var next = i + width;
            starts.Add(next);
            i = next - 1;
        }

        return starts.ToArray();
    }

    private static bool IsLineTerminatorAt(string text, int index, out int width)
    {
        width = 1;
        var ch = text[index];
        if (ch == '\r')
        {
            if (index + 1 < text.Length && text[index + 1] == '\n')
            {
                width = 2;
            }

            return true;
        }

        return ch is '\n' or '\u2028' or '\u2029';
    }
}
