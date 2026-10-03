using FenBrowser.Js.Runtime;

namespace FenBrowser.Js.Heap;

/// <summary>
/// The object values an embedder-side root table (a C# dictionary the collector
/// cannot see into) has taken since they could still be young.
/// </summary>
/// <remarks>
/// Such tables are GC roots, and walking every entry on every minor collection
/// cost YouTube a millisecond or more per table per collection, although nearly
/// every value in them had long been promoted - and a minor collection neither
/// frees nor needs to mark an Old cell. A minor collection therefore traces only
/// this log, dropping entries as they are promoted; a major collection walks the
/// whole table and clears the log, after which every survivor is Old. Every
/// write into the table must go through <see cref="Record(JsValue)"/>.
/// </remarks>
public sealed class RecentRootLog
{
    private readonly List<ObjectHandle> _handles = new();

    public void Record(JsValue value)
    {
        if (value.Tag == JsValueTag.Object)
        {
            _handles.Add(value.AsObjectHandle());
        }
    }

    /// <summary>Minor collection: traces the entries still young, forgets the rest.</summary>
    public void TraceYoung(JsHeap heap, IHeapTracer tracer, string context)
    {
        var kept = 0;
        for (var i = 0; i < _handles.Count; i++)
        {
            var handle = _handles[i];
            if (heap.IsOldObject(handle))
            {
                continue;
            }

            tracer.TraceRoot(context, handle);
            _handles[kept++] = handle;
        }

        _handles.RemoveRange(kept, _handles.Count - kept);
    }

    /// <summary>A major collection walked the whole table; nothing logged is needed.</summary>
    public void Clear() => _handles.Clear();
}
