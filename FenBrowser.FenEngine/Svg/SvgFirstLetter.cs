using System;
using System.Collections.Generic;
using System.Globalization;

namespace FenBrowser.FenEngine.Svg
{
    /// <summary>
    /// The <c>::first-letter</c> pseudo-element of SVG text (CSS Pseudo-Elements 4 §2.4,
    /// SVG 2 §11.2: the <c>text</c> element is the block that has a first letter).
    /// The first typographic letter unit of a styled <c>text</c> element is wrapped in
    /// a synthetic element placed where CSS puts the pseudo-element: inside the
    /// innermost element that holds the letter. It lives only in the parent's content
    /// list, never among its children, so structural selectors and IDs are unaffected;
    /// the cascade styles it from <c>::first-letter</c> rules alone, and text layout
    /// paints it like a tspan.
    /// </summary>
    internal static class SvgFirstLetter
    {
        /// <summary>Name of the synthetic element: text layout treats it as a tspan.</summary>
        public const string ElementName = "tspan";

        /// <summary>
        /// The properties that apply to <c>::first-letter</c> (CSS Pseudo-Elements 4
        /// §2.4.2), restricted to the ones SVG text paints. Custom properties always
        /// apply.
        /// </summary>
        private static readonly HashSet<string> ApplicableProperties = new(StringComparer.OrdinalIgnoreCase)
        {
            "color", "opacity",
            "fill", "fill-opacity", "stroke", "stroke-width", "stroke-opacity",
            "stroke-dasharray", "stroke-dashoffset", "stroke-linecap", "stroke-linejoin",
            "stroke-miterlimit", "paint-order",
            "font", "font-family", "font-size", "font-size-adjust", "font-stretch",
            "font-style", "font-variant", "font-weight", "font-kerning",
            "font-feature-settings", "font-variation-settings",
            "letter-spacing", "word-spacing", "text-transform", "text-shadow",
            "text-decoration", "text-decoration-line", "text-decoration-color",
            "text-decoration-style", "text-decoration-thickness"
        };

        public static bool AppliesTo(string property) =>
            property != null && (property.StartsWith("--", StringComparison.Ordinal) ||
                                 ApplicableProperties.Contains(property));

        /// <summary>
        /// Wraps the first typographic letter unit of <paramref name="text"/> in a
        /// synthetic element and returns it; null when the element has no letter to
        /// style (only white space, or no letter, number or symbol).
        /// </summary>
        public static SvgElement Insert(SvgElement text)
        {
            if (text == null) return null;
            if (text.Content.Count == 0 && !string.IsNullOrEmpty(text.TextContent))
            {
                // A text element holding only character data keeps it in TextContent.
                text.Content.Add(new SvgContentPart(text.TextContent));
            }
            return InsertInto(text, text, depth: 0);
        }

        private static SvgElement InsertInto(SvgElement container, SvgElement origin, int depth)
        {
            if (depth > 32) return null;
            for (int i = 0; i < container.Content.Count; i++)
            {
                var part = container.Content[i];
                if (part.IsText)
                {
                    string value = part.Text ?? string.Empty;
                    int start = 0;
                    while (start < value.Length && IsCollapsibleSpace(value[start])) start++;
                    if (start == value.Length) continue;
                    if (!TryMeasureLetterUnit(value, start, out int length)) return null;
                    return Split(container, i, value, start, length, origin);
                }

                var child = part.Element;
                if (child == null) continue;
                if (child.Name is not ("tspan" or "a" or "textPath")) continue;
                if (child.Content.Count == 0 && !string.IsNullOrEmpty(child.TextContent))
                {
                    child.Content.Add(new SvgContentPart(child.TextContent));
                }
                var inserted = InsertInto(child, origin, depth + 1);
                if (inserted != null) return inserted;
                if (HasText(child)) return null;   // the first letter was not a letter
            }
            return null;
        }

        private static SvgElement Split(
            SvgElement container, int index, string value, int start, int length, SvgElement origin)
        {
            string letter = value.Substring(start, length);
            var pseudo = new SvgElement
            {
                Name = ElementName,
                NamespaceUri = container.NamespaceUri,
                Attributes = Array.Empty<KeyValuePair<string, string>>(),
                TextContent = letter,
                Parent = container,
                FirstLetterOrigin = origin
            };
            pseudo.Content.Add(new SvgContentPart(letter));

            var replacement = new List<SvgContentPart>(3);
            if (start > 0) replacement.Add(new SvgContentPart(value.Substring(0, start)));
            replacement.Add(new SvgContentPart(pseudo));
            if (start + length < value.Length) replacement.Add(new SvgContentPart(value.Substring(start + length)));
            container.Content.RemoveAt(index);
            container.Content.InsertRange(index, replacement);
            return pseudo;
        }

        /// <summary>
        /// The first typographic letter unit starting at <paramref name="start"/>:
        /// preceding punctuation, one letter, number or symbol (a whole grapheme), and
        /// the punctuation that follows it.
        /// </summary>
        private static bool TryMeasureLetterUnit(string value, int start, out int length)
        {
            length = 0;
            int position = start;
            while (position < value.Length && IsPunctuation(value, position))
            {
                position += CharCount(value, position);
            }
            if (position >= value.Length || !IsLetterNumberOrSymbol(value, position)) return false;

            position += StringInfo.GetNextTextElementLength(value, position);
            while (position < value.Length && IsPunctuation(value, position))
            {
                position += CharCount(value, position);
            }
            length = position - start;
            return true;
        }

        private static bool IsPunctuation(string value, int index) =>
            CharUnicodeInfo.GetUnicodeCategory(value, index) is
                UnicodeCategory.OpenPunctuation or UnicodeCategory.ClosePunctuation or
                UnicodeCategory.InitialQuotePunctuation or UnicodeCategory.FinalQuotePunctuation or
                UnicodeCategory.OtherPunctuation;

        private static bool IsLetterNumberOrSymbol(string value, int index) =>
            CharUnicodeInfo.GetUnicodeCategory(value, index) is
                UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter or
                UnicodeCategory.TitlecaseLetter or UnicodeCategory.ModifierLetter or
                UnicodeCategory.OtherLetter or UnicodeCategory.DecimalDigitNumber or
                UnicodeCategory.LetterNumber or UnicodeCategory.OtherNumber or
                UnicodeCategory.MathSymbol or UnicodeCategory.CurrencySymbol or
                UnicodeCategory.ModifierSymbol or UnicodeCategory.OtherSymbol;

        private static int CharCount(string value, int index) =>
            char.IsHighSurrogate(value[index]) && index + 1 < value.Length && char.IsLowSurrogate(value[index + 1]) ? 2 : 1;

        private static bool IsCollapsibleSpace(char c) => c is ' ' or '\t' or '\n' or '\r';

        private static bool HasText(SvgElement element)
        {
            foreach (var part in element.Content)
            {
                if (part.IsText && !string.IsNullOrWhiteSpace(part.Text)) return true;
                if (part.Element != null && HasText(part.Element)) return true;
            }
            return !string.IsNullOrWhiteSpace(element.TextContent) && element.Content.Count == 0;
        }
    }
}
