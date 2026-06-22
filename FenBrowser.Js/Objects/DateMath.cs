using System;
using System.Globalization;

namespace FenBrowser.Js.Objects;

// ECMA-262 §21.4.1 time abstract operations, implemented on IEEE doubles so the full
// ECMAScript time range (±8.64e15 ms, ~±275760 years) is representable. The engine's
// previous Date implementation funneled through .NET DateTimeOffset, which only spans
// years 1..9999 and threw ArgumentOutOfRangeException (crash) for any valid date
// outside that window. LocalTZA is 0 in this engine, so local time == UTC.
internal static class DateMath
{
    public const double MsPerDay = 86_400_000.0;
    public const double MsPerHour = 3_600_000.0;
    public const double MsPerMinute = 60_000.0;
    public const double MsPerSecond = 1_000.0;

    // §5.2.5 modulo: result has the sign of the divisor (always non-negative here).
    private static double Modulo(double a, double b) => a - b * Math.Floor(a / b);

    private static double Trunc(double x) => x < 0 ? Math.Ceiling(x) : Math.Floor(x);

    // ECMA-262 7.1.4 ToInteger: sign(number) * floor(abs(number))
    public static double ToInteger(double x)
    {
        if (double.IsNaN(x) || x == 0.0) return 0.0;
        if (double.IsInfinity(x)) return x;
        return x < 0 ? Math.Ceiling(x) : Math.Floor(x);
    }

    private static readonly int[] MonthStart = { 0, 31, 59, 90, 120, 151, 181, 212, 243, 273, 304, 334 };
    private static readonly string[] DayNames = { "Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat" };
    private static readonly string[] MonthNames = { "Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec" };

    public static double Day(double t) => Math.Floor(t / MsPerDay);
    public static double TimeWithinDay(double t) => Modulo(t, MsPerDay);

    private static bool IsLeap(double y) => Modulo(y, 4) == 0 && (Modulo(y, 100) != 0 || Modulo(y, 400) == 0);
    public static int DaysInYear(double y) => IsLeap(y) ? 366 : 365;

    // §21.4.1.3 DayFromYear (kept on doubles so extreme years don't overflow int).
    public static double DayFromYear(double y) =>
        365.0 * (y - 1970.0)
        + Math.Floor((y - 1969.0) / 4.0)
        - Math.Floor((y - 1901.0) / 100.0)
        + Math.Floor((y - 1601.0) / 400.0);

    public static double TimeFromYear(double y) => MsPerDay * DayFromYear(y);

    // §21.4.1.5 YearFromTime: start from a calendar estimate then walk to the exact year.
    public static int YearFromTime(double t)
    {
        var y = (int)Math.Floor(t / (MsPerDay * 365.2425)) + 1970;
        while (TimeFromYear(y) > t) y--;
        while (TimeFromYear(y + 1) <= t) y++;
        return y;
    }

    public static bool InLeapYear(double t) => IsLeap(YearFromTime(t));

    public static int DayWithinYear(double t) => (int)(Day(t) - DayFromYear(YearFromTime(t)));

    public static int MonthFromTime(double t)
    {
        var d = DayWithinYear(t);
        var leap = InLeapYear(t) ? 1 : 0;
        for (var m = 11; m >= 0; m--)
        {
            var start = MonthStart[m] + (m > 1 ? leap : 0);
            if (d >= start) return m;
        }
        return 0;
    }

    public static int DateFromTime(double t)
    {
        var d = DayWithinYear(t);
        var m = MonthFromTime(t);
        var leap = InLeapYear(t) ? 1 : 0;
        var before = MonthStart[m] + (m > 1 ? leap : 0);
        return d - before + 1;
    }

    public static int WeekDay(double t) => (int)Modulo(Day(t) + 4, 7);

    public static int HoursFromTime(double t) => (int)Modulo(Math.Floor(t / MsPerHour), 24);
    public static int MinFromTime(double t) => (int)Modulo(Math.Floor(t / MsPerMinute), 60);
    public static int SecFromTime(double t) => (int)Modulo(Math.Floor(t / MsPerSecond), 60);
    public static int MsFromTime(double t) => (int)Modulo(t, 1000);

    // §21.4.1.13 MakeTime
    public static double MakeTime(double hour, double min, double sec, double ms)
    {
        if (!double.IsFinite(hour) || !double.IsFinite(min) || !double.IsFinite(sec) || !double.IsFinite(ms))
            return double.NaN;
        return Trunc(hour) * MsPerHour + Trunc(min) * MsPerMinute + Trunc(sec) * MsPerSecond + Trunc(ms);
    }

    // §21.4.1.14 MakeDay
    public static double MakeDay(double year, double month, double date)
    {
        if (!double.IsFinite(year) || !double.IsFinite(month) || !double.IsFinite(date))
            return double.NaN;
        var y = Trunc(year);
        var m = Trunc(month);
        var dt = Trunc(date);
        var ym = y + Math.Floor(m / 12.0);
        var mn = (int)Modulo(m, 12.0); // 0..11
        if (!double.IsFinite(ym)) return double.NaN;
        var leap = IsLeap(ym) ? 1 : 0;
        var before = MonthStart[mn] + (mn > 1 ? leap : 0);
        return DayFromYear(ym) + before + dt - 1.0;
    }

    // §21.4.1.15 MakeDate
    public static double MakeDate(double day, double time)
    {
        if (!double.IsFinite(day) || !double.IsFinite(time)) return double.NaN;
        return day * MsPerDay + time;
    }

    // §21.4.1.31 TimeClip
    public static double TimeClip(double t)
    {
        if (!double.IsFinite(t) || Math.Abs(t) > 8.64e15) return double.NaN;
        return Trunc(t) + 0.0; // +0 normalises -0
    }

    // ECMA-262 21.4.4.41 Date.prototype.toString: "Www Mmm DD YYYY HH:mm:ss GMT+HHmm"
    // LocalTZA is 0 in this engine, so the timezone suffix is always "GMT+0000".
    public static string DateToString(double t)
    {
        if (!double.IsFinite(t)) return "Invalid Date";
        return string.Format(CultureInfo.InvariantCulture,
            "{0} {1} {2:D2} {3} {4:D2}:{5:D2}:{6:D2} GMT+0000",
            DayNames[WeekDay(t)], MonthNames[MonthFromTime(t)], DateFromTime(t),
            FormatYear4(t), HoursFromTime(t), MinFromTime(t), SecFromTime(t));
    }

    private static string FormatYear4(double t)
    {
        var y = YearFromTime(t);
        return y >= 0
            ? y.ToString("D4", CultureInfo.InvariantCulture)
            : "-" + Math.Abs(y).ToString("D4", CultureInfo.InvariantCulture);
    }

    // ECMA-262 21.4.3.2 Date.parse semantics: ISO-8601, toString format, and the
    // looser forms .NET understands. Returns NaN on failure. Applies TimeClip.
    public static double ParseDateValue(string text)
    {
        if (text.Length >= 4 && text.All(char.IsDigit))
        {
            long year = long.Parse(text, CultureInfo.InvariantCulture);
            if (year >= 0 && year <= 9999)
            {
                var d = MakeDate(MakeDay(year, 0, 1), MakeTime(0, 0, 0, 0));
                return TimeClip(d);
            }
        }

        if (text.Length > 0 && (text[0] == '+' || text[0] == '-') && text.Length >= 12)
        {
            var result = TryParseExtendedIsoDate(text);
            if (result.HasValue) return result.Value;
        }

        {
            var dtResult = TryParseDateToStringFormat(text);
            if (dtResult.HasValue) return dtResult.Value;
        }

        if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var parsed))
        {
            return TimeClip(parsed.ToUnixTimeMilliseconds());
        }
        return double.NaN;
    }

    private static double? TryParseExtendedIsoDate(string text)
    {
        if (text.Length < 13) return null;
        var sign = text[0] == '-' ? -1 : 1;
        int pos = 1;
        long year = 0;
        while (pos < text.Length && char.IsDigit(text[pos]))
        {
            year = year * 10 + (text[pos] - '0');
            pos++;
        }
        var yearDigits = pos - 1;
        if (yearDigits < 4) return null;
        if (sign < 0 && year == 0) return null;
        if (sign < 0) year = -year;
        if (pos >= text.Length || text[pos] != '-') return null;
        pos++;
        if (pos + 1 >= text.Length) return null;
        var month = (text[pos] - '0') * 10 + (text[pos + 1] - '0');
        pos += 2;
        if (month < 1 || month > 12) return null;
        if (pos >= text.Length || text[pos] != '-') return null;
        pos++;
        if (pos + 1 >= text.Length) return null;
        var day = (text[pos] - '0') * 10 + (text[pos + 1] - '0');
        pos += 2;
        if (day < 1 || day > 31) return null;
        long hours = 0, minutes = 0, seconds = 0, ms = 0;
        if (pos < text.Length && (text[pos] == 'T' || text[pos] == ' '))
        {
            pos++;
            if (pos + 1 >= text.Length) return null;
            hours = (text[pos] - '0') * 10 + (text[pos + 1] - '0');
            pos += 2;
            if (hours > 24) return null;
            if (pos < text.Length && text[pos] == ':')
            {
                pos++;
                if (pos + 1 >= text.Length) return null;
                minutes = (text[pos] - '0') * 10 + (text[pos + 1] - '0');
                pos += 2;
                if (minutes > 59) return null;
            }
            if (pos < text.Length && text[pos] == ':')
            {
                pos++;
                if (pos + 1 >= text.Length) return null;
                seconds = (text[pos] - '0') * 10 + (text[pos + 1] - '0');
                pos += 2;
                if (seconds > 59) return null;
            }
            if (pos < text.Length && text[pos] == '.')
            {
                pos++;
                var msDigits = 0;
                ms = 0;
                while (pos < text.Length && char.IsDigit(text[pos]) && msDigits < 3)
                {
                    ms = ms * 10 + (text[pos] - '0');
                    pos++;
                    msDigits++;
                }
                while (msDigits < 3) { ms *= 10; msDigits++; }
            }
        }
        var dateMs = MakeDate(MakeDay(year, month - 1, day), MakeTime(hours, minutes, seconds, ms));
        return TimeClip(dateMs);
    }

    private static double? TryParseDateToStringFormat(string text)
    {
        var t = text;
        var commaIdx = t.IndexOf(',');
        if (commaIdx >= 0 && commaIdx < 5)
            t = t.Substring(commaIdx + 1).TrimStart();
        else if (t.Length > 4 && t[3] == ' ')
            t = t.Substring(4);

        var gmtIdx = t.LastIndexOf("GMT", StringComparison.Ordinal);
        if (gmtIdx < 0) return null;
        var datePart = t.Substring(0, gmtIdx).Trim();
        var tzStr = t.Substring(gmtIdx + 3);

        var tzOffset = TimeSpan.Zero;
        if (tzStr.Length >= 5 && (tzStr[0] == '+' || tzStr[0] == '-'))
        {
            if (int.TryParse(tzStr.Substring(0, 3), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var tzHours) &&
                int.TryParse(tzStr.AsSpan(3, 2), NumberStyles.None, CultureInfo.InvariantCulture, out var tzMins))
            {
                tzOffset = new TimeSpan(tzHours, tzMins, 0);
            }
        }

        var parts = datePart.Split(' ');
        if (parts.Length >= 4)
        {
            var mIdx = Array.IndexOf(MonthNames, parts[0]);
            if (mIdx >= 0 && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var dd) &&
                int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var yyyy))
            {
                var timeParts = parts[3].Split(':');
                if (timeParts.Length == 3 &&
                    int.TryParse(timeParts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var hh) &&
                    int.TryParse(timeParts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var mm) &&
                    int.TryParse(timeParts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var ss))
                {
                    try
                    {
                        var dto = new DateTimeOffset(yyyy, mIdx + 1, dd, hh, mm, ss, TimeSpan.Zero);
                        return TimeClip((dto - tzOffset).ToUnixTimeMilliseconds());
                    }
                    catch (ArgumentOutOfRangeException) { }
                }
            }
        }

        if (DateTimeOffset.TryParseExact(datePart, "dd MMM yyyy HH:mm:ss",
                CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dto2))
        {
            return TimeClip(dto2.ToUnixTimeMilliseconds());
        }
        return null;
    }
}
