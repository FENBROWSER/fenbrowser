using FenBrowser.Js.Heap;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Intl;
using FenBrowser.Js.Objects;
using FenBrowser.Js.Runtime;
using FenBrowser.Js.Temporal;

namespace FenBrowser.Js.Builtins;

// ECMA-262 Temporal proposal (ES2024+).
// Full implementation backed by .NET DateTime/DateTimeOffset/TimeSpan/TimeZoneInfo.
public sealed class TemporalStub : IBuiltinModule
{
    public string Name => "Temporal";

    private static readonly DateTime Epoch = new(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static readonly System.Text.RegularExpressions.Regex IsoDurationRegex =
        new System.Text.RegularExpressions.Regex(
            @"^[+-]?[Pp](?=\d|[Tt]\d)(?:(?:\d+[Yy])?(?:\d+[Mm])?(?:\d+[Ww])?(?:\d+[Dd])?)(?:[Tt](?:(?:\d+[Hh])?(?:\d+[Mm])?(?:\d+(?:[.,]\d{1,9})?[Ss])|(?:\d+[Hh])?(?:\d+(?:[.,]\d{1,9})?[Mm])|(?:\d+(?:[.,]\d{1,9})?[Hh])))?$",
            System.Text.RegularExpressions.RegexOptions.Compiled);

    public IReadOnlyList<BuiltinBinding> GetBindings(IBuiltinContext context)
    {
        var heap = context.Heap;
        var temporal = new JsObject();
        temporal.SetPrototype(context.GetObjectPrototype());
        var tHandle = heap.AllocateObject(temporal, AllocationSite.Current());
        heap.PushRoot(tHandle);

        InstallNow(context, temporal, tHandle, heap);
        InstallDuration(context, temporal, tHandle, heap);
        InstallInstant(context, temporal, tHandle, heap);
        InstallPlainDate(context, temporal, tHandle, heap);
        InstallPlainTime(context, temporal, tHandle, heap);
        InstallPlainDateTime(context, temporal, tHandle, heap);
        InstallPlainYearMonth(context, temporal, tHandle, heap);
        InstallPlainMonthDay(context, temporal, tHandle, heap);
        InstallZonedDateTime(context, temporal, tHandle, heap);
        InstallCalendar(context, temporal, tHandle, heap);
        InstallTimeZone(context, temporal, tHandle, heap);
        var toStringTag = context.CreateWellKnownSymbol("toStringTag");
        _ = temporal.DefineOwnSymbolProperty(
            toStringTag.AsSymbolId(),
            new JsPropertyDescriptor(JsValue.FromString("Temporal"), Writable: false, Enumerable: false, Configurable: true));

        return new[] { BuiltinBinding.NonEnumerable("Temporal", JsValue.FromObject(tHandle)) };
    }

    // ─── Helpers ───────────────────────────────────────────

    private static double GetVNum(JsHeap h, JsObject o, string k)
    {
        var v = GetV(h, o, k);
        return v.Tag is JsValueTag.Number or JsValueTag.Int32 ? v.AsNumber() : 0;
    }

    private static string GetVStr(JsHeap h, JsObject o, string k)
    {
        var v = GetV(h, o, k);
        return v.Tag == JsValueTag.String ? v.AsString() : "";
    }

    /// <summary>Safely apply years/months/days to a DateTime, clamping to valid range.</summary>
    private static DateTime SafeAddDate(DateTime dt, int years, int months, int days)
    {
        try { dt = dt.AddYears(years); } catch (ArgumentOutOfRangeException) { dt = years > 0 ? DateTime.MaxValue : DateTime.MinValue; }
        try { dt = dt.AddMonths(months); } catch (ArgumentOutOfRangeException) { dt = months > 0 ? DateTime.MaxValue : DateTime.MinValue; }
        try { dt = dt.AddDays(days); } catch (ArgumentOutOfRangeException) { dt = days > 0 ? DateTime.MaxValue : DateTime.MinValue; }
        return dt;
    }

    /// <summary>Reconstruct a DateTime from a PlainDate _v object.</summary>
    private static DateTime DecodePlainDate(JsHeap h, JsObject o)
    {
        int y = (int)GetVNum(h, o, "y");
        int m = (int)GetVNum(h, o, "m");
        int d = (int)GetVNum(h, o, "d");
        y = Math.Max(1, Math.Min(9999, y));
        m = Math.Max(1, Math.Min(12, m));
        d = Math.Max(1, Math.Min(DateTime.DaysInMonth(y, m), d));
        return new DateTime(y, m, d);
    }

    /// <summary>Reconstruct a DateTime from a PlainDateTime _v object.</summary>
    private static DateTime DecodePlainDateTime(JsHeap h, JsObject o)
    {
        int y = (int)GetVNum(h, o, "year");
        int mo = (int)GetVNum(h, o, "month");
        int d = (int)GetVNum(h, o, "day");
        int hr = (int)GetVNum(h, o, "hour");
        int mi = (int)GetVNum(h, o, "minute");
        int s = (int)GetVNum(h, o, "second");
        int ms = (int)GetVNum(h, o, "millisecond");
        y = Math.Max(1, Math.Min(9999, y));
        mo = Math.Max(1, Math.Min(12, mo));
        d = Math.Max(1, Math.Min(DateTime.DaysInMonth(y, mo), d));
        hr = Math.Max(0, Math.Min(23, hr));
        mi = Math.Max(0, Math.Min(59, mi));
        s = Math.Max(0, Math.Min(59, s));
        ms = Math.Max(0, Math.Min(999, ms));
        return new DateTime(y, mo, d, hr, mi, s, ms);
    }

    /// <summary>Reconstruct a DateTime from a PlainYearMonth _v object (day defaults to 1).</summary>
    private static DateTime DecodePlainYearMonth(JsHeap h, JsObject o)
    {
        int y = (int)GetVNum(h, o, "y");
        int m = (int)GetVNum(h, o, "m");
        y = Math.Max(1, Math.Min(9999, y));
        m = Math.Max(1, Math.Min(12, m));
        return new DateTime(y, m, 1);
    }

    /// <summary>Reconstruct epoch nanoseconds from an Instant _v object.</summary>
    private static long DecodeInstantNanos(JsHeap h, JsObject o)
        => ToSafeLong(DecodeInstantNanosBig(h, o));

    private static System.Numerics.BigInteger DecodeInstantNanosBig(JsHeap h, JsObject o)
    {
        if (o.TryGetProperty("_v", x => h.GetObject(x), out var value) && value.Value.Tag == JsValueTag.Object)
        {
            var slots = h.GetObject(value.Value.AsObjectHandle());
            if (slots.TryGetProperty("ensBig", x => h.GetObject(x), out var exact) && exact.Value.Tag == JsValueTag.BigInt)
            {
                return exact.Value.AsBigInt();
            }
        }

        var ens = GetVNum(h, o, "ens");
        if (double.IsNaN(ens) || double.IsInfinity(ens)) return System.Numerics.BigInteger.Zero;
        return new System.Numerics.BigInteger(ens);
    }

    /// <summary>Compare fields of two _v objects. Returns -1, 0, or 1.</summary>
    private static int FieldsCompare(JsHeap h, JsObject a, JsObject b, params string[] fields)
    {
        foreach (var f in fields)
        {
            double va = GetVNum(h, a, f);
            double vb = GetVNum(h, b, f);
            if (va < vb) return -1;
            if (va > vb) return 1;
        }
        return 0;
    }

    /// <summary>True when all named fields are equal between two _v objects.</summary>
    private static bool FieldsEqual(JsHeap h, JsObject a, JsObject b, params string[] fields)
    {
        foreach (var f in fields)
        {
            if (GetVNum(h, a, f) != GetVNum(h, b, f)) return false;
        }
        return true;
    }

    /// <summary>Reconstruct a DateTime from Instant epoch nanos.</summary>
    private static DateTime InstantToDateTime(long nanos)
    {
        // Clamp to .NET DateTime range (1/1/0001 to 12/31/9999)
        const long minTicks = 0L;
        const long maxTicks = 3155378975999999999L;
        var ticks = Epoch.Ticks + (nanos / 100L);
        ticks = Math.Max(minTicks, Math.Min(maxTicks, ticks));
        return new DateTime(ticks, DateTimeKind.Utc);
    }

    /// <summary>Format an Instant as ISO 8601 string (e.g. "2024-01-15T12:00:00Z").</summary>
    private static JsValue FormatInstant(JsHeap h, JsObject o)
    {
        long nanos = DecodeInstantNanos(h, o);
        var dt = InstantToDateTime(nanos);
        long subMilliNanos = nanos % 1_000_000;
        if (subMilliNanos < 0) subMilliNanos += 1_000_000;
        string frac = subMilliNanos == 0 ? "" : $".{subMilliNanos:D6}".TrimEnd('0');
        return JsValue.FromString($"{dt.Year:D4}-{dt.Month:D2}-{dt.Day:D2}T{dt.Hour:D2}:{dt.Minute:D2}:{dt.Second:D2}{frac}Z");
    }

    private static JsValue FormatInstant(IBuiltinContext ctx, JsHeap h, JsObject o, IReadOnlyList<JsValue> args)
    {
        var opts = GetToStringOptions(ctx, h, args, 0, new[] { "fractionalSecondDigits", "roundingMode", "smallestUnit", "timeZone" });
        System.Numerics.BigInteger epochNs = DecodeInstantNanosBig(h, o);
        System.Numerics.BigInteger inc = PrecisionIncrementNs(opts);
        if (inc > 1) epochNs = RoundNsToIncrement(ctx, epochNs, inc, opts.RoundingMode);
        long epochNsLong = ToSafeLong(epochNs);

        // timeZone option: render the wall clock in that zone with its offset.
        if (opts.TimeZoneValue is not null)
        {
            string ctz = CanonicalizeTimeZoneId(ctx, opts.TimeZoneValue);
            long offNs = TemporalTimeZones.GetOffsetNs(ctz, epochNsLong);
            var (zd, zt) = TemporalTimeZones.WallFromEpochNs(epochNsLong, offNs);
            return JsValue.FromString($"{FormatIsoYear(zd.Year)}-{zd.Month:D2}-{zd.Day:D2}T{zt.Hour:D2}:{zt.Minute:D2}" +
                $"{FormatSecondsPart(zt.ToNanosecondsOfDay(), opts)}{TemporalTimeZones.FormatOffsetRoundedToMinute(offNs)}");
        }

        var (d, t) = TemporalTimeZones.WallFromEpochNs(epochNsLong, 0);
        return JsValue.FromString($"{FormatIsoYear(d.Year)}-{d.Month:D2}-{d.Day:D2}T{t.Hour:D2}:{t.Minute:D2}" +
            $"{FormatSecondsPart(t.ToNanosecondsOfDay(), opts)}Z");
    }

    private static JsValue InstantToLocaleString(IBuiltinContext ctx, JsHeap h, JsObject o, IReadOnlyList<JsValue> args)
    {
        var locale = args.Count > 0 ? ToStrArg(ctx, args[0]) : string.Empty;
        var options = ParseDateTimeFormatOptions(locale, ctx, h, args.Count > 1 ? args[1] : JsValue.Undefined);
        try
        {
            IntlDateTimeFormatting.ValidateOptions(options);
        }
        catch (InvalidOperationException)
        {
            throw new JsThrownException(ctx.CreateTypeError("dateStyle/timeStyle conflicts with explicit component options."));
        }

        var culture = IntlDateTimeFormatting.ResolveCulture(locale);
        var instant = new DateTimeOffset(InstantToDateTime(DecodeInstantNanos(h, o)));
        var result = IntlDateTimeFormatting.Format(instant, culture, options);
        return JsValue.FromString(result.Text);
    }

    private static JsValue InstantToZonedDateTimeIso(IBuiltinContext ctx, JsHeap h, JsObject o, IReadOnlyList<JsValue> args)
    {
        // sec-temporal.instant.prototype.tozoneddatetimeiso: the time zone
        // argument is required and must be a string (identifier or ISO string).
        if (args.Count == 0 || args[0].Tag != JsValueTag.String)
            throw new JsThrownException(ctx.CreateTypeError("toZonedDateTimeISO requires a time zone string."));
        string tz = CanonicalizeTimeZoneId(ctx, args[0].AsString());
        return MakeZonedDateTimeNsBig(ctx, h, DecodeInstantNanosBig(h, o), tz, "iso8601");
    }

    private static JsValue PlainTimeToLocaleString(IBuiltinContext ctx, JsHeap h, JsObject o, IReadOnlyList<JsValue> args)
    {
        var locale = args.Count > 0 ? ToStrArg(ctx, args[0]) : string.Empty;
        var options = ParseDateTimeFormatOptions(locale, ctx, h, args.Count > 1 ? args[1] : JsValue.Undefined);
        try
        {
            var result = IntlDateTimeFormatting.FormatPlainTime(
                (int)GetVNum(h, o, "hour"),
                (int)GetVNum(h, o, "minute"),
                (int)GetVNum(h, o, "second"),
                (int)GetVNum(h, o, "millisecond"),
                (int)GetVNum(h, o, "microsecond"),
                (int)GetVNum(h, o, "nanosecond"),
                IntlDateTimeFormatting.ResolveCulture(locale),
                options);
            return JsValue.FromString(result.Text);
        }
        catch (InvalidOperationException ex)
        {
            throw new JsThrownException(ctx.CreateTypeError(ex.Message));
        }
    }

    /// <summary>Format a Temporal date-only type (PlainDate, PlainYearMonth, PlainMonthDay) to a locale string.</summary>
    private static JsValue DateOnlyToLocaleString(IBuiltinContext ctx, JsHeap h, JsObject o, IReadOnlyList<JsValue> args,
        int year, int month, int day)
    {
        // timeStyle is not allowed for date-only types (check raw options before parsing)
        if (args.Count > 1 && args[1].Tag == JsValueTag.Object && TryGetField(ctx, h, args[1], "timeStyle", out _))
            throw new JsThrownException(ctx.CreateTypeError("timeStyle conflicts with date-only Temporal types."));

        var locale = args.Count > 0 ? ToStrArg(ctx, args[0]) : string.Empty;
        var opts = ParseDateTimeFormatOptions(locale, ctx, h, args.Count > 1 ? args[1] : JsValue.Undefined);

        // Strip time-related fields for date-only types (they must not appear in the output)
        opts = opts with { Hour = null, Minute = null, Second = null, FractionalSecondDigits = null, DayPeriod = null, TimeZoneName = null };

        // Default to date components when nothing explicit is set
        var hasExplicit = !string.IsNullOrEmpty(opts.DateStyle) ||
                          !string.IsNullOrEmpty(opts.Weekday) || !string.IsNullOrEmpty(opts.Era) ||
                          !string.IsNullOrEmpty(opts.Year) || !string.IsNullOrEmpty(opts.Month) ||
                          !string.IsNullOrEmpty(opts.Day);
        if (!hasExplicit)
            opts = opts with { Year = "numeric", Month = "numeric", Day = "numeric" };

        try
        {
            IntlDateTimeFormatting.ValidateOptions(opts);
        }
        catch (InvalidOperationException)
        {
            throw new JsThrownException(ctx.CreateTypeError("dateStyle/timeStyle conflicts with explicit component options."));
        }

        var culture = IntlDateTimeFormatting.ResolveCulture(locale);
        var instant = new DateTimeOffset(year, month, day, 0, 0, 0, TimeSpan.Zero);
        var result = IntlDateTimeFormatting.Format(instant, culture, opts);
        return JsValue.FromString(result.Text);
    }

    /// <summary>Format a Temporal date-time type (PlainDateTime, ZonedDateTime) to a locale string.</summary>
    private static JsValue DateTimeToLocaleString(IBuiltinContext ctx, JsHeap h, IReadOnlyList<JsValue> args,
        int year, int month, int day, int hour, int minute, int second, int millisecond, string? timeZoneId = null,
        long? epochNs = null)
    {
        var locale = args.Count > 0 ? ToStrArg(ctx, args[0]) : string.Empty;
        var opts = ParseDateTimeFormatOptions(locale, ctx, h, args.Count > 1 ? args[1] : JsValue.Undefined);

        // If the user didn't provide a timeZone and one is available from the ZDT, use it.
        if (string.IsNullOrEmpty(opts.TimeZoneId) && !string.IsNullOrEmpty(timeZoneId))
            opts = opts with { TimeZoneId = timeZoneId };

        try
        {
            IntlDateTimeFormatting.ValidateOptions(opts);
        }
        catch (InvalidOperationException)
        {
            throw new JsThrownException(ctx.CreateTypeError("dateStyle/timeStyle conflicts with explicit component options."));
        }

        var culture = IntlDateTimeFormatting.ResolveCulture(locale);

        DateTimeOffset instant;
        if (epochNs.HasValue)
        {
            // ZonedDateTime: use epoch nanos directly for accurate timezone conversion
            instant = new DateTimeOffset(InstantToDateTime(epochNs.Value));
        }
        else
        {
            instant = new DateTimeOffset(year, month, day, hour, minute, second, TimeSpan.Zero);
            // Add fractional seconds
            if (millisecond > 0)
                instant = instant.AddMilliseconds(millisecond);
        }

        var result = IntlDateTimeFormatting.Format(instant, culture, opts);
        return JsValue.FromString(result.Text);
    }

    /// <summary>Format a Duration as ISO 8601 string (e.g. "P1Y2M3DT4H5M6S", "-PT1H").</summary>
    private static JsValue FormatDuration(JsHeap h, JsObject o)
    {
        var d = DecodeDuration(h, o);
        bool negative = d.years < 0 || d.months < 0 || d.weeks < 0 || d.days < 0 || d.hours < 0
            || d.minutes < 0 || d.seconds < 0 || d.millis < 0 || d.micros < 0 || d.nanos < 0;
        var sb = new System.Text.StringBuilder(negative ? "-P" : "P");
        if (d.years != 0) sb.Append($"{Math.Abs(d.years)}Y");
        if (d.months != 0) sb.Append($"{Math.Abs(d.months)}M");
        if (d.weeks != 0) sb.Append($"{Math.Abs(d.weeks)}W");
        if (d.days != 0) sb.Append($"{Math.Abs(d.days)}D");
        if (d.hours == 0 && d.minutes == 0 && d.seconds == 0 && d.millis == 0 && d.micros == 0 && d.nanos == 0)
        {
            if (sb.Length == 1) sb.Append("T0S");
        }
        else
        {
            sb.Append('T');
            if (d.hours != 0) sb.Append($"{Math.Abs(d.hours)}H");
            if (d.minutes != 0) sb.Append($"{Math.Abs(d.minutes)}M");
            long frac = (long)Math.Abs(d.millis) * 1_000_000L + (long)Math.Abs(d.micros) * 1_000L + (long)Math.Abs(d.nanos);
            if (d.seconds != 0 || frac != 0)
            {
                sb.Append(Math.Abs(d.seconds));
                if (frac != 0) { sb.Append('.'); sb.Append(frac.ToString("D9").TrimEnd('0')); }
                sb.Append('S');
            }
        }
        return JsValue.FromString(sb.ToString());
    }

    /// <summary>
    /// Temporal.Duration.prototype.toString: format the duration applying the precision options
    /// (smallestUnit / fractionalSecondDigits / roundingMode). The fractional-seconds part is
    /// rounded to the requested precision, carrying any whole second into the seconds field.
    /// </summary>
    private static JsValue FormatDurationOpts(IBuiltinContext ctx, JsHeap h, JsObject o, IReadOnlyList<JsValue> a)
    {
        var opts = GetToStringOptions(ctx, h, a, 0, new[] { "fractionalSecondDigits", "roundingMode", "smallestUnit" });
        // Duration toString does not accept "minute" as smallestUnit.
        if (opts.SmallestUnit == "minute")
            throw new JsThrownException(ctx.CreateRangeError("'minute' is not a valid smallestUnit for Duration.toString."));

        var d = DecodeDuration(h, o);
        bool negative = d.years < 0 || d.months < 0 || d.weeks < 0 || d.days < 0 || d.hours < 0
            || d.minutes < 0 || d.seconds < 0 || d.millis < 0 || d.micros < 0 || d.nanos < 0;

        // Combine sub-second fields and the seconds field, then round the fraction to the precision.
        long secs = (long)d.seconds;
        long fracNs = (long)d.millis * 1_000_000L + (long)d.micros * 1_000L + (long)d.nanos;
        secs += fracNs / 1_000_000_000L; fracNs %= 1_000_000_000L;
        long inc = PrecisionIncrementNs(opts);
        if (inc > 1) fracNs = (long)RoundNsToIncrement(ctx, fracNs, inc, opts.RoundingMode);
        secs += fracNs / 1_000_000_000L; fracNs %= 1_000_000_000L;

        int digits = opts.SmallestUnit switch
        {
            "second" => 0, "millisecond" => 3, "microsecond" => 6, "nanosecond" => 9, _ => opts.FractionalDigits,
        };
        long absFrac = Math.Abs(fracNs);
        string fracStr = digits switch
        {
            < 0 => absFrac == 0 ? "" : $".{absFrac:D9}".TrimEnd('0'),
            0 => "",
            _ => "." + $"{absFrac:D9}"[..digits],
        };

        var sb = new System.Text.StringBuilder(negative ? "-P" : "P");
        if (d.years != 0) sb.Append($"{Math.Abs(d.years)}Y");
        if (d.months != 0) sb.Append($"{Math.Abs(d.months)}M");
        if (d.weeks != 0) sb.Append($"{Math.Abs(d.weeks)}W");
        if (d.days != 0) sb.Append($"{Math.Abs(d.days)}D");
        bool emitSeconds = secs != 0 || fracNs != 0 || digits >= 0;
        bool emitTime = d.hours != 0 || d.minutes != 0 || emitSeconds;
        if (emitTime)
        {
            sb.Append('T');
            if (d.hours != 0) sb.Append($"{Math.Abs(d.hours)}H");
            if (d.minutes != 0) sb.Append($"{Math.Abs(d.minutes)}M");
            if (emitSeconds) sb.Append($"{Math.Abs(secs)}{fracStr}S");
        }
        else if (sb.Length == 1)
        {
            sb.Append("T0S");
        }
        return JsValue.FromString(sb.ToString());
    }

    /// <summary>Format a PlainDate as ISO 8601 string (e.g. "2024-01-15", extended years signed 6-digit).</summary>
    private static JsValue FormatPlainDate(JsHeap h, JsObject o)
    {
        var dt = DecodeIsoDate(h, o);
        return JsValue.FromString($"{FormatIsoYear(dt.Year)}-{dt.Month:D2}-{dt.Day:D2}{CalendarSuffix(h, o)}");
    }

    /// <summary>Format a PlainTime as ISO 8601 string (e.g. "12:00:00.123").</summary>
    private static JsValue FormatPlainTime(JsHeap h, JsObject o)
    {
        int hr = (int)GetVNum(h, o, "hour");
        int mi = (int)GetVNum(h, o, "minute");
        int s = (int)GetVNum(h, o, "second");
        int ms = (int)GetVNum(h, o, "millisecond");
        int us = (int)GetVNum(h, o, "microsecond");
        int ns = (int)GetVNum(h, o, "nanosecond");
        long frac = ms * 1_000_000L + us * 1_000L + ns;
        string fracStr = frac == 0 ? "" : $".{frac:D9}".TrimEnd('0');
        return JsValue.FromString($"{hr:D2}:{mi:D2}:{s:D2}{fracStr}");
    }

    /// <summary>Format a PlainDateTime as ISO 8601 string (e.g. "2024-01-15T12:00:00").</summary>
    private static JsValue FormatPlainDateTime(JsHeap h, JsObject o)
    {
        var date = DecodeIsoDateLong(h, o);
        int hr = (int)GetVNum(h, o, "hour");
        int mi = (int)GetVNum(h, o, "minute");
        int se = (int)GetVNum(h, o, "second");
        int ms = (int)GetVNum(h, o, "millisecond");
        int us = (int)GetVNum(h, o, "microsecond");
        int ns = (int)GetVNum(h, o, "nanosecond");
        long frac = ms * 1_000_000L + us * 1_000L + ns;
        string fracStr = frac == 0 ? "" : $".{frac:D9}".TrimEnd('0');
        return JsValue.FromString($"{FormatIsoYear(date.Year)}-{date.Month:D2}-{date.Day:D2}T{hr:D2}:{mi:D2}:{se:D2}{fracStr}{CalendarSuffix(h, o)}");
    }

    /// <summary>Format a PlainYearMonth as ISO 8601 string (e.g. "2024-01").</summary>
    private static JsValue FormatPlainYearMonth(JsHeap h, JsObject o)
    {
        int y = (int)GetVNum(h, o, "y");
        int m = (int)GetVNum(h, o, "m");
        return JsValue.FromString($"{FormatIsoYear(y)}-{m:D2}{CalendarSuffix(h, o)}");
    }

    /// <summary>Format a PlainMonthDay as ISO 8601 string (e.g. "01-15").</summary>
    private static JsValue FormatPlainMonthDay(JsHeap h, JsObject o)
    {
        var iso = DecodeIsoDate(h, o);
        string cal = CalId(h, o);
        if (cal == "iso8601")
        {
            return JsValue.FromString($"{iso.Month:D2}-{iso.Day:D2}");
        }
        else
        {
            return JsValue.FromString($"{FormatIsoYear(iso.Year)}-{iso.Month:D2}-{iso.Day:D2}[u-ca={cal}]");
        }
    }

    /// <summary>Format a ZonedDateTime as ISO 8601 string per the toString options.</summary>
    private static JsValue FormatZonedDateTime(IBuiltinContext ctx, JsHeap h, JsObject o, ToStringOptions opts)
    {
        string tz = GetVStr(h, o, "tz");
        var epochNsBig = DecodeInstantNanosBig(h, o);
        System.Numerics.BigInteger inc = PrecisionIncrementNs(opts);
        if (inc > 1) epochNsBig = RoundNsToIncrement(ctx, epochNsBig, inc, opts.RoundingMode);
        long epochNs = ToSafeLong(epochNsBig);
        long offsetNs = TemporalTimeZones.GetOffsetNs(tz, epochNs);
        var (date, time) = TemporalTimeZones.WallFromEpochNs(epochNs, offsetNs);
        var sb = new System.Text.StringBuilder();
        sb.Append($"{FormatIsoYear(date.Year)}-{date.Month:D2}-{date.Day:D2}T{time.Hour:D2}:{time.Minute:D2}");
        sb.Append(FormatSecondsPart(time.ToNanosecondsOfDay(), opts));
        if (opts.ShowOffset != "never") sb.Append(TemporalTimeZones.FormatOffsetRoundedToMinute(offsetNs));
        if (opts.TimeZoneName != "never") sb.Append(opts.TimeZoneName == "critical" ? $"[!{tz}]" : $"[{tz}]");
        sb.Append(CalendarSuffix(h, o, opts));
        return JsValue.FromString(sb.ToString());
    }

    // ─── Arg helpers ────────────────────────────────────────

    private static string ToStrArg(IBuiltinContext ctx, JsValue v)
    {
        if (v.Tag == JsValueTag.String) return v.AsString();
        if (v.Tag == JsValueTag.Object) return ctx.ToStringValue(v);
        if (v.Tag is JsValueTag.Number or JsValueTag.Int32) return v.AsNumber().ToString(System.Globalization.CultureInfo.InvariantCulture);
        return ctx.ToStringValue(v);
    }

    // ─── Spec validation helpers (backed by FenBrowser.Js.Temporal) ───

    /// <summary>ECMA-262 ToIntegerWithTruncation: RangeError on NaN/±Infinity.</summary>
    private static double ToIntegerWithTruncation(IBuiltinContext ctx, JsValue v)
    {
        var n = ctx.ToNumber(v);
        if (double.IsNaN(n) || double.IsInfinity(n))
            throw new JsThrownException(ctx.CreateRangeError("Value must be a finite number."));
        return Math.Truncate(n);
    }

    /// <summary>Required integer argument (undefined → NaN → RangeError per spec).</summary>
    private static double ArgInt(IBuiltinContext ctx, IReadOnlyList<JsValue> a, int i)
        => ToIntegerWithTruncation(ctx, i < a.Count ? a[i] : JsValue.Undefined);

    /// <summary>Optional integer argument with a default for undefined.</summary>
    private static double ArgIntOr(IBuiltinContext ctx, IReadOnlyList<JsValue> a, int i, double dflt)
        => i >= a.Count || a[i].Tag == JsValueTag.Undefined ? dflt : ToIntegerWithTruncation(ctx, a[i]);

    /// <summary>Canonicalize a calendar identifier; RangeError when unsupported.</summary>
    private static string CanonicalizeCalendarId(IBuiltinContext ctx, string id)
    {
        var canonical = TemporalCalendars.Canonicalize(id);
        if (canonical is not null)
            return canonical;

        // ParseTemporalCalendarString: any ISO date/time string also names a
        // calendar via its optional [u-ca] annotation, defaulting to iso8601.
        // Accept date-time, year-month, month-day and time-only productions.
        if (TemporalIsoParser.TryParseDateTime(id, out var parsed, out _)
            || TemporalIsoParser.TryParseYearMonth(id, out parsed, out _)
            || TemporalIsoParser.TryParseMonthDay(id, out parsed, out _)
            || TemporalIsoParser.TryParseTime(id, out parsed, out _))
        {
            if (parsed.Calendar is null)
                return "iso8601";
            canonical = TemporalCalendars.Canonicalize(parsed.Calendar);
            if (canonical is not null)
                return canonical;
        }

        throw new JsThrownException(ctx.CreateRangeError($"'{id}' is not a valid calendar identifier."));
    }

    private static string ToCalendarIdentifier(IBuiltinContext ctx, JsHeap h, JsValue v)
    {
        if (v.Tag == JsValueTag.String)
            return CanonicalizeCalendarId(ctx, v.AsString());
        if (v.Tag == JsValueTag.Object)
        {
            var obj = h.GetObject(v.AsObjectHandle());
            if (obj.TryGetOwnProperty("_v", out var vd) && vd.Value.Tag == JsValueTag.String)
            {
                return vd.Value.AsString();
            }
            if (TryGetInternalData(h, obj, out var data) && HasOwn(h, data, "calendarId"))
            {
                var cid = GetVStr(h, obj, "calendarId");
                return string.IsNullOrEmpty(cid) ? "iso8601" : cid;
            }
            if (TryGetField(ctx, h, v, "calendar", out var calField) && calField.Tag != JsValueTag.Undefined)
            {
                return ToCalendarIdentifier(ctx, h, calField);
            }
        }

        throw new JsThrownException(ctx.CreateTypeError("calendar must be a string or object."));
    }

    /// <summary>Optional calendar argument: undefined → iso8601; non-string → TypeError.</summary>
    private static string CalendarArg(IBuiltinContext ctx, IReadOnlyList<JsValue> a, int i)
    {
        if (i >= a.Count || a[i].Tag == JsValueTag.Undefined) return "iso8601";
        if (a[i].Tag != JsValueTag.String)
            throw new JsThrownException(ctx.CreateTypeError("calendar must be a string."));
        return CanonicalizeCalendarId(ctx, a[i].AsString());
    }

    /// <summary>
    /// Calendar from a parsed ISO string's [u-ca] annotation (null → iso8601). The annotation
    /// value must itself be a valid calendar identifier — it is never re-parsed as a date, so a
    /// date-shaped value such as "11111111" or "1111-11-11" is a RangeError, not iso8601.
    /// </summary>
    private static string CalendarFromAnnotation(IBuiltinContext ctx, string? annotation)
    {
        if (annotation is null) return "iso8601";
        var canonical = TemporalCalendars.Canonicalize(annotation);
        if (canonical is not null) return canonical;
        throw new JsThrownException(ctx.CreateRangeError($"'{annotation}' is not a valid calendar identifier."));
    }

    /// <summary>GetOptionsObject: options must be undefined or an object.</summary>
    private static void RequireOptionsObject(IBuiltinContext ctx, IReadOnlyList<JsValue> a, int i)
    {
        if (i < a.Count && a[i].Tag != JsValueTag.Undefined && a[i].Tag != JsValueTag.Object)
            throw new JsThrownException(ctx.CreateTypeError("options must be an object or undefined."));
    }

    /// <summary>ToTemporalOverflow: "constrain" (default) or "reject"; validates options shape.</summary>
    private static string GetOverflowOption(IBuiltinContext ctx, JsHeap h, IReadOnlyList<JsValue> a, int i)
    {
        RequireOptionsObject(ctx, a, i);
        if (i >= a.Count || a[i].Tag != JsValueTag.Object) return "constrain";
        var optionsValue = a[i];
        var obj = h.GetObject(optionsValue.AsObjectHandle());
        if (!ctx.TryGetPropertyValue(obj, optionsValue, "overflow", out var v) || v.Tag == JsValueTag.Undefined)
            return "constrain";
        var s = ctx.ToStringValue(v);
        if (s is not ("constrain" or "reject"))
            throw new JsThrownException(ctx.CreateRangeError($"'{s}' is not a valid value for overflow."));
        return s;
    }

    /// <summary>GetTemporalDisambiguationOption: options.disambiguation, validated.</summary>
    private static string GetDisambiguationOption(IBuiltinContext ctx, JsHeap h, IReadOnlyList<JsValue> a, int i)
    {
        RequireOptionsObject(ctx, a, i);
        if (i >= a.Count || a[i].Tag != JsValueTag.Object) return "compatible";
        var optionsValue = a[i];
        var obj = h.GetObject(optionsValue.AsObjectHandle());
        if (!ctx.TryGetPropertyValue(obj, optionsValue, "disambiguation", out var v) || v.Tag == JsValueTag.Undefined)
            return "compatible";
        var s = ctx.ToStringValue(v);
        if (s is not ("compatible" or "earlier" or "later" or "reject"))
            throw new JsThrownException(ctx.CreateRangeError($"'{s}' is not a valid value for disambiguation."));
        return s;
    }

    /// <summary>ToTemporalOffset: options.offset, validated ("prefer"/"use"/"ignore"/"reject").</summary>
    private static string GetOffsetOption(
        IBuiltinContext ctx,
        JsHeap h,
        IReadOnlyList<JsValue> a,
        int i,
        string defaultValue = "prefer")
    {
        RequireOptionsObject(ctx, a, i);
        if (i >= a.Count || a[i].Tag != JsValueTag.Object) return defaultValue;
        var optionsValue = a[i];
        var obj = h.GetObject(optionsValue.AsObjectHandle());
        if (!ctx.TryGetPropertyValue(obj, optionsValue, "offset", out var v) || v.Tag == JsValueTag.Undefined)
            return defaultValue;
        var s = ctx.ToStringValue(v);
        if (s is not ("prefer" or "use" or "ignore" or "reject"))
            throw new JsThrownException(ctx.CreateRangeError($"'{s}' is not a valid value for offset."));
        return s;
    }

    /// <summary>Read a property through the full Get protocol (triggers accessors); false when absent/undefined.</summary>
    private static bool TryGetField(IBuiltinContext ctx, JsHeap h, JsValue bagValue, string name, out JsValue value)
    {
        var obj = h.GetObject(bagValue.AsObjectHandle());
        if (ctx.TryGetPropertyValue(obj, bagValue, name, out value) && value.Tag != JsValueTag.Undefined)
            return true;
        value = JsValue.Undefined;
        return false;
    }

    /// <summary>Resolve the month from a field bag's month/monthCode (ISO semantics).</summary>
    private static double GetMonthFromFields(IBuiltinContext ctx, JsHeap h, JsValue bagValue)
    {
        // A field that is present but undefined is treated as absent (spec reads each
        // field with Get and falls back when the result is undefined).
        bool hasMonth = TryGetField(ctx, h, bagValue, "month", out var monthValue) && monthValue.Tag != JsValueTag.Undefined;
        bool hasCode = TryGetField(ctx, h, bagValue, "monthCode", out var codeValue) && codeValue.Tag != JsValueTag.Undefined;
        if (hasCode)
        {
            codeValue = JsValue.FromString(ctx.ToStringValue(codeValue));
            if (codeValue.Tag != JsValueTag.String)
                throw new JsThrownException(ctx.CreateTypeError("monthCode must be a string."));
            var mc = codeValue.AsString();
            if (mc.Length != 3 || mc[0] != 'M' || !char.IsAsciiDigit(mc[1]) || !char.IsAsciiDigit(mc[2]))
                throw new JsThrownException(ctx.CreateRangeError($"'{mc}' is not a valid monthCode."));
            int m = (mc[1] - '0') * 10 + (mc[2] - '0');
            if (m < 1 || m > 12)
                throw new JsThrownException(ctx.CreateRangeError($"'{mc}' is not a valid monthCode for the iso8601 calendar."));
            if (hasMonth && ToIntegerWithTruncation(ctx, monthValue) != m)
                throw new JsThrownException(ctx.CreateRangeError("month and monthCode conflict."));
            return m;
        }

        if (!hasMonth)
            throw new JsThrownException(ctx.CreateTypeError("month or monthCode is required."));
        return ToIntegerWithTruncation(ctx, monthValue);
    }

    /// <summary>Calendar from a field bag ("calendar" property): TypeError/RangeError per ToTemporalCalendarIdentifier.</summary>
    private static string GetCalendarFromFields(IBuiltinContext ctx, JsHeap h, JsValue bagValue)
    {
        if (!TryGetField(ctx, h, bagValue, "calendar", out var calValue)) return "iso8601";
        return ToCalendarIdentifier(ctx, h, calValue);
    }

    /// <summary>RegulateISODate: constrain/reject then ISODateWithinLimits range-check.</summary>
    private static IsoDate RegulateIsoDate(IBuiltinContext ctx, double y, double m, double d, string overflow)
    {
        if (y is < -999_999 or > 999_999)
            throw new JsThrownException(ctx.CreateRangeError("Year is out of the supported range."));
        int year = (int)y;
        int month;
        int day;
        if (overflow == "constrain")
        {
            month = (int)Math.Clamp(m, 1, 12);
            day = (int)Math.Clamp(d, 1, IsoMath.DaysInMonth(year, month));
        }
        else
        {
            if (m is < 1 or > 12 || d is < 1 or > 31 || !IsoMath.IsValidIsoDate(year, (int)m, (int)d))
                throw new JsThrownException(ctx.CreateRangeError("Invalid ISO date."));
            month = (int)m;
            day = (int)d;
        }

        if (!IsoMath.IsoDateWithinLimits(new IsoDate(year, month, day)))
            throw new JsThrownException(ctx.CreateRangeError("Date is outside the supported Temporal range."));
        return new IsoDate(year, month, day);
    }

    /// <summary>RegulateISODate + CreateTemporalDate.</summary>
    private static JsValue MakePlainDateRegulated(IBuiltinContext ctx, JsHeap h, double y, double m, double d, string calendar, string overflow)
    {
        var date = RegulateIsoDate(ctx, y, m, d, overflow);
        return MakePlainDateYmd(ctx, h, date.Year, date.Month, date.Day, calendar);
    }

    // ─── Non-ISO calendar integration ───────────────────────────────────────

    /// <summary>The instance's calendar id, defaulting to iso8601.</summary>
    private static string CalId(JsHeap h, JsObject o)
    {
        var c = GetVStr(h, o, "calendarId");
        return string.IsNullOrEmpty(c) ? "iso8601" : c;
    }

    private static void RequireMatchingCalendar(IBuiltinContext ctx, string self, string other)
    {
        string a = string.IsNullOrEmpty(self) ? "iso8601" : self;
        string b = string.IsNullOrEmpty(other) ? "iso8601" : other;
        if (!string.Equals(a, b, StringComparison.Ordinal))
            throw new JsThrownException(ctx.CreateRangeError("cannot use until/since with PDTs having different calendars"));
    }

    private static void RequireMatchingTimeZone(IBuiltinContext ctx, string self, string other)
    {
        string a = CanonicalTimeZoneKey(ctx, self);
        string b = CanonicalTimeZoneKey(ctx, other);
        if (!string.Equals(a, b, StringComparison.OrdinalIgnoreCase))
            throw new JsThrownException(ctx.CreateRangeError("cannot use until/since with ZonedDateTimes having different time zones"));
    }

    private static void RequireNoTimeStyle(IBuiltinContext ctx, JsHeap h, IReadOnlyList<JsValue> a)
    {
        // Options may be in a[0] (no locale) or a[1] (locale, options)
        for (int i = 0; i < a.Count; i++)
        {
            if (a[i].Tag == JsValueTag.Object && TryGetField(ctx, h, a[i], "timeStyle", out _))
                throw new JsThrownException(ctx.CreateTypeError("timeStyle conflicts with date-only Temporal types."));
        }
    }

    private static void RequireNoDateStyle(IBuiltinContext ctx, JsHeap h, IReadOnlyList<JsValue> a)
    {
        for (int i = 0; i < a.Count; i++)
        {
            if (a[i].Tag == JsValueTag.Object && TryGetField(ctx, h, a[i], "dateStyle", out _))
                throw new JsThrownException(ctx.CreateTypeError("dateStyle conflicts with time-only Temporal types."));
        }
    }

    /// <summary>Calendar-relative field view of an ISO date, or null for iso8601 / unsupported calendars.</summary>
    private static CalendarFields? CalFields(string calId, IsoDate iso)
        => CalendarMath.Get(calId)?.ToFields(iso);

    /// <summary>era getter value for a non-ISO calendar (undefined for iso8601).</summary>
    private static JsValue EraValue(string calId, IsoDate iso)
    {
        var f = CalFields(calId, iso);
        return f is { Era: { } e } ? JsValue.FromString(e) : JsValue.Undefined;
    }

    private static JsValue EraYearValue(string calId, IsoDate iso)
    {
        var f = CalFields(calId, iso);
        return f is { EraYear: { } ey } ? JsValue.FromNumber(ey) : JsValue.Undefined;
    }

    /// <summary>True when a calendar defines eras (EraFor returns a non-null era).</summary>
    private static bool CalendarUsesEras(CalendarSystem sys)
        => sys.EraFor(0, 0).Era is not null;

    /// <summary>
    /// CalendarResolveFields for a date bag in a non-ISO calendar: read era/eraYear/year,
    /// month/monthCode and day (in sorted key order), resolving to native (year, monthOrdinal, day).
    /// When <paramref name="baseFields"/> is given (the `with` path), absent fields fall back to it.
    /// </summary>
    private static (int Year, int Month, int Day) ResolveCalendarDateFields(
        IBuiltinContext ctx, JsHeap h, JsValue bag, CalendarSystem sys, CalendarFields? baseFields, bool requireDay, bool readDay = true, bool isWith = false)
    {
        // PrepareTemporalFields reads keys in sorted order: day, era, eraYear, month, monthCode, year.
        JsValue dayV = JsValue.Undefined;
        bool hasDay = readDay && TryGetField(ctx, h, bag, "day", out dayV);
        bool hasEra = TryGetField(ctx, h, bag, "era", out var eraV);
        bool hasEraYear = TryGetField(ctx, h, bag, "eraYear", out var eraYearV);
        bool hasMonth = TryGetField(ctx, h, bag, "month", out var monthV);
        bool hasMonthCode = TryGetField(ctx, h, bag, "monthCode", out var mcV);
        bool hasYear = TryGetField(ctx, h, bag, "year", out var yearV);

        bool fresh = baseFields is null;
        bool usesEras = CalendarUsesEras(sys);

        // For calendars that do not use eras, era/eraYear are in
        // NonIsoFieldKeysToIgnore — treat them as absent.
        if (!usesEras && (hasEra || hasEraYear))
        {
            // Observe coercion of era/eraYear (spec requires it) even though ignored.
            if (hasEra) _ = ctx.ToStringValue(eraV);
            if (hasEraYear) _ = ToIntegerWithTruncation(ctx, eraYearV);
            if (isWith)
                throw new JsThrownException(ctx.CreateTypeError("eraYear and era are invalid for this calendar"));
            // NonIsoFieldKeysToIgnore removes era/eraYear for calendars without eras.
            // For `from` (isWith=false) these keys are silently dropped.
            hasEra = false;
            hasEraYear = false;
        }

        // ── presence checks (TypeError), before any value validation (RangeError) ──
        // era and eraYear must be supplied together.
        if (hasEra != hasEraYear)
            throw new JsThrownException(ctx.CreateTypeError("era and eraYear must be provided together."));
        bool hasYearInfo = hasYear || (hasEra && hasEraYear);
        if (fresh && !hasYearInfo)
            throw new JsThrownException(ctx.CreateTypeError("year (or era and eraYear) is required."));
        if (fresh && !hasMonth && !hasMonthCode)
            throw new JsThrownException(ctx.CreateTypeError("month or monthCode is required."));
        if (fresh && requireDay && !hasDay)
            throw new JsThrownException(ctx.CreateTypeError("day is required."));

        // ── value resolution (RangeError) ──
        int year;
        if (hasEra && hasEraYear)
        {
            eraV = JsValue.FromString(ctx.ToStringValue(eraV));
            if (eraV.Tag != JsValueTag.String)
                throw new JsThrownException(ctx.CreateTypeError("era must be a string."));
            int eraYear = ToSafeInt(ToIntegerWithTruncation(ctx, eraYearV));
            if (!sys.YearFromEra(eraV.AsString(), eraYear, out year))
                throw new JsThrownException(ctx.CreateRangeError($"'{eraV.AsString()}' is not a valid era for the {sys.Id} calendar."));
            if (hasYear) _ = ToIntegerWithTruncation(ctx, yearV); // observe coercion; era takes precedence
        }
        else if (hasYear)
        {
            year = ToSafeInt(ToIntegerWithTruncation(ctx, yearV));
        }
        else
        {
            year = baseFields!.Value.Year;
        }

        int monthOrdinal;
        if (hasMonthCode)
        {
            mcV = JsValue.FromString(ctx.ToStringValue(mcV));
            if (mcV.Tag != JsValueTag.String)
                throw new JsThrownException(ctx.CreateTypeError("monthCode must be a string."));
            if (!sys.MonthFromCode(year, mcV.AsString(), out monthOrdinal, out bool exists))
                throw new JsThrownException(ctx.CreateRangeError($"'{mcV.AsString()}' is not a valid monthCode."));
            if (!exists)
                throw new JsThrownException(ctx.CreateRangeError($"'{mcV.AsString()}' is not a valid monthCode for the {sys.Id} calendar in this year."));
            if (hasMonth && ToSafeInt(ToIntegerWithTruncation(ctx, monthV)) != monthOrdinal)
                throw new JsThrownException(ctx.CreateRangeError("month and monthCode conflict."));
        }
        else if (hasMonth)
        {
            monthOrdinal = ToSafeInt(ToIntegerWithTruncation(ctx, monthV));
        }
        else
        {
            // When neither monthCode nor month is provided, preserve the monthCode
            // from baseFields and re-resolve it for the new year. This is correct
            // because month ordinals may shift between leap/common years (e.g. Hebrew
            // M12 maps to ordinal 12 in a common year but 13 in a leap year).
            string baseCode = baseFields!.Value.MonthCode;
            if (!sys.MonthFromCode(year, baseCode, out monthOrdinal, out _))
                monthOrdinal = baseFields.Value.Month; // fallback
        }

        int day = hasDay ? ToSafeInt(ToIntegerWithTruncation(ctx, dayV)) : (baseFields?.Day ?? 1);
        return (year, monthOrdinal, day);
    }

    /// <summary>
    /// Resolve a date bag to an ISO date for the given calendar (ISO or non-ISO). Fields are read
    /// first, then the overflow option (matching the spec's observable operation order).
    /// </summary>
    private static IsoDate ResolveDateBagToIso(IBuiltinContext ctx, JsHeap h, JsValue bag, string calendar,
        IReadOnlyList<JsValue> a, int optIdx, CalendarFields? baseFields = null, bool requireDay = true, bool readDay = true, bool isWith = false, bool requireYear = true)
        => ResolveDateBagToIso(ctx, h, bag, calendar, a, optIdx, out _, baseFields, requireDay, readDay, isWith, requireYear);

    private static IsoDate ResolveDateBagToIso(IBuiltinContext ctx, JsHeap h, JsValue bag, string calendar,
        IReadOnlyList<JsValue> a, int optIdx, out string overflow, CalendarFields? baseFields = null, bool requireDay = true, bool readDay = true, bool isWith = false, bool requireYear = true)
    {
        overflow = GetOverflowOption(ctx, h, a, optIdx);
        bool hasMonth = TryGetField(ctx, h, bag, "month", out var monthValue) && monthValue.Tag != JsValueTag.Undefined;
        bool hasCode = TryGetField(ctx, h, bag, "monthCode", out var codeValue) && codeValue.Tag != JsValueTag.Undefined;
        if (baseFields is null && !hasMonth && !hasCode)
            throw new JsThrownException(ctx.CreateTypeError("month or monthCode is required."));

        var sys = CalendarMath.Get(calendar);
        if (sys is null || calendar == "iso8601")
        {
            // iso8601 (or a calendar we do not yet model): ISO field semantics.
            double y = baseFields?.Year ?? 1972;
            bool hasYear = TryGetField(ctx, h, bag, "year", out var yv);
            if (hasYear) y = ToIntegerWithTruncation(ctx, yv);
            else if (baseFields is null && requireYear) throw new JsThrownException(ctx.CreateTypeError("year is required."));
            double m;
            if (hasMonth || hasCode) m = GetMonthFromFields(ctx, h, bag);
            else if (baseFields is { } bfm) m = bfm.Month;
            else throw new JsThrownException(ctx.CreateTypeError("month or monthCode is required."));
            JsValue dv = JsValue.Undefined;
            bool hasDay = readDay && TryGetField(ctx, h, bag, "day", out dv);
            if (!hasDay && requireDay && baseFields is null)
                throw new JsThrownException(ctx.CreateTypeError("day is required."));
            double d = hasDay ? ToIntegerWithTruncation(ctx, dv) : (baseFields?.Day ?? 1);
            if (m < 1) throw new JsThrownException(ctx.CreateRangeError("Month must be a positive integer."));
            if (d < 1) throw new JsThrownException(ctx.CreateRangeError("Day must be a positive integer."));
            return RegulateIsoDate(ctx, y, m, d, overflow);
        }

        bool hasYearField = TryGetField(ctx, h, bag, "year", out _);
        bool hasEraField = TryGetField(ctx, h, bag, "era", out _);
        if (hasMonth && !hasYearField && !hasEraField && baseFields is null)
            throw new JsThrownException(ctx.CreateTypeError("month requires year (or use monthCode)."));

        if (!hasYearField && !hasEraField)
        {
            // ECMA-262: When no year or era is provided, use the receiver's fields
            // (baseFields) if available; otherwise treat it as the reference year 1972.
            if (requireYear && baseFields is null)
                throw new JsThrownException(ctx.CreateTypeError("year is required."));

            if (baseFields is null)
            {
                int refYear = 1972;
                if (hasCode && TryGetField(ctx, h, bag, "day", out var dayV))
                {
                    string mc = ctx.ToStringValue(codeValue);
                    int parsedDay = ToSafeInt(ToIntegerWithTruncation(ctx, dayV));

                    int maxPossibleDays = 0;
                    for (int y = 1972; y >= 1940; y--)
                    {
                        if (sys.MonthFromCode(y, mc, out int mo, out bool exists) && exists)
                        {
                            maxPossibleDays = Math.Max(maxPossibleDays, sys.DaysInMonthOrdinal(y, mo));
                        }
                    }

                    if (maxPossibleDays == 0)
                        throw new JsThrownException(ctx.CreateRangeError($"'{mc}' is not a valid monthCode for the {sys.Id} calendar."));

                    if (overflow == "constrain")
                    {
                        parsedDay = Math.Clamp(parsedDay, 1, maxPossibleDays);
                    }
                    else if (overflow == "reject")
                    {
                        if (parsedDay < 1 || parsedDay > maxPossibleDays)
                        {
                            throw new JsThrownException(ctx.CreateRangeError("Day is out of range for the month."));
                        }
                    }

                    bool found = false;
                    for (int targetIsoYear = 1972; targetIsoYear >= 1800; targetIsoYear--)
                    {
                        sys.ToNative(new IsoDate(targetIsoYear, 6, 15), out int cy, out _, out _);
                        for (int cyCandidate = cy + 1; cyCandidate >= cy - 1; cyCandidate--)
                        {
                            if (sys.MonthFromCode(cyCandidate, mc, out int mo, out bool exists) && exists)
                            {
                                if (parsedDay >= 1 && parsedDay <= sys.DaysInMonthOrdinal(cyCandidate, mo))
                                {
                                    if (sys.TryResolveToIso(cyCandidate, mo, parsedDay, "constrain", out var candidateIso))
                                    {
                                        if (candidateIso.Year <= 1972)
                                        {
                                            refYear = cyCandidate;
                                            found = true;
                                            break;
                                        }
                                    }
                                }
                            }
                        }
                        if (found)
                            break;
                    }
                }
                baseFields = new CalendarFields(null, null, refYear, 1, "M01", 1, 1, 31, 365, 12, false);
            }
        }

        var (year, monthOrdinal, day) = ResolveCalendarDateFields(ctx, h, bag, sys, baseFields, requireDay, readDay, isWith);
        if (!sys.TryResolveToIso(year, monthOrdinal, day, overflow, out var iso))
            throw new JsThrownException(ctx.CreateRangeError("Date is outside the supported range or invalid for the calendar."));
        return iso;
    }

    /// <summary>The internal _v data object, when the value is one of our Temporal instances.</summary>
    private static bool TryGetInternalData(JsHeap h, JsObject o, out JsObject data)
    {
        if (o.TryGetProperty("_v", x => h.GetObject(x), out var dd) && dd.Value.Tag == JsValueTag.Object)
        {
            data = h.GetObject(dd.Value.AsObjectHandle());
            return true;
        }

        data = null!;
        return false;
    }

    /// <summary>ToTemporalDate: PlainDate/PlainDateTime/ZonedDateTime instance, ISO string, or property bag → ISO date + calendar.</summary>
    private static (IsoDate Date, string Calendar) ToTemporalDateRecord(IBuiltinContext ctx, JsHeap h, JsValue arg)
    {
        if (arg.Tag == JsValueTag.String)
        {
            var s = arg.AsString();
            if (!TemporalIsoParser.TryParseDateTime(s, out var parsed, out var parseError) || parsed.HasUtcDesignator)
                throw new JsThrownException(ctx.CreateRangeError($"'{s}' is not a valid ISO date string: {parseError}"));
            var cal = CalendarFromAnnotation(ctx, parsed.Calendar);
            var date = new IsoDate(parsed.Year, parsed.Month, parsed.Day);
            if (!IsoMath.IsoDateWithinLimits(date))
                throw new JsThrownException(ctx.CreateRangeError("Date is outside the supported Temporal range."));
            return (date, cal);
        }

        if (arg.Tag == JsValueTag.Object)
        {
            var obj = h.GetObject(arg.AsObjectHandle());
            if (TryGetInternalData(h, obj, out var data))
            {
                if (HasOwn(h, data, "y") && HasOwn(h, data, "d"))
                    return (DecodeIsoDate(h, obj), GetVStr(h, obj, "calendarId"));
                if (HasOwn(h, data, "year") && HasOwn(h, data, "day"))
                    return (DecodeIsoDateLong(h, obj), GetVStr(h, obj, "calendarId"));
                throw new JsThrownException(ctx.CreateTypeError("Cannot convert this Temporal object to a date."));
            }

            string bagCal = GetCalendarFromFields(ctx, h, arg);
            var bagSys = CalendarMath.Get(bagCal);
            if (bagSys is not null)
            {
                var (cy, cmo, cd) = ResolveCalendarDateFields(ctx, h, arg, bagSys, null, requireDay: true);
                if (!bagSys.TryResolveToIso(cy, cmo, cd, "constrain", out var calIso))
                    throw new JsThrownException(ctx.CreateRangeError("Date is invalid for the calendar."));
                return (calIso, bagCal);
            }
            if (!TryGetField(ctx, h, arg, "year", out var yearValue))
                throw new JsThrownException(ctx.CreateTypeError("year is required."));
            double y = ToIntegerWithTruncation(ctx, yearValue);
            double m = GetMonthFromFields(ctx, h, arg);
            if (!TryGetField(ctx, h, arg, "day", out var dayValue))
                throw new JsThrownException(ctx.CreateTypeError("day is required."));
            double d = ToIntegerWithTruncation(ctx, dayValue);
            if (m < 1) throw new JsThrownException(ctx.CreateRangeError("Month must be a positive integer."));
            if (d < 1) throw new JsThrownException(ctx.CreateRangeError("Day must be a positive integer."));
            return (RegulateIsoDate(ctx, y, m, d, "constrain"), bagCal);
        }

        throw new JsThrownException(ctx.CreateTypeError("Cannot convert value to a Temporal date."));
    }

    private static (IsoDate Date, string Calendar) ToTemporalMonthDayRecord(IBuiltinContext ctx, JsHeap h, JsValue arg, IReadOnlyList<JsValue> a, int optIdx)
    {
        if (arg.Tag == JsValueTag.String)
        {
            var s = arg.AsString();
            if (!TemporalIsoParser.TryParseMonthDay(s, out var parsed, out var parseError) || parsed.HasUtcDesignator)
                throw new JsThrownException(ctx.CreateRangeError($"'{s}' is not a valid ISO string for PlainMonthDay: {parseError}"));
            var parsedCal = CalendarFromAnnotation(ctx, parsed.Calendar);
            _ = GetOverflowOption(ctx, h, a, optIdx);
            var parsedDate = new IsoDate(parsed.Year, parsed.Month, parsed.Day);
            return (FindMonthDayReferenceDate(ctx, parsedCal, parsedDate), parsedCal);
        }
        if (arg.Tag == JsValueTag.Object)
        {
            var obj = h.GetObject(arg.AsObjectHandle());
            if (TryGetInternalData(h, obj, out var data))
            {
                if (HasOwn(h, data, "y") && HasOwn(h, data, "d") && HasOwn(h, data, "calendarId"))
                {
                    _ = GetOverflowOption(ctx, h, a, optIdx);
                    return (DecodeIsoDate(h, obj), CalId(h, obj));
                }
                string ical = GetVStr(h, obj, "calendarId"); if (string.IsNullOrEmpty(ical)) ical = "iso8601";
                if (HasOwn(h, data, "year") && HasOwn(h, data, "day"))
                {
                    _ = GetOverflowOption(ctx, h, a, optIdx);
                    return (DecodeIsoDateLong(h, obj), ical);
                }
                if (HasOwn(h, data, "y") && HasOwn(h, data, "d"))
                {
                    _ = GetOverflowOption(ctx, h, a, optIdx);
                    return (DecodeIsoDate(h, obj), ical);
                }
            }
            // Property bag
            string cal = GetCalendarFromFields(ctx, h, arg);
            var iso = ResolveDateBagToIso(ctx, h, arg, cal, a, optIdx, requireDay: true, readDay: true, requireYear: false);
            return (FindMonthDayReferenceDate(ctx, cal, iso), cal);
        }
        throw new JsThrownException(ctx.CreateTypeError("Argument must be a string or property bag."));
    }

    private static IsoDate FindMonthDayReferenceDate(IBuiltinContext ctx, string calendarId, IsoDate validatedDate)
    {
        if (calendarId == "iso8601")
            return new IsoDate(1972, validatedDate.Month, validatedDate.Day);

        var system = CalendarMath.Get(calendarId);
        var target = system?.ToFields(validatedDate);
        if (system is null || target is null)
            return validatedDate;

        long first = IsoMath.CivilToEpochDays(1972, 12, 31);
        long last = IsoMath.CivilToEpochDays(1573, 1, 1);
        for (long epochDay = first; epochDay >= last; epochDay--)
        {
            var candidate = IsoMath.EpochDaysToCivil(epochDay);
            var fields = system.ToFields(candidate);
            if (fields.MonthCode == target.Value.MonthCode && fields.Day == target.Value.Day)
                return candidate;
        }

        throw new JsThrownException(ctx.CreateRangeError("No valid PlainMonthDay reference date was found."));
    }

    /// <summary>ToTemporalTime: PlainTime/PlainDateTime instance, ISO time string, or property bag → wall-clock time.</summary>
    private static IsoTime ToTemporalTimeRecord(IBuiltinContext ctx, JsHeap h, JsValue arg)
    {
        if (arg.Tag == JsValueTag.String)
        {
            var s = arg.AsString();
            if (!TemporalIsoParser.TryParseTime(s, out var parsed, out var parseError) || parsed.HasUtcDesignator)
                throw new JsThrownException(ctx.CreateRangeError($"'{s}' is not a valid ISO time string: {parseError}"));
            // PlainTime ignores calendar annotations (it has no calendar).
            return parsed.Time;
        }

        if (arg.Tag == JsValueTag.Object)
        {
            var obj = h.GetObject(arg.AsObjectHandle());
            if (TryGetInternalData(h, obj, out var data))
            {
                if (HasOwn(h, data, "hour"))
                    return new IsoTime((int)GetVNum(h, obj, "hour"), (int)GetVNum(h, obj, "minute"), (int)GetVNum(h, obj, "second"),
                        (int)GetVNum(h, obj, "millisecond"), (int)GetVNum(h, obj, "microsecond"), (int)GetVNum(h, obj, "nanosecond"));
                throw new JsThrownException(ctx.CreateTypeError("Cannot convert this Temporal object to a time."));
            }

            string[] fields = { "hour", "minute", "second", "millisecond", "microsecond", "nanosecond" };
            var values = new double[fields.Length];
            bool any = false;
            for (int fi = 0; fi < fields.Length; fi++)
            {
                if (TryGetField(ctx, h, arg, fields[fi], out var fv))
                {
                    values[fi] = ToIntegerWithTruncation(ctx, fv);
                    any = true;
                }
            }

            if (!any)
                throw new JsThrownException(ctx.CreateTypeError("At least one time field is required."));
            return new IsoTime(
                (int)Math.Clamp(values[0], 0, 23), (int)Math.Clamp(values[1], 0, 59), (int)Math.Clamp(values[2], 0, 59),
                (int)Math.Clamp(values[3], 0, 999), (int)Math.Clamp(values[4], 0, 999), (int)Math.Clamp(values[5], 0, 999));
        }

        throw new JsThrownException(ctx.CreateTypeError("Cannot convert value to a Temporal time."));
    }

    /// <summary>ToTemporalDuration: Duration instance, ISO duration string, or property bag.</summary>
    private static (double years, double months, double weeks, double days, double hours, double minutes, double seconds, double millis, double micros, double nanos)
        ToTemporalDurationRecord(IBuiltinContext ctx, JsHeap h, JsValue arg)
    {
        if (arg.Tag == JsValueTag.String)
        {
            var s = arg.AsString();
            if (ParseIsoDuration(s, out var y, out var mo, out var w, out var d,
                    out var hr, out var mi, out var sec, out var ms, out var us, out var ns))
            {
                var canonicalValues = new double[] { y, mo, w, d, hr, mi, sec, ms, us, ns };
                ValidateDuration(ctx, canonicalValues);
                return (y, mo, w, d, hr, mi, sec, ms, us, ns);
            }
            throw new JsThrownException(ctx.CreateRangeError($"'{s}' is not a valid ISO duration string."));
        }

        if (arg.Tag == JsValueTag.Object)
        {
            var obj = h.GetObject(arg.AsObjectHandle());
            if (TryGetInternalData(h, obj, out var data) && HasOwn(h, data, "years"))
                return DecodeDuration(h, obj);

            // Spec: ToTemporalDurationRecord reads fields in alphabetical order.
            // Alpha order: days, hours, microseconds, milliseconds, minutes, months, nanoseconds, seconds, weeks, years.
            string[] fields = { "days", "hours", "microseconds", "milliseconds", "minutes", "months", "nanoseconds", "seconds", "weeks", "years" };
            var values = new double[fields.Length];
            bool any = false;
            for (int fi = 0; fi < fields.Length; fi++)
            {
                if (TryGetField(ctx, h, arg, fields[fi], out var fv))
                {
                    values[fi] = ToIntegerIfIntegral(ctx, fv);
                    any = true;
                }
            }

            if (!any)
                throw new JsThrownException(ctx.CreateTypeError("At least one duration field is required."));
            
            var canonicalValues = new double[] {
                values[9],  // years
                values[5],  // months
                values[8],  // weeks
                values[0],  // days
                values[1],  // hours
                values[4],  // minutes
                values[7],  // seconds
                values[3],  // milliseconds
                values[2],  // microseconds
                values[6]   // nanoseconds
            };
            ValidateDuration(ctx, canonicalValues);
            return (
                values[9],  // years
                values[5],  // months
                values[8],  // weeks
                values[0],  // days
                values[1],  // hours
                values[4],  // minutes
                values[7],  // seconds
                values[3],  // milliseconds
                values[2],  // microseconds
                values[6]); // nanoseconds
        }

        throw new JsThrownException(ctx.CreateTypeError("Cannot convert value to a Temporal duration."));
    }

    private static long DecodeTimeOfDayNs(JsHeap h, JsObject o)
        => new IsoTime((int)GetVNum(h, o, "hour"), (int)GetVNum(h, o, "minute"), (int)GetVNum(h, o, "second"),
            (int)GetVNum(h, o, "millisecond"), (int)GetVNum(h, o, "microsecond"), (int)GetVNum(h, o, "nanosecond")).ToNanosecondsOfDay();

    /// <summary>ToTemporalDateTime: instance, ISO string, or property bag → ISO date + wall-clock time.</summary>
    private static (IsoDate Date, IsoTime Time) ToTemporalDateTimeRecord(IBuiltinContext ctx, JsHeap h, JsValue arg)
    {
        if (arg.Tag == JsValueTag.String)
        {
            var s = arg.AsString();
            if (!TemporalIsoParser.TryParseDateTime(s, out var parsed, out var parseError) || parsed.HasUtcDesignator)
                throw new JsThrownException(ctx.CreateRangeError($"'{s}' is not a valid ISO date-time string: {parseError}"));
            _ = CalendarFromAnnotation(ctx, parsed.Calendar);
            var date = new IsoDate(parsed.Year, parsed.Month, parsed.Day);
            if (!IsoMath.IsoDateWithinLimits(date))
                throw new JsThrownException(ctx.CreateRangeError("Date is outside the supported Temporal range."));
            return (date, parsed.HasTime ? parsed.Time : IsoTime.Midnight);
        }

        if (arg.Tag == JsValueTag.Object)
        {
            var obj = h.GetObject(arg.AsObjectHandle());
            if (TryGetInternalData(h, obj, out var data))
            {
                if (HasOwn(h, data, "year") && HasOwn(h, data, "day"))
                    return (DecodeIsoDateLong(h, obj), new IsoTime(
                        (int)GetVNum(h, obj, "hour"), (int)GetVNum(h, obj, "minute"), (int)GetVNum(h, obj, "second"),
                        (int)GetVNum(h, obj, "millisecond"), (int)GetVNum(h, obj, "microsecond"), (int)GetVNum(h, obj, "nanosecond")));
                if (HasOwn(h, data, "y") && HasOwn(h, data, "d"))
                    return (DecodeIsoDate(h, obj), IsoTime.Midnight);
                throw new JsThrownException(ctx.CreateTypeError("Cannot convert this Temporal object to a date-time."));
            }

            string dtCal = GetCalendarFromFields(ctx, h, arg);
            var dtSys = CalendarMath.Get(dtCal);
            IsoDate bagDate;
            if (dtSys is not null)
            {
                var (cy, cmo, cd) = ResolveCalendarDateFields(ctx, h, arg, dtSys, null, requireDay: true);
                if (!dtSys.TryResolveToIso(cy, cmo, cd, "constrain", out bagDate))
                    throw new JsThrownException(ctx.CreateRangeError("Date is invalid for the calendar."));
            }
            else
            {
                if (!TryGetField(ctx, h, arg, "year", out var yearValue))
                    throw new JsThrownException(ctx.CreateTypeError("year is required."));
                double y = ToIntegerWithTruncation(ctx, yearValue);
                double m = GetMonthFromFields(ctx, h, arg);
                if (!TryGetField(ctx, h, arg, "day", out var dayValue))
                    throw new JsThrownException(ctx.CreateTypeError("day is required."));
                double d = ToIntegerWithTruncation(ctx, dayValue);
                if (m < 1) throw new JsThrownException(ctx.CreateRangeError("Month must be a positive integer."));
                if (d < 1) throw new JsThrownException(ctx.CreateRangeError("Day must be a positive integer."));
                bagDate = RegulateIsoDate(ctx, y, m, d, "constrain");
            }
            string[] timeFields = { "hour", "minute", "second", "millisecond", "microsecond", "nanosecond" };
            var tv = new double[timeFields.Length];
            for (int fi = 0; fi < timeFields.Length; fi++)
            {
                if (TryGetField(ctx, h, arg, timeFields[fi], out var fv))
                    tv[fi] = ToIntegerWithTruncation(ctx, fv);
            }
            return (bagDate, new IsoTime(
                (int)Math.Clamp(tv[0], 0, 23), (int)Math.Clamp(tv[1], 0, 59), (int)Math.Clamp(tv[2], 0, 59),
                (int)Math.Clamp(tv[3], 0, 999), (int)Math.Clamp(tv[4], 0, 999), (int)Math.Clamp(tv[5], 0, 999)));
        }

        throw new JsThrownException(ctx.CreateTypeError("Cannot convert value to a Temporal date-time."));
    }

    private const long NsPerDay = 86_400_000_000_000L;

    /// <summary>DifferenceISODateTime with largestUnit=day: days + balanced time, uniform sign.</summary>
    /// <summary>CalendarDateAdd: add a date duration in the given calendar (ISO for iso8601/unmodelled).</summary>
    private static IsoDate AddDateInCalendar(string cal, IsoDate date, double years, double months, double weeks, double days, bool constrain, out bool invalid)
    {
        var sys = CalendarMath.Get(cal);
        if (sys is null)
            return IsoMath.AddIsoDate(date, years, months, weeks, days, constrain, out invalid);
        return sys.Add(date, (long)years, (long)months, (long)weeks, (long)days, constrain, out invalid);
    }

    /// <summary>CalendarDateUntil: date duration (years, months, weeks, days) from one to two in the calendar.</summary>
    private static (int Years, int Months, int Weeks, long Days) DifferenceDateDuration(string cal, IsoDate one, IsoDate two, string largestUnit)
    {
        var sys = CalendarMath.Get(cal);
        if (sys is null)
            return IsoMath.DifferenceIsoDate(one, two, largestUnit);
        return sys.Difference(one, two, largestUnit);
    }

    private static JsValue DifferencePlainDateTimes(IBuiltinContext ctx, JsHeap h, IsoDate d1, long t1Ns, IsoDate d2, long t2Ns, string cal, string largestUnit, bool negate = false)
    {
        long days = IsoMath.ToEpochDays(d2) - IsoMath.ToEpochDays(d1);
        long ns = t2Ns - t1Ns;
        if (days > 0 && ns < 0) { days--; ns += NsPerDay; }
        else if (days < 0 && ns > 0) { days++; ns -= NsPerDay; }

        int yy = 0, mm = 0, ww = 0;
        long dd = days;
        int rank = DiffUnitRank(largestUnit);
        if (rank <= 2) // year / month / week → decompose the day span in the calendar
        {
            var adj2 = IsoMath.EpochDaysToCivil(IsoMath.ToEpochDays(d1) + days);
            (yy, mm, ww, dd) = DifferenceDateDuration(cal, d1, adj2, largestUnit);
        }
        else if (rank >= 4) // hour or smaller → fold whole days into the time total
        {
            ns += dd * NsPerDay;
            dd = 0;
        }

        int sign = ns < 0 ? -1 : 1;
        long absNs = Math.Abs(ns);
        int f = negate ? -1 : 1;
        return MakeDuration(ctx, h, f * yy, f * mm, f * ww, f * ToSafeInt(dd),
            f * sign * (int)(absNs / 3_600_000_000_000L), f * sign * (int)(absNs / 60_000_000_000L % 60), f * sign * (int)(absNs / 1_000_000_000L % 60),
            f * sign * (int)(absNs / 1_000_000L % 1000), f * sign * (int)(absNs / 1_000L % 1000), f * sign * (int)(absNs % 1000));
    }

    /// <summary>DifferencePlainDateTimes with rounding support. Applies smallestUnit,
    /// roundingMode, and roundingIncrement for both time and calendar units.</summary>
    private static JsValue DifferencePlainDateTimesRounded(IBuiltinContext ctx, JsHeap h, IsoDate d1, long t1Ns, IsoDate d2, long t2Ns, string cal, DiffSettings s, bool negate = false)
    {
        long epoch1 = IsoMath.ToEpochDays(d1), epoch2 = IsoMath.ToEpochDays(d2);
        long diffNs = (epoch2 - epoch1) * NsPerDay + t2Ns - t1Ns;
        int neg = negate ? -1 : 1;
        int outSign = diffNs == 0 ? 0 : (neg * diffNs > 0 ? 1 : -1);

        string smallest = s.Smallest;
        if (smallest != "nanosecond" || s.Increment > 1)
        {
            int srank = DiffUnitRank(smallest);
            if (srank <= 2) // calendar unit (year, month, week, day)
            {
                // Decompose from earlier to later (positive direction), then apply
                // rounding mode (adjusted for output sign) before applying outSign.
                long absNs = Math.Abs(diffNs);
                long absDays = absNs / NsPerDay;
                long absRem = absNs % NsPerDay;
                var endDate = IsoMath.EpochDaysToCivil(epoch1 + (diffNs >= 0 ? absDays : -absDays));
                // Always decompose from earlier to later
                var from = diffNs >= 0 ? d1 : endDate;
                var to = diffNs >= 0 ? endDate : d1;
                var diff = DifferenceDateDuration(cal, from, to, smallest);
                long yR = Math.Abs(diff.Years), moR = Math.Abs(diff.Months), wR = Math.Abs(diff.Weeks), dR = Math.Abs(diff.Days);
                double inc = s.Increment;

                // When outSign < 0, some rounding modes need complementing because we
                // round the positive magnitude then negate (ceil on -x = -floor on x, etc.)
                string rMode = outSign >= 0 ? s.Mode : s.Mode switch {
                    "ceil" => "floor", "floor" => "ceil",
                    "halfCeil" => "halfFloor", "halfFloor" => "halfCeil",
                    _ => s.Mode
                };

                if (smallest == "year")
                {
                    yR = RoundToIncrement(yR, moR, 12, (long)inc, rMode, out _, out _);
                    moR = 0; wR = 0; dR = 0; absRem = 0;
                }
                else if (smallest == "month")
                {
                    // Use the "to" date's month length for fractional month rounding
                    int dim = IsoMath.DaysInMonth(to.Year, Math.Max(1, to.Month));
                    long totMonths = yR * 12 + moR;
                    totMonths = RoundToIncrement(totMonths, dR, dim, (long)inc, rMode, out _, out _);
                    yR = 0; moR = totMonths; wR = 0; dR = 0; absRem = 0;
                }
                else if (smallest == "week")
                {
                    // Round (weeks*7 + days) + time fraction to the week increment
                    long totalDayNs = (wR * 7 + dR) * NsPerDay + absRem;
                    long roundedNs = (long)RoundNsToIncrement(ctx, totalDayNs, (long)inc * 7 * NsPerDay, rMode);
                    wR = roundedNs / (7 * NsPerDay);
                    long remainNs = roundedNs % (7 * NsPerDay);
                    if (remainNs < 0) { wR--; remainNs += 7 * NsPerDay; }
                    dR = remainNs / NsPerDay;
                    absRem = remainNs % NsPerDay;
                }
                else // day
                {
                    long dayNs = dR * NsPerDay + absRem;
                    // For time units, RoundNsToIncrement handles sign correctly
                    dayNs = (long)RoundNsToIncrement(ctx, outSign >= 0 ? dayNs : -dayNs, (long)inc * NsPerDay, outSign >= 0 ? s.Mode : (s.Mode switch {
                        "ceil" => "floor", "floor" => "ceil",
                        "halfCeil" => "halfFloor", "halfFloor" => "halfCeil",
                        _ => s.Mode
                    }));
                    dR = dayNs / NsPerDay;
                    absRem = dayNs % NsPerDay;
                    if (absRem < 0) { dR--; absRem += NsPerDay; }
                }

                int remSign = absRem < 0 ? -1 : 1;
                long a = Math.Abs(absRem);
                return MakeDuration(ctx, h, outSign * (int)yR, outSign * (int)moR, outSign * (int)wR, outSign * ToSafeInt(dR),
                    outSign * remSign * (int)(a / 3_600_000_000_000L), outSign * remSign * (int)(a / 60_000_000_000L % 60),
                    outSign * remSign * (int)(a / 1_000_000_000L % 60), outSign * remSign * (int)(a / 1_000_000L % 1000),
                    outSign * remSign * (int)(a / 1_000L % 1000), outSign * remSign * (int)(a % 1000));
            }
            // time unit → round total ns directly
            return MakeDiffDuration(ctx, h, neg * diffNs, s);
        }

        // No rounding → decompose using largestUnit
        long absDiff = Math.Abs(diffNs);
        long dX = absDiff / NsPerDay, nsX = absDiff % NsPerDay;
        int yyX = 0, mmX = 0, wwX = 0; long ddX = dX;
        int rX = DiffUnitRank(s.Largest);
        if (rX <= 2) { var a2 = IsoMath.EpochDaysToCivil(epoch1 + (diffNs >= 0 ? dX : -dX)); (yyX, mmX, wwX, ddX) = DifferenceDateDuration(cal, d1, a2, s.Largest); yyX = Math.Abs(yyX); mmX = Math.Abs(mmX); wwX = Math.Abs(wwX); ddX = Math.Abs(ddX); }
        else if (rX >= 4) { nsX += ddX * NsPerDay; ddX = 0; }
        int sX = nsX < 0 ? -1 : 1; long aX = Math.Abs(nsX);
        return MakeDuration(ctx, h, outSign * yyX, outSign * mmX, outSign * wwX, outSign * ToSafeInt(ddX),
            outSign * sX * (int)(aX / 3_600_000_000_000L), outSign * sX * (int)(aX / 60_000_000_000L % 60),
            outSign * sX * (int)(aX / 1_000_000_000L % 60), outSign * sX * (int)(aX / 1_000_000L % 1000),
            outSign * sX * (int)(aX / 1_000L % 1000), outSign * sX * (int)(aX % 1000));
    }

    /// <summary>ToTemporalInstant for method arguments: Instant/ZonedDateTime instance or ISO string → epoch ns.</summary>
    private static long ToInstantNs(IBuiltinContext ctx, JsHeap h, JsValue arg)
    {
        if (arg.Tag == JsValueTag.Object)
        {
            var obj = h.GetObject(arg.AsObjectHandle());
            if (TryGetInternalData(h, obj, out var data))
            {
                if (HasOwn(h, data, "ens") || HasOwn(h, data, "ensBig"))
                    return DecodeInstantNanos(h, obj);
                if (HasOwn(h, data, "epochNanoseconds"))
                    return ToSafeLong(new System.Numerics.BigInteger(GetVNum(h, obj, "epochNanoseconds")));
            }

            arg = JsValue.FromString(ctx.ToStringValue(arg));
        }

        if (arg.Tag != JsValueTag.String)
            throw new JsThrownException(ctx.CreateTypeError("Cannot convert value to a Temporal instant."));
        var s = arg.AsString();
        if (!TemporalIsoParser.TryParseInstant(s, out var parsed, out var parseError))
            throw new JsThrownException(ctx.CreateRangeError($"'{s}' is not a valid ISO string for Instant: {parseError}"));
        // Temporal.Instant has no calendar: any [u-ca=...] annotation (even critical, even
        // an unknown value) is parsed and ignored, never validated.
        var epochDays = IsoMath.CivilToEpochDays(parsed.Year, parsed.Month, parsed.Day);
        var ns = new System.Numerics.BigInteger(epochDays) * NsPerDay
                 + parsed.Time.ToNanosecondsOfDay()
                 - (parsed.HasUtcDesignator ? 0L : parsed.OffsetNanoseconds);
        if (System.Numerics.BigInteger.Abs(ns) > MaxInstantNs)
            throw new JsThrownException(ctx.CreateRangeError("Instant is outside the supported range."));
        return ToSafeLong(ns);
    }

    /// <summary>ToTemporalInstant returning BigInteger epoch ns (no long clamping).</summary>
    private static System.Numerics.BigInteger ToInstantNsBig(IBuiltinContext ctx, JsHeap h, JsValue arg)
    {
        if (arg.Tag == JsValueTag.Object)
        {
            var obj = h.GetObject(arg.AsObjectHandle());
            if (TryGetInternalData(h, obj, out var data))
            {
                if (HasOwn(h, data, "ens") || HasOwn(h, data, "ensBig"))
                    return DecodeInstantNanosBig(h, obj);
                if (HasOwn(h, data, "epochNanoseconds"))
                    return new System.Numerics.BigInteger(GetVNum(h, obj, "epochNanoseconds"));
            }
            arg = JsValue.FromString(ctx.ToStringValue(arg));
        }
        if (arg.Tag != JsValueTag.String)
            throw new JsThrownException(ctx.CreateTypeError("Cannot convert value to a Temporal instant."));
        var s = arg.AsString();
        if (!TemporalIsoParser.TryParseInstant(s, out var parsed, out var parseError))
            throw new JsThrownException(ctx.CreateRangeError($"'{s}' is not a valid ISO string for Instant: {parseError}"));
        var epochDays = IsoMath.CivilToEpochDays(parsed.Year, parsed.Month, parsed.Day);
        var ns = new System.Numerics.BigInteger(epochDays) * NsPerDay
                 + parsed.Time.ToNanosecondsOfDay()
                 - (parsed.HasUtcDesignator ? 0L : parsed.OffsetNanoseconds);
        if (System.Numerics.BigInteger.Abs(ns) > MaxInstantNs)
            throw new JsThrownException(ctx.CreateRangeError("Instant is outside the supported range."));
        return ns;
    }

    /// <summary>ToTemporalYearMonth: instance, ISO string, or property bag.</summary>
    private static (int Year, int Month, int Day, string Calendar) ToTemporalYearMonthRecord(IBuiltinContext ctx, JsHeap h, JsValue arg)
    {
        if (arg.Tag == JsValueTag.String)
        {
            var s = arg.AsString();
            if (!TemporalIsoParser.TryParseYearMonth(s, out var parsed, out var parseError) || parsed.HasUtcDesignator)
                throw new JsThrownException(ctx.CreateRangeError($"'{s}' is not a valid ISO year-month string: {parseError}"));
            var cal = CalendarFromAnnotation(ctx, parsed.Calendar);
            var refIso = YearMonthReferenceIso(cal, new IsoDate(parsed.Year, parsed.Month, Math.Max(1, parsed.Day)));
            return (refIso.Year, refIso.Month, refIso.Day, cal);
        }

        if (arg.Tag == JsValueTag.Object)
        {
            var obj = h.GetObject(arg.AsObjectHandle());
            if (TryGetInternalData(h, obj, out var data) && HasOwn(h, data, "y"))
            {
                var iso = DecodeYearMonthIso(h, obj);
                return (iso.Year, iso.Month, iso.Day, GetVStr(h, obj, "calendarId"));
            }

            string bagCal = GetCalendarFromFields(ctx, h, arg);
            var bagSys = CalendarMath.Get(bagCal);
            if (bagSys is not null)
            {
                var (cy, cmo, _) = ResolveCalendarDateFields(ctx, h, arg, bagSys, null, requireDay: false);
                if (!bagSys.TryResolveToIso(cy, cmo, 1, "constrain", out var iso))
                    throw new JsThrownException(ctx.CreateRangeError("Year-month is invalid for the calendar."));
                return (iso.Year, iso.Month, iso.Day, bagCal);
            }
            if (!TryGetField(ctx, h, arg, "year", out var yearValue))
                throw new JsThrownException(ctx.CreateTypeError("year is required."));
            double y = ToIntegerWithTruncation(ctx, yearValue);
            double m = GetMonthFromFields(ctx, h, arg);
            if (y is < -999_999 or > 999_999)
                throw new JsThrownException(ctx.CreateRangeError("Year is out of the supported range."));
            return ((int)y, (int)Math.Clamp(m, 1, 12), 1, bagCal);
        }

        throw new JsThrownException(ctx.CreateTypeError("Cannot convert value to a Temporal year-month."));
    }

    /// <summary>ToIntegerIfIntegral: RangeError unless the value converts to a finite integer.</summary>
    private static double ToIntegerIfIntegral(IBuiltinContext ctx, JsValue v)
    {
        if (v.Tag == JsValueTag.Undefined) return 0;
        var n = ctx.ToNumber(v);
        if (!double.IsFinite(n) || n != Math.Truncate(n))
            throw new JsThrownException(ctx.CreateRangeError("Duration components must be finite integers."));
        return n + 0.0; // normalize -0 to +0
    }

    /// <summary>IsValidDuration sign check: all components share one sign.</summary>
    private static void ValidateDurationSigns(IBuiltinContext ctx, params double[] components)
    {
        int sign = 0;
        foreach (var c in components)
        {
            if (c == 0) continue;
            int s = c < 0 ? -1 : 1;
            if (sign == 0) sign = s;
            else if (s != sign)
                throw new JsThrownException(ctx.CreateRangeError("Mixed-sign durations are invalid."));
        }
    }

    /// <summary>
    /// IsValidDuration (Temporal spec §7.5.12): sign consistency plus the range limits —
    /// abs(years|months|weeks) &lt; 2^32 and the days-through-nanoseconds part, expressed
    /// as mathematical nanoseconds, has abs &lt; 2^53 × 10^9. Components are in canonical order
    /// [years, months, weeks, days, hours, minutes, seconds, ms, µs, ns].
    ///
    /// Uses BigInteger arithmetic to avoid floating-point precision loss near the 2^53 boundary.
    /// </summary>
    private static void ValidateDuration(IBuiltinContext ctx, double[] v)
    {
        // Step 2a: every component must be a finite Number.
        int sign = 0;
        const double twoTo32 = 4294967296.0; // 2^32
        for (int i = 0; i < v.Length; i++)
        {
            if (!double.IsFinite(v[i]))
                throw new JsThrownException(ctx.CreateRangeError("Duration components must be finite."));
            if (v[i] == 0) continue;
            int s = v[i] < 0 ? -1 : 1;
            if (sign == 0) sign = s;
            else if (s != sign)
                throw new JsThrownException(ctx.CreateRangeError("Mixed-sign durations are invalid."));
            // Steps 2d–2f: years, months, weeks magnitude
            if (i <= 2 && Math.Abs(v[i]) >= twoTo32)
                throw new JsThrownException(ctx.CreateRangeError("Duration years, months, or weeks out of range."));
        }
        if (sign == 0) return; // all-zero duration is always valid

        // Steps 3–4: normalize time part to total nanoseconds and verify abs &lt; 2^53 seconds.
        // Use System.Numerics.BigInteger to avoid floating-point precision loss.
        // ℝ(𝔽(v)) values are exact integers at this point (ToIntegerIfIntegral guarantee).
        var maxNs = new System.Numerics.BigInteger(9007199254740992L) * 1_000_000_000L; // 2^53 s → ns
        System.Numerics.BigInteger totalNs =
            ToBigInteger(v[3]) * 86_400_000_000_000L +  // days → ns
            ToBigInteger(v[4]) *  3_600_000_000_000L +  // hours → ns
            ToBigInteger(v[5]) *     60_000_000_000L +  // minutes → ns
            ToBigInteger(v[6]) *      1_000_000_000L +  // seconds → ns
            ToBigInteger(v[7]) *          1_000_000L +  // ms → ns
            ToBigInteger(v[8]) *              1_000L +  // µs → ns
            ToBigInteger(v[9]);                          // ns
        if (System.Numerics.BigInteger.Abs(totalNs) >= maxNs)
            throw new JsThrownException(ctx.CreateRangeError("Duration time fields out of range."));
    }

    /// <summary>Convert a finite, integer-valued double to BigInteger.</summary>
    private static System.Numerics.BigInteger ToBigInteger(double d)
    {
        // Use BigInteger(double) constructor — it rounds to the nearest integer.
        // All callers guarantee the value is a finite integer (via IsFinite check +
        // ToIntegerIfIntegral), so rounding within ULP is harmless.
        return new System.Numerics.BigInteger(d);
    }

    /// <summary>Singular unit name; plurals accepted; RangeError on anything else.</summary>
    private static string NormalizeUnitName(IBuiltinContext ctx, string unit, bool allowAuto = false)
    {
        var s = unit switch
        {
            "years" => "year", "months" => "month", "weeks" => "week", "days" => "day",
            "hours" => "hour", "minutes" => "minute", "seconds" => "second",
            "milliseconds" => "millisecond", "microseconds" => "microsecond", "nanoseconds" => "nanosecond",
            _ => unit,
        };
        bool ok = s is "year" or "month" or "week" or "day" or "hour" or "minute" or "second"
            or "millisecond" or "microsecond" or "nanosecond" || (allowAuto && s == "auto");
        if (!ok)
            throw new JsThrownException(ctx.CreateRangeError($"'{unit}' is not a valid unit."));
        return s;
    }

    /// <summary>Map a unit name (singular or plural) to its singular form, or null if unknown.</summary>
    private static string? TryNormalizeUnit(string unit)
    {
        var s = unit switch
        {
            "years" => "year", "months" => "month", "weeks" => "week", "days" => "day",
            "hours" => "hour", "minutes" => "minute", "seconds" => "second",
            "milliseconds" => "millisecond", "microseconds" => "microsecond", "nanoseconds" => "nanosecond",
            _ => unit,
        };
        return s is "year" or "month" or "week" or "day" or "hour" or "minute" or "second"
            or "millisecond" or "microsecond" or "nanosecond" ? s : null;
    }

    // Allowed difference units per Temporal type (for since/until largestUnit/smallestUnit).
    private static readonly string[] TimeDiffUnits = { "hour", "minute", "second", "millisecond", "microsecond", "nanosecond" };
    private static readonly string[] DateTimeDiffUnits = { "year", "month", "week", "day", "hour", "minute", "second", "millisecond", "microsecond", "nanosecond" };
    private static readonly string[] DateDiffUnits = { "year", "month", "week", "day" };
    private static readonly string[] YearMonthDiffUnits = { "year", "month" };

    /// <summary>Settings produced by <see cref="GetDifferenceSettings"/> for since()/until().</summary>
    private struct DiffSettings
    {
        public string Smallest;
        public string Largest;
        public int Increment;
        public string Mode;
    }

    /// <summary>Total ordering of all ten Temporal units; rank 0 = year (largest) … 9 = nanosecond.</summary>
    private static int DiffUnitRank(string unit) => unit switch
    {
        "year" => 0, "month" => 1, "week" => 2, "day" => 3, "hour" => 4, "minute" => 5,
        "second" => 6, "millisecond" => 7, "microsecond" => 8, "nanosecond" => 9, _ => 9,
    };

    /// <summary>MaximumTemporalDurationRoundingIncrement: dividend for a smallestUnit, or 0 if unbounded.</summary>
    private static long MaxDurationRoundingIncrement(string unit) => unit switch
    {
        "hour" => 24, "minute" => 60, "second" => 60,
        "millisecond" => 1000, "microsecond" => 1000, "nanosecond" => 1000,
        _ => 0, // year/month/week/day: no maximum
    };

    private const string ValidRoundingModes = "ceil floor expand trunc halfCeil halfFloor halfExpand halfTrunc halfEven";

    /// <summary>
    /// GetDifferenceSettings for since()/until(): reads largestUnit, roundingIncrement,
    /// roundingMode, smallestUnit (in that spec order) and fully validates each, resolving
    /// "auto" largestUnit and checking the largest≥smallest constraint and the increment maximum.
    /// </summary>
    private static DiffSettings GetDifferenceSettings(IBuiltinContext ctx, JsHeap h, IReadOnlyList<JsValue> a, int i,
        string[] allowed, string fallbackSmallest, string defaultLargest)
    {
        RequireOptionsObject(ctx, a, i);
        var s = new DiffSettings { Smallest = fallbackSmallest, Largest = "auto", Increment = 1, Mode = "trunc" };
        if (i >= a.Count || a[i].Tag != JsValueTag.Object)
        {
            s.Largest = DiffUnitRank(defaultLargest) <= DiffUnitRank(fallbackSmallest) ? defaultLargest : fallbackSmallest;
            return s;
        }
        var opt = a[i];

        // 1. Read fields and cast them in spec order
        string? lv_str = null;
        if (TryGetField(ctx, h, opt, "largestUnit", out var lv) && lv.Tag != JsValueTag.Undefined)
        {
            lv_str = lv.Tag == JsValueTag.String ? lv.AsString() : ctx.ToStringValue(lv);
        }

        double? inc_val = null;
        if (TryGetField(ctx, h, opt, "roundingIncrement", out var iv) && iv.Tag != JsValueTag.Undefined)
        {
            inc_val = ctx.ToNumber(iv);
        }

        string? mv_str = null;
        if (TryGetField(ctx, h, opt, "roundingMode", out var mv) && mv.Tag != JsValueTag.Undefined)
        {
            mv_str = mv.Tag == JsValueTag.String ? mv.AsString() : ctx.ToStringValue(mv);
        }

        string? sv_str = null;
        if (TryGetField(ctx, h, opt, "smallestUnit", out var sv) && sv.Tag != JsValueTag.Undefined)
        {
            sv_str = sv.Tag == JsValueTag.String ? sv.AsString() : ctx.ToStringValue(sv);
        }

        // 2. Validate values in spec order
        if (lv_str is not null)
        {
            if (lv_str != "auto")
            {
                var norm = TryNormalizeUnit(lv_str);
                if (norm is null || Array.IndexOf(allowed, norm) < 0)
                    throw new JsThrownException(ctx.CreateRangeError($"'{lv_str}' is not a valid value for largestUnit."));
                s.Largest = norm;
            }
            else
            {
                s.Largest = "auto";
            }
        }

        if (inc_val is not null)
        {
            double inc = inc_val.Value;
            if (double.IsNaN(inc) || double.IsInfinity(inc))
                throw new JsThrownException(ctx.CreateRangeError("roundingIncrement must be a finite number."));
            double trunc = Math.Truncate(inc);
            if (trunc < 1 || trunc > 1_000_000_000)
                throw new JsThrownException(ctx.CreateRangeError("roundingIncrement out of range."));
            s.Increment = (int)trunc;
        }

        if (mv_str is not null)
        {
            if (Array.IndexOf(ValidRoundingModes.Split(' '), mv_str) < 0)
                throw new JsThrownException(ctx.CreateRangeError($"'{mv_str}' is not a valid rounding mode."));
            s.Mode = mv_str;
        }

        if (sv_str is not null)
        {
            var norm = TryNormalizeUnit(sv_str);
            if (norm is null || Array.IndexOf(allowed, norm) < 0)
                throw new JsThrownException(ctx.CreateRangeError($"'{sv_str}' is not a valid value for smallestUnit."));
            s.Smallest = norm;
        }

        // 5/6. Resolve "auto" largestUnit to the larger of defaultLargest and smallestUnit.
        if (s.Largest == "auto")
            s.Largest = DiffUnitRank(defaultLargest) <= DiffUnitRank(s.Smallest) ? defaultLargest : s.Smallest;

        // 7. largestUnit must not be smaller than smallestUnit.
        if (DiffUnitRank(s.Largest) > DiffUnitRank(s.Smallest))
            throw new JsThrownException(ctx.CreateRangeError("largestUnit cannot be smaller than smallestUnit."));

        // 8/9. ValidateRoundingIncrement against the smallestUnit's exclusive maximum.
        long max = MaxDurationRoundingIncrement(s.Smallest);
        if (max != 0 && (s.Increment >= max || max % s.Increment != 0))
            throw new JsThrownException(ctx.CreateRangeError("roundingIncrement does not divide evenly into the maximum."));
        return s;
    }

    private struct RoundingOptions
    {
        public string Smallest;
        public double Increment;
        public string Mode;
    }

    private static RoundingOptions GetRoundingOptions(IBuiltinContext ctx, JsHeap h, IReadOnlyList<JsValue> a, string defaultMode = "halfExpand")
    {
        if (a.Count == 0 || a[0].Tag == JsValueTag.Undefined)
            throw new JsThrownException(ctx.CreateTypeError("round requires a unit or options argument."));
        var r = new RoundingOptions { Smallest = "", Increment = 1, Mode = defaultMode };
        if (a[0].Tag == JsValueTag.String)
        {
            r.Smallest = NormalizeUnitName(ctx, a[0].AsString());
            return r;
        }
        if (a[0].Tag != JsValueTag.Object)
        {
            throw new JsThrownException(ctx.CreateTypeError("round argument must be a string or options object."));
        }
        var opt = a[0];

        double? incVal = null;
        string? modeVal = null;
        string? smallestVal = null;

        // 1. Read and cast all properties first in alphabetical order
        if (TryGetField(ctx, h, opt, "roundingIncrement", out var iv) && iv.Tag != JsValueTag.Undefined)
        {
            incVal = ToIntegerWithTruncation(ctx, iv);
        }

        if (TryGetField(ctx, h, opt, "roundingMode", out var mv) && mv.Tag != JsValueTag.Undefined)
        {
            modeVal = mv.Tag == JsValueTag.String ? mv.AsString() : ctx.ToStringValue(mv);
        }

        if (TryGetField(ctx, h, opt, "smallestUnit", out var sv) && sv.Tag != JsValueTag.Undefined)
        {
            smallestVal = sv.Tag == JsValueTag.String ? sv.AsString() : ctx.ToStringValue(sv);
        }

        // 2. Validate all read properties second in alphabetical order
        if (incVal is not null)
        {
            if (incVal < 1 || incVal > 1_000_000_000)
                throw new JsThrownException(ctx.CreateRangeError("roundingIncrement out of range."));
            r.Increment = incVal.Value;
        }

        if (modeVal is not null)
        {
            if (modeVal is not ("ceil" or "floor" or "expand" or "trunc" or "halfCeil" or "halfFloor" or "halfExpand" or "halfTrunc" or "halfEven"))
                throw new JsThrownException(ctx.CreateRangeError($"'{modeVal}' is not a valid rounding mode."));
            r.Mode = modeVal;
        }

        if (smallestVal is null)
            throw new JsThrownException(ctx.CreateRangeError("smallestUnit is required."));
        r.Smallest = NormalizeUnitName(ctx, smallestVal);

        return r;
    }

    private static (IsoDate date, string calId, string? tz, System.Numerics.BigInteger? epochNs)? DecodeRelativeToValue(IBuiltinContext ctx, JsHeap h, JsValue relVal)
    {
        if (relVal.Tag == JsValueTag.Undefined) return null;
        if (relVal.Tag == JsValueTag.String)
        {
            var s = relVal.AsString();
            if (!TemporalIsoParser.TryParseDateTime(s, out var parsed, out var parseError))
                throw new JsThrownException(ctx.CreateRangeError($"'{s}' is not a valid ISO string: {parseError}"));
            if (parsed.TimeZoneAnnotation is not null)
            {
                var (epochNs, tz, cal) = ToTemporalZonedRecord(ctx, h, relVal);
                long epochNsClamped = epochNs >= long.MinValue && epochNs <= long.MaxValue
                    ? (long)epochNs
                    : (epochNs < 0 ? long.MinValue : long.MaxValue);
                long offsetNs = TemporalTimeZones.GetOffsetNs(tz, epochNsClamped);
                var (date, _) = TemporalTimeZones.WallFromEpochNsBig(epochNs, offsetNs);
                return (date, cal, tz, epochNs);
            }
            else
            {
                var (date, cal) = ToTemporalDateRecord(ctx, h, relVal);
                return (date, cal, null, null);
            }
        }
        else if (relVal.Tag == JsValueTag.Object)
        {
            var obj = h.GetObject(relVal.AsObjectHandle());
            if (TryGetInternalData(h, obj, out var data))
            {
                if (HasOwn(h, data, "tz"))
                {
                    var epochNs = DecodeInstantNanosBig(h, obj);
                    var tz = GetVStr(h, obj, "tz");
                    var cal = GetVStr(h, obj, "calendarId") is { Length: > 0 } c ? c : "iso8601";
                    long epochNsClamped = epochNs >= long.MinValue && epochNs <= long.MaxValue
                        ? (long)epochNs
                        : (epochNs < 0 ? long.MinValue : long.MaxValue);
                    long offsetNs = TemporalTimeZones.GetOffsetNs(tz, epochNsClamped);
                    var (date, _) = TemporalTimeZones.WallFromEpochNsBig(epochNs, offsetNs);
                    return (date, cal, tz, epochNs);
                }
                else if (HasOwn(h, data, "y") && HasOwn(h, data, "d"))
                {
                    var date = DecodeIsoDate(h, obj);
                    var cal = GetVStr(h, obj, "calendarId");
                    return (date, cal, null, null);
                }
                else if (HasOwn(h, data, "year") && HasOwn(h, data, "day"))
                {
                    var date = DecodeIsoDateLong(h, obj);
                    var cal = GetVStr(h, obj, "calendarId");
                    return (date, cal, null, null);
                }
                throw new JsThrownException(ctx.CreateTypeError("Invalid Temporal relativeTo object."));
            }
            else
            {
                // Property bag: read and coerce all fields in alphabetical order.
                // 1. calendar (read first)
                JsValue calendarVal = JsValue.Undefined;
                string bagCal = "iso8601";
                if (TryGetField(ctx, h, relVal, "calendar", out var calVal) && calVal.Tag != JsValueTag.Undefined)
                {
                    calendarVal = calVal;
                    bagCal = ToCalendarIdentifier(ctx, h, calVal);
                }

                // 2. alphabetical fields
                double? day = null;
                if (TryGetField(ctx, h, relVal, "day", out var dayVal) && dayVal.Tag != JsValueTag.Undefined)
                    day = ToIntegerWithTruncation(ctx, dayVal);

                double? hour = null;
                if (TryGetField(ctx, h, relVal, "hour", out var hourVal) && hourVal.Tag != JsValueTag.Undefined)
                    hour = ToIntegerWithTruncation(ctx, hourVal);

                double? microsecond = null;
                if (TryGetField(ctx, h, relVal, "microsecond", out var microsecondVal) && microsecondVal.Tag != JsValueTag.Undefined)
                    microsecond = ToIntegerWithTruncation(ctx, microsecondVal);

                double? millisecond = null;
                if (TryGetField(ctx, h, relVal, "millisecond", out var millisecondVal) && millisecondVal.Tag != JsValueTag.Undefined)
                    millisecond = ToIntegerWithTruncation(ctx, millisecondVal);

                double? minute = null;
                if (TryGetField(ctx, h, relVal, "minute", out var minuteVal) && minuteVal.Tag != JsValueTag.Undefined)
                    minute = ToIntegerWithTruncation(ctx, minuteVal);

                double? month = null;
                if (TryGetField(ctx, h, relVal, "month", out var monthVal) && monthVal.Tag != JsValueTag.Undefined)
                    month = ToIntegerWithTruncation(ctx, monthVal);

                string? monthCode = null;
                if (TryGetField(ctx, h, relVal, "monthCode", out var monthCodeVal) && monthCodeVal.Tag != JsValueTag.Undefined)
                    monthCode = ctx.ToStringValue(monthCodeVal);

                double? nanosecond = null;
                if (TryGetField(ctx, h, relVal, "nanosecond", out var nanosecondVal) && nanosecondVal.Tag != JsValueTag.Undefined)
                    nanosecond = ToIntegerWithTruncation(ctx, nanosecondVal);

                string? offset = null;
                if (TryGetField(ctx, h, relVal, "offset", out var offsetVal) && offsetVal.Tag != JsValueTag.Undefined)
                    offset = ctx.ToStringValue(offsetVal);

                double? second = null;
                if (TryGetField(ctx, h, relVal, "second", out var secondVal) && secondVal.Tag != JsValueTag.Undefined)
                    second = ToIntegerWithTruncation(ctx, secondVal);

                string? timeZone = null;
                if (TryGetField(ctx, h, relVal, "timeZone", out var timeZoneVal) && timeZoneVal.Tag != JsValueTag.Undefined)
                {
                    if (timeZoneVal.Tag != JsValueTag.String && timeZoneVal.Tag != JsValueTag.Object)
                        throw new JsThrownException(ctx.CreateTypeError("timeZone must be a string or object."));
                    timeZone = CanonicalizeTimeZoneId(ctx, ctx.ToStringValue(timeZoneVal));
                }

                double? year = null;
                if (TryGetField(ctx, h, relVal, "year", out var yearVal) && yearVal.Tag != JsValueTag.Undefined)
                    year = ToIntegerWithTruncation(ctx, yearVal);

                // Build clean JS object
                var cleanObj = new JsObject();
                cleanObj.SetPrototype(ctx.GetObjectPrototype());
                
                if (calendarVal.Tag != JsValueTag.Undefined)
                    cleanObj.SetProperty("calendar", calendarVal);
                if (day.HasValue)
                    cleanObj.SetProperty("day", JsValue.FromNumber(day.Value));
                if (hour.HasValue)
                    cleanObj.SetProperty("hour", JsValue.FromNumber(hour.Value));
                if (microsecond.HasValue)
                    cleanObj.SetProperty("microsecond", JsValue.FromNumber(microsecond.Value));
                if (millisecond.HasValue)
                    cleanObj.SetProperty("millisecond", JsValue.FromNumber(millisecond.Value));
                if (minute.HasValue)
                    cleanObj.SetProperty("minute", JsValue.FromNumber(minute.Value));
                if (month.HasValue)
                    cleanObj.SetProperty("month", JsValue.FromNumber(month.Value));
                if (monthCode != null)
                    cleanObj.SetProperty("monthCode", JsValue.FromString(monthCode));
                if (nanosecond.HasValue)
                    cleanObj.SetProperty("nanosecond", JsValue.FromNumber(nanosecond.Value));
                if (offset != null)
                    cleanObj.SetProperty("offset", JsValue.FromString(offset));
                if (second.HasValue)
                    cleanObj.SetProperty("second", JsValue.FromNumber(second.Value));
                if (timeZone != null)
                    cleanObj.SetProperty("timeZone", JsValue.FromString(timeZone));
                if (year.HasValue)
                    cleanObj.SetProperty("year", JsValue.FromNumber(year.Value));

                var cleanVal = h.AllocateObject(cleanObj, AllocationSite.Current());
                
                // Now proceed using the clean object
                bool isZoned = timeZone != null;
                if (isZoned)
                {
                    var (epochNs, tz, cal) = ToTemporalZonedRecord(ctx, h, JsValue.FromObject(cleanVal));
                    long epochNsClamped = epochNs >= long.MinValue && epochNs <= long.MaxValue
                        ? (long)epochNs
                        : (epochNs < 0 ? long.MinValue : long.MaxValue);
                    long offsetNs = TemporalTimeZones.GetOffsetNs(tz, epochNsClamped);
                    var (date, _) = TemporalTimeZones.WallFromEpochNsBig(epochNs, offsetNs);
                    return (date, cal, tz, epochNs);
                }
                else
                {
                    var (date, cal) = ToTemporalDateRecord(ctx, h, JsValue.FromObject(cleanVal));
                    return (date, cal, null, null);
                }
            }
        }
        throw new JsThrownException(ctx.CreateTypeError("relativeTo must be a string or object."));
    }

    private static System.Numerics.BigInteger AddDurationToZonedDateTime(
        IBuiltinContext ctx, JsHeap h,
        System.Numerics.BigInteger epochNsBig, string tz, string cal,
        (double years, double months, double weeks, double days, double hours, double minutes, double seconds, double millis, double micros, double nanos) dur,
        int sign)
    {
        System.Numerics.BigInteger resultNs = epochNsBig;
        if (dur.years != 0 || dur.months != 0 || dur.weeks != 0 || dur.days != 0)
        {
            long epochNsClamped = epochNsBig >= long.MinValue && epochNsBig <= long.MaxValue
                ? (long)epochNsBig
                : (epochNsBig < 0 ? long.MinValue : long.MaxValue);
            long offsetNs = TemporalTimeZones.GetOffsetNs(tz, epochNsClamped);
            var (date, time) = TemporalTimeZones.WallFromEpochNsBig(epochNsBig, offsetNs);
            
            var newDate = AddDateInCalendar(cal, date, sign * dur.years, sign * dur.months, sign * dur.weeks, sign * dur.days,
                constrain: true, out var invalid);
            if (invalid || !IsoMath.IsoDateWithinLimits(newDate))
                throw new JsThrownException(ctx.CreateRangeError("Resulting date is outside the supported range."));
                
            resultNs = TemporalTimeZones.EpochNsFromWallBig(tz, newDate, time);
        }
        
        System.Numerics.BigInteger timeNs = sign * DurationToNanos(0, dur.hours, dur.minutes, dur.seconds, dur.millis, dur.micros, dur.nanos);
        resultNs += timeNs;
        
        if (System.Numerics.BigInteger.Abs(resultNs) > MaxInstantNs)
            throw new JsThrownException(ctx.CreateRangeError("ZonedDateTime instant is outside the representable range."));
            
        return resultNs;
    }

    /// <summary>
    /// Round a signed nanosecond difference to the increment of smallestUnit, then balance the
    /// result into a Duration whose largest field is <paramref name="largest"/> (time units only).
    /// </summary>
    private static JsValue MakeDiffDuration(IBuiltinContext ctx, JsHeap h, long diffNs, DiffSettings s)
    {
        long unitNs = UnitNs(s.Smallest);
        long incNs = unitNs * s.Increment;
        if (incNs > 1) diffNs = (long)RoundNsToIncrement(ctx, diffNs, incNs, s.Mode);
        return MakeDurationFromNsBalanced(ctx, h, diffNs, s.Largest);
    }

    private static JsValue MakeDiffDuration(IBuiltinContext ctx, JsHeap h, System.Numerics.BigInteger diffNs, DiffSettings s)
    {
        System.Numerics.BigInteger unitNs = UnitNs(s.Smallest);
        System.Numerics.BigInteger incNs = unitNs * s.Increment;
        if (incNs > 1) diffNs = RoundNsToIncrement(ctx, diffNs, incNs, s.Mode);
        return MakeDurationBalancedNs(ctx, h, diffNs, s.Largest);
    }

    private static bool IsCalendarUnit(string? unit) => unit is "year" or "month" or "week";

    // Rank 0 = day (largest supported without relativeTo) … 6 = nanosecond.
    private static int UnitRank(string unit) => unit switch
    {
        "day" => 0, "hour" => 1, "minute" => 2, "second" => 3,
        "millisecond" => 4, "microsecond" => 5, _ => 6,
    };

    private static long UnitNs(string unit) => unit switch
    {
        "day" => NsPerDay, "hour" => 3_600_000_000_000L, "minute" => 60_000_000_000L,
        "second" => 1_000_000_000L, "millisecond" => 1_000_000L, "microsecond" => 1_000L, _ => 1L,
    };

    /// <summary>
    /// Temporal.Instant.prototype.round: validate smallestUnit (hour..nanosecond only),
    /// roundingIncrement (must divide its day-relative maximum), and roundingMode, then
    /// round the epoch nanoseconds to the increment.
    /// </summary>
    private static System.Numerics.BigInteger RoundInstantNs(IBuiltinContext ctx, JsHeap h, System.Numerics.BigInteger epochNs, IReadOnlyList<JsValue> a)
    {
        var opts = GetRoundingOptions(ctx, h, a);
        System.Numerics.BigInteger maximum = opts.Smallest switch
        {
            "hour" => 24,
            "minute" => 1440,
            "second" => 86_400,
            "millisecond" => 86_400_000,
            "microsecond" => 86_400_000_000,
            "nanosecond" => 86_400_000_000_000,
            _ => throw new JsThrownException(ctx.CreateRangeError($"'{opts.Smallest}' is not a valid smallestUnit for Instant.round.")),
        };
        var incBi = new System.Numerics.BigInteger(opts.Increment);
        if (incBi > maximum || maximum % incBi != 0)
            throw new JsThrownException(ctx.CreateRangeError("roundingIncrement does not divide evenly into the maximum."));

        var incrementNs = incBi * UnitNs(opts.Smallest);
        return RoundNsToIncrement(ctx, epochNs, incrementNs, opts.Mode);
    }

    private static long RoundPlainTimeNs(IBuiltinContext ctx, JsHeap h, long timeNs, IReadOnlyList<JsValue> a)
    {
        var opts = GetRoundingOptions(ctx, h, a);
        long maximum = opts.Smallest switch
        {
            "hour" => 24L,
            "minute" => 60L,
            "second" => 60L,
            "millisecond" => 1000L,
            "microsecond" => 1000L,
            "nanosecond" => 1000L,
            _ => throw new JsThrownException(ctx.CreateRangeError($"'{opts.Smallest}' is not a valid smallestUnit for PlainTime.round.")),
        };
        if (opts.Increment >= maximum || maximum % (long)opts.Increment != 0)
            throw new JsThrownException(ctx.CreateRangeError("roundingIncrement does not divide evenly into the maximum."));

        long incrementNs = (long)opts.Increment * UnitNs(opts.Smallest);
        return (long)RoundNsToIncrement(ctx, timeNs, incrementNs, opts.Mode);
    }

    private static (long DayCarry, long TimeNs) RoundPlainDateTimeTime(IBuiltinContext ctx, JsHeap h, long timeNs, IReadOnlyList<JsValue> a)
    {
        var opts = GetRoundingOptions(ctx, h, a);
        if (opts.Smallest == "day")
        {
            if (opts.Increment != 1)
                throw new JsThrownException(ctx.CreateRangeError("roundingIncrement must be 1 for day rounding."));
            long carry = (long)RoundNsToIncrement(ctx, timeNs, NsPerDay, opts.Mode) / NsPerDay;
            return (carry, 0L);
        }
        long maximum = opts.Smallest switch
        {
            "hour" => 24L,
            "minute" => 60L,
            "second" => 60L,
            "millisecond" => 1000L,
            "microsecond" => 1000L,
            "nanosecond" => 1000L,
            _ => throw new JsThrownException(ctx.CreateRangeError($"'{opts.Smallest}' is not a valid smallestUnit for PlainDateTime.round.")),
        };
        if (opts.Increment >= maximum || maximum % (long)opts.Increment != 0)
            throw new JsThrownException(ctx.CreateRangeError("roundingIncrement does not divide evenly into the maximum."));

        long rounded = (long)RoundNsToIncrement(ctx, timeNs, (long)opts.Increment * UnitNs(opts.Smallest), opts.Mode);
        long dayCarry = rounded / NsPerDay;
        long timeOfDay = rounded % NsPerDay;
        if (timeOfDay < 0) { timeOfDay += NsPerDay; dayCarry -= 1; }
        return (dayCarry, timeOfDay);
    }

    /// <summary>Total nanoseconds of the day/time portion (caller has excluded calendar units).</summary>
    private static System.Numerics.BigInteger DurationDayTimeNs((double years, double months, double weeks, double days, double hours, double minutes, double seconds, double millis, double micros, double nanos) d)
        => DurationToNanos(d.days, d.hours, d.minutes, d.seconds, d.millis, d.micros, d.nanos);

    /// <summary>DefaultTemporalLargestUnit for day/time durations.</summary>
    private static string DefaultLargestUnit((double years, double months, double weeks, double days, double hours, double minutes, double seconds, double millis, double micros, double nanos) d)
        => d.years != 0 ? "year" : d.months != 0 ? "month" : d.weeks != 0 ? "week"
            : d.days != 0 ? "day" : d.hours != 0 ? "hour" : d.minutes != 0 ? "minute" : d.seconds != 0 ? "second"
            : d.millis != 0 ? "millisecond" : d.micros != 0 ? "microsecond" : d.nanos != 0 ? "nanosecond" : "nanosecond";

    /// <summary>RoundNumberToIncrement over integer nanoseconds.</summary>
    private static System.Numerics.BigInteger RoundNsToIncrement(IBuiltinContext ctx, System.Numerics.BigInteger total, System.Numerics.BigInteger increment, string mode)
    {
        if (increment <= 0)
            throw new JsThrownException(ctx.CreateRangeError("roundingIncrement must be positive."));
        System.Numerics.BigInteger t = total / increment;
        System.Numerics.BigInteger r = total % increment;
        if (r == 0) return total;
        System.Numerics.BigInteger lower = r > 0 ? t : t - 1;
        System.Numerics.BigInteger upper = r > 0 ? t + 1 : t;
        System.Numerics.BigInteger absR2 = System.Numerics.BigInteger.Abs(r) * 2;
        System.Numerics.BigInteger nearer = absR2 < increment ? (r > 0 ? lower : upper) : (r > 0 ? upper : lower);
        System.Numerics.BigInteger result = mode switch
        {
            "ceil" => upper,
            "floor" => lower,
            "trunc" => t,
            "expand" => total > 0 ? upper : lower,
            "halfCeil" => absR2 == increment ? upper : nearer,
            "halfFloor" => absR2 == increment ? lower : nearer,
            "halfTrunc" => absR2 == increment ? t : nearer,
            "halfEven" => absR2 == increment ? (System.Numerics.BigInteger.Abs(lower) % 2 == 0 ? lower : upper) : nearer,
            _ => absR2 == increment ? (total > 0 ? upper : lower) : nearer, // halfExpand (default)
        };
        return result * increment;
    }

    /// <summary>Round a calendar-unit value (years or months) to an increment,
    /// carrying the remainder into the next-smaller unit.</summary>
    private static long RoundToIncrement(long value, long nextSmaller, long nextInOne, long increment, string mode,
        out long overflow, out bool didExpand)
    {
        overflow = 0; didExpand = false;
        if (increment <= 0) return value;
        long t = value / increment;
        long r = value % increment;
        // When there is a fractional part (nextSmaller != 0), treat exact
        // division as having a tiny remainder so ceil/floor react correctly.
        bool hasFraction = nextSmaller != 0;
        if (r == 0 && !hasFraction) return value;
        if (r == 0 && hasFraction) r = nextSmaller > 0 ? 1 : -1;
        long lower = r > 0 ? t : t - 1;
        long upper = r > 0 ? t + 1 : t;
        long absR2 = Math.Abs(r) * 2;
        long nearer = absR2 < increment ? (r > 0 ? lower : upper) : (r > 0 ? upper : lower);
        long result = mode switch
        {
            "ceil" => upper,
            "floor" => lower,
            "trunc" => t,
            "expand" => value > 0 ? upper : lower,
            "halfCeil" => absR2 == increment ? upper : nearer,
            "halfFloor" => absR2 == increment ? lower : nearer,
            "halfTrunc" => absR2 == increment ? t : nearer,
            "halfEven" => absR2 == increment ? (lower % 2 == 0 ? lower : upper) : nearer,
            _ => absR2 == increment ? (value > 0 ? upper : lower) : nearer, // halfExpand
        };
        didExpand = result != t;
        if (didExpand && result > t && nextSmaller > 0)
            overflow = -(nextInOne - nextSmaller);
        else if (didExpand && result < t && nextSmaller > 0)
            overflow = nextSmaller;
        return result * increment;
    }

    // ─── toString options (precision / calendarName / offset / timeZoneName) ───

    private sealed class ToStringOptions
    {
        public int FractionalDigits = -1;          // -1 = auto
        public string? SmallestUnit;               // minute..nanosecond, wins over digits
        public string RoundingMode = "trunc";
        public string CalendarName = "auto";       // auto|always|never|critical
        public string ShowOffset = "auto";         // auto|never
        public string TimeZoneName = "auto";       // auto|never|critical
        public string? TimeZoneValue;              // Added for Instant.prototype.toString
    }

    /// <summary>Read toString options in alphabetical property order, each validated.</summary>
    private static ToStringOptions GetToStringOptions(IBuiltinContext ctx, JsHeap h, IReadOnlyList<JsValue> a, int i, string[] keys)
    {
        RequireOptionsObject(ctx, a, i);
        var r = new ToStringOptions();
        if (i >= a.Count || a[i].Tag != JsValueTag.Object) return r;
        var optionsValue = a[i];
        var obj = h.GetObject(optionsValue.AsObjectHandle());

        // 1. Read and cast all properties first in the order specified by keys
        string? cn = null;
        string? fdStr = null;
        double? fdNum = null;
        string? ofv = null;
        string? rm = null;
        string? su = null;
        string? tzv = null;
        string? tzn = null;

        foreach (var key in keys)
        {
            switch (key)
            {
                case "calendarName":
                    if (ctx.TryGetPropertyValue(obj, optionsValue, "calendarName", out var cnVal) && cnVal.Tag != JsValueTag.Undefined)
                    {
                        cn = ctx.ToStringValue(cnVal);
                    }
                    break;
                case "fractionalSecondDigits":
                    if (ctx.TryGetPropertyValue(obj, optionsValue, "fractionalSecondDigits", out var fdVal) && fdVal.Tag != JsValueTag.Undefined)
                    {
                        if (fdVal.Tag is JsValueTag.Number or JsValueTag.Int32)
                        {
                            double n = fdVal.AsNumber();
                            if (double.IsNaN(n))
                                throw new JsThrownException(ctx.CreateRangeError("fractionalSecondDigits cannot be NaN."));
                            if (double.IsInfinity(n))
                                throw new JsThrownException(ctx.CreateRangeError("fractionalSecondDigits cannot be infinity."));
                            fdNum = Math.Floor(n);
                        }
                        else
                        {
                            fdStr = ctx.ToStringValue(fdVal);
                        }
                    }
                    break;
                case "offset":
                    if (ctx.TryGetPropertyValue(obj, optionsValue, "offset", out var ofvVal) && ofvVal.Tag != JsValueTag.Undefined)
                    {
                        ofv = ctx.ToStringValue(ofvVal);
                    }
                    break;
                case "roundingMode":
                    if (ctx.TryGetPropertyValue(obj, optionsValue, "roundingMode", out var rmVal) && rmVal.Tag != JsValueTag.Undefined)
                    {
                        rm = ctx.ToStringValue(rmVal);
                    }
                    break;
                case "smallestUnit":
                    if (ctx.TryGetPropertyValue(obj, optionsValue, "smallestUnit", out var suVal) && suVal.Tag != JsValueTag.Undefined)
                    {
                        su = ctx.ToStringValue(suVal);
                    }
                    break;
                case "timeZone":
                    if (ctx.TryGetPropertyValue(obj, optionsValue, "timeZone", out var tzvVal) && tzvVal.Tag != JsValueTag.Undefined)
                    {
                        tzv = ctx.ToStringValue(tzvVal);
                    }
                    break;
                case "timeZoneName":
                    if (ctx.TryGetPropertyValue(obj, optionsValue, "timeZoneName", out var tznVal) && tznVal.Tag != JsValueTag.Undefined)
                    {
                        tzn = ctx.ToStringValue(tznVal);
                    }
                    break;
            }
        }

        // 2. Validate all read properties second in the order of keys
        foreach (var key in keys)
        {
            switch (key)
            {
                case "calendarName":
                    if (cn is not null)
                    {
                        if (cn is not ("auto" or "always" or "never" or "critical"))
                            throw new JsThrownException(ctx.CreateRangeError($"'{cn}' is not a valid value for calendarName."));
                        r.CalendarName = cn;
                    }
                    break;
                case "fractionalSecondDigits":
                    if (fdNum is not null)
                    {
                        if (fdNum < 0 || fdNum > 9)
                            throw new JsThrownException(ctx.CreateRangeError("fractionalSecondDigits is out of range."));
                        r.FractionalDigits = (int)fdNum;
                    }
                    else if (fdStr is not null)
                    {
                        if (fdStr != "auto")
                            throw new JsThrownException(ctx.CreateRangeError($"'{fdStr}' is not a valid value for fractionalSecondDigits."));
                        r.FractionalDigits = -1;
                    }
                    break;
                case "offset":
                    if (ofv is not null)
                    {
                        if (ofv is not ("auto" or "never"))
                            throw new JsThrownException(ctx.CreateRangeError($"'{ofv}' is not a valid value for offset."));
                        r.ShowOffset = ofv;
                    }
                    break;
                case "roundingMode":
                    if (rm is not null)
                    {
                        if (rm is not ("ceil" or "floor" or "expand" or "trunc" or "halfCeil" or "halfFloor" or "halfExpand" or "halfTrunc" or "halfEven"))
                            throw new JsThrownException(ctx.CreateRangeError($"'{rm}' is not a valid rounding mode."));
                        r.RoundingMode = rm;
                    }
                    break;
                case "smallestUnit":
                    if (su is not null)
                    {
                        var s = NormalizeUnitName(ctx, su);
                        if (s is not ("minute" or "second" or "millisecond" or "microsecond" or "nanosecond"))
                            throw new JsThrownException(ctx.CreateRangeError($"'{s}' is not a valid smallestUnit for toString."));
                        r.SmallestUnit = s;
                    }
                    break;
                case "timeZone":
                    if (tzv is not null)
                    {
                        r.TimeZoneValue = tzv;
                    }
                    break;
                case "timeZoneName":
                    if (tzn is not null)
                    {
                        if (tzn is not ("auto" or "never" or "critical"))
                            throw new JsThrownException(ctx.CreateRangeError($"'{tzn}' is not a valid value for timeZoneName."));
                        r.TimeZoneName = tzn;
                    }
                    break;
            }
        }

        return r;
    }

    /// <summary>ToSecondsStringPrecision rounding increment in ns (smallestUnit wins over digits).</summary>
    private static long PrecisionIncrementNs(ToStringOptions o)
        => o.SmallestUnit is { } u ? UnitNs(u)
            : o.FractionalDigits < 0 ? 1L : (long)Math.Pow(10, 9 - o.FractionalDigits);

    /// <summary>":SS[.fff…]" seconds part per the precision options ("" at minute precision).</summary>
    private static string FormatSecondsPart(long timeOfDayNs, ToStringOptions o)
    {
        if (o.SmallestUnit == "minute") return "";
        long seconds = timeOfDayNs / 1_000_000_000L % 60;
        long frac = timeOfDayNs % 1_000_000_000L;
        int digits = o.SmallestUnit switch
        {
            "second" => 0,
            "millisecond" => 3,
            "microsecond" => 6,
            "nanosecond" => 9,
            _ => o.FractionalDigits,
        };
        string fracStr = digits switch
        {
            < 0 => frac == 0 ? "" : $".{frac:D9}".TrimEnd('0'),
            0 => "",
            _ => "." + $"{frac:D9}"[..digits],
        };
        return $":{seconds:D2}{fracStr}";
    }

    /// <summary>Calendar annotation per the calendarName option.</summary>
    private static string CalendarSuffix(JsHeap h, JsObject o, ToStringOptions opts)
    {
        var cid = GetVStr(h, o, "calendarId");
        if (string.IsNullOrEmpty(cid)) cid = "iso8601";
        return opts.CalendarName switch
        {
            "never" => "",
            "always" => $"[u-ca={cid}]",
            "critical" => $"[!u-ca={cid}]",
            _ => cid == "iso8601" ? "" : $"[u-ca={cid}]",
        };
    }

    /// <summary>Balance signed nanoseconds into a Duration capped at largestUnit.</summary>
    private static JsValue MakeDurationBalancedNs(IBuiltinContext ctx, JsHeap h, System.Numerics.BigInteger ns, string largestUnit)
    {
        int sign = ns < 0 ? -1 : 1;
        System.Numerics.BigInteger n = System.Numerics.BigInteger.Abs(ns);
        int rank = UnitRank(largestUnit);
        System.Numerics.BigInteger days = 0, hr = 0, mi = 0, se = 0, ms = 0, us = 0;
        if (rank <= 0) { days = n / NsPerDay; n %= NsPerDay; }
        if (rank <= 1) { hr = n / 3_600_000_000_000L; n %= 3_600_000_000_000L; }
        if (rank <= 2) { mi = n / 60_000_000_000L; n %= 60_000_000_000L; }
        if (rank <= 3) { se = n / 1_000_000_000L; n %= 1_000_000_000L; }
        if (rank <= 4) { ms = n / 1_000_000L; n %= 1_000_000L; }
        if (rank <= 5) { us = n / 1_000L; n %= 1_000L; }
        // ECMA-262 §7.5.28 IsValidDuration: each component must be < 2^53,
        // and the normalized total in seconds must also be < 2^53.
        var maxVal = new System.Numerics.BigInteger(9_007_199_254_740_991L); // 2^53 - 1
        if (days > maxVal || hr > maxVal || mi > maxVal || se > maxVal ||
            ms > maxVal || us > maxVal || n > maxVal)
            throw new JsThrownException(ctx.CreateRangeError("Duration value is outside the supported range."));
        // Normalized total in seconds (days × 86400 + hours × 3600 + ...).
        var totalSeconds = days * 86400 + hr * 3600 + mi * 60 + se;
        if (totalSeconds > maxVal)
            throw new JsThrownException(ctx.CreateRangeError("Duration value is outside the supported range."));

        return MakeDuration(ctx, h, 0, 0, 0, sign * (double)days, sign * (double)hr, sign * (double)mi, sign * (double)se,
            sign * (double)ms, sign * (double)us, sign * (double)n);
    }

    /// <summary>AddDurationToDateTime: time-of-day arithmetic with day carry, then AddISODate.</summary>
    private static JsValue AddDurationToPlainDateTime(IBuiltinContext ctx, JsHeap h, JsObject t, JsObject o,
        IReadOnlyList<JsValue> a, ObjectHandle pH, int sign)
    {
        var dur = ToTemporalDurationRecord(ctx, h, a.Count > 0 ? a[0] : JsValue.Undefined);
        bool constrain = GetOverflowOption(ctx, h, a, 1) == "constrain";
        var date = DecodeIsoDateLong(h, o);
        var timeNsBig = DecodeTimeOfDayNs(h, o)
            + sign * DurationToNanos(0, dur.hours, dur.minutes, dur.seconds, dur.millis, dur.micros, dur.nanos);
        // Use BigInteger for day-carry to avoid overflow with large sub-second values.
        // Must use floor division (toward −∞), not truncation (DivRem truncates toward zero).
        var dayNs = new System.Numerics.BigInteger(NsPerDay);
        var dayCarryBi = System.Numerics.BigInteger.DivRem(timeNsBig, dayNs, out var timeOfDayBig);
        if (timeOfDayBig < 0) { dayCarryBi -= 1; timeOfDayBig += dayNs; }
        long dayCarry = (long)dayCarryBi;
        long timeNs = (long)timeOfDayBig;
        var result = AddDateInCalendar(CalId(h, o), date, sign * dur.years, sign * dur.months, sign * dur.weeks,
            sign * (double)dur.days + dayCarry, constrain, out var invalid);
        if (invalid || !IsoMath.IsoDateWithinLimits(result))
            throw new JsThrownException(ctx.CreateRangeError("Resulting date is outside the supported range."));
        return AttachPrototype(h, MakePlainDateTimeParts(ctx, h, result.Year, result.Month, result.Day,
            (int)(timeNs / 3_600_000_000_000L), (int)(timeNs / 60_000_000_000L % 60), (int)(timeNs / 1_000_000_000L % 60),
            (int)(timeNs / 1_000_000L % 1000), (int)(timeNs / 1_000L % 1000), (int)(timeNs % 1000),
            GetVStr(h, o, "calendarId")), pH);
    }

    /// <summary>IsValidTime check → RangeError (all components integral and in range).</summary>
    private static void ValidateTime(IBuiltinContext ctx, double hr, double mi, double se, double ms, double us, double ns)
    {
        if (hr is < 0 or > 23 || mi is < 0 or > 59 || se is < 0 or > 59
            || ms is < 0 or > 999 || us is < 0 or > 999 || ns is < 0 or > 999)
            throw new JsThrownException(ctx.CreateRangeError("Invalid time component."));
    }

    /// <summary>Decode a PlainDate's stored fields as an IsoDate (no DateTime clamping).</summary>
    private static IsoDate DecodeIsoDate(JsHeap h, JsObject o)
    {
        int y = (int)GetVNum(h, o, "y");
        int m = Math.Max(1, (int)GetVNum(h, o, "m"));
        int d = Math.Max(1, (int)GetVNum(h, o, "d"));
        return new IsoDate(y, Math.Min(m, 12), Math.Min(d, IsoMath.DaysInMonth(y, Math.Min(m, 12))));
    }

    /// <summary>Decode a PlainDateTime/ZonedDateTime's stored date fields as an IsoDate.</summary>
    private static IsoDate DecodeIsoDateLong(JsHeap h, JsObject o)
    {
        int y = (int)GetVNum(h, o, "year");
        int m = Math.Max(1, (int)GetVNum(h, o, "month"));
        int d = Math.Max(1, (int)GetVNum(h, o, "day"));
        return new IsoDate(y, Math.Min(m, 12), Math.Min(d, IsoMath.DaysInMonth(y, Math.Min(m, 12))));
    }

    /// <summary>Format an ISO year: 4 digits, or signed 6 digits outside 0000-9999.</summary>
    private static string FormatIsoYear(int y)
        => y is >= 0 and <= 9999 ? y.ToString("D4") : (y < 0 ? "-" : "+") + Math.Abs(y).ToString("D6");

    /// <summary>Calendar annotation suffix for toString ([u-ca=x] for non-ISO calendars).</summary>
    private static string CalendarSuffix(JsHeap h, JsObject o)
    {
        var cid = GetVStr(h, o, "calendarId");
        return string.IsNullOrEmpty(cid) || cid == "iso8601" ? "" : $"[u-ca={cid}]";
    }

    // ─── Constructor factories ──────────────────────────────

    private static JsValue ConstructPlainDate(IBuiltinContext ctx, JsHeap h, IReadOnlyList<JsValue> args)
    {
        // new Temporal.PlainDate(year, month, day [, calendar]) — numeric args
        // via ToIntegerWithTruncation, then IsValidISODate/limits reject.
        double y = ArgInt(ctx, args, 0);
        double m = ArgInt(ctx, args, 1);
        double d = ArgInt(ctx, args, 2);
        string cal = CalendarArg(ctx, args, 3);
        return MakePlainDateRegulated(ctx, h, y, m, d, cal, "reject");
    }

    private static readonly System.Numerics.BigInteger MaxInstantNs =
        System.Numerics.BigInteger.Parse("8640000000000000000000"); // nsMaxInstant = 8.64e21

    private static JsValue ConstructInstant(IBuiltinContext ctx, JsHeap h, IReadOnlyList<JsValue> args)
    {
        // new Temporal.Instant(epochNanoseconds) — exactly one BigInt argument.
        if (args.Count == 0 || args[0].Tag != JsValueTag.BigInt)
            throw new JsThrownException(ctx.CreateTypeError("Instant constructor requires a BigInt epochNanoseconds argument."));
        var ns = args[0].AsBigInt();
        if (System.Numerics.BigInteger.Abs(ns) > MaxInstantNs)
            throw new JsThrownException(ctx.CreateRangeError("Instant is outside the supported range."));
        return MakeInstantFromNanoseconds(h, ToSafeLong(ns));
    }

    /// <summary>CreateTemporalInstant from a parsed ISO string (offset/Z applied), range-checked.</summary>
    private static JsValue MakeInstantFromParsed(IBuiltinContext ctx, JsHeap h, ParsedIsoString p)
    {
        var epochDays = IsoMath.CivilToEpochDays(p.Year, p.Month, p.Day);
        var ns = new System.Numerics.BigInteger(epochDays) * 86_400_000_000_000L
                 + p.Time.ToNanosecondsOfDay()
                 - (p.HasUtcDesignator ? 0L : p.OffsetNanoseconds);
        if (System.Numerics.BigInteger.Abs(ns) > MaxInstantNs)
            throw new JsThrownException(ctx.CreateRangeError("Instant is outside the supported range."));
        return MakeInstantFromNanoseconds(h, ToSafeLong(ns));
    }

    private static JsValue ConstructPlainTime(IBuiltinContext ctx, JsHeap h, IReadOnlyList<JsValue> args)
    {
        // new Temporal.PlainTime(h?, m?, s?, ms?, µs?, ns?) — each component
        // defaults to 0, ToIntegerWithTruncation, then IsValidTime reject.
        double hr = ArgIntOr(ctx, args, 0, 0);
        double mi = ArgIntOr(ctx, args, 1, 0);
        double se = ArgIntOr(ctx, args, 2, 0);
        double ms = ArgIntOr(ctx, args, 3, 0);
        double us = ArgIntOr(ctx, args, 4, 0);
        double ns = ArgIntOr(ctx, args, 5, 0);
        ValidateTime(ctx, hr, mi, se, ms, us, ns);
        return MakePlainTime(ctx, h, (int)hr, (int)mi, (int)se, (int)ms, (int)us, (int)ns);
    }

    private static JsValue ConstructPlainDateTime(IBuiltinContext ctx, JsHeap h, IReadOnlyList<JsValue> args)
    {
        // new Temporal.PlainDateTime(y, mo, d, h?, mi?, s?, ms?, µs?, ns? [, calendar])
        double y = ArgInt(ctx, args, 0);
        double mo = ArgInt(ctx, args, 1);
        double d = ArgInt(ctx, args, 2);
        double hr = ArgIntOr(ctx, args, 3, 0);
        double mi = ArgIntOr(ctx, args, 4, 0);
        double se = ArgIntOr(ctx, args, 5, 0);
        double ms = ArgIntOr(ctx, args, 6, 0);
        double us = ArgIntOr(ctx, args, 7, 0);
        double ns = ArgIntOr(ctx, args, 8, 0);
        string cal = CalendarArg(ctx, args, 9);
        if (y is < -999_999 or > 999_999 || mo is < 1 or > 12 || d is < 1 or > 31
            || !IsoMath.IsValidIsoDate((int)y, (int)mo, (int)d)
            || !IsoMath.IsoDateWithinLimits(new IsoDate((int)y, (int)mo, (int)d)))
            throw new JsThrownException(ctx.CreateRangeError("Invalid ISO date."));
        ValidateTime(ctx, hr, mi, se, ms, us, ns);
        return MakePlainDateTimeParts(ctx, h, (int)y, (int)mo, (int)d, (int)hr, (int)mi, (int)se, (int)ms, (int)us, (int)ns, cal);
    }

    private static JsValue ConstructDuration(IBuiltinContext ctx, JsHeap h, IReadOnlyList<JsValue> args)
    {
        // new Temporal.Duration(y?, mo?, w?, d?, h?, mi?, s?, ms?, µs?, ns?) —
        // ToIntegerIfIntegral each component, then IsValidDuration sign check.
        // Store as doubles to avoid int32 clamping (spec max is ~2^53 equivalent seconds).
        var v = new double[10];
        for (int i = 0; i < 10; i++)
            v[i] = ToIntegerIfIntegral(ctx, i < args.Count ? args[i] : JsValue.Undefined);
        ValidateDuration(ctx, v);
        return MakeDuration(ctx, h, v[0], v[1], v[2], v[3], v[4], v[5], v[6], v[7], v[8], v[9]);
    }

    /// <summary>Temporal.Duration.from(arg) — handles string, Duration object (copy), and property bag.</summary>
    private static JsValue DurationFrom(IBuiltinContext ctx, JsHeap h, IReadOnlyList<JsValue> args, ObjectHandle protoH)
    {
        if (args.Count == 0) throw new JsThrownException(ctx.CreateTypeError("Duration.from requires at least 1 argument."));
        var arg = args[0];

        if (arg.Tag == JsValueTag.String)
        {
            var s = arg.AsString();
            if (ParseIsoDuration(s, out var y, out var mo, out var w, out var d,
                    out var hr, out var mi, out var sec, out var ms, out var us, out var ns))
            {
                var values = new double[] { y, mo, w, d, hr, mi, sec, ms, us, ns };
                ValidateDuration(ctx, values);
                return AttachPrototype(h, MakeDuration(ctx, h, y, mo, w, d, hr, mi, sec, ms, us, ns), protoH);
            }
            throw new JsThrownException(ctx.CreateRangeError($"Invalid duration string: {s}"));
        }

        if (arg.Tag == JsValueTag.Object)
        {
            var obj = h.GetObject(arg.AsObjectHandle());
            // If it's already a Duration instance, copy its fields
            if (IsTemporalInstance(h, arg, protoH))
            {
                var dur = DecodeDuration(h, obj);
                return AttachPrototype(h, MakeDuration(ctx, h, dur.years, dur.months, dur.weeks, dur.days,
                    dur.hours, dur.minutes, dur.seconds, dur.millis, dur.micros, dur.nanos), protoH);
            }
            // Property bag with ToIntegerIfIntegral + sign validation.
            var r = ToTemporalDurationRecord(ctx, h, arg);
            return AttachPrototype(h, MakeDuration(ctx, h, r.years, r.months, r.weeks, r.days,
                r.hours, r.minutes, r.seconds, r.millis, r.micros, r.nanos), protoH);
        }

        throw new JsThrownException(ctx.CreateTypeError("Duration.from: argument must be a string, Duration, or property bag."));
    }

    /// <summary>Parse an ISO 8601 duration string like "P1Y2M3DT4H5M6S".</summary>
    private static bool ParseIsoDuration(string s, out double years, out double months, out double weeks, out double days,
        out double hours, out double minutes, out double seconds, out double millis, out double micros, out double nanos)
    {
        years = months = weeks = days = hours = minutes = seconds = millis = 0.0;
        micros = nanos = 0.0;
        if (string.IsNullOrEmpty(s)) return false;
        if (!IsoDurationRegex.IsMatch(s)) return false;
        // Optional ASCII sign; duration designators are case-insensitive.
        double sign = 1.0;
        int start = 0;
        if (s[0] == '+') start = 1;
        else if (s[0] == '-') { sign = -1.0; start = 1; }
        if (start >= s.Length || (s[start] != 'P' && s[start] != 'p')) return false;
        s = s[(start + 1)..].ToUpperInvariant();
        if (s.Length == 0) return false;
        if (sign < 0)
        {
            // Parse the absolute value, then negate every component below.
            if (!ParseIsoDuration("P" + s, out years, out months, out weeks, out days,
                    out hours, out minutes, out seconds, out millis, out micros, out nanos))
                return false;
            years = -years; months = -months; weeks = -weeks; days = -days;
            hours = -hours; minutes = -minutes; seconds = -seconds;
            millis = -millis; micros = -micros; nanos = -nanos;
            return true;
        }

        double SafeDouble(string str) => double.TryParse(str, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0.0;

        var timeIdx = s.IndexOf('T');
        var datePart = timeIdx >= 0 ? s.Substring(0, timeIdx) : s;
        var timePart = timeIdx >= 0 ? s.Substring(timeIdx + 1) : "";
        // Parse date part
        var i = 0;
        while (i < datePart.Length)
        {
            var numStart = i;
            while (i < datePart.Length && char.IsDigit(datePart[i])) i++;
            if (i == numStart) return false;
            var num = SafeDouble(datePart.Substring(numStart, i - numStart));
            if (i >= datePart.Length) return false;
            switch (datePart[i])
            {
                case 'Y': years = num; break;
                case 'M': months = num; break;
                case 'W': weeks = num; break;
                case 'D': days = num; break;
                default: return false;
            }
            i++;
        }
        // Parse time part
        i = 0;
        while (i < timePart.Length)
        {
            int numStart = i;
            while (i < timePart.Length && (char.IsDigit(timePart[i]) || timePart[i] == '.' || timePart[i] == ',')) i++;
            if (i == numStart) return false;
            var numStr = timePart.Substring(numStart, i - numStart).Replace(',', '.'); // ISO 8601 allows comma as decimal
            if (i >= timePart.Length) return false;
            if (numStr.Contains('.'))
            {
                // Parse fractional duration using exact mathematical values per spec.
                // The whole part goes to the designator's unit, the fractional remainder
                // cascades through successively smaller units via truncation, with the
                // final unit (nanoseconds) rounded halfExpand.
                if (!decimal.TryParse(numStr, System.Globalization.NumberStyles.AllowDecimalPoint | System.Globalization.NumberStyles.AllowLeadingSign,
                        System.Globalization.CultureInfo.InvariantCulture, out var decVal))
                    return false;
                long whole = (long)Math.Truncate(decVal);
                decimal frac = decVal - whole;
                if (frac < 0) { frac = -frac; } // work with absolute fractional part
                switch (timePart[i])
                {
                    case 'H':
                        hours = (double)whole;
                        {
                            decimal mins = frac * 60m;
                            minutes = (double)Math.Truncate(mins);
                            decimal secs = (mins - Math.Truncate(mins)) * 60m;
                            seconds = (double)Math.Truncate(secs);
                            decimal msecs = (secs - Math.Truncate(secs)) * 1000m;
                            millis = (double)Math.Truncate(msecs);
                            decimal usecs = (msecs - Math.Truncate(msecs)) * 1000m;
                            micros = (double)Math.Truncate(usecs);
                            decimal nsecs = (usecs - Math.Truncate(usecs)) * 1000m;
                            nanos = (double)Math.Round(nsecs, MidpointRounding.ToEven);
                        }
                        break;
                    case 'M':
                        minutes = (double)whole;
                        {
                            decimal secs = frac * 60m;
                            seconds = (double)Math.Truncate(secs);
                            decimal msecs = (secs - Math.Truncate(secs)) * 1000m;
                            millis = (double)Math.Truncate(msecs);
                            decimal usecs = (msecs - Math.Truncate(msecs)) * 1000m;
                            micros = (double)Math.Truncate(usecs);
                            decimal nsecs = (usecs - Math.Truncate(usecs)) * 1000m;
                            nanos = (double)Math.Round(nsecs, MidpointRounding.ToEven);
                        }
                        break;
                    case 'S':
                        seconds = (double)whole;
                        {
                            decimal msecs = frac * 1000m;
                            millis = (double)Math.Truncate(msecs);
                            decimal usecs = (msecs - Math.Truncate(msecs)) * 1000m;
                            micros = (double)Math.Truncate(usecs);
                            decimal nsecs = (usecs - Math.Truncate(usecs)) * 1000m;
                            nanos = (double)Math.Round(nsecs, MidpointRounding.ToEven);
                        }
                        break;
                }
                i++;
            }
            else
            {
                var num = SafeDouble(numStr);
                switch (timePart[i])
                {
                    case 'H': hours = num; break;
                    case 'M': minutes = num; break;
                    case 'S': seconds = num; break;
                    default: return false;
                }
                i++;
            }
        }
        return true;
    }

    private static (ObjectHandle, ObjectHandle) MakeCtor(IBuiltinContext ctx, JsHeap h, JsObject parent, ObjectHandle pH,
        string name, int len, bool ctor, Func<IBuiltinContext, JsHeap, IReadOnlyList<JsValue>, JsValue>? constructFactory = null)
    {
        var proto = new JsObject();
        proto.SetPrototype(ctx.GetObjectPrototype());
        var protoH = h.AllocateObject(proto, AllocationSite.Current());
        h.PushRoot(protoH);

        NativeFunctionObject f;
        if (ctor)
        {
            f = new NativeFunctionObject(name,
                // Called as a plain function (NewTarget undefined): Temporal constructors require `new`.
                (_, _a) => throw new JsThrownException(ctx.CreateTypeError($"Temporal.{name} must be called with new.")),
                _a =>
                {
                    if (constructFactory != null)
                        return AttachPrototype(h, constructFactory(ctx, h, _a), protoH);
                    var o = new JsObject(); o.SetPrototype(protoH);
                    o.DefineOwnProperty("_v", new JsPropertyDescriptor(JsValue.Undefined, false, false, false));
                    return JsValue.FromObject(h.AllocateObject(o, AllocationSite.Current()));
                }, length: len);
            var fnCH = ctx.MaterializeFunctionConstructor();
            var fnC = h.GetObject(fnCH);
            if (ctx.TryGetPropertyValue(fnC, JsValue.FromObject(fnCH), "prototype", out var fp) && fp.Tag == JsValueTag.Object)
                f.SetPrototype(fp.AsObjectHandle());
        }
        else
            f = new NativeFunctionObject(name, (_, _a) =>
                throw new JsThrownException(ctx.CreateTypeError($"{name} is not a constructor.")), length: len);

        f.DefineOwnProperty("prototype", new JsPropertyDescriptor(JsValue.FromObject(protoH), false, false, false));
        var cH = h.AllocateObject(f, AllocationSite.Current()); h.PushRoot(cH); h.WriteBarrier(cH, protoH);
        proto.DefineOwnProperty("constructor", new JsPropertyDescriptor(JsValue.FromObject(cH), true, false, true));
        // <Type>.prototype[@@toStringTag] = "Temporal.<Type>" (non-writable, non-enumerable, configurable).
        var toStringTag = ctx.CreateWellKnownSymbol("toStringTag");
        proto.DefineOwnSymbolProperty(toStringTag.AsSymbolId(), new JsPropertyDescriptor(
            JsValue.FromString($"Temporal.{name}"), Writable: false, Enumerable: false, Configurable: true));
        h.WriteBarrier(protoH, cH);
        parent.DefineOwnProperty(name, new JsPropertyDescriptor(JsValue.FromObject(cH), true, false, true));
        h.WriteBarrier(pH, cH);
        return (cH, protoH);
    }

    private static JsValue AttachPrototype(JsHeap h, JsValue value, ObjectHandle protoHandle)
    {
        if (value.Tag == JsValueTag.Object)
        {
            h.GetObject(value.AsObjectHandle()).SetPrototype(protoHandle);
        }

        return value;
    }

    /// <summary>Check whether value is an instance of a Temporal type (its prototype chain contains protoH).</summary>
    private static bool IsTemporalInstance(JsHeap h, JsValue v, ObjectHandle protoH)
    {
        if (v.Tag != JsValueTag.Object) return false;
        var obj = h.GetObject(v.AsObjectHandle());
        for (int i = 0; i < 64; i++) // safety bound
        {
            var p = obj.PrototypeHandle;
            if (p is not { } ph) return false;
            if (ph.Equals(protoH)) return true;
            obj = h.GetObject(ph);
        }
        return false;
    }

    /// <summary>
    /// Built-in functions that are not constructors must have Function.prototype as their
    /// [[Prototype]] (ECMA-262 ch.17). Apply it to a freshly-created native function object.
    /// </summary>
    private static void ApplyFunctionPrototype(IBuiltinContext ctx, JsHeap h, NativeFunctionObject nf)
    {
        var fnCH = ctx.MaterializeFunctionConstructor();
        var fnC = h.GetObject(fnCH);
        if (ctx.TryGetPropertyValue(fnC, JsValue.FromObject(fnCH), "prototype", out var fp) && fp.Tag == JsValueTag.Object)
            nf.SetPrototype(fp.AsObjectHandle());
    }

    private static void AddGetter(IBuiltinContext ctx, JsHeap h, ObjectHandle pH, JsObject p, string n, Func<JsObject, JsValue> g)
    {
        var brandProto = pH;
        var gf = new NativeFunctionObject("get " + n, (tv, _) =>
            IsTemporalInstance(h, tv, brandProto)
                ? g(h.GetObject(tv.AsObjectHandle()))
                : throw new JsThrownException(ctx.CreateTypeError($"get {n}: receiver is not a valid Temporal instance.")), length: 0);
        ApplyFunctionPrototype(ctx, h, gf);
        var gH = h.AllocateObject(gf, AllocationSite.Current());
        p.DefineOwnProperty(n, JsPropertyDescriptor.Accessor(JsValue.FromObject(gH), JsValue.Undefined, Enumerable: false, Configurable: true));
        h.WriteBarrier(pH, gH);
    }

    private static void AddMethod(IBuiltinContext ctx, JsHeap h, ObjectHandle pH, JsObject p, string n,
        Func<JsObject, IReadOnlyList<JsValue>, JsValue> fn, int len = 1)
    {
        // Capture protoH for brand checking
        var brandProto = pH;
        var nf = new NativeFunctionObject(n, (tv, a) =>
        {
            if (!IsTemporalInstance(h, tv, brandProto))
                throw new JsThrownException(ctx.CreateTypeError($"{n}: receiver is not a valid Temporal instance."));
            return fn(h.GetObject(tv.AsObjectHandle()), a);
        }, length: len);
        ApplyFunctionPrototype(ctx, h, nf);
        var nfH = h.AllocateObject(nf, AllocationSite.Current());
        p.DefineOwnProperty(n, new JsPropertyDescriptor(JsValue.FromObject(nfH), true, false, true));
        h.WriteBarrier(pH, nfH);
    }

    private static void AddStatic(IBuiltinContext ctx, JsHeap h, ObjectHandle cH, JsObject c, string n,
        Func<IReadOnlyList<JsValue>, JsValue> fn, int len = 1)
    {
        var nf = new NativeFunctionObject(n, (_, a) => fn(a), length: len);
        // Static functions must have Function.prototype as their [[Prototype]]
        ApplyFunctionPrototype(ctx, h, nf);
        var nfH = h.AllocateObject(nf, AllocationSite.Current());
        c.DefineOwnProperty(n, new JsPropertyDescriptor(JsValue.FromObject(nfH), true, false, true));
        h.WriteBarrier(cH, nfH);
    }

    // Store a value on the internal _v data object
    private static void SetV(JsHeap h, JsObject o, string k, JsValue v)
    {
        if (o.TryGetProperty("_v", x => h.GetObject(x), out var dd) && dd.Value.Tag == JsValueTag.Object)
        {
            var data = h.GetObject(dd.Value.AsObjectHandle());
            data.SetProperty(k, v);
        }
    }

    private static JsValue GetV(JsHeap h, JsObject o, string k)
    {
        if (o.TryGetProperty("_v", x => h.GetObject(x), out var dd) && dd.Value.Tag == JsValueTag.Object)
        {
            var data = h.GetObject(dd.Value.AsObjectHandle());
            if (data.TryGetProperty(k, x => h.GetObject(x), out var vd)) return vd.Value;
        }
        return JsValue.Undefined;
    }

    /// <summary>Check if an object has an own property (not inherited, not undefined).</summary>
    private static bool HasOwn(JsHeap h, JsObject o, string name)
    {
        return o.TryGetProperty(name, x => h.GetObject(x), out var vd) && vd.Value.Tag != JsValueTag.Undefined;
    }

    /// <summary>Read a numeric own-property directly from a plain object (not from _v).</summary>
    private static double ReadOwnNum(JsHeap h, JsObject o, string name)
    {
        if (o.TryGetProperty(name, x => h.GetObject(x), out var vd))
            return vd.Value.AsNumber();
        return 0;
    }

    /// <summary>Read a string own-property directly from a plain object (not from _v).</summary>
    private static string ReadOwnStr(JsHeap h, JsObject o, string name)
    {
        if (o.TryGetProperty(name, x => h.GetObject(x), out var vd) && vd.Value.Tag == JsValueTag.String)
            return vd.Value.AsString();
        return "";
    }

    /// <summary>Convert a double to int safely, clamping to int range (prevents overflow crashes).</summary>
    private static int ToSafeInt(double v)
    {
        if (double.IsNaN(v) || double.IsInfinity(v)) return 0;
        if (v > int.MaxValue) return int.MaxValue;
        if (v < int.MinValue) return int.MinValue;
        return (int)v;
    }

    /// <summary>Convert a BigInteger to long safely, clamping to long range.</summary>
    private static long ToSafeLong(System.Numerics.BigInteger bi)
    {
        // Clamp the negative side to MinValue+1 so Math.Abs on the result can't overflow.
        if (bi > long.MaxValue) return long.MaxValue;
        if (bi <= long.MinValue) return long.MinValue + 1;
        return (long)bi;
    }

    private static JsObject MakeData(JsHeap h)
    {
        var d = new JsObject();
        h.AllocateObject(d, AllocationSite.Current());
        return d;
    }

    // ─── Temporal.Now ──────────────────────────────────────
    private void InstallNow(IBuiltinContext ctx, JsObject t, ObjectHandle tH, JsHeap h)
    {
        var now = new JsObject();
        now.SetPrototype(ctx.GetObjectPrototype());
        var nH = h.AllocateObject(now, AllocationSite.Current()); h.PushRoot(nH);
        t.DefineOwnProperty("Now", new JsPropertyDescriptor(JsValue.FromObject(nH), true, false, true));
        h.WriteBarrier(tH, nH);
        // Temporal.Now[@@toStringTag] = "Temporal.Now".
        var nowTag = ctx.CreateWellKnownSymbol("toStringTag");
        now.DefineOwnSymbolProperty(nowTag.AsSymbolId(), new JsPropertyDescriptor(
            JsValue.FromString("Temporal.Now"), Writable: false, Enumerable: false, Configurable: true));

        AddNowStatic(ctx, h, nH, now, "timeZoneId", _ => JsValue.FromString(CanonicalizeTimeZoneId(ctx, TimeZoneInfo.Local.Id)));
        AddNowStatic(ctx, h, nH, now, "instant", _ => AttachTemporalPrototypeByName(ctx, h, t, "Instant", MakeInstant(ctx, h, DateTime.UtcNow)));
        AddNowStatic(ctx, h, nH, now, "plainDateISO", a => {
            string tz = ResolveTimeZoneId(ctx, h, a.Count > 0 ? a[0] : JsValue.Undefined, TimeZoneInfo.Local.Id);
            long ns = (DateTime.UtcNow.Ticks - Epoch.Ticks) * 100L;
            long offsetNs = TemporalTimeZones.GetOffsetNs(tz, ns);
            (IsoDate date, IsoTime time) = TemporalTimeZones.WallFromEpochNs(ns, offsetNs);
            return AttachTemporalPrototypeByName(ctx, h, t, "PlainDate", MakePlainDateYmd(ctx, h, date.Year, date.Month, date.Day, "iso8601"));
        });
        AddNowStatic(ctx, h, nH, now, "plainTimeISO", a => {
            string tz = ResolveTimeZoneId(ctx, h, a.Count > 0 ? a[0] : JsValue.Undefined, TimeZoneInfo.Local.Id);
            long ns = (DateTime.UtcNow.Ticks - Epoch.Ticks) * 100L;
            long offsetNs = TemporalTimeZones.GetOffsetNs(tz, ns);
            (IsoDate date, IsoTime time) = TemporalTimeZones.WallFromEpochNs(ns, offsetNs);
            return AttachTemporalPrototypeByName(ctx, h, t, "PlainTime", MakePlainTime(ctx, h, time.Hour, time.Minute, time.Second, time.Millisecond, time.Microsecond, time.Nanosecond));
        });
        AddNowStatic(ctx, h, nH, now, "plainDateTimeISO", a => {
            string tz = ResolveTimeZoneId(ctx, h, a.Count > 0 ? a[0] : JsValue.Undefined, TimeZoneInfo.Local.Id);
            long ns = (DateTime.UtcNow.Ticks - Epoch.Ticks) * 100L;
            long offsetNs = TemporalTimeZones.GetOffsetNs(tz, ns);
            (IsoDate date, IsoTime time) = TemporalTimeZones.WallFromEpochNs(ns, offsetNs);
            return AttachTemporalPrototypeByName(ctx, h, t, "PlainDateTime", MakePlainDateTimeParts(ctx, h, date.Year, date.Month, date.Day, time.Hour, time.Minute, time.Second, time.Millisecond, time.Microsecond, time.Nanosecond, "iso8601"));
        });
        AddNowStatic(ctx, h, nH, now, "zonedDateTimeISO", a => {
            string tz = ResolveTimeZoneId(ctx, h, a.Count > 0 ? a[0] : JsValue.Undefined, TimeZoneInfo.Local.Id);
            long ns = (DateTime.UtcNow.Ticks - Epoch.Ticks) * 100L;
            return AttachTemporalPrototypeByName(ctx, h, t, "ZonedDateTime", MakeZonedDateTimeNs(ctx, h, ns, tz, "iso8601"));
        });
    }

    private static void AddNowStatic(IBuiltinContext ctx, JsHeap h, ObjectHandle oH, JsObject o, string n,
        Func<IReadOnlyList<JsValue>, JsValue> fn)
    {
        var nf = new NativeFunctionObject(n, (_, a) => fn(a), length: 0);
        ApplyFunctionPrototype(ctx, h, nf);
        var nfH = h.AllocateObject(nf, AllocationSite.Current());
        o.DefineOwnProperty(n, new JsPropertyDescriptor(JsValue.FromObject(nfH), true, false, true));
        h.WriteBarrier(oH, nfH);
    }

    private static string ResolveTimeZoneId(IBuiltinContext ctx, JsHeap h, JsValue arg, string defaultTz)
    {
        if (arg.Tag == JsValueTag.Undefined)
        {
            return CanonicalizeTimeZoneId(ctx, defaultTz);
        }

        if (arg.Tag == JsValueTag.Object)
        {
            var obj = h.GetObject(arg.AsObjectHandle());
            if (obj.TryGetOwnProperty("_v", out var vd) && vd.Value.Tag == JsValueTag.String)
            {
                return vd.Value.AsString();
            }
            if (TryGetInternalData(h, obj, out var data) && HasOwn(h, data, "tz"))
            {
                var tz = GetVStr(h, obj, "tz");
                return CanonicalizeTimeZoneId(ctx, string.IsNullOrEmpty(tz) ? "UTC" : tz);
            }
            if (TryGetField(ctx, h, arg, "timeZone", out var tzField) && tzField.Tag != JsValueTag.Undefined)
            {
                return ResolveTimeZoneId(ctx, h, tzField, defaultTz);
            }
            throw new JsThrownException(ctx.CreateTypeError("Invalid time zone object."));
        }

        if (arg.Tag != JsValueTag.String)
        {
            throw new JsThrownException(ctx.CreateTypeError("Time zone must be a string or object."));
        }

        var raw = arg.AsString();
        if (string.IsNullOrWhiteSpace(raw))
        {
            throw new JsThrownException(ctx.CreateRangeError("Invalid time zone string."));
        }

        if (TemporalTimeZones.TryCanonicalize(raw, out var canonical, out _))
        {
            return canonical;
        }

        if (TemporalIsoParser.TryParseDateTime(raw, out var parsed, out _))
        {
            if (parsed.TimeZoneAnnotation is { } ann)
            {
                if (TemporalTimeZones.TryCanonicalize(ann, out canonical, out _))
                    return canonical;
            }
            else if (parsed.HasUtcDesignator)
            {
                return "UTC";
            }
            else if (parsed.HasOffset)
            {
                if (parsed.OffsetSubMinuteSyntax)
                {
                    throw new JsThrownException(ctx.CreateRangeError("Time zone offset has sub-minute precision."));
                }
                return TemporalTimeZones.FormatOffset(parsed.OffsetNanoseconds);
            }
            else
            {
                throw new JsThrownException(ctx.CreateRangeError("Bare ISO date-time string cannot be used as time zone."));
            }
        }

        throw new JsThrownException(ctx.CreateRangeError($"'{raw}' is not a valid time zone."));
    }

    private static JsValue AttachTemporalPrototypeByName(IBuiltinContext ctx, JsHeap h, JsObject temporal, string ctorName, JsValue value)
    {
        if (value.Tag != JsValueTag.Object)
        {
            return value;
        }

        if (!temporal.TryGetProperty(ctorName, x => h.GetObject(x), out var ctorDescriptor) || ctorDescriptor.Value.Tag != JsValueTag.Object)
        {
            return value;
        }

        var ctor = h.GetObject(ctorDescriptor.Value.AsObjectHandle());
        if (!ctx.TryGetPropertyValue(ctor, ctorDescriptor.Value, "prototype", out var prototypeValue) || prototypeValue.Tag != JsValueTag.Object)
        {
            return value;
        }

        h.GetObject(value.AsObjectHandle()).SetPrototype(prototypeValue.AsObjectHandle());
        return value;
    }

    // ─── Temporal.Duration ─────────────────────────────────
    private void InstallDuration(IBuiltinContext ctx, JsObject t, ObjectHandle tH, JsHeap h)
    {
        var (cH, pH) = MakeCtor(ctx, h, t, tH, "Duration", 0, true,
            (cctx, hh, a) => ConstructDuration(cctx, hh, a));
        var p = h.GetObject(pH);
        foreach (var f in new[] { "years", "months", "weeks", "days", "hours", "minutes", "seconds", "milliseconds", "microseconds", "nanoseconds" })
            AddGetter(ctx, h, pH, p, f, o => GetV(h, o, f));
        AddGetter(ctx, h, pH, p, "sign", o => {
            double sum = 0;
            foreach (var f in new[]{"years","months","weeks","days","hours","minutes","seconds","milliseconds","microseconds","nanoseconds"})
                sum += GetVNum(h, o, f);
            return JsValue.FromNumber(sum > 0 ? 1 : sum < 0 ? -1 : 0);
        });
        AddGetter(ctx, h, pH, p, "blank", o => {
            foreach (var f in new[]{"years","months","weeks","days","hours","minutes","seconds","milliseconds","microseconds","nanoseconds"})
                if (GetVNum(h, o, f) != 0) return JsValue.FromBoolean(false);
            return JsValue.FromBoolean(true);
        });
        AddMethod(ctx, h, pH, p, "with", (o, a) => AttachPrototype(h, DurationWith(ctx, h, o, a), pH), 1);
        AddMethod(ctx, h, pH, p, "negated", (o, _) => {
            var d = DecodeDuration(h, o);
            return AttachPrototype(h, MakeDuration(ctx, h, -d.years, -d.months, -d.weeks, -d.days, -d.hours, -d.minutes, -d.seconds, -d.millis, -d.micros, -d.nanos), pH);
        }, 0);
        AddMethod(ctx, h, pH, p, "abs", (o, _) => {
            var d = DecodeDuration(h, o);
            return AttachPrototype(h, MakeDuration(ctx, h, Math.Abs(d.years), Math.Abs(d.months), Math.Abs(d.weeks), Math.Abs(d.days), Math.Abs(d.hours), Math.Abs(d.minutes), Math.Abs(d.seconds), Math.Abs(d.millis), Math.Abs(d.micros), Math.Abs(d.nanos)), pH);
        }, 0);
        AddMethod(ctx, h, pH, p, "add", (o, a) => {
            var self = DecodeDuration(h, o);
            var other = ToTemporalDurationRecord(ctx, h, a.Count > 0 ? a[0] : JsValue.Undefined);
            // AddDurations: calendar units are not allowed without relativeTo.
            if (self.years != 0 || self.months != 0 || self.weeks != 0
                || other.years != 0 || other.months != 0 || other.weeks != 0)
                throw new JsThrownException(ctx.CreateRangeError("Duration.add does not support calendar units."));
            string largest = UnitRank(DefaultLargestUnit(self)) <= UnitRank(DefaultLargestUnit(other))
                ? DefaultLargestUnit(self) : DefaultLargestUnit(other);
            return AttachPrototype(h, MakeDurationBalancedNs(ctx, h, DurationDayTimeNs(self) + DurationDayTimeNs(other), largest), pH);
        }, 1);
        AddMethod(ctx, h, pH, p, "subtract", (o, a) => {
            var self = DecodeDuration(h, o);
            var other = ToTemporalDurationRecord(ctx, h, a.Count > 0 ? a[0] : JsValue.Undefined);
            if (self.years != 0 || self.months != 0 || self.weeks != 0
                || other.years != 0 || other.months != 0 || other.weeks != 0)
                throw new JsThrownException(ctx.CreateRangeError("Duration.subtract does not support calendar units."));
            string largest = UnitRank(DefaultLargestUnit(self)) <= UnitRank(DefaultLargestUnit(other))
                ? DefaultLargestUnit(self) : DefaultLargestUnit(other);
            return AttachPrototype(h, MakeDurationBalancedNs(ctx, h, DurationDayTimeNs(self) - DurationDayTimeNs(other), largest), pH);
        }, 1);
        AddMethod(ctx, h, pH, p, "round", (o, a) => AttachPrototype(h, DurationRound(ctx, h, o, a), pH), 1);
        AddMethod(ctx, h, pH, p, "total", (o, a) => DurationTotal(ctx, h, o, a), 1);
        AddMethod(ctx, h, pH, p, "toString", (o, a) => FormatDurationOpts(ctx, h, o, a), 0);
        AddMethod(ctx, h, pH, p, "toJSON", (o, _) => FormatDuration(h, o), 0);
        AddMethod(ctx, h, pH, p, "valueOf", (_, _2) => throw new JsThrownException(ctx.CreateTypeError("Duration.prototype.valueOf throws.")), 0);
        var c = h.GetObject(cH);
        AddStatic(ctx, h, cH, c, "from", a => DurationFrom(ctx, h, a, pH), 1);
        AddStatic(ctx, h, cH, c, "compare", a => {
            var d1 = ToTemporalDurationRecord(ctx, h, a.Count > 0 ? a[0] : JsValue.Undefined);
            var d2 = ToTemporalDurationRecord(ctx, h, a.Count > 1 ? a[1] : JsValue.Undefined);
            if (a.Count > 2 && a[2].Tag != JsValueTag.Undefined)
                RequireOptionsObject(ctx, a, 2);
            var relTo = a.Count > 2 ? TryDecodeRelativeTo(ctx, h, a[2]) : null;
            if (d1.years == d2.years && d1.months == d2.months && d1.weeks == d2.weeks && d1.days == d2.days
                && d1.hours == d2.hours && d1.minutes == d2.minutes && d1.seconds == d2.seconds
                && d1.millis == d2.millis && d1.micros == d2.micros && d1.nanos == d2.nanos)
            {
                return JsValue.FromNumber(0);
            }
            bool calUnits = d1.years != 0 || d1.months != 0 || d1.weeks != 0
                || d2.years != 0 || d2.months != 0 || d2.weeks != 0;
            if (calUnits && relTo is null)
                throw new JsThrownException(ctx.CreateRangeError("relativeTo is required for calendar units"));

            if (relTo is not null && relTo.Value.tz is not null)
            {
                var epochNs = relTo.Value.epochNs!.Value;
                var tz = relTo.Value.tz;
                var cal = relTo.Value.calId;
                var afterA = AddDurationToZonedDateTime(ctx, h, epochNs, tz, cal, d1, 1);
                var afterB = AddDurationToZonedDateTime(ctx, h, epochNs, tz, cal, d2, 1);
                return JsValue.FromNumber(afterA < afterB ? -1 : afterA > afterB ? 1 : 0);
            }
            else
            {
                System.Numerics.BigInteger totalA, totalB;
                if (relTo is not null)
                {
                    totalA = DurationTotalNsWithRelative(ctx, d1, (relTo.Value.date, relTo.Value.calId));
                    totalB = DurationTotalNsWithRelative(ctx, d2, (relTo.Value.date, relTo.Value.calId));
                }
                else
                {
                    totalA = DurationDayTimeNs(d1);
                    totalB = DurationDayTimeNs(d2);
                }
                return JsValue.FromNumber(totalA < totalB ? -1 : totalA > totalB ? 1 : 0);
            }
        }, 2);
    }

    /// <summary>Convert a Duration to total nanoseconds using a relativeTo date for
    /// calendar units (years/months/weeks).</summary>
    private static System.Numerics.BigInteger DurationTotalNsWithRelative(
        IBuiltinContext ctx,
        (double years, double months, double weeks, double days, double hours, double minutes, double seconds, double millis, double micros, double nanos) dur,
        (IsoDate date, string calId) relTo)
    {
        if (dur.years == 0 && dur.months == 0 && dur.weeks == 0)
            return DurationDayTimeNs(dur);
        var sys = CalendarMath.Get(relTo.calId);
        sys ??= CalendarMath.Get("iso8601")!;
        bool invalid;
        var relDate = sys.Add(relTo.date, ToSafeInt(dur.years), ToSafeInt(dur.months), ToSafeInt(dur.weeks), 0, constrain: true, out invalid);
        if (invalid || !IsoMath.IsoDateWithinLimits(relDate))
            throw new JsThrownException(ctx.CreateRangeError("Duration out of range for this relativeTo."));
        System.Numerics.BigInteger dateDays = IsoMath.CivilToEpochDays(relDate.Year, relDate.Month, relDate.Day)
                        - IsoMath.CivilToEpochDays(relTo.date.Year, relTo.date.Month, relTo.date.Day);
        System.Numerics.BigInteger totalDays = dateDays + new System.Numerics.BigInteger(dur.days);
        System.Numerics.BigInteger timeNs = new System.Numerics.BigInteger(dur.hours) * 3600000000000L 
            + new System.Numerics.BigInteger(dur.minutes) * 60000000000L
            + new System.Numerics.BigInteger(dur.seconds) * 1000000000L 
            + new System.Numerics.BigInteger(dur.millis) * 1000000L
            + new System.Numerics.BigInteger(dur.micros) * 1000L 
            + new System.Numerics.BigInteger(dur.nanos);
            
        System.Numerics.BigInteger targetEpochDays = IsoMath.ToEpochDays(relTo.date) + totalDays;
        System.Numerics.BigInteger extraDays = timeNs / 86400000000000L;
        System.Numerics.BigInteger remNs = timeNs % 86400000000000L;
        if (remNs < 0)
        {
            extraDays -= 1;
            remNs += 86400000000000L;
        }
        System.Numerics.BigInteger finalEpochDays = targetEpochDays + extraDays;
        if (finalEpochDays < -100_000_000 || finalEpochDays > 100_000_000)
            throw new JsThrownException(ctx.CreateRangeError("Resulting date is outside the supported range."));
        IsoDate finalDate = IsoMath.EpochDaysToCivil((long)finalEpochDays);
        if (!IsoMath.IsoDateWithinLimits(finalDate))
            throw new JsThrownException(ctx.CreateRangeError("Resulting date is outside the supported range."));
            
        return totalDays * 86400000000000L + timeNs;
    }

    /// <summary>Decode all Duration fields from _v object.</summary>
    private static (double years, double months, double weeks, double days, double hours, double minutes, double seconds, double millis, double micros, double nanos)
        DecodeDuration(JsHeap h, JsObject o) => (
        GetVNum(h, o, "years"),
        GetVNum(h, o, "months"),
        GetVNum(h, o, "weeks"),
        GetVNum(h, o, "days"),
        GetVNum(h, o, "hours"),
        GetVNum(h, o, "minutes"),
        GetVNum(h, o, "seconds"),
        GetVNum(h, o, "milliseconds"),
        GetVNum(h, o, "microseconds"),
        GetVNum(h, o, "nanoseconds")
    );

    /// <summary>Duration fields → total nanoseconds.</summary>
    private static System.Numerics.BigInteger DurationToNanos(double days, double hours, double minutes, double seconds, double millis, double micros, double nanos)
    {
        System.Numerics.BigInteger total = 0;
        total += new System.Numerics.BigInteger(days) * 86400000000000L;
        total += new System.Numerics.BigInteger(hours) * 3600000000000L;
        total += new System.Numerics.BigInteger(minutes) * 60000000000L;
        total += new System.Numerics.BigInteger(seconds) * 1000000000L;
        total += new System.Numerics.BigInteger(millis) * 1000000L;
        total += new System.Numerics.BigInteger(micros) * 1000L;
        total += new System.Numerics.BigInteger(nanos);
        return total;
    }

    /// <summary>Approximate total nanoseconds for a Duration (1 day = 86 400 × 10⁹ ns).</summary>
    private static double DurationTotalNs(JsHeap h, JsObject o)
    {
        const double perDay = 86_400_000_000_000.0;
        double ns = GetVNum(h, o, "nanoseconds");
        ns += GetVNum(h, o, "microseconds") * 1_000;
        ns += GetVNum(h, o, "milliseconds") * 1_000_000;
        ns += GetVNum(h, o, "seconds") * 1_000_000_000;
        ns += GetVNum(h, o, "minutes") * 60_000_000_000;
        ns += GetVNum(h, o, "hours") * 3_600_000_000_000;
        ns += GetVNum(h, o, "days") * perDay;
        ns += GetVNum(h, o, "weeks") * 7 * perDay;
        ns += GetVNum(h, o, "months") * 30.436875 * perDay; // avg days/month
        ns += GetVNum(h, o, "years") * 365.2425 * perDay;   // avg days/year
        return ns;
    }

    // ─── Temporal.Instant ──────────────────────────────────
    private void InstallInstant(IBuiltinContext ctx, JsObject t, ObjectHandle tH, JsHeap h)
    {
        var (cH, pH) = MakeCtor(ctx, h, t, tH, "Instant", 1, true,
            (cctx, hh, a) => ConstructInstant(cctx, hh, a));
        var p = h.GetObject(pH);
        AddGetter(ctx, h, pH, p, "epochSeconds", o => GetV(h, o, "es"));
        AddGetter(ctx, h, pH, p, "epochMilliseconds", o => GetV(h, o, "ems"));
        AddGetter(ctx, h, pH, p, "epochMicroseconds", o => GetV(h, o, "eus"));
        AddGetter(ctx, h, pH, p, "epochNanoseconds", o => GetV(h, o, "ensBig"));
        AddMethod(ctx, h, pH, p, "add", (o, a) => {
            var dur = ToTemporalDurationRecord(ctx, h, a.Count > 0 ? a[0] : JsValue.Undefined);
            if (dur.years != 0 || dur.months != 0 || dur.weeks != 0 || dur.days != 0)
                throw new JsThrownException(ctx.CreateRangeError("Instant arithmetic does not support calendar units."));
            System.Numerics.BigInteger totalNs = DurationToNanos(0, dur.hours, dur.minutes, dur.seconds, dur.millis, dur.micros, dur.nanos);
            return AttachPrototype(h, MakeInstantFromNanoseconds(h, DecodeInstantNanosBig(h, o) + totalNs), pH);
        }, 1);
        AddMethod(ctx, h, pH, p, "subtract", (o, a) => {
            var dur = ToTemporalDurationRecord(ctx, h, a.Count > 0 ? a[0] : JsValue.Undefined);
            if (dur.years != 0 || dur.months != 0 || dur.weeks != 0 || dur.days != 0)
                throw new JsThrownException(ctx.CreateRangeError("Instant arithmetic does not support calendar units."));
            System.Numerics.BigInteger totalNs = DurationToNanos(0, dur.hours, dur.minutes, dur.seconds, dur.millis, dur.micros, dur.nanos);
            return AttachPrototype(h, MakeInstantFromNanoseconds(h, DecodeInstantNanosBig(h, o) - totalNs), pH);
        }, 1);
        AddMethod(ctx, h, pH, p, "until", (o, a) => {
            System.Numerics.BigInteger otherNs = ToInstantNsBig(ctx, h, a.Count > 0 ? a[0] : JsValue.Undefined);
            var s = GetDifferenceSettings(ctx, h, a, 1, TimeDiffUnits, "nanosecond", "second");
            return AttachTemporalPrototypeByName(ctx, h, t, "Duration", MakeDiffDuration(ctx, h, otherNs - DecodeInstantNanosBig(h, o), s));
        }, 1);
        AddMethod(ctx, h, pH, p, "since", (o, a) => {
            System.Numerics.BigInteger otherNs = ToInstantNsBig(ctx, h, a.Count > 0 ? a[0] : JsValue.Undefined);
            var s = GetDifferenceSettings(ctx, h, a, 1, TimeDiffUnits, "nanosecond", "second");
            return AttachTemporalPrototypeByName(ctx, h, t, "Duration", MakeDiffDuration(ctx, h, DecodeInstantNanosBig(h, o) - otherNs, s));
        }, 1);
        AddMethod(ctx, h, pH, p, "round", (o, a) =>
            AttachPrototype(h, MakeInstantFromNanoseconds(h, RoundInstantNs(ctx, h, DecodeInstantNanosBig(h, o), a)), pH), 1);
        AddMethod(ctx, h, pH, p, "equals", (o, a) => {
            System.Numerics.BigInteger otherNs = ToInstantNsBig(ctx, h, a.Count > 0 ? a[0] : JsValue.Undefined);
            return JsValue.FromBoolean(DecodeInstantNanosBig(h, o) == otherNs);
        }, 1);
        AddMethod(ctx, h, pH, p, "toString", (o, a) => FormatInstant(ctx, h, o, a), 0);
        AddMethod(ctx, h, pH, p, "toLocaleString", (o, a) => InstantToLocaleString(ctx, h, o, a), 0);
        AddMethod(ctx, h, pH, p, "toJSON", (o, _) => FormatInstant(h, o), 0);
        AddMethod(ctx, h, pH, p, "toZonedDateTimeISO", (o, a) =>
            AttachTemporalPrototypeByName(ctx, h, t, "ZonedDateTime", InstantToZonedDateTimeIso(ctx, h, o, a)), 1);
        AddMethod(ctx, h, pH, p, "valueOf", (_, _2) => throw new JsThrownException(ctx.CreateTypeError("Instant.prototype.valueOf throws.")), 0);
        var c = h.GetObject(cH);
        AddStatic(ctx, h, cH, c, "from", a => {
            // ToTemporalInstant: Instant/ZonedDateTime copy, else string parse;
            // numbers and other primitives are a TypeError.
            if (a.Count == 0) throw new JsThrownException(ctx.CreateTypeError("Instant.from requires at least 1 argument."));
            var arg = a[0];
            if (arg.Tag == JsValueTag.Object)
            {
                var obj = h.GetObject(arg.AsObjectHandle());
                if (IsTemporalInstance(h, arg, pH))
                    return AttachPrototype(h, MakeInstantFromNanoseconds(h, DecodeInstantNanosBig(h, obj)), pH);
                arg = JsValue.FromString(ctx.ToStringValue(arg));
            }
            if (arg.Tag != JsValueTag.String)
                throw new JsThrownException(ctx.CreateTypeError("Instant.from requires a string or a Temporal instant-like object."));
            var s = arg.AsString();
            if (!TemporalIsoParser.TryParseInstant(s, out var parsed, out var parseError))
                throw new JsThrownException(ctx.CreateRangeError($"'{s}' is not a valid ISO string for Instant: {parseError}"));
            _ = CalendarFromAnnotation(ctx, parsed.Calendar);
            return AttachPrototype(h, MakeInstantFromParsed(ctx, h, parsed), pH);
        }, 1);
        AddStatic(ctx, h, cH, c, "fromEpochSeconds", a => AttachPrototype(h, MakeInstantEpoch(ctx, h, a, 1_000_000_000L), pH), 1);
        AddStatic(ctx, h, cH, c, "fromEpochMilliseconds", a => AttachPrototype(h, MakeInstantEpoch(ctx, h, a, 1_000_000L), pH), 1);
        AddStatic(ctx, h, cH, c, "fromEpochMicroseconds", a => AttachPrototype(h, MakeInstantEpoch(ctx, h, a, 1_000L), pH), 1);
        AddStatic(ctx, h, cH, c, "fromEpochNanoseconds", a => AttachPrototype(h, MakeInstantEpoch(ctx, h, a, 1L), pH), 1);
        AddStatic(ctx, h, cH, c, "compare", a => {
            System.Numerics.BigInteger nsA = ToInstantNsBig(ctx, h, a.Count > 0 ? a[0] : JsValue.Undefined);
            System.Numerics.BigInteger nsB = ToInstantNsBig(ctx, h, a.Count > 1 ? a[1] : JsValue.Undefined);
            return JsValue.FromNumber(nsA < nsB ? -1 : nsA > nsB ? 1 : 0);
        }, 2);
    }

    // ─── Temporal.PlainDate ────────────────────────────────
    private void InstallPlainDate(IBuiltinContext ctx, JsObject t, ObjectHandle tH, JsHeap h)
    {
        var (cH, pH) = MakeCtor(ctx, h, t, tH, "PlainDate", 3, true,
            (cctx, hh, a) => ConstructPlainDate(cctx, hh, a));
        var p = h.GetObject(pH);
        AddGetter(ctx, h, pH, p, "year", o => { var iso = DecodeIsoDate(h, o); return JsValue.FromNumber(CalFields(CalId(h, o), iso)?.Year ?? iso.Year); });
        AddGetter(ctx, h, pH, p, "month", o => { var iso = DecodeIsoDate(h, o); return JsValue.FromNumber(CalFields(CalId(h, o), iso)?.Month ?? iso.Month); });
        AddGetter(ctx, h, pH, p, "monthCode", o => { var iso = DecodeIsoDate(h, o); return JsValue.FromString(CalFields(CalId(h, o), iso)?.MonthCode ?? $"M{iso.Month:D2}"); });
        AddGetter(ctx, h, pH, p, "day", o => { var iso = DecodeIsoDate(h, o); return JsValue.FromNumber(CalFields(CalId(h, o), iso)?.Day ?? iso.Day); });
        AddGetter(ctx, h, pH, p, "calendarId", o => { var cid = GetVStr(h, o, "calendarId"); return JsValue.FromString(string.IsNullOrEmpty(cid) ? "iso8601" : cid); });
        AddGetter(ctx, h, pH, p, "dayOfWeek", o => JsValue.FromNumber(IsoMath.DayOfWeek(DecodeIsoDate(h, o))));
        AddGetter(ctx, h, pH, p, "dayOfYear", o => { var dt = DecodeIsoDate(h, o); return JsValue.FromNumber(CalFields(CalId(h, o), dt)?.DayOfYear ?? IsoMath.DayOfYear(dt)); });
        AddGetter(ctx, h, pH, p, "weekOfYear", o => { var dt = DecodeIsoDate(h, o); return CalFields(CalId(h, o), dt) is null ? JsValue.FromNumber(IsoMath.WeekOfYear(dt).Week) : JsValue.Undefined; });
        AddGetter(ctx, h, pH, p, "yearOfWeek", o => { var dt = DecodeIsoDate(h, o); return CalFields(CalId(h, o), dt) is null ? JsValue.FromNumber(IsoMath.WeekOfYear(dt).Year) : JsValue.Undefined; });
        AddGetter(ctx, h, pH, p, "daysInWeek", o => JsValue.FromNumber(7));
        AddGetter(ctx, h, pH, p, "daysInMonth", o => { var dt = DecodeIsoDate(h, o); return JsValue.FromNumber(CalFields(CalId(h, o), dt)?.DaysInMonth ?? IsoMath.DaysInMonth(dt.Year, dt.Month)); });
        AddGetter(ctx, h, pH, p, "daysInYear", o => { var dt = DecodeIsoDate(h, o); return JsValue.FromNumber(CalFields(CalId(h, o), dt)?.DaysInYear ?? IsoMath.DaysInYear(dt.Year)); });
        AddGetter(ctx, h, pH, p, "monthsInYear", o => { var dt = DecodeIsoDate(h, o); return JsValue.FromNumber(CalFields(CalId(h, o), dt)?.MonthsInYear ?? 12); });
        AddGetter(ctx, h, pH, p, "inLeapYear", o => { var dt = DecodeIsoDate(h, o); return JsValue.FromBoolean(CalFields(CalId(h, o), dt)?.InLeapYear ?? IsoMath.IsLeapYear(dt.Year)); });
        AddGetter(ctx, h, pH, p, "era", o => EraValue(CalId(h, o), DecodeIsoDate(h, o)));
        AddGetter(ctx, h, pH, p, "eraYear", o => EraYearValue(CalId(h, o), DecodeIsoDate(h, o)));
        AddMethod(ctx, h, pH, p, "with", (o, a) => {
            if (a.Count < 1 || a[0].Tag != JsValueTag.Object) throw new JsThrownException(ctx.CreateTypeError("with: argument must be an object."));
            var bagValue = a[0];
            // RejectObjectWithCalendarOrTimeZone: read calendar and timeZone first (observable order).
            bool hasCal = TryGetField(ctx, h, bagValue, "calendar", out _);
            bool hasCalId = TryGetField(ctx, h, bagValue, "calendarId", out _);
            bool hasTz = TryGetField(ctx, h, bagValue, "timeZone", out _);
            if (hasCal || hasCalId || hasTz)
                throw new JsThrownException(ctx.CreateTypeError("calendar and timeZone cannot be changed here; use withCalendar/withTimeZone."));
            var cur = DecodeIsoDate(h, o);
            string cal = CalId(h, o);
            bool hasYear = TryGetField(ctx, h, bagValue, "year", out _);
            bool hasMonth = TryGetField(ctx, h, bagValue, "month", out _);
            bool hasMonthCode = TryGetField(ctx, h, bagValue, "monthCode", out _);
            bool hasDay = TryGetField(ctx, h, bagValue, "day", out _);
            bool hasEra = TryGetField(ctx, h, bagValue, "era", out _);
            bool hasEraYear = TryGetField(ctx, h, bagValue, "eraYear", out _);
            if (!hasYear && !hasMonth && !hasMonthCode && !hasDay && !hasEra && !hasEraYear)
                throw new JsThrownException(ctx.CreateTypeError("with: at least one temporal field is required."));
            var iso = ResolveDateBagToIso(ctx, h, bagValue, cal, a, 1, CalFields(cal, cur) ?? new CalendarFields(null, null, cur.Year, cur.Month, $"M{cur.Month:D2}", cur.Day, 0, 0, 0, 12, false), isWith: true);
            return AttachPrototype(h, MakePlainDateYmd(ctx, h, iso.Year, iso.Month, iso.Day, cal), pH);
        }, 1);
        AddMethod(ctx, h, pH, p, "withCalendar", (o, a) => {
            var cal = ToCalendarIdentifier(ctx, h, a.Count > 0 ? a[0] : JsValue.Undefined);
            var cur = DecodeIsoDate(h, o);
            return AttachPrototype(h, MakePlainDateYmd(ctx, h, cur.Year, cur.Month, cur.Day, cal), pH);
        }, 1);
        AddMethod(ctx, h, pH, p, "add", (o, a) => {
            var dur = ToTemporalDurationRecord(ctx, h, a.Count > 0 ? a[0] : JsValue.Undefined);
            bool constrain = GetOverflowOption(ctx, h, a, 1) == "constrain";
            var result = AddDateInCalendar(CalId(h, o), DecodeIsoDate(h, o), dur.years, dur.months, dur.weeks,
                dur.days + (dur.hours * 3600L + dur.minutes * 60L + dur.seconds) / 86_400.0, constrain, out var invalid);
            if (invalid || !IsoMath.IsoDateWithinLimits(result))
                throw new JsThrownException(ctx.CreateRangeError("Resulting date is outside the supported range or invalid under overflow=reject."));
            return AttachPrototype(h, MakePlainDateYmd(ctx, h, result.Year, result.Month, result.Day, GetVStr(h, o, "calendarId")), pH);
        }, 1);
        AddMethod(ctx, h, pH, p, "subtract", (o, a) => {
            var dur = ToTemporalDurationRecord(ctx, h, a.Count > 0 ? a[0] : JsValue.Undefined);
            bool constrain = GetOverflowOption(ctx, h, a, 1) == "constrain";
            var result = AddDateInCalendar(CalId(h, o), DecodeIsoDate(h, o), -dur.years, -dur.months, -dur.weeks,
                -(dur.days + (dur.hours * 3600L + dur.minutes * 60L + dur.seconds) / 86_400.0), constrain, out var invalid);
            if (invalid || !IsoMath.IsoDateWithinLimits(result))
                throw new JsThrownException(ctx.CreateRangeError("Resulting date is outside the supported range or invalid under overflow=reject."));
            return AttachPrototype(h, MakePlainDateYmd(ctx, h, result.Year, result.Month, result.Day, GetVStr(h, o, "calendarId")), pH);
        }, 1);
        AddMethod(ctx, h, pH, p, "until", (o, a) => {
            var (other, otherCal) = ToTemporalDateRecord(ctx, h, a.Count > 0 ? a[0] : JsValue.Undefined);
            RequireMatchingCalendar(ctx, CalId(h, o), otherCal);
            var s = GetDifferenceSettings(ctx, h, a, 1, DateDiffUnits, "day", "day");
            var (yy, mm, ww, dd) = DifferenceDateDuration(CalId(h, o), DecodeIsoDate(h, o), other, s.Largest);
            return AttachTemporalPrototypeByName(ctx, h, t, "Duration",
                MakeDuration(ctx, h, yy, mm, ww, ToSafeInt(dd), 0, 0, 0, 0, 0, 0));
        }, 1);
        AddMethod(ctx, h, pH, p, "since", (o, a) => {
            var (other, otherCal) = ToTemporalDateRecord(ctx, h, a.Count > 0 ? a[0] : JsValue.Undefined);
            RequireMatchingCalendar(ctx, CalId(h, o), otherCal);
            var s = GetDifferenceSettings(ctx, h, a, 1, DateDiffUnits, "day", "day");
            var (yy, mm, ww, dd) = DifferenceDateDuration(CalId(h, o), DecodeIsoDate(h, o), other, s.Largest);
            return AttachTemporalPrototypeByName(ctx, h, t, "Duration",
                MakeDuration(ctx, h, -yy, -mm, -ww, -ToSafeInt(dd), 0, 0, 0, 0, 0, 0));
        }, 1);
        AddMethod(ctx, h, pH, p, "equals", (o, a) => {
            var (other, otherCal) = ToTemporalDateRecord(ctx, h, a.Count > 0 ? a[0] : JsValue.Undefined);
            var self = DecodeIsoDate(h, o);
            var selfCal = GetVStr(h, o, "calendarId");
            bool calsEqual = (string.IsNullOrEmpty(selfCal) ? "iso8601" : selfCal) == (string.IsNullOrEmpty(otherCal) ? "iso8601" : otherCal);
            return JsValue.FromBoolean(IsoMath.Compare(self, other) == 0 && calsEqual);
        }, 1);
        AddMethod(ctx, h, pH, p, "toPlainDateTime", (o, a) => {
            var d = DecodeIsoDate(h, o);
            var tm = a.Count > 0 && a[0].Tag != JsValueTag.Undefined ? ToTemporalTimeRecord(ctx, h, a[0]) : IsoTime.Midnight;
            return AttachTemporalPrototypeByName(ctx, h, t, "PlainDateTime", MakePlainDateTimeParts(ctx, h,
                d.Year, d.Month, d.Day, tm.Hour, tm.Minute, tm.Second, tm.Millisecond, tm.Microsecond, tm.Nanosecond,
                GetVStr(h, o, "calendarId")));
        }, 0);
        AddMethod(ctx, h, pH, p, "toPlainYearMonth", (o, _) => {
            string cal = CalId(h, o);
            var refIso = YearMonthReferenceIso(cal, DecodeIsoDate(h, o));
            return AttachTemporalPrototypeByName(ctx, h, t, "PlainYearMonth", MakePlainYearMonth(ctx, h, refIso.Year, refIso.Month, GetVStr(h, o, "calendarId"), refIso.Day));
        }, 0);
        AddMethod(ctx, h, pH, p, "toPlainMonthDay", (o, _) => {
            var d = DecodeIsoDate(h, o);
            return AttachTemporalPrototypeByName(ctx, h, t, "PlainMonthDay", MakePlainMonthDay(ctx, h, d.Year, d.Month, d.Day, GetVStr(h, o, "calendarId")));
        }, 0);
        AddMethod(ctx, h, pH, p, "toZonedDateTime", (o, a) => {
            // Argument: time zone string, or { timeZone, plainTime? }.
            var d = DecodeIsoDate(h, o);
            string tzRaw;
            var tm = IsoTime.Midnight;
            bool useStartOfDay = true;
            if (a.Count > 0 && a[0].Tag == JsValueTag.String)
            {
                tzRaw = a[0].AsString();
            }
            else if (a.Count > 0 && a[0].Tag == JsValueTag.Object)
            {
                if (!TryGetField(ctx, h, a[0], "timeZone", out var tzv) || tzv.Tag == JsValueTag.Undefined)
                    throw new JsThrownException(ctx.CreateTypeError("toZonedDateTime: timeZone is required."));
                if (tzv.Tag != JsValueTag.String)
                    throw new JsThrownException(ctx.CreateTypeError("toZonedDateTime: timeZone must be a string."));
                tzRaw = tzv.AsString();
                if (TryGetField(ctx, h, a[0], "plainTime", out var ptv) && ptv.Tag != JsValueTag.Undefined)
                {
                    tm = ToTemporalTimeRecord(ctx, h, ptv);
                    useStartOfDay = false;
                }
            }
            else
            {
                throw new JsThrownException(ctx.CreateTypeError("toZonedDateTime: time zone is required."));
            }
            string tz = CanonicalizeTimeZoneId(ctx, tzRaw);
            var epochNs = useStartOfDay
                ? TemporalTimeZones.GetStartOfDayEpochNsBig(tz, d)
                : TemporalTimeZones.EpochNsFromWallBig(tz, d, tm);
            return AttachTemporalPrototypeByName(ctx, h, t, "ZonedDateTime", MakeZonedDateTimeNsBig(ctx, h,
                epochNs, tz, GetVStr(h, o, "calendarId")));
        }, 1);
        AddMethod(ctx, h, pH, p, "toString", (o, a) => {
            var opts = GetToStringOptions(ctx, h, a, 0, new[] { "calendarName" });
            var d = DecodeIsoDate(h, o);
            return JsValue.FromString($"{FormatIsoYear(d.Year)}-{d.Month:D2}-{d.Day:D2}{CalendarSuffix(h, o, opts)}");
        }, 0);
        AddMethod(ctx, h, pH, p, "toLocaleString", (o, a) => {
            RequireNoTimeStyle(ctx, h, a);
            var locale = a.Count > 0 ? ToStrArg(ctx, a[0]) : string.Empty;
            var options = ParseDateTimeFormatOptions(locale, ctx, h, a.Count > 1 ? a[1] : JsValue.Undefined);
            try { IntlDateTimeFormatting.ValidateOptions(options); } catch (InvalidOperationException) { throw new JsThrownException(ctx.CreateTypeError("dateStyle/timeStyle conflicts with explicit component options.")); }
            try { IntlDateTimeFormatting.ValidateTemporalCalendar(GetVStr(h, o, "calendarId"), options); } catch (InvalidOperationException) { throw new JsThrownException(ctx.CreateRangeError("calendar mismatch")); }
            var culture = IntlDateTimeFormatting.ResolveCulture(locale);
            var dt = DecodeIsoDate(h, o);
            var result = IntlDateTimeFormatting.FormatDateOnly(dt.Year, dt.Month, dt.Day, culture, options);
            return JsValue.FromString(result.Text);
        }, 0);
        AddMethod(ctx, h, pH, p, "toJSON", (o, _) => FormatPlainDate(h, o), 0);
        AddMethod(ctx, h, pH, p, "valueOf", (_, _2) => throw new JsThrownException(ctx.CreateTypeError("PlainDate.prototype.valueOf throws.")), 0);
        var c = h.GetObject(cH);
        AddStatic(ctx, h, cH, c, "from", a => {
            if (a.Count == 0) throw new JsThrownException(ctx.CreateTypeError("PlainDate.from requires at least 1 argument."));
            var arg = a[0];
            if (arg.Tag == JsValueTag.String)
            {
                var s = arg.AsString();
                // ToTemporalDate: AnnotatedDateTime without UTC designator.
                if (!TemporalIsoParser.TryParseDateTime(s, out var parsed, out var parseError) || parsed.HasUtcDesignator)
                    throw new JsThrownException(ctx.CreateRangeError($"'{s}' is not a valid ISO string for PlainDate: {parseError}"));
                var parsedCal = CalendarFromAnnotation(ctx, parsed.Calendar);
                _ = GetOverflowOption(ctx, h, a, 1);
                if (!IsoMath.IsoDateWithinLimits(new IsoDate(parsed.Year, parsed.Month, parsed.Day)))
                    throw new JsThrownException(ctx.CreateRangeError("Date is outside the supported Temporal range."));
                return AttachPrototype(h, MakePlainDateYmd(ctx, h, parsed.Year, parsed.Month, parsed.Day, parsedCal), pH);
            }
            if (arg.Tag == JsValueTag.Object)
            {
                var obj = h.GetObject(arg.AsObjectHandle());
                if (IsTemporalInstance(h, arg, pH))
                {
                    _ = GetOverflowOption(ctx, h, a, 1);
                    int cy = ToSafeInt(GetVNum(h, obj, "y")), cm = ToSafeInt(GetVNum(h, obj, "m")), cd = ToSafeInt(GetVNum(h, obj, "d"));
                    string ccal = GetVStr(h, obj, "calendarId"); if (string.IsNullOrEmpty(ccal)) ccal = "iso8601";
                    return AttachPrototype(h, MakePlainDateYmd(ctx, h, cy, cm, cd, ccal), pH);
                }
                // ToTemporalDate step 2.b: a PlainDateTime instance yields its ISO date from
                // internal slots — never via observable getters.
                if (TryGetInternalData(h, obj, out var idata) && HasOwn(h, idata, "year") && HasOwn(h, idata, "day"))
                {
                    _ = GetOverflowOption(ctx, h, a, 1);
                    var iso = DecodeIsoDateLong(h, obj);
                    string ical = GetVStr(h, obj, "calendarId"); if (string.IsNullOrEmpty(ical)) ical = "iso8601";
                    return AttachPrototype(h, MakePlainDateYmd(ctx, h, iso.Year, iso.Month, iso.Day, ical), pH);
                }
                // Property bag: year (or era+eraYear) + (month|monthCode) + day required.
                string cal = GetCalendarFromFields(ctx, h, arg);
                var bagIso = ResolveDateBagToIso(ctx, h, arg, cal, a, 1);
                return AttachPrototype(h, MakePlainDateYmd(ctx, h, bagIso.Year, bagIso.Month, bagIso.Day, cal), pH);
            }
            throw new JsThrownException(ctx.CreateTypeError("PlainDate.from: argument must be a string, PlainDate, or property bag."));
        }, 1);
        AddStatic(ctx, h, cH, c, "compare", a => {
            var (one, _) = ToTemporalDateRecord(ctx, h, a.Count > 0 ? a[0] : JsValue.Undefined);
            var (two, _) = ToTemporalDateRecord(ctx, h, a.Count > 1 ? a[1] : JsValue.Undefined);
            return JsValue.FromNumber(IsoMath.Compare(one, two));
        }, 2);
    }

    // ─── Temporal.PlainTime ────────────────────────────────
    private void InstallPlainTime(IBuiltinContext ctx, JsObject t, ObjectHandle tH, JsHeap h)
    {
        var (cH, pH) = MakeCtor(ctx, h, t, tH, "PlainTime", 0, true,
            (cctx, hh, a) => ConstructPlainTime(cctx, hh, a));
        var p = h.GetObject(pH);
        foreach (var f in new[] { "hour", "microsecond", "millisecond", "minute", "nanosecond", "second" })
            AddGetter(ctx, h, pH, p, f, o => GetV(h, o, f));
        AddMethod(ctx, h, pH, p, "with", (o, a) => {
            if (a.Count < 1 || a[0].Tag != JsValueTag.Object) throw new JsThrownException(ctx.CreateTypeError("PlainTime.with: argument must be an object."));
            var bagValue = a[0];
            string[] fields = { "hour", "minute", "second", "millisecond", "microsecond", "nanosecond" };
            var values = new double[fields.Length];
            bool any = false;
            for (int fi = 0; fi < fields.Length; fi++)
            {
                if (TryGetField(ctx, h, bagValue, fields[fi], out var fv))
                {
                    values[fi] = ToIntegerWithTruncation(ctx, fv);
                    any = true;
                }
                else
                {
                    values[fi] = GetVNum(h, o, fields[fi]);
                }
            }
            if (!any)
                throw new JsThrownException(ctx.CreateTypeError("with: at least one time field is required."));
            var overflow = GetOverflowOption(ctx, h, a, 1);
            if (overflow == "reject")
                ValidateTime(ctx, values[0], values[1], values[2], values[3], values[4], values[5]);
            return AttachPrototype(h, MakePlainTime(ctx, h,
                (int)Math.Clamp(values[0], 0, 23), (int)Math.Clamp(values[1], 0, 59), (int)Math.Clamp(values[2], 0, 59),
                (int)Math.Clamp(values[3], 0, 999), (int)Math.Clamp(values[4], 0, 999), (int)Math.Clamp(values[5], 0, 999)), pH);
        }, 1);
        AddMethod(ctx, h, pH, p, "add", (o, a) => {
            var dur = ToTemporalDurationRecord(ctx, h, a.Count > 0 ? a[0] : JsValue.Undefined);
            System.Numerics.BigInteger totalNs = DurationToNanos(dur.days + dur.weeks * 7, dur.hours, dur.minutes, dur.seconds, dur.millis, dur.micros, dur.nanos);
            var selfNs = DurationToNanos(0, (int)GetVNum(h,o,"hour"), (int)GetVNum(h,o,"minute"), (int)GetVNum(h,o,"second"), (int)GetVNum(h,o,"millisecond"), (int)GetVNum(h,o,"microsecond"), (int)GetVNum(h,o,"nanosecond"));
            System.Numerics.BigInteger resultNs = ((selfNs + totalNs) % 86_400_000_000_000L + 86_400_000_000_000L) % 86_400_000_000_000L;
            return AttachPrototype(h, MakePlainTimeFromDayNs(ctx, h, (long)resultNs), pH);
        }, 1);
        AddMethod(ctx, h, pH, p, "subtract", (o, a) => {
            var dur = ToTemporalDurationRecord(ctx, h, a.Count > 0 ? a[0] : JsValue.Undefined);
            System.Numerics.BigInteger totalNs = DurationToNanos(dur.days + dur.weeks * 7, dur.hours, dur.minutes, dur.seconds, dur.millis, dur.micros, dur.nanos);
            var selfNs = DurationToNanos(0, (int)GetVNum(h,o,"hour"), (int)GetVNum(h,o,"minute"), (int)GetVNum(h,o,"second"), (int)GetVNum(h,o,"millisecond"), (int)GetVNum(h,o,"microsecond"), (int)GetVNum(h,o,"nanosecond"));
            System.Numerics.BigInteger resultNs = ((selfNs - totalNs) % 86_400_000_000_000L + 86_400_000_000_000L) % 86_400_000_000_000L;
            return AttachPrototype(h, MakePlainTimeFromDayNs(ctx, h, (long)resultNs), pH);
        }, 1);
        AddMethod(ctx, h, pH, p, "until", (o, a) => {
            var other = ToTemporalTimeRecord(ctx, h, a.Count > 0 ? a[0] : JsValue.Undefined);
            var s = GetDifferenceSettings(ctx, h, a, 1, TimeDiffUnits, "nanosecond", "hour");
            var selfNs = DurationToNanos(0, (int)GetVNum(h,o,"hour"), (int)GetVNum(h,o,"minute"), (int)GetVNum(h,o,"second"), (int)GetVNum(h,o,"millisecond"), (int)GetVNum(h,o,"microsecond"), (int)GetVNum(h,o,"nanosecond"));
            return AttachTemporalPrototypeByName(ctx, h, t, "Duration", MakeDiffDuration(ctx, h, (long)(other.ToNanosecondsOfDay() - selfNs), s));
        }, 1);
        AddMethod(ctx, h, pH, p, "since", (o, a) => {
            var other = ToTemporalTimeRecord(ctx, h, a.Count > 0 ? a[0] : JsValue.Undefined);
            var s = GetDifferenceSettings(ctx, h, a, 1, TimeDiffUnits, "nanosecond", "hour");
            var selfNs = DurationToNanos(0, (int)GetVNum(h,o,"hour"), (int)GetVNum(h,o,"minute"), (int)GetVNum(h,o,"second"), (int)GetVNum(h,o,"millisecond"), (int)GetVNum(h,o,"microsecond"), (int)GetVNum(h,o,"nanosecond"));
            return AttachTemporalPrototypeByName(ctx, h, t, "Duration", MakeDiffDuration(ctx, h, (long)(selfNs - other.ToNanosecondsOfDay()), s));
        }, 1);
        AddMethod(ctx, h, pH, p, "round", (o, a) => {
            long dayNs = RoundPlainTimeNs(ctx, h, DecodeTimeOfDayNs(h, o), a) % NsPerDay;
            if (dayNs < 0) dayNs += NsPerDay;
            int hr = (int)(dayNs / 3_600_000_000_000L);
            int mi = (int)(dayNs / 60_000_000_000L % 60);
            int se = (int)(dayNs / 1_000_000_000L % 60);
            int ms = (int)(dayNs / 1_000_000L % 1000);
            int us = (int)(dayNs / 1_000L % 1000);
            int ns = (int)(dayNs % 1000);
            return AttachPrototype(h, MakePlainTime(ctx, h, hr, mi, se, ms, us, ns), pH);
        }, 1);
        AddMethod(ctx, h, pH, p, "equals", (o, a) => {
            var other = ToTemporalTimeRecord(ctx, h, a.Count > 0 ? a[0] : JsValue.Undefined);
            var selfNs = DurationToNanos(0, (int)GetVNum(h,o,"hour"), (int)GetVNum(h,o,"minute"), (int)GetVNum(h,o,"second"), (int)GetVNum(h,o,"millisecond"), (int)GetVNum(h,o,"microsecond"), (int)GetVNum(h,o,"nanosecond"));
            return JsValue.FromBoolean(selfNs == other.ToNanosecondsOfDay());
        }, 1);
        AddMethod(ctx, h, pH, p, "toLocaleString", (o, a) => {
            RequireNoDateStyle(ctx, h, a);
            return PlainTimeToLocaleString(ctx, h, o, a);
        }, 0);
        AddMethod(ctx, h, pH, p, "toString", (o, a) => {
            var opts = GetToStringOptions(ctx, h, a, 0, new[] { "fractionalSecondDigits", "roundingMode", "smallestUnit" });
            long dayNs = DecodeTimeOfDayNs(h, o);
            long inc = PrecisionIncrementNs(opts);
            if (inc > 1) dayNs = (long)(RoundNsToIncrement(ctx, dayNs, inc, opts.RoundingMode) % NsPerDay);
            return JsValue.FromString($"{dayNs / 3_600_000_000_000L:D2}:{dayNs / 60_000_000_000L % 60:D2}{FormatSecondsPart(dayNs, opts)}");
        }, 0);
        AddMethod(ctx, h, pH, p, "toJSON", (o, _) => FormatPlainTime(h, o), 0);
        AddMethod(ctx, h, pH, p, "valueOf", (_, _2) => throw new JsThrownException(ctx.CreateTypeError("valueOf throws.")), 0);
        var c = h.GetObject(cH);
        AddStatic(ctx, h, cH, c, "from", a => {
            if (a.Count == 0) throw new JsThrownException(ctx.CreateTypeError("PlainTime.from requires at least 1 argument."));
            var arg = a[0];
            if (arg.Tag == JsValueTag.String)
            {
                var s = arg.AsString();
                // ToTemporalTime: time string without UTC designator.
                if (!TemporalIsoParser.TryParseTime(s, out var parsed, out var parseError) || parsed.HasUtcDesignator)
                    throw new JsThrownException(ctx.CreateRangeError($"'{s}' is not a valid ISO string for PlainTime: {parseError}"));
                // PlainTime ignores calendar annotations (it has no calendar).
                _ = GetOverflowOption(ctx, h, a, 1);
                var tm = parsed.Time;
                return AttachPrototype(h, MakePlainTime(ctx, h, tm.Hour, tm.Minute, tm.Second, tm.Millisecond, tm.Microsecond, tm.Nanosecond), pH);
            }
            if (arg.Tag == JsValueTag.Object)
            {
                var obj = h.GetObject(arg.AsObjectHandle());
                if (IsTemporalInstance(h, arg, pH))
                {
                    _ = GetOverflowOption(ctx, h, a, 1);
                    int hr = ToSafeInt(GetVNum(h, obj, "hour")), mi = ToSafeInt(GetVNum(h, obj, "minute")),
                        se = ToSafeInt(GetVNum(h, obj, "second")), ms = ToSafeInt(GetVNum(h, obj, "millisecond")),
                        us = ToSafeInt(GetVNum(h, obj, "microsecond")), ns = ToSafeInt(GetVNum(h, obj, "nanosecond"));
                    return AttachPrototype(h, MakePlainTime(ctx, h, hr, mi, se, ms, us, ns), pH);
                }
                // Property bag: at least one time field required; overflow regulates.
                string[] fields = { "hour", "minute", "second", "millisecond", "microsecond", "nanosecond" };
                var values = new double[fields.Length];
                bool any = false;
                for (int fi = 0; fi < fields.Length; fi++)
                {
                    if (TryGetField(ctx, h, arg, fields[fi], out var fv))
                    {
                        values[fi] = ToIntegerWithTruncation(ctx, fv);
                        any = true;
                    }
                }
                if (!any)
                    throw new JsThrownException(ctx.CreateTypeError("PlainTime.from: at least one time field is required."));
                var overflow = GetOverflowOption(ctx, h, a, 1);
                if (overflow == "constrain")
                {
                    values[0] = Math.Clamp(values[0], 0, 23);
                    values[1] = Math.Clamp(values[1], 0, 59);
                    values[2] = Math.Clamp(values[2], 0, 59);
                    values[3] = Math.Clamp(values[3], 0, 999);
                    values[4] = Math.Clamp(values[4], 0, 999);
                    values[5] = Math.Clamp(values[5], 0, 999);
                }
                else
                {
                    ValidateTime(ctx, values[0], values[1], values[2], values[3], values[4], values[5]);
                }
                return AttachPrototype(h, MakePlainTime(ctx, h,
                    (int)values[0], (int)values[1], (int)values[2], (int)values[3], (int)values[4], (int)values[5]), pH);
            }
            throw new JsThrownException(ctx.CreateTypeError("PlainTime.from: argument must be a string, PlainTime, or property bag."));
        }, 1);
        AddStatic(ctx, h, cH, c, "compare", a => {
            long one = ToTemporalTimeRecord(ctx, h, a.Count > 0 ? a[0] : JsValue.Undefined).ToNanosecondsOfDay();
            long two = ToTemporalTimeRecord(ctx, h, a.Count > 1 ? a[1] : JsValue.Undefined).ToNanosecondsOfDay();
            return JsValue.FromNumber(one < two ? -1 : one > two ? 1 : 0);
        }, 2);
    }

    // ─── Temporal.PlainDateTime ────────────────────────────
    private void InstallPlainDateTime(IBuiltinContext ctx, JsObject t, ObjectHandle tH, JsHeap h)
    {
        var (cH, pH) = MakeCtor(ctx, h, t, tH, "PlainDateTime", 3, true,
            (cctx, hh, a) => ConstructPlainDateTime(cctx, hh, a));
        var p = h.GetObject(pH);
        foreach (var f in new[] { "hour", "microsecond", "millisecond", "minute", "nanosecond", "second" })
            AddGetter(ctx, h, pH, p, f, o => GetV(h, o, f));
        AddGetter(ctx, h, pH, p, "year", o => { var iso = DecodeIsoDateLong(h, o); return JsValue.FromNumber(CalFields(CalId(h, o), iso)?.Year ?? iso.Year); });
        AddGetter(ctx, h, pH, p, "month", o => { var iso = DecodeIsoDateLong(h, o); return JsValue.FromNumber(CalFields(CalId(h, o), iso)?.Month ?? iso.Month); });
        AddGetter(ctx, h, pH, p, "day", o => { var iso = DecodeIsoDateLong(h, o); return JsValue.FromNumber(CalFields(CalId(h, o), iso)?.Day ?? iso.Day); });
        AddGetter(ctx, h, pH, p, "monthCode", o => { var iso = DecodeIsoDateLong(h, o); return JsValue.FromString(CalFields(CalId(h, o), iso)?.MonthCode ?? $"M{iso.Month:D2}"); });
        AddGetter(ctx, h, pH, p, "calendarId", o => { var cid = GetVStr(h, o, "calendarId"); return JsValue.FromString(string.IsNullOrEmpty(cid) ? "iso8601" : cid); });
        AddGetter(ctx, h, pH, p, "dayOfWeek", o => JsValue.FromNumber(IsoMath.DayOfWeek(DecodeIsoDateLong(h, o))));
        AddGetter(ctx, h, pH, p, "dayOfYear", o => { var dt = DecodeIsoDateLong(h, o); return JsValue.FromNumber(CalFields(CalId(h, o), dt)?.DayOfYear ?? IsoMath.DayOfYear(dt)); });
        AddGetter(ctx, h, pH, p, "weekOfYear", o => { var dt = DecodeIsoDateLong(h, o); return CalFields(CalId(h, o), dt) is null ? JsValue.FromNumber(IsoMath.WeekOfYear(dt).Week) : JsValue.Undefined; });
        AddGetter(ctx, h, pH, p, "yearOfWeek", o => { var dt = DecodeIsoDateLong(h, o); return CalFields(CalId(h, o), dt) is null ? JsValue.FromNumber(IsoMath.WeekOfYear(dt).Year) : JsValue.Undefined; });
        AddGetter(ctx, h, pH, p, "daysInWeek", o => JsValue.FromNumber(7));
        AddGetter(ctx, h, pH, p, "daysInMonth", o => { var dt = DecodeIsoDateLong(h, o); return JsValue.FromNumber(CalFields(CalId(h, o), dt)?.DaysInMonth ?? IsoMath.DaysInMonth(dt.Year, dt.Month)); });
        AddGetter(ctx, h, pH, p, "daysInYear", o => { var dt = DecodeIsoDateLong(h, o); return JsValue.FromNumber(CalFields(CalId(h, o), dt)?.DaysInYear ?? IsoMath.DaysInYear(dt.Year)); });
        AddGetter(ctx, h, pH, p, "monthsInYear", o => { var dt = DecodeIsoDateLong(h, o); return JsValue.FromNumber(CalFields(CalId(h, o), dt)?.MonthsInYear ?? 12); });
        AddGetter(ctx, h, pH, p, "inLeapYear", o => { var dt = DecodeIsoDateLong(h, o); return JsValue.FromBoolean(CalFields(CalId(h, o), dt)?.InLeapYear ?? IsoMath.IsLeapYear(dt.Year)); });
        AddGetter(ctx, h, pH, p, "era", o => EraValue(CalId(h, o), DecodeIsoDateLong(h, o)));
        AddGetter(ctx, h, pH, p, "eraYear", o => EraYearValue(CalId(h, o), DecodeIsoDateLong(h, o)));
        AddMethod(ctx, h, pH, p, "with", (o, a) => {
            if (a.Count < 1 || a[0].Tag != JsValueTag.Object) throw new JsThrownException(ctx.CreateTypeError("with: argument must be an object."));
            var bagValue = a[0];
            // RejectObjectWithCalendarOrTimeZone: read calendar and timeZone first (observable order).
            bool hasCal = TryGetField(ctx, h, bagValue, "calendar", out _);
            bool hasCalId = TryGetField(ctx, h, bagValue, "calendarId", out _);
            bool hasTz = TryGetField(ctx, h, bagValue, "timeZone", out _);
            if (hasCal || hasCalId || hasTz)
                throw new JsThrownException(ctx.CreateTypeError("calendar and timeZone cannot be changed here; use withCalendar/withTimeZone."));
            var curDate = DecodeIsoDateLong(h, o);
            string cal = CalId(h, o);
            bool hasYear = TryGetField(ctx, h, bagValue, "year", out _);
            bool hasMonth = TryGetField(ctx, h, bagValue, "month", out _);
            bool hasMonthCode = TryGetField(ctx, h, bagValue, "monthCode", out _);
            bool hasDay = TryGetField(ctx, h, bagValue, "day", out _);
            bool hasEra = TryGetField(ctx, h, bagValue, "era", out _);
            bool hasEraYear = TryGetField(ctx, h, bagValue, "eraYear", out _);
            bool hasDateField = hasYear || hasMonth || hasMonthCode || hasDay || hasEra || hasEraYear;
            string[] timeFields = { "hour", "minute", "second", "millisecond", "microsecond", "nanosecond" };
            var timeValues = new double[timeFields.Length];
            bool anyTime = false;
            for (int fi = 0; fi < timeFields.Length; fi++)
            {
                if (TryGetField(ctx, h, bagValue, timeFields[fi], out var fv))
                {
                    timeValues[fi] = ToIntegerWithTruncation(ctx, fv);
                    anyTime = true;
                }
                else
                {
                    timeValues[fi] = GetVNum(h, o, timeFields[fi]);
                }
            }
            if (!hasDateField && !anyTime)
                throw new JsThrownException(ctx.CreateTypeError("with: at least one temporal field is required."));
            var baseFields = CalFields(cal, curDate) ?? new CalendarFields(null, null, curDate.Year, curDate.Month, $"M{curDate.Month:D2}", curDate.Day, 0, 0, 0, 12, false);
            var date = ResolveDateBagToIso(ctx, h, bagValue, cal, a, 1, out var overflow, baseFields, isWith: true);
            if (overflow == "reject")
                ValidateTime(ctx, timeValues[0], timeValues[1], timeValues[2], timeValues[3], timeValues[4], timeValues[5]);
            return AttachPrototype(h, MakePlainDateTimeParts(ctx, h, date.Year, date.Month, date.Day,
                (int)Math.Clamp(timeValues[0], 0, 23), (int)Math.Clamp(timeValues[1], 0, 59), (int)Math.Clamp(timeValues[2], 0, 59),
                (int)Math.Clamp(timeValues[3], 0, 999), (int)Math.Clamp(timeValues[4], 0, 999), (int)Math.Clamp(timeValues[5], 0, 999),
                GetVStr(h, o, "calendarId")), pH);
        }, 1);
        AddMethod(ctx, h, pH, p, "withCalendar", (o, a) => {
            var cal = ToCalendarIdentifier(ctx, h, a.Count > 0 ? a[0] : JsValue.Undefined);
            var cur = DecodeIsoDateLong(h, o);
            return AttachPrototype(h, MakePlainDateTimeParts(ctx, h, cur.Year, cur.Month, cur.Day,
                (int)GetVNum(h, o, "hour"), (int)GetVNum(h, o, "minute"), (int)GetVNum(h, o, "second"),
                (int)GetVNum(h, o, "millisecond"), (int)GetVNum(h, o, "microsecond"), (int)GetVNum(h, o, "nanosecond"), cal), pH);
        }, 1);
        AddMethod(ctx, h, pH, p, "withPlainTime", (o, a) => {
            var tm = a.Count > 0 && a[0].Tag != JsValueTag.Undefined ? ToTemporalTimeRecord(ctx, h, a[0]) : IsoTime.Midnight;
            var cur = DecodeIsoDateLong(h, o);
            return AttachPrototype(h, MakePlainDateTimeParts(ctx, h, cur.Year, cur.Month, cur.Day,
                tm.Hour, tm.Minute, tm.Second, tm.Millisecond, tm.Microsecond, tm.Nanosecond, GetVStr(h, o, "calendarId")), pH);
        }, 0);
        AddMethod(ctx, h, pH, p, "add", (o, a) => AddDurationToPlainDateTime(ctx, h, t, o, a, pH, 1), 1);
        AddMethod(ctx, h, pH, p, "subtract", (o, a) => AddDurationToPlainDateTime(ctx, h, t, o, a, pH, -1), 1);
        AddMethod(ctx, h, pH, p, "until", (o, a) => {
            var (otherDate, otherTime) = ToTemporalDateTimeRecord(ctx, h, a.Count > 0 ? a[0] : JsValue.Undefined);
            var s = GetDifferenceSettings(ctx, h, a, 1, DateTimeDiffUnits, "nanosecond", "day");
            return AttachTemporalPrototypeByName(ctx, h, t, "Duration",
                DifferencePlainDateTimesRounded(ctx, h, DecodeIsoDateLong(h, o), DecodeTimeOfDayNs(h, o), otherDate, otherTime.ToNanosecondsOfDay(), CalId(h, o), s));
        }, 1);
        AddMethod(ctx, h, pH, p, "since", (o, a) => {
            var (otherDate, otherTime) = ToTemporalDateTimeRecord(ctx, h, a.Count > 0 ? a[0] : JsValue.Undefined);
            var s = GetDifferenceSettings(ctx, h, a, 1, DateTimeDiffUnits, "nanosecond", "day");
            return AttachTemporalPrototypeByName(ctx, h, t, "Duration",
                DifferencePlainDateTimesRounded(ctx, h, DecodeIsoDateLong(h, o), DecodeTimeOfDayNs(h, o), otherDate, otherTime.ToNanosecondsOfDay(), CalId(h, o), s, negate: true));
        }, 1);
        AddMethod(ctx, h, pH, p, "round", (o, a) => {
            var date = DecodeIsoDateLong(h, o);
            long timeNs = DecodeTimeOfDayNs(h, o);
            var (dayCarry, roundedTimeNs) = RoundPlainDateTimeTime(ctx, h, timeNs, a);
            var resultDate = IsoMath.EpochDaysToCivil(IsoMath.ToEpochDays(date) + dayCarry);
            int hr = (int)(roundedTimeNs / 3_600_000_000_000L);
            int mi = (int)(roundedTimeNs / 60_000_000_000L % 60);
            int se = (int)(roundedTimeNs / 1_000_000_000L % 60);
            int ms = (int)(roundedTimeNs / 1_000_000L % 1000);
            int us = (int)(roundedTimeNs / 1_000L % 1000);
            int ns = (int)(roundedTimeNs % 1000);
            string cal = GetVStr(h, o, "calendarId"); if (string.IsNullOrEmpty(cal)) cal = "iso8601";
            return AttachPrototype(h, MakePlainDateTimeParts(ctx, h, resultDate.Year, resultDate.Month, resultDate.Day,
                hr, mi, se, ms, us, ns, cal), pH);
        }, 1);
        AddMethod(ctx, h, pH, p, "equals", (o, a) => {
            var (otherDate, otherTime) = ToTemporalDateTimeRecord(ctx, h, a.Count > 0 ? a[0] : JsValue.Undefined);
            return JsValue.FromBoolean(IsoMath.Compare(DecodeIsoDateLong(h, o), otherDate) == 0
                && DecodeTimeOfDayNs(h, o) == otherTime.ToNanosecondsOfDay());
        }, 1);
        AddMethod(ctx, h, pH, p, "toPlainDate", (o, _) => {
            var cur = DecodeIsoDateLong(h, o);
            return AttachTemporalPrototypeByName(ctx, h, t, "PlainDate", MakePlainDateYmd(ctx, h, cur.Year, cur.Month, cur.Day, GetVStr(h, o, "calendarId")));
        }, 0);
        AddMethod(ctx, h, pH, p, "toPlainTime", (o, _) => AttachTemporalPrototypeByName(ctx, h, t, "PlainTime", MakePlainTime(ctx, h,
            (int)GetVNum(h, o, "hour"), (int)GetVNum(h, o, "minute"), (int)GetVNum(h, o, "second"),
            (int)GetVNum(h, o, "millisecond"), (int)GetVNum(h, o, "microsecond"), (int)GetVNum(h, o, "nanosecond"))), 0);
        AddMethod(ctx, h, pH, p, "toString", (o, a) => {
            var opts = GetToStringOptions(ctx, h, a, 0, new[] { "calendarName", "fractionalSecondDigits", "roundingMode", "smallestUnit" });
            var date = DecodeIsoDateLong(h, o);
            long dayNs = DecodeTimeOfDayNs(h, o);
            long inc = PrecisionIncrementNs(opts);
            if (inc > 1)
            {
                dayNs = (long)RoundNsToIncrement(ctx, dayNs, inc, opts.RoundingMode);
                long carry = dayNs / NsPerDay;
                if (carry != 0)
                {
                    date = IsoMath.EpochDaysToCivil(IsoMath.ToEpochDays(date) + carry);
                    dayNs -= carry * NsPerDay;
                }
            }
            return JsValue.FromString($"{FormatIsoYear(date.Year)}-{date.Month:D2}-{date.Day:D2}" +
                $"T{dayNs / 3_600_000_000_000L:D2}:{dayNs / 60_000_000_000L % 60:D2}{FormatSecondsPart(dayNs, opts)}{CalendarSuffix(h, o, opts)}");
        }, 0);
        AddMethod(ctx, h, pH, p, "toLocaleString", (o, a) => {
            var locale = a.Count > 0 ? ToStrArg(ctx, a[0]) : string.Empty;
            var options = ParseDateTimeFormatOptions(locale, ctx, h, a.Count > 1 ? a[1] : JsValue.Undefined);
            try { IntlDateTimeFormatting.ValidateOptions(options); } catch (InvalidOperationException) { throw new JsThrownException(ctx.CreateTypeError("dateStyle/timeStyle conflicts with explicit component options.")); }
            try { IntlDateTimeFormatting.ValidateTemporalCalendar(GetVStr(h, o, "calendarId"), options); } catch (InvalidOperationException) { throw new JsThrownException(ctx.CreateRangeError("calendar mismatch")); }
            var culture = IntlDateTimeFormatting.ResolveCulture(locale);
            var dt = DecodeIsoDateLong(h, o);
            var result = IntlDateTimeFormatting.FormatPlainDateTimeParts(
                dt.Year, dt.Month, dt.Day,
                (int)GetVNum(h, o, "hour"), (int)GetVNum(h, o, "minute"), (int)GetVNum(h, o, "second"),
                (int)GetVNum(h, o, "millisecond"), (int)GetVNum(h, o, "microsecond"), (int)GetVNum(h, o, "nanosecond"),
                culture, options);
            return JsValue.FromString(result.Text);
        }, 0);
        AddMethod(ctx, h, pH, p, "toJSON", (o, _) => FormatPlainDateTime(h, o), 0);
        AddMethod(ctx, h, pH, p, "toZonedDateTime", (o, a) => {
            if (a.Count == 0 || a[0].Tag != JsValueTag.String)
                throw new JsThrownException(ctx.CreateTypeError("toZonedDateTime requires a time zone string."));
            _ = GetDisambiguationOption(ctx, h, a, 1);
            string tz = CanonicalizeTimeZoneId(ctx, a[0].AsString());
            var time = new IsoTime((int)GetVNum(h, o, "hour"), (int)GetVNum(h, o, "minute"), (int)GetVNum(h, o, "second"),
                (int)GetVNum(h, o, "millisecond"), (int)GetVNum(h, o, "microsecond"), (int)GetVNum(h, o, "nanosecond"));
            return AttachTemporalPrototypeByName(ctx, h, t, "ZonedDateTime", MakeZonedDateTimeNsBig(ctx, h,
                TemporalTimeZones.EpochNsFromWallBig(tz, DecodeIsoDateLong(h, o), time), tz, GetVStr(h, o, "calendarId")));
        }, 1);
        AddMethod(ctx, h, pH, p, "valueOf", (_, _2) => throw new JsThrownException(ctx.CreateTypeError("valueOf throws.")), 0);
        var c = h.GetObject(cH);
        AddStatic(ctx, h, cH, c, "from", a => {
            if (a.Count == 0) throw new JsThrownException(ctx.CreateTypeError("PlainDateTime.from requires at least 1 argument."));
            var overflow = GetOverflowOption(ctx, h, a, 1);
            var arg = a[0];
            if (arg.Tag == JsValueTag.String)
            {
                var s = arg.AsString();
                // ToTemporalDateTime: AnnotatedDateTime without UTC designator;
                // the time part is optional (midnight when absent).
                if (!TemporalIsoParser.TryParseDateTime(s, out var parsed, out var parseError) || parsed.HasUtcDesignator)
                    throw new JsThrownException(ctx.CreateRangeError($"'{s}' is not a valid ISO string for PlainDateTime: {parseError}"));
                var parsedCal = CalendarFromAnnotation(ctx, parsed.Calendar);
                if (!IsoMath.IsoDateWithinLimits(new IsoDate(parsed.Year, parsed.Month, parsed.Day)))
                    throw new JsThrownException(ctx.CreateRangeError("Date is outside the supported Temporal range."));
                var tm = parsed.HasTime ? parsed.Time : IsoTime.Midnight;
                return AttachPrototype(h, MakePlainDateTimeParts(ctx, h, parsed.Year, parsed.Month, parsed.Day,
                    tm.Hour, tm.Minute, tm.Second, tm.Millisecond, tm.Microsecond, tm.Nanosecond, parsedCal), pH);
            }
            if (arg.Tag == JsValueTag.Object)
            {
                var obj = h.GetObject(arg.AsObjectHandle());
                // ToTemporalDateTime step 2: check internal slots for fast path
                // (read internal data, never observable getters).
                if (TryGetInternalData(h, obj, out var idata))
                {
                    if (HasOwn(h, idata, "year") && HasOwn(h, idata, "day"))
                    {
                        // PlainDateTime or ZonedDateTime — copy date + time from internal slots.
                        return AttachPrototype(h, MakePlainDateTimeParts(ctx, h,
                            ToSafeInt(GetVNum(h, obj, "year")), ToSafeInt(GetVNum(h, obj, "month")), ToSafeInt(GetVNum(h, obj, "day")),
                            HasOwn(h, idata, "hour") ? ToSafeInt(GetVNum(h, obj, "hour")) : 0,
                            HasOwn(h, idata, "minute") ? ToSafeInt(GetVNum(h, obj, "minute")) : 0,
                            HasOwn(h, idata, "second") ? ToSafeInt(GetVNum(h, obj, "second")) : 0,
                            HasOwn(h, idata, "millisecond") ? ToSafeInt(GetVNum(h, obj, "millisecond")) : 0,
                            HasOwn(h, idata, "microsecond") ? ToSafeInt(GetVNum(h, obj, "microsecond")) : 0,
                            HasOwn(h, idata, "nanosecond") ? ToSafeInt(GetVNum(h, obj, "nanosecond")) : 0,
                            GetVStr(h, obj, "calendarId")), pH);
                    }
                    if (HasOwn(h, idata, "y") && HasOwn(h, idata, "d"))
                    {
                        // PlainDate — extract date, use midnight for time.
                        var iso = DecodeIsoDate(h, obj);
                        return AttachPrototype(h, MakePlainDateTimeParts(ctx, h,
                            iso.Year, iso.Month, iso.Day, 0, 0, 0, 0, 0, 0,
                            GetVStr(h, obj, "calendarId")), pH);
                    }
                    // PlainYearMonth, PlainMonthDay, PlainTime, Duration, Instant: not date-time-like.
                    throw new JsThrownException(ctx.CreateTypeError("Cannot convert this Temporal object to a PlainDateTime."));
                }
                if (IsTemporalInstance(h, arg, pH))
                {
                    return AttachPrototype(h, MakePlainDateTimeParts(ctx, h,
                        ToSafeInt(GetVNum(h, obj, "year")), ToSafeInt(GetVNum(h, obj, "month")), ToSafeInt(GetVNum(h, obj, "day")),
                        ToSafeInt(GetVNum(h, obj, "hour")), ToSafeInt(GetVNum(h, obj, "minute")), ToSafeInt(GetVNum(h, obj, "second")),
                        ToSafeInt(GetVNum(h, obj, "millisecond")), ToSafeInt(GetVNum(h, obj, "microsecond")), ToSafeInt(GetVNum(h, obj, "nanosecond")),
                        GetVStr(h, obj, "calendarId")), pH);
                }
                // Property bag: year (or era+eraYear) + (month|monthCode) + day; time fields optional.
                string cal = GetCalendarFromFields(ctx, h, arg);
                var calSys = CalendarMath.Get(cal);
                double y = 0, mo = 0, d = 0;
                int nativeYear = 0, nativeMonth = 0, nativeDay = 0;
                if (calSys is null)
                {
                    if (!TryGetField(ctx, h, arg, "year", out var yearValue))
                        throw new JsThrownException(ctx.CreateTypeError("PlainDateTime.from: year is required."));
                    y = ToIntegerWithTruncation(ctx, yearValue);
                    mo = GetMonthFromFields(ctx, h, arg);
                    if (!TryGetField(ctx, h, arg, "day", out var dayValue))
                        throw new JsThrownException(ctx.CreateTypeError("PlainDateTime.from: day is required."));
                    d = ToIntegerWithTruncation(ctx, dayValue);
                }
                else
                {
                    (nativeYear, nativeMonth, nativeDay) = ResolveCalendarDateFields(ctx, h, arg, calSys, null, requireDay: true);
                }
                double hr = TryGetField(ctx, h, arg, "hour", out var hv) ? ToIntegerWithTruncation(ctx, hv) : 0;
                double mi = TryGetField(ctx, h, arg, "minute", out var miv) ? ToIntegerWithTruncation(ctx, miv) : 0;
                double se = TryGetField(ctx, h, arg, "second", out var sev) ? ToIntegerWithTruncation(ctx, sev) : 0;
                double ms = TryGetField(ctx, h, arg, "millisecond", out var msv) ? ToIntegerWithTruncation(ctx, msv) : 0;
                double us = TryGetField(ctx, h, arg, "microsecond", out var usv) ? ToIntegerWithTruncation(ctx, usv) : 0;
                double ns = TryGetField(ctx, h, arg, "nanosecond", out var nsv) ? ToIntegerWithTruncation(ctx, nsv) : 0;
                IsoDate date;
                if (calSys is null)
                {
                    if (y is < -999_999 or > 999_999)
                        throw new JsThrownException(ctx.CreateRangeError("Year is out of the supported range."));
                    date = RegulateIsoDate(ctx, y, mo, d, overflow);
                }
                else
                {
                    if (!calSys.TryResolveToIso(nativeYear, nativeMonth, nativeDay, overflow, out date))
                        throw new JsThrownException(ctx.CreateRangeError("Date is invalid for the calendar or outside the supported range."));
                }
                if (overflow == "constrain")
                {
                    hr = Math.Clamp(hr, 0, 23); mi = Math.Clamp(mi, 0, 59); se = Math.Clamp(se, 0, 59);
                    ms = Math.Clamp(ms, 0, 999); us = Math.Clamp(us, 0, 999); ns = Math.Clamp(ns, 0, 999);
                }
                else
                {
                    ValidateTime(ctx, hr, mi, se, ms, us, ns);
                }
                return AttachPrototype(h, MakePlainDateTimeParts(ctx, h, date.Year, date.Month, date.Day,
                    (int)hr, (int)mi, (int)se, (int)ms, (int)us, (int)ns, cal), pH);
            }
            throw new JsThrownException(ctx.CreateTypeError("PlainDateTime.from: argument must be a string, PlainDateTime, or property bag."));
        }, 1);
        AddStatic(ctx, h, cH, c, "compare", a => {
            var (d1, t1) = ToTemporalDateTimeRecord(ctx, h, a.Count > 0 ? a[0] : JsValue.Undefined);
            var (d2, t2) = ToTemporalDateTimeRecord(ctx, h, a.Count > 1 ? a[1] : JsValue.Undefined);
            int dateCmp = IsoMath.Compare(d1, d2);
            if (dateCmp != 0) return JsValue.FromNumber(dateCmp);
            long n1 = t1.ToNanosecondsOfDay(), n2 = t2.ToNanosecondsOfDay();
            return JsValue.FromNumber(n1 < n2 ? -1 : n1 > n2 ? 1 : 0);
        }, 2);
    }

    // ─── Temporal.PlainYearMonth ───────────────────────────
    private static JsValue ConstructPlainYearMonth(IBuiltinContext ctx, JsHeap h, IReadOnlyList<JsValue> args)
    {
        // new Temporal.PlainYearMonth(year, month [, calendar [, referenceISODay]])
        double y = ArgInt(ctx, args, 0);
        double m = ArgInt(ctx, args, 1);
        string cal = CalendarArg(ctx, args, 2);
        double refDay = ArgIntOr(ctx, args, 3, 1);
        if (y is < -999_999 or > 999_999 || m is < 1 or > 12 || refDay is < 1 or > 31
            || !IsoMath.IsValidIsoDate((int)y, (int)m, (int)refDay)
            || !IsoMath.IsoDateWithinLimits(new IsoDate((int)y, (int)m, (int)refDay)))
            throw new JsThrownException(ctx.CreateRangeError("Invalid ISO year-month."));
        return MakePlainYearMonth(ctx, h, (int)y, (int)m, cal, (int)refDay);
    }

    private void InstallPlainYearMonth(IBuiltinContext ctx, JsObject t, ObjectHandle tH, JsHeap h)
    {
        var (cH, pH) = MakeCtor(ctx, h, t, tH, "PlainYearMonth", 2, true,
            (cctx, hh, a) => ConstructPlainYearMonth(cctx, hh, a));
        var p = h.GetObject(pH);
        AddGetter(ctx, h, pH, p, "year", o => { var iso = DecodeYearMonthIso(h, o); return JsValue.FromNumber(CalFields(CalId(h, o), iso)?.Year ?? iso.Year); });
        AddGetter(ctx, h, pH, p, "month", o => { var iso = DecodeYearMonthIso(h, o); return JsValue.FromNumber(CalFields(CalId(h, o), iso)?.Month ?? iso.Month); });
        AddGetter(ctx, h, pH, p, "monthCode", o => { var iso = DecodeYearMonthIso(h, o); return JsValue.FromString(CalFields(CalId(h, o), iso)?.MonthCode ?? $"M{iso.Month:D2}"); });
        AddGetter(ctx, h, pH, p, "calendarId", o => { var cid = GetVStr(h, o, "calendarId"); return JsValue.FromString(string.IsNullOrEmpty(cid) ? "iso8601" : cid); });
        AddGetter(ctx, h, pH, p, "daysInMonth", o => { var iso = DecodeYearMonthIso(h, o); return JsValue.FromNumber(CalFields(CalId(h, o), iso)?.DaysInMonth ?? IsoMath.DaysInMonth(iso.Year, iso.Month)); });
        AddGetter(ctx, h, pH, p, "daysInYear", o => { var iso = DecodeYearMonthIso(h, o); return JsValue.FromNumber(CalFields(CalId(h, o), iso)?.DaysInYear ?? IsoMath.DaysInYear(iso.Year)); });
        AddGetter(ctx, h, pH, p, "monthsInYear", o => { var iso = DecodeYearMonthIso(h, o); return JsValue.FromNumber(CalFields(CalId(h, o), iso)?.MonthsInYear ?? 12); });
        AddGetter(ctx, h, pH, p, "inLeapYear", o => { var iso = DecodeYearMonthIso(h, o); return JsValue.FromBoolean(CalFields(CalId(h, o), iso)?.InLeapYear ?? IsoMath.IsLeapYear(iso.Year)); });
        AddGetter(ctx, h, pH, p, "era", o => EraValue(CalId(h, o), DecodeYearMonthIso(h, o)));
        AddGetter(ctx, h, pH, p, "eraYear", o => EraYearValue(CalId(h, o), DecodeYearMonthIso(h, o)));
        AddMethod(ctx, h, pH, p, "with", (o, a) => {
            if (a.Count < 1 || a[0].Tag != JsValueTag.Object) throw new JsThrownException(ctx.CreateTypeError("PlainYearMonth.with: argument must be an object."));
            var bagValue = a[0];
            // RejectObjectWithCalendarOrTimeZone: read calendar and timeZone first (observable order).
            bool hasCal = TryGetField(ctx, h, bagValue, "calendar", out _);
            bool hasCalId = TryGetField(ctx, h, bagValue, "calendarId", out _);
            bool hasTz = TryGetField(ctx, h, bagValue, "timeZone", out _);
            if (hasCal || hasCalId || hasTz)
                throw new JsThrownException(ctx.CreateTypeError("calendar and timeZone cannot be changed here; use withCalendar/withTimeZone."));
            string cal = CalId(h, o);
            bool hasYear = TryGetField(ctx, h, bagValue, "year", out _);
            bool hasMonth = TryGetField(ctx, h, bagValue, "month", out _);
            bool hasMonthCode = TryGetField(ctx, h, bagValue, "monthCode", out _);
            bool hasEra = TryGetField(ctx, h, bagValue, "era", out _);
            bool hasEraYear = TryGetField(ctx, h, bagValue, "eraYear", out _);
            if (!hasYear && !hasMonth && !hasMonthCode && !hasEra && !hasEraYear)
                throw new JsThrownException(ctx.CreateTypeError("with: at least one temporal field is required."));
            var cur = DecodeYearMonthIso(h, o);
            var baseFields = CalFields(cal, cur) ?? new CalendarFields(null, null, cur.Year, cur.Month, $"M{cur.Month:D2}", cur.Day, 0, 0, 0, 12, false);
            var iso = ResolveDateBagToIso(ctx, h, bagValue, cal, a, 1, baseFields, requireDay: false, readDay: false, isWith: true);
            iso = YearMonthReferenceIso(cal, iso);
            return AttachPrototype(h, MakePlainYearMonth(ctx, h, iso.Year, iso.Month, cal, iso.Day), pH);
        }, 1);
        AddMethod(ctx, h, pH, p, "add", (o, a) => {
            var dur = ToTemporalDurationRecord(ctx, h, a.Count > 0 ? a[0] : JsValue.Undefined);
            _ = GetOverflowOption(ctx, h, a, 1);
            // PlainYearMonth.add anchors at the first day of the month, so the day never overflows.
            var res = AddDateInCalendar(CalId(h, o), DecodeYearMonthIso(h, o), dur.years, dur.months, dur.weeks, dur.days, constrain: true, out _);
            res = YearMonthReferenceIso(CalId(h, o), res);
            return AttachPrototype(h, MakePlainYearMonth(ctx, h, res.Year, res.Month, GetVStr(h, o, "calendarId"), res.Day), pH);
        }, 1);
        AddMethod(ctx, h, pH, p, "subtract", (o, a) => {
            var dur = ToTemporalDurationRecord(ctx, h, a.Count > 0 ? a[0] : JsValue.Undefined);
            _ = GetOverflowOption(ctx, h, a, 1);
            var res = AddDateInCalendar(CalId(h, o), DecodeYearMonthIso(h, o), -dur.years, -dur.months, -dur.weeks, -dur.days, constrain: true, out _);
            res = YearMonthReferenceIso(CalId(h, o), res);
            return AttachPrototype(h, MakePlainYearMonth(ctx, h, res.Year, res.Month, GetVStr(h, o, "calendarId"), res.Day), pH);
        }, 1);
        AddMethod(ctx, h, pH, p, "until", (o, a) => {
            var (oy, om, od, otherCal) = ToTemporalYearMonthRecord(ctx, h, a.Count > 0 ? a[0] : JsValue.Undefined);
            RequireMatchingCalendar(ctx, CalId(h, o), otherCal);
            var s = GetDifferenceSettings(ctx, h, a, 1, YearMonthDiffUnits, "month", "year");
            var (yy, mm, _, _) = DifferenceDateDuration(CalId(h, o), DecodeYearMonthIso(h, o), new IsoDate(oy, om, od), s.Largest);
            return AttachTemporalPrototypeByName(ctx, h, t, "Duration",
                MakeDuration(ctx, h, yy, mm, 0, 0, 0, 0, 0, 0, 0, 0));
        }, 1);
        AddMethod(ctx, h, pH, p, "since", (o, a) => {
            var (oy, om, od, otherCal) = ToTemporalYearMonthRecord(ctx, h, a.Count > 0 ? a[0] : JsValue.Undefined);
            RequireMatchingCalendar(ctx, CalId(h, o), otherCal);
            var s = GetDifferenceSettings(ctx, h, a, 1, YearMonthDiffUnits, "month", "year");
            var (yy, mm, _, _) = DifferenceDateDuration(CalId(h, o), DecodeYearMonthIso(h, o), new IsoDate(oy, om, od), s.Largest);
            return AttachTemporalPrototypeByName(ctx, h, t, "Duration",
                MakeDuration(ctx, h, -yy, -mm, 0, 0, 0, 0, 0, 0, 0, 0));
        }, 1);
        AddMethod(ctx, h, pH, p, "equals", (o, a) => {
            var (oy, om, od, ocal) = ToTemporalYearMonthRecord(ctx, h, a.Count > 0 ? a[0] : JsValue.Undefined);
            var selfCal = GetVStr(h, o, "calendarId");
            bool calsEqual = (string.IsNullOrEmpty(selfCal) ? "iso8601" : selfCal) == (string.IsNullOrEmpty(ocal) ? "iso8601" : ocal);
            var self = DecodeYearMonthIso(h, o);
            return JsValue.FromBoolean(self.Year == oy && self.Month == om && self.Day == od && calsEqual);
        }, 1);
        AddMethod(ctx, h, pH, p, "toPlainDate", (o, a) => {
            int day = 1;
            if (a.Count > 0 && a[0].Tag == JsValueTag.Object && TryGetField(ctx, h, a[0], "day", out var dv))
                day = ToSafeInt(ToIntegerWithTruncation(ctx, dv));
            string cal = CalId(h, o);
            var iso = DecodeYearMonthIso(h, o);
            var sys = CalendarMath.Get(cal);
            if (sys is not null)
            {
                sys.ToNative(iso, out int cy, out int cmo, out _);
                if (!sys.TryResolveToIso(cy, cmo, day, "constrain", out iso))
                    throw new JsThrownException(ctx.CreateRangeError("Invalid day for the calendar month."));
            }
            else
            {
                int dd = Math.Clamp(day, 1, IsoMath.DaysInMonth(iso.Year, iso.Month));
                iso = new IsoDate(iso.Year, iso.Month, dd);
            }
            return AttachTemporalPrototypeByName(ctx, h, t, "PlainDate", MakePlainDateYmd(ctx, h, iso.Year, iso.Month, iso.Day, cal));
        }, 1);
        AddMethod(ctx, h, pH, p, "toString", (o, a) => {
            var opts = GetToStringOptions(ctx, h, a, 0, new[] { "calendarName" });
            var iso = DecodeYearMonthIso(h, o);
            string suffix = CalendarSuffix(h, o, opts);
            // With a calendar annotation the reference ISO day is included.
            string day = suffix.Length > 0 ? $"-{iso.Day:D2}" : "";
            return JsValue.FromString($"{FormatIsoYear(iso.Year)}-{iso.Month:D2}{day}{suffix}");
        }, 0);
        AddMethod(ctx, h, pH, p, "toJSON", (o, _) => FormatPlainYearMonth(h, o), 0);
        AddMethod(ctx, h, pH, p, "toLocaleString", (o, a) => {
            RequireNoTimeStyle(ctx, h, a);
            var locale = a.Count > 0 ? ToStrArg(ctx, a[0]) : string.Empty;
            var options = ParseDateTimeFormatOptions(locale, ctx, h, a.Count > 1 ? a[1] : JsValue.Undefined);
            try { IntlDateTimeFormatting.ValidateOptions(options); } catch (InvalidOperationException) { throw new JsThrownException(ctx.CreateTypeError("dateStyle/timeStyle conflicts with explicit component options.")); }
            try { IntlDateTimeFormatting.ValidateTemporalCalendar(GetVStr(h, o, "calendarId"), options, allowIsoCalendar: false); } catch (InvalidOperationException) { throw new JsThrownException(ctx.CreateRangeError("calendar mismatch")); }
            var culture = IntlDateTimeFormatting.ResolveCulture(locale);
            int y = (int)GetVNum(h, o, "y");
            int m = (int)GetVNum(h, o, "m");
            // PlainYearMonth has a reference ISO day; use that for day-of-week
            int d = (int)(GetVNum(h, o, "d"));
            var result = IntlDateTimeFormatting.FormatDateOnly(y, m, d, culture, options, defaultIncludesDay: false);
            return JsValue.FromString(result.Text);
        }, 0);
        AddMethod(ctx, h, pH, p, "valueOf", (_, _2) => throw new JsThrownException(ctx.CreateTypeError("valueOf throws.")), 0);
        var c = h.GetObject(cH);
        AddStatic(ctx, h, cH, c, "from", a => {
            if (a.Count == 0) throw new JsThrownException(ctx.CreateTypeError("PlainYearMonth.from requires at least 1 argument."));
            var overflow = GetOverflowOption(ctx, h, a, 1);
            var arg = a[0];
            if (arg.Tag == JsValueTag.String)
            {
                var s = arg.AsString();
                if (!TemporalIsoParser.TryParseYearMonth(s, out var parsed, out var parseError) || parsed.HasUtcDesignator)
                    throw new JsThrownException(ctx.CreateRangeError($"'{s}' is not a valid ISO string for PlainYearMonth: {parseError}"));
                var parsedCal = CalendarFromAnnotation(ctx, parsed.Calendar);
                if (!IsoMath.IsoDateWithinLimits(new IsoDate(parsed.Year, parsed.Month, 1)))
                    throw new JsThrownException(ctx.CreateRangeError("Year-month is outside the supported Temporal range."));
                var refIso = YearMonthReferenceIso(parsedCal, new IsoDate(parsed.Year, parsed.Month, Math.Max(1, parsed.Day)));
                return AttachPrototype(h, MakePlainYearMonth(ctx, h, refIso.Year, refIso.Month, parsedCal, refIso.Day), pH);
            }
            if (arg.Tag == JsValueTag.Object)
            {
                var obj = h.GetObject(arg.AsObjectHandle());
                if (IsTemporalInstance(h, arg, pH))
                {
                    var iso = DecodeYearMonthIso(h, obj);
                    return AttachPrototype(h, MakePlainYearMonth(ctx, h, iso.Year, iso.Month, GetVStr(h, obj, "calendarId"), iso.Day), pH);
                }
                // Property bag: year (or era+eraYear) + (month|monthCode) required.
                string cal = GetCalendarFromFields(ctx, h, arg);
                var ymSys = CalendarMath.Get(cal);
                if (ymSys is not null)
                {
                    var (cy, cmo, _) = ResolveCalendarDateFields(ctx, h, arg, ymSys, null, requireDay: false);
                    if (!ymSys.TryResolveToIso(cy, cmo, 1, overflow, out var ymIso))
                        throw new JsThrownException(ctx.CreateRangeError("Year-month is invalid for the calendar or outside the supported range."));
                    return AttachPrototype(h, MakePlainYearMonth(ctx, h, ymIso.Year, ymIso.Month, cal, ymIso.Day), pH);
                }
                if (!TryGetField(ctx, h, arg, "year", out var yearValue))
                    throw new JsThrownException(ctx.CreateTypeError("PlainYearMonth.from: year is required."));
                double y2 = ToIntegerWithTruncation(ctx, yearValue);
                double m2 = GetMonthFromFields(ctx, h, arg);
                if (y2 is < -999_999 or > 999_999)
                    throw new JsThrownException(ctx.CreateRangeError("Year is out of the supported range."));
                int month2;
                if (overflow == "constrain")
                    month2 = (int)Math.Clamp(m2, 1, 12);
                else if (m2 is < 1 or > 12)
                    throw new JsThrownException(ctx.CreateRangeError("Invalid ISO year-month."));
                else
                    month2 = (int)m2;
                return AttachPrototype(h, MakePlainYearMonth(ctx, h, (int)y2, month2, cal), pH);
            }
            throw new JsThrownException(ctx.CreateTypeError("PlainYearMonth.from: argument must be a string or property bag."));
        }, 1);
        AddStatic(ctx, h, cH, c, "compare", a => {
            var (y1, m1, d1, _) = ToTemporalYearMonthRecord(ctx, h, a.Count > 0 ? a[0] : JsValue.Undefined);
            var (y2, m2, d2, _) = ToTemporalYearMonthRecord(ctx, h, a.Count > 1 ? a[1] : JsValue.Undefined);
            return JsValue.FromNumber(IsoMath.Compare(new IsoDate(y1, m1, d1), new IsoDate(y2, m2, d2)));
        }, 2);
    }

    // ─── Temporal.PlainMonthDay ────────────────────────────
    private static JsValue ConstructPlainMonthDay(IBuiltinContext ctx, JsHeap h, IReadOnlyList<JsValue> args)
    {
        // new Temporal.PlainMonthDay(month, day [, calendar [, referenceISOYear]])
        double m = ArgInt(ctx, args, 0);
        double d = ArgInt(ctx, args, 1);
        string cal = CalendarArg(ctx, args, 2);
        double refYear = ArgIntOr(ctx, args, 3, 1972);
        if (refYear is < -999_999 or > 999_999 || m is < 1 or > 12 || d is < 1 or > 31
            || !IsoMath.IsValidIsoDate((int)refYear, (int)m, (int)d))
            throw new JsThrownException(ctx.CreateRangeError("Invalid ISO month-day."));
        return MakePlainMonthDay(ctx, h, (int)refYear, (int)m, (int)d, cal);
    }

    private void InstallPlainMonthDay(IBuiltinContext ctx, JsObject t, ObjectHandle tH, JsHeap h)
    {
        var (cH, pH) = MakeCtor(ctx, h, t, tH, "PlainMonthDay", 2, true,
            (cctx, hh, a) => ConstructPlainMonthDay(cctx, hh, a));
        var p = h.GetObject(pH);
        AddGetter(ctx, h, pH, p, "monthCode", o => {
            var iso = DecodeIsoDate(h, o);
            return JsValue.FromString(CalFields(CalId(h, o), iso)?.MonthCode ?? $"M{iso.Month:D2}");
        });
        AddGetter(ctx, h, pH, p, "day", o => {
            var iso = DecodeIsoDate(h, o);
            return JsValue.FromNumber(CalFields(CalId(h, o), iso)?.Day ?? iso.Day);
        });
        AddGetter(ctx, h, pH, p, "calendarId", o => { var cid = CalId(h, o); return JsValue.FromString(cid); });
        AddGetter(ctx, h, pH, p, "referenceISOYear", o => {
            var iso = DecodeIsoDate(h, o);
            return JsValue.FromNumber(iso.Year);
        });
        AddMethod(ctx, h, pH, p, "with", (o, a) => {
            if (a.Count < 1 || a[0].Tag != JsValueTag.Object) throw new JsThrownException(ctx.CreateTypeError("PlainMonthDay.with: argument must be an object."));
            var bagValue = a[0];
            bool hasCal = TryGetField(ctx, h, bagValue, "calendar", out _);
            bool hasCalId = TryGetField(ctx, h, bagValue, "calendarId", out _);
            bool hasTz = TryGetField(ctx, h, bagValue, "timeZone", out _);
            if (hasCal || hasCalId || hasTz)
                throw new JsThrownException(ctx.CreateTypeError("calendar and timeZone cannot be changed here."));

            var cur = DecodeIsoDate(h, o);
            string cal = CalId(h, o);
            bool hasMonth = TryGetField(ctx, h, bagValue, "month", out _);
            bool hasMonthCode = TryGetField(ctx, h, bagValue, "monthCode", out _);
            bool hasDay = TryGetField(ctx, h, bagValue, "day", out _);
            bool hasYear = TryGetField(ctx, h, bagValue, "year", out _);
            bool hasEra = TryGetField(ctx, h, bagValue, "era", out _);
            bool hasEraYear = TryGetField(ctx, h, bagValue, "eraYear", out _);
            if (!hasMonth && !hasMonthCode && !hasDay && !hasYear && !hasEra && !hasEraYear)
                throw new JsThrownException(ctx.CreateTypeError("with: at least one temporal field is required."));

            var baseFields = CalFields(cal, cur) ?? new CalendarFields(null, null, cur.Year, cur.Month, $"M{cur.Month:D2}", cur.Day, 0, 0, 0, 12, false);
            var iso = ResolveDateBagToIso(ctx, h, bagValue, cal, a, 1, baseFields, isWith: true, requireYear: false);
            return AttachPrototype(h, MakePlainMonthDay(ctx, h, iso.Year, iso.Month, iso.Day, cal), pH);
        }, 1);
        AddMethod(ctx, h, pH, p, "equals", (o, a) => {
            if (a.Count < 1) return JsValue.FromBoolean(false);
            try
            {
                var (other, otherCal) = ToTemporalMonthDayRecord(ctx, h, a[0], a, 1);
                var self = DecodeIsoDate(h, o);
                var selfCal = CalId(h, o);
                bool calsEqual = selfCal == otherCal;
                return JsValue.FromBoolean(IsoMath.Compare(self, other) == 0 && calsEqual);
            }
            catch
            {
                return JsValue.FromBoolean(false);
            }
        }, 1);
        AddMethod(ctx, h, pH, p, "toString", (o, a) => {
            var opts = GetToStringOptions(ctx, h, a, 0, new[] { "calendarName" });
            var iso = DecodeIsoDate(h, o);
            string suffix = CalendarSuffix(h, o, opts);
            if (suffix.Length > 0)
            {
                return JsValue.FromString($"{FormatIsoYear(iso.Year)}-{iso.Month:D2}-{iso.Day:D2}{suffix}");
            }
            else
            {
                return JsValue.FromString($"{iso.Month:D2}-{iso.Day:D2}");
            }
        }, 0);
        AddMethod(ctx, h, pH, p, "toJSON", (o, _) => FormatPlainMonthDay(h, o), 0);
        AddMethod(ctx, h, pH, p, "toLocaleString", (o, a) => {
            RequireNoTimeStyle(ctx, h, a);
            var locale = a.Count > 0 ? ToStrArg(ctx, a[0]) : string.Empty;
            var options = ParseDateTimeFormatOptions(locale, ctx, h, a.Count > 1 ? a[1] : JsValue.Undefined);
            try { IntlDateTimeFormatting.ValidateOptions(options); } catch (InvalidOperationException) { throw new JsThrownException(ctx.CreateTypeError("dateStyle/timeStyle conflicts with explicit component options.")); }
            try { IntlDateTimeFormatting.ValidateTemporalCalendar(CalId(h, o), options, allowIsoCalendar: false); } catch (InvalidOperationException) { throw new JsThrownException(ctx.CreateRangeError("calendar mismatch")); }
            var culture = IntlDateTimeFormatting.ResolveCulture(locale);
            var iso = DecodeIsoDate(h, o);
            var result = IntlDateTimeFormatting.FormatDateOnly(iso.Year, iso.Month, iso.Day, culture, options, defaultIncludesYear: false);
            return JsValue.FromString(result.Text);
        }, 0);
        AddMethod(ctx, h, pH, p, "valueOf", (_, _2) => throw new JsThrownException(ctx.CreateTypeError("valueOf throws.")), 0);
        var c = h.GetObject(cH);
        AddStatic(ctx, h, cH, c, "from", a => {
            if (a.Count == 0) throw new JsThrownException(ctx.CreateTypeError("PlainMonthDay.from requires at least 1 argument."));
            var arg = a[0];
            var (iso, cal) = ToTemporalMonthDayRecord(ctx, h, arg, a, 1);
            return AttachPrototype(h, MakePlainMonthDay(ctx, h, iso.Year, iso.Month, iso.Day, cal), pH);
        }, 1);
    }

    // ─── Temporal.ZonedDateTime ────────────────────────────

    /// <summary>ToTemporalTimeZoneIdentifier: identifier or ISO string with [tz] → canonical form, or RangeError.</summary>
    private static string CanonicalizeTimeZoneId(IBuiltinContext ctx, string id)
    {
        if (TemporalTimeZones.TryCanonicalize(id, out var canonical, out var fixedOffsetNs))
            return fixedOffsetNs.HasValue ? canonical : id;

        // An ISO date-time string also names a zone: its [tz] annotation,
        // or UTC for a 'Z' designator, or its numeric offset.
        if (TemporalIsoParser.TryParseDateTime(id, out var parsed, out _))
        {
            if (parsed.TimeZoneAnnotation is { } ann)
            {
                if (TemporalTimeZones.TryCanonicalize(ann, out canonical, out _))
                    return canonical;
            }
            else if (parsed.HasUtcDesignator)
            {
                return "UTC";
            }
            else if (parsed.HasOffset)
            {
                if (parsed.OffsetSubMinuteSyntax)
                {
                    throw new JsThrownException(ctx.CreateRangeError("Time zone offset has sub-minute precision."));
                }
                return TemporalTimeZones.FormatOffset(parsed.OffsetNanoseconds);
            }
        }

        throw new JsThrownException(ctx.CreateRangeError($"'{id}' is not a valid time zone."));
    }

    private static string CanonicalTimeZoneKey(IBuiltinContext ctx, string id)
    {
        if (TemporalTimeZones.TryCanonicalize(id, out var canonical, out _))
            return canonical;
        throw new JsThrownException(ctx.CreateRangeError($"'{id}' is not a valid time zone."));
    }

    private static bool StringOffsetMatchesTimeZone(
        string timeZone,
        long timeZoneOffsetNs,
        long parsedOffsetNs,
        bool offsetHasSubMinuteSyntax)
    {
        if (timeZoneOffsetNs == parsedOffsetNs)
            return true;

        if (offsetHasSubMinuteSyntax || timeZone.Length == 0 || timeZone[0] is '+' or '-')
            return false;

        const long minuteNs = 60_000_000_000L;
        long roundedMinutes = timeZoneOffsetNs >= 0
            ? (timeZoneOffsetNs + minuteNs / 2) / minuteNs
            : (timeZoneOffsetNs - minuteNs / 2) / minuteNs;
        return roundedMinutes * minuteNs == parsedOffsetNs;
    }

    /// <summary>ToTemporalZonedDateTime: instance, ISO string with [tz], or property bag → epoch ns + zone + calendar.</summary>
    private static (System.Numerics.BigInteger EpochNs, string Tz, string Calendar) ToTemporalZonedRecord(
        IBuiltinContext ctx,
        JsHeap h,
        JsValue arg,
        string offsetOption = "reject",
        string disambiguation = "compatible")
    {
        if (arg.Tag == JsValueTag.String)
        {
            var s = arg.AsString();
            if (!TemporalIsoParser.TryParseDateTime(s, out var parsed, out var parseError))
                throw new JsThrownException(ctx.CreateRangeError($"'{s}' is not a valid ISO string for ZonedDateTime: {parseError}"));
            if (parsed.TimeZoneAnnotation is null)
                throw new JsThrownException(ctx.CreateRangeError($"'{s}' has no time zone annotation; required for ZonedDateTime."));
            string tz = CanonicalizeTimeZoneId(ctx, parsed.TimeZoneAnnotation);
            string cal = CalendarFromAnnotation(ctx, parsed.Calendar);
            var date = new IsoDate(parsed.Year, parsed.Month, parsed.Day);
            var time = parsed.HasTime ? parsed.Time : IsoTime.Midnight;
            System.Numerics.BigInteger epochNs;
            if (parsed.HasUtcDesignator)
            {
                long days = IsoMath.ToEpochDays(date);
                epochNs = new System.Numerics.BigInteger(days) * NsPerDay + time.ToNanosecondsOfDay();
            }
            else if (parsed.HasOffset && offsetOption != "ignore")
            {
                long days = IsoMath.ToEpochDays(date);
                epochNs = new System.Numerics.BigInteger(days) * NsPerDay + time.ToNanosecondsOfDay() - parsed.OffsetNanoseconds;
                long epochNsClamped = epochNs >= long.MinValue && epochNs <= long.MaxValue
                    ? (long)epochNs
                    : (epochNs < 0 ? long.MinValue : long.MaxValue);
                long offsetNs = TemporalTimeZones.GetOffsetNs(tz, epochNsClamped);
                bool matches = StringOffsetMatchesTimeZone(
                        tz,
                        offsetNs,
                        parsed.OffsetNanoseconds,
                        parsed.OffsetSubMinuteSyntax);
                if (!matches && offsetOption == "reject")
                    throw new JsThrownException(ctx.CreateRangeError("Offset and time zone offset mismatch."));
                bool minuteRoundedNamedOffset = !parsed.OffsetSubMinuteSyntax && tz.Length > 0 && tz[0] is not ('+' or '-');
                if (offsetOption != "use" && (offsetNs != parsed.OffsetNanoseconds || minuteRoundedNamedOffset) &&
                    !TemporalTimeZones.TryResolveEpochNsFromWallBig(tz, date, time, disambiguation, out epochNs))
                    throw new JsThrownException(ctx.CreateRangeError("Wall time is ambiguous or does not exist in the time zone."));
            }
            else
            {
                if (!parsed.HasTime)
                    epochNs = TemporalTimeZones.GetStartOfDayEpochNsBig(tz, date);
                else if (!TemporalTimeZones.TryResolveEpochNsFromWallBig(tz, date, time, disambiguation, out epochNs))
                    throw new JsThrownException(ctx.CreateRangeError("Wall time is ambiguous or does not exist in the time zone."));
            }

            if (System.Numerics.BigInteger.Abs(epochNs) > MaxInstantNs)
                throw new JsThrownException(ctx.CreateRangeError("ZonedDateTime instant is outside the representable range."));

            return (epochNs, tz, cal);
        }

        if (arg.Tag == JsValueTag.Object)
        {
            var obj = h.GetObject(arg.AsObjectHandle());
            if (TryGetInternalData(h, obj, out var data) && HasOwn(h, data, "tz"))
                return (DecodeInstantNanosBig(h, obj), GetVStr(h, obj, "tz"), GetVStr(h, obj, "calendarId") is { Length: > 0 } c ? c : "iso8601");

            // Property bag: timeZone required, plus a PlainDateTime-style bag.
            string bagCal = GetCalendarFromFields(ctx, h, arg);
            if (!TryGetField(ctx, h, arg, "timeZone", out var tzValue))
                throw new JsThrownException(ctx.CreateTypeError("timeZone is required."));
            if (tzValue.Tag != JsValueTag.String)
                throw new JsThrownException(ctx.CreateTypeError("timeZone must be a string."));
            string bagTz = CanonicalizeTimeZoneId(ctx, tzValue.AsString());
            var bagSys = CalendarMath.Get(bagCal);
            IsoDate bagDate;
            if (bagSys is null)
            {
                if (!TryGetField(ctx, h, arg, "year", out var yearValue))
                    throw new JsThrownException(ctx.CreateTypeError("year is required."));
                double y = ToIntegerWithTruncation(ctx, yearValue);
                double m = GetMonthFromFields(ctx, h, arg);
                if (!TryGetField(ctx, h, arg, "day", out var dayValue))
                    throw new JsThrownException(ctx.CreateTypeError("day is required."));
                double d = ToIntegerWithTruncation(ctx, dayValue);
                // ECMA-262 §13.44: month and day must be positive integers.
                if (m < 1) throw new JsThrownException(ctx.CreateRangeError("Month must be a positive integer."));
                if (d < 1) throw new JsThrownException(ctx.CreateRangeError("Day must be a positive integer."));
                bagDate = RegulateIsoDate(ctx, y, m, d, "constrain");
            }
            else
            {
                var (cy, cmo, cd) = ResolveCalendarDateFields(ctx, h, arg, bagSys, null, requireDay: true);
                if (!bagSys.TryResolveToIso(cy, cmo, cd, "constrain", out bagDate))
                    throw new JsThrownException(ctx.CreateRangeError("Date is invalid for the calendar or outside the supported range."));
            }
            string[] timeFields = { "hour", "minute", "second", "millisecond", "microsecond", "nanosecond" };
            var tv = new double[timeFields.Length];
            for (int fi = 0; fi < timeFields.Length; fi++)
            {
                if (TryGetField(ctx, h, arg, timeFields[fi], out var fv))
                    tv[fi] = ToIntegerWithTruncation(ctx, fv);
            }

            var bagTime = new IsoTime(
                (int)Math.Clamp(tv[0], 0, 23), (int)Math.Clamp(tv[1], 0, 59), (int)Math.Clamp(tv[2], 0, 59),
                (int)Math.Clamp(tv[3], 0, 999), (int)Math.Clamp(tv[4], 0, 999), (int)Math.Clamp(tv[5], 0, 999));

            long? offsetNs = null;
            if (TryGetField(ctx, h, arg, "offset", out var offsetValue) && offsetValue.Tag != JsValueTag.Undefined)
            {
                if (offsetValue.Tag != JsValueTag.String)
                    throw new JsThrownException(ctx.CreateTypeError("offset must be a string."));
                var offsetStr = offsetValue.AsString();
                int offsetIdx = 0;
                string offsetErr = "";
                if (!TemporalIsoParser.TryParseUtcOffset(offsetStr, ref offsetIdx, subMinutePrecision: true, out var parsedOffsetNs, ref offsetErr)
                    || offsetIdx != offsetStr.Length)
                {
                    throw new JsThrownException(ctx.CreateRangeError($"'{offsetStr}' is not a valid offset string."));
                }
                offsetNs = parsedOffsetNs;
            }

            System.Numerics.BigInteger epochNs;
            if (offsetNs.HasValue && offsetOption != "ignore")
            {
                System.Numerics.BigInteger wallNs = new System.Numerics.BigInteger(IsoMath.ToEpochDays(bagDate)) * NsPerDay + bagTime.ToNanosecondsOfDay();
                epochNs = wallNs - offsetNs.Value;
                long epochNsClamped = epochNs >= long.MinValue && epochNs <= long.MaxValue
                    ? (long)epochNs
                    : (epochNs < 0 ? long.MinValue : long.MaxValue);
                long actualOffset = TemporalTimeZones.GetOffsetNs(bagTz, epochNsClamped);
                bool matches = actualOffset == offsetNs.Value;
                if (!matches && offsetOption == "reject")
                    throw new JsThrownException(ctx.CreateRangeError("Offset and time zone offset mismatch."));
                if (!matches && offsetOption == "prefer" &&
                    !TemporalTimeZones.TryResolveEpochNsFromWallBig(bagTz, bagDate, bagTime, disambiguation, out epochNs))
                    throw new JsThrownException(ctx.CreateRangeError("Wall time is ambiguous or does not exist in the time zone."));
            }
            else
            {
                if (!TemporalTimeZones.TryResolveEpochNsFromWallBig(bagTz, bagDate, bagTime, disambiguation, out epochNs))
                    throw new JsThrownException(ctx.CreateRangeError("Wall time is ambiguous or does not exist in the time zone."));
            }

            if (System.Numerics.BigInteger.Abs(epochNs) > MaxInstantNs)
                throw new JsThrownException(ctx.CreateRangeError("ZonedDateTime instant is outside the representable range."));

            return (epochNs, bagTz, bagCal);
        }

        throw new JsThrownException(ctx.CreateTypeError("Cannot convert value to a Temporal.ZonedDateTime."));
    }

    private static JsValue ConstructZonedDateTime(IBuiltinContext ctx, JsHeap h, IReadOnlyList<JsValue> args)
    {
        // new Temporal.ZonedDateTime(epochNanoseconds: BigInt, timeZone: string [, calendar])
        if (args.Count == 0 || args[0].Tag != JsValueTag.BigInt)
            throw new JsThrownException(ctx.CreateTypeError("ZonedDateTime constructor requires a BigInt epochNanoseconds argument."));
        var ns = args[0].AsBigInt();
        if (System.Numerics.BigInteger.Abs(ns) > MaxInstantNs)
            throw new JsThrownException(ctx.CreateRangeError("epochNanoseconds is outside the supported range."));
        if (args.Count < 2 || args[1].Tag != JsValueTag.String)
            throw new JsThrownException(ctx.CreateTypeError("ZonedDateTime constructor requires a time zone string."));
        string tz = CanonicalizeTimeZoneId(ctx, args[1].AsString());
        string cal = CalendarArg(ctx, args, 2);
        return MakeZonedDateTimeNsBig(ctx, h, ns, tz, cal);
    }

    /// <summary>Decode a ZonedDateTime's wall-clock time-of-day in nanoseconds.</summary>
    private static long ZonedWallTimeNs(JsHeap h, JsObject o) => DecodeTimeOfDayNs(h, o);

    /// <summary>Epoch ns of midnight (start of day) for the instance's wall date.</summary>
    private static System.Numerics.BigInteger ZonedStartOfDayNsBig(JsHeap h, JsObject o)
        => TemporalTimeZones.GetStartOfDayEpochNsBig(GetVStr(h, o, "tz"), DecodeIsoDateLong(h, o));

    private static long ZonedStartOfDayNs(JsHeap h, JsObject o)
        => ToSafeLong(ZonedStartOfDayNsBig(h, o));

    /// <summary>CreateTemporalZonedDateTime: epoch ns + zone + calendar, with the wall-clock fields cached in _v.</summary>
    private static JsValue MakeZonedDateTimeNs(IBuiltinContext ctx, JsHeap h, long epochNs, string tz, string calendarId)
        => MakeZonedDateTimeNsBig(ctx, h, new System.Numerics.BigInteger(epochNs), tz, calendarId);

    private static JsValue MakeZonedDateTimeNsBig(IBuiltinContext ctx, JsHeap h, System.Numerics.BigInteger epochNsBig, string tz, string calendarId)
    {
        long offsetNs;
        IsoDate date; IsoTime time;
        if (epochNsBig >= long.MinValue && epochNsBig <= long.MaxValue)
        {
            long ens = (long)epochNsBig;
            offsetNs = TemporalTimeZones.GetOffsetNs(tz, ens);
            (date, time) = TemporalTimeZones.WallFromEpochNs(ens, offsetNs);
        }
        else
        {
            // Outside long range: offset = 0 (no TZ data available), compute wall directly.
            offsetNs = 0;
            (date, time) = TemporalTimeZones.WallFromEpochNsBig(epochNsBig, 0);
        }
        var o = new JsObject();
        var d = new JsObject(); var dH = h.AllocateObject(d, AllocationSite.Current());
        d.SetProperty("day", JsValue.FromNumber(date.Day));
        d.SetProperty("hour", JsValue.FromNumber(time.Hour));
        d.SetProperty("microsecond", JsValue.FromNumber(time.Microsecond));
        d.SetProperty("millisecond", JsValue.FromNumber(time.Millisecond));
        d.SetProperty("minute", JsValue.FromNumber(time.Minute));
        d.SetProperty("month", JsValue.FromNumber(date.Month));
        d.SetProperty("nanosecond", JsValue.FromNumber(time.Nanosecond));
        d.SetProperty("second", JsValue.FromNumber(time.Second));
        d.SetProperty("year", JsValue.FromNumber(date.Year));
        long epochNs = 0;
        try { epochNs = (long)epochNsBig; } catch (OverflowException) { }
        d.SetProperty("epochSeconds", JsValue.FromNumber((double)(epochNsBig / 1_000_000_000)));
        d.SetProperty("epochMilliseconds", JsValue.FromNumber((double)(epochNsBig / 1_000_000)));
        d.SetProperty("epochMicroseconds", JsValue.FromNumber((double)(epochNsBig / 1_000)));
        d.SetProperty("ens", JsValue.FromNumber((double)epochNs));
        d.SetProperty("ensBig", JsValue.FromBigInt(epochNsBig));
        d.SetProperty("offsetNanoseconds", JsValue.FromNumber(offsetNs));
        d.SetProperty("tz", JsValue.FromString(tz));
        d.SetProperty("calendarId", JsValue.FromString(string.IsNullOrEmpty(calendarId) ? "iso8601" : calendarId));
        o.DefineOwnProperty("_v", new JsPropertyDescriptor(JsValue.FromObject(dH), false, false, false));
        return JsValue.FromObject(h.AllocateObject(o, AllocationSite.Current()));
    }

    private void InstallZonedDateTime(IBuiltinContext ctx, JsObject t, ObjectHandle tH, JsHeap h)
    {
        var (cH, pH) = MakeCtor(ctx, h, t, tH, "ZonedDateTime", 2, true,
            (cctx, hh, a) => ConstructZonedDateTime(cctx, hh, a));
        var p = h.GetObject(pH);
        foreach (var f in new[] { "epochMicroseconds", "epochMilliseconds", "epochSeconds", "hour", "microsecond", "millisecond", "minute", "nanosecond", "offsetNanoseconds", "second" })
            AddGetter(ctx, h, pH, p, f, o => GetV(h, o, f));
        AddGetter(ctx, h, pH, p, "epochNanoseconds", o => GetV(h, o, "ensBig"));
        AddGetter(ctx, h, pH, p, "calendarId", o => { var cid = GetVStr(h, o, "calendarId"); return JsValue.FromString(string.IsNullOrEmpty(cid) ? "iso8601" : cid); });
        AddGetter(ctx, h, pH, p, "year", o => { var iso = DecodeIsoDateLong(h, o); return JsValue.FromNumber(CalFields(CalId(h, o), iso)?.Year ?? iso.Year); });
        AddGetter(ctx, h, pH, p, "month", o => { var iso = DecodeIsoDateLong(h, o); return JsValue.FromNumber(CalFields(CalId(h, o), iso)?.Month ?? iso.Month); });
        AddGetter(ctx, h, pH, p, "day", o => { var iso = DecodeIsoDateLong(h, o); return JsValue.FromNumber(CalFields(CalId(h, o), iso)?.Day ?? iso.Day); });
        AddGetter(ctx, h, pH, p, "monthCode", o => { var iso = DecodeIsoDateLong(h, o); return JsValue.FromString(CalFields(CalId(h, o), iso)?.MonthCode ?? $"M{iso.Month:D2}"); });
        AddGetter(ctx, h, pH, p, "dayOfWeek", o => JsValue.FromNumber(IsoMath.DayOfWeek(DecodeIsoDateLong(h, o))));
        AddGetter(ctx, h, pH, p, "dayOfYear", o => { var dt = DecodeIsoDateLong(h, o); return JsValue.FromNumber(CalFields(CalId(h, o), dt)?.DayOfYear ?? IsoMath.DayOfYear(dt)); });
        AddGetter(ctx, h, pH, p, "weekOfYear", o => { var dt = DecodeIsoDateLong(h, o); return CalFields(CalId(h, o), dt) is null ? JsValue.FromNumber(IsoMath.WeekOfYear(dt).Week) : JsValue.Undefined; });
        AddGetter(ctx, h, pH, p, "yearOfWeek", o => { var dt = DecodeIsoDateLong(h, o); return CalFields(CalId(h, o), dt) is null ? JsValue.FromNumber(IsoMath.WeekOfYear(dt).Year) : JsValue.Undefined; });
        AddGetter(ctx, h, pH, p, "hoursInDay", o => {
            var date = DecodeIsoDateLong(h, o);
            string tz = GetVStr(h, o, "tz");
            long start = ToSafeLong(TemporalTimeZones.GetStartOfDayEpochNsBig(tz, date));
            long nextDays = IsoMath.ToEpochDays(date) + 1;
            long end = ToSafeLong(TemporalTimeZones.GetStartOfDayEpochNsBig(tz, IsoMath.EpochDaysToCivil(nextDays)));
            return JsValue.FromNumber((end - start) / 3_600_000_000_000.0);
        });
        AddGetter(ctx, h, pH, p, "daysInWeek", o => JsValue.FromNumber(7));
        AddGetter(ctx, h, pH, p, "daysInMonth", o => { var dt = DecodeIsoDateLong(h, o); return JsValue.FromNumber(CalFields(CalId(h, o), dt)?.DaysInMonth ?? IsoMath.DaysInMonth(dt.Year, dt.Month)); });
        AddGetter(ctx, h, pH, p, "daysInYear", o => { var dt = DecodeIsoDateLong(h, o); return JsValue.FromNumber(CalFields(CalId(h, o), dt)?.DaysInYear ?? IsoMath.DaysInYear(dt.Year)); });
        AddGetter(ctx, h, pH, p, "monthsInYear", o => { var dt = DecodeIsoDateLong(h, o); return JsValue.FromNumber(CalFields(CalId(h, o), dt)?.MonthsInYear ?? 12); });
        AddGetter(ctx, h, pH, p, "inLeapYear", o => { var dt = DecodeIsoDateLong(h, o); return JsValue.FromBoolean(CalFields(CalId(h, o), dt)?.InLeapYear ?? IsoMath.IsLeapYear(dt.Year)); });
        AddGetter(ctx, h, pH, p, "offset", o => JsValue.FromString(TemporalTimeZones.FormatOffset((long)GetVNum(h, o, "offsetNanoseconds"))));
        AddGetter(ctx, h, pH, p, "timeZoneId", o => GetV(h, o, "tz"));
        AddGetter(ctx, h, pH, p, "era", o => EraValue(CalId(h, o), DecodeIsoDateLong(h, o)));
        AddGetter(ctx, h, pH, p, "eraYear", o => EraYearValue(CalId(h, o), DecodeIsoDateLong(h, o)));
        AddMethod(ctx, h, pH, p, "with", (o, a) => {
            if (a.Count < 1 || a[0].Tag != JsValueTag.Object) throw new JsThrownException(ctx.CreateTypeError("ZonedDateTime.with: argument must be an object."));
            var bagValue = a[0];
            // Spec: if argument is a Temporal object, throw TypeError (must be a property bag).
            var bagObj = h.GetObject(bagValue.AsObjectHandle());
            if (bagObj.TryGetOwnProperty("_v", out _))
                throw new JsThrownException(ctx.CreateTypeError("ZonedDateTime.with: argument must be a property bag, not a Temporal object."));
            // RejectObjectWithCalendarOrTimeZone: read calendar, calendarId and timeZone first (observable order).
            bool hasCal = TryGetField(ctx, h, bagValue, "calendar", out _);
            bool hasCalId = TryGetField(ctx, h, bagValue, "calendarId", out _);
            bool hasTz = TryGetField(ctx, h, bagValue, "timeZone", out _);
            if (hasCal || hasCalId || hasTz)
                throw new JsThrownException(ctx.CreateTypeError("with: timeZone and calendar cannot be changed here; use withTimeZone/withCalendar."));
            var curDate = DecodeIsoDateLong(h, o);
            string cal = CalId(h, o);
            bool hasYear = TryGetField(ctx, h, bagValue, "year", out _);
            bool hasMonth = TryGetField(ctx, h, bagValue, "month", out _);
            bool hasMonthCode = TryGetField(ctx, h, bagValue, "monthCode", out _);
            bool hasDay = TryGetField(ctx, h, bagValue, "day", out _);
            bool hasEra = TryGetField(ctx, h, bagValue, "era", out _);
            bool hasEraYear = TryGetField(ctx, h, bagValue, "eraYear", out _);
            bool hasDateField = hasYear || hasMonth || hasMonthCode || hasDay || hasEra || hasEraYear;
            bool hasOffset = TryGetField(ctx, h, bagValue, "offset", out var offsetVal);
            if (hasOffset)
            {
                // Validate offset: must be a string in valid offset format (±HH:MM).
                if (offsetVal.Tag != JsValueTag.String)
                    throw new JsThrownException(ctx.CreateTypeError("offset must be a string."));
                var offsetStr = offsetVal.AsString();
                int oi = 0;
                string oErr = "";
                if (!TemporalIsoParser.TryParseUtcOffset(offsetStr, ref oi, subMinutePrecision: false, out _, ref oErr) || oi != offsetStr.Length)
                    throw new JsThrownException(ctx.CreateRangeError($"'{offsetStr}' is not a valid offset string."));
            }
            string[] timeFields = { "hour", "minute", "second", "millisecond", "microsecond", "nanosecond" };
            var timeValues = new double[timeFields.Length];
            bool anyTime = false;
            for (int fi = 0; fi < timeFields.Length; fi++)
            {
                if (TryGetField(ctx, h, bagValue, timeFields[fi], out var fv))
                {
                    timeValues[fi] = ToIntegerWithTruncation(ctx, fv);
                    anyTime = true;
                }
                else
                {
                    timeValues[fi] = GetVNum(h, o, timeFields[fi]);
                }
            }
            if (!hasDateField && !anyTime && !hasOffset)
                throw new JsThrownException(ctx.CreateTypeError("with: at least one temporal field is required."));
            // Validate options (spec steps 4-7): options type, disambiguation, offset, overflow.
            RequireOptionsObject(ctx, a, 1);
            _ = GetDisambiguationOption(ctx, h, a, 1);
            _ = GetOffsetOption(ctx, h, a, 1);
            var baseFields = CalFields(cal, curDate) ?? new CalendarFields(null, null, curDate.Year, curDate.Month, $"M{curDate.Month:D2}", curDate.Day, 0, 0, 0, 12, false);
            var date = ResolveDateBagToIso(ctx, h, bagValue, cal, a, 1, out var overflow, baseFields, isWith: true);
            if (overflow == "reject")
                ValidateTime(ctx, timeValues[0], timeValues[1], timeValues[2], timeValues[3], timeValues[4], timeValues[5]);
            var time = new IsoTime(
                (int)Math.Clamp(timeValues[0], 0, 23), (int)Math.Clamp(timeValues[1], 0, 59), (int)Math.Clamp(timeValues[2], 0, 59),
                (int)Math.Clamp(timeValues[3], 0, 999), (int)Math.Clamp(timeValues[4], 0, 999), (int)Math.Clamp(timeValues[5], 0, 999));
            string tz = GetVStr(h, o, "tz");
            return AttachPrototype(h, MakeZonedDateTimeNsBig(ctx, h,
                TemporalTimeZones.EpochNsFromWallBig(tz, date, time), tz, GetVStr(h, o, "calendarId")), pH);
        }, 1);
        AddMethod(ctx, h, pH, p, "withCalendar", (o, a) => {
            var cal = ToCalendarIdentifier(ctx, h, a.Count > 0 ? a[0] : JsValue.Undefined);
            return AttachPrototype(h, MakeZonedDateTimeNsBig(ctx, h, DecodeInstantNanosBig(h, o), GetVStr(h, o, "tz"), cal), pH);
        }, 1);
        AddMethod(ctx, h, pH, p, "withTimeZone", (o, a) => {
            if (a.Count == 0 || a[0].Tag != JsValueTag.String)
                throw new JsThrownException(ctx.CreateTypeError("withTimeZone: time zone must be a string."));
            var tz = CanonicalizeTimeZoneId(ctx, a[0].AsString());
            return AttachPrototype(h, MakeZonedDateTimeNsBig(ctx, h, DecodeInstantNanosBig(h, o), tz, GetVStr(h, o, "calendarId")), pH);
        }, 1);
        AddMethod(ctx, h, pH, p, "withPlainDate", (o, a) => {
            var (date, _) = ToTemporalDateRecord(ctx, h, a.Count > 0 ? a[0] : JsValue.Undefined);
            var time = new IsoTime((int)GetVNum(h, o, "hour"), (int)GetVNum(h, o, "minute"), (int)GetVNum(h, o, "second"),
                (int)GetVNum(h, o, "millisecond"), (int)GetVNum(h, o, "microsecond"), (int)GetVNum(h, o, "nanosecond"));
            string tz = GetVStr(h, o, "tz");
            return AttachPrototype(h, MakeZonedDateTimeNsBig(ctx, h,
                TemporalTimeZones.EpochNsFromWallBig(tz, date, time), tz, GetVStr(h, o, "calendarId")), pH);
        }, 1);
        AddMethod(ctx, h, pH, p, "withPlainTime", (o, a) => {
            var time = a.Count > 0 && a[0].Tag != JsValueTag.Undefined ? ToTemporalTimeRecord(ctx, h, a[0]) : IsoTime.Midnight;
            string tz = GetVStr(h, o, "tz");
            var epochNs = a.Count == 0 || a[0].Tag == JsValueTag.Undefined
                ? TemporalTimeZones.GetStartOfDayEpochNsBig(tz, DecodeIsoDateLong(h, o))
                : TemporalTimeZones.EpochNsFromWallBig(tz, DecodeIsoDateLong(h, o), time);
            return AttachPrototype(h, MakeZonedDateTimeNsBig(ctx, h,
                epochNs, tz, GetVStr(h, o, "calendarId")), pH);
        }, 0);
        AddMethod(ctx, h, pH, p, "add", (o, a) => AddDurationToZoned(ctx, h, o, a, pH, 1), 1);
        AddMethod(ctx, h, pH, p, "subtract", (o, a) => AddDurationToZoned(ctx, h, o, a, pH, -1), 1);
        AddMethod(ctx, h, pH, p, "until", (o, a) => {
            var (otherNs, otherTz, otherCal) = ToTemporalZonedRecord(ctx, h, a.Count > 0 ? a[0] : JsValue.Undefined);
            RequireMatchingCalendar(ctx, CalId(h, o), otherCal);
            RequireMatchingTimeZone(ctx, GetVStr(h, o, "tz"), otherTz);
            var s = GetDifferenceSettings(ctx, h, a, 1, DateTimeDiffUnits, "nanosecond", "hour");
            return AttachTemporalPrototypeByName(ctx, h, t, "Duration", ZdtDiff(ctx, h, o, otherNs, s.Largest, 1));
        }, 1);
        AddMethod(ctx, h, pH, p, "since", (o, a) => {
            var (otherNs, otherTz, otherCal) = ToTemporalZonedRecord(ctx, h, a.Count > 0 ? a[0] : JsValue.Undefined);
            RequireMatchingCalendar(ctx, CalId(h, o), otherCal);
            RequireMatchingTimeZone(ctx, GetVStr(h, o, "tz"), otherTz);
            var s = GetDifferenceSettings(ctx, h, a, 1, DateTimeDiffUnits, "nanosecond", "hour");
            return AttachTemporalPrototypeByName(ctx, h, t, "Duration", ZdtDiff(ctx, h, o, otherNs, s.Largest, -1));
        }, 1);
        AddMethod(ctx, h, pH, p, "round", (o, a) => ZonedRound(ctx, h, o, a, pH), 1);
        AddMethod(ctx, h, pH, p, "equals", (o, a) => {
            var (otherNs, otherTz, otherCal) = ToTemporalZonedRecord(ctx, h, a.Count > 0 ? a[0] : JsValue.Undefined);
            var selfCal = GetVStr(h, o, "calendarId");
            bool calsEqual = (string.IsNullOrEmpty(selfCal) ? "iso8601" : selfCal) == (string.IsNullOrEmpty(otherCal) ? "iso8601" : otherCal);
            bool tzEqual = string.Equals(
                CanonicalTimeZoneKey(ctx, GetVStr(h, o, "tz")),
                CanonicalTimeZoneKey(ctx, otherTz),
                StringComparison.OrdinalIgnoreCase);
            return JsValue.FromBoolean(DecodeInstantNanosBig(h, o) == otherNs && tzEqual && calsEqual);
        }, 1);
        AddMethod(ctx, h, pH, p, "startOfDay", (o, _) =>
            AttachPrototype(h, MakeZonedDateTimeNsBig(ctx, h, ZonedStartOfDayNsBig(h, o), GetVStr(h, o, "tz"), GetVStr(h, o, "calendarId")), pH), 0);
        AddMethod(ctx, h, pH, p, "getTimeZoneTransition", (o, a) => {
            // Direction is required: "next"/"previous" or { direction }.
            if (a.Count == 0 || a[0].Tag == JsValueTag.Undefined)
                throw new JsThrownException(ctx.CreateTypeError("getTimeZoneTransition requires a direction."));
            string direction;
            if (a[0].Tag == JsValueTag.String) direction = a[0].AsString();
            else if (a[0].Tag == JsValueTag.Object)
            {
                if (!TryGetField(ctx, h, a[0], "direction", out var dv))
                    throw new JsThrownException(ctx.CreateRangeError("direction is required."));
                direction = dv.Tag == JsValueTag.String ? dv.AsString() : ctx.ToStringValue(dv);
            }
            else throw new JsThrownException(ctx.CreateTypeError("getTimeZoneTransition: invalid argument."));
            if (direction is not ("next" or "previous"))
                throw new JsThrownException(ctx.CreateRangeError($"'{direction}' is not a valid transition direction."));
            if (!TemporalTimeZones.TryGetTransition(
                    GetVStr(h, o, "tz"),
                    DecodeInstantNanosBig(h, o),
                    direction == "next",
                    out var transitionNs))
                return JsValue.Null;
            return AttachPrototype(h, MakeZonedDateTimeNsBig(
                ctx, h, transitionNs, GetVStr(h, o, "tz"), GetVStr(h, o, "calendarId")), pH);
        }, 1);
        AddMethod(ctx, h, pH, p, "toInstant", (o, _) =>
            AttachTemporalPrototypeByName(ctx, h, t, "Instant", MakeInstantFromNanoseconds(h, DecodeInstantNanosBig(h, o))), 0);
        AddMethod(ctx, h, pH, p, "toPlainDate", (o, _) => {
            var d = DecodeIsoDateLong(h, o);
            return AttachTemporalPrototypeByName(ctx, h, t, "PlainDate", MakePlainDateYmd(ctx, h, d.Year, d.Month, d.Day, GetVStr(h, o, "calendarId")));
        }, 0);
        AddMethod(ctx, h, pH, p, "toPlainTime", (o, _) => AttachTemporalPrototypeByName(ctx, h, t, "PlainTime", MakePlainTime(ctx, h,
            (int)GetVNum(h, o, "hour"), (int)GetVNum(h, o, "minute"), (int)GetVNum(h, o, "second"),
            (int)GetVNum(h, o, "millisecond"), (int)GetVNum(h, o, "microsecond"), (int)GetVNum(h, o, "nanosecond"))), 0);
        AddMethod(ctx, h, pH, p, "toPlainDateTime", (o, _) => {
            var d = DecodeIsoDateLong(h, o);
            return AttachTemporalPrototypeByName(ctx, h, t, "PlainDateTime", MakePlainDateTimeParts(ctx, h, d.Year, d.Month, d.Day,
                (int)GetVNum(h, o, "hour"), (int)GetVNum(h, o, "minute"), (int)GetVNum(h, o, "second"),
                (int)GetVNum(h, o, "millisecond"), (int)GetVNum(h, o, "microsecond"), (int)GetVNum(h, o, "nanosecond"),
                GetVStr(h, o, "calendarId")));
        }, 0);
        AddMethod(ctx, h, pH, p, "toString", (o, a) => FormatZonedDateTime(ctx, h, o, GetToStringOptions(ctx, h, a, 0, new[] { "calendarName", "fractionalSecondDigits", "offset", "roundingMode", "smallestUnit", "timeZoneName" })), 0);
        AddMethod(ctx, h, pH, p, "toLocaleString", (o, a) => {
            // ECMA-402: ZonedDateTime.toLocaleString must not accept a timeZone option —
            // the instance already has a time zone and options.timeZone is ignored.
            if (a.Count > 1 && a[1].Tag == JsValueTag.Object && TryGetField(ctx, h, a[1], "timeZone", out _))
                throw new JsThrownException(ctx.CreateTypeError("ZonedDateTime.toLocaleString does not accept timeZone option."));
            var locale = a.Count > 0 ? ToStrArg(ctx, a[0]) : string.Empty;
            var options = ParseDateTimeFormatOptions(locale, ctx, h, a.Count > 1 ? a[1] : JsValue.Undefined);
            try { IntlDateTimeFormatting.ValidateOptions(options); } catch (InvalidOperationException) { throw new JsThrownException(ctx.CreateTypeError("dateStyle/timeStyle conflicts with explicit component options.")); }
            try { IntlDateTimeFormatting.ValidateTemporalCalendar(GetVStr(h, o, "calendarId"), options); } catch (InvalidOperationException) { throw new JsThrownException(ctx.CreateRangeError("calendar mismatch")); }
            // If nothing explicit is set, default to date+time+timeZoneName for ZonedDateTime.
            var hasExplicit = !string.IsNullOrEmpty(options.DateStyle) || !string.IsNullOrEmpty(options.TimeStyle) ||
                !string.IsNullOrEmpty(options.Weekday) || !string.IsNullOrEmpty(options.Era) ||
                !string.IsNullOrEmpty(options.Year) || !string.IsNullOrEmpty(options.Month) ||
                !string.IsNullOrEmpty(options.Day) || !string.IsNullOrEmpty(options.Hour) ||
                !string.IsNullOrEmpty(options.Minute) || !string.IsNullOrEmpty(options.Second) ||
                options.FractionalSecondDigits.HasValue || !string.IsNullOrEmpty(options.DayPeriod) ||
                !string.IsNullOrEmpty(options.TimeZoneName);
            if (!hasExplicit)
                options = options with { Year = "numeric", Month = "numeric", Day = "numeric", Hour = "numeric", Minute = "numeric", Second = "numeric", TimeZoneName = "short" };
            var culture = IntlDateTimeFormatting.ResolveCulture(locale);
            System.Numerics.BigInteger epochNs = DecodeInstantNanosBig(h, o);
            // Use the ZonedDateTime's timezone, not the options timezone
            string tz = GetVStr(h, o, "tz");
            var instant = new DateTimeOffset(InstantToDateTime((long)epochNs));
            var tzOptions = options with { TimeZoneId = tz };
            var result = IntlDateTimeFormatting.Format(instant, culture, tzOptions);
            return JsValue.FromString(result.Text);
        }, 0);
        AddMethod(ctx, h, pH, p, "toJSON", (o, _) => FormatZonedDateTime(ctx, h, o, new ToStringOptions()), 0);
        AddMethod(ctx, h, pH, p, "valueOf", (_, _2) => throw new JsThrownException(ctx.CreateTypeError("valueOf throws.")), 0);
        var c = h.GetObject(cH);
        AddStatic(ctx, h, cH, c, "from", a => {
            if (a.Count == 0) throw new JsThrownException(ctx.CreateTypeError("ZonedDateTime.from requires at least 1 argument."));
            RequireOptionsObject(ctx, a, 1);
            var disambiguation = GetDisambiguationOption(ctx, h, a, 1);
            var offset = GetOffsetOption(ctx, h, a, 1, "reject");
            var overflow = GetOverflowOption(ctx, h, a, 1);
            var (ns, tz, cal) = ToTemporalZonedRecord(ctx, h, a[0], offset, disambiguation);
            return AttachPrototype(h, MakeZonedDateTimeNsBig(ctx, h, ns, tz, cal), pH);
        }, 1);
        AddStatic(ctx, h, cH, c, "compare", a => {
            var (nsA, _, _) = ToTemporalZonedRecord(ctx, h, a.Count > 0 ? a[0] : JsValue.Undefined);
            var (nsB, _, _) = ToTemporalZonedRecord(ctx, h, a.Count > 1 ? a[1] : JsValue.Undefined);
            return JsValue.FromNumber(nsA < nsB ? -1 : nsA > nsB ? 1 : 0);
        }, 2);
    }

    /// <summary>AddZonedDateTime: calendar units on the wall date (re-resolved in the zone), time units on the epoch.</summary>
    private static JsValue AddDurationToZoned(IBuiltinContext ctx, JsHeap h, JsObject o, IReadOnlyList<JsValue> a, ObjectHandle pH, int sign)
    {
        var dur = ToTemporalDurationRecord(ctx, h, a.Count > 0 ? a[0] : JsValue.Undefined);
        bool constrain = GetOverflowOption(ctx, h, a, 1) == "constrain";
        string tz = GetVStr(h, o, "tz");
        var epochNsBig = DecodeInstantNanosBig(h, o);
        if (dur.years != 0 || dur.months != 0 || dur.weeks != 0 || dur.days != 0)
        {
            var date = DecodeIsoDateLong(h, o);
            var newDate = AddDateInCalendar(CalId(h, o), date, sign * ToSafeInt(dur.years), sign * ToSafeInt(dur.months), sign * ToSafeInt(dur.weeks), sign * dur.days,
                constrain, out var invalid);
            if (invalid || !IsoMath.IsoDateWithinLimits(newDate))
                throw new JsThrownException(ctx.CreateRangeError("Resulting date is outside the supported range."));
            var time = new IsoTime((int)GetVNum(h, o, "hour"), (int)GetVNum(h, o, "minute"), (int)GetVNum(h, o, "second"),
                (int)GetVNum(h, o, "millisecond"), (int)GetVNum(h, o, "microsecond"), (int)GetVNum(h, o, "nanosecond"));
            epochNsBig = TemporalTimeZones.EpochNsFromWallBig(tz, newDate, time);
        }

        epochNsBig += sign * DurationToNanos(0, dur.hours, dur.minutes, dur.seconds, dur.millis, dur.micros, dur.nanos);
        // ECMA-262: the resulting ZonedDateTime instant must be within the representable range.
        if (System.Numerics.BigInteger.Abs(epochNsBig) > MaxInstantNs)
            throw new JsThrownException(ctx.CreateRangeError("Resulting instant is outside the representable range."));
        return AttachPrototype(h, MakeZonedDateTimeNsBig(ctx, h, epochNsBig, tz, GetVStr(h, o, "calendarId")), pH);
    }

    /// <summary>ZonedDateTime.prototype.round: round the wall time, re-resolve in the zone.</summary>
    private static JsValue ZonedRound(IBuiltinContext ctx, JsHeap h, JsObject o, IReadOnlyList<JsValue> a, ObjectHandle pH)
    {
        var opts = GetRoundingOptions(ctx, h, a);
        var smallest = opts.Smallest;
        var increment = opts.Increment;
        var mode = opts.Mode;

        if (IsCalendarUnit(smallest))
            throw new JsThrownException(ctx.CreateRangeError($"'{smallest}' is not a valid value for smallest unit."));
        if (smallest != "day")
        {
            long maxInc = smallest == "hour" ? 24 : smallest is "minute" or "second" ? 60 : 1000;
            if (increment >= maxInc || maxInc % (long)increment != 0)
                throw new JsThrownException(ctx.CreateRangeError("roundingIncrement does not divide evenly."));
        }
        else if (increment != 1)
        {
            throw new JsThrownException(ctx.CreateRangeError("roundingIncrement must be 1 for day."));
        }

        string tz = GetVStr(h, o, "tz");
        var date = DecodeIsoDateLong(h, o);
        long timeNs = ZonedWallTimeNs(h, o);
        if (smallest == "day")
        {
            // Round relative to the actual day length in the zone.
            long start = ToSafeLong(TemporalTimeZones.GetStartOfDayEpochNsBig(tz, date));
            long end = ToSafeLong(TemporalTimeZones.GetStartOfDayEpochNsBig(tz, IsoMath.EpochDaysToCivil(IsoMath.ToEpochDays(date) + 1)));
            System.Numerics.BigInteger self = DecodeInstantNanosBig(h, o);
            System.Numerics.BigInteger rounded = RoundNsToIncrement(ctx, self - start, (System.Numerics.BigInteger)(end - start), mode) + start;
            return AttachPrototype(h, MakeZonedDateTimeNsBig(ctx, h, rounded == start ? start : end, tz, GetVStr(h, o, "calendarId")), pH);
        }

        long roundedTime = (long)RoundNsToIncrement(ctx, timeNs, (long)increment * UnitNs(smallest), mode);
        long dayCarry = roundedTime / NsPerDay;
        roundedTime -= dayCarry * NsPerDay;
        var newDate = dayCarry == 0 ? date : IsoMath.EpochDaysToCivil(IsoMath.ToEpochDays(date) + dayCarry);
        if (!IsoMath.IsoDateWithinLimits(newDate))
            throw new JsThrownException(ctx.CreateRangeError("Rounded date is outside the representable range."));
        var newTime = new IsoTime(
            (int)(roundedTime / 3_600_000_000_000L), (int)(roundedTime / 60_000_000_000L % 60), (int)(roundedTime / 1_000_000_000L % 60),
            (int)(roundedTime / 1_000_000L % 1000), (int)(roundedTime / 1_000L % 1000), (int)(roundedTime % 1000));
        return AttachPrototype(h, MakeZonedDateTimeNsBig(ctx, h,
            TemporalTimeZones.EpochNsFromWallBig(tz, newDate, newTime), tz, GetVStr(h, o, "calendarId")), pH);
    }

    private static string GetCalendarId(JsHeap h, JsObject o)
    {
        if (o.TryGetOwnProperty("_v", out var vd) && vd.Value.Tag == JsValueTag.String)
        {
            return vd.Value.AsString();
        }
        return "iso8601";
    }

    private static string GetTimeZoneId(JsHeap h, JsObject o)
    {
        if (o.TryGetOwnProperty("_v", out var vd) && vd.Value.Tag == JsValueTag.String)
        {
            return vd.Value.AsString();
        }
        return "UTC";
    }

    // ─── Temporal.Calendar ─────────────────────────────────
    private void InstallCalendar(IBuiltinContext ctx, JsObject t, ObjectHandle tH, JsHeap h)
    {
        var (cH, pH) = MakeCtor(ctx, h, t, tH, "Calendar", 1, true, (cCtx, cH, a) => {
            if (a.Count == 0 || a[0].Tag == JsValueTag.Undefined)
                throw new JsThrownException(cCtx.CreateTypeError("Calendar ID must be a string."));
            if (a[0].Tag != JsValueTag.String)
                throw new JsThrownException(cCtx.CreateTypeError("Calendar ID must be a string."));
            var id = a[0].AsString();
            var canonical = CanonicalizeCalendarId(cCtx, id);
            var o = new JsObject();
            o.DefineOwnProperty("_v", new JsPropertyDescriptor(JsValue.FromString(canonical), false, false, false));
            return JsValue.FromObject(cH.AllocateObject(o, AllocationSite.Current()));
        });
        var p = h.GetObject(pH);
        AddGetter(ctx, h, pH, p, "id", o => JsValue.FromString(GetCalendarId(h, o)));
        AddMethod(ctx, h, pH, p, "toString", (o, _) => JsValue.FromString(GetCalendarId(h, o)), 0);
        AddMethod(ctx, h, pH, p, "toJSON", (o, _) => JsValue.FromString(GetCalendarId(h, o)), 0);
        var c = h.GetObject(cH);
        AddStatic(ctx, h, cH, c, "from", a => {
            if (a.Count == 0 || a[0].Tag == JsValueTag.Undefined)
            {
                var calVal = MakeCalendar(ctx, h, "iso8601");
                AttachTemporalPrototypeByName(ctx, h, t, "Calendar", calVal);
                return calVal;
            }
            if (a[0].Tag == JsValueTag.Object)
            {
                var obj = h.GetObject(a[0].AsObjectHandle());
                if (obj.TryGetOwnProperty("_v", out var vd) && vd.Value.Tag == JsValueTag.String)
                {
                    return a[0];
                }
            }
            var cidParsed = ToCalendarIdentifier(ctx, h, a[0]);
            var val = MakeCalendar(ctx, h, cidParsed);
            AttachTemporalPrototypeByName(ctx, h, t, "Calendar", val);
            return val;
        }, 1);
    }

    // ─── Temporal.TimeZone ─────────────────────────────────
    private void InstallTimeZone(IBuiltinContext ctx, JsObject t, ObjectHandle tH, JsHeap h)
    {
        var (cH, pH) = MakeCtor(ctx, h, t, tH, "TimeZone", 1, true, (cCtx, cH, a) => {
            if (a.Count == 0 || a[0].Tag == JsValueTag.Undefined)
                throw new JsThrownException(cCtx.CreateTypeError("TimeZone ID must be a string."));
            if (a[0].Tag != JsValueTag.String)
                throw new JsThrownException(cCtx.CreateTypeError("TimeZone ID must be a string."));
            var id = a[0].AsString();
            if (!TemporalTimeZones.TryCanonicalize(id, out var canonical, out _))
                throw new JsThrownException(cCtx.CreateRangeError($"'{id}' is not a valid time zone."));
            var o = new JsObject();
            o.DefineOwnProperty("_v", new JsPropertyDescriptor(JsValue.FromString(canonical), false, false, false));
            return JsValue.FromObject(cH.AllocateObject(o, AllocationSite.Current()));
        });
        var p = h.GetObject(pH);
        AddGetter(ctx, h, pH, p, "id", o => JsValue.FromString(GetTimeZoneId(h, o)));
        AddMethod(ctx, h, pH, p, "getOffsetNanosecondsFor", (o, a) => {
            if (a.Count == 0 || a[0].Tag != JsValueTag.Object)
                throw new JsThrownException(ctx.CreateTypeError("getOffsetNanosecondsFor: instant must be an object."));
            var instantObj = h.GetObject(a[0].AsObjectHandle());
            if (!instantObj.TryGetOwnProperty("_v", out var valProp) || valProp.Value.Tag != JsValueTag.Object)
                throw new JsThrownException(ctx.CreateTypeError("getOffsetNanosecondsFor: instant is not a valid Temporal.Instant."));
            var slotsObj = h.GetObject(valProp.Value.AsObjectHandle());
            if (!slotsObj.TryGetOwnProperty("ensBig", out _) && !slotsObj.TryGetOwnProperty("ens", out _))
                throw new JsThrownException(ctx.CreateTypeError("getOffsetNanosecondsFor: instant is not a valid Temporal.Instant."));
            var epochNsBig = DecodeInstantNanosBig(h, instantObj);
            long ens = (long)epochNsBig;
            string tz = GetTimeZoneId(h, o);
            long offsetNs = TemporalTimeZones.GetOffsetNs(tz, ens);
            return JsValue.FromNumber(offsetNs);
        }, 1);
        AddMethod(ctx, h, pH, p, "getNextTransition", (o, _) => JsValue.Null, 1);
        AddMethod(ctx, h, pH, p, "getPreviousTransition", (o, _) => JsValue.Null, 1);
        AddMethod(ctx, h, pH, p, "toString", (o, _) => JsValue.FromString(GetTimeZoneId(h, o)), 0);
        AddMethod(ctx, h, pH, p, "toJSON", (o, _) => JsValue.FromString(GetTimeZoneId(h, o)), 0);
        var c = h.GetObject(cH);
        AddStatic(ctx, h, cH, c, "from", a => {
            if (a.Count > 0 && a[0].Tag == JsValueTag.Object)
            {
                var obj = h.GetObject(a[0].AsObjectHandle());
                if (obj.TryGetOwnProperty("_v", out var vd) && vd.Value.Tag == JsValueTag.String)
                {
                    return a[0];
                }
            }
            var tzId = ResolveTimeZoneId(ctx, h, a.Count > 0 ? a[0] : JsValue.Undefined, "UTC");
            var val = MakeTimeZone(ctx, h, tzId);
            AttachTemporalPrototypeByName(ctx, h, t, "TimeZone", val);
            return val;
        }, 1);
    }

    // ─── Factory methods ───────────────────────────────────
    private static JsValue MakeDuration(IBuiltinContext ctx, JsHeap h, TimeSpan ts)
    {
        return MakeDuration(ctx, h, 0, 0, 0, ts.Days, ts.Hours, ts.Minutes, ts.Seconds, ts.Milliseconds, 0, 0);
    }

    private static JsValue MakeDuration(IBuiltinContext ctx, JsHeap h,
        double years, double months, double weeks, double days,
        double hours, double minutes, double seconds, double millis, double micros, double nanos)
    {
        var o = new JsObject();
        var d = new JsObject(); var dH = h.AllocateObject(d, AllocationSite.Current());
        d.SetProperty("days", JsValue.FromNumber(days));
        d.SetProperty("hours", JsValue.FromNumber(hours));
        d.SetProperty("microseconds", JsValue.FromNumber(micros));
        d.SetProperty("milliseconds", JsValue.FromNumber(millis));
        d.SetProperty("minutes", JsValue.FromNumber(minutes));
        d.SetProperty("months", JsValue.FromNumber(months));
        d.SetProperty("nanoseconds", JsValue.FromNumber(nanos));
        d.SetProperty("seconds", JsValue.FromNumber(seconds));
        d.SetProperty("weeks", JsValue.FromNumber(weeks));
        d.SetProperty("years", JsValue.FromNumber(years));
        o.DefineOwnProperty("_v", new JsPropertyDescriptor(JsValue.FromObject(dH), false, false, false));
        return JsValue.FromObject(h.AllocateObject(o, AllocationSite.Current()));
    }

    private static JsValue MakeInstant(IBuiltinContext ctx, JsHeap h, DateTime dt)
    {
        if (dt.Kind == DateTimeKind.Local) dt = dt.ToUniversalTime();
        var ns = (dt.Ticks - Epoch.Ticks) * 100L;
        return MakeInstantFromNanoseconds(h, ns);
    }

    private static JsValue MakeInstantFromNanoseconds(JsHeap h, System.Numerics.BigInteger ns)
    {
        var o = new JsObject();
        var d = new JsObject(); var dH = h.AllocateObject(d, AllocationSite.Current());
        d.SetProperty("es", JsValue.FromNumber((double)ns / 1_000_000_000));
        d.SetProperty("ems", JsValue.FromNumber((double)ns / 1_000_000));
        d.SetProperty("eus", JsValue.FromNumber((double)ns / 1_000));
        d.SetProperty("ens", JsValue.FromNumber((double)ns));
        d.SetProperty("ensBig", JsValue.FromBigInt(ns));
        o.DefineOwnProperty("_v", new JsPropertyDescriptor(JsValue.FromObject(dH), false, false, false));
        return JsValue.FromObject(h.AllocateObject(o, AllocationSite.Current()));
    }

    private static JsValue MakeInstantEpoch(IBuiltinContext ctx, JsHeap h, IReadOnlyList<JsValue> a, long mul)
    {
        if (a.Count == 0)
            return MakeInstantFromNanoseconds(h, System.Numerics.BigInteger.Zero);
        var arg = a[0];
        // fromEpochNanoseconds (mul == 1) accepts BigInt per spec.
        if (arg.Tag == JsValueTag.BigInt)
        {
            var bi = arg.AsBigInt();
            if (mul != 1) bi *= mul;
            return MakeInstantFromNanoseconds(h, bi);
        }
        var v = ctx.ToNumber(arg);
        var ns = new System.Numerics.BigInteger(v) * mul;
        return MakeInstantFromNanoseconds(h, ns);
    }

    private static JsValue MakePlainDate(IBuiltinContext ctx, JsHeap h, DateTime dt, string calendarId = "iso8601")
        => MakePlainDateYmd(ctx, h, dt.Year, dt.Month, dt.Day, calendarId);

    private static JsValue MakePlainDateYmd(IBuiltinContext ctx, JsHeap h, int y, int m, int d, string calendarId = "iso8601")
    {
        var o = new JsObject();
        var data = new JsObject(); var dH = h.AllocateObject(data, AllocationSite.Current());
        data.SetProperty("d", JsValue.FromNumber(d));
        data.SetProperty("m", JsValue.FromNumber(m));
        data.SetProperty("y", JsValue.FromNumber(y));
        data.SetProperty("calendarId", JsValue.FromString(string.IsNullOrEmpty(calendarId) ? "iso8601" : calendarId));
        o.DefineOwnProperty("_v", new JsPropertyDescriptor(JsValue.FromObject(dH), false, false, false));
        return JsValue.FromObject(h.AllocateObject(o, AllocationSite.Current()));
    }

    private static JsValue MakePlainTime(IBuiltinContext ctx, JsHeap h, TimeSpan ts)
    {
        return MakePlainTime(ctx, h, ts.Hours, ts.Minutes, ts.Seconds, ts.Milliseconds, 0, 0);
    }

    private static JsValue MakePlainTimeFromDayNs(IBuiltinContext ctx, JsHeap h, long dayNs)
    {
        return MakePlainTime(ctx, h,
            (int)(dayNs / 3_600_000_000_000L), (int)(dayNs / 60_000_000_000L % 60), (int)(dayNs / 1_000_000_000L % 60),
            (int)(dayNs / 1_000_000L % 1000), (int)(dayNs / 1_000L % 1000), (int)(dayNs % 1000));
    }

    /// <summary>
    /// Balance a signed nanosecond total into a time-only Duration whose largest populated field
    /// is <paramref name="largest"/> (one of hour, minute, second, millisecond, microsecond,
    /// nanosecond). Components are emitted as doubles so a large second/millisecond count that
    /// overflows int is preserved exactly.
    /// </summary>
    /// <summary>ZonedDateTime diff: calendar-aware for year/month/week/day, ns-balanced below.</summary>
    private static JsValue ZdtDiff(IBuiltinContext ctx, JsHeap h, JsObject self, System.Numerics.BigInteger otherNs, string largest, int sign)
        => ZdtDiffInternal(ctx, h, self, otherNs, largest, sign);

    private static JsValue ZdtDiff(IBuiltinContext ctx, JsHeap h, JsObject self, long otherNs, string largest, int sign)
        => ZdtDiffInternal(ctx, h, self, new System.Numerics.BigInteger(otherNs), largest, sign);

    private static JsValue ZdtDiffInternal(IBuiltinContext ctx, JsHeap h, JsObject self, System.Numerics.BigInteger otherNsBig, string largest, int sign)
    {
        var selfNsBig = DecodeInstantNanosBig(h, self);
        var diffNsBig = sign > 0 ? otherNsBig - selfNsBig : selfNsBig - otherNsBig;
        long diffNs;
        try { diffNs = (long)diffNsBig; }
        catch (OverflowException) { diffNs = diffNsBig > System.Numerics.BigInteger.Zero ? long.MaxValue : long.MinValue; }
        if (diffNs == long.MinValue) diffNs = long.MinValue + 1;
        // Sub-day units: pure ns balancing.
        if (largest is "nanosecond" or "microsecond" or "millisecond" or "second" or "minute" or "hour")
            return MakeDurationFromNsBalanced(ctx, h, diffNs, largest);

        // Day-or-above: compare wall dates in the instance's zone, anchor the
        // calendar part at the instance, then compute the exact instant remainder.
        var selfDate = DecodeIsoDateLong(h, self);
        long selfTimeNs = ZonedWallTimeNs(h, self);
        string tz = GetVStr(h, self, "tz");
        long otherNsForOffset = otherNsBig >= long.MinValue && otherNsBig <= long.MaxValue
            ? (long)otherNsBig
            : (otherNsBig < 0 ? long.MinValue : long.MaxValue);
        long otherOffsetNs = TemporalTimeZones.GetOffsetNs(tz, otherNsForOffset);
        var (otherDate, otherTime) = TemporalTimeZones.WallFromEpochNsBig(otherNsBig, otherOffsetNs);
        long otherTimeNs = otherTime.ToNanosecondsOfDay();

        long wallDays = IsoMath.ToEpochDays(otherDate) - IsoMath.ToEpochDays(selfDate);
        long wallTimeRemainder = otherTimeNs - selfTimeNs;
        if (wallDays > 0 && wallTimeRemainder < 0) wallDays--;
        else if (wallDays < 0 && wallTimeRemainder > 0) wallDays++;
        var adjustedOtherDate = IsoMath.EpochDaysToCivil(IsoMath.ToEpochDays(selfDate) + wallDays);

        var sys = CalendarMath.Get(CalId(h, self));
        (int y, int m, int w, long d) datePart;
        if (sys != null && largest is "year" or "month")
            datePart = sys.Difference(selfDate, adjustedOtherDate, largest);
        else
        {
            datePart = largest == "week"
                ? (0, 0, (int)(wallDays / 7), wallDays % 7)
                : (0, 0, 0, wallDays);
        }

        var anchorDate = AddDateInCalendar(
            CalId(h, self), selfDate, datePart.y, datePart.m, datePart.w, datePart.d,
            constrain: true, out var invalidAnchor);
        if (invalidAnchor || !TemporalTimeZones.TryResolveEpochNsFromWallBig(
                tz, anchorDate, new IsoTime(
                    (int)(selfTimeNs / 3_600_000_000_000L),
                    (int)(selfTimeNs / 60_000_000_000L % 60),
                    (int)(selfTimeNs / 1_000_000_000L % 60),
                    (int)(selfTimeNs / 1_000_000L % 1000),
                    (int)(selfTimeNs / 1_000L % 1000),
                    (int)(selfTimeNs % 1000)),
                "compatible", out var anchorNs))
            throw new JsThrownException(ctx.CreateRangeError("Unable to resolve the ZonedDateTime difference anchor."));

        var remainderBig = otherNsBig - anchorNs;
        long remainder;
        try { remainder = (long)remainderBig; }
        catch (OverflowException) { remainder = remainderBig > 0 ? long.MaxValue : long.MinValue + 1; }
        int remainderSign = remainder < 0 ? -1 : 1;
        long rem = Math.Abs(remainder);

        return MakeDuration(ctx, h,
            sign * datePart.y, sign * datePart.m, sign * datePart.w, sign * datePart.d,
            sign * remainderSign * (double)(rem / 3_600_000_000_000L),
            sign * remainderSign * (double)(rem / 60_000_000_000L % 60),
            sign * remainderSign * (double)(rem / 1_000_000_000L % 60),
            sign * remainderSign * (double)(rem / 1_000_000L % 1000),
            sign * remainderSign * (double)(rem / 1_000L % 1000),
            sign * remainderSign * (double)(rem % 1000));
    }

    private static JsValue MakeDurationFromNsBalanced(IBuiltinContext ctx, JsHeap h, long totalNs, string largest)
    {
        if (totalNs == long.MinValue) totalNs = long.MinValue + 1;
        double sign = totalNs < 0 ? -1 : 1;
        long abs = Math.Abs(totalNs);
        long days = 0, weeks = 0;
        double hours = 0, minutes = 0, seconds = 0, millis = 0, micros = 0, nanos = 0;
        // For day/week, use UTC-equivalent 24h day length. DST-aware balancing is
        // done in Duration.prototype.round with a relativeTo ZonedDateTime.
        const long DayNs = 86_400_000_000_000L;
        switch (largest)
        {
            case "year":
            case "month":
                // years/months require calendar-aware diff; for now, balance to days
                days = abs / DayNs; abs %= DayNs;
                goto case "hour";
            case "week":
                weeks = abs / (DayNs * 7); abs %= (DayNs * 7);
                days = abs / DayNs; abs %= DayNs;
                goto case "hour";
            case "day":
                days = abs / DayNs; abs %= DayNs;
                goto case "hour";
            case "hour":
                hours = abs / 3_600_000_000_000L; abs %= 3_600_000_000_000L;
                minutes = abs / 60_000_000_000L; abs %= 60_000_000_000L;
                seconds = abs / 1_000_000_000L; abs %= 1_000_000_000L;
                millis = abs / 1_000_000L; abs %= 1_000_000L; micros = abs / 1_000L; nanos = abs % 1_000L;
                break;
            case "minute":
                minutes = abs / 60_000_000_000L; abs %= 60_000_000_000L;
                seconds = abs / 1_000_000_000L; abs %= 1_000_000_000L;
                millis = abs / 1_000_000L; abs %= 1_000_000L; micros = abs / 1_000L; nanos = abs % 1_000L;
                break;
            case "second":
                seconds = abs / 1_000_000_000L; abs %= 1_000_000_000L;
                millis = abs / 1_000_000L; abs %= 1_000_000L; micros = abs / 1_000L; nanos = abs % 1_000L;
                break;
            case "millisecond":
                millis = abs / 1_000_000L; abs %= 1_000_000L; micros = abs / 1_000L; nanos = abs % 1_000L;
                break;
            case "microsecond":
                micros = abs / 1_000L; nanos = abs % 1_000L;
                break;
            default: // nanosecond
                nanos = abs;
                break;
        }
        return MakeDuration(ctx, h, 0, 0, sign * weeks, sign * days,
            sign * hours, sign * minutes, sign * seconds, sign * millis, sign * micros, sign * nanos);
    }

    private static JsValue MakePlainTime(IBuiltinContext ctx, JsHeap h,
        int hour, int minute, int second, int millisecond, int microsecond, int nanosecond)
    {
        var o = new JsObject();
        var d = new JsObject(); var dH = h.AllocateObject(d, AllocationSite.Current());
        d.SetProperty("hour", JsValue.FromNumber(hour));
        d.SetProperty("microsecond", JsValue.FromNumber(microsecond));
        d.SetProperty("millisecond", JsValue.FromNumber(millisecond));
        d.SetProperty("minute", JsValue.FromNumber(minute));
        d.SetProperty("nanosecond", JsValue.FromNumber(nanosecond));
        d.SetProperty("second", JsValue.FromNumber(second));
        o.DefineOwnProperty("_v", new JsPropertyDescriptor(JsValue.FromObject(dH), false, false, false));
        return JsValue.FromObject(h.AllocateObject(o, AllocationSite.Current()));
    }

    private static JsValue MakePlainDateTime(IBuiltinContext ctx, JsHeap h, DateTime dt, string calendarId = "iso8601")
        => MakePlainDateTimeParts(ctx, h, dt.Year, dt.Month, dt.Day, dt.Hour, dt.Minute, dt.Second, dt.Millisecond, 0, 0, calendarId);

    private static JsValue MakePlainDateTimeParts(IBuiltinContext ctx, JsHeap h,
        int y, int mo, int d, int hr, int mi, int se, int ms, int us, int ns, string calendarId = "iso8601")
    {
        var o = new JsObject();
        var data = new JsObject(); var dH = h.AllocateObject(data, AllocationSite.Current());
        data.SetProperty("day", JsValue.FromNumber(d));
        data.SetProperty("hour", JsValue.FromNumber(hr));
        data.SetProperty("microsecond", JsValue.FromNumber(us));
        data.SetProperty("millisecond", JsValue.FromNumber(ms));
        data.SetProperty("minute", JsValue.FromNumber(mi));
        data.SetProperty("month", JsValue.FromNumber(mo));
        data.SetProperty("nanosecond", JsValue.FromNumber(ns));
        data.SetProperty("second", JsValue.FromNumber(se));
        data.SetProperty("year", JsValue.FromNumber(y));
        data.SetProperty("calendarId", JsValue.FromString(string.IsNullOrEmpty(calendarId) ? "iso8601" : calendarId));
        o.DefineOwnProperty("_v", new JsPropertyDescriptor(JsValue.FromObject(dH), false, false, false));
        return JsValue.FromObject(h.AllocateObject(o, AllocationSite.Current()));
    }

    private static JsValue MakePlainYearMonth(IBuiltinContext ctx, JsHeap h, int y, int m, string calendarId = "iso8601", int refDay = 1)
    {
        var o = new JsObject();
        var d = new JsObject(); var dH = h.AllocateObject(d, AllocationSite.Current());
        d.SetProperty("d", JsValue.FromNumber(refDay));
        d.SetProperty("m", JsValue.FromNumber(m));
        d.SetProperty("y", JsValue.FromNumber(y));
        d.SetProperty("calendarId", JsValue.FromString(string.IsNullOrEmpty(calendarId) ? "iso8601" : calendarId));
        d.SetProperty("__temporalType", JsValue.FromString("PlainYearMonth"));
        o.DefineOwnProperty("_v", new JsPropertyDescriptor(JsValue.FromObject(dH), false, false, false));
        return JsValue.FromObject(h.AllocateObject(o, AllocationSite.Current()));
    }

    /// <summary>
    /// The reference ISO date for a year-month: day 1 of the calendar month containing
    /// <paramref name="isoFromString"/> (for iso8601 this is simply that month's day 1).
    /// </summary>
    private static IsoDate YearMonthReferenceIso(string calendar, IsoDate isoFromString)
    {
        var sys = CalendarMath.Get(calendar);
        if (sys is null)
            return new IsoDate(isoFromString.Year, isoFromString.Month, 1);
        sys.ToNative(isoFromString, out int cy, out int cmo, out _);
        return sys.TryResolveToIso(cy, cmo, 1, "constrain", out var iso) ? iso : isoFromString;
    }

    /// <summary>The ISO reference date stored for a PlainYearMonth (reference day defaults to 1).</summary>
    private static IsoDate DecodeYearMonthIso(JsHeap h, JsObject o)
    {
        int y = (int)GetVNum(h, o, "y");
        int m = Math.Clamp((int)GetVNum(h, o, "m"), 1, 12);
        int d = TryGetInternalData(h, o, out var data) && HasOwn(h, data, "d") ? (int)GetVNum(h, o, "d") : 1;
        d = Math.Clamp(d, 1, IsoMath.DaysInMonth(y, m));
        return new IsoDate(y, m, d);
    }

    private static JsValue MakePlainMonthDay(IBuiltinContext ctx, JsHeap h, int y, int m, int d, string calendarId = "iso8601")
    {
        string cal = string.IsNullOrEmpty(calendarId) ? "iso8601" : calendarId;
        if (cal == "iso8601") y = 1972;
        var o = new JsObject();
        var dd = new JsObject(); var ddH = h.AllocateObject(dd, AllocationSite.Current());
        dd.SetProperty("d", JsValue.FromNumber(d));
        dd.SetProperty("m", JsValue.FromNumber(m));
        dd.SetProperty("y", JsValue.FromNumber(y));
        dd.SetProperty("calendarId", JsValue.FromString(cal));
        dd.SetProperty("__temporalType", JsValue.FromString("PlainMonthDay"));
        o.DefineOwnProperty("_v", new JsPropertyDescriptor(JsValue.FromObject(ddH), false, false, false));
        return JsValue.FromObject(h.AllocateObject(o, AllocationSite.Current()));
    }

    private static JsValue MakeZonedDateTime(IBuiltinContext ctx, JsHeap h, DateTimeOffset dto, string tz)
    {
        var o = new JsObject();
        var d = new JsObject(); var dH = h.AllocateObject(d, AllocationSite.Current());
        d.SetProperty("day", JsValue.FromNumber(dto.Day)); d.SetProperty("month", JsValue.FromNumber(dto.Month));
        d.SetProperty("year", JsValue.FromNumber(dto.Year));
        d.SetProperty("hour", JsValue.FromNumber(dto.Hour)); d.SetProperty("minute", JsValue.FromNumber(dto.Minute));
        d.SetProperty("second", JsValue.FromNumber(dto.Second)); d.SetProperty("millisecond", JsValue.FromNumber(dto.Millisecond));
        d.SetProperty("microsecond", JsValue.FromNumber(0)); d.SetProperty("nanosecond", JsValue.FromNumber(0));
        var ns = (dto.UtcTicks - Epoch.Ticks) * 100L;
        d.SetProperty("epochSeconds", JsValue.FromNumber((double)ns / 1_000_000_000));
        d.SetProperty("epochMilliseconds", JsValue.FromNumber((double)ns / 1_000_000));
        d.SetProperty("epochMicroseconds", JsValue.FromNumber((double)ns / 1_000));
        d.SetProperty("epochNanoseconds", JsValue.FromNumber((double)ns));
        d.SetProperty("offsetNanoseconds", JsValue.FromNumber(dto.Offset.Ticks * 100L));
        d.SetProperty("tz", JsValue.FromString(tz));
        o.DefineOwnProperty("_v", new JsPropertyDescriptor(JsValue.FromObject(dH), false, false, false));
        o.DefineOwnProperty("timeZoneId", new JsPropertyDescriptor(JsValue.FromString(tz), true, true, true));
        return JsValue.FromObject(h.AllocateObject(o, AllocationSite.Current()));
    }

    private static JsValue MakeCalendar(IBuiltinContext ctx, JsHeap h, string id)
    {
        var o = new JsObject();
        o.DefineOwnProperty("_v", new JsPropertyDescriptor(JsValue.FromString(id), false, false, false));
        return JsValue.FromObject(h.AllocateObject(o, AllocationSite.Current()));
    }

    private static JsValue MakeTimeZone(IBuiltinContext ctx, JsHeap h, string id)
    {
        var o = new JsObject();
        o.DefineOwnProperty("_v", new JsPropertyDescriptor(JsValue.FromString(id), false, false, false));
        return JsValue.FromObject(h.AllocateObject(o, AllocationSite.Current()));
    }

    private static IntlDateTimeFormatOptions ParseDateTimeFormatOptions(string locale, IBuiltinContext ctx, JsHeap h, JsValue optionsValue)
    {
        if (optionsValue.Tag != JsValueTag.Object)
        {
            return new IntlDateTimeFormatOptions(CalendarId: ParseCalendarId(locale));
        }

        var optionsObject = h.GetObject(optionsValue.AsObjectHandle());
        string? GetString(string name)
        {
            return TryGetStringProperty(ctx, h, optionsObject, optionsValue, name, out var value) ? value : null;
        }

        bool? GetBool(string name)
        {
            if (!optionsObject.TryGetProperty(name, x => h.GetObject(x), out var descriptor) || descriptor.Value.Tag == JsValueTag.Undefined)
            {
                return null;
            }

            var value = descriptor.Value;
            return value.Tag switch
            {
                JsValueTag.Boolean => value.AsBoolean(),
                _ => ctx.ToNumber(value) != 0 && !double.IsNaN(ctx.ToNumber(value)),
            };
        }

        int? GetInt(string name)
        {
            if (!optionsObject.TryGetProperty(name, x => h.GetObject(x), out var descriptor) || descriptor.Value.Tag == JsValueTag.Undefined)
            {
                return null;
            }

            return (int)ctx.ToNumber(descriptor.Value);
        }

        return new IntlDateTimeFormatOptions(
            CalendarId: ParseCalendarId(locale),
            TimeZoneId: GetString("timeZone"),
            DateStyle: GetString("dateStyle"),
            TimeStyle: GetString("timeStyle"),
            HourCycle: GetString("hourCycle"),
            Hour12: GetBool("hour12"),
            Weekday: GetString("weekday"),
            Era: GetString("era"),
            Year: GetString("year"),
            Month: GetString("month"),
            Day: GetString("day"),
            Hour: GetString("hour"),
            Minute: GetString("minute"),
            Second: GetString("second"),
            FractionalSecondDigits: GetInt("fractionalSecondDigits"),
            DayPeriod: GetString("dayPeriod"),
            TimeZoneName: GetString("timeZoneName"));
    }

    private static string? ParseCalendarId(string locale)
    {
        const string marker = "-u-ca-";
        var index = locale.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            return null;
        }

        var start = index + marker.Length;
        var remaining = locale[start..];
        // Calendar IDs may contain hyphens (e.g. "islamic-tbla").
        // Find the next BCP47 extension key boundary: "-" + 2 letters + "-"
        int nextExt = remaining.Length;
        for (int i = 0; i < remaining.Length - 3; i++)
        {
            if (remaining[i] == '-' &&
                char.IsAsciiLetter(remaining[i + 1]) && char.IsAsciiLetter(remaining[i + 2]) &&
                remaining[i + 3] == '-')
            {
                nextExt = i;
                break;
            }
        }

        return remaining[..nextExt];
    }

    private static bool TryGetStringProperty(IBuiltinContext ctx, JsHeap h, JsObject obj, JsValue receiver, string name, out string value)
    {
        value = string.Empty;
        if (!obj.TryGetProperty(name, x => h.GetObject(x), out var descriptor) || descriptor.Value.Tag == JsValueTag.Undefined)
        {
            return false;
        }

        var property = descriptor.Value;
        value = property.Tag == JsValueTag.String ? property.AsString() : ctx.ToStringValue(property);
        return true;
    }

    /// <summary>Duration.prototype.with — merge duration-like object fields into a copy.</summary>
    private static JsValue DurationWith(IBuiltinContext ctx, JsHeap h, JsObject o, IReadOnlyList<JsValue> a)
    {
        if (a.Count < 1 || a[0].Tag != JsValueTag.Object)
            throw new JsThrownException(ctx.CreateTypeError("Duration.with: argument must be an object."));
        var bagValue = a[0];
        var dur = DecodeDuration(h, o);
        var current = new double[] { dur.years, dur.months, dur.weeks, dur.days, dur.hours, dur.minutes, dur.seconds, dur.millis, dur.micros, dur.nanos };
        string[] fields = { "days", "hours", "microseconds", "milliseconds", "minutes", "months", "nanoseconds", "seconds", "weeks", "years" };
        bool any = false;
        for (int fi = 0; fi < fields.Length; fi++)
        {
            if (TryGetField(ctx, h, bagValue, fields[fi], out var fv))
            {
                current[fi] = ToIntegerIfIntegral(ctx, fv);
                any = true;
            }
        }

        if (!any)
            throw new JsThrownException(ctx.CreateTypeError("with: at least one duration field is required."));
        ValidateDuration(ctx, current);
        return MakeDuration(ctx, h, current[9], current[5], current[8], current[0], current[1], current[4], current[7], current[3], current[2], current[6]);
    }

    /// <summary>Decode a relativeTo option. Returns null
    /// if no relativeTo was provided.</summary>
    private static (IsoDate date, string calId, string? tz, System.Numerics.BigInteger? epochNs)? TryDecodeRelativeTo(IBuiltinContext ctx, JsHeap h, JsValue options)
    {
        if (options.Tag != JsValueTag.Object) return null;
        if (!TryGetField(ctx, h, options, "relativeTo", out var relVal) || relVal.Tag == JsValueTag.Undefined)
            return null;
        return DecodeRelativeToValue(ctx, h, relVal);
    }

    /// <summary>Calendar-aware Duration.prototype.round using relativeTo for
    /// years/months/weeks/days balancing and rounding.</summary>
    private static JsValue DurationRoundCalendar(IBuiltinContext ctx, JsHeap h,
        (double years, double months, double weeks, double days, double hours, double minutes, double seconds, double millis, double micros, double nanos) dur, string? smallest, string? largest, double increment, string mode,
        (IsoDate date, string calId) relTo)
    {
        var sys = CalendarMath.Get(relTo.calId);
        // Use iso8601 calendar math for simplicity if the calendar is not recognized.
        sys ??= CalendarMath.Get("iso8601")!;
        // Accumulate date part: add years, months, weeks to the relativeTo date.
        var relDate = relTo.date;
        bool invalid;
        relDate = sys.Add(relDate, ToSafeInt(dur.years), ToSafeInt(dur.months), ToSafeInt(dur.weeks), 0, constrain: true, out invalid);
        System.Numerics.BigInteger dateDays = IsoMath.CivilToEpochDays(relDate.Year, relDate.Month, relDate.Day)
                        - IsoMath.CivilToEpochDays(relTo.date.Year, relTo.date.Month, relTo.date.Day);
        // Calendar days from Add + the duration's own days field.
        System.Numerics.BigInteger totalDays = dateDays + new System.Numerics.BigInteger(dur.days);
        // Time components only (hours..nanos), NOT days.
        System.Numerics.BigInteger timeNs = new System.Numerics.BigInteger(dur.hours) * 3_600_000_000_000L 
            + new System.Numerics.BigInteger(dur.minutes) * 60_000_000_000L
            + new System.Numerics.BigInteger(dur.seconds) * 1_000_000_000L 
            + new System.Numerics.BigInteger(dur.millis) * 1_000_000L
            + new System.Numerics.BigInteger(dur.micros) * 1_000L 
            + new System.Numerics.BigInteger(dur.nanos);
        System.Numerics.BigInteger totalNs = totalDays * 86_400_000_000_000L + timeNs;

        // ECMA-262: the resulting date after adding must be within Temporal limits.
        System.Numerics.BigInteger resultEpochDay = IsoMath.CivilToEpochDays(relTo.date.Year, relTo.date.Month, relTo.date.Day) + totalDays;
        if (resultEpochDay < IsoMath.MinEpochDay || resultEpochDay > IsoMath.MaxEpochDay)
            throw new JsThrownException(ctx.CreateRangeError("Duration out of range for this relativeTo."));

        smallest ??= "nanosecond";
        // Determine the largest unit to use for Difference balancing.
        // When largest is explicitly specified, use it. When it's auto/null:
        // preserve the highest calendar unit present in the original duration
        // so years/months/weeks are properly separated in the output.
        string largestEff;
        if (largest is not null && largest != "auto")
            largestEff = largest;
        else if (dur.years != 0)
            largestEff = "year";
        else if (dur.months != 0)
            largestEff = "month";
        else if (dur.weeks != 0)
            largestEff = "week";
        else if (IsCalendarUnit(smallest))
            largestEff = smallest!;
        else
            largestEff = "day";

        return DurationRoundToCalendarUnit(ctx, h, dur, totalNs, smallest!, largestEff, (long)increment, mode, sys, relTo);
    }

    /// <summary>Round total nanoseconds of a calendar-aware duration to a calendar
    /// unit (years, months, weeks) using the relativeTo anchor date.</summary>
    private static JsValue DurationRoundToCalendarUnit(IBuiltinContext ctx, JsHeap h,
        (double years, double months, double weeks, double days, double hours, double minutes, double seconds, double millis, double micros, double nanos) original, System.Numerics.BigInteger totalNs, string unit, string largestEff,
        long increment, string mode, CalendarSystem sys, (IsoDate, string) relTo)
    {
        var anchor = relTo.Item1;
        System.Numerics.BigInteger totalDays = totalNs / 86_400_000_000_000L;
        System.Numerics.BigInteger remainderNs = totalNs % 86_400_000_000_000L;
        if (remainderNs < 0) { totalDays--; remainderNs += 86_400_000_000_000L; }

        long anchorEpoch = IsoMath.CivilToEpochDays(anchor.Year, anchor.Month, anchor.Day);
        var endDate = IsoMath.EpochDaysToCivil(anchorEpoch + (long)totalDays);

        // Time-only units: round the nanosecond total directly.
        if (!IsCalendarUnit(unit) && unit != "day")
        {
            System.Numerics.BigInteger tUnitNs = increment * UnitNs(unit);
            System.Numerics.BigInteger roundedNs = RoundNsToIncrement(ctx, totalNs, tUnitNs, mode);
            System.Numerics.BigInteger rd = roundedNs / 86_400_000_000_000L;
            System.Numerics.BigInteger rr = roundedNs % 86_400_000_000_000L;
            if (rr < 0) { rd--; rr += 86_400_000_000_000L; }
            var newEnd = IsoMath.EpochDaysToCivil(anchorEpoch + (long)rd);
            var diff = sys.Difference(anchor, newEnd, largestEff);
            long r = (long)rr;
            double hh = (double)(r / 3_600_000_000_000L); r %= 3_600_000_000_000L;
            double mi = (double)(r / 60_000_000_000L); r %= 60_000_000_000L;
            double se = (double)(r / 1_000_000_000L); r %= 1_000_000_000L;
            double ms = (double)(r / 1_000_000L); r %= 1_000_000L;
            double us = (double)(r / 1_000L); r %= 1_000L;
            double ns = (double)r;
            return MakeDuration(ctx, h, diff.Years, diff.Months, diff.Weeks, (double)diff.Days,
                hh, mi, se, ms, us, ns);
        }

        // Calendar / day units: compute the broken-down difference, round the
        // target unit, then rebalance through sys.Difference.
        var diffFull = sys.Difference(anchor, endDate, largestEff == "auto" ? "day" : largestEff);
        double years = diffFull.Years, months = diffFull.Months, weeks = diffFull.Weeks, days = diffFull.Days;

        if (unit == "day")
        {
            // Round days using the time-of-day remainder as the fractional part.
            System.Numerics.BigInteger dayNs = new System.Numerics.BigInteger(days) * 86_400_000_000_000L + remainderNs;
            System.Numerics.BigInteger roundedDayNs = RoundNsToIncrement(ctx, dayNs, increment * 86_400_000_000_000L, mode);
            System.Numerics.BigInteger rd = roundedDayNs / 86_400_000_000_000L;
            System.Numerics.BigInteger rr = roundedDayNs % 86_400_000_000_000L;
            if (rr < 0) { rd--; rr += 86_400_000_000_000L; }
            // Rebalance: shift end date by (roundedDays - originalDays) then re-diff.
            long endEpoch = IsoMath.CivilToEpochDays(endDate.Year, endDate.Month, endDate.Day);
            long newEpoch = anchorEpoch + (endEpoch - anchorEpoch) - (long)days + (long)rd;
            var newEnd = IsoMath.EpochDaysToCivil(newEpoch);
            var finalDiff = sys.Difference(anchor, newEnd, largestEff == "auto" ? "day" : largestEff);
            long r = (long)rr;
            double hh = (double)(r / 3_600_000_000_000L); r %= 3_600_000_000_000L;
            double mi = (double)(r / 60_000_000_000L); r %= 60_000_000_000L;
            double se = (double)(r / 1_000_000_000L); r %= 1_000_000_000L;
            double ms = (double)(r / 1_000_000L); r %= 1_000_000L;
            double us = (double)(r / 1_000L); r %= 1_000L;
            double ns = (double)r;
            return MakeDuration(ctx, h, finalDiff.Years, finalDiff.Months, finalDiff.Weeks, (double)finalDiff.Days,
                hh, mi, se, ms, us, ns);
        }

        // Year / Month / Week rounding: round the target unit then rebalance.
        remainderNs = 0; // calendar-unit rounding discards sub-unit fractions
        if (unit == "year")
        {
            years = RoundToIncrement((long)years, (long)months, 12, increment, mode, out long ovf, out _);
            if (ovf != 0) years += ovf / 12;
            months = 0; weeks = 0; days = 0;
        }
        else if (unit == "month")
        {
            if (largestEff == "year" || largestEff == "years")
            {
                // Months within the year: round using days as the fractional part.
                int targetYear = (int)(anchor.Year + years);
                int dimCur = sys.DaysInMonthOrdinal(targetYear,
                    Math.Max(1, Math.Min(sys.MonthsInYear(targetYear), (int)(months + 1))));
                months = RoundToIncrement((long)months, (long)days, dimCur, increment, mode, out long ovf, out _);
                if (ovf != 0)
                {
                    // Rounding crossed a year boundary: carry.
                    if (ovf < 0) { months = 0; years += 1; }
                    else if (ovf > 0) { months = (int)ovf; years -= 1; }
                }
                weeks = 0; days = 0;
            }
            else
            {
                // Total months (years already collapsed into months by Difference
                // when largestEff does not include "year").
                long totalMonths = (long)years * 12 + (long)months;
                int dimCur = sys.DaysInMonthOrdinal((int)anchor.Year, 1);
                totalMonths = RoundToIncrement(totalMonths, (long)days, dimCur, increment, mode, out _, out _);
                years = 0; months = (double)totalMonths; weeks = 0; days = 0;
            }
        }
        else if (unit == "week")
        {
            weeks = RoundToIncrement((long)weeks, (long)days, 7, increment, mode, out long ovf, out _);
            if (ovf != 0) weeks += ovf / 7;
            days = 0;
        }

        // Year/month/week rounding: the carries are already handled in the
        // rounding logic above, so use the rounded values directly.  Rebalancing
        // through Difference would lose information when day-constraint in Add
        // shifts the effective date (e.g. 2 months from Jul 31 = Sep 30, which
        // Difference reports as 1 month + 30 days).
        return MakeDuration(ctx, h, years, months, weeks, days,
            0, 0, 0, 0, 0, 0);
    }

    /// <summary>Duration.prototype.round for day/time units.</summary>
    private static JsValue DurationRound(IBuiltinContext ctx, JsHeap h, JsObject o, IReadOnlyList<JsValue> a)
    {
        if (a.Count == 0 || a[0].Tag == JsValueTag.Undefined)
            throw new JsThrownException(ctx.CreateTypeError("round requires a unit or options argument."));
        string? smallest = null;
        string? largest = null;
        double increment = 1;
        string mode = "halfExpand";
        (IsoDate date, string calId)? relTo = null;
        if (a[0].Tag == JsValueTag.String)
        {
            smallest = NormalizeUnitName(ctx, a[0].AsString());
        }
        else if (a[0].Tag == JsValueTag.Object)
        {
            string? lvStr = null;
            JsValue rtvVal = JsValue.Undefined;
            double? ivNum = null;
            string? mvStr = null;
            string? svStr = null;

            // 1. Read and cast all properties first in spec order
            if (TryGetField(ctx, h, a[0], "largestUnit", out var lv) && lv.Tag != JsValueTag.Undefined)
            {
                lvStr = NormalizeUnitName(ctx, lv.Tag == JsValueTag.String ? lv.AsString() : ctx.ToStringValue(lv), allowAuto: true);
            }

            if (TryGetField(ctx, h, a[0], "relativeTo", out var rtv) && rtv.Tag != JsValueTag.Undefined)
            {
                rtvVal = rtv;
            }

            if (TryGetField(ctx, h, a[0], "roundingIncrement", out var iv) && iv.Tag != JsValueTag.Undefined)
            {
                ivNum = ToIntegerWithTruncation(ctx, iv);
            }

            if (TryGetField(ctx, h, a[0], "roundingMode", out var mv) && mv.Tag != JsValueTag.Undefined)
            {
                mvStr = mv.Tag == JsValueTag.String ? mv.AsString() : ctx.ToStringValue(mv);
            }

            if (TryGetField(ctx, h, a[0], "smallestUnit", out var sv) && sv.Tag != JsValueTag.Undefined)
            {
                svStr = NormalizeUnitName(ctx, sv.Tag == JsValueTag.String ? sv.AsString() : ctx.ToStringValue(sv));
            }

            // 2. Validate all read properties second in spec order
            if (lvStr is not null)
            {
                largest = lvStr;
            }

            if (rtvVal.Tag != JsValueTag.Undefined)
            {
                var decoded = DecodeRelativeToValue(ctx, h, rtvVal);
                if (decoded is not null)
                {
                    relTo = (decoded.Value.date, decoded.Value.calId);
                }
            }

            if (ivNum is not null)
            {
                increment = ivNum.Value;
                if (increment < 1 || increment > 1_000_000_000)
                    throw new JsThrownException(ctx.CreateRangeError("roundingIncrement out of range."));
            }

            if (mvStr is not null)
            {
                if (mvStr is not ("ceil" or "floor" or "expand" or "trunc" or "halfCeil" or "halfFloor" or "halfExpand" or "halfTrunc" or "halfEven"))
                    throw new JsThrownException(ctx.CreateRangeError($"'{mvStr}' is not a valid rounding mode."));
                mode = mvStr;
            }

            if (svStr is not null)
            {
                smallest = svStr;
            }

            if (smallest is null && (largest is null || largest == "auto"))
                throw new JsThrownException(ctx.CreateRangeError("round requires smallestUnit or largestUnit."));
        }
        else
        {
            throw new JsThrownException(ctx.CreateTypeError("round argument must be a string or options object."));
        }

        var dur = DecodeDuration(h, o);
        bool hasCalendarUnits = dur.years != 0 || dur.months != 0 || dur.weeks != 0
            || IsCalendarUnit(smallest) || (largest is not null && largest != "auto" && IsCalendarUnit(largest));
        if (hasCalendarUnits && relTo is null)
            throw new JsThrownException(ctx.CreateRangeError("Calendar units require relativeTo."));
        if (hasCalendarUnits)
        {
            return DurationRoundCalendar(ctx, h, dur, smallest, largest, increment, mode, relTo!.Value);
        }
        smallest ??= "nanosecond";
        string largestEff = largest is null or "auto"
            ? (UnitRank(DefaultLargestUnit(dur)) <= UnitRank(smallest) ? DefaultLargestUnit(dur) : smallest)
            : largest;
        if (UnitRank(smallest) < UnitRank(largestEff))
            throw new JsThrownException(ctx.CreateRangeError("smallestUnit is larger than largestUnit."));
        if (smallest != "day")
        {
            long maxInc = smallest == "hour" ? 24 : smallest is "minute" or "second" ? 60 : 1000;
            if (increment >= maxInc || maxInc % (long)increment != 0)
                throw new JsThrownException(ctx.CreateRangeError("roundingIncrement does not divide evenly."));
        }

        var rounded = RoundNsToIncrement(ctx, DurationDayTimeNs(dur), (System.Numerics.BigInteger)increment * UnitNs(smallest), mode);
        return MakeDurationBalancedNs(ctx, h, rounded, largestEff);
    }

    private static JsValue DurationTotal(IBuiltinContext ctx, JsHeap h, JsObject o, IReadOnlyList<JsValue> a)
    {
        if (a.Count == 0 || a[0].Tag == JsValueTag.Undefined)
            throw new JsThrownException(ctx.CreateTypeError("total requires a unit or options argument."));
        string unit;
        (IsoDate date, string calId)? relTo = null;
        if (a[0].Tag == JsValueTag.String)
        {
            unit = NormalizeUnitName(ctx, a[0].AsString());
        }
        else if (a[0].Tag == JsValueTag.Object)
        {
            JsValue rtv = JsValue.Undefined;
            JsValue uv = JsValue.Undefined;
            TryGetField(ctx, h, a[0], "relativeTo", out rtv);
            TryGetField(ctx, h, a[0], "unit", out uv);

            var decoded = DecodeRelativeToValue(ctx, h, rtv);
            if (decoded is not null)
            {
                relTo = (decoded.Value.date, decoded.Value.calId);
            }
            if (uv.Tag == JsValueTag.Undefined)
                throw new JsThrownException(ctx.CreateRangeError("total requires a unit."));
            unit = NormalizeUnitName(ctx, uv.Tag == JsValueTag.String ? uv.AsString() : ctx.ToStringValue(uv));
        }
        else
        {
            throw new JsThrownException(ctx.CreateTypeError("total argument must be a string or options object."));
        }

        var dur = DecodeDuration(h, o);
        bool hasCalendar = dur.years != 0 || dur.months != 0 || dur.weeks != 0 || IsCalendarUnit(unit);
        if (hasCalendar && relTo is null)
            throw new JsThrownException(ctx.CreateRangeError("Calendar units require relativeTo."));
        if (hasCalendar)
        {
            return DurationTotalCalendar(ctx, h, dur, unit, relTo!.Value);
        }
        return JsValue.FromNumber((double)DurationDayTimeNs(dur) / UnitNs(unit));
    }

    /// <summary>Calendar-aware Duration.prototype.total: add years/months/weeks
    /// to the relativeTo date, then measure the total difference in the target unit.</summary>
    private static JsValue DurationTotalCalendar(IBuiltinContext ctx, JsHeap h,
        (double years, double months, double weeks, double days, double hours, double minutes, double seconds, double millis, double micros, double nanos) dur, string unit, (IsoDate date, string calId) relTo)
    {
        var sys = CalendarMath.Get(relTo.calId);
        sys ??= CalendarMath.Get("iso8601")!;
        var relDate = relTo.date;
        bool invalid;
        relDate = sys.Add(relDate, ToSafeInt(dur.years), ToSafeInt(dur.months), ToSafeInt(dur.weeks), 0, constrain: true, out invalid);
        System.Numerics.BigInteger dateDays = IsoMath.CivilToEpochDays(relDate.Year, relDate.Month, relDate.Day)
                        - IsoMath.CivilToEpochDays(relTo.date.Year, relTo.date.Month, relTo.date.Day);
        System.Numerics.BigInteger totalDays = dateDays + new System.Numerics.BigInteger(dur.days);
        System.Numerics.BigInteger timeNs = new System.Numerics.BigInteger(dur.hours) * 3_600_000_000_000L 
            + new System.Numerics.BigInteger(dur.minutes) * 60_000_000_000L
            + new System.Numerics.BigInteger(dur.seconds) * 1_000_000_000L 
            + new System.Numerics.BigInteger(dur.millis) * 1_000_000L
            + new System.Numerics.BigInteger(dur.micros) * 1_000L 
            + new System.Numerics.BigInteger(dur.nanos);
        System.Numerics.BigInteger totalNs = totalDays * 86_400_000_000_000L + timeNs;
        double timeFraction = (double)timeNs / 86_400_000_000_000.0;
        double totalDaysDouble = (double)totalDays + timeFraction;
        if (unit == "day") return JsValue.FromNumber(totalDaysDouble);
        if (!IsCalendarUnit(unit)) return JsValue.FromNumber((double)totalNs / UnitNs(unit));

        // Calendar unit: decompose the date span with the largest calendar unit
        // that preserves the required output unit, then compute the fraction.
        var endDate = IsoMath.EpochDaysToCivil(IsoMath.CivilToEpochDays(relTo.date.Year, relTo.date.Month, relTo.date.Day) + (long)totalDays);
        var diff = sys.Difference(relTo.date, endDate, unit);
        // Remaining after whole calendar units, converted to days
        double remainingDays = unit switch
        {
            "year" => 0.0,  // handled below
            "month" => 0.0, // handled below
            "week" => diff.Days,
            _ => 0.0
        };

        double daySpan;
        if (unit == "year")
        {
            // Whole years from diff, then measure remaining days as fraction of
            // a year using the days-in-year of the year that follows the whole years.
            long epochAnchor = IsoMath.CivilToEpochDays(relTo.date.Year, relTo.date.Month, relTo.date.Day);
            var afterWholeYears = sys.Add(relTo.date, diff.Years, 0, 0, 0, constrain: true, out _);
            long epochAfterYears = IsoMath.CivilToEpochDays(afterWholeYears.Year, afterWholeYears.Month, afterWholeYears.Day);
            remainingDays = epochAnchor + (double)totalDays - epochAfterYears + timeFraction;
            int baseYearForFrac = diff.Years != 0 ? afterWholeYears.Year : relTo.date.Year;
            daySpan = sys.DaysInYear(baseYearForFrac);
            return JsValue.FromNumber(diff.Years + remainingDays / daySpan);
        }
        else if (unit == "month")
        {
            long epochAnchor = IsoMath.CivilToEpochDays(relTo.date.Year, relTo.date.Month, relTo.date.Day);
            var afterWholeMonths = sys.Add(relTo.date, 0, diff.Years * 12 + diff.Months, 0, 0, constrain: true, out _);
            long epochAfterMonths = IsoMath.CivilToEpochDays(afterWholeMonths.Year, afterWholeMonths.Month, afterWholeMonths.Day);
            remainingDays = (double)(epochAnchor + totalDays - epochAfterMonths) + timeFraction;
            // Approximate month length from the month we're in
            int mStart = afterWholeMonths.Month;
            int mStartYear = afterWholeMonths.Year;
            daySpan = sys.DaysInMonthOrdinal(mStartYear, mStart);
            return JsValue.FromNumber(diff.Years * 12.0 + diff.Months + remainingDays / daySpan);
        }
        else if (unit == "week")
        {
            return JsValue.FromNumber(diff.Years * 52.1775 + diff.Months * 4.34524 + diff.Weeks + (remainingDays + timeFraction) / 7.0);
        }
        return JsValue.FromNumber(totalDaysDouble);
    }

    private static JsValue CloneTemporal(IBuiltinContext ctx, JsHeap h, JsObject orig)
    {
        var clone = new JsObject();
        if (orig.TryGetProperty("_v", x => h.GetObject(x), out var dd) && dd.Value.Tag == JsValueTag.Object)
            clone.DefineOwnProperty("_v", new JsPropertyDescriptor(dd.Value, false, false, false));
        if (orig.PrototypeHandle is { } ph) clone.SetPrototype(ph);
        return JsValue.FromObject(h.AllocateObject(clone, AllocationSite.Current()));
    }
}
