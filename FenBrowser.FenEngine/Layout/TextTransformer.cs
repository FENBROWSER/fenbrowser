using System.Globalization;
using FenBrowser.Core.Css;

namespace FenBrowser.FenEngine.Layout
{
    /// <summary>
    /// CSS Text 3 §2.1 `text-transform`. The transformed text is what gets measured, broken
    /// into lines and painted, so layout applies it wherever it reads a text node's data.
    /// Case mapping here is one character to one character, so offsets into the original
    /// data (selection, hit testing) still line up with the transformed string.
    /// </summary>
    internal static class TextTransformer
    {
        public static string Apply(string text, CssComputed style)
        {
            if (string.IsNullOrEmpty(text) || style == null)
            {
                return text;
            }

            string transform = style.TextTransform;
            if (string.IsNullOrEmpty(transform))
            {
                return text;
            }

            transform = transform.Trim();
            if (transform.Equals("uppercase", System.StringComparison.OrdinalIgnoreCase))
            {
                return text.ToUpperInvariant();
            }

            if (transform.Equals("lowercase", System.StringComparison.OrdinalIgnoreCase))
            {
                return text.ToLowerInvariant();
            }

            if (transform.Equals("capitalize", System.StringComparison.OrdinalIgnoreCase))
            {
                return Capitalize(text);
            }

            return text;
        }

        // Only the first letter of each word changes; the rest keep their case, unlike
        // title-casing. A word starts after anything that is not a letter or a digit.
        private static string Capitalize(string text)
        {
            char[] chars = null;
            bool atWordStart = true;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (char.IsLetter(c))
                {
                    if (atWordStart)
                    {
                        char upper = char.ToUpper(c, CultureInfo.InvariantCulture);
                        if (upper != c)
                        {
                            chars ??= text.ToCharArray();
                            chars[i] = upper;
                        }
                    }

                    atWordStart = false;
                }
                else
                {
                    atWordStart = !char.IsDigit(c) && c != '\'' && c != '’';
                }
            }

            return chars == null ? text : new string(chars);
        }
    }
}
