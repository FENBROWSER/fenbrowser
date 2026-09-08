using FenBrowser.Js.Objects;

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
    internal static CacheIRProgram? TryAttach(JsObject receiver, string key, bool keyVariesAtSite)
    {
        if (receiver is ProxyObject or ModuleNamespaceObject) return null;

        var shape = receiver.CurrentShape;
        if (!shape.TryGetSlot(key, out var slot)) return null;

        var properties = receiver.PropertyArray;
        if ((uint)slot >= (uint)properties.Length ||
            properties[slot] is not { } descriptor ||
            descriptor.IsAccessor)
        {
            return null;
        }

        var writer = new CacheIRWriter();
        writer.GuardNotExotic();
        writer.GuardShape(shape);
        if (keyVariesAtSite) writer.GuardKey(key);
        writer.LoadSlotResult(slot);
        return writer.Build();
    }
}
