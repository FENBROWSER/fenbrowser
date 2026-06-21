namespace FenBrowser.Js.Temporal;

// An ISO 8601 calendar date. Year is the (possibly negative) astronomical
// year; Month is 1-12; Day is 1-31. Plain data carrier — validity is
// enforced by the abstract operations in IsoMath.
internal readonly record struct IsoDate(int Year, int Month, int Day);

// A wall-clock time with nanosecond precision.
internal readonly record struct IsoTime(int Hour, int Minute, int Second, int Millisecond, int Microsecond, int Nanosecond)
{
    public static readonly IsoTime Midnight = new(0, 0, 0, 0, 0, 0);

    public long ToNanosecondsOfDay()
        => ((Hour * 3600L + Minute * 60L + Second) * 1_000_000_000L)
           + (Millisecond * 1_000_000L) + (Microsecond * 1_000L) + Nanosecond;
}

// Calendar-neutral ISO date arithmetic per the Temporal proposal's
// "ISO Date Records" abstract operations (tc39.es/proposal-temporal).
// Everything is epoch-day based so the full Temporal range
// (-271821-04-19 .. +275760-09-13) works without System.DateTime limits.
internal static class IsoMath
{
    // ECMA-262 21.4.1.3 Day Number — epoch days for 1970-01-01 = 0.
    // Valid epoch-day range for PlainDate (ISODateWithinLimits):
    // nsMinInstant - nsPerDay .. nsMaxInstant + nsPerDay, i.e. ±(1e8 + 1) days.
    public const long MinEpochDay = -100_000_001;
    public const long MaxEpochDay = 100_000_001;

    public static bool IsLeapYear(int year)
        => year % 4 == 0 && (year % 100 != 0 || year % 400 == 0);

    public static int DaysInYear(int year) => IsLeapYear(year) ? 366 : 365;

    public static int DaysInMonth(int year, int month) => month switch
    {
        1 or 3 or 5 or 7 or 8 or 10 or 12 => 31,
        4 or 6 or 9 or 11 => 30,
        _ => IsLeapYear(year) ? 29 : 28,
    };

    // Temporal IsValidISODate: month 1-12, day 1-DaysInMonth.
    public static bool IsValidIsoDate(int year, int month, int day)
        => month >= 1 && month <= 12 && day >= 1 && day <= DaysInMonth(year, month);

    // Days from 1970-01-01 to year-01-01. Uses the standard civil-calendar
    // algorithm (Howard Hinnant's days_from_civil), exact over all int years.
    public static long EpochDaysAtStartOfYear(int year)
        => CivilToEpochDays(year, 1, 1);

    // Howard Hinnant's days_from_civil — exact for all years.
    public static long CivilToEpochDays(int year, int month, int day)
    {
        long y = year;
        if (month <= 2)
        {
            y -= 1;
        }

        long era = (y >= 0 ? y : y - 399) / 400;
        long yoe = y - era * 400;                                    // [0, 399]
        long doy = (153 * (month + (month > 2 ? -3 : 9)) + 2) / 5 + day - 1; // [0, 365]
        long doe = yoe * 365 + yoe / 4 - yoe / 100 + doy;            // [0, 146096]
        return era * 146097 + doe - 719468;
    }

    // Inverse of CivilToEpochDays (Howard Hinnant's civil_from_days).
    public static IsoDate EpochDaysToCivil(long epochDays)
    {
        long z = epochDays + 719468;
        long era = (z >= 0 ? z : z - 146096) / 146097;
        long doe = z - era * 146097;                                  // [0, 146096]
        long yoe = (doe - doe / 1460 + doe / 36524 - doe / 146096) / 365; // [0, 399]
        long y = yoe + era * 400;
        long doy = doe - (365 * yoe + yoe / 4 - yoe / 100);           // [0, 365]
        long mp = (5 * doy + 2) / 153;                                // [0, 11]
        long d = doy - (153 * mp + 2) / 5 + 1;                        // [1, 31]
        long m = mp + (mp < 10 ? 3 : -9);                             // [1, 12]
        if (m <= 2)
        {
            y += 1;
        }

        return new IsoDate(checked((int)y), (int)m, (int)d);
    }

    public static long ToEpochDays(IsoDate date) => CivilToEpochDays(date.Year, date.Month, date.Day);

    // Temporal ISODateWithinLimits: representable as a PlainDateTime at noon,
    // i.e. within ±(nsMaxInstant + nsPerDay) of the epoch.
    public static bool IsoDateWithinLimits(IsoDate date)
    {
        var days = ToEpochDays(date);
        return days >= MinEpochDay && days <= 100_000_000;
    }

    public static bool IsoDateWithinZonedLimits(IsoDate date)
    {
        var days = ToEpochDays(date);
        return days is >= -100_000_000 and <= 100_000_000;
    }

    // ISO 8601 day of week: Monday=1 .. Sunday=7. 1970-01-01 was a Thursday (4).
    public static int DayOfWeek(IsoDate date)
    {
        var days = ToEpochDays(date);
        var dow = (int)((days + 3) % 7); // 0=Monday
        if (dow < 0)
        {
            dow += 7;
        }

        return dow + 1;
    }

    public static int DayOfYear(IsoDate date)
        => checked((int)(ToEpochDays(date) - EpochDaysAtStartOfYear(date.Year) + 1));

    // ISO 8601 week numbering: week 1 is the week containing the first
    // Thursday of the year. Returns (week, yearOfWeek).
    public static (int Week, int Year) WeekOfYear(IsoDate date)
    {
        int doy = DayOfYear(date);
        int dow = DayOfWeek(date);
        int week = (doy - dow + 10) / 7;
        if (week < 1)
        {
            // Belongs to the last week of the previous year.
            int prevYear = date.Year - 1;
            int prevDoy = doy + DaysInYear(prevYear);
            return ((prevDoy - dow + 10) / 7, prevYear);
        }

        if (week == 53)
        {
            // Might actually be week 1 of the next year.
            int daysInYear = DaysInYear(date.Year);
            if (daysInYear - doy < 4 - dow)
            {
                return (1, date.Year + 1);
            }
        }

        return (week, date.Year);
    }

    // Temporal BalanceISODate — normalize an out-of-range day (and any
    // year/month overflow) by walking through epoch days.
    public static IsoDate BalanceIsoDate(int year, int month, int day)
    {
        // First balance year/month.
        long y = year + (long)Math.Floor((month - 1) / 12.0);
        int m = (int)((month - 1) % 12);
        if (m < 0)
        {
            m += 12;
        }

        m += 1;
        // Day can be far out of range; go through epoch days.
        long epochDays = CivilToEpochDays(checked((int)y), m, 1) + (day - 1);
        return EpochDaysToCivil(epochDays);
    }

    // Temporal RegulateISODate with overflow=constrain.
    public static IsoDate ConstrainIsoDate(int year, int month, int day)
    {
        int m = Math.Clamp(month, 1, 12);
        int d = Math.Clamp(day, 1, DaysInMonth(year, m));
        return new IsoDate(year, m, d);
    }

    // Temporal CompareISODate.
    public static int Compare(IsoDate a, IsoDate b)
    {
        if (a.Year != b.Year)
        {
            return a.Year < b.Year ? -1 : 1;
        }

        if (a.Month != b.Month)
        {
            return a.Month < b.Month ? -1 : 1;
        }

        if (a.Day != b.Day)
        {
            return a.Day < b.Day ? -1 : 1;
        }

        return 0;
    }

    // Temporal AddISODate (overflow handled by caller for the day clamp).
    // years/months are applied first (with constrain/reject on the
    // intermediate), then weeks/days are added by epoch-day arithmetic.
    public static IsoDate AddIsoDate(IsoDate date, double years, double months, double weeks, double days, bool constrainIntermediate, out bool intermediateInvalid)
    {
        intermediateInvalid = false;
        long yearsMonths = (long)(date.Year + years) * 12 + (date.Month - 1) + (long)months;
        long y = DivFloor(yearsMonths, 12);
        int m = (int)(yearsMonths - y * 12) + 1;
        if (y < int.MinValue / 2 || y > int.MaxValue / 2)
        {
            intermediateInvalid = true;
            return date;
        }

        int year = (int)y;
        int d = date.Day;
        if (d > DaysInMonth(year, m))
        {
            if (!constrainIntermediate)
            {
                intermediateInvalid = true;
                return date;
            }

            d = DaysInMonth(year, m);
        }

        long epochDays = CivilToEpochDays(year, m, d) + (long)(weeks * 7) + (long)days;
        if (epochDays < MinEpochDay - 400 || epochDays > MaxEpochDay + 400)
        {
            intermediateInvalid = true;
            return date;
        }

        return EpochDaysToCivil(epochDays);
    }

    private static long DivFloor(long a, long b)
    {
        long q = a / b;
        if ((a % b != 0) && ((a < 0) != (b < 0)))
        {
            q--;
        }

        return q;
    }

    // Temporal DifferenceISODate. largestUnit: "year", "month", "week", "day".
    // Returns a date duration (years, months, weeks, days) with uniform sign.
    public static (int Years, int Months, int Weeks, long Days) DifferenceIsoDate(IsoDate one, IsoDate two, string largestUnit)
    {
        int sign = -Compare(one, two);
        if (sign == 0)
        {
            return (0, 0, 0, 0);
        }

        int years = 0;
        int months = 0;
        if (largestUnit is "year" or "month")
        {
            if (largestUnit == "year")
            {
                // Candidate years: difference of calendar years, adjusted so the
                // intermediate (one + years) does not overshoot two.
                int candidateYears = two.Year - one.Year;
                if (candidateYears != 0)
                {
                    candidateYears -= sign;
                }

                while (!IsoDateSurpasses(sign, one.Year + candidateYears, one.Month, one.Day, two))
                {
                    years = candidateYears;
                    candidateYears += sign;
                }
            }

            // Month computation: for "year" largestUnit, anchor at (one + years);
            // for "month" largestUnit, anchor at one.Year so months are the total
            // calendar months (years is 0, collapsed into months).
            int startYear = largestUnit == "year" ? one.Year + years : one.Year;
            int candidateMonths = sign;
            var intermediate = BalanceYearMonth(startYear, one.Month + candidateMonths);
            while (!IsoDateSurpasses(sign, intermediate.Year, intermediate.Month, one.Day, two))
            {
                months = candidateMonths;
                candidateMonths += sign;
                intermediate = BalanceYearMonth(intermediate.Year, intermediate.Month + sign);
            }
        }

        var inter = BalanceYearMonth(one.Year + years, one.Month + months);
        var constrained = ConstrainIsoDate(inter.Year, inter.Month, one.Day);
        long weeks = 0;
        long days = ToEpochDays(two) - ToEpochDays(constrained);
        if (largestUnit == "week")
        {
            weeks = days / 7;
            days %= 7;
        }

        return (years, months, (int)weeks, days);
    }

    private static (int Year, int Month) BalanceYearMonth(int year, int month)
    {
        long total = (long)year * 12 + (month - 1);
        long y = DivFloor(total, 12);
        int m = (int)(total - y * 12) + 1;
        return ((int)y, m);
    }

    // Temporal ISODateSurpasses: does (y1,m1,constrained d1) surpass `two`
    // in the direction of sign?
    private static bool IsoDateSurpasses(int sign, int y1, int m1, int d1, IsoDate two)
    {
        if (y1 != two.Year)
        {
            return sign * (y1 - two.Year) > 0;
        }

        if (m1 != two.Month)
        {
            return sign * (m1 - two.Month) > 0;
        }

        if (d1 != two.Day)
        {
            return sign * (d1 - two.Day) > 0;
        }

        return false;
    }
}
