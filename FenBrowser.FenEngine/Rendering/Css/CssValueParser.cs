using System;
using System.Collections.Generic;
using System.Globalization;
using SkiaSharp;

namespace FenBrowser.FenEngine.Rendering.Css
{
    /// <summary>
    /// CSS Value Parser per CSS Values and Units Level 4.
    /// Parses CSS values into typed CssValue objects.
    /// Reference: https://www.w3.org/TR/css-values-4/
    /// </summary>
    public static class CssValueParser
    {
        public static CssValue Parse(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            value = value.Trim();

            // Try Keyword first
            if (char.IsLetter(value[0]) && !value.Contains("("))
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
            if (i < value.Length && (value[i] == '-' || value[i] == '+')) i++;
            while (i < value.Length && (char.IsDigit(value[i]) || value[i] == '.')) i++;

            // Handle scientific notation (e.g., 1e-10)
            if (i < value.Length && (value[i] == 'e' || value[i] == 'E'))
            {
                int j = i + 1;
                if (j < value.Length && (value[j] == '-' || value[j] == '+')) j++;
                while (j < value.Length && char.IsDigit(value[j])) j++;
                i = j;
            }

            if (i == 0) return new CssString(value);

            string numPart = value.Substring(0, i);
            if (!double.TryParse(numPart, NumberStyles.Float, CultureInfo.InvariantCulture, out double num))
            {
                return new CssString(value);
            }

            // Unitless number
            if (i == value.Length)
            {
                return new CssNumber(num);
            }

            // Get unit part
            string unitPart = value.Substring(i).ToLowerInvariant();
            var unit = ParseUnit(unitPart);

            if (unit == CssUnit.None && !string.IsNullOrEmpty(unitPart))
            {
                // Unknown unit - return as length with None unit (spec: dimension with unknown unit)
                return new CssLengthValue(num, CssUnit.None);
            }

            return new CssLengthValue(num, unit);
        }

        /// <summary>
        /// Parse a CSS unit string into CssUnit enum.
        /// </summary>
        public static CssUnit ParseUnit(string unit)
        {
            if (string.IsNullOrEmpty(unit)) return CssUnit.None;

            unit = unit.ToLowerInvariant();

            return unit switch
            {
                // Absolute length units
                "px" => CssUnit.Px,
                "cm" => CssUnit.Cm,
                "mm" => CssUnit.Mm,
                "q" => CssUnit.Q,
                "in" => CssUnit.In,
                "pc" => CssUnit.Pc,
                "pt" => CssUnit.Pt,

                // Font-relative length units
                "em" => CssUnit.Em,
                "rem" => CssUnit.Rem,
                "ex" => CssUnit.Ex,
                "rex" => CssUnit.Rex,
                "cap" => CssUnit.Cap,
                "rcap" => CssUnit.Rcap,
                "ch" => CssUnit.Ch,
                "rch" => CssUnit.Rch,
                "ic" => CssUnit.Ic,
                "ric" => CssUnit.Ric,
                "lh" => CssUnit.Lh,
                "rlh" => CssUnit.Rlh,

                // Viewport-percentage length units
                "vw" => CssUnit.Vw,
                "vh" => CssUnit.Vh,
                "vi" => CssUnit.Vi,
                "vb" => CssUnit.Vb,
                "vmin" => CssUnit.Vmin,
                "vmax" => CssUnit.Vmax,

                // Small viewport units
                "svw" => CssUnit.Svw,
                "svh" => CssUnit.Svh,
                "svi" => CssUnit.Svi,
                "svb" => CssUnit.Svb,
                "svmin" => CssUnit.Svmin,
                "svmax" => CssUnit.Svmax,

                // Large viewport units
                "lvw" => CssUnit.Lvw,
                "lvh" => CssUnit.Lvh,
                "lvi" => CssUnit.Lvi,
                "lvb" => CssUnit.Lvb,
                "lvmin" => CssUnit.Lvmin,
                "lvmax" => CssUnit.Lvmax,

                // Dynamic viewport units
                "dvw" => CssUnit.Dvw,
                "dvh" => CssUnit.Dvh,
                "dvi" => CssUnit.Dvi,
                "dvb" => CssUnit.Dvb,
                "dvmin" => CssUnit.Dvmin,
                "dvmax" => CssUnit.Dvmax,

                // Container query units
                "cqw" => CssUnit.Cqw,
                "cqh" => CssUnit.Cqh,
                "cqi" => CssUnit.Cqi,
                "cqb" => CssUnit.Cqb,
                "cqmin" => CssUnit.Cqmin,
                "cqmax" => CssUnit.Cqmax,

                // Percentage
                "%" => CssUnit.Percent,

                // Angle units
                "deg" => CssUnit.Deg,
                "grad" => CssUnit.Grad,
                "rad" => CssUnit.Rad,
                "turn" => CssUnit.Turn,

                // Time units
                "s" => CssUnit.S,
                "ms" => CssUnit.Ms,

                // Frequency units
                "hz" => CssUnit.Hz,
                "khz" => CssUnit.Khz,

                // Resolution units
                "dpi" => CssUnit.Dpi,
                "dpcm" => CssUnit.Dpcm,
                "dppx" => CssUnit.Dppx,
                "x" => CssUnit.X,

                // Flex unit
                "fr" => CssUnit.Fr,

                _ => CssUnit.None
            };
        }

        /// <summary>
        /// Convert a CSS length value to pixels given context values.
        /// </summary>
        /// <param name="value">The numeric value</param>
        /// <param name="unit">The CSS unit</param>
        /// <param name="fontSize">Element's computed font size in px</param>
        /// <param name="rootFontSize">Root element's font size in px</param>
        /// <param name="viewportWidth">Viewport width in px</param>
        /// <param name="viewportHeight">Viewport height in px</param>
        /// <param name="containerWidth">Container width in px (for container queries)</param>
        /// <param name="containerHeight">Container height in px (for container queries)</param>
        /// <param name="lineHeight">Element's computed line-height in px</param>
        /// <param name="rootLineHeight">Root element's line-height in px</param>
        /// <returns>Value in pixels</returns>
        public static double ToPx(
            double value,
            CssUnit unit,
            double fontSize = 16,
            double rootFontSize = 16,
            double viewportWidth = 1920,
            double viewportHeight = 1080,
            double containerWidth = 0,
            double containerHeight = 0,
            double lineHeight = 0,
            double rootLineHeight = 0)
        {
            // Use element values as defaults for root if not specified
            if (rootFontSize <= 0) rootFontSize = 16;
            if (fontSize <= 0) fontSize = rootFontSize;
            if (lineHeight <= 0) lineHeight = fontSize * 1.2; // Default line-height
            if (rootLineHeight <= 0) rootLineHeight = rootFontSize * 1.2;
            if (containerWidth <= 0) containerWidth = viewportWidth;
            if (containerHeight <= 0) containerHeight = viewportHeight;

            // Approximate font metrics (can be refined with actual font measurements)
            double xHeight = fontSize * 0.5;           // Approximate x-height
            double capHeight = fontSize * 0.7;         // Approximate cap height
            double chWidth = fontSize * 0.5;           // Approximate '0' width
            double icWidth = fontSize;                 // Approximate CJK character width
            double rootXHeight = rootFontSize * 0.5;
            double rootCapHeight = rootFontSize * 0.7;
            double rootChWidth = rootFontSize * 0.5;
            double rootIcWidth = rootFontSize;

            double vmin = Math.Min(viewportWidth, viewportHeight);
            double vmax = Math.Max(viewportWidth, viewportHeight);

            // Small viewport (assume same as viewport for desktop)
            double svw = viewportWidth;
            double svh = viewportHeight;
            double svmin = Math.Min(svw, svh);
            double svmax = Math.Max(svw, svh);

            // Large viewport (assume same as viewport for desktop)
            double lvw = viewportWidth;
            double lvh = viewportHeight;
            double lvmin = Math.Min(lvw, lvh);
            double lvmax = Math.Max(lvw, lvh);

            // Dynamic viewport (use viewport for now)
            double dvw = viewportWidth;
            double dvh = viewportHeight;
            double dvmin = Math.Min(dvw, dvh);
            double dvmax = Math.Max(dvw, dvh);

            // Container query sizes
            double cqmin = Math.Min(containerWidth, containerHeight);
            double cqmax = Math.Max(containerWidth, containerHeight);

            return unit switch
            {
                CssUnit.None => value,
                CssUnit.Px => value,

                // Absolute units (1in = 96px per CSS spec)
                CssUnit.Cm => value * 96 / 2.54,         // 1cm = 96/2.54 px
                CssUnit.Mm => value * 96 / 25.4,         // 1mm = 96/25.4 px
                CssUnit.Q => value * 96 / 101.6,         // 1Q = 96/101.6 px (1/4 mm)
                CssUnit.In => value * 96,                // 1in = 96px
                CssUnit.Pc => value * 16,                // 1pc = 16px (1/6 in)
                CssUnit.Pt => value * 96 / 72,           // 1pt = 96/72 px (1/72 in)

                // Font-relative units
                CssUnit.Em => value * fontSize,
                CssUnit.Rem => value * rootFontSize,
                CssUnit.Ex => value * xHeight,
                CssUnit.Rex => value * rootXHeight,
                CssUnit.Cap => value * capHeight,
                CssUnit.Rcap => value * rootCapHeight,
                CssUnit.Ch => value * chWidth,
                CssUnit.Rch => value * rootChWidth,
                CssUnit.Ic => value * icWidth,
                CssUnit.Ric => value * rootIcWidth,
                CssUnit.Lh => value * lineHeight,
                CssUnit.Rlh => value * rootLineHeight,

                // Viewport units
                CssUnit.Vw => value * viewportWidth / 100,
                CssUnit.Vh => value * viewportHeight / 100,
                CssUnit.Vi => value * viewportWidth / 100,   // Inline axis (horizontal for ltr)
                CssUnit.Vb => value * viewportHeight / 100,  // Block axis (vertical for ltr)
                CssUnit.Vmin => value * vmin / 100,
                CssUnit.Vmax => value * vmax / 100,

                // Small viewport units
                CssUnit.Svw => value * svw / 100,
                CssUnit.Svh => value * svh / 100,
                CssUnit.Svi => value * svw / 100,
                CssUnit.Svb => value * svh / 100,
                CssUnit.Svmin => value * svmin / 100,
                CssUnit.Svmax => value * svmax / 100,

                // Large viewport units
                CssUnit.Lvw => value * lvw / 100,
                CssUnit.Lvh => value * lvh / 100,
                CssUnit.Lvi => value * lvw / 100,
                CssUnit.Lvb => value * lvh / 100,
                CssUnit.Lvmin => value * lvmin / 100,
                CssUnit.Lvmax => value * lvmax / 100,

                // Dynamic viewport units
                CssUnit.Dvw => value * dvw / 100,
                CssUnit.Dvh => value * dvh / 100,
                CssUnit.Dvi => value * dvw / 100,
                CssUnit.Dvb => value * dvh / 100,
                CssUnit.Dvmin => value * dvmin / 100,
                CssUnit.Dvmax => value * dvmax / 100,

                // Container query units
                CssUnit.Cqw => value * containerWidth / 100,
                CssUnit.Cqh => value * containerHeight / 100,
                CssUnit.Cqi => value * containerWidth / 100,
                CssUnit.Cqb => value * containerHeight / 100,
                CssUnit.Cqmin => value * cqmin / 100,
                CssUnit.Cqmax => value * cqmax / 100,

                // Percentage (requires context - return as-is for now)
                CssUnit.Percent => value,

                // Angle units (convert to degrees)
                CssUnit.Deg => value,
                CssUnit.Grad => value * 0.9,             // 1grad = 0.9deg
                CssUnit.Rad => value * 180 / Math.PI,    // rad to deg
                CssUnit.Turn => value * 360,             // 1turn = 360deg

                // Time units (convert to seconds)
                CssUnit.S => value,
                CssUnit.Ms => value / 1000,

                // Frequency units (convert to Hz)
                CssUnit.Hz => value,
                CssUnit.Khz => value * 1000,

                // Resolution units (convert to dppx)
                CssUnit.Dpi => value / 96,               // 96dpi = 1dppx
                CssUnit.Dpcm => value * 2.54 / 96,       // dpcm to dppx
                CssUnit.Dppx => value,
                CssUnit.X => value,                      // x is alias for dppx

                // Flex units (return as-is, used in grid context)
                CssUnit.Fr => value,

                _ => value
            };
        }

        /// <summary>
        /// All 147 CSS named colors per CSS Color Level 4.
        /// Reference: https://www.w3.org/TR/css-color-4/#named-colors
        /// </summary>
        public static readonly Dictionary<string, SKColor> NamedColors = new(StringComparer.OrdinalIgnoreCase)
        {
            // Basic colors
            { "black", new SKColor(0, 0, 0) },
            { "silver", new SKColor(192, 192, 192) },
            { "gray", new SKColor(128, 128, 128) },
            { "grey", new SKColor(128, 128, 128) },
            { "white", new SKColor(255, 255, 255) },
            { "maroon", new SKColor(128, 0, 0) },
            { "red", new SKColor(255, 0, 0) },
            { "purple", new SKColor(128, 0, 128) },
            { "fuchsia", new SKColor(255, 0, 255) },
            { "magenta", new SKColor(255, 0, 255) },
            { "green", new SKColor(0, 128, 0) },
            { "lime", new SKColor(0, 255, 0) },
            { "olive", new SKColor(128, 128, 0) },
            { "yellow", new SKColor(255, 255, 0) },
            { "navy", new SKColor(0, 0, 128) },
            { "blue", new SKColor(0, 0, 255) },
            { "teal", new SKColor(0, 128, 128) },
            { "aqua", new SKColor(0, 255, 255) },
            { "cyan", new SKColor(0, 255, 255) },

            // Extended colors (alphabetical)
            { "aliceblue", new SKColor(240, 248, 255) },
            { "antiquewhite", new SKColor(250, 235, 215) },
            { "aquamarine", new SKColor(127, 255, 212) },
            { "azure", new SKColor(240, 255, 255) },
            { "beige", new SKColor(245, 245, 220) },
            { "bisque", new SKColor(255, 228, 196) },
            { "blanchedalmond", new SKColor(255, 235, 205) },
            { "blueviolet", new SKColor(138, 43, 226) },
            { "brown", new SKColor(165, 42, 42) },
            { "burlywood", new SKColor(222, 184, 135) },
            { "cadetblue", new SKColor(95, 158, 160) },
            { "chartreuse", new SKColor(127, 255, 0) },
            { "chocolate", new SKColor(210, 105, 30) },
            { "coral", new SKColor(255, 127, 80) },
            { "cornflowerblue", new SKColor(100, 149, 237) },
            { "cornsilk", new SKColor(255, 248, 220) },
            { "crimson", new SKColor(220, 20, 60) },
            { "darkblue", new SKColor(0, 0, 139) },
            { "darkcyan", new SKColor(0, 139, 139) },
            { "darkgoldenrod", new SKColor(184, 134, 11) },
            { "darkgray", new SKColor(169, 169, 169) },
            { "darkgrey", new SKColor(169, 169, 169) },
            { "darkgreen", new SKColor(0, 100, 0) },
            { "darkkhaki", new SKColor(189, 183, 107) },
            { "darkmagenta", new SKColor(139, 0, 139) },
            { "darkolivegreen", new SKColor(85, 107, 47) },
            { "darkorange", new SKColor(255, 140, 0) },
            { "darkorchid", new SKColor(153, 50, 204) },
            { "darkred", new SKColor(139, 0, 0) },
            { "darksalmon", new SKColor(233, 150, 122) },
            { "darkseagreen", new SKColor(143, 188, 143) },
            { "darkslateblue", new SKColor(72, 61, 139) },
            { "darkslategray", new SKColor(47, 79, 79) },
            { "darkslategrey", new SKColor(47, 79, 79) },
            { "darkturquoise", new SKColor(0, 206, 209) },
            { "darkviolet", new SKColor(148, 0, 211) },
            { "deeppink", new SKColor(255, 20, 147) },
            { "deepskyblue", new SKColor(0, 191, 255) },
            { "dimgray", new SKColor(105, 105, 105) },
            { "dimgrey", new SKColor(105, 105, 105) },
            { "dodgerblue", new SKColor(30, 144, 255) },
            { "firebrick", new SKColor(178, 34, 34) },
            { "floralwhite", new SKColor(255, 250, 240) },
            { "forestgreen", new SKColor(34, 139, 34) },
            { "gainsboro", new SKColor(220, 220, 220) },
            { "ghostwhite", new SKColor(248, 248, 255) },
            { "gold", new SKColor(255, 215, 0) },
            { "goldenrod", new SKColor(218, 165, 32) },
            { "greenyellow", new SKColor(173, 255, 47) },
            { "honeydew", new SKColor(240, 255, 240) },
            { "hotpink", new SKColor(255, 105, 180) },
            { "indianred", new SKColor(205, 92, 92) },
            { "indigo", new SKColor(75, 0, 130) },
            { "ivory", new SKColor(255, 255, 240) },
            { "khaki", new SKColor(240, 230, 140) },
            { "lavender", new SKColor(230, 230, 250) },
            { "lavenderblush", new SKColor(255, 240, 245) },
            { "lawngreen", new SKColor(124, 252, 0) },
            { "lemonchiffon", new SKColor(255, 250, 205) },
            { "lightblue", new SKColor(173, 216, 230) },
            { "lightcoral", new SKColor(240, 128, 128) },
            { "lightcyan", new SKColor(224, 255, 255) },
            { "lightgoldenrodyellow", new SKColor(250, 250, 210) },
            { "lightgray", new SKColor(211, 211, 211) },
            { "lightgrey", new SKColor(211, 211, 211) },
            { "lightgreen", new SKColor(144, 238, 144) },
            { "lightpink", new SKColor(255, 182, 193) },
            { "lightsalmon", new SKColor(255, 160, 122) },
            { "lightseagreen", new SKColor(32, 178, 170) },
            { "lightskyblue", new SKColor(135, 206, 250) },
            { "lightslategray", new SKColor(119, 136, 153) },
            { "lightslategrey", new SKColor(119, 136, 153) },
            { "lightsteelblue", new SKColor(176, 196, 222) },
            { "lightyellow", new SKColor(255, 255, 224) },
            { "limegreen", new SKColor(50, 205, 50) },
            { "linen", new SKColor(250, 240, 230) },
            { "mediumaquamarine", new SKColor(102, 205, 170) },
            { "mediumblue", new SKColor(0, 0, 205) },
            { "mediumorchid", new SKColor(186, 85, 211) },
            { "mediumpurple", new SKColor(147, 112, 219) },
            { "mediumseagreen", new SKColor(60, 179, 113) },
            { "mediumslateblue", new SKColor(123, 104, 238) },
            { "mediumspringgreen", new SKColor(0, 250, 154) },
            { "mediumturquoise", new SKColor(72, 209, 204) },
            { "mediumvioletred", new SKColor(199, 21, 133) },
            { "midnightblue", new SKColor(25, 25, 112) },
            { "mintcream", new SKColor(245, 255, 250) },
            { "mistyrose", new SKColor(255, 228, 225) },
            { "moccasin", new SKColor(255, 228, 181) },
            { "navajowhite", new SKColor(255, 222, 173) },
            { "oldlace", new SKColor(253, 245, 230) },
            { "olivedrab", new SKColor(107, 142, 35) },
            { "orange", new SKColor(255, 165, 0) },
            { "orangered", new SKColor(255, 69, 0) },
            { "orchid", new SKColor(218, 112, 214) },
            { "palegoldenrod", new SKColor(238, 232, 170) },
            { "palegreen", new SKColor(152, 251, 152) },
            { "paleturquoise", new SKColor(175, 238, 238) },
            { "palevioletred", new SKColor(219, 112, 147) },
            { "papayawhip", new SKColor(255, 239, 213) },
            { "peachpuff", new SKColor(255, 218, 185) },
            { "peru", new SKColor(205, 133, 63) },
            { "pink", new SKColor(255, 192, 203) },
            { "plum", new SKColor(221, 160, 221) },
            { "powderblue", new SKColor(176, 224, 230) },
            { "rebeccapurple", new SKColor(102, 51, 153) },
            { "rosybrown", new SKColor(188, 143, 143) },
            { "royalblue", new SKColor(65, 105, 225) },
            { "saddlebrown", new SKColor(139, 69, 19) },
            { "salmon", new SKColor(250, 128, 114) },
            { "sandybrown", new SKColor(244, 164, 96) },
            { "seagreen", new SKColor(46, 139, 87) },
            { "seashell", new SKColor(255, 245, 238) },
            { "sienna", new SKColor(160, 82, 45) },
            { "skyblue", new SKColor(135, 206, 235) },
            { "slateblue", new SKColor(106, 90, 205) },
            { "slategray", new SKColor(112, 128, 144) },
            { "slategrey", new SKColor(112, 128, 144) },
            { "snow", new SKColor(255, 250, 250) },
            { "springgreen", new SKColor(0, 255, 127) },
            { "steelblue", new SKColor(70, 130, 180) },
            { "tan", new SKColor(210, 180, 140) },
            { "thistle", new SKColor(216, 191, 216) },
            { "tomato", new SKColor(255, 99, 71) },
            { "turquoise", new SKColor(64, 224, 208) },
            { "violet", new SKColor(238, 130, 238) },
            { "wheat", new SKColor(245, 222, 179) },
            { "whitesmoke", new SKColor(245, 245, 245) },
            { "yellowgreen", new SKColor(154, 205, 50) },

            // Special keywords
            { "transparent", new SKColor(0, 0, 0, 0) },
            { "currentcolor", new SKColor(255, 0, 255, 1) }, // Sentinel value for currentColor

            // System colors (basic mapping - should ideally query OS)
            { "canvas", new SKColor(255, 255, 255) },
            { "canvastext", new SKColor(0, 0, 0) },
            { "linktext", new SKColor(0, 0, 238) },
            { "visitedtext", new SKColor(85, 26, 139) },
            { "activetext", new SKColor(255, 0, 0) },
            { "buttonface", new SKColor(240, 240, 240) },
            { "buttontext", new SKColor(0, 0, 0) },
            { "buttonborder", new SKColor(118, 118, 118) },
            { "field", new SKColor(255, 255, 255) },
            { "fieldtext", new SKColor(0, 0, 0) },
            { "highlight", new SKColor(0, 120, 215) },
            { "highlighttext", new SKColor(255, 255, 255) },
            { "selecteditem", new SKColor(0, 120, 215) },
            { "selecteditemtext", new SKColor(255, 255, 255) },
            { "mark", new SKColor(255, 255, 0) },
            { "marktext", new SKColor(0, 0, 0) },
            { "graytext", new SKColor(128, 128, 128) },
            { "accentcolor", new SKColor(0, 120, 215) },
            { "accentcolortext", new SKColor(255, 255, 255) }
        };

        /// <summary>
        /// Check if a unit is a length unit (not angle, time, etc.)
        /// </summary>
        public static bool IsLengthUnit(CssUnit unit)
        {
            return unit switch
            {
                CssUnit.Px or CssUnit.Cm or CssUnit.Mm or CssUnit.Q or
                CssUnit.In or CssUnit.Pc or CssUnit.Pt or
                CssUnit.Em or CssUnit.Rem or CssUnit.Ex or CssUnit.Rex or
                CssUnit.Cap or CssUnit.Rcap or CssUnit.Ch or CssUnit.Rch or
                CssUnit.Ic or CssUnit.Ric or CssUnit.Lh or CssUnit.Rlh or
                CssUnit.Vw or CssUnit.Vh or CssUnit.Vi or CssUnit.Vb or
                CssUnit.Vmin or CssUnit.Vmax or
                CssUnit.Svw or CssUnit.Svh or CssUnit.Svi or CssUnit.Svb or
                CssUnit.Svmin or CssUnit.Svmax or
                CssUnit.Lvw or CssUnit.Lvh or CssUnit.Lvi or CssUnit.Lvb or
                CssUnit.Lvmin or CssUnit.Lvmax or
                CssUnit.Dvw or CssUnit.Dvh or CssUnit.Dvi or CssUnit.Dvb or
                CssUnit.Dvmin or CssUnit.Dvmax or
                CssUnit.Cqw or CssUnit.Cqh or CssUnit.Cqi or CssUnit.Cqb or
                CssUnit.Cqmin or CssUnit.Cqmax => true,
                _ => false
            };
        }

        /// <summary>
        /// Check if a unit is viewport-relative
        /// </summary>
        public static bool IsViewportUnit(CssUnit unit)
        {
            return unit switch
            {
                CssUnit.Vw or CssUnit.Vh or CssUnit.Vi or CssUnit.Vb or
                CssUnit.Vmin or CssUnit.Vmax or
                CssUnit.Svw or CssUnit.Svh or CssUnit.Svi or CssUnit.Svb or
                CssUnit.Svmin or CssUnit.Svmax or
                CssUnit.Lvw or CssUnit.Lvh or CssUnit.Lvi or CssUnit.Lvb or
                CssUnit.Lvmin or CssUnit.Lvmax or
                CssUnit.Dvw or CssUnit.Dvh or CssUnit.Dvi or CssUnit.Dvb or
                CssUnit.Dvmin or CssUnit.Dvmax => true,
                _ => false
            };
        }

        /// <summary>
        /// Check if a unit is font-relative
        /// </summary>
        public static bool IsFontRelativeUnit(CssUnit unit)
        {
            return unit switch
            {
                CssUnit.Em or CssUnit.Rem or CssUnit.Ex or CssUnit.Rex or
                CssUnit.Cap or CssUnit.Rcap or CssUnit.Ch or CssUnit.Rch or
                CssUnit.Ic or CssUnit.Ric or CssUnit.Lh or CssUnit.Rlh => true,
                _ => false
            };
        }

        /// <summary>
        /// Check if a unit is container-relative
        /// </summary>
        public static bool IsContainerUnit(CssUnit unit)
        {
            return unit switch
            {
                CssUnit.Cqw or CssUnit.Cqh or CssUnit.Cqi or CssUnit.Cqb or
                CssUnit.Cqmin or CssUnit.Cqmax => true,
                _ => false
            };
        }
    }
}
