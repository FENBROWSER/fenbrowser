using System.Collections.Generic;
using FenBrowser.Core.Dom;
using SkiaSharp;
using FenBrowser.Core.Css; // For Thickness/CornerRadius if needed (assuming based on partial view)
// If Thickness not available, use struct or dummy. Assuming avalonia types removed, let's use what was in the file.
// The file used "Thickness" and "CornerRadius". These might be in FenBrowser.Core or defined locally.
// Let's assume they are standard or mapped. 
using FenBrowser.Core; // Check for Thickness

namespace FenBrowser.FenEngine.Rendering
{
    public class InputOverlayData
    {
        public Element Node { get; set; }
        public SKRect Bounds { get; set; }
        public string Type { get; set; }
        public string InitialText { get; set; }
        public string Placeholder { get; set; }  
        public List<string> Options { get; set; } = new List<string>();
        public int SelectedIndex { get; set; } = -1;

        // Visual Styling
        public SKColor? BackgroundColor { get; set; }
        public SKColor? TextColor { get; set; }
        public string FontFamily { get; set; }
        public float FontSize { get; set; }
        public Thickness BorderThickness { get; set; }
        public SKColor? BorderColor { get; set; }
        public CssCornerRadius BorderRadius { get; set; }
        public string TextAlign { get; set; } 

        // Pseudo-elements styling
        public SKColor? PlaceholderColor { get; set; }
        public string PlaceholderFontFamily { get; set; }
        public float PlaceholderFontSize { get; set; }

        public SKColor? SelectionColor { get; set; }
        public SKColor? SelectionBackgroundColor { get; set; }
    }

    public class BoxShadowParsed
    {
        public float OffsetX { get; set; }
        public float OffsetY { get; set; }
        public float BlurRadius { get; set; }
        public float SpreadRadius { get; set; }
        public SKColor Color { get; set; } = SKColors.Black;
        public bool Inset { get; set; } = false;
        
        /// <summary>
        /// Parse CSS box-shadow value into a list of shadows.
        /// </summary>
        public static List<BoxShadowParsed> Parse(string boxShadow)
        {
            var shadows = new List<BoxShadowParsed>();
            if (string.IsNullOrWhiteSpace(boxShadow) || boxShadow == "none")
                return shadows;
            
            // Split by comma for multiple shadows
            var parts = boxShadow.Split(',');
            foreach (var part in parts)
            {
                var shadow = ParseSingle(part.Trim());
                if (shadow != null)
                    shadows.Add(shadow);
            }
            
            return shadows;
        }
        
        private static BoxShadowParsed ParseSingle(string shadowStr)
        {
            if (string.IsNullOrEmpty(shadowStr)) return null;
            
            var shadow = new BoxShadowParsed();
            var tokens = shadowStr.Split(new[] { ' ' }, System.StringSplitOptions.RemoveEmptyEntries);
            
            int numericIndex = 0;
            foreach (var token in tokens)
            {
                if (token.ToLower() == "inset")
                {
                    shadow.Inset = true;
                    continue;
                }
                
                if (TryParseLength(token, out float length))
                {
                    switch (numericIndex)
                    {
                        case 0: shadow.OffsetX = length; break;
                        case 1: shadow.OffsetY = length; break;
                        case 2: shadow.BlurRadius = length; break;
                        case 3: shadow.SpreadRadius = length; break;
                    }
                    numericIndex++;
                }
                else if (SKColor.TryParse(token, out var color))
                {
                    shadow.Color = color;
                }
            }
            
            return numericIndex >= 2 ? shadow : null;
        }
        
        private static bool TryParseLength(string str, out float length)
        {
            length = 0;
            if (string.IsNullOrEmpty(str)) return false;
            
            str = str.Trim().ToLower();
            if (str.EndsWith("px")) str = str.Substring(0, str.Length - 2);
            
            return float.TryParse(str, out length);
        }
    }

    public class TextDecorationParsed
    {
        public bool Underline { get; set; }
        public bool Overline { get; set; }
        public bool LineThrough { get; set; }
        public SKColor? Color { get; set; }
        public string Style { get; set; } = "solid"; 
    }
}
