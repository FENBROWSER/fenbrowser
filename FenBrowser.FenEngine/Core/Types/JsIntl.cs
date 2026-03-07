using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using FenBrowser.FenEngine.Core.Interfaces;

namespace FenBrowser.FenEngine.Core.Types
{
    public static class JsIntl
    {
        public static FenObject CreateIntlObject(IExecutionContext context)
        {
            var intl = new FenObject();

            var dateTimeFormat = CreateDateTimeFormat(context);
            AttachSupportedLocalesOf(dateTimeFormat);
            intl.Set("DateTimeFormat", FenValue.FromFunction(dateTimeFormat));

            var numberFormat = CreateNumberFormat(context);
            AttachSupportedLocalesOf(numberFormat);
            intl.Set("NumberFormat", FenValue.FromFunction(numberFormat));

            var collator = CreateCollator(context);
            AttachSupportedLocalesOf(collator);
            intl.Set("Collator", FenValue.FromFunction(collator));

            return intl;
        }

        private sealed class IntlDateTimeFormatOptions
        {
            public string Locale { get; set; }
            public string Weekday { get; set; }
            public string Year { get; set; }
            public string Month { get; set; }
            public string Day { get; set; }
            public string Hour { get; set; }
            public string Minute { get; set; }
            public string Second { get; set; }
            public bool Hour12 { get; set; }
            public string TimeZone { get; set; }
            public string TimeZoneName { get; set; }
        }

        private sealed class IntlNumberFormatOptions
        {
            public string Locale { get; set; }
            public string Style { get; set; } = "decimal";
            public string Currency { get; set; } = "USD";
            public string CurrencyDisplay { get; set; } = "symbol";
            public int MinimumFractionDigits { get; set; } = 0;
            public int MaximumFractionDigits { get; set; } = 3;
            public bool UseGrouping { get; set; } = true;
            public string SignDisplay { get; set; } = "auto";
        }

        private sealed class IntlCollatorOptions
        {
            public string Locale { get; set; }
            public string Sensitivity { get; set; } = "variant";
            public bool Numeric { get; set; }
            public bool IgnorePunctuation { get; set; }
            public string CaseFirst { get; set; } = "false";
            public string Usage { get; set; } = "sort";
        }

        private static FenFunction CreateDateTimeFormat(IExecutionContext context)
        {
            var ctor = new FenFunction("DateTimeFormat", (args, thisVal) =>
            {
                var locales = args.Length > 0 ? args[0] : FenValue.Undefined;
                var options = args.Length > 1 ? args[1] : FenValue.Undefined;

                var culture = ResolveCulture(locales);
                var formatOptions = ParseDateTimeFormatOptions(culture, options);

                var formatter = new FenObject();
                formatter.Set("format", FenValue.FromFunction(new FenFunction("format", (fArgs, fThis) =>
                {
                    var value = fArgs.Length > 0 ? fArgs[0] : FenValue.Undefined;
                    var dateTime = ResolveDateTime(value, culture, formatOptions.TimeZone);
                    return FenValue.FromString(FormatDateTime(dateTime, culture, formatOptions));
                })));

                formatter.Set("resolvedOptions", FenValue.FromFunction(new FenFunction("resolvedOptions", (rArgs, rThis) =>
                {
                    return FenValue.FromObject(CreateDateTimeResolvedOptions(formatOptions));
                })));

                return FenValue.FromObject(formatter);
            });

            ctor.NativeLength = 0;
            return ctor;
        }

        private static FenFunction CreateNumberFormat(IExecutionContext context)
        {
            var ctor = new FenFunction("NumberFormat", (args, thisVal) =>
            {
                var locales = args.Length > 0 ? args[0] : FenValue.Undefined;
                var options = args.Length > 1 ? args[1] : FenValue.Undefined;

                var culture = ResolveCulture(locales);
                var formatOptions = ParseNumberFormatOptions(culture, options);

                var formatter = new FenObject();
                formatter.Set("format", FenValue.FromFunction(new FenFunction("format", (fArgs, fThis) =>
                {
                    double number = fArgs.Length > 0 ? fArgs[0].ToNumber() : double.NaN;
                    return FenValue.FromString(FormatNumber(number, culture, formatOptions));
                })));

                formatter.Set("resolvedOptions", FenValue.FromFunction(new FenFunction("resolvedOptions", (rArgs, rThis) =>
                {
                    return FenValue.FromObject(CreateNumberResolvedOptions(formatOptions));
                })));

                return FenValue.FromObject(formatter);
            });

            ctor.NativeLength = 0;
            return ctor;
        }

        private static FenFunction CreateCollator(IExecutionContext context)
        {
            var ctor = new FenFunction("Collator", (args, thisVal) =>
            {
                var locales = args.Length > 0 ? args[0] : FenValue.Undefined;
                var options = args.Length > 1 ? args[1] : FenValue.Undefined;

                var culture = ResolveCulture(locales);
                var collatorOptions = ParseCollatorOptions(culture, options);
                var compareOptions = BuildCompareOptions(collatorOptions);

                var collator = new FenObject();
                collator.Set("compare", FenValue.FromFunction(new FenFunction("compare", (cArgs, cThis) =>
                {
                    string left = cArgs.Length > 0 ? cArgs[0].ToString() : string.Empty;
                    string right = cArgs.Length > 1 ? cArgs[1].ToString() : string.Empty;
                    int result = culture.CompareInfo.Compare(left, right, compareOptions);
                    if (result < 0) return FenValue.FromNumber(-1);
                    if (result > 0) return FenValue.FromNumber(1);
                    return FenValue.FromNumber(0);
                })));

                collator.Set("resolvedOptions", FenValue.FromFunction(new FenFunction("resolvedOptions", (rArgs, rThis) =>
                {
                    return FenValue.FromObject(CreateCollatorResolvedOptions(collatorOptions));
                })));

                return FenValue.FromObject(collator);
            });

            ctor.NativeLength = 0;
            return ctor;
        }

        private static void AttachSupportedLocalesOf(FenFunction ctor)
        {
            ctor.Set("supportedLocalesOf", FenValue.FromFunction(new FenFunction("supportedLocalesOf", (args, thisVal) =>
            {
                var locales = args.Length > 0 ? args[0] : FenValue.Undefined;
                var supported = new List<FenValue>();
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var tag in EnumerateLocaleTags(locales))
                {
                    if (!TryCreateCulture(tag, out var culture))
                    {
                        continue;
                    }

                    if (seen.Add(culture.Name))
                    {
                        supported.Add(FenValue.FromString(culture.Name));
                    }
                }

                return FenValue.FromObject(CreateArray(supported));
            })));
        }

        private static CultureInfo ResolveCulture(FenValue locales)
        {
            foreach (var tag in EnumerateLocaleTags(locales))
            {
                if (TryCreateCulture(tag, out var culture))
                {
                    return culture;
                }
            }

            return CultureInfo.CurrentCulture;
        }

        private static IEnumerable<string> EnumerateLocaleTags(FenValue locales)
        {
            if (locales.IsUndefined || locales.IsNull)
            {
                yield break;
            }

            if (locales.IsString)
            {
                var locale = NormalizeLocaleTag(locales.ToString());
                if (!string.IsNullOrWhiteSpace(locale))
                {
                    yield return locale;
                }

                yield break;
            }

            if (!locales.IsObject)
            {
                yield break;
            }

            var obj = locales.AsObject();
            if (obj == null)
            {
                yield break;
            }

            var lengthValue = obj.Get("length");
            if (lengthValue.IsNumber)
            {
                int length = (int)Math.Max(0, lengthValue.ToNumber());
                for (int index = 0; index < length; index++)
                {
                    var entry = obj.Get(index.ToString());
                    if (!entry.IsString)
                    {
                        continue;
                    }

                    var locale = NormalizeLocaleTag(entry.ToString());
                    if (!string.IsNullOrWhiteSpace(locale))
                    {
                        yield return locale;
                    }
                }

                yield break;
            }

            var localeValue = obj.Get("locale");
            if (localeValue.IsString)
            {
                var locale = NormalizeLocaleTag(localeValue.ToString());
                if (!string.IsNullOrWhiteSpace(locale))
                {
                    yield return locale;
                }
            }
        }

        private static string NormalizeLocaleTag(string locale)
        {
            return string.IsNullOrWhiteSpace(locale)
                ? string.Empty
                : locale.Trim().Replace('_', '-');
        }

        private static bool TryCreateCulture(string locale, out CultureInfo culture)
        {
            try
            {
                culture = CultureInfo.GetCultureInfo(locale);
                return true;
            }
            catch (CultureNotFoundException)
            {
                culture = null;
                return false;
            }
        }

        private static IntlDateTimeFormatOptions ParseDateTimeFormatOptions(CultureInfo culture, FenValue options)
        {
            var result = new IntlDateTimeFormatOptions
            {
                Locale = culture.Name,
                Year = "numeric",
                Month = "numeric",
                Day = "numeric",
                Hour12 = culture.DateTimeFormat.ShortTimePattern.Contains("tt"),
                TimeZone = TimeZoneInfo.Local.Id
            };

            if (!options.IsObject)
            {
                return result;
            }

            var obj = options.AsObject();
            if (obj == null)
            {
                return result;
            }

            result.Weekday = GetStringOption(obj, "weekday");
            result.Year = GetStringOption(obj, "year", result.Year);
            result.Month = GetStringOption(obj, "month", result.Month);
            result.Day = GetStringOption(obj, "day", result.Day);
            result.Hour = GetStringOption(obj, "hour");
            result.Minute = GetStringOption(obj, "minute");
            result.Second = GetStringOption(obj, "second");
            result.TimeZone = GetStringOption(obj, "timeZone", result.TimeZone);
            result.TimeZoneName = GetStringOption(obj, "timeZoneName");

            var hour12 = obj.Get("hour12");
            if (hour12.IsBoolean)
            {
                result.Hour12 = hour12.ToBoolean();
            }

            return result;
        }

        private static IntlNumberFormatOptions ParseNumberFormatOptions(CultureInfo culture, FenValue options)
        {
            var result = new IntlNumberFormatOptions
            {
                Locale = culture.Name
            };

            if (!options.IsObject)
            {
                return result;
            }

            var obj = options.AsObject();
            if (obj == null)
            {
                return result;
            }

            result.Style = GetStringOption(obj, "style", result.Style);
            result.Currency = GetStringOption(obj, "currency", result.Currency).ToUpperInvariant();
            result.CurrencyDisplay = GetStringOption(obj, "currencyDisplay", result.CurrencyDisplay);
            result.SignDisplay = GetStringOption(obj, "signDisplay", result.SignDisplay);

            var minFraction = obj.Get("minimumFractionDigits");
            if (minFraction.IsNumber)
            {
                result.MinimumFractionDigits = ClampFractionDigits((int)minFraction.ToNumber());
            }

            var maxFraction = obj.Get("maximumFractionDigits");
            if (maxFraction.IsNumber)
            {
                result.MaximumFractionDigits = ClampFractionDigits((int)maxFraction.ToNumber());
            }

            if (result.MaximumFractionDigits < result.MinimumFractionDigits)
            {
                result.MaximumFractionDigits = result.MinimumFractionDigits;
            }

            var useGrouping = obj.Get("useGrouping");
            if (useGrouping.IsBoolean)
            {
                result.UseGrouping = useGrouping.ToBoolean();
            }

            if (string.Equals(result.Style, "currency", StringComparison.OrdinalIgnoreCase)
                && !maxFraction.IsNumber
                && !minFraction.IsNumber)
            {
                result.MinimumFractionDigits = 2;
                result.MaximumFractionDigits = 2;
            }

            return result;
        }

        private static IntlCollatorOptions ParseCollatorOptions(CultureInfo culture, FenValue options)
        {
            var result = new IntlCollatorOptions
            {
                Locale = culture.Name
            };

            if (!options.IsObject)
            {
                return result;
            }

            var obj = options.AsObject();
            if (obj == null)
            {
                return result;
            }

            result.Sensitivity = GetStringOption(obj, "sensitivity", result.Sensitivity);
            result.CaseFirst = GetStringOption(obj, "caseFirst", result.CaseFirst);
            result.Usage = GetStringOption(obj, "usage", result.Usage);

            var numeric = obj.Get("numeric");
            if (numeric.IsBoolean)
            {
                result.Numeric = numeric.ToBoolean();
            }

            var ignorePunctuation = obj.Get("ignorePunctuation");
            if (ignorePunctuation.IsBoolean)
            {
                result.IgnorePunctuation = ignorePunctuation.ToBoolean();
            }

            return result;
        }

        private static CompareOptions BuildCompareOptions(IntlCollatorOptions options)
        {
            CompareOptions compareOptions = CompareOptions.None;

            switch ((options.Sensitivity ?? string.Empty).ToLowerInvariant())
            {
                case "base":
                    compareOptions |= CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace | CompareOptions.IgnoreSymbols;
                    break;
                case "accent":
                    compareOptions |= CompareOptions.IgnoreCase;
                    break;
                case "case":
                    compareOptions |= CompareOptions.IgnoreNonSpace;
                    break;
            }

            if (options.Numeric)
            {
                compareOptions |= CompareOptions.None;
            }

            if (options.IgnorePunctuation)
            {
                compareOptions |= CompareOptions.IgnoreSymbols;
            }

            return compareOptions;
        }

        private static DateTime ResolveDateTime(FenValue value, CultureInfo culture, string timeZoneId)
        {
            DateTime dateTime;

            if (value.IsUndefined || value.IsNull)
            {
                dateTime = DateTime.Now;
            }
            else if (value.IsNumber)
            {
                dateTime = DateTimeOffset.FromUnixTimeMilliseconds((long)value.ToNumber()).LocalDateTime;
            }
            else if (value.IsString)
            {
                if (!DateTime.TryParse(value.ToString(), culture, DateTimeStyles.RoundtripKind, out dateTime)
                    && !DateTime.TryParse(value.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out dateTime))
                    {
                    dateTime = DateTime.Now;
                }
            }
            else if (value.IsObject && value.AsObject() is FenObject fenObject)
            {
                if (fenObject.NativeObject is DateTime nativeDateTime)
                {
                    dateTime = nativeDateTime;
                }
                else if (fenObject.NativeObject is DateTimeOffset nativeDateTimeOffset)
                {
                    dateTime = nativeDateTimeOffset.LocalDateTime;
                }
                else
                {
                    dateTime = DateTime.Now;
                }
            }
            else
            {
                dateTime = DateTime.Now;
            }

            return ConvertToRequestedTimeZone(dateTime, timeZoneId);
        }

        private static DateTime ConvertToRequestedTimeZone(DateTime dateTime, string timeZoneId)
        {
            if (string.IsNullOrWhiteSpace(timeZoneId))
            {
                return dateTime;
            }

            try
            {
                var targetZone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
                return TimeZoneInfo.ConvertTime(dateTime, targetZone);
            }
            catch (TimeZoneNotFoundException)
            {
                return dateTime;
            }
            catch (InvalidTimeZoneException)
            {
                return dateTime;
            }
        }

        private static string FormatDateTime(DateTime dateTime, CultureInfo culture, IntlDateTimeFormatOptions options)
        {
            var builder = new StringBuilder();
            bool wrote = false;

            if (!string.IsNullOrEmpty(options.Weekday))
            {
                builder.Append(GetWeekdayValue(dateTime, culture, options.Weekday));
                wrote = true;
            }

            var dateParts = BuildDateParts(dateTime, culture, options);
            if (dateParts.Count > 0)
            {
                if (wrote)
                {
                    builder.Append(", ");
                }

                builder.Append(string.Join(culture.DateTimeFormat.DateSeparator, dateParts));
                wrote = true;
            }

            var timeParts = BuildTimeParts(dateTime, culture, options);
            if (timeParts.Count > 0)
            {
                if (wrote)
                {
                    builder.Append(", ");
                }

                builder.Append(string.Join(culture.DateTimeFormat.TimeSeparator, timeParts));

                if (options.Hour12)
                {
                    string dayPeriod = dateTime.Hour >= 12
                        ? culture.DateTimeFormat.PMDesignator
                        : culture.DateTimeFormat.AMDesignator;

                    if (!string.IsNullOrEmpty(dayPeriod))
                    {
                        builder.Append(' ');
                        builder.Append(dayPeriod);
                    }
                }

                wrote = true;
            }

            if (!string.IsNullOrEmpty(options.TimeZoneName))
            {
                string zoneName = GetTimeZoneDisplayName(dateTime, options.TimeZone, options.TimeZoneName);
                if (!string.IsNullOrEmpty(zoneName))
                {
                    if (wrote)
                    {
                        builder.Append(' ');
                    }

                    builder.Append(zoneName);
                    wrote = true;
                }
            }

            if (!wrote)
            {
                return dateTime.ToString(culture);
            }

            return builder.ToString();
        }

        private static List<string> BuildDateParts(DateTime dateTime, CultureInfo culture, IntlDateTimeFormatOptions options)
        {
            var parts = new List<string>();
            foreach (var token in GetDatePartOrder(culture))
            {
                switch (token)
                {
                    case 'd':
                        if (!string.IsNullOrEmpty(options.Day))
                        {
                            parts.Add(options.Day == "2-digit"
                                ? dateTime.Day.ToString("00", culture)
                                : dateTime.Day.ToString(culture));
                        }
                        break;
                    case 'M':
                        if (!string.IsNullOrEmpty(options.Month))
                        {
                            parts.Add(GetMonthValue(dateTime, culture, options.Month));
                        }
                        break;
                    case 'y':
                        if (!string.IsNullOrEmpty(options.Year))
                        {
                            parts.Add(options.Year == "2-digit"
                                ? (dateTime.Year % 100).ToString("00", culture)
                                : dateTime.Year.ToString("0000", culture));
                        }
                        break;
                }
            }

            return parts;
        }

        private static List<string> BuildTimeParts(DateTime dateTime, CultureInfo culture, IntlDateTimeFormatOptions options)
        {
            var parts = new List<string>();
            if (!string.IsNullOrEmpty(options.Hour))
            {
                int hour = options.Hour12 ? (dateTime.Hour % 12 == 0 ? 12 : dateTime.Hour % 12) : dateTime.Hour;
                parts.Add(options.Hour == "2-digit"
                    ? hour.ToString("00", culture)
                    : hour.ToString(culture));
            }

            if (!string.IsNullOrEmpty(options.Minute))
            {
                parts.Add(options.Minute == "2-digit"
                    ? dateTime.Minute.ToString("00", culture)
                    : dateTime.Minute.ToString(culture));
            }

            if (!string.IsNullOrEmpty(options.Second))
            {
                parts.Add(options.Second == "2-digit"
                    ? dateTime.Second.ToString("00", culture)
                    : dateTime.Second.ToString(culture));
            }

            return parts;
        }

        private static List<char> GetDatePartOrder(CultureInfo culture)
        {
            var order = new List<char>(3);
            foreach (char ch in culture.DateTimeFormat.ShortDatePattern)
            {
                char normalized;
                switch (ch)
                {
                    case 'd':
                    case 'M':
                    case 'y':
                        normalized = ch;
                        break;
                    default:
                        continue;
                }

                if (!order.Contains(normalized))
                {
                    order.Add(normalized);
                }
            }

            if (order.Count == 0)
            {
                order.Add('M');
                order.Add('d');
                order.Add('y');
            }

            return order;
        }

        private static string GetWeekdayValue(DateTime dateTime, CultureInfo culture, string weekday)
        {
            return string.Equals(weekday, "long", StringComparison.OrdinalIgnoreCase)
                ? culture.DateTimeFormat.GetDayName(dateTime.DayOfWeek)
                : culture.DateTimeFormat.GetAbbreviatedDayName(dateTime.DayOfWeek);
        }

        private static string GetMonthValue(DateTime dateTime, CultureInfo culture, string month)
        {
            switch ((month ?? string.Empty).ToLowerInvariant())
            {
                case "2-digit":
                    return dateTime.Month.ToString("00", culture);
                case "short":
                    return culture.DateTimeFormat.GetAbbreviatedMonthName(dateTime.Month);
                case "long":
                    return culture.DateTimeFormat.GetMonthName(dateTime.Month);
                default:
                    return dateTime.Month.ToString(culture);
            }
        }

        private static string GetTimeZoneDisplayName(DateTime dateTime, string timeZoneId, string format)
        {
            try
            {
                var timeZone = string.IsNullOrWhiteSpace(timeZoneId)
                    ? TimeZoneInfo.Local
                    : TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);

                if (string.Equals(format, "long", StringComparison.OrdinalIgnoreCase))
                {
                    return timeZone.IsDaylightSavingTime(dateTime) ? timeZone.DaylightName : timeZone.StandardName;
                }

                var offset = timeZone.GetUtcOffset(dateTime);
                return $"GMT{offset:+hh\\:mm;-hh\\:mm;+00\\:00}";
            }
            catch (TimeZoneNotFoundException)
            {
                return string.Empty;
            }
            catch (InvalidTimeZoneException)
            {
                return string.Empty;
            }
        }

        private static string FormatNumber(double number, CultureInfo culture, IntlNumberFormatOptions options)
        {
            if (double.IsNaN(number) || double.IsInfinity(number))
            {
                return number.ToString(culture);
            }

            var numberFormat = (NumberFormatInfo)culture.NumberFormat.Clone();
            if (!options.UseGrouping)
            {
                numberFormat.NumberGroupSeparator = string.Empty;
                numberFormat.CurrencyGroupSeparator = string.Empty;
                numberFormat.PercentGroupSeparator = string.Empty;
            }

            string formatSpecifier;
            switch ((options.Style ?? string.Empty).ToLowerInvariant())
            {
                case "currency":
                    formatSpecifier = "C" + options.MaximumFractionDigits.ToString(CultureInfo.InvariantCulture);
                    numberFormat.CurrencyDecimalDigits = options.MaximumFractionDigits;
                    numberFormat.CurrencySymbol = ResolveCurrencySymbol(culture, options.Currency, options.CurrencyDisplay);
                    break;
                case "percent":
                    formatSpecifier = "P" + options.MaximumFractionDigits.ToString(CultureInfo.InvariantCulture);
                    numberFormat.PercentDecimalDigits = options.MaximumFractionDigits;
                    break;
                default:
                    formatSpecifier = (options.UseGrouping ? "N" : "F") + options.MaximumFractionDigits.ToString(CultureInfo.InvariantCulture);
                    numberFormat.NumberDecimalDigits = options.MaximumFractionDigits;
                    break;
            }

            string formatted = number.ToString(formatSpecifier, numberFormat);
            return ApplySignDisplay(formatted, number, numberFormat, options.SignDisplay);
        }

        private static string ResolveCurrencySymbol(CultureInfo culture, string currencyCode, string display)
        {
            if (string.Equals(display, "code", StringComparison.OrdinalIgnoreCase)
                || string.Equals(display, "name", StringComparison.OrdinalIgnoreCase))
            {
                return currencyCode;
            }

            try
            {
                var region = new RegionInfo(culture.Name);
                if (string.Equals(region.ISOCurrencySymbol, currencyCode, StringComparison.OrdinalIgnoreCase))
                {
                    return region.CurrencySymbol;
                }
            }
            catch (ArgumentException)
            {
            }

            return currencyCode;
        }

        private static string ApplySignDisplay(string formatted, double number, NumberFormatInfo numberFormat, string signDisplay)
        {
            switch ((signDisplay ?? "auto").ToLowerInvariant())
            {
                case "always":
                    if (number > 0)
                    {
                        return numberFormat.PositiveSign + formatted;
                    }
                    return formatted;
                case "exceptzero":
                    if (number > 0 && number != 0)
                    {
                        return numberFormat.PositiveSign + formatted;
                    }
                    return formatted;
                case "never":
                    return formatted.Replace(numberFormat.NegativeSign, string.Empty)
                        .Replace(numberFormat.PositiveSign, string.Empty);
                default:
                    return formatted;
            }
        }

        private static FenObject CreateDateTimeResolvedOptions(IntlDateTimeFormatOptions options)
        {
            var resolved = new FenObject();
            resolved.Set("locale", FenValue.FromString(options.Locale ?? string.Empty));
            resolved.Set("calendar", FenValue.FromString("gregory"));
            resolved.Set("numberingSystem", FenValue.FromString("latn"));
            resolved.Set("timeZone", FenValue.FromString(options.TimeZone ?? TimeZoneInfo.Local.Id));

            SetIfPresent(resolved, "weekday", options.Weekday);
            SetIfPresent(resolved, "year", options.Year);
            SetIfPresent(resolved, "month", options.Month);
            SetIfPresent(resolved, "day", options.Day);
            SetIfPresent(resolved, "hour", options.Hour);
            SetIfPresent(resolved, "minute", options.Minute);
            SetIfPresent(resolved, "second", options.Second);
            resolved.Set("hour12", FenValue.FromBoolean(options.Hour12));
            SetIfPresent(resolved, "timeZoneName", options.TimeZoneName);
            return resolved;
        }

        private static FenObject CreateNumberResolvedOptions(IntlNumberFormatOptions options)
        {
            var resolved = new FenObject();
            resolved.Set("locale", FenValue.FromString(options.Locale ?? string.Empty));
            resolved.Set("numberingSystem", FenValue.FromString("latn"));
            resolved.Set("style", FenValue.FromString(options.Style ?? "decimal"));
            resolved.Set("useGrouping", FenValue.FromBoolean(options.UseGrouping));
            resolved.Set("minimumFractionDigits", FenValue.FromNumber(options.MinimumFractionDigits));
            resolved.Set("maximumFractionDigits", FenValue.FromNumber(options.MaximumFractionDigits));
            resolved.Set("signDisplay", FenValue.FromString(options.SignDisplay ?? "auto"));

            if (string.Equals(options.Style, "currency", StringComparison.OrdinalIgnoreCase))
            {
                resolved.Set("currency", FenValue.FromString(options.Currency ?? "USD"));
                resolved.Set("currencyDisplay", FenValue.FromString(options.CurrencyDisplay ?? "symbol"));
            }

            return resolved;
        }

        private static FenObject CreateCollatorResolvedOptions(IntlCollatorOptions options)
        {
            var resolved = new FenObject();
            resolved.Set("locale", FenValue.FromString(options.Locale ?? string.Empty));
            resolved.Set("usage", FenValue.FromString(options.Usage ?? "sort"));
            resolved.Set("sensitivity", FenValue.FromString(options.Sensitivity ?? "variant"));
            resolved.Set("ignorePunctuation", FenValue.FromBoolean(options.IgnorePunctuation));
            resolved.Set("numeric", FenValue.FromBoolean(options.Numeric));
            resolved.Set("caseFirst", FenValue.FromString(options.CaseFirst ?? "false"));
            resolved.Set("collation", FenValue.FromString("default"));
            return resolved;
        }

        private static string GetStringOption(IObject options, string key, string defaultValue = null)
        {
            var value = options.Get(key);
            return value.IsString ? value.ToString() : defaultValue;
        }

        private static int ClampFractionDigits(int digits)
        {
            if (digits < 0)
            {
                return 0;
            }

            if (digits > 20)
            {
                return 20;
            }

            return digits;
        }

        private static void SetIfPresent(FenObject target, string key, string value)
        {
            if (!string.IsNullOrEmpty(value))
            {
                target.Set(key, FenValue.FromString(value));
            }
        }

        private static FenObject CreateArray(IReadOnlyList<FenValue> values)
        {
            var array = new FenObject();
            for (int index = 0; index < values.Count; index++)
            {
                array.Set(index.ToString(), values[index]);
            }

            array.Set("length", FenValue.FromNumber(values.Count));
            return array;
        }
    }
}
