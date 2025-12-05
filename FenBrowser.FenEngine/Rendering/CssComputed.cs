using System.Collections.Generic;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;

namespace FenBrowser.FenEngine.Rendering
{
    /// <summary>
    /// Computed CSS values for a DOM element. Contains a raw property map plus common typed projections
    /// used by DomBasicRenderer and RendererStyles.
    /// </summary>
    public sealed class CssComputed
    {
        public Dictionary<string, string> Map { get; private set; }
        public Dictionary<string, string> CustomProperties { get; private set; }

        public CssComputed()
        {
            Map = new Dictionary<string, string>(System.StringComparer.OrdinalIgnoreCase);
            CustomProperties = new Dictionary<string, string>(System.StringComparer.Ordinal);
        }

        // Display & positioning
        public string Display { get; set; }
        public string Position { get; set; }
        public int? ZIndex { get; set; }
        public double? Left { get; set; }
        public double? Top { get; set; }
        public double? Right { get; set; }
        public double? Bottom { get; set; }
        public string Overflow { get; set; }

        // Box model
        public Thickness Margin { get; set; }
        public Thickness Padding { get; set; }
        public Thickness BorderThickness { get; set; }
        public IBrush BorderBrush { get; set; }
        public CornerRadius BorderRadius { get; set; }

        // Sizing
        public double? Width { get; set; }
        public double? Height { get; set; }
        public double? MinWidth { get; set; }
        public double? MinHeight { get; set; }
        public double? MaxWidth { get; set; }
        public double? MaxHeight { get; set; }
        public double? WidthPercent { get; set; }
        public double? HeightPercent { get; set; }
        public double? AspectRatio { get; set; }  // width/height ratio

        // Flexbox / Grid gaps
        public double? ColumnGap { get; set; }
        public double? RowGap { get; set; }
        public double? Gap { get; set; }
        public string GridTemplateColumns { get; set; }

        // Flexbox
        public string FlexDirection { get; set; }
        public string FlexWrap { get; set; }
        public string JustifyContent { get; set; }
        public string AlignItems { get; set; }
        public string AlignContent { get; set; }
        public double? FlexGrow { get; set; }
        public double? FlexShrink { get; set; }
        public double? FlexBasis { get; set; }

        // Typography & Visuals
        public IBrush Background { get; set; }
        public Color? BackgroundColor { get; set; }
        public IBrush Foreground { get; set; }
        public Color? ForegroundColor { get; set; }
        public double? FontSize { get; set; }
        public FontWeight? FontWeight { get; set; }
        public FontStyle? FontStyle { get; set; }

        public string FontFamilyName { get; set; }
        private FontFamily _fontFamily;
        public FontFamily FontFamily
        {
            get
            {
                if (_fontFamily == null && !string.IsNullOrEmpty(FontFamilyName))
                {
                    try { _fontFamily = new FontFamily(FontFamilyName); } catch { }
                }
                return _fontFamily;
            }
            set { _fontFamily = value; }
        }

        public TextAlignment? TextAlign { get; set; }
        public string Hyphens { get; set; }
        public string TextDecoration { get; set; }
        public string ListStyleType { get; set; }

        // Extra color for border
        public Color? BorderBrushColor { get; set; }

        // Visual effects
        public string Transform { get; set; }
        public string Float { get; set; }
        public double? Opacity { get; set; }        // 0.0 to 1.0
        public string TextShadow { get; set; }      // CSS text-shadow value
        public string BoxShadow { get; set; }       // CSS box-shadow value

        // New properties for better fidelity
        public double? LineHeight { get; set; }     // Multiplier (e.g. 1.5) or px value
        public string VerticalAlign { get; set; }
        public string WhiteSpace { get; set; }
        public string TextOverflow { get; set; }
        public string BoxSizing { get; set; }
        public string Cursor { get; set; }

        // CSS Transitions
        public string Transition { get; set; }              // transition shorthand
        public string TransitionProperty { get; set; }      // which properties to animate
        public string TransitionDuration { get; set; }      // duration (e.g., "0.3s", "300ms")
        public string TransitionTimingFunction { get; set; } // easing function
        public string TransitionDelay { get; set; }         // delay before start

        // CSS Filters
        public string Filter { get; set; }                  // filter effects (blur, grayscale, etc.)
        public string BackdropFilter { get; set; }          // backdrop filter effects
        public string ClipPath { get; set; }                // clip-path (circle, polygon, etc.)

        // CSS Scroll Snap
        public string ScrollSnapType { get; set; }          // x, y, both, mandatory/proximity
        public string ScrollSnapAlign { get; set; }         // start, center, end

        // CSS Counters
        public string CounterReset { get; set; }            // reset counter(s)
        public string CounterIncrement { get; set; }        // increment counter(s)
        public string Content { get; set; }                 // content property for ::before/::after

        // CSS Masks
        public string MaskImage { get; set; }               // mask-image
        public string MaskMode { get; set; }                // alpha, luminance
        public string MaskRepeat { get; set; }              // repeat, no-repeat, etc.
        public string MaskPosition { get; set; }            // position
        public string MaskSize { get; set; }                // size

        // CSS Shapes
        public string ShapeOutside { get; set; }            // shape-outside
        public string ShapeMargin { get; set; }             // shape-margin
        public string ShapeImageThreshold { get; set; }     // shape-image-threshold

        // @layer tracking
        public string CascadeLayer { get; set; }            // which @layer this rule belongs to
    }
}