// ECMA-262 §22.2.1.1 — Unicode character property database for \p{...} and \P{...} escapes.
//
// Maps Unicode property name + optional value to a sorted set of code point ranges.
// Lookup is O(log N) per character via binary search over flat uint arrays.
//
// Property names and values are case-insensitive per UTS#18. Short aliases
// (e.g. "gc" for General_Category, "sc" for Script, "Lu" for Uppercase_Letter)
// are resolved alongside their long forms.
//
// Data is derived from Unicode 15.1.0 and is sufficient for ECMA-262 conformance.

using System.Diagnostics.CodeAnalysis;

namespace FenBrowser.Js.Regex;

/// <summary>
/// Lookup table for Unicode character properties.
/// Thread-safe; all data is immutable after static initialization.
/// </summary>
public static class RegexUnicodeData
{
    // Each property is a sorted array of (start, end) inclusive code point pairs.
    // Using uint for efficient binary search; all values are ≤ 0x10FFFF.
    private static readonly Dictionary<string, uint[]> s_properties =
        new(StringComparer.OrdinalIgnoreCase);

    // Mapping from short alias to canonical name.
    private static readonly Dictionary<string, string> s_aliases =
        new(StringComparer.OrdinalIgnoreCase);

    static RegexUnicodeData()
    {
        InitializeGeneralCategory();
        InitializeScripts();
        InitializeBinaryProperties();
        InitializeAliases();
    }

    /// <summary>
    /// Returns true if the code point has the given Unicode property.
    /// Property can be a binary property name (e.g. "Emoji", "Alphabetic"),
    /// or a General_Category value (e.g. "Letter", "Lu").
    /// The value parameter is for property=value pairs: e.g. ("General_Category", "Letter")
    /// or ("Script", "Latin"). For binary properties, value is null.
    /// </summary>
    public static bool HasProperty(int codePoint, string property, string? value)
    {
        if (codePoint < 0 || codePoint > 0x10FFFF) return false;

        // Canonicalise the property name via aliases.
        var canonicalProp = ResolveAlias(property);

        if (value != null)
        {
            // Property=Value form: "General_Category=Letter", "Script=Latin", etc.
            var canonicalValue = ResolveAlias(value);
            var key = canonicalProp + "=" + canonicalValue;
            if (s_properties.TryGetValue(key, out var ranges))
                return InRanges((uint)codePoint, ranges);
            return false;
        }

        // Binary property or General_Category value used as binary.
        if (s_properties.TryGetValue(canonicalProp, out var binRanges))
            return InRanges((uint)codePoint, binRanges);

        // Try as General_Category value (e.g. just "Letter" instead of "General_Category=Letter").
        var gcKey = s_gcCanonical + "=" + canonicalProp;
        if (s_properties.TryGetValue(gcKey, out var gcRanges))
            return InRanges((uint)codePoint, gcRanges);

        // Try as Script value.
        var scKey = s_scCanonical + "=" + canonicalProp;
        if (s_properties.TryGetValue(scKey, out var scRanges))
            return InRanges((uint)codePoint, scRanges);

        return false;
    }

    /// <summary>
    /// Returns all code points matching the given property as a list of (start,end) inclusive ranges.
    /// Used by the regex compiler to build character class sets.
    /// </summary>
    public static IReadOnlyList<(uint Start, uint End)> GetPropertyRanges(string property, string? value)
    {
        var canonicalProp = ResolveAlias(property);
        uint[]? ranges = null;

        if (value != null)
        {
            var canonicalValue = ResolveAlias(value);
            s_properties.TryGetValue(canonicalProp + "=" + canonicalValue, out ranges);
        }
        else
        {
            if (!s_properties.TryGetValue(canonicalProp, out ranges))
            {
                s_properties.TryGetValue(s_gcCanonical + "=" + canonicalProp, out ranges);
            }
        }

        if (ranges == null)
            return Array.Empty<(uint, uint)>();

        var result = new List<(uint, uint)>(ranges.Length / 2);
        for (var i = 0; i < ranges.Length; i += 2)
            result.Add((ranges[i], ranges[i + 1]));
        return result;
    }

    // ─── Internal helpers ─────────────────────────────────────────

    private static string ResolveAlias(string name) =>
        s_aliases.TryGetValue(name, out var canonical) ? canonical : name;

    private static bool InRanges(uint cp, uint[] ranges)
    {
        // Binary search: each pair is (start, end) inclusive, sorted by start.
        var lo = 0;
        var hi = ranges.Length / 2 - 1;
        while (lo <= hi)
        {
            var mid = (lo + hi) / 2;
            var start = ranges[mid * 2];
            var end = ranges[mid * 2 + 1];
            if (cp < start) hi = mid - 1;
            else if (cp > end) lo = mid + 1;
            else return true;
        }
        return false;
    }

    [SuppressMessage("Microsoft.Globalization", "CA1308", Justification = "Case-insensitive canonicalization per UTS#18")]
    private static void Add(string key, uint[] ranges)
    {
        s_properties[key.ToLowerInvariant()] = ranges;
    }

    // Canonical property names for General_Category and Script.
    private const string s_gcCanonical = "general_category";
    private const string s_scCanonical = "script";

    // ─── General_Category ─────────────────────────────────────────
    // ECMA-262 §22.2.1.1 — loaded from embedded JSON data (Unicode 15.1.0).

    private static void InitializeGeneralCategory()
    {
        // Load all General_Category=* keys from the JSON database.
        foreach (var key in UnicodePropertyEscapeData.GetPropertyKeys())
        {
            if (!key.StartsWith("General_Category=", StringComparison.Ordinal))
                continue;
            var ranges = UnicodePropertyEscapeData.GetRanges(key);
            if (ranges is not { Length: > 0 }) continue;

            var name = key.Substring("General_Category=".Length);
            Add("general_category=" + name, ranges);
        }
    }

    // ─── Script ───────────────────────────────────────────────────

    private static void InitializeScripts()
    {
        // Load all Script=* keys from the JSON database.
        foreach (var key in UnicodePropertyEscapeData.GetPropertyKeys())
        {
            if (!key.StartsWith("Script=", StringComparison.Ordinal))
                continue;
            var ranges = UnicodePropertyEscapeData.GetRanges(key);
            if (ranges is not { Length: > 0 }) continue;

            var name = key.Substring("Script=".Length);
            Add("script=" + name, ranges);
        }
    }

    // ─── Binary Properties ────────────────────────────────────────

    private static void InitializeBinaryProperties()
    {
        // Load all binary (non-namespaced) properties from the JSON database.
        foreach (var key in UnicodePropertyEscapeData.GetPropertyKeys())
        {
            if (key.Contains('=')) continue; // skip General_Category=, Script=, etc.
            var ranges = UnicodePropertyEscapeData.GetRanges(key);
            if (ranges is not { Length: > 0 }) continue;

            Add(key, ranges);
        }
    }

    // ─── Alias Resolution ─────────────────────────────────────────

    private static void InitializeAliases()
    {
        // General_Category aliases (per UTS#18)
        AddAlias("gc", s_gcCanonical);
        AddAlias("generalcategory", s_gcCanonical);
        AddAlias("general category", s_gcCanonical);

        // Script aliases
        AddAlias("sc", s_scCanonical);
        AddAlias("scriptextensions", "script_extensions");

        // General_Category value aliases (short form)
        AddAlias("uppercase_letter", "Lu"); AddAlias("lowercase_letter", "Ll");
        AddAlias("titlecase_letter", "Lt"); AddAlias("modifier_letter", "Lm");
        AddAlias("other_letter", "Lo");     AddAlias("letter", "L");
        AddAlias("cased_letter", "LC");

        AddAlias("nonspacing_mark", "Mn"); AddAlias("spacing_mark", "Mc");
        AddAlias("enclosing_mark", "Me");  AddAlias("mark", "M");
        AddAlias("combining_mark", "M");

        AddAlias("decimal_number", "Nd"); AddAlias("letter_number", "Nl");
        AddAlias("other_number", "No");    AddAlias("number", "N");

        AddAlias("connector_punctuation", "Pc"); AddAlias("dash_punctuation", "Pd");
        AddAlias("open_punctuation", "Ps"); AddAlias("close_punctuation", "Pe");
        AddAlias("initial_punctuation", "Pi"); AddAlias("final_punctuation", "Pf");
        AddAlias("other_punctuation", "Po"); AddAlias("punctuation", "P");

        AddAlias("math_symbol", "Sm"); AddAlias("currency_symbol", "Sc");
        AddAlias("modifier_symbol", "Sk"); AddAlias("other_symbol", "So");
        AddAlias("symbol", "S");

        AddAlias("space_separator", "Zs"); AddAlias("line_separator", "Zl");
        AddAlias("paragraph_separator", "Zp"); AddAlias("separator", "Z");

        AddAlias("control", "Cc"); AddAlias("format", "Cf");
        AddAlias("surrogate", "Cs"); AddAlias("private_use", "Co");
        AddAlias("unassigned", "Cn"); AddAlias("other", "C");

        // Binary property aliases
        AddAlias("alpha", "Alphabetic");
        AddAlias("ascii_hex_digit", "Hex_Digit");
    }

    private static void AddAlias(string alias, string canonical)
    {
        s_aliases[alias] = canonical;
    }

}
