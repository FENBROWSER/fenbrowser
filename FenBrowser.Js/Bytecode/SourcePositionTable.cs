namespace FenBrowser.Js.Bytecode;

/// <summary>
/// Maps an instruction index back to the line and column of the source that
/// produced it, for stack traces. Stored as runs rather than per instruction:
/// entry i covers every instruction from <c>Ips[i]</c> up to the next entry's
/// start, which is what the compiler naturally produces as it moves from one
/// expression to the next.
/// </summary>
public sealed class SourcePositionTable
{
    private readonly int[] _ips;
    private readonly int[] _lines;
    private readonly int[] _columns;

    public SourcePositionTable(int[] ips, int[] lines, int[] columns)
    {
        ArgumentNullException.ThrowIfNull(ips);
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(columns);
        if (lines.Length != ips.Length || columns.Length != ips.Length)
        {
            throw new ArgumentException("Position table columns must be the same length.");
        }

        _ips = ips;
        _lines = lines;
        _columns = columns;
    }

    public int Count => _ips.Length;

    /// <summary>
    /// The position of the run containing <paramref name="ip"/>, or false when
    /// the instruction comes before the first recorded position.
    /// </summary>
    public bool TryGetPosition(int ip, out int line, out int column)
    {
        var index = Array.BinarySearch(_ips, ip);
        if (index < 0)
        {
            // Not an exact start: the run it belongs to is the one before the
            // insertion point.
            index = ~index - 1;
        }

        if (index < 0)
        {
            line = 0;
            column = 0;
            return false;
        }

        line = _lines[index];
        column = _columns[index];
        return true;
    }
}
