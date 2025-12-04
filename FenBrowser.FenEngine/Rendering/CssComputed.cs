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
    }
}