using FenBrowser.Js.Runtime;

namespace FenBrowser.Js.Heap;

public sealed class RootSet
{
    private readonly List<ObjectHandle> _objectRoots = new();

    public int Count => _objectRoots.Count;

    public void Push(ObjectHandle handle) => _objectRoots.Add(handle);

    public void PopTo(int mark)
    {
        if (mark < 0 || mark > _objectRoots.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(mark));
        }

        if (mark == _objectRoots.Count)
        {
            return;
        }

        _objectRoots.RemoveRange(mark, _objectRoots.Count - mark);
    }

    public IReadOnlyList<ObjectHandle> Snapshot() => _objectRoots;
}
