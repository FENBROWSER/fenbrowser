using System.Collections.Generic;

namespace FenBrowser.DevTools.Core;

/// <summary>
/// Provides CSS property autocomplete suggestions.
/// </summary>
public static class CssAutocomplete
{
    // Common CSS color names
    private static readonly string[] ColorValues = {
        "transparent", "currentColor", "inherit",
        "black", "white", "red", "green", "blue", "yellow", "orange", "purple", "pink", "gray", "grey",
        "aqua", "navy", "teal", "olive", "maroon", "lime", "fuchsia", "silver"
    };
    
    // Common size units
    private static readonly string[] SizeUnits = { "px", "em", "rem", "%", "vh", "vw", "auto" };
    
    // Display values
    private static readonly string[] DisplayValues = {
        "none", "block", "inline", "inline-block", "flex", "inline-flex", "grid", "inline-grid", "table", "contents"
    };
    
    // Position values
    private static readonly string[] PositionValues = { "static", "relative", "absolute", "fixed", "sticky" };
    
    // Flex values
    private static readonly string[] FlexDirectionValues = { "row", "row-reverse", "column", "column-reverse" };
    private static readonly string[] JustifyContentValues = { "flex-start", "flex-end", "center", "space-between", "space-around", "space-evenly" };
    private static readonly string[] AlignItemsValues = { "stretch", "flex-start", "flex-end", "center", "baseline" };
    
    // Font weight
    private static readonly string[] FontWeightValues = { "normal", "bold", "bolder", "lighter", "100", "200", "300", "400", "500", "600", "700", "800", "900" };
    
    // Text align
    private static readonly string[] TextAlignValues = { "left", "right", "center", "justify", "start", "end" };
    
    // Overflow
    private static readonly string[] OverflowValues = { "visible", "hidden", "scroll", "auto", "clip" };
    
    // Border style
    private static readonly string[] BorderStyleValues = { "none", "solid", "dashed", "dotted", "double", "groove", "ridge", "inset", "outset" };
    
    // Cursor
    private static readonly string[] CursorValues = { "auto", "default", "pointer", "move", "text", "wait", "help", "crosshair", "not-allowed", "grab", "grabbing" };
    
    /// <summary>
    /// Get suggestions for a CSS property value.
    /// </summary>
    public static List<string> GetSuggestions(string propertyName, string currentValue)
    {
        var suggestions = new List<string>();
        string prop = propertyName.ToLower().Trim();
        string val = currentValue.ToLower().Trim();
        
        // Color properties
        if (prop.Contains("color") || prop == "background" || prop == "fill" || prop == "stroke")
        {
            AddMatching(suggestions, ColorValues, val);
        }
        // Display
        else if (prop == "display")
        {
            AddMatching(suggestions, DisplayValues, val);
        }
        // Position
        else if (prop == "position")
        {
            AddMatching(suggestions, PositionValues, val);
        }
        // Flex properties
        else if (prop == "flex-direction")
        {
            AddMatching(suggestions, FlexDirectionValues, val);
        }
        else if (prop == "justify-content")
        {
            AddMatching(suggestions, JustifyContentValues, val);
        }
        else if (prop == "align-items" || prop == "align-self" || prop == "align-content")
        {
            AddMatching(suggestions, AlignItemsValues, val);
        }
        // Font weight
        else if (prop == "font-weight")
        {
            AddMatching(suggestions, FontWeightValues, val);
        }
        // Text align
        else if (prop == "text-align")
        {
            AddMatching(suggestions, TextAlignValues, val);
        }
        // Overflow
        else if (prop.StartsWith("overflow"))
        {
            AddMatching(suggestions, OverflowValues, val);
        }
        // Border style
        else if (prop.Contains("border") && prop.Contains("style"))
        {
            AddMatching(suggestions, BorderStyleValues, val);
        }
        // Cursor
        else if (prop == "cursor")
        {
            AddMatching(suggestions, CursorValues, val);
        }
        // Size properties (margin, padding, width, height, etc.)
        else if (prop.Contains("margin") || prop.Contains("padding") || 
                 prop == "width" || prop == "height" || prop == "top" || prop == "left" || 
                 prop == "right" || prop == "bottom" || prop.Contains("gap") ||
                 prop == "font-size" || prop.Contains("radius"))
        {
            // If they typed a number, suggest units
            if (val.Length > 0 && char.IsDigit(val[val.Length - 1]))
            {
                foreach (var unit in SizeUnits)
                {
                    if (unit != "auto")
                        suggestions.Add(val + unit);
                }
            }
            else
            {
                suggestions.Add("auto");
                suggestions.Add("0");
            }
        }
        
        return suggestions;
    }
    
    private static void AddMatching(List<string> list, string[] values, string prefix)
    {
        foreach (var v in values)
        {
            if (string.IsNullOrEmpty(prefix) || v.StartsWith(prefix))
            {
                list.Add(v);
            }
        }
    }
}
