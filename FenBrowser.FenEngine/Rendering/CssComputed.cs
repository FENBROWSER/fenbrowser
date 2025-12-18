using SkiaSharp;
using FenBrowser.Core;
// using FenBrowser.Core.Math; // Namespace moved to Core
// using Avalonia; // Removed

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

        public CssComputed Before { get; set; }
        public CssComputed After { get; set; }
        public CssComputed Marker { get; set; }  // ::marker pseudo-element styles
        public CssComputed Placeholder { get; set; }  // ::placeholder pseudo-element styles
        public CssComputed Selection { get; set; }  // ::selection pseudo-element styles
        public CssComputed FirstLine { get; set; }  // ::first-line pseudo-element styles
        public CssComputed FirstLetter { get; set; }  // ::first-letter pseudo-element styles
        
        // Cache for virtual DOM node used in Rendering
        public object PseudoElementInstance { get; set; }

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
        public double? LeftPercent { get; set; }
        public double? Top { get; set; }
        public double? TopPercent { get; set; }
        public double? Right { get; set; }
        public double? RightPercent { get; set; }
        public double? Bottom { get; set; }
        public double? BottomPercent { get; set; }
        public string Overflow { get; set; }
        public string OverflowX { get; set; }
        public string OverflowY { get; set; }
        
        // Background properties
        public string BackgroundClip { get; set; }    // border-box, padding-box, content-box
        public string BackgroundOrigin { get; set; }  // border-box, padding-box, content-box
        public string BackgroundRepeat { get; set; }  // repeat, no-repeat, repeat-x, repeat-y
        public string BackgroundImage { get; set; }   // url, gradient, etc.

        // Box model
        public Thickness Margin { get; set; }
        public Thickness Padding { get; set; }
        public Thickness BorderThickness { get; set; }
        public SKColor? BorderBrush { get; set; }
        public CornerRadius BorderRadius { get; set; }
        
        // Border styles - solid, dashed, dotted, double, groove, ridge, inset, outset, none, hidden
        public string BorderStyleTop { get; set; } = "none";
        public string BorderStyleRight { get; set; } = "none";
        public string BorderStyleBottom { get; set; } = "none";
        public string BorderStyleLeft { get; set; } = "none";

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

        // Expression storage for complex functions (calc, min, max, clamp)
        public string WidthExpression { get; set; }
        public string HeightExpression { get; set; }
        public string MinWidthExpression { get; set; }
        public string MinHeightExpression { get; set; }
        public string MaxWidthExpression { get; set; }
        public string MaxHeightExpression { get; set; }

        // Flexbox / Grid gaps
        public double? ColumnGap { get; set; }
        public double? RowGap { get; set; }
        public double? Gap { get; set; }
        public string GridTemplateColumns { get; set; }
        public string GridTemplateRows { get; set; }
        public string GridTemplateAreas { get; set; }
        
        // Grid Item Placement
        public string GridColumnStart { get; set; }
        public string GridColumnEnd { get; set; }
        public string GridRowStart { get; set; }
        public string GridRowEnd { get; set; }
        
        // Grid Auto Placement & Implicit Tracks
        public string GridAutoFlow { get; set; }      // "row", "column", "row dense", "column dense"
        public string GridAutoColumns { get; set; }   // Track sizing for implicit cols
        public string GridAutoRows { get; set; }      // Track sizing for implicit rows
        public string GridArea { get; set; } // Shorthand storage

        // Flexbox
        public string FlexDirection { get; set; }
        public string FlexWrap { get; set; }
        public string JustifyContent { get; set; }
        public string AlignItems { get; set; }
        public string AlignContent { get; set; }
        public string AlignSelf { get; set; }  // Individual flex item alignment override
        public double? FlexGrow { get; set; }
        public double? FlexShrink { get; set; }
        public double? FlexBasis { get; set; }
        public int? Order { get; set; }  // Flex item ordering

        // Image object-fit and object-position (for img, video, etc.)
        public string ObjectFit { get; set; }        // fill, contain, cover, none, scale-down
        public string ObjectPosition { get; set; }   // CSS position value (e.g., "center", "top left")
        public string ImageRendering { get; set; }   // auto, crisp-edges, pixelated, smooth

        // Typography & Visuals
        // Typography & Visuals
        public object Background { get; set; } // Legacy brush/gradient holder
        public object Foreground { get; set; } // Legacy brush holder
        // public object BorderBrush { get; set; } // Duplicate removed
        public SKColor? BackgroundColor { get; set; }
        public SKColor? ForegroundColor { get; set; }
        public double? FontSize { get; set; }
        public int? FontWeight { get; set; } // Changed to int (100-900)
        public SKFontStyleSlant? FontStyle { get; set; }

        public string FontFamilyName { get; set; }
        private SKTypeface _fontFamily; // Changed to SKTypeface
        public SKTypeface FontFamily
        {
            get
            {
                if (_fontFamily == null && !string.IsNullOrEmpty(FontFamilyName))
                {
                    try { _fontFamily = SKTypeface.FromFamilyName(FontFamilyName); } catch { }
                }
                return _fontFamily;
            }
            set { _fontFamily = value; }
        }

        public SKTextAlign? TextAlign { get; set; }
        public string Hyphens { get; set; }
        public string TextDecoration { get; set; }
        public string ListStyleType { get; set; }
        
        // Text spacing properties
        public double? WordSpacing { get; set; }      // px offset for spaces between words
        public double? LetterSpacing { get; set; }    // px offset for spaces between letters

        // Extra color for border
        public SKColor? BorderBrushColor { get; set; }

        // Visual effects
        public string Transform { get; set; }
        public string TransformOrigin { get; set; }      // transform-origin (e.g., "center", "50% 50%")
        public string TransformStyle { get; set; }       // flat, preserve-3d
        public string BackfaceVisibility { get; set; }   // visible, hidden
        public string Perspective { get; set; }          // perspective value (e.g., "1000px")
        public string PerspectiveOrigin { get; set; }    // perspective-origin (e.g., "50% 50%")
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
        
        // Bidirectional text support
        public string Direction { get; set; }            // ltr, rtl, auto
        public string UnicodeBidi { get; set; }          // normal, embed, bidi-override, isolate, isolate-override, plaintext

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
        
        // Modern CSS Properties
        public string Contain { get; set; }                  // none, strict, content, size, layout, style, paint
        public string ColorScheme { get; set; }              // light, dark, normal
        public string AccentColor { get; set; }              // auto or color value for form controls
        public string CaretColor { get; set; }               // auto or color value for text cursor
        public string WillChange { get; set; }               // auto, transform, opacity, etc.
        public string Isolation { get; set; }                // auto, isolate
        public string MixBlendMode { get; set; }             // normal, multiply, screen, overlay, etc.
        
        // Interaction Properties
        public string PointerEvents { get; set; }            // auto, none, visiblePainted, etc.
        public string UserSelect { get; set; }               // auto, text, none, contain, all
        public string TouchAction { get; set; }              // auto, none, pan-x, pan-y, manipulation
        public string Resize { get; set; }                   // none, both, horizontal, vertical
        public string ScrollBehavior { get; set; }           // auto, smooth
        
        // Print and Page
        public string PageBreakBefore { get; set; }          // auto, always, avoid
        public string PageBreakAfter { get; set; }           // auto, always, avoid
        public string PageBreakInside { get; set; }          // auto, avoid
        
        // Additional Text Properties
        public string WritingMode { get; set; }              // horizontal-tb, vertical-rl, vertical-lr
        public string TextTransform { get; set; }            // none, capitalize, uppercase, lowercase
        public string TextRendering { get; set; }            // auto, optimizeSpeed, optimizeLegibility, geometricPrecision
        public string FontVariant { get; set; }              // normal, small-caps
        public string FontFeatureSettings { get; set; }      // OpenType features
        public double? TextIndent { get; set; }              // First line indentation in pixels
        public string WordBreak { get; set; }                // normal, break-all, keep-all, break-word
        public string OverflowWrap { get; set; }             // normal, break-word, anywhere
        public string TabSize { get; set; }                  // Number or length for tab character width
        public string TextUnderlineOffset { get; set; }      // auto or length value
        public string TextDecorationStyle { get; set; }      // solid, double, dotted, dashed, wavy
        public string TextDecorationThickness { get; set; }  // auto, from-font, or length
        
        // List Properties
        public string ListStylePosition { get; set; }        // inside, outside
        public string ListStyleImage { get; set; }           // none or url()
        
        // Table Properties
        public string TableLayout { get; set; }              // auto, fixed
        public string BorderCollapse { get; set; }           // separate, collapse
        public string BorderSpacing { get; set; }            // length value(s)
        public string CaptionSide { get; set; }              // top, bottom
        public string EmptyCells { get; set; }               // show, hide
        
        // Visibility & Display
        public string Visibility { get; set; }               // visible, hidden, collapse
        public string BackfaceVisibilityVal { get; set; }    // visible, hidden (for 3D)
        public string Appearance { get; set; }               // none, auto, button, textfield, etc.
        
        // Columns (Multi-column layout)
        public string Columns { get; set; }                  // column-width column-count shorthand
        public string ColumnCount { get; set; }              // auto or number
        public string ColumnWidth { get; set; }              // auto or length
        public string ColumnGapValue { get; set; }           // normal or length
        public string ColumnRuleWidth { get; set; }          // thin, medium, thick, or length
        public string ColumnRuleStyle { get; set; }          // none, solid, dotted, etc.
        public string ColumnRuleColor { get; set; }          // color value
        public string ColumnSpan { get; set; }               // none, all
        
        // Outline (distinct from border)
        public string OutlineWidth { get; set; }             // thin, medium, thick, or length
        public string OutlineStyle { get; set; }             // none, solid, dotted, dashed, etc.
        public string OutlineColor { get; set; }             // color value
        public string OutlineOffset { get; set; }            // length value
        
        // Inset (logical shorthand for top/right/bottom/left)
        public string Inset { get; set; }                    // Shorthand for positioning
        public string InsetBlock { get; set; }               // top + bottom in logical terms
        public string InsetInline { get; set; }              // left + right in logical terms
    }
}