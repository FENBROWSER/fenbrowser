using NodaTime;
using NodaTime.TimeZones;

namespace FenBrowser.Js.Temporal;

// Temporal time zone support: identifier validation/canonicalization,
// offset lookup at an instant, and wall-clock → epoch resolution
// (tc39.es/proposal-temporal sec-temporal-timezone-abstract-ops).
//
// Offset identifiers ("+05:30") are exact. Named zones resolve through
// System.TimeZoneInfo (IANA ids via ICU); instants outside DateTimeOffset's
// year 1-9999 window use the offset clamped at the nearest representable
// instant, which is exact for fixed-offset zones and the modern era.
internal static class TemporalTimeZones
{
    private const long NsPerDay = 86_400_000_000_000L;

    private static readonly Dictionary<string, string> _tzdbIds = BuildTzdbIdMap();

    private static Dictionary<string, string> BuildTzdbIdMap()
    {
        var source = TzdbDateTimeZoneSource.Default;
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in DateTimeZoneProviders.Tzdb.Ids)
        {
            result[id] = source.CanonicalIdMap.TryGetValue(id, out var canonical) ? canonical : id;
        }

        return result;
    }

    /// <summary>
    /// Validate and canonicalize a time zone identifier. Returns false for
    /// unknown identifiers. Offset identifiers normalize to "±HH:MM" and
    /// report their fixed offset via <paramref name="fixedOffsetNs"/>
    /// (null for named zones).
    /// </summary>
    public static bool TryCanonicalize(string id, out string canonical, out long? fixedOffsetNs)
    {
        canonical = "";
        fixedOffsetNs = null;
        if (string.IsNullOrEmpty(id))
        {
            return false;
        }

        if (string.Equals(id, "UTC", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(id, "GMT", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(id, "Etc/UTC", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(id, "Etc/GMT", StringComparison.OrdinalIgnoreCase))
        {
            canonical = "UTC";
            return true;
        }

        // Offset time zone: sign HH[:MM] — minute precision only.
        if (id[0] == '+' || id[0] == '-')
        {
            int i = 0;
            string err = "";
            if (!TemporalIsoParser.TryParseUtcOffset(id, ref i, subMinutePrecision: false, out var offsetNs, ref err)
                || i != id.Length)
            {
                return false;
            }

            long minutes = offsetNs / 60_000_000_000L;
            long absMinutes = Math.Abs(minutes);
            canonical = $"{(minutes < 0 ? "-" : "+")}{absMinutes / 60:D2}:{absMinutes % 60:D2}";
            fixedOffsetNs = offsetNs;
            return true;
        }

        // A bare "Z" or a date-time string is not a time zone identifier.
        if (!IsIanaNameShape(id))
        {
            return false;
        }

        // Resolve IANA link→canonical before .NET lookup (Windows may not resolve links).
        if (_tzdbIds.TryGetValue(id, out var canonicalId))
        {
            canonical = canonicalId;
            return true;
        }

        return false;
    }

    // TZLeadingChar TZChar* components separated by '/'; rejects strings
    // with 'T', digits-only starts, etc. so date-time strings don't pass.
    private static bool IsIanaNameShape(string id)
    {
        foreach (var part in id.Split('/'))
        {
            if (part.Length == 0)
            {
                return false;
            }

            char c0 = part[0];
            if (!(char.IsAsciiLetter(c0) || c0 == '.' || c0 == '_'))
            {
                return false;
            }

            for (int k = 1; k < part.Length; k++)
            {
                char c = part[k];
                if (!(char.IsAsciiLetterOrDigit(c) || c == '.' || c == '-' || c == '_' || c == '+'))
                {
                    return false;
                }
            }
        }

        // Single-component names must not be bare offsets-like or 'Z'.
        return !string.Equals(id, "Z", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>GetOffsetNanosecondsFor: UTC offset of the zone at an epoch instant.</summary>
    public static long GetOffsetNs(string canonicalId, long epochNs)
    {
        if (canonicalId.Length > 0 && (canonicalId[0] == '+' || canonicalId[0] == '-'))
        {
            int i = 0;
            string err = "";
            _ = TemporalIsoParser.TryParseUtcOffset(canonicalId, ref i, subMinutePrecision: false, out var offsetNs, ref err);
            return offsetNs;
        }

        if (canonicalId == "UTC")
        {
            return 0;
        }

        var zone = DateTimeZoneProviders.Tzdb.GetZoneOrNull(canonicalId);
        if (zone is not null)
        {
            return zone.GetUtcOffset(Instant.FromUnixTimeTicks(epochNs / 100L)).Seconds * 1_000_000_000L;
        }

        return 0;
    }

    /// <summary>
    /// GetEpochNanosecondsFor with disambiguation "compatible": resolve a
    /// wall-clock date/time in the zone to an epoch instant.
    /// </summary>
    public static long EpochNsFromWall(string canonicalId, IsoDate date, IsoTime time)
    {
        // long holds ±106,751 days of nanoseconds (~±292 years); clamp wall
        // instants beyond that so the day multiply below cannot overflow.
        long days = Math.Clamp(IsoMath.ToEpochDays(date), -106_751L, 106_751L);
        long wallNs = days * NsPerDay + time.ToNanosecondsOfDay();
        long offset1 = GetOffsetNs(canonicalId, wallNs);
        long candidate = wallNs - offset1;
        long offset2 = GetOffsetNs(canonicalId, candidate);
        if (offset2 == offset1)
        {
            return candidate;
        }

        // Around a transition: re-resolve once with the candidate's offset.
        // For a gap this lands after the transition (compatible behavior);
        // for an overlap it picks the earlier offset's instant.
        return wallNs - offset2;
    }

    /// <summary>Split an epoch instant into wall-clock date/time at the given offset.</summary>
    public static (IsoDate Date, IsoTime Time) WallFromEpochNs(long epochNs, long offsetNs)
    {
        long local = epochNs + offsetNs;
        long days = (long)Math.Floor(local / (double)NsPerDay);
        long timeNs = local - days * NsPerDay;
        var date = IsoMath.EpochDaysToCivil(days);
        var time = new IsoTime(
            (int)(timeNs / 3_600_000_000_000L), (int)(timeNs / 60_000_000_000L % 60), (int)(timeNs / 1_000_000_000L % 60),
            (int)(timeNs / 1_000_000L % 1000), (int)(timeNs / 1_000L % 1000), (int)(timeNs % 1000));
        return (date, time);
    }

    /// <summary>"±HH:MM" display form of an offset (rounded toward zero to minutes).</summary>
    public static string FormatOffset(long offsetNs)
    {
        long minutes = offsetNs / 60_000_000_000L;
        long absMinutes = Math.Abs(minutes);
        return $"{(offsetNs < 0 ? "-" : "+")}{absMinutes / 60:D2}:{absMinutes % 60:D2}";
    }

    /// <summary>
    /// BigInteger version of EpochNsFromWall that handles the full Temporal
    /// date range. For UTC the offset is always zero; for named zones outside
    /// the ±292-year window no DST data exists, so offset is treated as zero.
    /// </summary>
    public static System.Numerics.BigInteger EpochNsFromWallBig(string canonicalId, IsoDate date, IsoTime time)
    {
        long days = IsoMath.ToEpochDays(date);
        var wallNs = new System.Numerics.BigInteger(days) * NsPerDay + time.ToNanosecondsOfDay();

        // Offset at this instant; for UTC/offsets this is exact regardless.
        long offsetNs = 0;
        if (wallNs >= long.MinValue && wallNs <= long.MaxValue)
        {
            offsetNs = GetOffsetNs(canonicalId, (long)wallNs);
            var candidate = wallNs - offsetNs;
            if (candidate >= long.MinValue && candidate <= long.MaxValue)
            {
                long offset2 = GetOffsetNs(canonicalId, (long)candidate);
                if (offset2 != offsetNs)
                    return wallNs - offset2;
            }
        } // else: outside long range → offset = 0 (no TZ data available)

        return wallNs - offsetNs;
    }

    /// <summary>
    /// BigInteger version of WallFromEpochNs: split epoch nanos into wall-clock
    /// date/time at the given offset, without clamping to the long range.
    /// </summary>
    public static (IsoDate Date, IsoTime Time) WallFromEpochNsBig(System.Numerics.BigInteger epochNs, long offsetNs)
    {
        var local = epochNs + offsetNs;
        var days = (long)System.Numerics.BigInteger.DivRem(local, NsPerDay, out var timeNsBig);
        long timeNs = (long)timeNsBig;
        // Adjust for negative time (DivRem truncates toward zero)
        if (timeNs < 0) { days -= 1; timeNs += NsPerDay; }
        var date = IsoMath.EpochDaysToCivil(days);
        var time = new IsoTime(
            (int)(timeNs / 3_600_000_000_000L), (int)(timeNs / 60_000_000_000L % 60), (int)(timeNs / 1_000_000_000L % 60),
            (int)(timeNs / 1_000_000L % 1000), (int)(timeNs / 1_000L % 1000), (int)(timeNs % 1000));
        return (date, time);
    }
}
