using System.Collections.Concurrent;

namespace FenBrowser.Js.Runtime;

public sealed class StringInterner
{
    private readonly ConcurrentDictionary<string, string> _interned = new(StringComparer.Ordinal);
    public string Intern(string value) => _interned.GetOrAdd(value, value);
    public string Intern(ReadOnlySpan<char> value) => Intern(value.ToString());
    public int Count => _interned.Count;
    public void Clear() => _interned.Clear();
}
