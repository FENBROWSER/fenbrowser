namespace FenBrowser.Js.Temporal;

// Proleptic Gregorian calendar (Temporal "gregory"): the ISO day grid with
// era/eraYear labelling. Buddhist/ROC/Japanese reuse the same grid and only
// relabel the year and era, so they derive from this.
internal class GregorianCalendarSystem : CalendarSystem
{
    private readonly string _id;
    public GregorianCalendarSystem(string id) => _id = id;
    public override string Id => _id;

    // Map between the calendar's displayed year and the proleptic-Gregorian
    // (ISO) year that shares the same day grid.
    protected virtual int ToIsoYear(int calYear) => calYear;
    protected virtual int ToCalYear(int isoYear) => isoYear;

    public override int MonthsInYear(int year) => 12;

    public override int DaysInMonthOrdinal(int year, int month)
        => IsoMath.DaysInMonth(ToIsoYear(year), month);

    public override long ToFixed(int year, int month, int day)
        => IsoMath.CivilToEpochDays(ToIsoYear(year), month, day);

    public override void FromFixed(long epochDay, out int year, out int month, out int day)
    {
        var iso = IsoMath.EpochDaysToCivil(epochDay);
        year = ToCalYear(iso.Year);
        month = iso.Month;
        day = iso.Day;
    }

    public override bool InLeapYear(int year) => IsoMath.IsLeapYear(ToIsoYear(year));

    public override (string?, int?) EraFor(int year, long epochDay)
        => year > 0 ? ("ce", year) : ("bce", 1 - year);

    public override bool YearFromEra(string era, int eraYear, out int year)
    {
        switch (era)
        {
            case "ce" or "ad": year = eraYear; return true;
            case "bce" or "bc": year = 1 - eraYear; return true;
            default: year = 0; return false;
        }
    }

    public override string MonthCodeFor(int year, int month) => $"M{month:D2}";

    public override bool MonthFromCode(int year, string code, out int month, out bool existsInYear)
    {
        existsInYear = false;
        if (!ParseSimpleMonthCode(code, out int ord, out bool leap) || leap || ord > 12)
        {
            month = 0;
            return false;
        }

        month = ord;
        existsInYear = ord is >= 1 and <= 12;
        return true;
    }
}

// Thai Buddhist: Gregorian grid, year = ISO year + 543, single era "be".
internal sealed class BuddhistCalendarSystem : GregorianCalendarSystem
{
    public BuddhistCalendarSystem() : base("buddhist") { }
    protected override int ToIsoYear(int calYear) => calYear - 543;
    protected override int ToCalYear(int isoYear) => isoYear + 543;
    public override (string?, int?) EraFor(int year, long epochDay) => ("be", year);
    public override bool YearFromEra(string era, int eraYear, out int year)
    {
        if (era == "be") { year = eraYear; return true; }
        year = 0;
        return false;
    }
}

// Minguo / ROC: Gregorian grid, year = ISO year - 1911, eras roc / broc.
internal sealed class RocCalendarSystem : GregorianCalendarSystem
{
    public RocCalendarSystem() : base("roc") { }
    protected override int ToIsoYear(int calYear) => calYear + 1911;
    protected override int ToCalYear(int isoYear) => isoYear - 1911;
    public override (string?, int?) EraFor(int year, long epochDay)
        => year > 0 ? ("roc", year) : ("broc", 1 - year);
    public override bool YearFromEra(string era, int eraYear, out int year)
    {
        switch (era)
        {
            case "roc": year = eraYear; return true;
            case "broc": year = 1 - eraYear; return true;
            default: year = 0; return false;
        }
    }
}

// Japanese imperial calendar: Gregorian grid with reign eras. Year is the
// related Gregorian year; the reign era is chosen by the full date, and only
// applies from Japan's Gregorian adoption (Meiji 6 = 1873-01-01) onward —
// earlier dates fall back to ce/bce, matching ICU/Temporal.
internal sealed class JapaneseCalendarSystem : GregorianCalendarSystem
{
    public JapaneseCalendarSystem() : base("japanese") { }

    private static readonly long Meiji = IsoMath.CivilToEpochDays(1873, 1, 1);
    private static readonly long Taisho = IsoMath.CivilToEpochDays(1912, 7, 30);
    private static readonly long Showa = IsoMath.CivilToEpochDays(1926, 12, 25);
    private static readonly long Heisei = IsoMath.CivilToEpochDays(1989, 1, 8);
    private static readonly long Reiwa = IsoMath.CivilToEpochDays(2019, 5, 1);

    public override (string?, int?) EraFor(int year, long epochDay)
    {
        if (epochDay >= Reiwa) return ("reiwa", year - 2018);
        if (epochDay >= Heisei) return ("heisei", year - 1988);
        if (epochDay >= Showa) return ("showa", year - 1925);
        if (epochDay >= Taisho) return ("taisho", year - 1911);
        if (epochDay >= Meiji) return ("meiji", year - 1867);
        return year > 0 ? ("ce", year) : ("bce", 1 - year);
    }

    public override bool YearFromEra(string era, int eraYear, out int year)
    {
        switch (era)
        {
            case "meiji": year = 1867 + eraYear; return true;
            case "taisho": year = 1911 + eraYear; return true;
            case "showa": year = 1925 + eraYear; return true;
            case "heisei": year = 1988 + eraYear; return true;
            case "reiwa": year = 2018 + eraYear; return true;
            case "ce" or "ad": year = eraYear; return true;
            case "bce" or "bc": year = 1 - eraYear; return true;
            default: year = 0; return false;
        }
    }
}

// Coptic and Ethiopic share a 13-month structure (12 months of 30 days + a
// final month of 5 or 6 days) and the leap rule year mod 4 == 3. They differ
// only in epoch and era labelling. Algorithm: Dershowitz & Reingold.
internal abstract class CopticLikeCalendar : CalendarSystem
{
    private readonly string _id;
    private readonly long _epochRd;

    protected CopticLikeCalendar(string id, long epochRd)
    {
        _id = id;
        _epochRd = epochRd;
    }

    public override string Id => _id;

    // The year used by the arithmetic formulas (Coptic/Ethiopic-mihret year).
    protected virtual int ToInternalYear(int year) => year;
    protected virtual int FromInternalYear(int internalYear) => internalYear;

    public override int MonthsInYear(int year) => 13;

    public override long ToFixed(int year, int month, int day)
    {
        long iy = ToInternalYear(year);
        long rd = _epochRd - 1 + 365 * (iy - 1) + FloorDiv(iy, 4) + 30 * (month - 1) + day;
        return rd - RataDieToEpoch;
    }

    public override void FromFixed(long epochDay, out int year, out int month, out int day)
    {
        long date = epochDay + RataDieToEpoch;
        long iy = FloorDiv(4 * (date - _epochRd) + 1463, 1461);
        long yearStart = _epochRd - 1 + 365 * (iy - 1) + FloorDiv(iy, 4) + 1;
        long m = FloorDiv(date - yearStart, 30) + 1;
        long monthStart = yearStart + 30 * (m - 1);
        long d = date - monthStart + 1;
        year = FromInternalYear((int)iy);
        month = (int)m;
        day = (int)d;
    }

    public override int DaysInMonthOrdinal(int year, int month)
    {
        if (month <= 12) return 30;
        return InLeapYear(year) ? 6 : 5;
    }

    public override bool InLeapYear(int year) => Mod(ToInternalYear(year), 4) == 3;

    public override string MonthCodeFor(int year, int month) => $"M{month:D2}";

    public override bool MonthFromCode(int year, string code, out int month, out bool existsInYear)
    {
        existsInYear = false;
        if (!ParseSimpleMonthCode(code, out int ord, out bool leap) || leap || ord > 13)
        {
            month = 0;
            return false;
        }

        month = ord;
        existsInYear = ord is >= 1 and <= 13;
        return true;
    }

    protected static long FloorDiv(long a, long b)
    {
        long q = a / b;
        if (a % b != 0 && (a < 0) != (b < 0)) q--;
        return q;
    }

    protected static long Mod(long a, long b)
    {
        long r = a % b;
        if (r != 0 && (r < 0) != (b < 0)) r += b;
        return r;
    }
}

// Coptic (Anno Martyrum). Epoch: RD 103605 = Julian 284-08-29.
internal sealed class CopticCalendarSystem : CopticLikeCalendar
{
    public CopticCalendarSystem() : base("coptic", 103605) { }
    public override (string?, int?) EraFor(int year, long epochDay) => ("am", year);
    public override bool YearFromEra(string era, int eraYear, out int year)
    {
        if (era == "am") { year = eraYear; return true; }
        year = 0;
        return false;
    }
}

// Ethiopic. Epoch: RD 2796 = Julian 8-08-29. Two eras: amete-mihret ("am",
// incarnation) for years >= 1, amete-alem ("aa") for the proleptic remainder,
// where the amete-alem year = mihret year + 5500.
internal sealed class EthiopicCalendarSystem : CopticLikeCalendar
{
    public EthiopicCalendarSystem() : base("ethiopic", 2796) { }
    public override (string?, int?) EraFor(int year, long epochDay)
        => year >= 1 ? ("am", year) : ("aa", year + 5500);
    public override bool YearFromEra(string era, int eraYear, out int year)
    {
        switch (era)
        {
            case "am": year = eraYear; return true;
            case "aa": year = eraYear - 5500; return true;
            default: year = 0; return false;
        }
    }
}

// Ethiopic Amete Alem ("ethioaa"): the same grid as Ethiopic with a single
// continuous era "aa", numbered 5500 years ahead of the mihret year.
internal sealed class EthioaaCalendarSystem : CopticLikeCalendar
{
    public EthioaaCalendarSystem() : base("ethioaa", 2796) { }
    protected override int ToInternalYear(int year) => year - 5500;
    protected override int FromInternalYear(int internalYear) => internalYear + 5500;
    public override (string?, int?) EraFor(int year, long epochDay) => ("aa", year);
    public override bool YearFromEra(string era, int eraYear, out int year)
    {
        if (era == "aa") { year = eraYear; return true; }
        year = 0;
        return false;
    }
}
