using FenBrowser.Core.Css;
using FenBrowser.Core.Dom;
using SkiaSharp;
using System;
using FenBrowser.Core;
// using FenBrowser.Core.Math; // Namespace moved to Core

namespace FenBrowser.FenEngine.Rendering.UserAgent
{
    /// <summary>
    /// User Agent stylesheet provider.
    /// Applies default browser styles for standard HTML elements.
    /// </summary>
    public static class UAStyleProvider
    {
        /// <summary>
        /// Apply User Agent (browser default) styles to an element.
        /// These are applied only when CSS doesn't specify a value.
        /// </summary>
        /// <param name="node">The element</param>
        /// <param name="style">The computed style (ref, can be created if null)</param>
        public static void Apply(Element node, ref CssComputed style)
        {
            if (node == null) return;
            string tag = node.Tag?.ToUpperInvariant();
            if (string.IsNullOrEmpty(tag)) return;

            // HTML and BODY
            if (tag == "HTML" || tag == "BODY")
            {
                if (style == null) style = new CssComputed();
                
                // Root elements fill viewport (critical for modern layouts)
                if (!style.Height.HasValue && !style.HeightPercent.HasValue)
                {
                    style.HeightPercent = 100;
                }
                if (string.IsNullOrEmpty(style.Display)) style.Display = "flex";
                if (string.IsNullOrEmpty(style.FlexDirection)) style.FlexDirection = "column";
            }

            if (tag == "BODY")
            {
                if (style == null) style = new CssComputed();
                // Default browser margin is 8px
                if (style.Margin.Left == 0 && style.Margin.Top == 0 && 
                    style.Margin.Right == 0 && style.Margin.Bottom == 0)
                {
                    style.Margin = new Thickness(8);
                }
            }

            if (tag == "HEAD" || tag == "TITLE" || tag == "SCRIPT" || tag == "STYLE" || 
                tag == "META" || tag == "LINK" || tag == "BASE" || tag == "NOSCRIPT" || tag == "TEMPLATE")
            {
                if (style == null) style = new CssComputed();
                style.Display = "none";
            }

            // Dialog
            if (tag == "DIALOG")
            {
                if (style == null) style = new CssComputed();
                bool isOpen = node.Attr != null && node.Attr.ContainsKey("open");
                if (!isOpen)
                {
                     style.Display = "none";
                }
                else
                {
                     if (string.IsNullOrEmpty(style.Display)) style.Display = "block";
                     
                     bool hasBg = (style.BackgroundColor.HasValue && style.BackgroundColor.Value.Alpha > 0) || 
                                  (style.Map != null && (style.Map.ContainsKey("background") || style.Map.ContainsKey("background-color")));
                                  
                     if (!hasBg) style.BackgroundColor = SKColors.White;
                     if (style.Padding.Left == 0) style.Padding = new Thickness(24);
                     if (style.BorderThickness.Left == 0) style.BorderThickness = new Thickness(2);
                     if (style.BorderBrushColor == null) style.BorderBrushColor = SKColors.Black;
                     if (style.ForegroundColor == null) style.ForegroundColor = SKColors.Black;
                     
                     if (node.Attr.ContainsKey("modal"))
                     {
                         if (string.IsNullOrEmpty(style.Position)) style.Position = "fixed";
                         
                         // Center using transform trick
                         if (!style.Left.HasValue && !style.LeftPercent.HasValue) style.LeftPercent = 50;
                         if (!style.Top.HasValue && !style.TopPercent.HasValue) style.TopPercent = 50;
                         
                         // Only set transform if not present (don't override user transform)
                         if (string.IsNullOrEmpty(style.Transform)) style.Transform = "translate(-50%, -50%)";
                         
                         // Ensure Z-Index is high? But Top Layer painting handles visibility.
                         // But for Layout, Z-Index helps? 
                         // NewPaintTreeBuilder uses TopLayer list, so Z-Index is irrelevant for painting order there.
                     }
                     else
                     {
                         if (string.IsNullOrEmpty(style.Position)) style.Position = "absolute";
                     }
                }
            }

            // Generic Block Elements
            if (tag == "DIV" || tag == "NAV" || tag == "SECTION" || tag == "ARTICLE" || 
                tag == "HEADER" || tag == "FOOTER" || tag == "ASIDE" || tag == "MAIN" || tag == "FIGURE")
            {
                if (style == null) style = new CssComputed();
                if (string.IsNullOrEmpty(style.Display)) style.Display = "block";
                
                if (tag == "MAIN")
                {
                    // ARCHITECTURAL FIX: Centering via UA flex properties
                    if (string.IsNullOrEmpty(style.Display) || style.Display == "block") 
                        style.Display = "flex";
                    
                    if (string.IsNullOrEmpty(style.FlexDirection)) style.FlexDirection = "column";
                    if (string.IsNullOrEmpty(style.JustifyContent)) style.JustifyContent = "center";
                    if (string.IsNullOrEmpty(style.AlignItems)) style.AlignItems = "center";
                    if (!style.FlexGrow.HasValue) style.FlexGrow = 1.0;
                }
            }

            // Center Tag (Legacy)
            if (tag == "CENTER")
            {
                if (style == null) style = new CssComputed();
                if (string.IsNullOrEmpty(style.Display)) style.Display = "block";
                if (style.TextAlign == null) style.TextAlign = SKTextAlign.Center;
            }
            
            // SVG
            if (tag == "SVG")
            {
                if (style == null) style = new CssComputed();
                if (string.IsNullOrEmpty(style.Display)) style.Display = "inline";
            }

            // Headings (H1-H6)
            if (tag.Length == 2 && tag[0] == 'H' && char.IsDigit(tag[1]))
            {
                if (style == null) style = new CssComputed();
                if (!style.FontSize.HasValue)
                {
                    double baseSize = tag switch
                    {
                        "H1" => 32.0,   // 2em
                        "H2" => 24.0,   // 1.5em
                        "H3" => 18.72,  // 1.17em
                        "H4" => 16.0,   // 1em
                        "H5" => 13.28,  // 0.83em
                        "H6" => 10.72,  // 0.67em
                        _ => 16.0
                    };
                    style.FontSize = baseSize;
                }
                if (!style.FontWeight.HasValue) 
                    style.FontWeight = 700;
                
                if (style.Margin.Left == 0 && style.Margin.Top == 0)
                {
                    double marginEm = (tag == "H1" || tag == "H2") ? 0.67 : 1.0;
                    float m = (float)(style.FontSize.Value * marginEm);
                    style.Margin = new Thickness(0, m, 0, m);
                }
            }

            // Form elements
            if (tag == "INPUT" || tag == "TEXTAREA" || tag == "BUTTON" || 
                tag == "SELECT" || tag == "FIELDSET")
            {
                ApplyFormElementStyles(node, ref style, tag);
            }

            // Paragraphs
            if (tag == "P")
            {
                if (style == null) style = new CssComputed();
                if (style.Margin.Top == 0 && style.Margin.Bottom == 0)
                {
                    style.Margin = new Thickness(0, 16, 0, 16); // 1em default
                }
            }

            // Lists
            if (tag == "UL" || tag == "OL")
            {
                if (style == null) style = new CssComputed();
                if (style.Padding.Left == 0) style.Padding = new Thickness(40, 0, 0, 0);
                if (style.Margin.Top == 0) style.Margin = new Thickness(0, 16, 0, 16);
            }

            // Links
            // Links - color would be set but CssComputed uses other mechanism
            // The renderer handles link colors directly
            if (tag == "A")
            {
                // Default link styling handled by renderer's text painting
            }

            // Pre/Code
            if (tag == "PRE" || tag == "CODE")
            {
                if (style == null) style = new CssComputed();
                if (style.FontFamilyName == null)
                {
                    style.FontFamilyName = "monospace";
                }
            }

            // Blockquote
            if (tag == "BLOCKQUOTE")
            {
                if (style == null) style = new CssComputed();
                if (style.Margin.Left == 0)
                {
                    style.Margin = new Thickness(40, 16, 40, 16);
                }
            }

            // HR
            if (tag == "HR")
            {
                if (style == null) style = new CssComputed();
                if (!style.Height.HasValue) style.Height = 1;
                if (!style.BackgroundColor.HasValue) 
                    style.BackgroundColor = SKColors.Gray;
                if (style.Margin.Top == 0)
                    style.Margin = new Thickness(0, 8, 0, 8);
            }

            // Strong/B
            if (tag == "STRONG" || tag == "B")
            {
                if (style == null) style = new CssComputed();
                if (!style.FontWeight.HasValue)
                    style.FontWeight = 700;
            }

            // Em/I
            if (tag == "EM" || tag == "I")
            {
                if (style == null) style = new CssComputed();
                if (!style.FontStyle.HasValue)
                    style.FontStyle = SKFontStyleSlant.Italic;
            }
            
            // CRITICAL: Ensure display is NEVER null
            // This removes undefined layout states that cause calculation errors
            if (string.IsNullOrEmpty(style?.Display))
            {
                if (style == null) style = new CssComputed();
                style.Display = IsInlineElement(tag) ? "inline" : "block";
            }
        }
        
        /// <summary>
        /// Determines if an element is inline by default per HTML5 spec.
        /// </summary>
        private static bool IsInlineElement(string tag)
        {
            if (string.IsNullOrEmpty(tag)) return false;
            
            return tag switch
            {
                // Phrasing content / inline elements
                "A" or "ABBR" or "ACRONYM" or "B" or "BDI" or "BDO" or "BIG" or 
                "BR" or "CITE" or "CODE" or "DATA" or "DEL" or "DFN" or "EM" or 
                "I" or "IMG" or "INS" or "KBD" or "LABEL" or "MAP" or "MARK" or 
                "METER" or "OUTPUT" or "PICTURE" or "PROGRESS" or "Q" or "RUBY" or 
                "S" or "SAMP" or "SMALL" or "SPAN" or "STRONG" or "SUB" or "SUP" or 
                "TIME" or "TT" or "U" or "VAR" or "WBR" => true,
                
                // Form elements that are inline-block by nature
                "INPUT" or "SELECT" or "TEXTAREA" or "BUTTON" => true,
                
                // SVG is inline by default
                "SVG" => true,
                
                // Everything else is block
                _ => false
            };
        }

        /// <summary>
        /// Apply styles specific to form elements.
        /// </summary>
        private static void ApplyFormElementStyles(Element node, ref CssComputed style, string tag)
        {
            if (style == null) style = new CssComputed();

            string inputType = node.Attr?.ContainsKey("type") == true 
                ? node.Attr["type"]?.ToLowerInvariant() : "";
            bool isButtonType = tag == "BUTTON" || inputType == "submit" || 
                               inputType == "button" || inputType == "reset";

            // Background - only apply default if CSS hasn't specified any background
            // Check both BackgroundColor and the raw Map for explicit "background" or "background-color"
            bool cssSpecifiedBackground = style.Map != null && 
                (style.Map.ContainsKey("background") || style.Map.ContainsKey("background-color"));
            bool hasBackground = (style.BackgroundColor.HasValue && style.BackgroundColor.Value.Alpha > 0) || cssSpecifiedBackground;
            if (!hasBackground && tag != "FIELDSET")
            {
                style.BackgroundColor = isButtonType 
                    ? new SKColor(0xf8, 0xf9, 0xfa)  // Light gray
                    : SKColors.White;
            }

            // Border - only apply if CSS hasn't explicitly specified a border
            bool cssSpecifiedBorder = style.Map != null && 
                (style.Map.ContainsKey("border") || style.Map.ContainsKey("border-width") || 
                 style.Map.ContainsKey("border-top-width") || style.Map.ContainsKey("border-left-width") ||
                 style.Map.ContainsKey("border-style") || style.Map.ContainsKey("border: none") ||
                 style.Map.ContainsKey("border: 0"));
            if (!cssSpecifiedBorder && style.BorderThickness.Top == 0 && style.BorderThickness.Left == 0)
            {
                style.BorderThickness = new Thickness(1);
                style.BorderBrushColor = isButtonType 
                    ? new SKColor(0xf8, 0xf9, 0xfa)
                    : SKColors.Gray;
            }

            // Padding
            if (style.Padding.Top == 0 && style.Padding.Left == 0)
            {
                style.Padding = isButtonType 
                    ? new Thickness(12, 6, 12, 6) // Reduced from 16,8 for tighter buttons
                    : new Thickness(5, 2, 5, 2);
            }

            // Border radius
            if (style.BorderRadius.TopLeft.Value == 0 && style.BorderRadius.TopLeft.IsPercent == false)
            {
                if (isButtonType)
                    style.BorderRadius = new CssCornerRadius(4); // Reduced from 8 to match Google/Standard
                else if (tag == "INPUT" || tag == "TEXTAREA")
                    style.BorderRadius = new CssCornerRadius(4);
            }
        }
    }
}


