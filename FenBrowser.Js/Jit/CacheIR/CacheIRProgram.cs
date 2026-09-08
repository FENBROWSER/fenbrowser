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

    /// <summary>Set when a guarded slot stopped being a plain data property.</summary>
    internal bool IsStale { get; private set; }

    internal CacheIRProgram(CacheOp[] ops, int[] args, Shape[] shapes, string[] keys)
    {
        _ops = ops;
        _args = args;
        _shapes = shapes;
        _keys = keys;
        _resultSlot = -1;

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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool TryHit(JsObject receiver, Shape shape, string key, out JsValue result)
    {
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
