using FenBrowser.Js.Runtime;

namespace FenBrowser.Js.Heap;

public sealed class RootSet
{
    private readonly List<RootEntry> _entries = new();

    public int Count => _entries.Count;

    public void Push(ObjectHandle handle) => _entries.Add(new RootEntry(RootKind.Object, handle.ToInt64()));
    public void Push(StringHandle handle) => _entries.Add(new RootEntry(RootKind.String, handle.ToInt64()));
    public void Push(SymbolHandle handle) => _entries.Add(new RootEntry(RootKind.Symbol, handle.ToInt64()));

    public void PopTo(int mark)
    {
        if (mark < 0 || mark > Count)
        {
            throw new ArgumentOutOfRangeException(nameof(mark));
        }

        if (mark == Count)
        {
            return;
        }

        _entries.RemoveRange(mark, _entries.Count - mark);
    }

    public IReadOnlyList<ObjectHandle> Snapshot()
    {
        var list = new List<ObjectHandle>();
        foreach (var entry in _entries)
        {
            if (entry.Kind == RootKind.Object)
            {
                list.Add(ObjectHandle.FromInt64(entry.Payload));
            }
        }

        return list;
    }

    public IReadOnlyList<StringHandle> StringSnapshot()
    {
        var list = new List<StringHandle>();
        foreach (var entry in _entries)
        {
            if (entry.Kind == RootKind.String)
            {
                list.Add(StringHandle.FromInt64(entry.Payload));
            }
        }

        return list;
    }

    public IReadOnlyList<SymbolHandle> SymbolSnapshot()
    {
        var list = new List<SymbolHandle>();
        foreach (var entry in _entries)
        {
            if (entry.Kind == RootKind.Symbol)
            {
                list.Add(SymbolHandle.FromInt64(entry.Payload));
            }
        }

        return list;
    }

    private readonly record struct RootEntry(RootKind Kind, long Payload);

    private enum RootKind : byte
    {
        Object,
        String,
        Symbol
    }
}
