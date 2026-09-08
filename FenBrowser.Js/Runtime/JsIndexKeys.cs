using System.Globalization;

namespace FenBrowser.Js.Runtime;

/// <summary>
/// The property-key strings for small array indices, made once.
/// </summary>
/// <remarks>
/// Every array-like traversal that goes through the ordinary property path has
/// to name each index, and <c>i.ToString()</c> allocates a fresh string for it.
/// On a page whose code passes arrays through <c>Function.prototype.apply</c>
/// and builds an <c>arguments</c> object on most calls, that is a string per
/// element per call, and the strings are drawn from a tiny set that never
/// changes.
///
/// The table covers the range real code indexes; anything above it formats as
/// before. The strings are ordinary interned literals as far as callers are
/// concerned — equality and hashing are unchanged, so a cached key behaves
/// exactly like a formatted one.
/// </remarks>
internal static class JsIndexKeys
{
    private const int CachedCount = 4096;

    private static readonly string[] Cache = BuildCache();

    private static string[] BuildCache()
    {
        var cache = new string[CachedCount];
        for (var i = 0; i < CachedCount; i++)
        {
            cache[i] = i.ToString(CultureInfo.InvariantCulture);
        }

        return cache;
    }

    /// <summary>The canonical property key for <paramref name="index"/>.</summary>
    public static string For(int index) =>
        (uint)index < (uint)CachedCount
            ? Cache[index]
            : index.ToString(CultureInfo.InvariantCulture);

    /// <summary>The canonical property key for <paramref name="index"/>.</summary>
    public static string For(uint index) =>
        index < (uint)CachedCount
            ? Cache[(int)index]
            : index.ToString(CultureInfo.InvariantCulture);

    /// <summary>The canonical property key for <paramref name="index"/>.</summary>
    public static string For(long index) =>
        (ulong)index < (ulong)CachedCount
            ? Cache[(int)index]
            : index.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// The canonical property key for <paramref name="index"/>. Only a
    /// non-negative integral value inside the table is answered from it;
    /// everything else formats exactly as it did before, so a fractional or
    /// out-of-range length still produces the same key it always did.
    /// </summary>
    public static string For(double index)
    {
        if (index >= 0 && index < CachedCount)
        {
            var truncated = (int)index;
            if (truncated == index)
            {
                return Cache[truncated];
            }
        }

        return index.ToString(CultureInfo.InvariantCulture);
    }
}
