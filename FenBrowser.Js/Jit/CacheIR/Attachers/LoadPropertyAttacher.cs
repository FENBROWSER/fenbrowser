using FenBrowser.Js.Objects;
using FenBrowser.Js.Runtime;

namespace FenBrowser.Js.Jit.CacheIR.Attachers;

/// <summary>
/// Builds cache programs for reading a named property off an object.
/// </summary>
/// <remarks>
/// This is the only place that decides what a property read may assume, and it
/// attaches nothing user code can observe: no proxy, no module namespace, no
/// accessor, nothing off the prototype chain. Everything it refuses stays on
/// the general path, where the specification's ordering still holds.
/// </remarks>
internal static class LoadPropertyAttacher
{
    /// <param name="keyVariesAtSite">
    /// True for sites whose key is a runtime value (<c>obj[k]</c>), which must
    /// therefore guard it. A site reading a fixed name has the key baked into
    /// the bytecode and pays no comparison for it.
    /// </param>
    /// <param name="resolve">
    /// Turns a heap handle into the object it names. The cache layer knows
    /// nothing of the heap otherwise, and a prototype is reached by handle.
    /// </param>
    internal static CacheIRProgram? TryAttach(
        JsObject receiver, string key, bool keyVariesAtSite, Func<ObjectHandle, JsObject>? resolve = null)
    {
        if (receiver is ProxyObject or ModuleNamespaceObject) return null;

        // A dense array's length is the vector's count, not a property in its
        // shape, so no shape guard can reach it and every read of it missed.
        // It is the single most-read name on a real page - 1.76 million of the
        // 4.16 million missed reads in one reCAPTCHA run, and nothing else in
        // that table was above 40 thousand.
        if (receiver is ArrayObject { IsDense: true } &&
            string.Equals(key, "length", StringComparison.Ordinal))
        {
            var lengthWriter = new CacheIRWriter();
            lengthWriter.GuardDenseArray();
            lengthWriter.LoadArrayLengthResult();
            return lengthWriter.Build();
        }

        var shape = receiver.CurrentShape;
        if (shape.TryGetSlot(key, out var slot) && receiver.TryReadDataSlot(slot, out _))
        {
            var writer = new CacheIRWriter();
            writer.GuardNotExotic();
            writer.GuardShape(shape);
            if (keyVariesAtSite) writer.GuardKey(key);
            writer.LoadSlotResult(slot);
            return writer.Build();
        }

        // Not the receiver's own - so the immediate prototype, which is where a
        // method call on a class instance, an array or an object literal finds
        // what it is calling. Everything deeper stays on the general path: one
        // link is what covers the shapes real code has.
        return TryAttachPrototypeLoad(receiver, key, keyVariesAtSite, resolve);
    }

    /// <summary>
    /// A read that lands on the receiver's immediate prototype.
    /// </summary>
    /// <remarks>
    /// Three guards make it safe, and each catches a different way the answer
    /// could move. The receiver's shape catches an own property appearing that
    /// would shadow the name. Its [[Prototype]] catches the chain being
    /// reassigned - shapes here do not encode the prototype, so nothing else
    /// would. The holder's shape catches the property being added, removed or
    /// relaid-out on the prototype itself.
    ///
    /// The holder is then read directly. Holding it is safe because the second
    /// guard proves the receiver still points at it: a handle carries a
    /// generation as well as an index, so a cell that had been freed and reused
    /// would not match.
    /// </remarks>
    private static CacheIRProgram? TryAttachPrototypeLoad(
        JsObject receiver, string key, bool keyVariesAtSite, Func<ObjectHandle, JsObject>? resolve)
    {
        if (resolve is null || receiver.PrototypeHandle is not { } protoHandle) return null;

        // The receiver must genuinely not have the name. Falling through to the
        // prototype because the own property was not a *readable data slot* is
        // wrong twice over: an own accessor shadows the prototype and has to be
        // called, and an own property the shape does not describe cannot be
        // guarded against appearing later.
        if (receiver.MayGainOwnPropertyOutsideShape(key)) return null;
        if (receiver.TryGetOwnProperty(key, out _)) return null;

        var holder = resolve(protoHandle);
        if (holder is ProxyObject or ModuleNamespaceObject) return null;

        var holderShape = holder.CurrentShape;
        if (!holderShape.TryGetSlot(key, out var holderSlot)) return null;
        if (!holder.TryReadDataSlot(holderSlot, out _)) return null;

        var writer = new CacheIRWriter();
        writer.GuardNotExotic();
        writer.GuardShape(receiver.CurrentShape);
        if (keyVariesAtSite) writer.GuardKey(key);
        writer.LoadFromPrototype(protoHandle, holder, holderShape, holderSlot);
        return writer.Build();
    }
}
