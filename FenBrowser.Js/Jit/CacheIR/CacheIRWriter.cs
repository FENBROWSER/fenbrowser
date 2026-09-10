using FenBrowser.Js.Objects;
using FenBrowser.Js.Runtime;

namespace FenBrowser.Js.Jit.CacheIR;

/// <summary>Builds one <see cref="CacheIRProgram"/>. Not thread-safe; attachers are short-lived.</summary>
internal sealed class CacheIRWriter
{
    private readonly List<CacheOp> _ops = new(4);
    private readonly List<int> _args = new(4);
    private readonly List<Shape> _shapes = new(1);
    private readonly List<string> _keys = new(1);

    private ObjectHandle? _protoHandle;
    private JsObject? _holder;
    private Shape? _holderShape;

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

    /// <summary>
    /// The prototype-chain form, written as one call because its three parts
    /// are only ever correct together: the receiver's own layout, the identity
    /// of its prototype, and that prototype's layout.
    /// </summary>
    internal void LoadFromPrototype(ObjectHandle protoHandle, JsObject holder, Shape holderShape, int slot)
    {
        ArgumentNullException.ThrowIfNull(holder);
        ArgumentNullException.ThrowIfNull(holderShape);
        if (slot < 0) throw new ArgumentOutOfRangeException(nameof(slot));

        _ops.Add(CacheOp.GuardProto);
        _args.Add(0);
        _ops.Add(CacheOp.GuardHolderShape);
        _args.Add(0);
        _ops.Add(CacheOp.LoadHolderSlotResult);
        _args.Add(slot);
        _protoHandle = protoHandle;
        _holder = holder;
        _holderShape = holderShape;
    }

    internal void GuardStringReceiver()
    {
        _ops.Add(CacheOp.GuardStringReceiver);
        _args.Add(0);
    }

    internal void LoadStringLengthResult()
    {
        _ops.Add(CacheOp.LoadStringLengthResult);
        _args.Add(0);
    }

    /// <summary>
    /// The string-primitive counterpart of <see cref="LoadFromPrototype"/>,
    /// written as one call for the same reason: the identity of the realm's
    /// %String.prototype% and that object's layout are only correct together.
    /// There is no receiver shape to guard - a string has none - and none is
    /// needed, because the caller has already established that the name is
    /// neither `length` nor an integer index, which is everything a string
    /// primitive can own.
    /// </summary>
    internal void LoadFromStringPrototype(
        ObjectHandle stringProtoHandle, JsObject holder, Shape holderShape, int slot)
    {
        ArgumentNullException.ThrowIfNull(holder);
        ArgumentNullException.ThrowIfNull(holderShape);
        if (slot < 0) throw new ArgumentOutOfRangeException(nameof(slot));

        _ops.Add(CacheOp.GuardStringPrototype);
        _args.Add(0);
        _ops.Add(CacheOp.GuardHolderShape);
        _args.Add(0);
        _ops.Add(CacheOp.LoadHolderSlotResult);
        _args.Add(slot);
        _protoHandle = stringProtoHandle;
        _holder = holder;
        _holderShape = holderShape;
    }

    internal void GuardDenseArray()
    {
        _ops.Add(CacheOp.GuardDenseArray);
        _args.Add(0);
    }

    internal void LoadArrayLengthResult()
    {
        _ops.Add(CacheOp.LoadArrayLengthResult);
        _args.Add(0);
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
        _keys.Count == 0 ? Array.Empty<string>() : _keys.ToArray(),
        _protoHandle ?? default,
        _holder,
        _holderShape);
}
