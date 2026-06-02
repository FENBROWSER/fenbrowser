using System.Linq;
using System.Reflection;
using System.Text.Json;

namespace FenBrowser.Js.Regex;

internal static class UnicodePropertyEscapeData
{
    public static bool IsLoaded
    {
        get
        {
            try { return s_data.IsValueCreated && s_data.Value.Count > 0; }
            catch { return false; }
        }
    }

    /// <summary>Force-load the data. Returns error message or null on success.</summary>
    public static string? EnsureLoaded()
    {
        if (s_data.IsValueCreated && s_data.Value.Count > 0) return null;
        try { var _ = s_data.Value; return s_data.Value.Count > 0 ? null : "Data empty"; }
        catch (Exception ex) { return ex.Message; }
    }

    /// <summary>Get the code point ranges for a property key. Returns null if not found.</summary>
    public static uint[]? GetRanges(string key)
    {
        try
        {
            EnsureLoaded();
            s_data.Value.TryGetValue(key, out var ranges);
            return ranges;
        }
        catch { return null; }
    }

    /// <summary>Get all loaded property keys.</summary>
    public static IEnumerable<string> GetPropertyKeys()
    {
        try
        {
            EnsureLoaded();
            return s_data.Value.Keys;
        }
        catch { return Array.Empty<string>(); }
    }

    private static readonly Lazy<Dictionary<string, uint[]>> s_data =
        new(Load, LazyThreadSafetyMode.ExecutionAndPublication);

    public static bool TryHasPropertyCodePoint(string body, int codePoint, out bool hasProperty)
    {
        hasProperty = false;
        if (codePoint < 0 || codePoint > 0x10FFFF)
        {
            return false;
        }

        try
        {
            // Try exact body first (e.g. "ASCII", "Letter", "Latin", "General_Category=Letter")
            if (s_data.Value.TryGetValue(body, out var ranges))
            {
                hasProperty = InRanges((uint)codePoint, ranges);
                return true;
            }

            // Try stripping namespace prefix: "General_Category=Letter" → "Letter"
            var eqIdx = body.IndexOf('=');
            if (eqIdx > 0 && eqIdx < body.Length - 1)
            {
                var value = body.Substring(eqIdx + 1);
                if (s_data.Value.TryGetValue(value, out var valueRanges))
                {
                    hasProperty = InRanges((uint)codePoint, valueRanges);
                    return true;
                }
            }
        }
        catch
        {
            // Fall through to hardcoded data
        }

        // Hardcoded fallback for common properties (ASCII, Letter, etc.)
        if (TryHardcodedProperty(body, codePoint, out hasProperty))
            return true;

        return false; // property not in database
    }

    private static bool TryHardcodedProperty(string body, int codePoint, out bool hasProperty)
    {
        hasProperty = false;
        // Strip namespace prefix
        var key = body;
        var eqIdx = body.IndexOf('=');
        if (eqIdx > 0) key = body.Substring(eqIdx + 1);
        key = key.ToLowerInvariant();

        switch (key)
        {
            case "ascii": hasProperty = codePoint <= 0x7F; return true;
            case "ascii_hex_digit": case "ahex":
                hasProperty = (codePoint >= '0' && codePoint <= '9') || (codePoint >= 'A' && codePoint <= 'F') || (codePoint >= 'a' && codePoint <= 'f');
                return true;
            case "alphabetic": case "alpha":
                hasProperty = (codePoint >= 'A' && codePoint <= 'Z') || (codePoint >= 'a' && codePoint <= 'z');
                return true;
            case "any": case "assigned": hasProperty = codePoint <= 0x10FFFF; return true;
            case "hex_digit": case "hex":
                hasProperty = (codePoint >= '0' && codePoint <= '9') || (codePoint >= 'A' && codePoint <= 'F') || (codePoint >= 'a' && codePoint <= 'f');
                return true;
            case "white_space": case "wspace":
                hasProperty = codePoint == 0x20 || codePoint == 0x09 || codePoint == 0x0A || codePoint == 0x0D || codePoint == 0x0B || codePoint == 0x0C || codePoint == 0xA0;
                return true;
            case "letter": case "l":
                hasProperty = (codePoint >= 'A' && codePoint <= 'Z') || (codePoint >= 'a' && codePoint <= 'z');
                return true;
            case "uppercase_letter": case "lu":
                hasProperty = codePoint >= 'A' && codePoint <= 'Z'; return true;
            case "lowercase_letter": case "ll":
                hasProperty = codePoint >= 'a' && codePoint <= 'z'; return true;
            case "decimal_number": case "nd": case "digit":
                hasProperty = codePoint >= '0' && codePoint <= '9'; return true;
            case "emoji": hasProperty = false; return true; // stub
            case "emoji_presentation": case "epres": hasProperty = false; return true;
        }
        return false; // not in hardcoded set
    }

    private static bool InRanges(uint codePoint, uint[] ranges)
    {
        var lo = 0;
        var hi = (ranges.Length / 2) - 1;
        while (lo <= hi)
        {
            var mid = (lo + hi) / 2;
            var start = ranges[mid * 2];
            var end = ranges[mid * 2 + 1];
            if (codePoint < start)
            {
                hi = mid - 1;
                continue;
            }

            if (codePoint > end)
            {
                lo = mid + 1;
                continue;
            }

            return true;
        }

        return false;
    }

    private static Dictionary<string, uint[]> Load()
    {
        const string resourceName = "FenBrowser.Js.Regex.unicode_property_escapes.generated.json";
        var assembly = typeof(UnicodePropertyEscapeData).Assembly;
        var allNames = assembly.GetManifestResourceNames();

        // Try exact match first, then fall back to suffix match
        Stream? stream = assembly.GetManifestResourceStream(resourceName);
        if (stream == null)
        {
            var fallback = allNames.FirstOrDefault(n =>
                n.Contains("unicode_property_escapes", StringComparison.OrdinalIgnoreCase));
            if (fallback != null)
                stream = assembly.GetManifestResourceStream(fallback);
        }
        if (stream == null)
            throw new InvalidOperationException(
                $"Missing embedded resource: {resourceName}. Available: {string.Join(", ", allNames)}");

        using var document = JsonDocument.Parse(stream);

        var map = new Dictionary<string, uint[]>(StringComparer.Ordinal);
        foreach (var property in document.RootElement.EnumerateObject())
        {
            var pairs = property.Value;
            var flat = new uint[pairs.GetArrayLength() * 2];
            var i = 0;
            foreach (var pair in pairs.EnumerateArray())
            {
                flat[i++] = pair[0].GetUInt32();
                flat[i++] = pair[1].GetUInt32();
            }

            map[property.Name] = flat;
        }

        return map;
    }
}
