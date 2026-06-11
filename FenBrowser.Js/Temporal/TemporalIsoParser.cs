namespace FenBrowser.Js.Temporal;

// Result of parsing a Temporal ISO 8601 string per the proposal grammar
// (tc39.es/proposal-temporal #sec-temporal-iso8601grammar).
internal sealed class ParsedIsoString
{
    public bool HasDate;
    public int Year;
    public int Month;
    public int Day;

    public bool HasTime;
    public IsoTime Time;

    public bool HasUtcDesignator;          // 'Z' / 'z'
    public bool HasOffset;                  // numeric UTC offset
    public long OffsetNanoseconds;
    public bool OffsetSubMinuteSyntax;      // offset written with seconds (invalid as a tz id)

    public string? TimeZoneAnnotation;      // contents of the first [..] bracket
    public string? Calendar;                // first u-ca annotation value
}

// Hand-rolled scanner for the Temporal ISO 8601 grammar. Strict: ASCII
// signs only (U+2212 is rejected), -000000 rejected as extended year,
// at most 9 fractional digits, lowercase annotation keys, critical-flag
// rules for unknown/duplicate annotations.
internal static class TemporalIsoParser
{
    // ParseTemporalDateTimeString variant for ToTemporalDate /
    // ToTemporalDateTime: AnnotatedDateTime[~Zoned]. Returns false with a
    // reason when the string does not match the grammar.
    public static bool TryParseDateTime(string s, out ParsedIsoString result, out string error)
    {
        result = new ParsedIsoString();
        error = "";
        int i = 0;

        if (!TryParseDate(s, ref i, result, ref error))
        {
            return false;
        }

        // Optional time part: DateTimeSeparator TimeSpec [DateTimeUTCOffset]
        if (i < s.Length && (s[i] == 'T' || s[i] == 't' || s[i] == ' '))
        {
            int save = i;
            i++;
            if (!TryParseTimeSpec(s, ref i, result, ref error))
            {
                // A space might just be junk; the grammar requires TimeSpec
                // after a separator, so this is a hard failure.
                i = save;
                error = error.Length > 0 ? error : "expected time after date-time separator";
                return false;
            }

            // Optional UTC offset / designator (only valid after a time).
            if (i < s.Length && (s[i] == 'Z' || s[i] == 'z'))
            {
                result.HasUtcDesignator = true;
                i++;
            }
            else if (i < s.Length && (s[i] == '+' || s[i] == '-'))
            {
                int offsetStart = i;
                if (!TryParseUtcOffset(s, ref i, subMinutePrecision: true, out var offsetNs, ref error))
                {
                    return false;
                }

                result.HasOffset = true;
                result.OffsetNanoseconds = offsetNs;
                // More than sign+HH+MM digits means a seconds component was written.
                int offsetDigits = 0;
                for (int k = offsetStart; k < i; k++)
                {
                    if (IsDigit(s[k])) offsetDigits++;
                }

                result.OffsetSubMinuteSyntax = offsetDigits > 4;
            }
        }

        if (!TryParseAnnotations(s, ref i, result, ref error))
        {
            return false;
        }

        if (i != s.Length)
        {
            error = $"unexpected characters at position {i}";
            return false;
        }

        if (!IsoMath.IsValidIsoDate(result.Year, result.Month, result.Day))
        {
            error = "date out of range";
            return false;
        }

        return true;
    }

    // ParseTemporalInstantString: date + time + (Z or numeric offset) required.
    public static bool TryParseInstant(string s, out ParsedIsoString result, out string error)
    {
        if (!TryParseDateTime(s, out result, out error))
        {
            return false;
        }

        if (!result.HasTime || (!result.HasUtcDesignator && !result.HasOffset))
        {
            error = "instant requires a time and a UTC offset or Z";
            return false;
        }

        return true;
    }

    // ParseTemporalYearMonthString: DateSpecYearMonth Annotations, or a full
    // AnnotatedDateTime (the year-month is taken from its date part).
    public static bool TryParseYearMonth(string s, out ParsedIsoString result, out string error)
    {
        result = new ParsedIsoString();
        error = "";
        int i = 0;
        if (TryParseYearMonthSpec(s, ref i, result, ref error)
            && TryParseAnnotations(s, ref i, result, ref error)
            && i == s.Length)
        {
            return true;
        }

        // Fall back to the full date-time grammar.
        return TryParseDateTime(s, out result, out error);
    }

    private static bool TryParseYearMonthSpec(string s, ref int i, ParsedIsoString result, ref string error)
    {
        int year;
        if (i < s.Length && (s[i] == '+' || s[i] == '-'))
        {
            char sign = s[i];
            i++;
            if (!TryReadDigits(s, ref i, 6, out int y6))
            {
                error = "expected 6-digit extended year";
                return false;
            }

            if (sign == '-' && y6 == 0)
            {
                error = "reject minus zero as extended year";
                return false;
            }

            year = sign == '-' ? -y6 : y6;
        }
        else if (!TryReadDigits(s, ref i, 4, out year))
        {
            error = "expected 4-digit year";
            return false;
        }

        if (i < s.Length && s[i] == '-')
        {
            i++;
        }

        if (!TryReadDigits(s, ref i, 2, out int month) || month < 1 || month > 12)
        {
            error = "month out of range";
            return false;
        }

        result.HasDate = true;
        result.Year = year;
        result.Month = month;
        result.Day = 1;
        return true;
    }

    // ParseTemporalMonthDayString: [--] DateMonth [-] DateDay Annotations,
    // or a full AnnotatedDateTime. Reference year 1972 (leap) when absent.
    public static bool TryParseMonthDay(string s, out ParsedIsoString result, out string error)
    {
        result = new ParsedIsoString();
        error = "";
        int i = 0;
        if (i + 1 < s.Length && s[i] == '-' && s[i + 1] == '-')
        {
            i += 2;
        }

        if (TryReadDigits(s, ref i, 2, out int month) && month >= 1 && month <= 12)
        {
            if (i < s.Length && s[i] == '-')
            {
                i++;
            }

            if (TryReadDigits(s, ref i, 2, out int day)
                && day >= 1 && day <= IsoMath.DaysInMonth(1972, month)
                && TryParseAnnotations(s, ref i, result, ref error)
                && i == s.Length)
            {
                result.HasDate = true;
                result.Year = 1972;
                result.Month = month;
                result.Day = day;
                return true;
            }
        }

        // Fall back to the full date-time grammar.
        result = new ParsedIsoString();
        return TryParseDateTime(s, out result, out error);
    }

    // ParseTemporalTimeString: [T] TimeSpec [DateTimeUTCOffset] Annotations,
    // or a full AnnotatedDateTime that includes a time. Without the leading
    // time designator, strings that are also valid DateSpecYearMonth /
    // DateSpecMonthDay are ambiguous and rejected.
    public static bool TryParseTime(string s, out ParsedIsoString result, out string error)
    {
        result = new ParsedIsoString();
        error = "";
        int i = 0;
        bool designated = i < s.Length && (s[i] == 'T' || s[i] == 't');
        if (designated)
        {
            i++;
        }

        if (TryParseTimeSpec(s, ref i, result, ref error))
        {
            if (i < s.Length && (s[i] == 'Z' || s[i] == 'z'))
            {
                result.HasUtcDesignator = true;
                i++;
            }
            else if (i < s.Length && (s[i] == '+' || s[i] == '-'))
            {
                int offsetStart = i;
                if (!TryParseUtcOffset(s, ref i, subMinutePrecision: true, out var offsetNs, ref error))
                {
                    return false;
                }

                result.HasOffset = true;
                result.OffsetNanoseconds = offsetNs;
                // More than sign+HH+MM digits means a seconds component was written.
                int offsetDigits = 0;
                for (int k = offsetStart; k < i; k++)
                {
                    if (IsDigit(s[k])) offsetDigits++;
                }

                result.OffsetSubMinuteSyntax = offsetDigits > 4;
            }

            if (TryParseAnnotations(s, ref i, result, ref error) && i == s.Length)
            {
                if (!designated && IsAmbiguousWithDate(s))
                {
                    error = "time string is ambiguous with a year-month or month-day";
                    return false;
                }

                result.HasTime = true;
                return true;
            }
        }

        // Fall back to the full date-time grammar; a time part is required.
        result = new ParsedIsoString();
        error = "";
        if (!TryParseDateTime(s, out result, out error))
        {
            return false;
        }

        if (!result.HasTime)
        {
            error = "no time component in string";
            return false;
        }

        return true;
    }

    private static bool IsAmbiguousWithDate(string s)
    {
        // Strip annotations for the ambiguity check.
        int bracket = s.IndexOf('[');
        string bare = bracket >= 0 ? s[..bracket] : s;
        if (TryParseYearMonth(bare, out _, out _) && !bare.Contains(':'))
        {
            return true;
        }

        var probe = new ParsedIsoString();
        string err = "";
        int j = 0;
        if (j + 1 < bare.Length && bare[j] == '-' && bare[j + 1] == '-')
        {
            j += 2;
        }

        if (TryReadDigits(bare, ref j, 2, out int month) && month >= 1 && month <= 12)
        {
            if (j < bare.Length && bare[j] == '-')
            {
                j++;
            }

            if (TryReadDigits(bare, ref j, 2, out int day)
                && day >= 1 && day <= IsoMath.DaysInMonth(1972, month)
                && TryParseAnnotations(bare, ref j, probe, ref err)
                && j == bare.Length)
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryParseDate(string s, ref int i, ParsedIsoString result, ref string error)
    {
        // DateYear: 4 digits, or ASCII sign + 6 digits ("-000000" rejected).
        int year;
        if (i < s.Length && (s[i] == '+' || s[i] == '-'))
        {
            char sign = s[i];
            i++;
            if (!TryReadDigits(s, ref i, 6, out int y6))
            {
                error = "expected 6-digit extended year";
                return false;
            }

            if (sign == '-' && y6 == 0)
            {
                error = "reject minus zero as extended year";
                return false;
            }

            year = sign == '-' ? -y6 : y6;
        }
        else
        {
            if (!TryReadDigits(s, ref i, 4, out year))
            {
                error = "expected 4-digit year";
                return false;
            }
        }

        bool extended = i < s.Length && s[i] == '-';
        if (extended)
        {
            i++;
        }

        if (!TryReadDigits(s, ref i, 2, out int month))
        {
            error = "expected 2-digit month";
            return false;
        }

        if (month < 1 || month > 12)
        {
            error = "month out of range";
            return false;
        }

        if (extended)
        {
            if (i >= s.Length || s[i] != '-')
            {
                error = "expected '-' before day";
                return false;
            }

            i++;
        }

        if (!TryReadDigits(s, ref i, 2, out int day))
        {
            error = "expected 2-digit day";
            return false;
        }

        if (day < 1 || day > 31)
        {
            error = "day out of range";
            return false;
        }

        result.HasDate = true;
        result.Year = year;
        result.Month = month;
        result.Day = day;
        return true;
    }

    private static bool TryParseTimeSpec(string s, ref int i, ParsedIsoString result, ref string error)
    {
        if (!TryReadDigits(s, ref i, 2, out int hour) || hour > 23)
        {
            error = "expected 2-digit hour";
            return false;
        }

        int minute = 0;
        int second = 0;
        long fractionNs = 0;
        bool extended = i < s.Length && s[i] == ':';
        if (extended)
        {
            i++;
        }

        if (i < s.Length && IsDigit(s[i]))
        {
            if (!TryReadDigits(s, ref i, 2, out minute) || minute > 59)
            {
                error = "minute out of range";
                return false;
            }

            bool hasSecondSep = i < s.Length && s[i] == ':';
            if (extended && hasSecondSep)
            {
                i++;
            }

            if ((extended ? hasSecondSep : i < s.Length && IsDigit(s[i])))
            {
                if (!TryReadDigits(s, ref i, 2, out second) || second > 60)
                {
                    error = "second out of range";
                    return false;
                }

                if (second == 60)
                {
                    second = 59; // leap seconds clamp per spec
                }

                if (i < s.Length && (s[i] == '.' || s[i] == ','))
                {
                    i++;
                    if (!TryReadFraction(s, ref i, out fractionNs))
                    {
                        error = "no more than 9 decimal places are allowed";
                        return false;
                    }
                }
            }
        }
        else if (extended)
        {
            error = "expected minutes after ':'";
            return false;
        }

        result.HasTime = true;
        result.Time = new IsoTime(
            hour, minute, second,
            (int)(fractionNs / 1_000_000),
            (int)(fractionNs / 1_000 % 1_000),
            (int)(fractionNs % 1_000));
        return true;
    }

    // UTCOffset: ASCII sign HH [:MM [:SS [.fff…]]] (or compact without ':').
    // subMinutePrecision is allowed in date-time strings but not in
    // time zone annotations.
    public static bool TryParseUtcOffset(string s, ref int i, bool subMinutePrecision, out long offsetNs, ref string error)
    {
        offsetNs = 0;
        if (i >= s.Length || (s[i] != '+' && s[i] != '-'))
        {
            error = "expected offset sign";
            return false;
        }

        int sign = s[i] == '-' ? -1 : 1;
        i++;
        if (!TryReadDigits(s, ref i, 2, out int hours) || hours > 23)
        {
            error = "offset hours out of range";
            return false;
        }

        int minutes = 0;
        int seconds = 0;
        long fractionNs = 0;
        bool extended = i < s.Length && s[i] == ':';
        if (extended)
        {
            i++;
        }

        if (i < s.Length && IsDigit(s[i]))
        {
            if (!TryReadDigits(s, ref i, 2, out minutes) || minutes > 59)
            {
                error = "offset minutes out of range";
                return false;
            }

            bool hasSecondSep = i < s.Length && s[i] == ':';
            if (extended && hasSecondSep)
            {
                i++;
            }

            bool secondsFollow = extended ? hasSecondSep : i + 1 < s.Length && IsDigit(s[i]) && IsDigit(s[i + 1]);
            if (secondsFollow)
            {
                if (!subMinutePrecision)
                {
                    error = "sub-minute offset precision not allowed here";
                    return false;
                }

                if (!TryReadDigits(s, ref i, 2, out seconds) || seconds > 59)
                {
                    error = "offset seconds out of range";
                    return false;
                }

                if (i < s.Length && (s[i] == '.' || s[i] == ','))
                {
                    i++;
                    if (!TryReadFraction(s, ref i, out fractionNs))
                    {
                        error = "no more than 9 decimal places are allowed";
                        return false;
                    }
                }
            }
        }
        else if (extended)
        {
            error = "expected offset minutes after ':'";
            return false;
        }

        offsetNs = sign * (((hours * 3600L + minutes * 60L + seconds) * 1_000_000_000L) + fractionNs);
        return true;
    }

    private static bool TryParseAnnotations(string s, ref int i, ParsedIsoString result, ref string error)
    {
        // Optional TimeZoneAnnotation first: [ [!] tz-identifier ]
        // then any number of [ [!] key=value ] annotations.
        bool first = true;
        int calendarCount = 0;
        bool anyCalendarCritical = false;

        while (i < s.Length && s[i] == '[')
        {
            i++;
            bool critical = i < s.Length && s[i] == '!';
            if (critical)
            {
                i++;
            }

            int close = s.IndexOf(']', i);
            if (close < 0)
            {
                error = "unterminated annotation";
                return false;
            }

            string body = s[i..close];
            i = close + 1;

            if (first && IsTimeZoneAnnotationBody(body))
            {
                result.TimeZoneAnnotation = body;
                first = false;
                continue;
            }

            first = false;

            // Key-value annotation: key '=' value
            int eq = body.IndexOf('=');
            if (eq <= 0)
            {
                error = $"invalid annotation '{body}'";
                return false;
            }

            string key = body[..eq];
            string value = body[(eq + 1)..];
            if (!IsValidAnnotationKey(key))
            {
                error = $"annotation keys must be lowercase: {key}";
                return false;
            }

            if (!IsValidAnnotationValue(value))
            {
                error = $"invalid annotation value '{value}'";
                return false;
            }

            if (key == "u-ca")
            {
                calendarCount++;
                anyCalendarCritical |= critical;
                if (calendarCount == 1)
                {
                    result.Calendar = value;
                }
            }
            else if (critical)
            {
                error = $"unrecognized critical annotation key '{key}'";
                return false;
            }
        }

        // More than one u-ca annotation is tolerated (later ones ignored)
        // unless any of them carries the critical flag.
        if (calendarCount > 1 && anyCalendarCritical)
        {
            error = "reject more than one calendar annotation if any critical";
            return false;
        }

        return true;
    }

    private static bool IsTimeZoneAnnotationBody(string body)
    {
        if (body.Length == 0)
        {
            return false;
        }

        // Offset form: sign HH[:MM] (minute precision only).
        if (body[0] == '+' || body[0] == '-')
        {
            int j = 0;
            string err = "";
            return TryParseUtcOffset(body, ref j, subMinutePrecision: false, out _, ref err) && j == body.Length;
        }

        // IANA name: TZLeadingChar TZChar* ('/' separated components).
        foreach (var part in body.Split('/'))
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

            if (part is "." or "..")
            {
                // "." and ".." components are allowed by the grammar
                // (legacy names) — keep them.
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

        return true;
    }

    private static bool IsValidAnnotationKey(string key)
    {
        if (key.Length == 0)
        {
            return false;
        }

        char c0 = key[0];
        if (!(c0 is >= 'a' and <= 'z' || c0 == '_'))
        {
            return false;
        }

        for (int k = 1; k < key.Length; k++)
        {
            char c = key[k];
            if (!(c is >= 'a' and <= 'z' || c is >= '0' and <= '9' || c == '-' || c == '_'))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsValidAnnotationValue(string value)
    {
        if (value.Length == 0)
        {
            return false;
        }

        foreach (var part in value.Split('-'))
        {
            if (part.Length == 0)
            {
                return false;
            }

            foreach (var c in part)
            {
                if (!char.IsAsciiLetterOrDigit(c))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static bool TryReadDigits(string s, ref int i, int count, out int value)
    {
        value = 0;
        if (i + count > s.Length)
        {
            return false;
        }

        for (int k = 0; k < count; k++)
        {
            char c = s[i + k];
            if (!IsDigit(c))
            {
                return false;
            }

            value = value * 10 + (c - '0');
        }

        i += count;
        return true;
    }

    // Fraction: 1-9 digits, scaled to nanoseconds.
    private static bool TryReadFraction(string s, ref int i, out long ns)
    {
        ns = 0;
        int digits = 0;
        while (i < s.Length && IsDigit(s[i]))
        {
            if (digits < 9)
            {
                ns = ns * 10 + (s[i] - '0');
            }

            digits++;
            i++;
        }

        if (digits == 0 || digits > 9)
        {
            return false;
        }

        for (int k = digits; k < 9; k++)
        {
            ns *= 10;
        }

        return true;
    }

    private static bool IsDigit(char c) => c is >= '0' and <= '9';
}
