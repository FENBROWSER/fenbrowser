using System.Collections.Concurrent;

namespace FenBrowser.Js.Runtime;

/// <summary>
/// Per-runtime string canonicalization for hot identifiers/property names.
/// Interning is an optimization only: JavaScript string equality does not depend on
/// CLR reference identity, so attacker-controlled strings must not be able to turn
/// this helper into an unbounded lifetime cache.
/// </summary>
public sealed class StringInterner
{
    private const int MaxInternedEntries = 65_536;
    private const int MaxInternedStringLength = 256;

    private readonly ConcurrentDictionary<string, string> _interned = new(StringComparer.Ordinal);

    public string Intern(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        // Long payload strings have little reuse value and can be arbitrarily large.
        // Once the bounded table is saturated, preserving the caller's string is
        // semantically equivalent to interning it without retaining another root.
        if (value.Length > MaxInternedStringLength)
        {
            return value;
        }

        if (_interned.TryGetValue(value, out var existing))
        {
            return existing;
        }

        if (_interned.Count >= MaxInternedEntries)
        {
            return value;
        }

        return _interned.GetOrAdd(value, static key => key);
    }

    public string Intern(ReadOnlySpan<char> value)
    {
        if (value.Length > MaxInternedStringLength)
        {
            return value.ToString();
        }

        return Intern(value.ToString());
    }

    public int Count => _interned.Count;

    public void Clear() => _interned.Clear();
}
