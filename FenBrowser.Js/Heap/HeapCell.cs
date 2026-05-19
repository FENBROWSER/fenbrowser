namespace FenBrowser.Js.Heap;

public sealed class HeapCell
{
    public int Generation;

    public bool Marked;

    public required HeapCellKind Kind;

    public required ITraceable Payload;
}
