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

    /// <summary>Set when a guarded slot stopped being a plain data property.</summary>
    internal bool IsStale { get; private set; }

    internal CacheIRProgram(CacheOp[] ops, int[] args, Shape[] shapes, string[] keys)
    {
        _ops = ops;
        _args = args;
        _shapes = shapes;
        _keys = keys;
        _resultSlot = -1;

        if (ops.Length is 3 or 4 &&
            ops[0] == CacheOp.GuardNotExotic &&
            ops[1] == CacheOp.GuardShape &&
            ops[^1] == CacheOp.LoadSlotResult &&
            (ops.Length == 3 || ops[2] == CacheOp.GuardKey))
        {
            _guardedShape = shapes[args[1]];
            _guardedKey = ops.Length == 4 ? keys[args[2]] : null;
            _resultSlot = args[^1];
        }
    }

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
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool TryHit(JsObject receiver, Shape shape, string key, out JsValue result)
    {
        if (!ReferenceEquals(_guardedShape, shape) ||
            (_guardedKey is { } guardedKey && !string.Equals(key, guardedKey, StringComparison.Ordinal)))
        {
            result = JsValue.Undefined;
            return false;
        }

        var properties = receiver.PropertyArray;
        var slot = _resultSlot;
        if ((uint)slot < (uint)properties.Length &&
            properties[slot] is { } descriptor &&
            !descriptor.IsAccessor)
        {
            result = descriptor.Value;
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

    private static CacheRunResult LoadSlot(JsObject receiver, int slot, out JsValue result)
    {
        var properties = receiver.PropertyArray;
        if ((uint)slot >= (uint)properties.Length ||
            properties[slot] is not { } descriptor ||
            descriptor.IsAccessor)
        {
            result = JsValue.Undefined;
            return CacheRunResult.Stale;
        }

        result = descriptor.Value;
        return CacheRunResult.Hit;
    }
}
