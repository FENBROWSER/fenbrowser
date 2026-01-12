using System.Collections.Generic;

namespace FenBrowser.Core.Logging
{
    /// <summary>
    /// CSS Spec default values for compliance checking.
    /// Values based on W3C CSS2.1 and CSS3 specifications.
    /// Base font size assumed: 16px (browser default)
    /// </summary>
    public static class CssSpecDefaults
    {
        public const float BaseFontSizePx = 16f;

        /// <summary>
        /// Typography spec defaults (CSS2.1 Section 15)
        /// https://www.w3.org/TR/CSS21/fonts.html
        /// </summary>
        public static readonly Dictionary<string, TypographySpec> Typography = new(System.StringComparer.OrdinalIgnoreCase)
        {
            // Headings (user agent stylesheet defaults)
            ["h1"] = new TypographySpec("2em", 32f, 700, "0.67em", 10.72f),
            ["h2"] = new TypographySpec("1.5em", 24f, 700, "0.83em", 13.28f),
            ["h3"] = new TypographySpec("1.17em", 18.72f, 700, "1em", 16f),
            ["h4"] = new TypographySpec("1em", 16f, 700, "1.33em", 21.28f),
            ["h5"] = new TypographySpec("0.83em", 13.28f, 700, "1.67em", 26.72f),
            ["h6"] = new TypographySpec("0.67em", 10.72f, 700, "2.33em", 37.28f),
            
            // Body text
            ["body"] = new TypographySpec("1em", 16f, 400, "0", 0f),
            ["p"] = new TypographySpec("1em", 16f, 400, "1em", 16f),
            
            // Inline elements
            ["strong"] = new TypographySpec(null, null, 700, null, null),
            ["b"] = new TypographySpec(null, null, 700, null, null),
            ["em"] = new TypographySpec(null, null, null, null, null, "italic"),
            ["i"] = new TypographySpec(null, null, null, null, null, "italic"),
            ["small"] = new TypographySpec("smaller", 13.33f, null, null, null),
            ["big"] = new TypographySpec("larger", 19.2f, null, null, null),
            
            // Lists
            ["ul"] = new TypographySpec("1em", 16f, 400, "1em", 16f),
            ["ol"] = new TypographySpec("1em", 16f, 400, "1em", 16f),
            ["li"] = new TypographySpec("1em", 16f, 400, null, null),
        };

        /// <summary>
        /// Box model spec defaults (CSS2.1 Section 8)
        /// https://www.w3.org/TR/CSS21/box.html
        /// </summary>
        public static readonly Dictionary<string, BoxModelSpec> BoxModel = new(System.StringComparer.OrdinalIgnoreCase)
        {
            // Body has 8px margin by default
            ["body"] = new BoxModelSpec(8f, 8f, 8f, 8f, 0f, 0f, 0f, 0f),
            
            // Block elements with vertical margins
            ["p"] = new BoxModelSpec(16f, 0f, 16f, 0f, 0f, 0f, 0f, 0f),
            ["h1"] = new BoxModelSpec(21.44f, 0f, 21.44f, 0f, 0f, 0f, 0f, 0f), // 0.67em margin
            ["h2"] = new BoxModelSpec(19.92f, 0f, 19.92f, 0f, 0f, 0f, 0f, 0f), // 0.83em margin
            ["h3"] = new BoxModelSpec(18.72f, 0f, 18.72f, 0f, 0f, 0f, 0f, 0f), // 1em margin
            ["h4"] = new BoxModelSpec(21.28f, 0f, 21.28f, 0f, 0f, 0f, 0f, 0f), // 1.33em margin
            ["h5"] = new BoxModelSpec(22.18f, 0f, 22.18f, 0f, 0f, 0f, 0f, 0f), // 1.67em margin
            ["h6"] = new BoxModelSpec(24.89f, 0f, 24.89f, 0f, 0f, 0f, 0f, 0f), // 2.33em margin
            
            // Lists
            ["ul"] = new BoxModelSpec(16f, 0f, 16f, 0f, 0f, 0f, 0f, 40f), // 40px padding-left
            ["ol"] = new BoxModelSpec(16f, 0f, 16f, 0f, 0f, 0f, 0f, 40f),
            
            // Blockquote
            ["blockquote"] = new BoxModelSpec(16f, 40f, 16f, 40f, 0f, 0f, 0f, 0f),
            
            // Pre
            ["pre"] = new BoxModelSpec(16f, 0f, 16f, 0f, 0f, 0f, 0f, 0f),
            
            // HR
            ["hr"] = new BoxModelSpec(8f, 0f, 8f, 0f, 0f, 0f, 0f, 0f),
        };

        /// <summary>
        /// Flexbox spec defaults (CSS Flexbox Level 1)
        /// https://www.w3.org/TR/css-flexbox-1/
        /// </summary>
        public static readonly FlexboxSpec FlexDefaults = new FlexboxSpec
        {
            FlexDirection = "row",
            FlexWrap = "nowrap",
            JustifyContent = "flex-start",
            AlignItems = "stretch",
            AlignContent = "stretch",
            FlexGrow = 0,
            FlexShrink = 1,
            FlexBasis = "auto"
        };
    }

    /// <summary>
    /// Typography specification for an element
    /// </summary>
    public record TypographySpec(
        string FontSizeSpec,      // e.g., "2em", "1.5em"
        float? FontSizePx,        // Computed px value @ 16px base
        int? FontWeight,          // 400 = normal, 700 = bold
        string MarginSpec,        // e.g., "0.67em"
        float? MarginPx,          // Computed vertical margin in px
        string FontStyle = "normal"
    );

    /// <summary>
    /// Box model specification for an element
    /// </summary>
    public record BoxModelSpec(
        float MarginTop,
        float MarginRight,
        float MarginBottom,
        float MarginLeft,
        float PaddingTop,
        float PaddingRight,
        float PaddingBottom,
        float PaddingLeft
    );

    /// <summary>
    /// Flexbox specification defaults
    /// </summary>
    public class FlexboxSpec
    {
        public string FlexDirection { get; set; }
        public string FlexWrap { get; set; }
        public string JustifyContent { get; set; }
        public string AlignItems { get; set; }
        public string AlignContent { get; set; }
        public float FlexGrow { get; set; }
        public float FlexShrink { get; set; }
        public string FlexBasis { get; set; }
    }
}
