using System;

namespace FenBrowser.FenEngine.Layout
{
    internal static class ListMarkerFormatter
    {
        public static string Format(int ordinal, string listStyleType)
        {
            string type = string.IsNullOrWhiteSpace(listStyleType)
                ? "disc"
                : listStyleType.Trim().ToLowerInvariant();

            return type switch
            {
                "none" => string.Empty,
                "disc" => "\u2022 ",
                "circle" => "\u25e6 ",
                "square" => "\u25a0 ",
                "decimal" => $"{ordinal}. ",
                "decimal-leading-zero" => $"{ordinal:D2}. ",
                "lower-alpha" or "lower-latin" => $"{ToAlpha(ordinal, false)}. ",
                "upper-alpha" or "upper-latin" => $"{ToAlpha(ordinal, true)}. ",
                "lower-roman" => $"{ToRoman(ordinal, false)}. ",
                "upper-roman" => $"{ToRoman(ordinal, true)}. ",
                "disclosure-closed" => "\u25b6 ",
                "disclosure-open" => "\u25bc ",
                _ => $"{ordinal}. "
            };
        }

        private static string ToAlpha(int value, bool upper)
        {
            if (value <= 0) return value.ToString();
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
            if (value <= 0 || value > 3999) return value.ToString();
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
