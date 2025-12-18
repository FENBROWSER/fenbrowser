using SkiaSharp;
using System;
using FenBrowser.Core;

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
        public static void Apply(LiteElement node, ref CssComputed style)
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
                if (string.IsNullOrEmpty(style.Display)) style.Display = "block";
            }

            if (tag == "BODY")
            {
                if (style == null) style = new CssComputed();
                // Default browser margin is 8px
                if (style.Margin.Left == 0 && style.Margin.Top == 0 && 
                    style.Margin.Right == 0 && style.Margin.Bottom == 0)
                {
                    style.Margin = new Avalonia.Thickness(8);
                }
            }

            // Hidden Metadata Elements
            if (tag == "HEAD" || tag == "TITLE" || tag == "SCRIPT" || tag == "STYLE" || 
                tag == "META" || tag == "LINK" || tag == "BASE" || tag == "NOSCRIPT" || tag == "TEMPLATE")
            {
                if (style == null) style = new CssComputed();
                style.Display = "none";
            }

            // Generic Block Elements
            if (tag == "DIV" || tag == "NAV" || tag == "SECTION" || tag == "ARTICLE" || 
                tag == "HEADER" || tag == "FOOTER" || tag == "ASIDE" || tag == "MAIN" || tag == "FIGURE")
            {
                if (style == null) style = new CssComputed();
                if (string.IsNullOrEmpty(style.Display)) style.Display = "block";
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
                    style.FontWeight = Avalonia.Media.FontWeight.Bold;
                
                if (style.Margin.Left == 0 && style.Margin.Top == 0)
                {
                    double marginEm = (tag == "H1" || tag == "H2") ? 0.67 : 1.0;
                    float m = (float)(style.FontSize.Value * marginEm);
                    style.Margin = new Avalonia.Thickness(0, m, 0, m);
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
                    style.Margin = new Avalonia.Thickness(0, 16, 0, 16); // 1em default
                }
            }

            // Lists
            if (tag == "UL" || tag == "OL")
            {
                if (style == null) style = new CssComputed();
                if (style.Padding.Left == 0) style.Padding = new Avalonia.Thickness(40, 0, 0, 0);
                if (style.Margin.Top == 0) style.Margin = new Avalonia.Thickness(0, 16, 0, 16);
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
                if (style.FontFamily == null)
                {
                    style.FontFamily = new Avalonia.Media.FontFamily("monospace");
                }
            }

            // Blockquote
            if (tag == "BLOCKQUOTE")
            {
                if (style == null) style = new CssComputed();
                if (style.Margin.Left == 0)
                {
                    style.Margin = new Avalonia.Thickness(40, 16, 40, 16);
                }
            }

            // HR
            if (tag == "HR")
            {
                if (style == null) style = new CssComputed();
                if (!style.Height.HasValue) style.Height = 1;
                if (!style.BackgroundColor.HasValue) 
                    style.BackgroundColor = Avalonia.Media.Colors.Gray;
                if (style.Margin.Top == 0)
                    style.Margin = new Avalonia.Thickness(0, 8, 0, 8);
            }

            // Strong/B
            if (tag == "STRONG" || tag == "B")
            {
                if (style == null) style = new CssComputed();
                if (!style.FontWeight.HasValue)
                    style.FontWeight = Avalonia.Media.FontWeight.Bold;
            }

            // Em/I
            if (tag == "EM" || tag == "I")
            {
                if (style == null) style = new CssComputed();
                if (!style.FontStyle.HasValue)
                    style.FontStyle = Avalonia.Media.FontStyle.Italic;
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
        private static void ApplyFormElementStyles(LiteElement node, ref CssComputed style, string tag)
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
            bool hasBackground = (style.BackgroundColor.HasValue && style.BackgroundColor.Value.A > 0) || cssSpecifiedBackground;
            if (!hasBackground && tag != "FIELDSET")
            {
                style.BackgroundColor = isButtonType 
                    ? Avalonia.Media.Color.FromRgb(0xf8, 0xf9, 0xfa)  // Light gray
                    : Avalonia.Media.Colors.White;
            }

            // Border
            if (style.BorderThickness.Top == 0 && style.BorderThickness.Left == 0)
            {
                style.BorderThickness = new Avalonia.Thickness(1);
                style.BorderBrushColor = isButtonType 
                    ? Avalonia.Media.Color.FromRgb(0xf8, 0xf9, 0xfa)
                    : Avalonia.Media.Colors.Gray;
            }

            // Padding
            if (style.Padding.Top == 0 && style.Padding.Left == 0)
            {
                style.Padding = isButtonType 
                    ? new Avalonia.Thickness(16, 8, 16, 8)
                    : new Avalonia.Thickness(5, 2, 5, 2);
            }

            // Border radius
            if (style.BorderRadius.TopLeft == 0)
            {
                if (isButtonType)
                    style.BorderRadius = new Avalonia.CornerRadius(8);
                else if (tag == "INPUT" || tag == "TEXTAREA")
                    style.BorderRadius = new Avalonia.CornerRadius(4);
            }
        }
    }
}
