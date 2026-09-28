using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using SkiaSharp;

namespace FenBrowser.FenEngine.Rendering
{
    /// <summary>
    /// Builds the declarations an inline &lt;svg&gt; root needs from the HTML document
    /// so the SVG engine can resolve <c>currentColor</c> and <c>var()</c> itself
    /// (CSS Color 4 §6.4 currentcolor, CSS Variables 1 §3 var() substitution).
    /// The serialized markup is only read, never rewritten.
    ///
    /// SECURITY: every emitted declaration is re-validated here. Custom property
    /// names must be identifiers and values must be self-contained token runs, so
    /// no value can close the declaration it sits in and inject another one into
    /// the root style attribute. Output is bounded in count and size.
    /// </summary>
    internal static class InlineSvgContext
    {
        internal const int MaxCustomProperties = 128;
        internal const int MaxCustomPropertyValueChars = 2048;
        internal const int MaxDeclarationChars = 32 * 1024;

        /// <summary>
        /// Returns the root declarations, or an empty string when there is nothing
        /// to add. <paramref name="rejectedCustomProperties"/> counts referenced
        /// custom properties that were withheld because they failed validation or
        /// exceeded a bound.
        /// </summary>
        internal static string BuildRootDeclarations(
            string serializedMarkup,
            SKColor foreground,
            IReadOnlyDictionary<string, string> customProperties,
            out int rejectedCustomProperties)
        {
            rejectedCustomProperties = 0;
            var declarations = new StringBuilder();
            declarations.Append("color: ").Append(FormatColor(foreground)).Append(';');

            if (customProperties == null || customProperties.Count == 0 ||
                string.IsNullOrEmpty(serializedMarkup))
            {
                return declarations.ToString();
            }

            var pending = new Queue<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            EnqueueReferences(serializedMarkup, pending, seen);

            int emitted = 0;
            while (pending.Count > 0)
            {
                string name = pending.Dequeue();
                if (!customProperties.TryGetValue(name, out string value) || value == null)
                {
                    continue;
                }

                value = value.Trim();
                if (emitted >= MaxCustomProperties ||
                    !IsSafeCustomPropertyValue(value) ||
                    declarations.Length + name.Length + value.Length + 3 > MaxDeclarationChars)
                {
                    rejectedCustomProperties++;
                    continue;
                }

                declarations.Append(' ').Append(name).Append(": ").Append(value).Append(';');
                emitted++;
                EnqueueReferences(value, pending, seen);
            }

            return declarations.ToString();
        }

        internal static string FormatColor(SKColor color)
        {
            if (color.Alpha == 255)
            {
                return string.Create(
                    CultureInfo.InvariantCulture, $"#{color.Red:X2}{color.Green:X2}{color.Blue:X2}");
            }

            return string.Create(
                CultureInfo.InvariantCulture,
                $"rgba({color.Red}, {color.Green}, {color.Blue}, {color.Alpha / 255.0:0.###})");
        }

        /// <summary>
        /// Adds every <c>var(--name</c> reference in <paramref name="text"/> that has
        /// not been seen yet. Only well-formed names are queued.
        /// </summary>
        private static void EnqueueReferences(string text, Queue<string> pending, HashSet<string> seen)
        {
            int index = 0;
            while (seen.Count <= MaxCustomProperties * 2)
            {
                index = text.IndexOf("var(", index, StringComparison.OrdinalIgnoreCase);
                if (index < 0)
                {
                    return;
                }

                int start = index + 4;
                while (start < text.Length && IsCssWhitespace(text[start])) start++;
                int end = start;
                while (end < text.Length && IsNameChar(text[end])) end++;
                index = Math.Max(end, index + 4);

                if (end - start > 2 && text[start] == '-' && text[start + 1] == '-')
                {
                    string name = text.Substring(start, end - start);
                    if (seen.Add(name))
                    {
                        pending.Enqueue(name);
                    }
                }
            }
        }

        /// <summary>
        /// A value is emitted only when it cannot leave its declaration: no
        /// declaration or block delimiters, no comment openers, no control
        /// characters, balanced parentheses and brackets, and closed strings.
        /// </summary>
        internal static bool IsSafeCustomPropertyValue(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length > MaxCustomPropertyValueChars)
            {
                return false;
            }

            int depth = 0;
            char quote = '\0';
            for (int i = 0; i < value.Length; i++)
            {
                char ch = value[i];
                if ((ch < 0x20 && ch != '\t') || ch == 0x7F)
                {
                    return false;
                }

                if (quote != '\0')
                {
                    if (ch == '\\') { i++; continue; }
                    if (ch == quote) quote = '\0';
                    else if (ch == ';' || ch == '{' || ch == '}') return false;
                    continue;
                }

                switch (ch)
                {
                    case '"':
                    case '\'':
                        quote = ch;
                        break;
                    case '\\':
                        return false;
                    case ';':
                    case '{':
                    case '}':
                    case '!':
                        return false;
                    case '/' when i + 1 < value.Length && value[i + 1] == '*':
                        return false;
                    case '(':
                    case '[':
                        depth++;
                        break;
                    case ')':
                    case ']':
                        if (--depth < 0) return false;
                        break;
                }
            }

            return quote == '\0' && depth == 0;
        }

        private static bool IsNameChar(char ch) =>
            ch == '-' || ch == '_' || char.IsAsciiLetterOrDigit(ch) || ch >= 0x80;

        private static bool IsCssWhitespace(char ch) =>
            ch == ' ' || ch == '\t' || ch == '\n' || ch == '\r' || ch == '\f';
    }
}
