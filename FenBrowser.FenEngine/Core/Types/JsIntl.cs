using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using FenBrowser.FenEngine.Core.Interfaces;
using FenBrowser.FenEngine.Errors;

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

            var pluralRules = CreatePluralRules(context);
            AttachSupportedLocalesOf(pluralRules);
            intl.Set("PluralRules", FenValue.FromFunction(pluralRules));

            var relativeTimeFormat = CreateRelativeTimeFormat(context);
            AttachSupportedLocalesOf(relativeTimeFormat);
            intl.Set("RelativeTimeFormat", FenValue.FromFunction(relativeTimeFormat));

            var listFormat = CreateListFormat(context);
            AttachSupportedLocalesOf(listFormat);
            intl.Set("ListFormat", FenValue.FromFunction(listFormat));

            var displayNames = CreateDisplayNames(context);
            intl.Set("DisplayNames", FenValue.FromFunction(displayNames));

            var segmenter = CreateSegmenter(context);
            AttachSupportedLocalesOf(segmenter);
            intl.Set("Segmenter", FenValue.FromFunction(segmenter));

            // ECMA-402 §9.2.1 — Intl.getCanonicalLocales
            intl.Set("getCanonicalLocales", FenValue.FromFunction(new FenFunction("getCanonicalLocales", (args, _) =>
            {
                var result = new FenObject();
                if (args.Length == 0)
                {
                    result.Set("length", FenValue.FromNumber(0));
                    return FenValue.FromObject(result);
                }

                var locales = args[0].IsObject
                    ? GetLocaleList(args[0])
                    : new[] { args[0].ToString2() };

                int i = 0;
                foreach (var loc in locales)
                {
                    if (string.IsNullOrEmpty(loc))
                    {
                        continue;
                    }

                    try
                    {
                        var ci = CultureInfo.GetCultureInfo(loc);
                        result.Set(i.ToString(), FenValue.FromString(ci.Name));
                        i++;
                    }
                    catch (CultureNotFoundException)
                    {
                        throw new FenRangeError($"Invalid language tag: {loc}");
                    }
                }

                result.Set("length", FenValue.FromNumber(i));
                return FenValue.FromObject(result);
            })));

            // ECMA-402 §9.2.10 — Intl.supportedValuesOf
            intl.Set("supportedValuesOf", FenValue.FromFunction(new FenFunction("supportedValuesOf", (args, _) =>
            {
                var key = args.Length > 0 ? args[0].ToString2() : string.Empty;
                string[] values;
                switch (key)
                {
                    case "calendar":
                        values = new[] { "gregory", "iso8601" };
                        break;
                    case "collation":
                        values = new[] { "default", "search", "standard", "unicode" };
                        break;
                    case "currency":
                        values = CultureInfo.GetCultures(CultureTypes.SpecificCultures)
                            .Select(c => {
                                try { return new RegionInfo(c.Name).ISOCurrencySymbol; }
                                catch { return null; }
                            })
                            .Where(s => s != null)
                            .Distinct(StringComparer.Ordinal)
                            .OrderBy(s => s, StringComparer.Ordinal)
                            .ToArray();
                        break;
                    case "numberingSystem":
                        values = new[] { "arab", "arabext", "bali", "beng", "deva", "fullwide", "gujr", "guru", "hanidec", "khmr", "knda", "laoo", "latn", "limb", "mlym", "mong", "mymr", "orya", "tamldec", "telu", "thai", "tibt" };
                        break;
                    case "timeZone":
                        values = TimeZoneInfo.GetSystemTimeZones()
                            .Select(tz => tz.Id)
                            .OrderBy(id => id, StringComparer.Ordinal)
                            .ToArray();
                        break;
                    case "unit":
                        values = new[] { "acre", "bit", "byte", "celsius", "centimeter", "day", "degree", "fahrenheit", "fluid-ounce", "foot", "gallon", "gigabit", "gigabyte", "gram", "hectare", "hour", "inch", "kilobit", "kilobyte", "kilogram", "kilometer", "liter", "megabit", "megabyte", "meter", "microsecond", "mile", "mile-scandinavian", "milliliter", "millimeter", "millisecond", "minute", "month", "nanosecond", "ounce", "percent", "petabyte", "pound", "second", "stone", "terabit", "terabyte", "week", "yard", "year" };
                        break;
                    default:
                        throw new FenRangeError($"Invalid key: {key}");
                }

                return FenValue.FromObject(CreateArray(values.Select(v => FenValue.FromString(v)).ToList()));
            })));

            return intl;
        }

        // ── Option classes ────────────────────────────────────────────────────

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
            // ECMA-402 §15.3.3: style-dependent defaults applied in ParseNumberFormatOptions
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

        // ── DateTimeFormat (ECMA-402 §11) ────────────────────────────────────

        private static FenFunction CreateDateTimeFormat(IExecutionContext context)
        {
            var ctor = new FenFunction("DateTimeFormat", (args, thisVal) =>
            {
                var locales = args.Length > 0 ? args[0] : FenValue.Undefined;
                var options = args.Length > 1 ? args[1] : FenValue.Undefined;

                var culture = ResolveCulture(locales);
                var formatOptions = ParseDateTimeFormatOptions(culture, options);

                var formatter = new FenObject();

                // §11.3.4 format()
                formatter.Set("format", FenValue.FromFunction(new FenFunction("format", (fArgs, fThis) =>
                {
                    var value = fArgs.Length > 0 ? fArgs[0] : FenValue.Undefined;
                    var dateTime = ResolveDateTime(value, culture, formatOptions.TimeZone);
                    return FenValue.FromString(FormatDateTime(dateTime, culture, formatOptions));
                })));

                // §11.3.5 formatToParts()
                formatter.Set("formatToParts", FenValue.FromFunction(new FenFunction("formatToParts", (fArgs, fThis) =>
                {
                    var value = fArgs.Length > 0 ? fArgs[0] : FenValue.Undefined;
                    var dateTime = ResolveDateTime(value, culture, formatOptions.TimeZone);
                    var parts = BuildDateTimeFormatParts(dateTime, culture, formatOptions);
                    return FenValue.FromObject(CreateArray(parts));
                })));

                // §11.3.7 resolvedOptions()
                formatter.Set("resolvedOptions", FenValue.FromFunction(new FenFunction("resolvedOptions", (rArgs, rThis) =>
                {
                    return FenValue.FromObject(CreateDateTimeResolvedOptions(formatOptions));
                })));

                formatter.InternalClass = "DateTimeFormat";
                return FenValue.FromObject(formatter);
            });

            ctor.NativeLength = 0;
            return ctor;
        }

        private static List<FenValue> BuildDateTimeFormatParts(DateTime dateTime, CultureInfo culture, IntlDateTimeFormatOptions options)
        {
            var parts = new List<FenValue>();

            void AddPart(string type, string value)
            {
                var obj = new FenObject();
                obj.Set("type", FenValue.FromString(type));
                obj.Set("value", FenValue.FromString(value));
                parts.Add(FenValue.FromObject(obj));
            }

            bool wrote = false;

            if (!string.IsNullOrEmpty(options.Weekday))
            {
                AddPart("weekday", GetWeekdayValue(dateTime, culture, options.Weekday));
                wrote = true;
            }

            // Date parts in locale order
            var datePartOrder = GetDatePartOrder(culture);
            bool dateHasParts = (!string.IsNullOrEmpty(options.Year) || !string.IsNullOrEmpty(options.Month) || !string.IsNullOrEmpty(options.Day))
                                && datePartOrder.Count > 0;

            if (dateHasParts)
            {
                if (wrote)
                {
                    AddPart("literal", ", ");
                }

                bool firstDatePart = true;
                foreach (var token in datePartOrder)
                {
                    string type = null;
                    string val = null;

                    switch (token)
                    {
                        case 'd':
                            if (!string.IsNullOrEmpty(options.Day))
                            {
                                type = "day";
                                val = options.Day == "2-digit"
                                    ? dateTime.Day.ToString("00", culture)
                                    : dateTime.Day.ToString(culture);
                            }
                            break;
                        case 'M':
                            if (!string.IsNullOrEmpty(options.Month))
                            {
                                type = "month";
                                val = GetMonthValue(dateTime, culture, options.Month);
                            }
                            break;
                        case 'y':
                            if (!string.IsNullOrEmpty(options.Year))
                            {
                                type = "year";
                                val = options.Year == "2-digit"
                                    ? (dateTime.Year % 100).ToString("00", culture)
                                    : dateTime.Year.ToString("0000", culture);
                            }
                            break;
                    }

                    if (type != null && val != null)
                    {
                        if (!firstDatePart)
                        {
                            AddPart("literal", culture.DateTimeFormat.DateSeparator);
                        }

                        AddPart(type, val);
                        firstDatePart = false;
                        wrote = true;
                    }
                }
            }

            // Time parts
            bool hasTime = !string.IsNullOrEmpty(options.Hour)
                           || !string.IsNullOrEmpty(options.Minute)
                           || !string.IsNullOrEmpty(options.Second);

            if (hasTime)
            {
                if (wrote)
                {
                    AddPart("literal", ", ");
                }

                bool firstTimePart = true;

                if (!string.IsNullOrEmpty(options.Hour))
                {
                    int hour = options.Hour12 ? (dateTime.Hour % 12 == 0 ? 12 : dateTime.Hour % 12) : dateTime.Hour;
                    AddPart("hour", options.Hour == "2-digit" ? hour.ToString("00", culture) : hour.ToString(culture));
                    firstTimePart = false;
                    wrote = true;
                }

                if (!string.IsNullOrEmpty(options.Minute))
                {
                    if (!firstTimePart)
                    {
                        AddPart("literal", culture.DateTimeFormat.TimeSeparator);
                    }

                    AddPart("minute", options.Minute == "2-digit"
                        ? dateTime.Minute.ToString("00", culture)
                        : dateTime.Minute.ToString(culture));
                    firstTimePart = false;
                    wrote = true;
                }

                if (!string.IsNullOrEmpty(options.Second))
                {
                    if (!firstTimePart)
                    {
                        AddPart("literal", culture.DateTimeFormat.TimeSeparator);
                    }

                    AddPart("second", options.Second == "2-digit"
                        ? dateTime.Second.ToString("00", culture)
                        : dateTime.Second.ToString(culture));
                    wrote = true;
                }

                if (options.Hour12 && !string.IsNullOrEmpty(options.Hour))
                {
                    string dayPeriod = dateTime.Hour >= 12
                        ? culture.DateTimeFormat.PMDesignator
                        : culture.DateTimeFormat.AMDesignator;

                    if (!string.IsNullOrEmpty(dayPeriod))
                    {
                        AddPart("literal", " ");
                        AddPart("dayPeriod", dayPeriod);
                    }
                }
            }

            if (!string.IsNullOrEmpty(options.TimeZoneName))
            {
                string zoneName = GetTimeZoneDisplayName(dateTime, options.TimeZone, options.TimeZoneName);
                if (!string.IsNullOrEmpty(zoneName))
                {
                    if (wrote)
                    {
                        AddPart("literal", " ");
                    }

                    AddPart("timeZoneName", zoneName);
                }
            }

            if (parts.Count == 0)
            {
                // fallback: emit entire formatted string as a literal
                AddPart("literal", dateTime.ToString(culture));
            }

            return parts;
        }

        // ── NumberFormat (ECMA-402 §15) ───────────────────────────────────────

        private static FenFunction CreateNumberFormat(IExecutionContext context)
        {
            var ctor = new FenFunction("NumberFormat", (args, thisVal) =>
            {
                var locales = args.Length > 0 ? args[0] : FenValue.Undefined;
                var options = args.Length > 1 ? args[1] : FenValue.Undefined;

                var culture = ResolveCulture(locales);
                var formatOptions = ParseNumberFormatOptions(culture, options);

                var formatter = new FenObject();

                // §15.3.4 format()
                formatter.Set("format", FenValue.FromFunction(new FenFunction("format", (fArgs, fThis) =>
                {
                    double number = fArgs.Length > 0 ? fArgs[0].ToNumber() : double.NaN;
                    return FenValue.FromString(FormatNumber(number, culture, formatOptions));
                })));

                // §15.3.6 formatToParts()
                formatter.Set("formatToParts", FenValue.FromFunction(new FenFunction("formatToParts", (fArgs, fThis) =>
                {
                    double number = fArgs.Length > 0 ? fArgs[0].ToNumber() : double.NaN;
                    var parts = BuildNumberFormatParts(number, culture, formatOptions);
                    return FenValue.FromObject(CreateArray(parts));
                })));

                // §15.3.7 formatRange()
                formatter.Set("formatRange", FenValue.FromFunction(new FenFunction("formatRange", (fArgs, fThis) =>
                {
                    double start = fArgs.Length > 0 ? fArgs[0].ToNumber() : 0;
                    double end = fArgs.Length > 1 ? fArgs[1].ToNumber() : 0;
                    var startStr = FormatNumber(start, culture, formatOptions);
                    var endStr = FormatNumber(end, culture, formatOptions);
                    return FenValue.FromString(startStr + " – " + endStr);
                })));

                // §15.3.9 resolvedOptions()
                formatter.Set("resolvedOptions", FenValue.FromFunction(new FenFunction("resolvedOptions", (rArgs, rThis) =>
                {
                    return FenValue.FromObject(CreateNumberResolvedOptions(formatOptions));
                })));

                formatter.InternalClass = "NumberFormat";
                return FenValue.FromObject(formatter);
            });

            ctor.NativeLength = 0;
            return ctor;
        }

        private static List<FenValue> BuildNumberFormatParts(double number, CultureInfo culture, IntlNumberFormatOptions options)
        {
            var parts = new List<FenValue>();

            void AddPart(string type, string value)
            {
                var obj = new FenObject();
                obj.Set("type", FenValue.FromString(type));
                obj.Set("value", FenValue.FromString(value));
                parts.Add(FenValue.FromObject(obj));
            }

            if (double.IsNaN(number))
            {
                AddPart("nan", "NaN");
                return parts;
            }

            if (double.IsPositiveInfinity(number))
            {
                AddPart("infinity", "∞");
                return parts;
            }

            if (double.IsNegativeInfinity(number))
            {
                AddPart("minusSign", culture.NumberFormat.NegativeSign);
                AddPart("infinity", "∞");
                return parts;
            }

            bool isNegative = number < 0;
            double absNumber = Math.Abs(number);

            if (isNegative)
            {
                AddPart("minusSign", culture.NumberFormat.NegativeSign);
            }
            else
            {
                var signDisplay = options.SignDisplay ?? "auto";
                if (string.Equals(signDisplay, "always", StringComparison.OrdinalIgnoreCase)
                    || (string.Equals(signDisplay, "exceptzero", StringComparison.OrdinalIgnoreCase) && number != 0))
                {
                    AddPart("plusSign", culture.NumberFormat.PositiveSign);
                }
            }

            var style = (options.Style ?? "decimal").ToLowerInvariant();

            if (style == "currency")
            {
                string symbol = ResolveCurrencySymbol(culture, options.Currency, options.CurrencyDisplay);
                // Check if symbol goes before or after the number per culture
                bool symbolFirst = !culture.NumberFormat.CurrencyPositivePattern.ToString().StartsWith("n", StringComparison.OrdinalIgnoreCase);
                if (symbolFirst)
                {
                    AddPart("currency", symbol);
                    AddPart("literal", " ");
                }

                AppendIntegerAndFraction(parts, absNumber, culture, options, AddPart);

                if (!symbolFirst)
                {
                    AddPart("literal", " ");
                    AddPart("currency", symbol);
                }

                return parts;
            }

            if (style == "percent")
            {
                AppendIntegerAndFraction(parts, absNumber * 100, culture, options, AddPart);
                AddPart("percentSign", culture.NumberFormat.PercentSymbol);
                return parts;
            }

            // decimal
            AppendIntegerAndFraction(parts, absNumber, culture, options, AddPart);
            return parts;
        }

        private static void AppendIntegerAndFraction(
            List<FenValue> parts,
            double absNumber,
            CultureInfo culture,
            IntlNumberFormatOptions options,
            Action<string, string> addPart)
        {
            // Format using standard specifier to get properly grouped and fractioned string
            var nfi = (NumberFormatInfo)culture.NumberFormat.Clone();
            if (!options.UseGrouping)
            {
                nfi.NumberGroupSeparator = string.Empty;
                nfi.CurrencyGroupSeparator = string.Empty;
                nfi.PercentGroupSeparator = string.Empty;
            }

            nfi.NumberDecimalDigits = options.MaximumFractionDigits;
            string formatted = absNumber.ToString("N" + options.MaximumFractionDigits.ToString(CultureInfo.InvariantCulture), nfi);

            // Split on decimal separator
            var decSep = nfi.NumberDecimalSeparator;
            var grpSep = nfi.NumberGroupSeparator;

            int decIdx = formatted.IndexOf(decSep, StringComparison.Ordinal);
            string intPart = decIdx >= 0 ? formatted.Substring(0, decIdx) : formatted;
            string fracPart = decIdx >= 0 ? formatted.Substring(decIdx + decSep.Length) : null;

            // Trim trailing zeros below minimumFractionDigits
            if (fracPart != null)
            {
                while (fracPart.Length > options.MinimumFractionDigits && fracPart.EndsWith("0", StringComparison.Ordinal))
                {
                    fracPart = fracPart.Substring(0, fracPart.Length - 1);
                }
            }

            // Emit integer with group separators
            if (string.IsNullOrEmpty(grpSep) || !options.UseGrouping)
            {
                addPart("integer", intPart);
            }
            else
            {
                // Split by group separator
                var segments = intPart.Split(new[] { grpSep }, StringSplitOptions.None);
                for (int i = 0; i < segments.Length; i++)
                {
                    addPart("integer", segments[i]);
                    if (i < segments.Length - 1)
                    {
                        addPart("group", grpSep);
                    }
                }
            }

            // Emit fraction
            if (!string.IsNullOrEmpty(fracPart))
            {
                addPart("decimal", decSep);
                addPart("fraction", fracPart);
            }
        }

        // ── Collator (ECMA-402 §10) ───────────────────────────────────────────

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
                    string left = cArgs.Length > 0 ? cArgs[0].ToString2() : string.Empty;
                    string right = cArgs.Length > 1 ? cArgs[1].ToString2() : string.Empty;
                    int result = culture.CompareInfo.Compare(left, right, compareOptions);
                    if (result < 0) return FenValue.FromNumber(-1);
                    if (result > 0) return FenValue.FromNumber(1);
                    return FenValue.FromNumber(0);
                })));

                collator.Set("resolvedOptions", FenValue.FromFunction(new FenFunction("resolvedOptions", (rArgs, rThis) =>
                {
                    return FenValue.FromObject(CreateCollatorResolvedOptions(collatorOptions));
                })));

                collator.InternalClass = "Collator";
                return FenValue.FromObject(collator);
            });

            ctor.NativeLength = 0;
            return ctor;
        }

        // ── PluralRules (ECMA-402 §16) ────────────────────────────────────────

        private static FenFunction CreatePluralRules(IExecutionContext context)
        {
            return new FenFunction("PluralRules", (args, thisVal) =>
            {
                var localeArg = args.Length > 0 && !args[0].IsUndefined && !args[0].IsNull
                    ? args[0]
                    : FenValue.FromString("en");
                var optionsArg = args.Length > 1 ? args[1] : FenValue.Undefined;

                var culture = ResolveCulture(localeArg);
                var optObj = optionsArg.IsObject ? optionsArg.AsObject() : null;
                var typeOpt = optObj != null ? GetStringOption(optObj, "type", "cardinal") : "cardinal";

                var instance = new FenObject();

                // §16.4.4 select()
                instance.Set("select", FenValue.FromFunction(new FenFunction("select", (selArgs, __) =>
                {
                    var n = selArgs.Length > 0 ? selArgs[0].ToNumber() : 0.0;
                    string category = SelectPluralCategory(n, culture, typeOpt);
                    return FenValue.FromString(category);
                })));

                // §16.4.5 selectRange()
                instance.Set("selectRange", FenValue.FromFunction(new FenFunction("selectRange", (selArgs, __) =>
                {
                    return FenValue.FromString("other");
                })));

                // §16.4.6 resolvedOptions()
                instance.Set("resolvedOptions", FenValue.FromFunction(new FenFunction("resolvedOptions", (_, __) =>
                {
                    var opts = new FenObject();
                    opts.Set("locale", FenValue.FromString(culture.Name));
                    opts.Set("type", FenValue.FromString(typeOpt ?? "cardinal"));
                    opts.Set("minimumIntegerDigits", FenValue.FromNumber(1));
                    opts.Set("minimumFractionDigits", FenValue.FromNumber(0));
                    opts.Set("maximumFractionDigits", FenValue.FromNumber(3));
                    // Simplified: real CLDR data would enumerate all applicable categories
                    opts.Set("pluralCategories", FenValue.FromObject(
                        CreateArray(new List<FenValue> { FenValue.FromString("one"), FenValue.FromString("other") })));
                    return FenValue.FromObject(opts);
                })));

                instance.InternalClass = "PluralRules";
                return FenValue.FromObject(instance);
            });
        }

        // ECMA-402 §16 CLDR English plural rules (cardinal and ordinal).
        // For non-English locales we fall back to "one"/"other" which covers most Germanic/Romance languages.
        private static string SelectPluralCategory(double n, CultureInfo culture, string type)
        {
            bool isOrdinal = string.Equals(type, "ordinal", StringComparison.OrdinalIgnoreCase);
            long i = (long)Math.Abs(n);

            if (culture.TwoLetterISOLanguageName == "en")
            {
                if (isOrdinal)
                {
                    long mod10 = i % 10;
                    long mod100 = i % 100;
                    if (mod10 == 1 && mod100 != 11) return "one";   // 1st, 21st …
                    if (mod10 == 2 && mod100 != 12) return "two";   // 2nd, 22nd …
                    if (mod10 == 3 && mod100 != 13) return "few";   // 3rd, 23rd …
                    return "other";
                }
                else
                {
                    return (i == 1 && n == 1.0) ? "one" : "other";
                }
            }

            // Russian / Slavic (two-letter code check)
            if (culture.TwoLetterISOLanguageName == "ru" || culture.TwoLetterISOLanguageName == "uk")
            {
                long mod10 = i % 10;
                long mod100 = i % 100;
                if (mod10 == 1 && mod100 != 11) return "one";
                if (mod10 >= 2 && mod10 <= 4 && (mod100 < 10 || mod100 >= 20)) return "few";
                return "many";
            }

            // Arabic (zero/one/two/few/many/other)
            if (culture.TwoLetterISOLanguageName == "ar")
            {
                if (n == 0) return "zero";
                if (n == 1) return "one";
                if (n == 2) return "two";
                long mod100 = i % 100;
                if (mod100 >= 3 && mod100 <= 10) return "few";
                if (mod100 >= 11 && mod100 <= 99) return "many";
                return "other";
            }

            // Generic: one vs other
            return (n == 1.0) ? "one" : "other";
        }

        // ── RelativeTimeFormat (ECMA-402 §17) ─────────────────────────────────

        private static FenFunction CreateRelativeTimeFormat(IExecutionContext context)
        {
            return new FenFunction("RelativeTimeFormat", (args, thisVal) =>
            {
                var localeArg = args.Length > 0 && !args[0].IsUndefined && !args[0].IsNull
                    ? args[0]
                    : FenValue.FromString("en");
                var optionsArg = args.Length > 1 ? args[1] : FenValue.Undefined;

                var culture = ResolveCulture(localeArg);
                var optObj = optionsArg.IsObject ? optionsArg.AsObject() : null;
                var style = optObj != null ? GetStringOption(optObj, "style", "long") : "long";
                var numeric = optObj != null ? GetStringOption(optObj, "numeric", "always") : "always";

                var instance = new FenObject();

                // §17.4.3 format()
                instance.Set("format", FenValue.FromFunction(new FenFunction("format", (fmtArgs, __) =>
                {
                    if (fmtArgs.Length < 2)
                    {
                        throw new FenTypeError("RelativeTimeFormat.format requires value and unit");
                    }

                    var value = (long)fmtArgs[0].ToNumber();
                    var unit = NormalizeTimeUnit(fmtArgs[1].ToString2());
                    return FenValue.FromString(FormatRelativeTime(value, unit, culture, style, numeric));
                })));

                // §17.4.4 formatToParts()
                instance.Set("formatToParts", FenValue.FromFunction(new FenFunction("formatToParts", (fmtArgs, __) =>
                {
                    if (fmtArgs.Length < 2)
                    {
                        throw new FenTypeError("RelativeTimeFormat.formatToParts requires value and unit");
                    }

                    var value = (long)fmtArgs[0].ToNumber();
                    var unit = NormalizeTimeUnit(fmtArgs[1].ToString2());
                    var parts = BuildRelativeTimeParts(value, unit, culture, style, numeric);
                    return FenValue.FromObject(CreateArray(parts));
                })));

                // §17.4.5 resolvedOptions()
                instance.Set("resolvedOptions", FenValue.FromFunction(new FenFunction("resolvedOptions", (_, __) =>
                {
                    var opts = new FenObject();
                    opts.Set("locale", FenValue.FromString(culture.Name));
                    opts.Set("style", FenValue.FromString(style ?? "long"));
                    opts.Set("numeric", FenValue.FromString(numeric ?? "always"));
                    opts.Set("numberingSystem", FenValue.FromString("latn"));
                    return FenValue.FromObject(opts);
                })));

                instance.InternalClass = "RelativeTimeFormat";
                return FenValue.FromObject(instance);
            });
        }

        private static string NormalizeTimeUnit(string unit)
        {
            // ECMA-402 §17.1.1 — allow plural forms
            if (unit.EndsWith("s", StringComparison.OrdinalIgnoreCase) && unit.Length > 1)
            {
                return unit.Substring(0, unit.Length - 1).ToLowerInvariant();
            }

            return unit.ToLowerInvariant();
        }

        private static string FormatRelativeTime(long value, string unit, CultureInfo culture, string style, string numeric)
        {
            // "auto" numeric: use named forms for -1/0/1 in English
            if (string.Equals(numeric, "auto", StringComparison.OrdinalIgnoreCase)
                && culture.TwoLetterISOLanguageName == "en")
            {
                var autoResult = TryAutoNamed(value, unit);
                if (autoResult != null) return autoResult;
            }

            long abs = Math.Abs(value);
            bool past = value < 0;

            // Plural category for abs
            string plural = SelectPluralCategory(abs, culture, "cardinal");

            string label = GetEnglishUnitLabel(unit, plural);
            return past
                ? $"{abs} {label} ago"
                : $"in {abs} {label}";
        }

        private static string TryAutoNamed(long value, string unit)
        {
            switch (unit)
            {
                case "day":
                    if (value == -1) return "yesterday";
                    if (value == 0) return "today";
                    if (value == 1) return "tomorrow";
                    break;
                case "hour":
                    if (value == 0) return "this hour";
                    break;
                case "minute":
                    if (value == 0) return "this minute";
                    break;
                case "second":
                    if (value == 0) return "now";
                    break;
                case "week":
                    if (value == -1) return "last week";
                    if (value == 0) return "this week";
                    if (value == 1) return "next week";
                    break;
                case "month":
                    if (value == -1) return "last month";
                    if (value == 0) return "this month";
                    if (value == 1) return "next month";
                    break;
                case "year":
                    if (value == -1) return "last year";
                    if (value == 0) return "this year";
                    if (value == 1) return "next year";
                    break;
                case "quarter":
                    if (value == -1) return "last quarter";
                    if (value == 0) return "this quarter";
                    if (value == 1) return "next quarter";
                    break;
            }

            return null;
        }

        private static string GetEnglishUnitLabel(string unit, string plural)
        {
            bool isOne = string.Equals(plural, "one", StringComparison.OrdinalIgnoreCase);
            switch (unit)
            {
                case "second": return isOne ? "second" : "seconds";
                case "minute": return isOne ? "minute" : "minutes";
                case "hour": return isOne ? "hour" : "hours";
                case "day": return isOne ? "day" : "days";
                case "week": return isOne ? "week" : "weeks";
                case "month": return isOne ? "month" : "months";
                case "quarter": return isOne ? "quarter" : "quarters";
                case "year": return isOne ? "year" : "years";
                default: return unit;
            }
        }

        private static List<FenValue> BuildRelativeTimeParts(long value, string unit, CultureInfo culture, string style, string numeric)
        {
            var parts = new List<FenValue>();

            void AddPart(string type, string val)
            {
                var obj = new FenObject();
                obj.Set("type", FenValue.FromString(type));
                obj.Set("value", FenValue.FromString(val));
                parts.Add(FenValue.FromObject(obj));
            }

            string formatted = FormatRelativeTime(value, unit, culture, style, numeric);
            long abs = Math.Abs(value);
            bool past = value < 0;

            // Decompose into typed parts on a best-effort basis for English
            if (culture.TwoLetterISOLanguageName == "en")
            {
                if (past)
                {
                    AddPart("literal", "");
                    AddPart("integer", abs.ToString());
                    string plural = SelectPluralCategory(abs, culture, "cardinal");
                    AddPart("literal", " " + GetEnglishUnitLabel(unit, plural) + " ago");
                }
                else
                {
                    AddPart("literal", "in ");
                    AddPart("integer", abs.ToString());
                    string plural = SelectPluralCategory(abs, culture, "cardinal");
                    AddPart("literal", " " + GetEnglishUnitLabel(unit, plural));
                }
            }
            else
            {
                AddPart("literal", formatted);
            }

            return parts;
        }

        // ── ListFormat (ECMA-402 §37) ─────────────────────────────────────────

        private static FenFunction CreateListFormat(IExecutionContext context)
        {
            return new FenFunction("ListFormat", (args, thisVal) =>
            {
                var localeArg = args.Length > 0 && !args[0].IsUndefined && !args[0].IsNull
                    ? args[0]
                    : FenValue.FromString("en");
                var optionsArg = args.Length > 1 ? args[1] : FenValue.Undefined;

                var culture = ResolveCulture(localeArg);
                var optObj = optionsArg.IsObject ? optionsArg.AsObject() : null;
                var style = optObj != null ? GetStringOption(optObj, "style", "long") : "long";
                var type = optObj != null ? GetStringOption(optObj, "type", "conjunction") : "conjunction";

                var instance = new FenObject();

                // §37.4.3 format()
                instance.Set("format", FenValue.FromFunction(new FenFunction("format", (fmtArgs, __) =>
                {
                    var items = fmtArgs.Length > 0 ? GetFenArrayItems(fmtArgs[0]) : new FenValue[0];
                    return FenValue.FromString(FormatList(items.Select(v => v.ToString2()).ToArray(), type, style));
                })));

                // §37.4.4 formatToParts()
                instance.Set("formatToParts", FenValue.FromFunction(new FenFunction("formatToParts", (fmtArgs, __) =>
                {
                    var items = fmtArgs.Length > 0 ? GetFenArrayItems(fmtArgs[0]) : new FenValue[0];
                    var parts = BuildListFormatParts(items.Select(v => v.ToString2()).ToArray(), type, style);
                    return FenValue.FromObject(CreateArray(parts));
                })));

                // §37.4.5 resolvedOptions()
                instance.Set("resolvedOptions", FenValue.FromFunction(new FenFunction("resolvedOptions", (_, __) =>
                {
                    var opts = new FenObject();
                    opts.Set("locale", FenValue.FromString(culture.Name));
                    opts.Set("type", FenValue.FromString(type ?? "conjunction"));
                    opts.Set("style", FenValue.FromString(style ?? "long"));
                    return FenValue.FromObject(opts);
                })));

                instance.InternalClass = "ListFormat";
                return FenValue.FromObject(instance);
            });
        }

        private static string FormatList(string[] strings, string type, string style)
        {
            if (strings.Length == 0) return string.Empty;
            if (strings.Length == 1) return strings[0];

            bool isDisjunction = string.Equals(type, "disjunction", StringComparison.OrdinalIgnoreCase);
            bool isUnit = string.Equals(type, "unit", StringComparison.OrdinalIgnoreCase);
            bool isNarrow = string.Equals(style, "narrow", StringComparison.OrdinalIgnoreCase);
            bool isShort = string.Equals(style, "short", StringComparison.OrdinalIgnoreCase);

            string sep;
            if (isUnit)
            {
                sep = isNarrow ? "" : " ";
            }
            else
            {
                sep = isDisjunction ? " or " : (isNarrow || isShort ? ", " : " and ");
            }

            string midSep = ", ";
            var allButLast = string.Join(midSep, strings.Take(strings.Length - 1));
            return allButLast + sep + strings[strings.Length - 1];
        }

        private static List<FenValue> BuildListFormatParts(string[] strings, string type, string style)
        {
            var parts = new List<FenValue>();

            void AddPart(string partType, string value)
            {
                var obj = new FenObject();
                obj.Set("type", FenValue.FromString(partType));
                obj.Set("value", FenValue.FromString(value));
                parts.Add(FenValue.FromObject(obj));
            }

            if (strings.Length == 0) return parts;

            bool isDisjunction = string.Equals(type, "disjunction", StringComparison.OrdinalIgnoreCase);
            bool isUnit = string.Equals(type, "unit", StringComparison.OrdinalIgnoreCase);
            bool isNarrow = string.Equals(style, "narrow", StringComparison.OrdinalIgnoreCase);
            bool isShort = string.Equals(style, "short", StringComparison.OrdinalIgnoreCase);

            string finalSep = isUnit
                ? (isNarrow ? "" : " ")
                : (isDisjunction ? " or " : (isNarrow || isShort ? ", " : " and "));

            for (int i = 0; i < strings.Length; i++)
            {
                AddPart("element", strings[i]);
                if (i < strings.Length - 2)
                {
                    AddPart("literal", ", ");
                }
                else if (i == strings.Length - 2)
                {
                    AddPart("literal", finalSep);
                }
            }

            return parts;
        }

        // ── DisplayNames (ECMA-402 §12) ───────────────────────────────────────

        private static FenFunction CreateDisplayNames(IExecutionContext context)
        {
            return new FenFunction("DisplayNames", (args, thisVal) =>
            {
                var localeArg = args.Length > 0 && !args[0].IsUndefined && !args[0].IsNull
                    ? args[0]
                    : FenValue.FromString("en");
                var optionsArg = args.Length > 1 ? args[1] : FenValue.Undefined;

                var culture = ResolveCulture(localeArg);
                var optObj = optionsArg.IsObject ? optionsArg.AsObject() : null;
                var type = optObj != null ? GetStringOption(optObj, "type", "language") : "language";
                var styleOpt = optObj != null ? GetStringOption(optObj, "style", "long") : "long";
                var languageDisplayOpt = optObj != null ? GetStringOption(optObj, "languageDisplay", "dialect") : "dialect";
                var fallback = optObj != null ? GetStringOption(optObj, "fallback", "code") : "code";

                var instance = new FenObject();

                // §12.4.3 of()
                instance.Set("of", FenValue.FromFunction(new FenFunction("of", (ofArgs, __) =>
                {
                    if (ofArgs.Length == 0 || ofArgs[0].IsUndefined || ofArgs[0].IsNull)
                    {
                        return FenValue.Undefined;
                    }

                    var code = ofArgs[0].ToString2();
                    if (string.IsNullOrEmpty(code)) return FenValue.Undefined;

                    try
                    {
                        switch ((type ?? "language").ToLowerInvariant())
                        {
                            case "language":
                                var ci = CultureInfo.GetCultureInfo(code);
                                return FenValue.FromString(ci.DisplayName);

                            case "region":
                                var region = new RegionInfo(code);
                                return FenValue.FromString(region.DisplayName);

                            case "script":
                                // .NET has no direct script name API; return code as-is or undefined
                                return string.Equals(fallback, "code", StringComparison.OrdinalIgnoreCase)
                                    ? FenValue.FromString(code)
                                    : FenValue.Undefined;

                            case "currency":
                                // Find a culture whose ISO currency matches
                                var currencyRegion = CultureInfo.GetCultures(CultureTypes.SpecificCultures)
                                    .Select(c => { try { return new RegionInfo(c.Name); } catch { return null; } })
                                    .FirstOrDefault(r => r != null && string.Equals(r.ISOCurrencySymbol, code, StringComparison.OrdinalIgnoreCase));
                                if (currencyRegion != null)
                                {
                                    return FenValue.FromString(currencyRegion.CurrencyEnglishName);
                                }

                                return string.Equals(fallback, "code", StringComparison.OrdinalIgnoreCase)
                                    ? FenValue.FromString(code)
                                    : FenValue.Undefined;

                            case "calendar":
                                // No direct .NET API; return code
                                return FenValue.FromString(code);

                            case "datetimefield":
                                return FenValue.FromString(code);

                            default:
                                return FenValue.Undefined;
                        }
                    }
                    catch (Exception)
                    {
                        return string.Equals(fallback, "code", StringComparison.OrdinalIgnoreCase)
                            ? FenValue.FromString(code)
                            : FenValue.Undefined;
                    }
                })));

                // §12.4.4 resolvedOptions()
                instance.Set("resolvedOptions", FenValue.FromFunction(new FenFunction("resolvedOptions", (_, __) =>
                {
                    var opts = new FenObject();
                    opts.Set("locale", FenValue.FromString(culture.Name));
                    opts.Set("type", FenValue.FromString(type ?? "language"));
                    opts.Set("style", FenValue.FromString(styleOpt ?? "long"));
                    opts.Set("languageDisplay", FenValue.FromString(languageDisplayOpt ?? "dialect"));
                    opts.Set("fallback", FenValue.FromString(fallback ?? "code"));
                    return FenValue.FromObject(opts);
                })));

                instance.InternalClass = "DisplayNames";
                return FenValue.FromObject(instance);
            });
        }

        // ── Segmenter (ECMA-402 §18) ──────────────────────────────────────────

        private static FenFunction CreateSegmenter(IExecutionContext context)
        {
            return new FenFunction("Segmenter", (args, thisVal) =>
            {
                var localeArg = args.Length > 0 && !args[0].IsUndefined && !args[0].IsNull
                    ? args[0]
                    : FenValue.FromString("en");
                var optionsArg = args.Length > 1 ? args[1] : FenValue.Undefined;

                var culture = ResolveCulture(localeArg);
                var optObj = optionsArg.IsObject ? optionsArg.AsObject() : null;
                var granularity = optObj != null ? GetStringOption(optObj, "granularity", "grapheme") : "grapheme";

                var instance = new FenObject();

                // §18.4.3 segment()
                instance.Set("segment", FenValue.FromFunction(new FenFunction("segment", (segArgs, __) =>
                {
                    var input = segArgs.Length > 0 ? segArgs[0].ToString2() : string.Empty;
                    var segments = BuildSegments(input, granularity);
                    return FenValue.FromObject(CreateArray(segments));
                })));

                // §18.4.4 resolvedOptions()
                instance.Set("resolvedOptions", FenValue.FromFunction(new FenFunction("resolvedOptions", (_, __) =>
                {
                    var opts = new FenObject();
                    opts.Set("locale", FenValue.FromString(culture.Name));
                    opts.Set("granularity", FenValue.FromString(granularity ?? "grapheme"));
                    return FenValue.FromObject(opts);
                })));

                instance.InternalClass = "Segmenter";
                return FenValue.FromObject(instance);
            });
        }

        private static List<FenValue> BuildSegments(string input, string granularity)
        {
            var segments = new List<FenValue>();
            if (string.IsNullOrEmpty(input)) return segments;

            string[] parts;
            bool[] isWordLike;

            switch ((granularity ?? "grapheme").ToLowerInvariant())
            {
                case "word":
                    // Split on word boundaries: word characters vs non-word characters
                    var wordParts = new List<string>();
                    var wordLikes = new List<bool>();
                    var matches = Regex.Matches(input, @"\w+|\W+");
                    int lastIdx = 0;
                    foreach (Match m in matches)
                    {
                        if (m.Index > lastIdx)
                        {
                            wordParts.Add(input.Substring(lastIdx, m.Index - lastIdx));
                            wordLikes.Add(false);
                        }

                        wordParts.Add(m.Value);
                        wordLikes.Add(Regex.IsMatch(m.Value, @"^\w+$"));
                        lastIdx = m.Index + m.Length;
                    }

                    if (lastIdx < input.Length)
                    {
                        wordParts.Add(input.Substring(lastIdx));
                        wordLikes.Add(false);
                    }

                    parts = wordParts.ToArray();
                    isWordLike = wordLikes.ToArray();
                    break;

                case "sentence":
                    // Simple sentence splitting on . ! ? followed by space or end
                    var sentParts = Regex.Split(input, @"(?<=[.!?])\s+");
                    parts = sentParts.Where(s => s.Length > 0).ToArray();
                    isWordLike = parts.Select(_ => false).ToArray();
                    break;

                default:
                    // "grapheme" — iterate Unicode code points (simplified: per char)
                    var graphemes = new List<string>();
                    var indices = new List<int>();
                    var e = StringInfo.GetTextElementEnumerator(input);
                    while (e.MoveNext())
                    {
                        graphemes.Add(e.GetTextElement());
                    }

                    parts = graphemes.ToArray();
                    isWordLike = parts.Select(_ => false).ToArray();
                    break;
            }

            int idx = 0;
            for (int i = 0; i < parts.Length; i++)
            {
                var seg = new FenObject();
                seg.Set("segment", FenValue.FromString(parts[i]));
                seg.Set("index", FenValue.FromNumber(idx));
                seg.Set("input", FenValue.FromString(input));
                seg.Set("isWordLike", FenValue.FromBoolean(i < isWordLike.Length && isWordLike[i]));
                segments.Add(FenValue.FromObject(seg));
                idx += parts[i].Length;
            }

            return segments;
        }

        // ── Shared locale resolution (ECMA-402 §9.2.2 BestFit) ────────────────

        private static CultureInfo ResolveCulture(FenValue locales)
        {
            foreach (var tag in EnumerateLocaleTags(locales))
            {
                var culture = ResolveSingleLocale(tag);
                if (culture != null) return culture;
            }

            // ECMA-402 §9.2.2: fall back to invariant English, not CurrentCulture
            return CultureInfo.GetCultureInfo("en");
        }

        // ECMA-402 §9.2.2 BestFit matching for a single locale tag
        private static CultureInfo ResolveSingleLocale(string locale)
        {
            if (string.IsNullOrEmpty(locale)) return null;

            // 1. Exact match
            if (TryCreateCulture(locale, out var exact)) return exact;

            // 2. Strip subtags one at a time (e.g. en-US-posix → en-US → en)
            var parts = locale.Split('-');
            for (int len = parts.Length - 1; len >= 1; len--)
            {
                var shorter = string.Join("-", parts.Take(len));
                if (TryCreateCulture(shorter, out var c)) return c;
            }

            return null;
        }

        private static IEnumerable<string> EnumerateLocaleTags(FenValue locales)
        {
            if (locales.IsUndefined || locales.IsNull)
            {
                yield break;
            }

            if (locales.IsString)
            {
                var locale = NormalizeLocaleTag(locales.ToString2());
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

                    var locale = NormalizeLocaleTag(entry.ToString2());
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
                var locale = NormalizeLocaleTag(localeValue.ToString2());
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

        private static string[] GetLocaleList(FenValue localesValue)
        {
            if (!localesValue.IsObject) return new string[0];
            var obj = localesValue.AsObject();
            if (obj == null) return new string[0];

            var lengthVal = obj.Get("length");
            if (!lengthVal.IsNumber) return new string[0];

            int len = (int)Math.Max(0, lengthVal.ToNumber());
            var result = new List<string>(len);
            for (int i = 0; i < len; i++)
            {
                var entry = obj.Get(i.ToString());
                if (entry.IsString)
                {
                    result.Add(entry.ToString2());
                }
            }

            return result.ToArray();
        }

        private static FenValue[] GetFenArrayItems(FenValue arrayValue)
        {
            if (!arrayValue.IsObject) return new FenValue[0];
            var obj = arrayValue.AsObject();
            if (obj == null) return new FenValue[0];

            var lengthVal = obj.Get("length");
            if (!lengthVal.IsNumber) return new FenValue[0];

            int len = (int)Math.Max(0, lengthVal.ToNumber());
            var result = new FenValue[len];
            for (int i = 0; i < len; i++)
            {
                result[i] = obj.Get(i.ToString());
            }

            return result;
        }

        // ── Options parsing ───────────────────────────────────────────────────

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
            // ECMA-402 §15.3.3: start with style-independent defaults
            var result = new IntlNumberFormatOptions
            {
                Locale = culture.Name,
                Style = "decimal",
                MinimumFractionDigits = 0,
                MaximumFractionDigits = 3
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
            var maxFraction = obj.Get("maximumFractionDigits");

            // ECMA-402 §15.3.3 style-dependent defaults (applied BEFORE user overrides)
            bool styleIsCurrency = string.Equals(result.Style, "currency", StringComparison.OrdinalIgnoreCase);
            bool styleIsPercent = string.Equals(result.Style, "percent", StringComparison.OrdinalIgnoreCase);

            if (!minFraction.IsNumber && !maxFraction.IsNumber)
            {
                if (styleIsCurrency)
                {
                    result.MinimumFractionDigits = 2;
                    result.MaximumFractionDigits = 2;
                }
                else if (styleIsPercent)
                {
                    result.MinimumFractionDigits = 0;
                    result.MaximumFractionDigits = 0;
                }
                // decimal keeps 0/3
            }

            if (minFraction.IsNumber)
            {
                result.MinimumFractionDigits = ClampFractionDigits((int)minFraction.ToNumber());
            }

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

            if (options.IgnorePunctuation)
            {
                compareOptions |= CompareOptions.IgnoreSymbols;
            }

            return compareOptions;
        }

        // ── DateTime formatting helpers ────────────────────────────────────────

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
                if (!DateTime.TryParse(value.ToString2(), culture, DateTimeStyles.RoundtripKind, out dateTime)
                    && !DateTime.TryParse(value.ToString2(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out dateTime))
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

        // ── Number formatting helpers ──────────────────────────────────────────

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

        // ── resolvedOptions builders ───────────────────────────────────────────

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
            resolved.Set("minimumIntegerDigits", FenValue.FromNumber(1));

            if (string.Equals(options.Style, "currency", StringComparison.OrdinalIgnoreCase))
            {
                resolved.Set("currency", FenValue.FromString(options.Currency ?? "USD"));
                resolved.Set("currencyDisplay", FenValue.FromString(options.CurrencyDisplay ?? "symbol"));
                resolved.Set("currencySign", FenValue.FromString("standard"));
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

        // ── supportedLocalesOf ────────────────────────────────────────────────

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

        // ── Small utilities ───────────────────────────────────────────────────

        private static string GetStringOption(IObject options, string key, string defaultValue = null)
        {
            var value = options.Get(key);
            return value.IsString ? value.ToString2() : defaultValue;
        }

        private static int ClampFractionDigits(int digits)
        {
            if (digits < 0) return 0;
            if (digits > 20) return 20;
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
