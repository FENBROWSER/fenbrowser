using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using FenBrowser.Core.Dom.V2;
using FenBrowser.FenEngine.Rendering.Css;

namespace FenBrowser.FenEngine.Layout
{
    /// <summary>
    /// A document's @counter-style rules (CSS Counter Styles 3), rebuilt from its
    /// style set before each cascade and consulted when a list marker or counter()
    /// names a style. Invalid rules are dropped, leaving the name undefined (which
    /// then behaves as decimal).
    /// </summary>
    internal static class CounterStyleRegistry
    {
        internal sealed class Definition
        {
            public string System { get; init; } = "symbolic";
            public string Extends { get; init; }
            public int FirstSymbolValue { get; init; } = 1;
            public IReadOnlyList<string> Symbols { get; init; } = Array.Empty<string>();
            public IReadOnlyList<(int Weight, string Symbol)> AdditiveSymbols { get; init; } = Array.Empty<(int, string)>();
            public string Prefix { get; init; }
            public string Suffix { get; init; }
            public string NegativePrefix { get; init; }
            public string NegativeSuffix { get; init; }
            public int? PadLength { get; init; }
            public string PadSymbol { get; init; }
            public string Fallback { get; init; }
        }

        private static readonly ConditionalWeakTable<Document, Dictionary<string, Definition>> s_documents = new();

        public static bool TryGet(Document document, string name, out Definition definition)
        {
            definition = null;
            return document != null &&
                   name != null &&
                   s_documents.TryGetValue(document, out var styles) &&
                   styles.TryGetValue(name, out definition);
        }

        /// <summary>
        /// Replaces the document's counter styles with the @counter-style rules in
        /// <paramref name="styleSet"/>; a later rule of the same name wins (§3).
        /// </summary>
        public static void Rebuild(Document document, StyleSet styleSet)
        {
            if (document == null || styleSet == null)
            {
                return;
            }

            Dictionary<string, Definition> styles = null;
            for (var i = 0; i < styleSet.Count; i++)
            {
                Collect(styleSet.Sheets[i]?.Rules, ref styles);
            }

            if (styles == null)
            {
                s_documents.Remove(document);
            }
            else
            {
                s_documents.AddOrUpdate(document, styles);
            }
        }

        /// <summary>Defines one style from the text inside the rule's braces.</summary>
        public static void Register(Document document, string name, string body)
        {
            if (document == null || body == null)
            {
                return;
            }

            var declarations = new List<(string, string)>();
            foreach (var declaration in body.Split(';'))
            {
                var colon = declaration.IndexOf(':');
                if (colon > 0)
                {
                    declarations.Add((declaration.Substring(0, colon), declaration.Substring(colon + 1)));
                }
            }

            var styles = s_documents.GetOrCreateValue(document);
            if (Parse(name, declarations) is { } definition)
            {
                styles[name.Trim()] = definition;
            }
        }

        /// <summary>
        /// CSS Counter Styles 3 §4 <c>symbols( &lt;symbols-type&gt;? &lt;string&gt;+ )</c>:
        /// an anonymous style, symbolic unless a type is given, with an empty prefix
        /// and a space suffix. Only strings are symbols here.
        /// </summary>
        public static bool TryParseSymbolsFunction(string value, out Definition definition)
        {
            definition = null;
            var text = value?.Trim();
            if (text == null ||
                !text.StartsWith("symbols(", StringComparison.OrdinalIgnoreCase) ||
                !text.EndsWith(")", StringComparison.Ordinal))
            {
                return false;
            }

            var arguments = text.Substring("symbols(".Length, text.Length - "symbols(".Length - 1).Trim();
            var system = "symbolic";
            var firstQuote = arguments.IndexOfAny(new[] { '"', '\'' });
            if (firstQuote < 0)
            {
                return false;
            }

            if (firstQuote > 0)
            {
                system = arguments.Substring(0, firstQuote).Trim().ToLowerInvariant();
                if (system is not ("cyclic" or "numeric" or "alphabetic" or "symbolic" or "fixed"))
                {
                    return false;
                }
            }

            var symbols = ReadSymbols(arguments.Substring(firstQuote));
            if (symbols == null ||
                symbols.Count == 0 ||
                (system is "alphabetic" or "numeric" && symbols.Count < 2))
            {
                return false;
            }

            definition = new Definition
            {
                System = system,
                Symbols = symbols,
                Prefix = string.Empty,
                Suffix = " "
            };
            return true;
        }

        private static void Collect(IEnumerable<CssRule> rules, ref Dictionary<string, Definition> styles)
        {
            if (rules == null)
            {
                return;
            }

            foreach (var rule in rules)
            {
                switch (rule)
                {
                    case CssCounterStyleRule counterStyle:
                        var declarations = new List<(string, string)>();
                        foreach (var declaration in counterStyle.Declarations)
                        {
                            declarations.Add((declaration.Property, declaration.Value));
                        }

                        if (Parse(counterStyle.Name, declarations) is { } definition)
                        {
                            styles ??= new Dictionary<string, Definition>(StringComparer.Ordinal);
                            styles[counterStyle.Name] = definition;
                        }
                        break;
                    case CssMediaRule media:
                        Collect(media.Rules, ref styles);
                        break;
                }
            }
        }

        // §3: the rule's name and descriptors. Returns null for an invalid rule.
        private static Definition Parse(string name, IEnumerable<(string Property, string Value)> declarations)
        {
            name = name?.Trim();
            if (string.IsNullOrEmpty(name) || IsReservedName(name))
            {
                return null;
            }

            string system = "symbolic";
            string extends = null;
            int first = 1;
            List<string> symbols = null;
            List<(int, string)> additive = null;
            string prefix = null, suffix = null, negativePrefix = null, negativeSuffix = null, padSymbol = null, fallback = null;
            int? padLength = null;

            foreach (var (rawProperty, rawValue) in declarations)
            {
                var property = rawProperty?.Trim().ToLowerInvariant();
                var value = rawValue?.Trim() ?? string.Empty;
                switch (property)
                {
                    case "system":
                        var parts = value.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length == 0) return null;
                        system = parts[0].ToLowerInvariant();
                        if (system == "extends")
                        {
                            if (parts.Length != 2) return null;
                            extends = parts[1];
                        }
                        else if (system == "fixed" && parts.Length == 2)
                        {
                            if (!TryParseInteger(parts[1], out first)) return null;
                        }
                        else if (parts.Length != 1)
                        {
                            return null;
                        }
                        break;
                    case "symbols":
                        symbols = ReadSymbols(value);
                        if (symbols == null || symbols.Count == 0) symbols = null;
                        break;
                    case "additive-symbols":
                        additive = ReadAdditiveSymbols(value);
                        break;
                    case "prefix":
                        prefix = ReadSingleSymbol(value) ?? prefix;
                        break;
                    case "suffix":
                        suffix = ReadSingleSymbol(value) ?? suffix;
                        break;
                    case "negative":
                        var negative = ReadSymbols(value);
                        if (negative is { Count: 1 or 2 })
                        {
                            negativePrefix = negative[0];
                            negativeSuffix = negative.Count == 2 ? negative[1] : string.Empty;
                        }
                        break;
                    case "pad":
                        var padParts = value.Split((char[])null, 2, StringSplitOptions.RemoveEmptyEntries);
                        if (padParts.Length == 2 && TryParseInteger(padParts[0], out var length) && length >= 0 &&
                            ReadSingleSymbol(padParts[1]) is { } pad)
                        {
                            padLength = length;
                            padSymbol = pad;
                        }
                        break;
                    case "fallback":
                        if (value.Length > 0 && value.IndexOf(' ') < 0) fallback = value;
                        break;
                }
            }

            // §3.1: each system needs its own symbols, and extends takes none.
            var valid = system switch
            {
                "cyclic" or "fixed" or "symbolic" => symbols is { Count: >= 1 },
                "alphabetic" or "numeric" => symbols is { Count: >= 2 },
                "additive" => additive is { Count: >= 1 },
                "extends" => symbols == null && additive == null && !IsReservedExtendsTarget(extends),
                _ => false
            };
            if (!valid)
            {
                return null;
            }

            return new Definition
            {
                System = system,
                Extends = extends,
                FirstSymbolValue = first,
                Symbols = (IReadOnlyList<string>)symbols ?? Array.Empty<string>(),
                AdditiveSymbols = (IReadOnlyList<(int, string)>)additive ?? Array.Empty<(int, string)>(),
                Prefix = prefix,
                Suffix = suffix,
                NegativePrefix = negativePrefix,
                NegativeSuffix = negativeSuffix,
                PadLength = padLength,
                PadSymbol = padSymbol,
                Fallback = fallback
            };
        }

        // §3: "none" and the CSS-wide keywords never name a counter style, and the
        // predefined decimal, disc, square, circle and disclosure styles cannot be
        // redefined.
        private static bool IsReservedName(string name) =>
            name.ToLowerInvariant() is "none" or "inherit" or "initial" or "unset" or "default" or "revert" or "revert-layer" or
                "decimal" or "disc" or "square" or "circle" or "disclosure-open" or "disclosure-closed";

        private static bool IsReservedExtendsTarget(string name) =>
            name == null || name.ToLowerInvariant() is "none" or "inherit" or "initial" or "unset" or "default" or "revert" or "revert-layer";

        private static bool TryParseInteger(string text, out int value) =>
            int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value);

        private static string ReadSingleSymbol(string value)
        {
            var symbols = ReadSymbols(value);
            return symbols is { Count: 1 } ? symbols[0] : null;
        }

        // <additive-tuple>#: "<integer> <symbol>" pairs, weights strictly decreasing.
        private static List<(int, string)> ReadAdditiveSymbols(string value)
        {
            var tuples = new List<(int, string)>();
            foreach (var tuple in value.Split(','))
            {
                var parts = tuple.Trim().Split((char[])null, 2, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length != 2 || !TryParseInteger(parts[0], out var weight) || weight < 0 ||
                    ReadSingleSymbol(parts[1]) is not { } symbol ||
                    (tuples.Count > 0 && weight >= tuples[^1].Item1))
                {
                    return null;
                }

                tuples.Add((weight, symbol));
            }

            return tuples;
        }

        // <symbol>+: CSS strings (escapes decoded) or identifiers. A CSS-wide
        // keyword is not a <custom-ident>, which makes the whole value invalid.
        private static List<string> ReadSymbols(string value)
        {
            var symbols = new List<string>();
            var i = 0;
            while (i < value.Length)
            {
                var c = value[i];
                if (char.IsWhiteSpace(c))
                {
                    i++;
                    continue;
                }

                var text = new StringBuilder();
                var quote = c == '"' || c == '\'' ? c : '\0';
                if (quote != '\0')
                {
                    i++;
                }

                while (i < value.Length && (quote != '\0' ? value[i] != quote : !char.IsWhiteSpace(value[i])))
                {
                    if (value[i] == '\\' && i + 1 < value.Length)
                    {
                        i++;
                        var hexStart = i;
                        while (i < value.Length && i - hexStart < 6 && Uri.IsHexDigit(value[i]))
                        {
                            i++;
                        }

                        if (i > hexStart)
                        {
                            var codePoint = Convert.ToInt32(value.Substring(hexStart, i - hexStart), 16);
                            text.Append(codePoint is > 0 and <= 0x10FFFF and not (>= 0xD800 and <= 0xDFFF)
                                ? char.ConvertFromUtf32(codePoint)
                                : "�");
                            if (i < value.Length && char.IsWhiteSpace(value[i]))
                            {
                                i++;
                            }

                            continue;
                        }
                    }

                    text.Append(value[i]);
                    i++;
                }

                if (quote != '\0')
                {
                    i++;
                }
                else if (text.ToString().ToLowerInvariant() is "inherit" or "initial" or "unset" or "default" or "revert" or "revert-layer")
                {
                    return null;
                }

                symbols.Add(text.ToString());
            }

            return symbols;
        }
    }
}
