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

// Indian national calendar (Saka). 12 months: Chaitra (30, or 31 in a leap year),
// then five months of 31 and six of 30. The year begins on Chaitra 1 = Gregorian
// March 22 (March 21 when Gregorian year S+78 is leap); leap years track Gregorian.
internal sealed class IndianCalendarSystem : CalendarSystem
{
    public override string Id => "indian";
    public override int MonthsInYear(int year) => 12;
    public override bool InLeapYear(int year) => IsoMath.IsLeapYear(year + 78);

    private static long YearStart(int sakaYear)
    {
        int g = sakaYear + 78;
        return IsoMath.CivilToEpochDays(g, 3, IsoMath.IsLeapYear(g) ? 21 : 22);
    }

    public override int DaysInMonthOrdinal(int year, int month)
        => month == 1 ? (InLeapYear(year) ? 31 : 30) : (month <= 6 ? 31 : 30);

    public override long ToFixed(int year, int month, int day)
    {
        long start = YearStart(year);
        int chaitra = InLeapYear(year) ? 31 : 30;
        long before = month == 1 ? 0
            : month <= 6 ? chaitra + 31L * (month - 2)
            : chaitra + 31L * 5 + 30L * (month - 7);
        return start + before + (day - 1);
    }

    public override void FromFixed(long epochDay, out int year, out int month, out int day)
    {
        int g = IsoMath.EpochDaysToCivil(epochDay).Year;
        int saka = g - 78;
        long start = YearStart(saka);
        if (epochDay < start) { saka -= 1; start = YearStart(saka); }
        long doy = epochDay - start;
        year = saka;
        int chaitra = InLeapYear(saka) ? 31 : 30;
        if (doy < chaitra) { month = 1; day = (int)doy + 1; return; }
        doy -= chaitra;
        if (doy < 31 * 5) { month = 2 + (int)(doy / 31); day = (int)(doy % 31) + 1; return; }
        doy -= 31 * 5;
        month = 7 + (int)(doy / 30);
        day = (int)(doy % 30) + 1;
    }

    public override (string?, int?) EraFor(int year, long epochDay) => ("shaka", year);
    public override bool YearFromEra(string era, int eraYear, out int year)
    {
        if (era == "shaka") { year = eraYear; return true; }
        year = 0;
        return false;
    }

    public override string MonthCodeFor(int year, int month) => $"M{month:D2}";
    public override bool MonthFromCode(int year, string code, out int month, out bool existsInYear)
    {
        existsInYear = false;
        if (!ParseSimpleMonthCode(code, out int ord, out bool leap) || leap || ord > 12) { month = 0; return false; }
        month = ord;
        existsInYear = ord is >= 1 and <= 12;
        return true;
    }
}

// Islamic Umm Al-Qura calendar (islamic-umalqura) backed by .NET's
// System.Globalization.UmAlQuraCalendar. The .NET implementation covers the
// range 1318–1500 AH (1900–2077 CE); dates outside this range fall back to the
// tabular Islamic (civil) algorithm.
internal sealed class IslamicUmalquraCalendarSystem : CalendarSystem
{
    private static readonly System.Globalization.UmAlQuraCalendar _netCal = new();
    private const long TableEpochRd = 227015; // same epoch as islamic-civil

    // .NET UmAlQuraCalendar range in epoch days.
    private static readonly long _netMinEpoch = IsoMath.CivilToEpochDays(
        _netCal.MinSupportedDateTime.Year, _netCal.MinSupportedDateTime.Month, _netCal.MinSupportedDateTime.Day);
    private static readonly long _netMaxEpoch = IsoMath.CivilToEpochDays(
        _netCal.MaxSupportedDateTime.Year, _netCal.MaxSupportedDateTime.Month, _netCal.MaxSupportedDateTime.Day);
    private static readonly int _netMinYear = _netCal.GetYear(_netCal.MinSupportedDateTime);
    private static readonly int _netMaxYear = _netCal.GetYear(_netCal.MaxSupportedDateTime);

    public override string Id => "islamic-umalqura";
    public override int MonthsInYear(int year) => 12;

    private static bool InRange(int year) => year >= _netMinYear && year <= _netMaxYear;

    public override bool InLeapYear(int year) => DaysInYear(year) == 355;

    public override int DaysInMonthOrdinal(int year, int month)
    {
        if (month == 12) return InLeapYear(year) ? 30 : 29;
        return month % 2 == 1 ? 30 : 29;
    }

    public override long ToFixed(int year, int month, int day)
    {
        try
        {
            var dt = _netCal.ToDateTime(year, month, day, 0, 0, 0, 0);
            return IsoMath.CivilToEpochDays(dt.Year, dt.Month, dt.Day);
        }
        catch
        {
            // Tabular Islamic fallback (same epoch as islamic-civil).
            long rd = TableEpochRd - 1 + (year - 1L) * 354 + FloorDiv(3 + 11L * year, 30)
                      + 29L * (month - 1) + FloorDiv(month, 2) + day;
            return rd - RataDieToEpoch;
        }
    }

    public override void FromFixed(long epochDay, out int year, out int month, out int day)
    {
        if (epochDay >= _netMinEpoch && epochDay <= _netMaxEpoch)
        {
            var iso = IsoMath.EpochDaysToCivil(epochDay);
            var dt = new DateTime(iso.Year, iso.Month, iso.Day);
            year = _netCal.GetYear(dt);
            month = _netCal.GetMonth(dt);
            day = _netCal.GetDayOfMonth(dt);
            return;
        }

        // Tabular Islamic fallback.
        long date = epochDay + RataDieToEpoch;
        long y = FloorDiv(30 * (date - TableEpochRd) + 10646, 10631);
        long priorDays = date - (TableToFixed((int)y, 1, 1) + RataDieToEpoch);
        long m = Math.Min(12, FloorDiv(11 * priorDays + 330, 325));
        long monthStart = TableToFixed((int)y, (int)m, 1) + RataDieToEpoch;
        long d = date - monthStart + 1;
        year = (int)y;
        month = (int)m;
        day = (int)d;
    }

    private long TableToFixed(int year, int month, int day)
    {
        long rd = TableEpochRd - 1 + (year - 1L) * 354 + FloorDiv(3 + 11L * year, 30)
                  + 29L * (month - 1) + FloorDiv(month, 2) + day;
        return rd - RataDieToEpoch;
    }

    public override (string?, int?) EraFor(int year, long epochDay)
        => year >= 1 ? ("ah", year) : ("bh", 1 - year);

    public override bool YearFromEra(string era, int eraYear, out int year)
    {
        switch (era)
        {
            case "ah": year = eraYear; return true;
            case "bh": year = 1 - eraYear; return true;
            default: year = 0; return false;
        }
    }

    public override string MonthCodeFor(int year, int month) => $"M{month:D2}";
    public override bool MonthFromCode(int year, string code, out int month, out bool existsInYear)
    {
        existsInYear = false;
        if (!ParseSimpleMonthCode(code, out int ord, out bool leap) || leap || ord > 12) { month = 0; return false; }
        month = ord;
        existsInYear = ord is >= 1 and <= 12;
        return true;
    }

    private static long FloorDiv(long a, long b) { long q = a / b; if (a % b != 0 && (a < 0) != (b < 0)) q--; return q; }
    private static long Mod(long a, long b) { long r = a % b; if (r != 0 && (r < 0) != (b < 0)) r += b; return r; }
}

// Tabular Islamic calendar (islamic-civil and islamic-tbla). 12 alternating
// months of 30/29 days; the 30-year cycle has 11 leap years (extra day in the
// 12th month). islamic-civil and islamic-tbla differ only by epoch (Fri/Thu).
internal sealed class IslamicCalendarSystem : CalendarSystem
{
    private readonly string _id;
    private readonly long _epochRd;

    public IslamicCalendarSystem(string id, bool civilEpoch)
    {
        _id = id;
        _epochRd = civilEpoch ? 227015 : 227014;
    }

    public override string Id => _id;
    public override int MonthsInYear(int year) => 12;
    public override bool InLeapYear(int year) => Mod(14 + 11L * year, 30) < 11;

    public override int DaysInMonthOrdinal(int year, int month)
    {
        if (month == 12) return InLeapYear(year) ? 30 : 29;
        return month % 2 == 1 ? 30 : 29;
    }

    public override long ToFixed(int year, int month, int day)
    {
        long rd = _epochRd - 1 + (year - 1L) * 354 + FloorDiv(3 + 11L * year, 30)
                  + 29L * (month - 1) + FloorDiv(month, 2) + day;
        return rd - RataDieToEpoch;
    }

    public override void FromFixed(long epochDay, out int year, out int month, out int day)
    {
        long date = epochDay + RataDieToEpoch;
        long y = FloorDiv(30 * (date - _epochRd) + 10646, 10631);
        long priorDays = date - (ToFixed((int)y, 1, 1) + RataDieToEpoch);
        long m = Math.Min(12, FloorDiv(11 * priorDays + 330, 325));
        long monthStart = ToFixed((int)y, (int)m, 1) + RataDieToEpoch;
        long d = date - monthStart + 1;
        year = (int)y;
        month = (int)m;
        day = (int)d;
    }

    public override (string?, int?) EraFor(int year, long epochDay)
        => year >= 1 ? ("ah", year) : ("bh", 1 - year);
    public override bool YearFromEra(string era, int eraYear, out int year)
    {
        switch (era)
        {
            case "ah": year = eraYear; return true;
            case "bh": year = 1 - eraYear; return true;
            default: year = 0; return false;
        }
    }

    public override string MonthCodeFor(int year, int month) => $"M{month:D2}";
    public override bool MonthFromCode(int year, string code, out int month, out bool existsInYear)
    {
        existsInYear = false;
        if (!ParseSimpleMonthCode(code, out int ord, out bool leap) || leap || ord > 12) { month = 0; return false; }
        month = ord;
        existsInYear = ord is >= 1 and <= 12;
        return true;
    }

    private static long FloorDiv(long a, long b)
    {
        long q = a / b;
        if (a % b != 0 && (a < 0) != (b < 0)) q--;
        return q;
    }

    private static long Mod(long a, long b)
    {
        long r = a % b;
        if (r != 0 && (r < 0) != (b < 0)) r += b;
        return r;
    }
}

// Arithmetic Persian (Solar Hijri) calendar, 2820-year cycle (Dershowitz &
// Reingold), matching ICU. Months 1-6 have 31 days, 7-11 have 30, month 12 has
// 29 (30 in a leap year). Single era "ap".
internal sealed class PersianCalendarSystem : CalendarSystem
{
    private const long PersianEpochRd = 226896;

    public override string Id => "persian";
    public override int MonthsInYear(int year) => 12;

    public override long ToFixed(int year, int month, int day)
    {
        long yPrime = year > 0 ? year - 474 : year - 473;
        long yearInCycle = Mod(yPrime, 2820) + 474;
        long rd = PersianEpochRd - 1
                  + 1029983 * FloorDiv(yPrime, 2820)
                  + 365 * (yearInCycle - 1)
                  + FloorDiv(31 * yearInCycle - 5, 128)
                  + (month <= 7 ? 31L * (month - 1) : 30L * (month - 1) + 6)
                  + day;
        return rd - RataDieToEpoch;
    }

    private long YearFromFixed(long date)
    {
        long d0 = date - (ToFixed(475, 1, 1) + RataDieToEpoch);
        long n2820 = FloorDiv(d0, 1029983);
        long d1 = Mod(d0, 1029983);
        long y2820 = d1 == 1029982 ? 2820 : FloorDiv(2816 * d1 + 1031337, 1028522);
        long year = 474 + 2820 * n2820 + y2820;
        return year > 0 ? year : year - 1;
    }

    public override void FromFixed(long epochDay, out int year, out int month, out int day)
    {
        long date = epochDay + RataDieToEpoch;
        long y = YearFromFixed(date);
        long dayOfYear = 1 + date - (ToFixed((int)y, 1, 1) + RataDieToEpoch);
        long m = dayOfYear <= 186 ? CeilDiv(dayOfYear, 31) : CeilDiv(dayOfYear - 6, 30);
        long monthStart = ToFixed((int)y, (int)m, 1) + RataDieToEpoch;
        year = (int)y;
        month = (int)m;
        day = (int)(date - monthStart + 1);
    }

    public override int DaysInMonthOrdinal(int year, int month)
    {
        if (month <= 6) return 31;
        if (month <= 11) return 30;
        return InLeapYear(year) ? 30 : 29;
    }

    public override bool InLeapYear(int year)
        => ToFixed(year + 1, 1, 1) - ToFixed(year, 1, 1) == 366;

    public override (string?, int?) EraFor(int year, long epochDay) => ("ap", year);
    public override bool YearFromEra(string era, int eraYear, out int year)
    {
        if (era == "ap") { year = eraYear; return true; }
        year = 0;
        return false;
    }

    public override string MonthCodeFor(int year, int month) => $"M{month:D2}";
    public override bool MonthFromCode(int year, string code, out int month, out bool existsInYear)
    {
        existsInYear = false;
        if (!ParseSimpleMonthCode(code, out int ord, out bool leap) || leap || ord > 12) { month = 0; return false; }
        month = ord;
        existsInYear = ord is >= 1 and <= 12;
        return true;
    }

    private static long FloorDiv(long a, long b)
    {
        long q = a / b;
        if (a % b != 0 && (a < 0) != (b < 0)) q--;
        return q;
    }

    private static long CeilDiv(long a, long b) => FloorDiv(a + b - 1, b);

    private static long Mod(long a, long b)
    {
        long r = a % b;
        if (r != 0 && (r < 0) != (b < 0)) r += b;
        return r;
    }
}

// Hebrew (lunisolar) calendar, Dershowitz & Reingold arithmetic. Internally
// months use the Dershowitz numbering (Nisan = 1 … Adar = 12, Adar II = 13 in a
// leap year). Temporal exposes ordinals counted from Tishri and the stable
// month codes M01..M12 with the leap month Adar I as M05L; this class maps
// between them. Single era "am" (Anno Mundi).
internal sealed class HebrewCalendarSystem : CalendarSystem
{
    private const long HebrewEpochRd = -1373427;

    public override string Id => "hebrew";

    private static bool LeapYear(int year) => Mod(7L * year + 1, 19) < 7;
    private static int LastMonth(int year) => LeapYear(year) ? 13 : 12;
    public override int MonthsInYear(int year) => LeapYear(year) ? 13 : 12;
    public override bool InLeapYear(int year) => LeapYear(year);

    private static long ElapsedDays(int year)
    {
        long monthsElapsed = FloorDiv(235L * year - 234, 19);
        long partsElapsed = 12084 + 13753 * monthsElapsed;
        long day = monthsElapsed * 29 + FloorDiv(partsElapsed, 25920);
        return Mod(3 * (day + 1), 7) < 3 ? day + 1 : day;
    }

    private static long NewYearRd(int year)
    {
        long ny0 = ElapsedDays(year - 1), ny1 = ElapsedDays(year), ny2 = ElapsedDays(year + 1);
        int correction = ny2 - ny1 == 356 ? 2 : ny1 - ny0 == 382 ? 1 : 0;
        return HebrewEpochRd + ElapsedDays(year) + correction;
    }

    private static int HebDaysInYear(int year) => (int)(NewYearRd(year + 1) - NewYearRd(year));
    private static bool LongMarheshvan(int year) => HebDaysInYear(year) is 355 or 385;
    private static bool ShortKislev(int year) => HebDaysInYear(year) is 353 or 383;

    // last day of a Dershowitz month (Nisan=1 … Adar/Adar II = 12/13).
    private static int LastDayOfMonthD(int year, int dm)
    {
        if (dm is 2 or 4 or 6 or 10 or 13) return 29;
        if (dm == 12 && !LeapYear(year)) return 29;
        if (dm == 8 && !LongMarheshvan(year)) return 29;
        if (dm == 9 && ShortKislev(year)) return 29;
        return 30;
    }

    private long FixedFromHebrewD(int year, int dm, int day)
    {
        long rd = NewYearRd(year) + day - 1;
        if (dm < 7)
        {
            for (int m = 7; m <= LastMonth(year); m++) rd += LastDayOfMonthD(year, m);
            for (int m = 1; m < dm; m++) rd += LastDayOfMonthD(year, m);
        }
        else
        {
            for (int m = 7; m < dm; m++) rd += LastDayOfMonthD(year, m);
        }

        return rd;
    }

    // Temporal ordinal (Tishri=1) ↔ Dershowitz month (Nisan=1).
    private static int OrdinalToD(int year, int ord)
    {
        int last = LastMonth(year);
        return ord <= last - 6 ? ord + 6 : ord - (last - 6);
    }

    private static int DToOrdinal(int year, int dm)
    {
        int last = LastMonth(year);
        return dm >= 7 ? dm - 6 : dm + (last - 6);
    }

    public override int DaysInMonthOrdinal(int year, int month)
        => LastDayOfMonthD(year, OrdinalToD(year, month));

    public override long ToFixed(int year, int month, int day)
        => FixedFromHebrewD(year, OrdinalToD(year, month), day) - RataDieToEpoch;

    public override void FromFixed(long epochDay, out int year, out int month, out int day)
    {
        long date = epochDay + RataDieToEpoch;
        int y = (int)(FloorDiv(98496L * (date - HebrewEpochRd), 35975351) + 1);
        while (NewYearRd(y + 1) <= date) y++;
        while (NewYearRd(y) > date) y--;
        int dm = date < FixedFromHebrewD(y, 1, 1) ? 7 : 1;
        while (date > FixedFromHebrewD(y, dm, LastDayOfMonthD(y, dm))) dm++;
        year = y;
        month = DToOrdinal(y, dm);
        day = (int)(date - FixedFromHebrewD(y, dm, 1) + 1);
    }

    public override (string?, int?) EraFor(int year, long epochDay) => ("am", year);
    public override bool YearFromEra(string era, int eraYear, out int year)
    {
        if (era == "am") { year = eraYear; return true; }
        year = 0;
        return false;
    }

    public override string MonthCodeFor(int year, int month)
    {
        if (!LeapYear(year)) return $"M{month:D2}";
        if (month <= 5) return $"M{month:D2}";
        if (month == 6) return "M05L";          // Adar I
        return $"M{month - 1:D2}";              // Adar II .. Elul
    }

    public override bool MonthFromCode(int year, string code, out int month, out bool existsInYear)
    {
        existsInYear = false;
        if (!ParseSimpleMonthCode(code, out int nn, out bool leap) || nn > 12) { month = 0; return false; }
        bool isLeap = LeapYear(year);
        if (leap)
        {
            // Adar I = M05L is the only leap month code; others are invalid.
            if (nn != 5) { month = 0; return false; }
            if (isLeap) { month = 6; existsInYear = true; return true; }
            month = 5; // common year: constrain M05L → Adar (M05)
            existsInYear = false;
            return true;
        }

        if (!isLeap) { month = nn; existsInYear = nn is >= 1 and <= 12; return true; }
        month = nn <= 5 ? nn : nn + 1; // shift past Adar I in leap years
        existsInYear = nn is >= 1 and <= 12;
        return true;
    }

    private static long FloorDiv(long a, long b)
    {
        long q = a / b;
        if (a % b != 0 && (a < 0) != (b < 0)) q--;
        return q;
    }

    private static long Mod(long a, long b)
    {
        long r = a % b;
        if (r != 0 && (r < 0) != (b < 0)) r += b;
        return r;
    }
}

// Chinese / Korean (dangi) lunisolar calendars, backed by System.Globalization's
// EastAsianLunisolarCalendar (limited to roughly 1901-2101 / 918-2051). Temporal
// exposes the month ordinal directly (1..13) and stable month codes where a leap
// month duplicates the previous code with an "L" suffix (e.g. M05L). No era.
internal sealed class EastAsianCalendarSystem : CalendarSystem
{
    private readonly string _id;
    private readonly System.Globalization.EastAsianLunisolarCalendar _cal;
    private readonly long _minEpoch;
    private readonly long _maxEpoch;

    public EastAsianCalendarSystem(string id, System.Globalization.EastAsianLunisolarCalendar cal)
    {
        _id = id;
        _cal = cal;
        _minEpoch = IsoMath.CivilToEpochDays(cal.MinSupportedDateTime.Year, cal.MinSupportedDateTime.Month, cal.MinSupportedDateTime.Day);
        _maxEpoch = IsoMath.CivilToEpochDays(cal.MaxSupportedDateTime.Year, cal.MaxSupportedDateTime.Month, cal.MaxSupportedDateTime.Day);
    }

    public override string Id => _id;

    public override int MonthsInYear(int year)
    {
        try { return _cal.GetMonthsInYear(year); }
        catch { return 12; }
    }

    public override bool InLeapYear(int year)
    {
        try { return _cal.GetMonthsInYear(year) == 13; }
        catch { return false; }
    }

    public override int DaysInMonthOrdinal(int year, int month)
    {
        try { return _cal.GetDaysInMonth(year, month); }
        catch { return 30; }
    }

    public override long ToFixed(int year, int month, int day)
    {
        try
        {
            var dt = _cal.ToDateTime(year, month, day, 0, 0, 0, 0);
            return IsoMath.CivilToEpochDays(dt.Year, dt.Month, dt.Day);
        }
        catch
        {
            return long.MinValue; // out of the backing calendar's range → caller treats as invalid
        }
    }

    public override void FromFixed(long epochDay, out int year, out int month, out int day)
    {
        long clamped = Math.Clamp(epochDay, _minEpoch, _maxEpoch);
        var iso = IsoMath.EpochDaysToCivil(clamped);
        var dt = new DateTime(iso.Year, iso.Month, iso.Day);
        year = _cal.GetYear(dt);
        month = _cal.GetMonth(dt);
        day = _cal.GetDayOfMonth(dt);
    }

    public override (string?, int?) EraFor(int year, long epochDay) => (null, null);
    public override bool YearFromEra(string era, int eraYear, out int year) { year = 0; return false; }

    public override string MonthCodeFor(int year, int month)
    {
        int leap = LeapMonth(year);
        if (leap == 0 || month < leap) return $"M{month:D2}";
        if (month == leap) return $"M{month - 1:D2}L";
        return $"M{month - 1:D2}";
    }

    public override bool MonthFromCode(int year, string code, out int month, out bool existsInYear)
    {
        existsInYear = false;
        if (!ParseSimpleMonthCode(code, out int nn, out bool leap)) { month = 0; return false; }
        int leapMonth = LeapMonth(year);
        if (leap)
        {
            if (leapMonth > 0 && leapMonth - 1 == nn)
            {
                month = leapMonth;
                existsInYear = true;
                return true;
            }

            // Leap month MnnL absent this year: constrain to the base month Mnn.
            month = (leapMonth == 0 || nn < leapMonth) ? nn : nn + 1;
            existsInYear = false;
            return true;
        }

        month = (leapMonth == 0 || nn < leapMonth) ? nn : nn + 1;
        existsInYear = month >= 1 && month <= MonthsInYear(year);
        return true;
    }

    private int LeapMonth(int year)
    {
        try { return _cal.GetLeapMonth(year); }
        catch { return 0; }
    }

    public override int DaysInYear(int year)
    {
        try { return _cal.GetDaysInYear(year); }
        catch { return 354; }
    }
}
