using System;

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

    private static readonly int[] MonthStart = { 0, 31, 59, 90, 120, 151, 181, 212, 243, 273, 304, 334 };

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
}
