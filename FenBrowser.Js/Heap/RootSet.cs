using FenBrowser.Js.Runtime;

namespace FenBrowser.Js.Heap;

public sealed class RootSet
{
    private readonly List<ObjectHandle> _objectRoots = new();
    private readonly List<StringHandle> _stringRoots = new();
    private readonly List<SymbolHandle> _symbolRoots = new();

    public int Count => _objectRoots.Count + _stringRoots.Count + _symbolRoots.Count;

    public void Push(ObjectHandle handle) => _objectRoots.Add(handle);
    public void Push(StringHandle handle) => _stringRoots.Add(handle);
    public void Push(SymbolHandle handle) => _symbolRoots.Add(handle);

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

        var toRemove = Count - mark;
        RemoveFromEnd(_symbolRoots, ref toRemove);
        RemoveFromEnd(_stringRoots, ref toRemove);
        RemoveFromEnd(_objectRoots, ref toRemove);
    }

    public IReadOnlyList<ObjectHandle> Snapshot() => _objectRoots;
    public IReadOnlyList<StringHandle> StringSnapshot() => _stringRoots;
    public IReadOnlyList<SymbolHandle> SymbolSnapshot() => _symbolRoots;

    private static void RemoveFromEnd<T>(List<T> list, ref int toRemove)
    {
        if (toRemove <= 0 || list.Count == 0)
        {
            return;
        }

        var count = Math.Min(list.Count, toRemove);
        list.RemoveRange(list.Count - count, count);
        toRemove -= count;
    }
}
