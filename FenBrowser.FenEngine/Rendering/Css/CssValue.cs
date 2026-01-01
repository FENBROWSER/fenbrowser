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

    public enum CssUnit
    {
        None,
        Px,
        Em,
        Rem,
        Percent,
        Vh,
        Vw,
        Deg,
        Rad,
        S,
        Ms
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

    public class CssLength : CssValue
    {
        public override CssValueType Type => Unit == CssUnit.Percent ? CssValueType.Percentage : CssValueType.Length;
        public double Value { get; }
        public CssUnit Unit { get; }

        public CssLength(double value, CssUnit unit)
        {
            Value = value;
            Unit = unit;
        }

        public override string ToString()
        {
            string u = Unit switch
            {
                CssUnit.Px => "px",
                CssUnit.Em => "em",
                CssUnit.Rem => "rem",
                CssUnit.Percent => "%",
                CssUnit.Vh => "vh",
                CssUnit.Vw => "vw",
                CssUnit.Deg => "deg",
                CssUnit.Rad => "rad",
                CssUnit.S => "s",
                CssUnit.Ms => "ms",
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
