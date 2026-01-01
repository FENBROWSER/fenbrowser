using System;
using System.Globalization;
using SkiaSharp;

namespace FenBrowser.FenEngine.Rendering.Css
{
    public static class CssValueParser
    {
        public static CssValue Parse(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            value = value.Trim();

            // Try Keyword
            if (char.IsLetter(value[0]))
            {
                // Is it a color keyword?
                if (NamedColors.TryGetValue(value, out var color))
                {
                    return new CssColor(color);
                }
                // Generic keyword
                return new CssKeyword(value);
            }

            // Try Color (Hex)
            if (value.StartsWith("#"))
            {
                if (SKColor.TryParse(value, out var color))
                {
                    return new CssColor(color);
                }
            }



            // Try Quoted String
            if ((value.StartsWith("\"") && value.EndsWith("\"")) || (value.StartsWith("'") && value.EndsWith("'")))
            {
                 if (value.Length >= 2)
                    return new CssString(value.Substring(1, value.Length - 2));
                 return new CssString("");
            }

            // Try Length/Number/Percent
            if (char.IsDigit(value[0]) || value[0] == '.' || value[0] == '-' || value[0] == '+')
            {
                return ParseNumeric(value);
            }

            // Fallback (treat as generic unquoted string/keyword if not caught above)
            return new CssString(value);
        }

        private static CssValue ParseNumeric(string value)
        {
            // Find where number ends
            int i = 0;
            if (value[i] == '-' || value[i] == '+') i++;
            while (i < value.Length && (char.IsDigit(value[i]) || value[i] == '.')) i++;

            if (i == 0) return new CssString(value); // Should not happen based on caller

            string numPart = value.Substring(0, i);
            if (!double.TryParse(numPart, NumberStyles.Any, CultureInfo.InvariantCulture, out double num))
            {
                return new CssString(value);
            }

            if (i == value.Length)
            {
                return new CssNumber(num);
            }

            string unitPart = value.Substring(i).ToLowerInvariant();
            switch (unitPart)
            {
                case "px": return new CssLength(num, CssUnit.Px);
                case "em": return new CssLength(num, CssUnit.Em);
                case "rem": return new CssLength(num, CssUnit.Rem);
                case "%": return new CssLength(num, CssUnit.Percent);
                case "vh": return new CssLength(num, CssUnit.Vh);
                case "vw": return new CssLength(num, CssUnit.Vw);
                case "deg": return new CssLength(num, CssUnit.Deg);
                case "rad": return new CssLength(num, CssUnit.Rad);
                case "s": return new CssLength(num, CssUnit.S);
                case "ms": return new CssLength(num, CssUnit.Ms);
                default: return new CssLength(num, CssUnit.None); // Or treat as number? Spec says dimension token with unknown unit.
            }
        }

        // Minimal set for now
        private static readonly System.Collections.Generic.Dictionary<string, SKColor> NamedColors = new(StringComparer.OrdinalIgnoreCase)
        {
            { "red", SKColors.Red },
            { "green", SKColors.Green },
            { "blue", SKColors.Blue },
            { "black", SKColors.Black },
            { "white", SKColors.White },
            { "transparent", SKColors.Transparent },
            { "yellow", SKColors.Yellow },
            { "cyan", SKColors.Cyan },
            { "magenta", SKColors.Magenta },
            { "gray", SKColors.Gray },
            { "orange", SKColors.Orange },
            { "purple", SKColors.Purple }
        };
    }
}
