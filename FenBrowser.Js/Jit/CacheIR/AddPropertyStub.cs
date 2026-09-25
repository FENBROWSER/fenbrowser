using FenBrowser.Js.Objects;
using FenBrowser.Js.Runtime;

namespace FenBrowser.Js.Jit.CacheIR;

/// <summary>
/// A store that adds a property: `this.x = v` in a constructor, `o.y = 1` on an
/// object that did not have it. Recorded after one such add went through the
/// full [[Set]], and replayed while nothing it depended on has changed.
/// </summary>
/// <remarks>
/// <para>
/// ECMA-262 10.1.9.2 OrdinarySetWithOwnDescriptor finds no own property, walks
/// the prototype chain for a setter or a read-only property of that name, and
/// only then creates a data property on the receiver. The walk is what made an
/// add cost several times an overwrite. It can be skipped when the chain is the
/// same objects in the same layouts as when the add was recorded and none of
/// them has the name at all: a prototype gaining a setter or a read-only
/// property of that name has to add it, which changes its shape.
/// </para>
/// <para>
/// "At all" includes a slot left behind by a deleted property: re-adding one
/// reuses the slot without a new shape, so a prototype whose shape still names
/// the key is never recorded. Only a plain object receiver qualifies, since an
/// exotic one may do more on an add than take a slot.
/// </para>
/// <para>
/// Kept apart from the CacheIR store programs on purpose: compiled code inlines
/// those, and this is a different operation.
/// </para>
/// </remarks>
internal sealed class AddPropertyStub
{
    private const int MaxChainLength = 8;

    private readonly Shape _before;
    private readonly Shape _after;
    private readonly ObjectHandle[] _protoHandles;
    private readonly JsObject[] _protos;
    private readonly Shape[] _protoShapes;

    private AddPropertyStub(Shape before, Shape after, ObjectHandle[] protoHandles, JsObject[] protos, Shape[] protoShapes)
    {
        _before = before;
        _after = after;
        _protoHandles = protoHandles;
        _protos = protos;
        _protoShapes = protoShapes;
    }

    /// <summary>
    /// A stub for the add that just took <paramref name="receiver"/> from
    /// <paramref name="before"/> to its current shape, or null when that add is
    /// not one this can replay.
    /// </summary>
    internal static AddPropertyStub? TryCreate(
        JsObject receiver, Shape before, string key, Func<ObjectHandle, JsObject> resolve)
    {
        if (receiver.GetType() != typeof(JsObject) || !receiver.Extensible)
        {
            return null;
        }

        var after = receiver.CurrentShape;
        if (!ReferenceEquals(after.Parent, before) ||
            !string.Equals(after.AddedProperty, key, StringComparison.Ordinal) ||
            after.AddedSlot != after.PropertyCount - 1 ||
            !receiver.TryGetOwnProperty(key, out var added) ||
            added.IsAccessor || !added.Writable || !added.Enumerable || !added.Configurable)
        {
            return null;
        }

        var handles = new List<ObjectHandle>();
        var protos = new List<JsObject>();
        var shapes = new List<Shape>();
        for (var handle = receiver.PrototypeHandle; handle is { } current; handle = protos[^1].PrototypeHandle)
        {
            if (protos.Count == MaxChainLength)
            {
                return null;
            }

            var proto = resolve(current);
            if (proto is ProxyObject or ModuleNamespaceObject || proto.CurrentShape.TryGetSlot(key, out _))
            {
                return null;
            }

            handles.Add(current);
            protos.Add(proto);
            shapes.Add(proto.CurrentShape);
        }

        return new AddPropertyStub(before, after, handles.ToArray(), protos.ToArray(), shapes.ToArray());
    }

    /// <summary>Adds the property as the recorded [[Set]] did, or returns false and touches nothing.</summary>
    internal bool TryAdd(JsObject receiver, JsValue value)
    {
        if (!ReferenceEquals(receiver.CurrentShape, _before) ||
            !receiver.Extensible ||
            receiver.GetType() != typeof(JsObject))
        {
            return false;
        }

        var link = receiver.PrototypeHandle;
        for (var i = 0; i < _protos.Length; i++)
        {
            if (link is not { } handle || !handle.Equals(_protoHandles[i]) ||
                !ReferenceEquals(_protos[i].CurrentShape, _protoShapes[i]))
            {
                return false;
            }

            link = _protos[i].PrototypeHandle;
        }

        if (link is not null)
        {
            return false;
        }

        receiver.AppendDataProperty(_after, value);
        return true;
    }
}
