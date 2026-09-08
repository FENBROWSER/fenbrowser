using FenBrowser.Js.Objects;

namespace FenBrowser.Js.Jit.CacheIR;

/// <summary>Builds one <see cref="CacheIRProgram"/>. Not thread-safe; attachers are short-lived.</summary>
internal sealed class CacheIRWriter
{
    private readonly List<CacheOp> _ops = new(4);
    private readonly List<int> _args = new(4);
    private readonly List<Shape> _shapes = new(1);
    private readonly List<string> _keys = new(1);

    internal void GuardNotExotic()
    {
        _ops.Add(CacheOp.GuardNotExotic);
        _args.Add(0);
    }

    internal void GuardShape(Shape shape)
    {
        ArgumentNullException.ThrowIfNull(shape);
        _ops.Add(CacheOp.GuardShape);
        _args.Add(_shapes.Count);
        _shapes.Add(shape);
    }

    internal void GuardKey(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        _ops.Add(CacheOp.GuardKey);
        _args.Add(_keys.Count);
        _keys.Add(key);
    }

    internal void GuardNotProxy()
    {
        _ops.Add(CacheOp.GuardNotProxy);
        _args.Add(0);
    }

    internal void GuardNotArray()
    {
        _ops.Add(CacheOp.GuardNotArray);
        _args.Add(0);
    }

    internal void StoreSlotResult(int slot)
    {
        if (slot < 0) throw new ArgumentOutOfRangeException(nameof(slot));
        _ops.Add(CacheOp.StoreSlotResult);
        _args.Add(slot);
    }

    internal void LoadSlotResult(int slot)
    {
        if (slot < 0) throw new ArgumentOutOfRangeException(nameof(slot));
        _ops.Add(CacheOp.LoadSlotResult);
        _args.Add(slot);
    }

    internal CacheIRProgram Build() => new(
        _ops.ToArray(),
        _args.ToArray(),
        _shapes.Count == 0 ? Array.Empty<Shape>() : _shapes.ToArray(),
        _keys.Count == 0 ? Array.Empty<string>() : _keys.ToArray());
}
