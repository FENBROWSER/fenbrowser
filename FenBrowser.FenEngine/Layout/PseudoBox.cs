// FenBrowser.FenEngine.Layout - PseudoBox
// Pseudo-elements are NOT DOM nodes - they're layout-only constructs

using System;
using System.Collections.Generic;
using FenBrowser.Core.Css;
using FenBrowser.Core.Dom.V2;

namespace FenBrowser.FenEngine.Layout
{
    /// <summary>
    /// Represents a pseudo-element (::before, ::after, ::marker, ::first-line, ::first-letter)
    /// in the layout tree.
    ///
    /// IMPORTANT: This is NOT a DOM node! Pseudo-elements exist only in the layout/render tree.
    /// They are generated from CSS 'content' properties during layout.
    ///
    /// Why separate from DOM:
    /// - Pseudo-elements are not queryable via DOM APIs (can't querySelector ::before)
    /// - They don't receive events directly
    /// - They don't have attributes or children in the DOM sense
    /// - They're purely presentational artifacts
    /// </summary>
    public sealed class PseudoBox
    {
        /// <summary>
        /// The type of pseudo-element.
        /// </summary>
        public PseudoType Type { get; }

        /// <summary>
        /// The originating DOM element that generated this pseudo-element.
        /// </summary>
        public Element OriginatingElement { get; }

        /// <summary>
        /// The computed style for this pseudo-element.
        /// </summary>
        public CssComputed Style { get; }

        /// <summary>
        /// The generated content (from CSS 'content' property).
        /// </summary>
        public IReadOnlyList<ContentItem> Content { get; }

        /// <summary>
        /// The computed box model for this pseudo-element.
        /// Set during layout.
        /// </summary>
        public BoxModel Box { get; set; }

        /// <summary>
        /// Child boxes (e.g., generated text runs).
        /// </summary>
        public List<object> Children { get; } = new List<object>();

        public PseudoBox(Element origin, PseudoType type, CssComputed style, IReadOnlyList<ContentItem> content)
        {
            OriginatingElement = origin ?? throw new ArgumentNullException(nameof(origin));
            Type = type;
            Style = style ?? throw new ArgumentNullException(nameof(style));
            Content = content ?? Array.Empty<ContentItem>();
        }

        /// <summary>
        /// Gets the display value from the style.
        /// </summary>
        public string Display => Style?.Display ?? "inline";

        /// <summary>
        /// Gets whether this pseudo-element should be rendered.
        /// </summary>
        public bool ShouldRender
        {
            get
            {
                if (Style == null) return false;
                if (Style.Display == "none") return false;
                if (Content.Count == 0 && Type != PseudoType.Marker) return false;
                return true;
            }
        }

        public override string ToString()
        {
            return $"PseudoBox [{Type}] for <{OriginatingElement?.LocalName}>";
        }
    }

    /// <summary>
    /// Types of CSS pseudo-elements.
    /// </summary>
    public enum PseudoType
    {
        /// <summary>::before pseudo-element</summary>
        Before,

        /// <summary>::after pseudo-element</summary>
        After,

        /// <summary>::marker pseudo-element (for list items)</summary>
        Marker,

        /// <summary>::first-line pseudo-element</summary>
        FirstLine,

        /// <summary>::first-letter pseudo-element</summary>
        FirstLetter,

        /// <summary>::placeholder pseudo-element (for input/textarea)</summary>
        Placeholder,

        /// <summary>::selection pseudo-element</summary>
        Selection,

        /// <summary>::backdrop pseudo-element (for fullscreen/dialog)</summary>
        Backdrop
    }

    /// <summary>
    /// Represents a single item in the CSS 'content' property.
    /// </summary>
    public abstract class ContentItem
    {
        /// <summary>
        /// Gets the string representation of this content.
        /// </summary>
        public abstract string GetText();
    }

    /// <summary>
    /// A literal string content item.
    /// </summary>
    public sealed class StringContentItem : ContentItem
    {
        public string Value { get; }

        public StringContentItem(string value)
        {
            Value = value ?? "";
        }

        public override string GetText() => Value;
        public override string ToString() => $"\"{Value}\"";
    }

    /// <summary>
    /// An attr() content item - reads attribute from originating element.
    /// </summary>
    public sealed class AttrContentItem : ContentItem
    {
        public string AttributeName { get; }
        public Element Element { get; set; }

        public AttrContentItem(string attributeName)
        {
            AttributeName = attributeName ?? throw new ArgumentNullException(nameof(attributeName));
        }

        public override string GetText()
        {
            return Element?.GetAttribute(AttributeName) ?? "";
        }

        public override string ToString() => $"attr({AttributeName})";
    }

    /// <summary>
    /// A counter() content item - inserts a counter value.
    /// </summary>
    public sealed class CounterContentItem : ContentItem
    {
        public string CounterName { get; }
        public string ListStyleType { get; }
        public int Value { get; set; }

        public CounterContentItem(string counterName, string listStyleType = "decimal")
        {
            CounterName = counterName ?? throw new ArgumentNullException(nameof(counterName));
            ListStyleType = listStyleType;
        }

        public override string GetText()
        {
            return FormatCounter(Value, ListStyleType);
        }

        private static string FormatCounter(int value, string style)
        {
            return style switch
            {
                "decimal" => value.ToString(),
                "decimal-leading-zero" => value.ToString("D2"),
                "lower-roman" => ToRoman(value, false),
                "upper-roman" => ToRoman(value, true),
                "lower-alpha" or "lower-latin" => ToAlpha(value, false),
                "upper-alpha" or "upper-latin" => ToAlpha(value, true),
                "disc" => "•",
                "circle" => "◦",
                "square" => "▪",
                "none" => "",
                _ => value.ToString()
            };
        }

        private static string ToRoman(int value, bool upper)
        {
            if (value <= 0 || value > 3999) return value.ToString();

            var result = "";
            var numerals = new (int, string)[]
            {
                (1000, "M"), (900, "CM"), (500, "D"), (400, "CD"),
                (100, "C"), (90, "XC"), (50, "L"), (40, "XL"),
                (10, "X"), (9, "IX"), (5, "V"), (4, "IV"), (1, "I")
            };

            foreach (var (num, str) in numerals)
            {
                while (value >= num)
                {
                    result += str;
                    value -= num;
                }
            }

            return upper ? result : result.ToLowerInvariant();
        }

        private static string ToAlpha(int value, bool upper)
        {
            if (value <= 0) return value.ToString();

            var result = "";
            while (value > 0)
            {
                value--;
                result = (char)((upper ? 'A' : 'a') + (value % 26)) + result;
                value /= 26;
            }
            return result;
        }

        public override string ToString() => $"counter({CounterName}, {ListStyleType})";
    }

    /// <summary>
    /// An image content item - inserts an image via url().
    /// </summary>
    public sealed class ImageContentItem : ContentItem
    {
        public string Url { get; }
        public string AltText { get; }

        public ImageContentItem(string url, string altText = "")
        {
            Url = url ?? "";
            AltText = altText ?? "";
        }

        public override string GetText() => AltText;
        public override string ToString() => $"url({Url})";
    }

    /// <summary>
    /// Keyword content items (open-quote, close-quote, no-open-quote, no-close-quote).
    /// </summary>
    public sealed class QuoteContentItem : ContentItem
    {
        public QuoteType QuoteType { get; }
        public int QuoteDepth { get; set; }

        public QuoteContentItem(QuoteType type)
        {
            QuoteType = type;
        }

        public override string GetText()
        {
            return QuoteType switch
            {
                QuoteType.OpenQuote => QuoteDepth == 0 ? "\"" : "'",
                QuoteType.CloseQuote => QuoteDepth == 0 ? "\"" : "'",
                QuoteType.NoOpenQuote or QuoteType.NoCloseQuote => "",
                _ => ""
            };
        }

        public override string ToString() => QuoteType.ToString().ToLowerInvariant().Replace("_", "-");
    }

    /// <summary>
    /// Quote content types.
    /// </summary>
    public enum QuoteType
    {
        OpenQuote,
        CloseQuote,
        NoOpenQuote,
        NoCloseQuote
    }

    /// <summary>
    /// Factory for creating PseudoBox instances during layout.
    /// </summary>
    public static class PseudoBoxFactory
    {
        /// <summary>
        /// Creates a ::before pseudo-box for an element if applicable.
        /// </summary>
        public static PseudoBox CreateBefore(Element element, CssComputed beforeStyle)
        {
            if (beforeStyle == null) return null;
            if (beforeStyle.Display == "none") return null;

            var content = ParseContent(beforeStyle.Content, element);
            if (content.Count == 0) return null;

            return new PseudoBox(element, PseudoType.Before, beforeStyle, content);
        }

        /// <summary>
        /// Creates an ::after pseudo-box for an element if applicable.
        /// </summary>
        public static PseudoBox CreateAfter(Element element, CssComputed afterStyle)
        {
            if (afterStyle == null) return null;
            if (afterStyle.Display == "none") return null;

            var content = ParseContent(afterStyle.Content, element);
            if (content.Count == 0) return null;

            return new PseudoBox(element, PseudoType.After, afterStyle, content);
        }

        /// <summary>
        /// Creates a ::marker pseudo-box for a list item element.
        /// </summary>
        public static PseudoBox CreateMarker(Element element, CssComputed markerStyle, string listStyleType, int ordinal)
        {
            if (markerStyle == null) return null;
            if (markerStyle.Display == "none") return null;

            var content = new List<ContentItem>
            {
                new CounterContentItem("list-item", listStyleType) { Value = ordinal }
            };

            return new PseudoBox(element, PseudoType.Marker, markerStyle, content);
        }

        /// <summary>
        /// Parses the CSS 'content' property value into content items.
        /// </summary>
        private static List<ContentItem> ParseContent(string contentValue, Element element)
        {
            var items = new List<ContentItem>();

            if (string.IsNullOrEmpty(contentValue) ||
                contentValue == "none" ||
                contentValue == "normal")
            {
                return items;
            }

            // Simple parser for common content values (now extended to attr(), counter(), counters(), url(), quotes, escapes)

            int i = 0;
            while (i < contentValue.Length)
            {
                // Skip whitespace
                while (i < contentValue.Length && char.IsWhiteSpace(contentValue[i]))
                    i++;

                if (i >= contentValue.Length)
                    break;

                // String literal
                if (contentValue[i] == '"' || contentValue[i] == '\'')
                {
                    char quote = contentValue[i];
                    i++;
                    int start = i;
                    while (i < contentValue.Length && contentValue[i] != quote)
                    {
                        if (contentValue[i] == '\\' && i + 1 < contentValue.Length)
                            i++; // Skip escaped char
                        i++;
                    }
                    var str = contentValue.Substring(start, i - start);
                    items.Add(new StringContentItem(UnescapeString(str)));
                    if (i < contentValue.Length) i++; // Skip closing quote
                }
                // Function call (attr, counter, url, etc.)
                else if (char.IsLetter(contentValue[i]))
                {
                    int start = i;
                    while (i < contentValue.Length && (char.IsLetterOrDigit(contentValue[i]) || contentValue[i] == '-'))
                        i++;
                    var funcName = contentValue.Substring(start, i - start).ToLowerInvariant();

                    if (i < contentValue.Length && contentValue[i] == '(')
                    {
                        i++; // Skip '('
                        int parenDepth = 1;
                        start = i;
                        while (i < contentValue.Length && parenDepth > 0)
                        {
                            if (contentValue[i] == '(') parenDepth++;
                            else if (contentValue[i] == ')') parenDepth--;
                            if (parenDepth > 0) i++;
                        }
                        var args = contentValue.Substring(start, i - start).Trim();
                        if (i < contentValue.Length) i++; // Skip ')'

                        switch (funcName)
                        {
                            case "attr":
                                items.Add(new AttrContentItem(args) { Element = element });
                                break;
                            case "counter":
                                {
                                    var counterParts = args.Split(',');
                                    items.Add(new CounterContentItem(
                                        counterParts[0].Trim(),
                                        counterParts.Length > 1 ? counterParts[1].Trim() : "decimal"));
                                    break;
                                }
                            case "counters":
                                {
                                    // counters(name, "sep", style?)
                                    var parts = SplitArgs(args);
                                    if (parts.Count >= 2)
                                    {
                                        var name = parts[0].Trim();
                                        var sep = UnescapeString(parts[1].Trim(' ', '"', '\''));
                                        var style = parts.Count >= 3 ? parts[2].Trim() : "decimal";
                                        // Store as a string item concatenated; real counter increment handled elsewhere.
                                        items.Add(new StringContentItem($"{name}:{sep}:{style}")); // marker for downstream if needed
                                    }
                                    break;
                                }
                            case "url":
                                items.Add(new ImageContentItem(args.Trim(' ', '"', '\'')));
                                break;
                        }
                    }
                    else
                    {
                        // Keyword
                        switch (funcName)
                        {
                            case "open-quote":
                                items.Add(new QuoteContentItem(QuoteType.OpenQuote));
                                break;
                            case "close-quote":
                                items.Add(new QuoteContentItem(QuoteType.CloseQuote));
                                break;
                            case "no-open-quote":
                                items.Add(new QuoteContentItem(QuoteType.NoOpenQuote));
                                break;
                            case "no-close-quote":
                                items.Add(new QuoteContentItem(QuoteType.NoCloseQuote));
                                break;
                        }
                    }
                }
                else
                {
                    i++; // Skip unknown character
                }
            }

            return items;
        }

        private static string UnescapeString(string s)
        {
            if (!s.Contains('\\')) return s;

            var result = new System.Text.StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] == '\\' && i + 1 < s.Length)
                {
                    i++;
                    char next = s[i];
                    switch (next)
                    {
                        case 'n': result.Append('\n'); break;
                        case 't': result.Append('\t'); break;
                        case 'r': result.Append('\r'); break;
                        case 'a':
                        case 'A': result.Append('\n'); break; // CSS \A
                        case '\\': result.Append('\\'); break;
                        case '"': result.Append('"'); break;
                        case '\'': result.Append('\''); break;
                        case 'x':
                            if (i + 2 < s.Length)
                            {
                                var hex = s.Substring(i + 1, 2);
                                if (int.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out int code))
                                {
                                    result.Append((char)code);
                                    i += 2;
                                    break;
                                }
                            }
                            result.Append(next);
                            break;
                        default:
                            // Unicode escape \XXXX
                            if (IsHexDigit(next) && i + 3 < s.Length &&
                                IsHexDigit(s[i + 1]) && IsHexDigit(s[i + 2]) && IsHexDigit(s[i + 3]))
                            {
                                var hex = new string(new[] { next, s[i + 1], s[i + 2], s[i + 3] });
                                if (int.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out int code))
                                {
                                    result.Append((char)code);
                                    i += 3;
                                    break;
                                }
                            }
                            result.Append(next);
                            break;
                    }
                }
                else
                {
                    result.Append(s[i]);
                }
            }
            return result.ToString();
        }

        private static bool IsHexDigit(char c)
        {
            return (c >= '0' && c <= '9') ||
                   (c >= 'a' && c <= 'f') ||
                   (c >= 'A' && c <= 'F');
        }

        private static List<string> SplitArgs(string args)
        {
            var list = new List<string>();
            if (string.IsNullOrEmpty(args)) return list;

            int start = 0;
            bool inQuote = false;
            char quoteChar = '\0';

            for (int i = 0; i < args.Length; i++)
            {
                char c = args[i];
                if (c == '"' || c == '\'')
                {
                    if (inQuote && c == quoteChar)
                    {
                        inQuote = false;
                    }
                    else if (!inQuote)
                    {
                        inQuote = true;
                        quoteChar = c;
                    }
                }
                else if (c == ',' && !inQuote)
                {
                    list.Add(args.Substring(start, i - start));
                    start = i + 1;
                }
            }

            if (start <= args.Length)
            {
                list.Add(args.Substring(start));
            }

            return list;
        }
    }

    /// <summary>
    /// Placeholder BoxModel structure.
    /// Replace with actual BoxModel from Layout namespace.
    /// </summary>

}
