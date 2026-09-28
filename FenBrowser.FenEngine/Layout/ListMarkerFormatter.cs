using System;
using System.Globalization;
using System.Text;
using FenBrowser.Core.Dom.V2;

namespace FenBrowser.FenEngine.Layout
{
    internal static class ListMarkerFormatter
    {
        /// <summary>
        /// A list marker's text: the counter representation between the style's
        /// prefix and suffix - ". " after a number, a space after a symbol (CSS
        /// Counter Styles 3 §6 predefined styles). <paramref name="document"/>
        /// supplies its @counter-style rules.
        /// </summary>
        public static string Format(int ordinal, string listStyleType, Document document = null)
        {
            string type = Normalize(listStyleType);
            if (type == "none")
            {
                return string.Empty;
            }

            // CSS Lists 3 §3.3: a <string> list-style-type is the marker text itself.
            string trimmed = listStyleType.Trim();
            if (trimmed.Length >= 2 && (trimmed[0] == '"' || trimmed[0] == '\'') && trimmed[^1] == trimmed[0])
            {
                return trimmed.Substring(1, trimmed.Length - 2);
            }

            if (TryResolveCustom(listStyleType, document, out var custom))
            {
                return (custom.Prefix ?? string.Empty) + FormatCustom(ordinal, custom, document, 0) +
                       (custom.Suffix ?? (custom.Algorithm == null && IsSymbolic(custom.BaseType) ? " " : ". "));
            }

            return FormatPredefined(ordinal, type) + (IsSymbolic(type) ? " " : ". ");
        }

        /// <summary>
        /// The bare counter representation, as counter() and counters() insert it.
        /// </summary>
        public static string FormatCounterValue(int value, string listStyleType, Document document = null) =>
            FormatCounterValue(value, listStyleType, document, 0);

        private static string FormatCounterValue(int value, string listStyleType, Document document, int depth)
        {
            if (depth < 8 && TryResolveCustom(listStyleType, document, out var custom))
            {
                return FormatCustom(value, custom, document, depth);
            }

            return FormatPredefined(value, listStyleType);
        }

        // An author @counter-style after following its extends chain: the algorithm
        // it ends at (an author system, or a predefined style by name) and each
        // descriptor from the first style in the chain that gives it (CSS Counter
        // Styles 3 §3.1.8: extends takes everything unspecified from the named style).
        private readonly record struct ResolvedStyle(
            string BaseType,
            CounterStyleRegistry.Definition Algorithm,
            string Prefix,
            string Suffix,
            string NegativePrefix,
            string NegativeSuffix,
            int? PadLength,
            string PadSymbol,
            string Fallback);

        private static bool TryResolveCustom(string listStyleType, Document document, out ResolvedStyle resolved)
        {
            resolved = default;
            if (CounterStyleRegistry.TryParseSymbolsFunction(listStyleType, out var anonymous))
            {
                resolved = new ResolvedStyle(null, anonymous, anonymous.Prefix, anonymous.Suffix, null, null, null, null, null);
                return true;
            }

            if (document == null || string.IsNullOrWhiteSpace(listStyleType))
            {
                return false;
            }

            string name = listStyleType.Trim();
            string prefix = null, suffix = null, negativePrefix = null, negativeSuffix = null, padSymbol = null, fallback = null;
            int? padLength = null;
            bool found = false;

            // A cycle of extends makes every style in it behave as decimal.
            for (int depth = 0; depth < 16; depth++)
            {
                if (!CounterStyleRegistry.TryGet(document, name, out var definition))
                {
                    if (!found)
                    {
                        return false;
                    }

                    // An extends of an undefined name extends decimal.
                    var baseType = Normalize(name);
                    resolved = new ResolvedStyle(IsPredefined(baseType) ? baseType : "decimal", null,
                        prefix, suffix, negativePrefix, negativeSuffix, padLength, padSymbol, fallback);
                    return true;
                }

                found = true;
                prefix ??= definition.Prefix;
                suffix ??= definition.Suffix;
                if (negativePrefix == null && definition.NegativePrefix != null)
                {
                    negativePrefix = definition.NegativePrefix;
                    negativeSuffix = definition.NegativeSuffix;
                }

                if (padLength == null && definition.PadLength != null)
                {
                    padLength = definition.PadLength;
                    padSymbol = definition.PadSymbol;
                }

                fallback ??= definition.Fallback;
                if (definition.System != "extends")
                {
                    resolved = new ResolvedStyle(null, definition,
                        prefix, suffix, negativePrefix, negativeSuffix, padLength, padSymbol, fallback);
                    return true;
                }

                name = definition.Extends;
            }

            resolved = new ResolvedStyle("decimal", null, prefix, suffix, negativePrefix, negativeSuffix, padLength, padSymbol, fallback);
            return true;
        }

        private static string FormatCustom(int value, ResolvedStyle style, Document document, int depth)
        {
            var definition = style.Algorithm;
            var system = definition?.System ?? PredefinedSystem(style.BaseType);

            // §3.1: outside the style's range (the "auto" range of its system) the
            // fallback style, decimal by default, represents the value.
            if (!InAutoRange(value, system, definition))
            {
                return FormatCounterValue(value, style.Fallback ?? "decimal", document, depth + 1);
            }

            bool negative = value < 0 && system is "symbolic" or "alphabetic" or "numeric" or "additive";
            long magnitude = negative ? -(long)value : value;
            string representation = definition == null
                ? FormatPredefined((int)Math.Min(magnitude, int.MaxValue), style.BaseType)
                : Algorithm(magnitude, definition);
            if (representation == null)
            {
                return FormatCounterValue(value, style.Fallback ?? "decimal", document, depth + 1);
            }

            string negativePrefix = negative ? style.NegativePrefix ?? "-" : string.Empty;
            string negativeSuffix = negative ? style.NegativeSuffix ?? string.Empty : string.Empty;

            // §3.5 pad: prepend pad symbols up to the minimum length, counting the
            // negative sign's graphemes.
            if (style.PadLength is int padLength && !string.IsNullOrEmpty(style.PadSymbol))
            {
                int length = Graphemes(representation) + Graphemes(negativePrefix) + Graphemes(negativeSuffix);
                if (length < padLength)
                {
                    var padding = new StringBuilder();
                    for (int i = length; i < padLength && i < 1000; i++)
                    {
                        padding.Append(style.PadSymbol);
                    }

                    representation = padding + representation;
                }
            }

            return negativePrefix + representation + negativeSuffix;
        }

        private static bool InAutoRange(int value, string system, CounterStyleRegistry.Definition definition) => system switch
        {
            "symbolic" or "alphabetic" => value >= 1,
            "additive" => value >= 0,
            "roman" => value is >= 1 and <= 3999,
            "fixed" => definition != null &&
                       value >= definition.FirstSymbolValue &&
                       (long)value < (long)definition.FirstSymbolValue + definition.Symbols.Count,
            _ => true
        };

        // The representation of a non-negative value by an author system; null when
        // the system cannot represent it.
        private static string Algorithm(long value, CounterStyleRegistry.Definition definition)
        {
            var symbols = definition.Symbols;
            switch (definition.System)
            {
                case "cyclic":
                    return symbols[(int)(((value - 1) % symbols.Count + symbols.Count) % symbols.Count)];
                case "fixed":
                    return symbols[(int)(value - definition.FirstSymbolValue)];
                case "numeric":
                {
                    if (value == 0) return symbols[0];
                    var result = new StringBuilder();
                    while (value > 0)
                    {
                        result.Insert(0, symbols[(int)(value % symbols.Count)]);
                        value /= symbols.Count;
                    }
                    return result.ToString();
                }
                case "alphabetic":
                {
                    var result = new StringBuilder();
                    while (value > 0)
                    {
                        value--;
                        result.Insert(0, symbols[(int)(value % symbols.Count)]);
                        value /= symbols.Count;
                    }
                    return result.ToString();
                }
                case "symbolic":
                {
                    var symbol = symbols[(int)((value - 1) % symbols.Count)];
                    var repeat = (value - 1) / symbols.Count + 1;
                    if (repeat > 60) return null;
                    var builder = new StringBuilder();
                    for (long i = 0; i < repeat; i++) builder.Append(symbol);
                    return builder.ToString();
                }
                case "additive":
                {
                    var tuples = definition.AdditiveSymbols;
                    if (value == 0)
                    {
                        foreach (var (weight, symbol) in tuples)
                        {
                            if (weight == 0) return symbol;
                        }

                        return null;
                    }

                    var builder = new StringBuilder();
                    foreach (var (weight, symbol) in tuples)
                    {
                        if (weight == 0) break;
                        var times = value / weight;
                        if (times > 60) return null;
                        for (long i = 0; i < times; i++) builder.Append(symbol);
                        value -= times * weight;
                    }

                    return value == 0 ? builder.ToString() : null;
                }
                default:
                    return null;
            }
        }

        private static int Graphemes(string text) =>
            string.IsNullOrEmpty(text) ? 0 : new StringInfo(text).LengthInTextElements;

        private static string PredefinedSystem(string type) => type switch
        {
            "disc" or "circle" or "square" or "disclosure-closed" or "disclosure-open" => "cyclic",
            "lower-alpha" or "lower-latin" or "upper-alpha" or "upper-latin" => "alphabetic",
            "lower-roman" or "upper-roman" => "roman",
            _ => "numeric"
        };

        private static bool IsPredefined(string type) =>
            type is "decimal" or "decimal-leading-zero" or "disc" or "circle" or "square" or
                "disclosure-closed" or "disclosure-open" or "lower-alpha" or "lower-latin" or
                "upper-alpha" or "upper-latin" or "lower-roman" or "upper-roman";

        private static string FormatPredefined(int value, string listStyleType)
        {
            return Normalize(listStyleType) switch
            {
                "none" => string.Empty,
                "disc" => "•",
                "circle" => "◦",
                "square" => "■",
                "disclosure-closed" => "▶",
                "disclosure-open" => "▼",
                "decimal-leading-zero" => value is >= 0 and < 10 ? $"0{value}" : value.ToString(CultureInfo.InvariantCulture),
                "lower-alpha" or "lower-latin" => ToAlpha(value, false),
                "upper-alpha" or "upper-latin" => ToAlpha(value, true),
                "lower-roman" => ToRoman(value, false),
                "upper-roman" => ToRoman(value, true),
                _ => value.ToString(CultureInfo.InvariantCulture)
            };
        }

        private static string Normalize(string listStyleType) =>
            string.IsNullOrWhiteSpace(listStyleType) ? "disc" : listStyleType.Trim().ToLowerInvariant();

        private static bool IsSymbolic(string type) =>
            type is "disc" or "circle" or "square" or "disclosure-closed" or "disclosure-open";

        private static string ToAlpha(int value, bool upper)
        {
            if (value <= 0) return value.ToString(CultureInfo.InvariantCulture);
            string result = string.Empty;
            while (value > 0)
            {
                value--;
                result = (char)((upper ? 'A' : 'a') + value % 26) + result;
                value /= 26;
            }
            return result;
        }

        private static string ToRoman(int value, bool upper)
        {
            if (value <= 0 || value > 3999) return value.ToString(CultureInfo.InvariantCulture);
            var numerals = new (int Value, string Text)[]
            {
                (1000, "M"), (900, "CM"), (500, "D"), (400, "CD"),
                (100, "C"), (90, "XC"), (50, "L"), (40, "XL"),
                (10, "X"), (9, "IX"), (5, "V"), (4, "IV"), (1, "I")
            };
            string result = string.Empty;
            foreach (var numeral in numerals)
            {
                while (value >= numeral.Value)
                {
                    result += numeral.Text;
                    value -= numeral.Value;
                }
            }
            return upper ? result : result.ToLowerInvariant();
        }
    }
}
