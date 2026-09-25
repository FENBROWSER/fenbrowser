using FenBrowser.Js.Objects;
using FenBrowser.Js.Runtime;

namespace FenBrowser.Js.Jit.CacheIR;

/// <summary>
/// The programs attached to one bytecode offset, tried in order.
/// </summary>
/// <remarks>
/// Each program carries its own guards, so a linear scan is correct regardless
/// of what the others hold. A site that outgrows its capacity turns megamorphic
/// and stops attaching rather than thrashing.
/// </remarks>
internal sealed class CacheIRSite
{
    internal const int Capacity = 4;

    private CacheIRProgram? _p0, _p1, _p2, _p3;

    internal bool IsMegamorphic { get; private set; }

    internal int Count
    {
        get
        {
            var n = 0;
            if (_p0 is not null) n++;
            if (_p1 is not null) n++;
            if (_p2 is not null) n++;
            if (_p3 is not null) n++;
            return n;
        }
    }

    /// <summary>The first attached program, for callers that inline the guard themselves.</summary>
    internal CacheIRProgram? First => _p0;

    internal bool TryRun(JsObject receiver, string key, out JsValue result)
    {
        // Hoisted once for the whole site: both are the same for every program
        // attached here, and the guards that remain are a reference compare.
        if (receiver is ProxyObject or ModuleNamespaceObject)
        {
            result = JsValue.Undefined;
            return false;
        }

        var shape = receiver.CurrentShape;
        if (_p0 is { } p0 && p0.TryHit(receiver, shape, key, out result)) return true;
        if (_p1 is { } p1 && p1.TryHit(receiver, shape, key, out result)) return true;
        if (_p2 is { } p2 && p2.TryHit(receiver, shape, key, out result)) return true;
        if (_p3 is { } p3 && p3.TryHit(receiver, shape, key, out result)) return true;

        // A megamorphic site keeps no programs, but most of what it sees is still
        // an own data property the receiver's shape can locate directly: one
        // table lookup instead of the general [[Get]]. An accessor, an inherited
        // name or anything the shape does not describe still misses.
        if (IsMegamorphic && shape.TryGetSlot(key, out var ownSlot) && receiver.TryReadDataSlot(ownSlot, out result))
        {
            return true;
        }

        result = JsValue.Undefined;
        return false;
    }

    /// <summary>
    /// The string-primitive path. A string carries no shape and no realm, so
    /// the caller supplies the realm's %String.prototype% and each program
    /// checks it for itself.
    /// </summary>
    internal bool TryRunString(
        in JsValue receiver, string key, ObjectHandle stringPrototype, out JsValue result)
    {
        if (_p0 is { } p0 && p0.TryHitString(receiver, key, stringPrototype, out result)) return true;
        if (_p1 is { } p1 && p1.TryHitString(receiver, key, stringPrototype, out result)) return true;
        if (_p2 is { } p2 && p2.TryHitString(receiver, key, stringPrototype, out result)) return true;
        if (_p3 is { } p3 && p3.TryHitString(receiver, key, stringPrototype, out result)) return true;

        result = JsValue.Undefined;
        return false;
    }

    internal bool TryResolveStore(JsObject receiver, string key, out int slot)
    {
        if (receiver is ProxyObject)
        {
            slot = -1;
            return false;
        }

        var shape = receiver.CurrentShape;
        if (_p0 is { } p0 && p0.TryHitStore(receiver, shape, key, out slot)) return true;
        if (_p1 is { } p1 && p1.TryHitStore(receiver, shape, key, out slot)) return true;
        if (_p2 is { } p2 && p2.TryHitStore(receiver, shape, key, out slot)) return true;
        if (_p3 is { } p3 && p3.TryHitStore(receiver, shape, key, out slot)) return true;

        // The store counterpart of the megamorphic load path: an existing,
        // writable own data property, never an array's length.
        if (IsMegamorphic &&
            shape.TryGetSlot(key, out slot) &&
            receiver.IsWritableDataSlot(slot) &&
            !(receiver is ArrayObject && string.Equals(key, "length", StringComparison.Ordinal)))
        {
            return true;
        }

        slot = -1;
        return false;
    }

    /// <summary>True when some attached program already covers this receiver.</summary>
    internal bool Covers(Shape shape, string? key) =>
        (_p0 is { } a && a.Guards(shape, key)) ||
        (_p1 is { } b && b.Guards(shape, key)) ||
        (_p2 is { } c && c.Guards(shape, key)) ||
        (_p3 is { } d && d.Guards(shape, key));

    internal void Attach(CacheIRProgram program)
    {
        ArgumentNullException.ThrowIfNull(program);
        if (IsMegamorphic) return;

        // A program whose slot stopped being a plain data property can never hit
        // again. Attaching is the first moment after a miss, so it is where the
        // site sheds them.
        if (_p0 is { IsStale: true }) _p0 = null;
        if (_p1 is { IsStale: true }) _p1 = null;
        if (_p2 is { IsStale: true }) _p2 = null;
        if (_p3 is { IsStale: true }) _p3 = null;

        if (_p0 is null) { _p0 = program; return; }
        if (_p1 is null) { _p1 = program; return; }
        if (_p2 is null) { _p2 = program; return; }
        if (_p3 is null) { _p3 = program; return; }

        IsMegamorphic = true;
        _p0 = _p1 = _p2 = _p3 = null;
    }

    internal CacheIRProgram? ProgramAt(int index) => index switch
    {
        0 => _p0,
        1 => _p1,
        2 => _p2,
        3 => _p3,
        _ => null,
    };
}
