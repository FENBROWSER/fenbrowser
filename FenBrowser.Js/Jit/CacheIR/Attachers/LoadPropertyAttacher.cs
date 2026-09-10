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
            if (keyVariesAtSite) lengthWriter.GuardKey(key);
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
    /// Builds a program for a read off a string primitive.
    /// </summary>
    /// <remarks>
    /// A string is not an object: it has no shape, so every read off one missed
    /// every site, and on a real page those reads are not rare - `length` and
    /// `charCodeAt` between them were most of what still missed after the
    /// object forms were in.
    ///
    /// What makes it cacheable is that a string primitive's own properties are
    /// exactly `length` and its integer indices, and that set can never grow.
    /// Every other name resolves on the realm's %String.prototype%, so once the
    /// key is neither of those there is no receiver state left to guard - only
    /// which realm's prototype answered, and whether that object still holds the
    /// name in the same slot.
    /// </remarks>
    /// <param name="stringPrototype">The realm's %String.prototype%.</param>
    internal static CacheIRProgram? TryAttachString(
        JsObject stringPrototype, ObjectHandle stringPrototypeHandle, string key, bool keyVariesAtSite)
    {
        if (key.Length == 0) return null;

        if (string.Equals(key, "length", StringComparison.Ordinal))
        {
            var lengthWriter = new CacheIRWriter();
            lengthWriter.GuardStringReceiver();
            if (keyVariesAtSite) lengthWriter.GuardKey(key);
            lengthWriter.LoadStringLengthResult();
            return lengthWriter.Build();
        }

        // An index is the string's own, and which character it names depends on
        // the receiver - so it is not this site's to answer. Refusing every key
        // that merely begins with a digit is wider than the canonical-index
        // rule and errs the safe way: those names resolve on the prototype to
        // undefined, and letting them take the general path costs nothing.
        if (key[0] is >= '0' and <= '9') return null;

        if (stringPrototype is ProxyObject or ModuleNamespaceObject) return null;

        var holderShape = stringPrototype.CurrentShape;
        if (!holderShape.TryGetSlot(key, out var holderSlot)) return null;
        if (!stringPrototype.TryReadDataSlot(holderSlot, out _)) return null;

        var writer = new CacheIRWriter();
        writer.GuardStringReceiver();
        if (keyVariesAtSite) writer.GuardKey(key);
        writer.LoadFromStringPrototype(stringPrototypeHandle, stringPrototype, holderShape, holderSlot);
        return writer.Build();
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
