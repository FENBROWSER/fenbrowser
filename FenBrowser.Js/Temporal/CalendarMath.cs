using System.Globalization;

namespace FenBrowser.Js.Temporal;

// Resolved calendar-relative view of an ISO date: the fields a Temporal
// PlainDate/PlainDateTime exposes for a non-ISO calendar.
internal readonly record struct CalendarFields(
    string? Era,
    int? EraYear,
    int Year,
    int Month,
    string MonthCode,
    int Day,
    int DaysInMonth,
    int DaysInYear,
    int MonthsInYear,
    bool InLeapYear);

// Outcome of resolving a calendar field-bag back to an ISO date.
internal enum CalendarResolveStatus
{
    Ok,
    Overflow,        // a field was out of range under overflow=reject
    OutOfRange,      // the resulting ISO date is outside the Temporal limits
    UnknownEra,
    InvalidMonthCode,
}

// Temporal calendar abstract operations for the CLDR calendars. Each calendar
// is modelled in its own native (year, monthOrdinal, day) space and bridged to
// epoch days (1970-01-01 = 0) via ToFixed/FromFixed, which lets the generic
// arithmetic/difference/field code live here once. Algorithms follow
// Dershowitz & Reingold, "Calendrical Calculations" (matching ICU/Temporal).
internal abstract class CalendarSystem
{
    // RD 1 = proleptic Gregorian 0001-01-01; epoch day 0 = 1970-01-01 = RD 719163.
    protected const long RataDieToEpoch = 719163;

    public abstract string Id { get; }

    // Number of months in the given calendar year (12 for most, 13 for Coptic/
    // Ethiopic, 12 or 13 for Hebrew).
    public abstract int MonthsInYear(int year);

    // Days in the given (year, monthOrdinal). monthOrdinal is 1..MonthsInYear.
    public abstract int DaysInMonthOrdinal(int year, int month);

    public abstract long ToFixed(int year, int month, int day);

    public abstract void FromFixed(long epochDay, out int year, out int month, out int day);

    public abstract bool InLeapYear(int year);

    // era / eraYear for a (year, epochDay). Most calendars ignore epochDay; the
    // Japanese calendar needs the full date to pick the reign era.
    public abstract (string? Era, int? EraYear) EraFor(int year, long epochDay);

    // Resolve an era code + eraYear to the native calendar year. Returns false
    // for an era this calendar does not recognise.
    public abstract bool YearFromEra(string era, int eraYear, out int year);

    // monthOrdinal -> month code ("M01", "M05L" for a leap month).
    public abstract string MonthCodeFor(int year, int month);

    // month code -> monthOrdinal within the year. Returns false when the code is
    // syntactically invalid; sets existsInYear=false when the code is valid but
    // not present in this year (e.g. a leap month code in a common year).
    public abstract bool MonthFromCode(int year, string code, out int month, out bool existsInYear);

    public virtual int DaysInYear(int year)
        => (int)(ToFixed(year + 1, 1, 1) - ToFixed(year, 1, 1));

    // ── shared helpers ──────────────────────────────────────────────────────

    // Build the full calendar-field view of an ISO date.
    public CalendarFields ToFields(IsoDate iso)
    {
        long epoch = IsoMath.CivilToEpochDays(iso.Year, iso.Month, iso.Day);
        FromFixed(epoch, out int y, out int m, out int d);
        var (era, eraYear) = EraFor(y, epoch);
        return new CalendarFields(
            era, eraYear, y, m, MonthCodeFor(y, m), d,
            DaysInMonthOrdinal(y, m), DaysInYear(y), MonthsInYear(y), InLeapYear(y));
    }

    public void ToNative(IsoDate iso, out int year, out int month, out int day)
        => FromFixed(IsoMath.CivilToEpochDays(iso.Year, iso.Month, iso.Day), out year, out month, out day);

    // Resolve a native (year, monthOrdinal, day) to an ISO date with Temporal
    // overflow handling (constrain clamps month/day; reject fails).
    public bool TryResolveToIso(int year, int month, int day, string overflow, out IsoDate iso)
    {
        iso = default;
        int miy = MonthsInYear(year);
        if (overflow == "reject")
        {
            if (month < 1 || month > miy) return false;
            int dimR = DaysInMonthOrdinal(year, month);
            if (day < 1 || day > dimR) return false;
        }
        else
        {
            month = Math.Clamp(month, 1, miy);
            int dim = DaysInMonthOrdinal(year, month);
            day = Math.Clamp(day, 1, dim);
        }

        long epoch = ToFixed(year, month, day);
        if (epoch is < IsoMath.MinEpochDay or > IsoMath.MaxEpochDay) return false;
        iso = IsoMath.EpochDaysToCivil(epoch);
        return true;
    }

    // CalendarDateAdd: add years/months (in calendar space, clamping the day to
    // the target month length) then weeks/days by epoch arithmetic.
    public IsoDate Add(IsoDate date, long years, long months, long weeks, long days, bool constrain, out bool invalid)
    {
        invalid = false;
        ToNative(date, out int y, out int mo, out int d);
        long ty = y + years;
        long tm = mo + months;
        if (ty is < -1_000_000 or > 1_000_000) { invalid = true; return date; }
        var (by, bm) = BalanceYearMonth((int)ty, tm);
        int dim = DaysInMonthOrdinal(by, bm);
        if (d > dim)
        {
            if (!constrain) { invalid = true; return date; }
            d = dim;
        }

        long epoch = ToFixed(by, bm, d) + weeks * 7 + days;
        if (epoch is < IsoMath.MinEpochDay - 400 or > IsoMath.MaxEpochDay + 400) { invalid = true; return date; }
        return IsoMath.EpochDaysToCivil(epoch);
    }

    // CalendarDateUntil. largestUnit: "year"|"month"|"week"|"day".
    public (int Years, int Months, int Weeks, long Days) Difference(IsoDate one, IsoDate two, string largestUnit)
    {
        long ep1 = IsoMath.CivilToEpochDays(one.Year, one.Month, one.Day);
        long ep2 = IsoMath.CivilToEpochDays(two.Year, two.Month, two.Day);
        int sign = ep1 < ep2 ? 1 : ep1 > ep2 ? -1 : 0;
        if (sign == 0) return (0, 0, 0, 0);

        ToNative(one, out int y1, out int m1, out int d1);
        int years = 0, months = 0;
        if (largestUnit is "year" or "month")
        {
            ToNative(two, out int y2, out _, out _);
            int candidateYears = y2 - y1;
            if (candidateYears != 0) candidateYears -= sign;
            while (!Surpasses(sign, BalanceYearMonth(y1 + candidateYears, m1), d1, two))
            {
                years = candidateYears;
                candidateYears += sign;
            }

            int candidateMonths = sign;
            var inter = BalanceYearMonth(y1 + years, m1 + candidateMonths);
            while (!Surpasses(sign, inter, d1, two))
            {
                months = candidateMonths;
                candidateMonths += sign;
                inter = BalanceYearMonth(inter.Year, inter.Month + sign);
            }

            if (largestUnit == "month")
            {
                // Constant months-per-year for the calendars handled here.
                months += years * MonthsInYear(y1);
                years = 0;
            }
        }

        var (fy, fm) = BalanceYearMonth(y1 + years, m1 + months);
        int cd = Math.Min(d1, DaysInMonthOrdinal(fy, fm));
        long days = ep2 - ToFixed(fy, fm, cd);
        long weeks = 0;
        if (largestUnit == "week") { weeks = days / 7; days %= 7; }
        return (years, months, (int)weeks, days);
    }

    private (int Year, int Month) BalanceYearMonth(int year, long month)
    {
        int y = year;
        long m = month;
        while (m > MonthsInYear(y)) { m -= MonthsInYear(y); y++; }
        while (m < 1) { y--; m += MonthsInYear(y); }
        return (y, (int)m);
    }

    private bool Surpasses(int sign, (int Year, int Month) ym, int day, IsoDate two)
    {
        ToNative(two, out int ty, out int tm, out int td);
        if (ym.Year != ty) return sign * (ym.Year - ty) > 0;
        if (ym.Month != tm) return sign * (ym.Month - tm) > 0;
        int dd = Math.Min(day, DaysInMonthOrdinal(ym.Year, ym.Month));
        if (dd != td) return sign * (dd - td) > 0;
        return false;
    }

    // Standard "month code MnnL?" parser shared by calendars without leap months.
    protected static bool ParseSimpleMonthCode(string code, out int ordinal, out bool leap)
    {
        ordinal = 0;
        leap = false;
        if (code.Length is not (3 or 4) || code[0] != 'M' || !char.IsAsciiDigit(code[1]) || !char.IsAsciiDigit(code[2]))
            return false;
        if (code.Length == 4)
        {
            if (code[3] != 'L') return false;
            leap = true;
        }
        ordinal = (code[1] - '0') * 10 + (code[2] - '0');
        return ordinal >= 1;
    }
}

internal static class CalendarMath
{
    private static readonly Dictionary<string, CalendarSystem> _systems = Build();

    private static Dictionary<string, CalendarSystem> Build()
    {
        var list = new CalendarSystem[]
        {
            new GregorianCalendarSystem("gregory"),
            new BuddhistCalendarSystem(),
            new RocCalendarSystem(),
            new JapaneseCalendarSystem(),
            new CopticCalendarSystem(),
            new EthiopicCalendarSystem(),
            new EthioaaCalendarSystem(),
            new IndianCalendarSystem(),
            new IslamicCalendarSystem("islamic-civil", civilEpoch: true),
            new IslamicCalendarSystem("islamic-tbla", civilEpoch: false),
            new PersianCalendarSystem(),
            new HebrewCalendarSystem(),
            new EastAsianCalendarSystem("chinese", new System.Globalization.ChineseLunisolarCalendar()),
            new EastAsianCalendarSystem("dangi", new System.Globalization.KoreanLunisolarCalendar()),
        };
        var map = new Dictionary<string, CalendarSystem>(StringComparer.Ordinal);
        foreach (var c in list) map[c.Id] = c;
        return map;
    }

    // True when this calendar id has a real (non-ISO) implementation here.
    public static bool IsSupportedNonIso(string calendarId)
        => _systems.ContainsKey(calendarId);

    public static CalendarSystem? Get(string calendarId)
        => _systems.TryGetValue(calendarId, out var c) ? c : null;
}
