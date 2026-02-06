using System;
using SkiaSharp;
using FenBrowser.Core.Css;

namespace FenBrowser.FenEngine.Rendering.Css
{
    public enum CssValueType
    {
        Keyword,
        Length,
        Percentage,
        Color,
        Number,
        String,
        Url,
        Function,
        List
    }

    /// <summary>
    /// CSS Units per CSS Values and Units Level 4.
    /// Reference: https://www.w3.org/TR/css-values-4/#lengths
    /// </summary>
    public enum CssUnit
    {
        None,           // Unitless number

        // === Absolute Length Units ===
        Px,             // Pixels (1px = 1/96in)
        Cm,             // Centimeters
        Mm,             // Millimeters
        Q,              // Quarter-millimeters (1Q = 1/40cm)
        In,             // Inches (1in = 96px)
        Pc,             // Picas (1pc = 12pt)
        Pt,             // Points (1pt = 1/72in)

        // === Font-relative Length Units ===
        Em,             // Font size of the element
        Rem,            // Font size of the root element
        Ex,             // x-height of the element's font
        Rex,            // x-height of the root element's font
        Cap,            // Cap height (height of capital letters)
        Rcap,           // Cap height of root element's font
        Ch,             // Width of "0" (zero) glyph
        Rch,            // Width of "0" glyph in root element's font
        Ic,             // Width of "水" (CJK water ideograph)
        Ric,            // Width of "水" in root element's font
        Lh,             // Line-height of the element
        Rlh,            // Line-height of the root element

        // === Viewport-percentage Length Units ===
        Vw,             // 1% of viewport width
        Vh,             // 1% of viewport height
        Vi,             // 1% of viewport size in inline axis
        Vb,             // 1% of viewport size in block axis
        Vmin,           // Smaller of vw or vh
        Vmax,           // Larger of vw or vh

        // === Small Viewport Units (excluding dynamic UI) ===
        Svw,            // 1% of small viewport width
        Svh,            // 1% of small viewport height
        Svi,            // 1% of small viewport inline size
        Svb,            // 1% of small viewport block size
        Svmin,          // Smaller of svw or svh
        Svmax,          // Larger of svw or svh

        // === Large Viewport Units (assuming UI is hidden) ===
        Lvw,            // 1% of large viewport width
        Lvh,            // 1% of large viewport height
        Lvi,            // 1% of large viewport inline size
        Lvb,            // 1% of large viewport block size
        Lvmin,          // Smaller of lvw or lvh
        Lvmax,          // Larger of lvw or lvh

        // === Dynamic Viewport Units (current viewport) ===
        Dvw,            // 1% of dynamic viewport width
        Dvh,            // 1% of dynamic viewport height
        Dvi,            // 1% of dynamic viewport inline size
        Dvb,            // 1% of dynamic viewport block size
        Dvmin,          // Smaller of dvw or dvh
        Dvmax,          // Larger of dvw or dvh

        // === Container Query Units ===
        Cqw,            // 1% of query container's width
        Cqh,            // 1% of query container's height
        Cqi,            // 1% of query container's inline size
        Cqb,            // 1% of query container's block size
        Cqmin,          // Smaller of cqi or cqb
        Cqmax,          // Larger of cqi or cqb

        // === Percentage ===
        Percent,        // Percentage of containing block

        // === Angle Units ===
        Deg,            // Degrees (360deg = full circle)
        Grad,           // Gradians (400grad = full circle)
        Rad,            // Radians (2π rad = full circle)
        Turn,           // Turns (1turn = full circle)

        // === Time Units ===
        S,              // Seconds
        Ms,             // Milliseconds

        // === Frequency Units ===
        Hz,             // Hertz
        Khz,            // Kilohertz

        // === Resolution Units ===
        Dpi,            // Dots per inch
        Dpcm,           // Dots per centimeter
        Dppx,           // Dots per px unit (device pixel ratio)
        X,              // Alias for dppx

        // === Flex Units (Grid) ===
        Fr              // Flexible fraction unit for CSS Grid
    }

    /// <summary>
    /// Base class for all parsed CSS values.
    /// </summary>
    public abstract class CssValue
    {
        public abstract CssValueType Type { get; }

        public virtual bool IsAuto => false;
        public virtual bool IsNone => false;
        public virtual bool IsInherit => false;

        public override string ToString() => string.Empty;
    }

    public class CssKeyword : CssValue
    {
        public override CssValueType Type => CssValueType.Keyword;
        public string Keyword { get; }

        public override bool IsAuto => Keyword.Equals("auto", StringComparison.OrdinalIgnoreCase);
        public override bool IsNone => Keyword.Equals("none", StringComparison.OrdinalIgnoreCase);
        public override bool IsInherit => Keyword.Equals("inherit", StringComparison.OrdinalIgnoreCase);

        public CssKeyword(string keyword)
        {
            Keyword = keyword.ToLowerInvariant();
        }

        public override string ToString() => Keyword;
    }

    public class CssLengthValue : CssValue
    {
        public override CssValueType Type => Unit == CssUnit.Percent ? CssValueType.Percentage : CssValueType.Length;
        public double Value { get; }
        public CssUnit Unit { get; }

        public CssLengthValue(double value, CssUnit unit)
        {
            Value = value;
            Unit = unit;
        }

        public override string ToString()
        {
            string u = Unit switch
            {
                // Absolute
                CssUnit.Px => "px",
                CssUnit.Cm => "cm",
                CssUnit.Mm => "mm",
                CssUnit.Q => "Q",
                CssUnit.In => "in",
                CssUnit.Pc => "pc",
                CssUnit.Pt => "pt",
                // Font-relative
                CssUnit.Em => "em",
                CssUnit.Rem => "rem",
                CssUnit.Ex => "ex",
                CssUnit.Rex => "rex",
                CssUnit.Cap => "cap",
                CssUnit.Rcap => "rcap",
                CssUnit.Ch => "ch",
                CssUnit.Rch => "rch",
                CssUnit.Ic => "ic",
                CssUnit.Ric => "ric",
                CssUnit.Lh => "lh",
                CssUnit.Rlh => "rlh",
                // Viewport
                CssUnit.Vw => "vw",
                CssUnit.Vh => "vh",
                CssUnit.Vi => "vi",
                CssUnit.Vb => "vb",
                CssUnit.Vmin => "vmin",
                CssUnit.Vmax => "vmax",
                // Small viewport
                CssUnit.Svw => "svw",
                CssUnit.Svh => "svh",
                CssUnit.Svi => "svi",
                CssUnit.Svb => "svb",
                CssUnit.Svmin => "svmin",
                CssUnit.Svmax => "svmax",
                // Large viewport
                CssUnit.Lvw => "lvw",
                CssUnit.Lvh => "lvh",
                CssUnit.Lvi => "lvi",
                CssUnit.Lvb => "lvb",
                CssUnit.Lvmin => "lvmin",
                CssUnit.Lvmax => "lvmax",
                // Dynamic viewport
                CssUnit.Dvw => "dvw",
                CssUnit.Dvh => "dvh",
                CssUnit.Dvi => "dvi",
                CssUnit.Dvb => "dvb",
                CssUnit.Dvmin => "dvmin",
                CssUnit.Dvmax => "dvmax",
                // Container query
                CssUnit.Cqw => "cqw",
                CssUnit.Cqh => "cqh",
                CssUnit.Cqi => "cqi",
                CssUnit.Cqb => "cqb",
                CssUnit.Cqmin => "cqmin",
                CssUnit.Cqmax => "cqmax",
                // Percentage
                CssUnit.Percent => "%",
                // Angle
                CssUnit.Deg => "deg",
                CssUnit.Grad => "grad",
                CssUnit.Rad => "rad",
                CssUnit.Turn => "turn",
                // Time
                CssUnit.S => "s",
                CssUnit.Ms => "ms",
                // Frequency
                CssUnit.Hz => "Hz",
                CssUnit.Khz => "kHz",
                // Resolution
                CssUnit.Dpi => "dpi",
                CssUnit.Dpcm => "dpcm",
                CssUnit.Dppx => "dppx",
                CssUnit.X => "x",
                // Flex
                CssUnit.Fr => "fr",
                _ => ""
            };
            return $"{Value}{u}";
        }
    }

    public class CssColor : CssValue
    {
        public override CssValueType Type => CssValueType.Color;
        public SKColor Color { get; }

        public CssColor(SKColor color)
        {
            Color = color;
        }

        public override string ToString() => Color.ToString();
    }

    public class CssNumber : CssValue
    {
        public override CssValueType Type => CssValueType.Number;
        public double Value { get; }

        public CssNumber(double value)
        {
            Value = value;
        }

        public override string ToString() => Value.ToString();
    }

    public class CssString : CssValue
    {
        public override CssValueType Type => CssValueType.String;
        public string Value { get; }

        public CssString(string value)
        {
            Value = value;
        }

        public override string ToString() => $"\"{Value}\""; // Simple quoting
    }
}
