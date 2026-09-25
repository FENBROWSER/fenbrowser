using FenBrowser.Js.Objects;
using FenBrowser.Js.Runtime;

namespace FenBrowser.Js.Jit.CacheIR;

/// <summary>
/// Where a property access found an accessor - `get x()` / `set x(v)` on the
/// receiver or up its prototype chain - so the next access through the same
/// instruction can go straight to the function instead of repeating the
/// [[Get]] or [[Set]] walk.
/// </summary>
/// <remarks>
/// <para>
/// The guards mirror the deep prototype load: the receiver's shape (nothing of
/// its own shadows the name), then every prototype up to the holder, each the
/// same object in the same layout, pointing at the next. The accessor itself
/// is read out of the holder's slot on every use, so redefining it with
/// Object.defineProperty - which can keep the holder's shape - is still seen.
/// </para>
/// <para>
/// An intermediate prototype is only recorded when its shape does not name the
/// key at all, because re-adding a deleted property reuses its slot without a
/// new shape; and never one whose own properties its shape does not fully
/// describe.
/// </para>
/// </remarks>
internal sealed class AccessorStub
{
    private const int MaxPrototypeLinks = 8;

    private readonly Shape _receiverShape;
    private readonly ObjectHandle[] _handles;
    private readonly JsObject[] _protos;
    private readonly Shape[] _shapes;
    private readonly int _slot;
    private readonly bool _forSetter;

    private AccessorStub(Shape receiverShape, ObjectHandle[] handles, JsObject[] protos, Shape[] shapes, int slot, bool forSetter)
    {
        _receiverShape = receiverShape;
        _handles = handles;
        _protos = protos;
        _shapes = shapes;
        _slot = slot;
        _forSetter = forSetter;
    }

    /// <summary>
    /// A stub for <paramref name="key"/> on <paramref name="receiver"/> when it
    /// resolves to an accessor with the function this access needs, else null.
    /// </summary>
    internal static AccessorStub? TryCreate(
        JsObject receiver, string key, bool forSetter, Func<ObjectHandle, JsObject> resolve)
    {
        if (receiver is ProxyObject or ModuleNamespaceObject)
        {
            return null;
        }

        var receiverShape = receiver.CurrentShape;
        if (receiverShape.TryGetSlot(key, out var ownSlot))
        {
            return HasAccessor(receiver, ownSlot, forSetter)
                ? new AccessorStub(receiverShape, [], [], [], ownSlot, forSetter)
                : null;
        }

        if (receiver.MayGainOwnPropertyOutsideShape(key) || receiver.TryGetOwnProperty(key, out _))
        {
            return null;
        }

        var handles = new List<ObjectHandle>();
        var protos = new List<JsObject>();
        var shapes = new List<Shape>();
        for (var link = receiver.PrototypeHandle; link is { } handle; link = protos[^1].PrototypeHandle)
        {
            if (protos.Count == MaxPrototypeLinks)
            {
                return null;
            }

            var proto = resolve(handle);
            if (proto is ProxyObject or ModuleNamespaceObject)
            {
                return null;
            }

            handles.Add(handle);
            protos.Add(proto);
            shapes.Add(proto.CurrentShape);
            if (proto.CurrentShape.TryGetSlot(key, out var slot))
            {
                return HasAccessor(proto, slot, forSetter)
                    ? new AccessorStub(receiverShape, handles.ToArray(), protos.ToArray(), shapes.ToArray(), slot, forSetter)
                    : null;
            }

            if (proto.MayGainOwnPropertyOutsideShape(key) || proto.TryGetOwnProperty(key, out _))
            {
                return null;
            }
        }

        return null;
    }

    private static bool HasAccessor(JsObject holder, int slot, bool forSetter) =>
        holder.TryReadAccessorSlot(slot, out var getter, out var setter) &&
        (forSetter ? setter : getter).Tag == JsValueTag.Object;

    /// <summary>The getter or setter to call, or false when the guards no longer hold.</summary>
    internal bool TryGetAccessor(JsObject receiver, out JsValue accessor)
    {
        accessor = JsValue.Undefined;
        if (!ReferenceEquals(receiver.CurrentShape, _receiverShape))
        {
            return false;
        }

        var holder = receiver;
        var link = receiver.PrototypeHandle;
        for (var i = 0; i < _protos.Length; i++)
        {
            if (link is not { } handle || !handle.Equals(_handles[i]) ||
                !ReferenceEquals(_protos[i].CurrentShape, _shapes[i]))
            {
                return false;
            }

            holder = _protos[i];
            link = holder.PrototypeHandle;
        }

        if (!holder.TryReadAccessorSlot(_slot, out var getter, out var setter))
        {
            return false;
        }

        accessor = _forSetter ? setter : getter;
        return accessor.Tag == JsValueTag.Object;
    }
}
