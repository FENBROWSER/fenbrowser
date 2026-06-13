namespace FenBrowser.Js.Temporal;

// ECMA-402 / Temporal calendar identifier handling: the CLDR calendar
// key set, ASCII-case-insensitive lookup, alias canonicalization.
internal static class TemporalCalendars
{
    private static readonly Dictionary<string, string> _canonical = BuildCanonicalMap();

    private static Dictionary<string, string> BuildCanonicalMap()
    {
        string[] ids =
        {
            "buddhist", "chinese", "coptic", "dangi", "ethioaa", "ethiopic",
            "gregory", "hebrew", "indian", "islamic-civil",
            "islamic-tbla", "islamic-umalqura", "iso8601",
            "japanese", "persian", "roc",
        };
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in ids)
        {
            map[id] = id;
        }

        // CLDR aliases.
        map["islamicc"] = "islamic-civil";
        map["ethiopic-amete-alem"] = "ethioaa";
        return map;
    }

    // Temporal CanonicalizeCalendar: case-insensitive lookup against the
    // available calendars; returns the canonical (lowercase) form or null
    // when the identifier is not a supported calendar.
    public static string? Canonicalize(string identifier)
        => _canonical.TryGetValue(identifier, out var canonical) ? canonical : null;
}
