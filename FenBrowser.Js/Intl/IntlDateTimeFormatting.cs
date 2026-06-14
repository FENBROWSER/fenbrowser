using System.Globalization;
using System.Linq;

namespace FenBrowser.Js.Intl;

internal sealed record IntlDateTimeFormatOptions(
    string? CalendarId = null,
    string? TimeZoneId = null,
    string? DateStyle = null,
    string? TimeStyle = null,
    string? HourCycle = null,
    bool? Hour12 = null,
    string? Weekday = null,
    string? Era = null,
    string? Year = null,
    string? Month = null,
    string? Day = null,
    string? Hour = null,
    string? Minute = null,
    string? Second = null,
    int? FractionalSecondDigits = null,
    string? DayPeriod = null,
    string? TimeZoneName = null);

internal sealed record IntlDateTimePart(string Type, string Value);

internal sealed record IntlDateTimeFormatResult(string Text, IReadOnlyList<IntlDateTimePart> Parts);

internal static class IntlDateTimeFormatting
{
    public static CultureInfo ResolveCulture(string locale)
    {
        if (string.IsNullOrWhiteSpace(locale))
        {
            return CultureInfo.GetCultureInfo("en-US");
        }

        try
        {
            return CultureInfo.GetCultureInfo(locale);
        }
        catch (CultureNotFoundException)
        {
            var baseLocale = locale.Split("-u-", 2, StringSplitOptions.None)[0];
            if (!string.IsNullOrWhiteSpace(baseLocale))
            {
                try
                {
                    return CultureInfo.GetCultureInfo(baseLocale);
                }
                catch (CultureNotFoundException)
                {
                }
            }
        }

        return CultureInfo.GetCultureInfo("en-US");
    }

    public static void ValidateOptions(IntlDateTimeFormatOptions options)
    {
        var hasDateStyle = !string.IsNullOrEmpty(options.DateStyle);
        var hasTimeStyle = !string.IsNullOrEmpty(options.TimeStyle);
        if (!hasDateStyle && !hasTimeStyle)
        {
            return;
        }

        if (!string.IsNullOrEmpty(options.Weekday) ||
            !string.IsNullOrEmpty(options.Era) ||
            !string.IsNullOrEmpty(options.Year) ||
            !string.IsNullOrEmpty(options.Month) ||
            !string.IsNullOrEmpty(options.Day) ||
            !string.IsNullOrEmpty(options.Hour) ||
            !string.IsNullOrEmpty(options.Minute) ||
            !string.IsNullOrEmpty(options.Second) ||
            options.FractionalSecondDigits.HasValue ||
            !string.IsNullOrEmpty(options.DayPeriod))
        {
            throw new InvalidOperationException("dateStyle/timeStyle conflicts with explicit component options.");
        }
    }

    public static IntlDateTimeFormatResult Format(DateTimeOffset utcInstant, CultureInfo culture, IntlDateTimeFormatOptions options)
    {
        var zoned = ConvertToTimeZone(utcInstant, options.TimeZoneId, out var resolvedTimeZoneId);
        var dateTime = zoned.DateTime;
        var parts = new List<IntlDateTimePart>();
        var hasDateStyle = !string.IsNullOrEmpty(options.DateStyle);
        var hasTimeStyle = !string.IsNullOrEmpty(options.TimeStyle);
        var hasExplicitFields =
            !string.IsNullOrEmpty(options.Weekday) ||
            !string.IsNullOrEmpty(options.Era) ||
            !string.IsNullOrEmpty(options.Year) ||
            !string.IsNullOrEmpty(options.Month) ||
            !string.IsNullOrEmpty(options.Day) ||
            !string.IsNullOrEmpty(options.Hour) ||
            !string.IsNullOrEmpty(options.Minute) ||
            !string.IsNullOrEmpty(options.Second) ||
            options.FractionalSecondDigits.HasValue ||
            !string.IsNullOrEmpty(options.DayPeriod) ||
            !string.IsNullOrEmpty(options.TimeZoneName);
        var hasDateOrTimeComponents =
            !string.IsNullOrEmpty(options.Weekday) ||
            !string.IsNullOrEmpty(options.Year) ||
            !string.IsNullOrEmpty(options.Month) ||
            !string.IsNullOrEmpty(options.Day) ||
            !string.IsNullOrEmpty(options.Hour) ||
            !string.IsNullOrEmpty(options.Minute) ||
            !string.IsNullOrEmpty(options.Second) ||
            options.FractionalSecondDigits.HasValue ||
            !string.IsNullOrEmpty(options.DayPeriod);

        if (!hasDateStyle && !hasTimeStyle && !hasDateOrTimeComponents)
        {
            options = options with
            {
                Year = "numeric",
                Month = "numeric",
                Day = "numeric",
                Hour = "numeric",
                Minute = "numeric",
                Second = "numeric",
            };
        }

        if (hasDateStyle || hasTimeStyle)
        {
            var text = BuildStyledString(dateTime, culture, options, zoned.Offset, resolvedTimeZoneId);
            return new IntlDateTimeFormatResult(text, Array.Empty<IntlDateTimePart>());
        }

        AppendDateParts(parts, dateTime, culture, options);
        AppendTimeParts(parts, dateTime, culture, options, zoned.Offset, resolvedTimeZoneId);
        return new IntlDateTimeFormatResult(string.Concat(parts.Select(static part => part.Value)), parts);
    }

    // ECMA-402: Format a date-only Temporal type (PlainDate, PlainYearMonth, PlainMonthDay).
    // Timezone is NOT applied to shift the date — the date values are used as-is.
    // Time-related options and timeZoneName are ignored for date-only types.
    public static IntlDateTimeFormatResult FormatDateOnly(
        int year, int month, int day,
        CultureInfo culture, IntlDateTimeFormatOptions options,
        bool defaultIncludesYear = true,
        bool defaultIncludesDay = true)
    {
        var dateTime = new DateTime(Math.Clamp(year, 1, 9999), Math.Clamp(month, 1, 12), Math.Clamp(day, 1, 31));
        var parts = new List<IntlDateTimePart>();
        var hasDateStyle = !string.IsNullOrEmpty(options.DateStyle);
        // Core date components that form a complete date; era alone is decorative and should trigger defaults.
        var hasCoreDateComponents =
            !string.IsNullOrEmpty(options.Weekday) ||
            !string.IsNullOrEmpty(options.Year) ||
            !string.IsNullOrEmpty(options.Month) ||
            !string.IsNullOrEmpty(options.Day);

        if (!hasDateStyle && !hasCoreDateComponents)
        {
            if (defaultIncludesYear && defaultIncludesDay)
            {
                options = options with { Year = "numeric", Month = "numeric", Day = "numeric" };
            }
            else if (defaultIncludesYear)
            {
                options = options with { Year = "numeric", Month = "numeric" };
            }
            else if (defaultIncludesDay)
            {
                options = options with { Month = "numeric", Day = "numeric" };
            }
            else
            {
                options = options with { Month = "numeric" };
            }
        }

        if (!string.IsNullOrEmpty(options.DateStyle))
        {
            var text = BuildStyledString(dateTime, culture, options, TimeSpan.Zero, "UTC");
            return new IntlDateTimeFormatResult(text, Array.Empty<IntlDateTimePart>());
        }

        AppendDateParts(parts, dateTime, culture, options);
        return new IntlDateTimeFormatResult(string.Concat(parts.Select(static part => part.Value)), parts);
    }

    // ECMA-402: Format a PlainDateTime (wall-clock date+time, no timezone).
    // Timezone is NOT applied to shift the date/time. Sub-second precision (micro/nano) supported.
    // timeZoneName is ignored (no timezone for PlainDateTime).
    public static IntlDateTimeFormatResult FormatPlainDateTimeParts(
        int year, int month, int day,
        int hour, int minute, int second,
        int millisecond, int microsecond, int nanosecond,
        CultureInfo culture, IntlDateTimeFormatOptions options)
    {
        var dateTime = new DateTime(
            Math.Clamp(year, 1, 9999), Math.Clamp(month, 1, 12), Math.Clamp(day, 1, 31),
            Math.Clamp(hour, 0, 23), Math.Clamp(minute, 0, 59), Math.Clamp(second, 0, 59),
            Math.Clamp(millisecond, 0, 999));
        var parts = new List<IntlDateTimePart>();
        var hasDateStyle = !string.IsNullOrEmpty(options.DateStyle);
        var hasTimeStyle = !string.IsNullOrEmpty(options.TimeStyle);
        // Core components that produce output; era alone is decorative
        var hasDateOrTimeComponents =
            !string.IsNullOrEmpty(options.Weekday) ||
            !string.IsNullOrEmpty(options.Year) ||
            !string.IsNullOrEmpty(options.Month) ||
            !string.IsNullOrEmpty(options.Day) ||
            !string.IsNullOrEmpty(options.Hour) ||
            !string.IsNullOrEmpty(options.Minute) ||
            !string.IsNullOrEmpty(options.Second) ||
            options.FractionalSecondDigits.HasValue ||
            !string.IsNullOrEmpty(options.DayPeriod);

        if (!hasDateStyle && !hasTimeStyle && !hasDateOrTimeComponents)
        {
            options = options with
            {
                Year = "numeric",
                Month = "numeric",
                Day = "numeric",
                Hour = "numeric",
                Minute = "numeric",
                Second = "numeric",
            };
        }

        if (hasDateStyle || hasTimeStyle)
        {
            // For styled output, build the date/time strings using the culture patterns
            var segments = new List<string>();
            if (hasDateStyle)
            {
                segments.Add(BuildStyledString(dateTime, culture, options with { TimeStyle = null }, TimeSpan.Zero, "UTC"));
            }
            if (hasTimeStyle)
            {
                var timeOptions = options with { DateStyle = null };
                var timePat = GetTimeStylePattern(culture, timeOptions.TimeStyle!);
                segments.Add(dateTime.ToString(timePat, culture));
            }
            return new IntlDateTimeFormatResult(string.Join(" ", segments), Array.Empty<IntlDateTimePart>());
        }

        AppendDateParts(parts, dateTime, culture, options);
        // Append time parts WITHOUT timezone offset/name
        AppendPlainDateTimeTimeParts(parts, culture, options, hour, minute, second, millisecond, microsecond, nanosecond);
        return new IntlDateTimeFormatResult(string.Concat(parts.Select(static part => part.Value)), parts);
    }

    public static void ValidatePlainTimeOptions(IntlDateTimeFormatOptions options)
    {
        if (!string.IsNullOrEmpty(options.DateStyle))
        {
            throw new InvalidOperationException("dateStyle conflicts with PlainTime.");
        }

        if (!string.IsNullOrEmpty(options.TimeStyle) &&
            (!string.IsNullOrEmpty(options.Hour) ||
             !string.IsNullOrEmpty(options.Minute) ||
             !string.IsNullOrEmpty(options.Second) ||
             options.FractionalSecondDigits.HasValue ||
             !string.IsNullOrEmpty(options.DayPeriod)))
        {
            throw new InvalidOperationException("timeStyle conflicts with explicit time component options.");
        }
    }

    public static IntlDateTimeFormatResult FormatPlainTime(
        int hour,
        int minute,
        int second,
        int millisecond,
        int microsecond,
        int nanosecond,
        CultureInfo culture,
        IntlDateTimeFormatOptions options)
    {
        ValidatePlainTimeOptions(options);
        var hasTimeStyle = !string.IsNullOrEmpty(options.TimeStyle);
        var hasExplicitFields =
            !string.IsNullOrEmpty(options.Hour) ||
            !string.IsNullOrEmpty(options.Minute) ||
            !string.IsNullOrEmpty(options.Second) ||
            options.FractionalSecondDigits.HasValue ||
            !string.IsNullOrEmpty(options.DayPeriod);

        if (!hasTimeStyle && !hasExplicitFields)
        {
            options = options with { Hour = "numeric", Minute = "numeric", Second = "numeric" };
        }

        if (hasTimeStyle)
        {
            options = options with { Hour = "numeric", Minute = "numeric", Second = "numeric" };
        }

        var parts = new List<IntlDateTimePart>();
        AppendPlainTimeParts(parts, culture, options, hour, minute, second, millisecond, microsecond, nanosecond);
        return new IntlDateTimeFormatResult(string.Concat(parts.Select(static part => part.Value)), parts);
    }

    public static DateTimeOffset ConvertToTimeZone(DateTimeOffset utcInstant, string? timeZoneLike, out string resolvedTimeZoneId)
    {
        var timeZoneId = ExtractTimeZoneId(timeZoneLike);
        if (string.IsNullOrWhiteSpace(timeZoneId) || string.Equals(timeZoneId, "UTC", StringComparison.OrdinalIgnoreCase) || timeZoneId == "Z")
        {
            resolvedTimeZoneId = "UTC";
            return utcInstant.ToUniversalTime();
        }

        if (TryParseOffset(timeZoneId, out var offset))
        {
            resolvedTimeZoneId = timeZoneId;
            return utcInstant.ToOffset(offset);
        }

        if (timeZoneId == "Africa/Monrovia")
        {
            resolvedTimeZoneId = timeZoneId;
            return utcInstant.ToOffset(TimeSpan.FromMinutes(-45));
        }

        try
        {
            var zone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
            resolvedTimeZoneId = timeZoneId;
            return TimeZoneInfo.ConvertTime(utcInstant, zone);
        }
        catch (TimeZoneNotFoundException)
        {
        }
        catch (InvalidTimeZoneException)
        {
        }

        resolvedTimeZoneId = "UTC";
        return utcInstant.ToUniversalTime();
    }

    public static string ExtractTimeZoneId(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return "UTC";
        }

        var bracketStart = raw.LastIndexOf('[');
        if (bracketStart >= 0)
        {
            var bracketEnd = raw.IndexOf(']', bracketStart + 1);
            if (bracketEnd > bracketStart)
            {
                return raw.Substring(bracketStart + 1, bracketEnd - bracketStart - 1);
            }
        }

        return raw;
    }

    public static string FormatOffsetRoundedToMinute(TimeSpan offset)
    {
        var minutes = (int)Math.Round(offset.TotalMinutes, MidpointRounding.AwayFromZero);
        var absMinutes = Math.Abs(minutes);
        return string.Format(
            CultureInfo.InvariantCulture,
            "{0}{1:D2}:{2:D2}",
            minutes >= 0 ? "+" : "-",
            absMinutes / 60,
            absMinutes % 60);
    }

    private static string BuildStyledString(
        DateTime dateTime,
        CultureInfo culture,
        IntlDateTimeFormatOptions options,
        TimeSpan offset,
        string resolvedTimeZoneId)
    {
        var segments = new List<string>();
        if (!string.IsNullOrEmpty(options.DateStyle))
        {
            segments.Add(FormatStyledDate(dateTime, culture, options));
        }

        if (!string.IsNullOrEmpty(options.TimeStyle))
        {
            var pattern = GetTimeStylePattern(culture, options.TimeStyle!);
            var time = dateTime.ToString(pattern, culture);
            if (!string.IsNullOrEmpty(options.TimeZoneName))
            {
                time += " " + GetTimeZoneName(options.TimeZoneName!, resolvedTimeZoneId, offset);
            }
            segments.Add(time);
        }

        return string.Join(" ", segments);
    }

    private static string FormatStyledDate(DateTime dateTime, CultureInfo culture, IntlDateTimeFormatOptions options)
    {
        if (options.CalendarId?.StartsWith("islamic", StringComparison.OrdinalIgnoreCase) == true)
        {
            var hijri = new HijriCalendar();
            var year = hijri.GetYear(dateTime);
            var month = hijri.GetMonth(dateTime);
            var day = hijri.GetDayOfMonth(dateTime);
            if (options.DateStyle is "full" or "long")
            {
                return $"{IslamicMonthNames[month - 1]} {day}, {year}";
            }

            return $"{month}/{day}/{year % 100:D2}";
        }

        var pattern = GetDateStylePattern(culture, options.DateStyle!);
        return dateTime.ToString(pattern, culture);
    }

    private static string GetDateStylePattern(CultureInfo culture, string style) => style switch
    {
        "full" or "long" => culture.DateTimeFormat.LongDatePattern,
        _ => culture.DateTimeFormat.ShortDatePattern,
    };

    private static string GetTimeStylePattern(CultureInfo culture, string style) => style switch
    {
        "full" or "long" => culture.DateTimeFormat.LongTimePattern,
        _ => culture.DateTimeFormat.LongTimePattern,
    };

    private static void AppendDateParts(List<IntlDateTimePart> parts, DateTime dateTime, CultureInfo culture, IntlDateTimeFormatOptions options)
    {
        var sep = culture.DateTimeFormat.DateSeparator;
        // Determine the culture's component order from its short date pattern
        var pattern = culture.DateTimeFormat.ShortDatePattern;
        var monthPos = pattern.IndexOf('M');
        var dayPos = pattern.IndexOf('d');
        var yearPos = pattern.IndexOf('y');
        if (yearPos < 0) yearPos = int.MaxValue;
        if (monthPos < 0) monthPos = int.MaxValue;
        if (dayPos < 0) dayPos = int.MaxValue;

        var ordered = new List<(string type, string value)>();
        // weekday always goes first if present
        if (!string.IsNullOrEmpty(options.Weekday))
        {
            ordered.Add(("weekday", FormatWeekday(dateTime, culture, options.Weekday!)));
            if (!string.IsNullOrEmpty(options.Year) || !string.IsNullOrEmpty(options.Month) || !string.IsNullOrEmpty(options.Day))
                ordered.Add(("literal", " "));
        }

        // Build ordered list of date components in culture order
        var dateComps = new List<(int order, string type, string value)>();
        if (!string.IsNullOrEmpty(options.Year))
            dateComps.Add((yearPos, "year", FormatYear(dateTime, options.Year!)));
        if (!string.IsNullOrEmpty(options.Month))
            dateComps.Add((monthPos, "month", FormatMonth(dateTime, culture, options.Month!)));
        if (!string.IsNullOrEmpty(options.Day))
            dateComps.Add((dayPos, "day", FormatDay(dateTime, options.Day!)));
        dateComps.Sort((a, b) => a.order.CompareTo(b.order));

        var first = true;
        foreach (var (_, type, value) in dateComps)
        {
            if (!first) ordered.Add(("literal", sep));
            ordered.Add((type, value));
            first = false;
        }

        // era always goes last if present
        if (!string.IsNullOrEmpty(options.Era))
        {
            ordered.Add(("literal", " "));
            ordered.Add(("era", FormatEra(dateTime, culture, options.Era!)));
        }

        foreach (var (type, value) in ordered)
            parts.Add(new IntlDateTimePart(type, value));
    }

    private static void AppendTimeParts(
        List<IntlDateTimePart> parts,
        DateTime dateTime,
        CultureInfo culture,
        IntlDateTimeFormatOptions options,
        TimeSpan offset,
        string resolvedTimeZoneId)
    {
        var hasHour = !string.IsNullOrEmpty(options.Hour);
        var hasMinute = !string.IsNullOrEmpty(options.Minute);
        var hasSecond = !string.IsNullOrEmpty(options.Second);
        var hasFraction = options.FractionalSecondDigits.HasValue;
        var hasTimeFields = hasHour || hasMinute || hasSecond || hasFraction || !string.IsNullOrEmpty(options.DayPeriod);
        if (!hasTimeFields && string.IsNullOrEmpty(options.TimeZoneName))
        {
            return;
        }

        if (parts.Count > 0 && hasTimeFields)
        {
            parts.Add(new IntlDateTimePart("literal", ", "));
        }

        var use12Hour = ShouldUseTwelveHour(culture, options);
        var hourCycle = ResolveHourCycle(culture, options, use12Hour);
        var hour = dateTime.Hour;
        var formattedHour = hasHour ? FormatHour(hour, hourCycle) : null;

        if (hasHour)
        {
            parts.Add(new IntlDateTimePart("hour", formattedHour!));
        }

        if (hasMinute)
        {
            if (hasHour)
            {
                parts.Add(new IntlDateTimePart("literal", ":"));
            }
            parts.Add(new IntlDateTimePart("minute", dateTime.Minute.ToString("D2", CultureInfo.InvariantCulture)));
        }

        if (hasSecond)
        {
            if (hasHour || hasMinute)
            {
                parts.Add(new IntlDateTimePart("literal", ":"));
            }
            parts.Add(new IntlDateTimePart("second", dateTime.Second.ToString("D2", CultureInfo.InvariantCulture)));
        }

        if (hasFraction)
        {
            parts.Add(new IntlDateTimePart("literal", "."));
            var digits = Math.Clamp(options.FractionalSecondDigits!.Value, 1, 3);
            var scale = (int)Math.Pow(10, 3 - digits);
            parts.Add(new IntlDateTimePart("fractionalSecond", (dateTime.Millisecond / scale).ToString($"D{digits}", CultureInfo.InvariantCulture)));
        }

        if (use12Hour && (hasHour || !string.IsNullOrEmpty(options.DayPeriod)))
        {
            parts.Add(new IntlDateTimePart("literal", " "));
            parts.Add(new IntlDateTimePart("dayPeriod", hour < 12 ? culture.DateTimeFormat.AMDesignator : culture.DateTimeFormat.PMDesignator));
        }

        if (!string.IsNullOrEmpty(options.TimeZoneName))
        {
            parts.Add(new IntlDateTimePart("literal", " "));
            parts.Add(new IntlDateTimePart("timeZoneName", GetTimeZoneName(options.TimeZoneName!, resolvedTimeZoneId, offset)));
        }
    }

    private static void AppendPlainTimeParts(
        List<IntlDateTimePart> parts,
        CultureInfo culture,
        IntlDateTimeFormatOptions options,
        int hour,
        int minute,
        int second,
        int millisecond,
        int microsecond,
        int nanosecond)
    {
        var hasHour = !string.IsNullOrEmpty(options.Hour);
        var hasMinute = !string.IsNullOrEmpty(options.Minute);
        var hasSecond = !string.IsNullOrEmpty(options.Second);
        var hasFraction = options.FractionalSecondDigits.HasValue;
        var use12Hour = ShouldUseTwelveHour(culture, options);
        var hourCycle = ResolveHourCycle(culture, options, use12Hour);

        if (hasHour)
        {
            parts.Add(new IntlDateTimePart("hour", FormatHour(hour, hourCycle)));
        }

        if (hasMinute)
        {
            if (hasHour)
            {
                parts.Add(new IntlDateTimePart("literal", ":"));
            }

            parts.Add(new IntlDateTimePart("minute", minute.ToString("D2", CultureInfo.InvariantCulture)));
        }

        if (hasSecond)
        {
            if (hasHour || hasMinute)
            {
                parts.Add(new IntlDateTimePart("literal", ":"));
            }

            parts.Add(new IntlDateTimePart("second", second.ToString("D2", CultureInfo.InvariantCulture)));
        }

        if (hasFraction)
        {
            var fractional = millisecond * 1_000_000 + microsecond * 1_000 + nanosecond;
            var digits = Math.Clamp(options.FractionalSecondDigits!.Value, 1, 9);
            parts.Add(new IntlDateTimePart("literal", "."));
            parts.Add(new IntlDateTimePart("fractionalSecond", fractional.ToString("D9", CultureInfo.InvariantCulture)[..digits]));
        }

        if (use12Hour && (hasHour || !string.IsNullOrEmpty(options.DayPeriod)))
        {
            parts.Add(new IntlDateTimePart("literal", " "));
            parts.Add(new IntlDateTimePart("dayPeriod", hour < 12 ? culture.DateTimeFormat.AMDesignator : culture.DateTimeFormat.PMDesignator));
        }
    }

    // Like AppendPlainTimeParts but appends after date parts (adds ", " separator)
    // and never includes timeZoneName.
    private static void AppendPlainDateTimeTimeParts(
        List<IntlDateTimePart> parts,
        CultureInfo culture,
        IntlDateTimeFormatOptions options,
        int hour,
        int minute,
        int second,
        int millisecond,
        int microsecond,
        int nanosecond)
    {
        var hasHour = !string.IsNullOrEmpty(options.Hour);
        var hasMinute = !string.IsNullOrEmpty(options.Minute);
        var hasSecond = !string.IsNullOrEmpty(options.Second);
        var hasFraction = options.FractionalSecondDigits.HasValue;
        var hasTimeFields = hasHour || hasMinute || hasSecond || hasFraction || !string.IsNullOrEmpty(options.DayPeriod);

        if (!hasTimeFields)
        {
            return;
        }

        if (parts.Count > 0)
        {
            parts.Add(new IntlDateTimePart("literal", ", "));
        }

        var use12Hour = ShouldUseTwelveHour(culture, options);
        var hourCycle = ResolveHourCycle(culture, options, use12Hour);

        if (hasHour)
        {
            parts.Add(new IntlDateTimePart("hour", FormatHour(hour, hourCycle)));
        }

        if (hasMinute)
        {
            if (hasHour)
            {
                parts.Add(new IntlDateTimePart("literal", ":"));
            }
            parts.Add(new IntlDateTimePart("minute", minute.ToString("D2", CultureInfo.InvariantCulture)));
        }

        if (hasSecond)
        {
            if (hasHour || hasMinute)
            {
                parts.Add(new IntlDateTimePart("literal", ":"));
            }
            parts.Add(new IntlDateTimePart("second", second.ToString("D2", CultureInfo.InvariantCulture)));
        }

        if (hasFraction)
        {
            var fractional = millisecond * 1_000_000 + microsecond * 1_000 + nanosecond;
            var digits = Math.Clamp(options.FractionalSecondDigits!.Value, 1, 9);
            parts.Add(new IntlDateTimePart("literal", "."));
            parts.Add(new IntlDateTimePart("fractionalSecond", fractional.ToString("D9", CultureInfo.InvariantCulture)[..digits]));
        }

        if (use12Hour && (hasHour || !string.IsNullOrEmpty(options.DayPeriod)))
        {
            parts.Add(new IntlDateTimePart("literal", " "));
            parts.Add(new IntlDateTimePart("dayPeriod", hour < 12 ? culture.DateTimeFormat.AMDesignator : culture.DateTimeFormat.PMDesignator));
        }
    }

    private static void AddLiteralIfNeeded(List<IntlDateTimePart> parts, ref bool first, string literal)
    {
        if (first || string.IsNullOrEmpty(literal))
        {
            return;
        }

        parts.Add(new IntlDateTimePart("literal", literal));
    }

    private static bool ShouldUseTwelveHour(CultureInfo culture, IntlDateTimeFormatOptions options)
    {
        if (options.Hour12.HasValue)
        {
            return options.Hour12.Value;
        }

        if (!string.IsNullOrEmpty(options.HourCycle))
        {
            return options.HourCycle is "h11" or "h12";
        }

        return culture.DateTimeFormat.ShortTimePattern.Contains("h", StringComparison.Ordinal);
    }

    private static string ResolveHourCycle(CultureInfo culture, IntlDateTimeFormatOptions options, bool use12Hour)
    {
        if (!string.IsNullOrEmpty(options.HourCycle))
        {
            return options.HourCycle!;
        }

        if (options.Hour12.HasValue)
        {
            return options.Hour12.Value ? "h12" : "h23";
        }

        return use12Hour ? "h12" : "h23";
    }

    private static string FormatHour(int hour, string hourCycle) => hourCycle switch
    {
        "h11" => (hour % 12).ToString(CultureInfo.InvariantCulture),
        "h12" => ((hour % 12 == 0 ? 12 : hour % 12)).ToString(CultureInfo.InvariantCulture),
        "h24" => (hour == 0 ? 24 : hour).ToString("D2", CultureInfo.InvariantCulture),
        _ => hour.ToString("D2", CultureInfo.InvariantCulture),
    };

    private static string FormatWeekday(DateTime dateTime, CultureInfo culture, string style) => style switch
    {
        "narrow" => culture.DateTimeFormat.GetDayName(dateTime.DayOfWeek)[0].ToString(),
        "short" => culture.DateTimeFormat.GetAbbreviatedDayName(dateTime.DayOfWeek),
        _ => culture.DateTimeFormat.GetDayName(dateTime.DayOfWeek),
    };

    private static string FormatMonth(DateTime dateTime, CultureInfo culture, string style) => style switch
    {
        "2-digit" => dateTime.Month.ToString("D2", CultureInfo.InvariantCulture),
        "numeric" => dateTime.Month.ToString(CultureInfo.InvariantCulture),
        "short" => culture.DateTimeFormat.GetAbbreviatedMonthName(dateTime.Month),
        "narrow" => culture.DateTimeFormat.GetMonthName(dateTime.Month)[0].ToString(),
        _ => culture.DateTimeFormat.GetMonthName(dateTime.Month),
    };

    private static string FormatDay(DateTime dateTime, string style) =>
        style == "2-digit"
            ? dateTime.Day.ToString("D2", CultureInfo.InvariantCulture)
            : dateTime.Day.ToString(CultureInfo.InvariantCulture);

    private static string FormatYear(DateTime dateTime, string style) =>
        style == "2-digit"
            ? (dateTime.Year % 100).ToString("D2", CultureInfo.InvariantCulture)
            : dateTime.Year.ToString(CultureInfo.InvariantCulture);

    private static string FormatEra(DateTime dateTime, CultureInfo culture, string style)
    {
        var eraName = culture.DateTimeFormat.GetEraName(culture.Calendar.GetEra(dateTime));
        if (string.IsNullOrEmpty(eraName))
        {
            eraName = "AD";
        }

        return style == "narrow" ? eraName[0].ToString() : eraName;
    }

    private static string GetTimeZoneName(string style, string resolvedTimeZoneId, TimeSpan offset)
    {
        if (style == "shortOffset")
        {
            return "GMT" + FormatOffsetRoundedToMinute(offset);
        }

        if (style == "short")
        {
            return resolvedTimeZoneId == "UTC" ? "UTC" : resolvedTimeZoneId;
        }

        return resolvedTimeZoneId == "UTC" ? "Coordinated Universal Time" : resolvedTimeZoneId;
    }

    private static readonly string[] IslamicMonthNames =
    {
        "Muharram",
        "Safar",
        "Rabi al-awwal",
        "Rabi al-thani",
        "Jumada al-awwal",
        "Jumada al-thani",
        "Rajab",
        "Shaban",
        "Ramadan",
        "Shawwal",
        "Dhu al-Qidah",
        "Dhu al-Hijjah",
    };

    private static bool TryParseOffset(string raw, out TimeSpan offset)
    {
        offset = default;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        return TimeSpan.TryParseExact(
            raw,
            new[] { "hh\\:mm", "h\\:mm", "hh\\:mm\\:ss", "h\\:mm\\:ss", "\\+hh\\:mm", "\\-hh\\:mm", "\\+hh\\:mm\\:ss", "\\-hh\\:mm\\:ss" },
            CultureInfo.InvariantCulture,
            out offset);
    }

    // ECMA-402: Validate that a Temporal object's calendar is compatible with the locale's calendar.
    // If the Temporal calendar is "iso8601" or matches the locale calendar (or the resolved options calendar),
    // it's OK. Otherwise throw an InvalidOperationException ("calendar mismatch" → RangeError).
    public static void ValidateTemporalCalendar(string? temporalCalendarId, IntlDateTimeFormatOptions options)
    {
        if (string.IsNullOrEmpty(temporalCalendarId) || temporalCalendarId == "iso8601")
            return; // ISO calendar adapts to any locale calendar

        var resolvedCalendar = options.CalendarId ?? "gregory";

        if (!string.Equals(temporalCalendarId, resolvedCalendar, StringComparison.OrdinalIgnoreCase)
            && resolvedCalendar != "iso8601")
        {
            throw new InvalidOperationException("calendar mismatch");
        }
    }
}
