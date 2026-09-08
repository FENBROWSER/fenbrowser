using FenBrowser.Js.Objects;

namespace FenBrowser.Js.Jit.CacheIR.Attachers;

/// <summary>
/// Builds cache programs for writing a named property on an object.
/// </summary>
/// <remarks>
/// Stricter than the load attacher, because a write that takes a shortcut is
/// observable in more ways: only a writable, non-accessor own data property
/// qualifies, never through a proxy, and never an array's length, whose write
/// may delete elements.
/// </remarks>
internal static class StorePropertyAttacher
{
    internal static CacheIRProgram? TryAttach(JsObject receiver, string key, bool keyVariesAtSite)
    {
        if (receiver is ProxyObject) return null;

        var guardsArrayLength = string.Equals(key, "length", StringComparison.Ordinal);
        if (guardsArrayLength && receiver is ArrayObject) return null;

        var shape = receiver.CurrentShape;
        if (!shape.TryGetSlot(key, out var slot)) return null;

        var properties = receiver.PropertyArray;
        if ((uint)slot >= (uint)properties.Length ||
            properties[slot] is not { } descriptor ||
            descriptor.IsAccessor ||
            !descriptor.Writable)
        {
            return null;
        }

        var writer = new CacheIRWriter();
        writer.GuardNotProxy();
        writer.GuardShape(shape);
        if (keyVariesAtSite) writer.GuardKey(key);
        if (guardsArrayLength) writer.GuardNotArray();
        writer.StoreSlotResult(slot);
        return writer.Build();
    }
}
