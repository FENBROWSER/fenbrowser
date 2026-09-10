using System.Runtime.CompilerServices;
using FenBrowser.Js.Objects;
using FenBrowser.Js.Runtime;

namespace FenBrowser.Js.Jit.CacheIR;

/// <summary>
/// A frozen guard sequence for one receiver shape at one cache site.
/// </summary>
/// <remarks>
/// The same program is the single description of the lookup for both tiers: the
/// interpreter runs <see cref="Run"/>, and the baseline compiler emits the ops
/// inline. Neither tier repeats the other's reasoning about what is safe to
/// cache, which is what kept the two paths from drifting apart before.
/// </remarks>
internal sealed class CacheIRProgram
{
    /// <summary>Which string-primitive sequence a program is, if any.</summary>
    private enum StringForm : byte
    {
        None,
        Length,
        PrototypeSlot,
    }


    private readonly CacheOp[] _ops;
    private readonly int[] _args;
    private readonly Shape[] _shapes;
    private readonly string[] _keys;

    // Every program the load attacher emits is one of two fixed sequences, so
    // those two run without walking the op list. The specialisation is derived
    // from the ops rather than set alongside them: a program that is not one of
    // these shapes falls to the general loop, and neither path can drift from
    // the other because only one of them is ever authored.
    private Shape? _guardedShape;
    private readonly string? _guardedKey;
    private readonly int _resultSlot;
    private readonly bool _isStore;
    private readonly bool _guardsArray;

    // The one program that guards no shape, because what it reads is not in
    // one. Recognised here for the same reason the others are: so the hot path
    // is a type test rather than a walk of the op list.
    private readonly bool _isDenseArrayLength;

    // The string-primitive forms, which guard no receiver shape because a
    // string has none. Held as one field so the object hot path pays a single
    // predictable test to turn them away rather than one per form.
    private readonly StringForm _stringForm;

    // The prototype form. The holder is held directly, which is safe because
    // the guard proves the receiver still points at it: a cell that was freed
    // and reused would not match the handle, since a handle carries the
    // generation as well as the index.
    private readonly bool _isProtoLoad;
    private readonly ObjectHandle _protoHandle;
    private readonly JsObject? _holder;
    private Shape? _holderShape;

    /// <summary>Set when a guarded slot stopped being a plain data property.</summary>
    internal bool IsStale { get; private set; }

    internal CacheIRProgram(
        CacheOp[] ops,
        int[] args,
        Shape[] shapes,
        string[] keys,
        ObjectHandle protoHandle = default,
        JsObject? holder = null,
        Shape? holderShape = null)
    {
        _ops = ops;
        _args = args;
        _shapes = shapes;
        _keys = keys;
        _resultSlot = -1;
        _protoHandle = protoHandle;
        _holder = holder;
        _holderShape = holderShape;

        if (ops[0] == CacheOp.GuardStringReceiver)
        {
            var i = 1;
            if (ops[i] == CacheOp.GuardKey) _guardedKey = keys[args[i++]];

            if (i == ops.Length - 1 && ops[i] == CacheOp.LoadStringLengthResult)
            {
                _stringForm = StringForm.Length;
                return;
            }

            if (i == ops.Length - 3 &&
                ops[i] == CacheOp.GuardStringPrototype &&
                ops[i + 1] == CacheOp.GuardHolderShape &&
                ops[i + 2] == CacheOp.LoadHolderSlotResult &&
                holder is not null &&
                holderShape is not null)
            {
                _stringForm = StringForm.PrototypeSlot;
                _resultSlot = args[i + 2];
                return;
            }

            // Not a sequence this class knows how to run. Leaving the form
            // unset makes every entry point refuse it, which is what an
            // unrecognised program has always done here.
            _guardedKey = null;
            return;
        }

        if (ops.Length == 5 &&
            ops[0] == CacheOp.GuardNotExotic &&
            ops[1] == CacheOp.GuardShape &&
            ops[2] == CacheOp.GuardProto &&
            ops[3] == CacheOp.GuardHolderShape &&
            ops[4] == CacheOp.LoadHolderSlotResult &&
            holder is not null &&
            holderShape is not null)
        {
            _isProtoLoad = true;
            _guardedShape = shapes[args[1]];
            _resultSlot = args[4];
            return;
        }

        if (ops.Length == 2 &&
            ops[0] == CacheOp.GuardDenseArray &&
            ops[1] == CacheOp.LoadArrayLengthResult)
        {
            _isDenseArrayLength = true;
            return;
        }

        if (ops.Length >= 3 &&
            ops[0] is CacheOp.GuardNotExotic or CacheOp.GuardNotProxy &&
            ops[1] == CacheOp.GuardShape &&
            ops[^1] is CacheOp.LoadSlotResult or CacheOp.StoreSlotResult)
        {
            var wellFormed = true;
            for (var i = 2; i < ops.Length - 1; i++)
            {
                if (ops[i] == CacheOp.GuardKey) _guardedKey = keys[args[i]];
                else if (ops[i] == CacheOp.GuardNotArray) _guardsArray = true;
                else wellFormed = false;
            }

            if (wellFormed)
            {
                _guardedShape = shapes[args[1]];
                _resultSlot = args[^1];
                _isStore = ops[^1] == CacheOp.StoreSlotResult;
            }
            else
            {
                _guardedKey = null;
            }
        }
    }

    /// <summary>
    /// The shape a load program guards, when the program is one a compiled body
    /// can check for itself: a load, with no key to compare. Null once the
    /// program has gone stale, so a compiled guard fails closed on the same
    /// test it already makes.
    /// </summary>
    internal Shape? InlineLoadShape => !_isStore && _guardedKey is null ? _guardedShape : null;

    /// <summary>The store counterpart, which also declines the array-length form.</summary>
    internal Shape? InlineStoreShape =>
        _isStore && _guardedKey is null && !_guardsArray ? _guardedShape : null;

    /// <summary>The slot a guarded program reads or writes.</summary>
    internal int ResultSlot => _resultSlot;

    internal bool Guards(Shape shape, string? key) =>
        !_isDenseArrayLength &&
        !_isProtoLoad &&
        _stringForm == StringForm.None &&
        ReferenceEquals(_guardedShape, shape) &&
        (_guardedKey is null || string.Equals(_guardedKey, key, StringComparison.Ordinal));

    internal int Length => _ops.Length;

    internal CacheOp OpAt(int index) => _ops[index];

    internal int ArgAt(int index) => _args[index];

    internal Shape ShapeAt(int index) => _shapes[index];

    internal string KeyAt(int index) => _keys[index];

    /// <summary>
    /// The hot path. The caller has already read <paramref name="shape"/> and
    /// established that the receiver is not exotic, so a hit is one reference
    /// compare, an optional key compare and a slot read.
    /// </summary>
    /// <summary>
    /// The store counterpart of <see cref="TryHit"/>: yields the slot to write,
    /// leaving the write and its barriers to the caller.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool TryHitStore(JsObject receiver, Shape shape, string key, out int slot)
    {
        slot = _resultSlot;
        if (!_isStore ||
            !ReferenceEquals(_guardedShape, shape) ||
            (_guardsArray && receiver is ArrayObject) ||
            (_guardedKey is { } guardedKey && !string.Equals(key, guardedKey, StringComparison.Ordinal)))
        {
            return false;
        }

        if (receiver.IsWritableDataSlot(slot))
        {
            return true;
        }

        MarkStale();
        return false;
    }

    /// <summary>
    /// The string-primitive counterpart of <see cref="TryHit"/>. The caller has
    /// established that the receiver is a string, and passes the realm's
    /// current %String.prototype% because the receiver does not name one.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool TryHitString(
        in JsValue receiver, string key, ObjectHandle stringPrototype, out JsValue result)
    {
        if (_guardedKey is { } guardedKey && !string.Equals(key, guardedKey, StringComparison.Ordinal))
        {
            result = JsValue.Undefined;
            return false;
        }

        if (_stringForm == StringForm.Length)
        {
            // Read off the value, not off a flattened copy of it: `s.length`
            // inside the loop that builds `s` is the shape that made string
            // concatenation quadratic once already.
            result = JsValue.FromNumber(receiver.StringLength);
            return true;
        }

        if (_stringForm == StringForm.PrototypeSlot &&
            stringPrototype.Equals(_protoHandle) &&
            ReferenceEquals(_holder!.CurrentShape, _holderShape) &&
            _holder.TryReadDataSlot(_resultSlot, out result))
        {
            return true;
        }

        result = JsValue.Undefined;
        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool TryHit(JsObject receiver, Shape shape, string key, out JsValue result)
    {
        // A string program answers nothing about an object, and guards no shape
        // that would turn one away on its own.
        if (_stringForm != StringForm.None)
        {
            result = JsValue.Undefined;
            return false;
        }

        if (_isDenseArrayLength)
        {
            // The site's key is fixed at "length" when it was attached, so
            // there is nothing to compare but the receiver's state.
            if (receiver is ArrayObject { IsDense: true } array)
            {
                result = JsValue.FromNumber(array.DenseCount);
                return true;
            }

            result = JsValue.Undefined;
            return false;
        }

        if (_isProtoLoad)
        {
            // The receiver's own layout is unchanged, so nothing of its own
            // shadows the name; its prototype is the same object; and that
            // object's layout is unchanged, so the property is still in the
            // slot it was found in.
            if (ReferenceEquals(_guardedShape, shape) &&
                receiver.PrototypeHandle is { } proto &&
                proto.Equals(_protoHandle) &&
                ReferenceEquals(_holder!.CurrentShape, _holderShape) &&
                _holder.TryReadDataSlot(_resultSlot, out result))
            {
                return true;
            }

            result = JsValue.Undefined;
            return false;
        }

        if (_isStore ||
            !ReferenceEquals(_guardedShape, shape) ||
            (_guardedKey is { } guardedKey && !string.Equals(key, guardedKey, StringComparison.Ordinal)))
        {
            result = JsValue.Undefined;
            return false;
        }

        if (receiver.TryReadDataSlot(_resultSlot, out result))
        {
            return true;
        }

        MarkStale();
        result = JsValue.Undefined;
        return false;
    }

    private void MarkStale()
    {
        IsStale = true;
        _guardedShape = null;
        _holderShape = null;
    }

    internal CacheRunResult Run(JsObject receiver, string key, out JsValue result)
    {
        for (var i = 0; i < _ops.Length; i++)
        {
            switch (_ops[i])
            {
                case CacheOp.GuardNotExotic:
                    if (receiver is ProxyObject or ModuleNamespaceObject) goto miss;
                    break;

                case CacheOp.GuardNotProxy:
                    if (receiver is ProxyObject) goto miss;
                    break;

                case CacheOp.GuardNotArray:
                    if (receiver is ArrayObject) goto miss;
                    break;

                case CacheOp.GuardShape:
                    if (!ReferenceEquals(receiver.CurrentShape, _shapes[_args[i]])) goto miss;
                    break;

                case CacheOp.GuardKey:
                    if (!string.Equals(key, _keys[_args[i]], StringComparison.Ordinal)) goto miss;
                    break;

                case CacheOp.GuardProto:
                    if (receiver.PrototypeHandle is not { } runProto ||
                        !runProto.Equals(_protoHandle)) goto miss;
                    break;

                case CacheOp.GuardHolderShape:
                    if (!ReferenceEquals(_holder?.CurrentShape, _holderShape)) goto miss;
                    break;

                case CacheOp.LoadHolderSlotResult:
                    return LoadSlot(_holder!, _args[i], out result);

                case CacheOp.GuardStringReceiver:
                case CacheOp.GuardStringPrototype:
                case CacheOp.LoadStringLengthResult:
                    // This walk is the object-receiver interpretation of a
                    // program; a string form has no object to run against.
                    goto miss;

                case CacheOp.GuardDenseArray:
                    if (receiver is not ArrayObject { IsDense: true }) goto miss;
                    break;

                case CacheOp.LoadArrayLengthResult:
                    result = JsValue.FromNumber(((ArrayObject)receiver).DenseCount);
                    return CacheRunResult.Hit;

                case CacheOp.LoadSlotResult:
                    return LoadSlot(receiver, _args[i], out result);

                default:
                    goto miss;
            }
        }

    miss:
        result = JsValue.Undefined;
        return CacheRunResult.Miss;
    }

    private static CacheRunResult LoadSlot(JsObject receiver, int slot, out JsValue result) =>
        receiver.TryReadDataSlot(slot, out result) ? CacheRunResult.Hit : CacheRunResult.Stale;
}
