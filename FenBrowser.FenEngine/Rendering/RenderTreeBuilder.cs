using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using System.Collections.Generic;
using FenBrowser.Core;

namespace FenBrowser.FenEngine.Rendering
{
    /// <summary>
    /// Converts a DOM tree (LiteElement) into a Render Tree (RenderObject).
    /// </summary>
    public static class RenderTreeBuilder
    {
        public static RenderObject Build(LiteElement root, Dictionary<LiteElement, CssComputed> styles)
        {
            if (root == null) return null;

            RenderObject renderNode = null;

            if (root.IsText)
            {
                if (!string.IsNullOrWhiteSpace(root.Text))
                {
                    renderNode = new RenderText { Text = root.Text, Node = root };
                }
            }
            else
            {
                // Filter out non-visual elements
                string tag = root.Tag?.ToUpperInvariant();
                if (tag == "HEAD" || tag == "STYLE" || tag == "SCRIPT" || tag == "META" || tag == "TITLE" || tag == "LINK")
                {
                    return null;
                }

                // Default to Box for all elements for now
                var box = new RenderBox { Node = root };
                renderNode = box;

                // Apply Styles
                if (styles != null && styles.TryGetValue(root, out var css))
                {
                    box.Style = css;
                    // Convert Colors to Brushes (Thread-safe fix)
                    if (css.BackgroundColor.HasValue && css.Background == null)
                        css.Background = new SolidColorBrush(css.BackgroundColor.Value);
                    if (css.ForegroundColor.HasValue && css.Foreground == null)
                        css.Foreground = new SolidColorBrush(css.ForegroundColor.Value);
                    if (css.BorderBrushColor.HasValue && css.BorderBrush == null)
                        css.BorderBrush = new SolidColorBrush(css.BorderBrushColor.Value);
                }
                else
                {
                    box.Style = new CssComputed(); // Default style
                }

                // Apply User Agent Styles (Defaults)
                ApplyUserAgentStyles(box);

                // List Marker Injection
                if (tag == "LI")
                {
                    // Check if list-style-type is none
                    bool suppressMarker = box.Style != null && 
                        string.Equals(box.Style.ListStyleType, "none", StringComparison.OrdinalIgnoreCase);
                    
                    if (!suppressMarker)
                    {
                        var parentTag = root.Parent?.Tag?.ToUpperInvariant();
                        string marker = null;
                        if (parentTag == "UL") marker = "• ";
                        else if (parentTag == "OL")
                        {
                            int index = 1;
                            var siblings = root.Parent.Children;
                            for (int i = 0; i < siblings.Count; i++)
                            {
                                if (siblings[i] == root) break;
                                if (siblings[i].Tag?.ToUpperInvariant() == "LI") index++;
                            }
                            marker = $"{index}. ";
                        }

                        if (marker != null)
                        {
                            var markerNode = new RenderText 
                            { 
                                Text = marker, 
                                Style = box.Style, // Inherit style
                                Parent = box
                            };
                            // Add as first child
                            box.AddChild(markerNode);
                        }
                    }
                }

                // Pseudo-element Injection (::before)
                if (box.Style != null && box.Style.Before != null)
                {
                    var beforeStyle = box.Style.Before;
                    if (beforeStyle.Content != "none")
                    {
                        var beforeNode = CreatePseudoElement(beforeStyle, box);
                        if (beforeNode != null) box.AddChild(beforeNode);
                    }
                }

                // Recurse
                if (root.Children != null)
                {
                    foreach (var child in root.Children)
                    {
                        var childRender = Build(child, styles);
                        if (childRender != null)
                        {
                            box.AddChild(childRender);
                        }
                    }
                }

                // Pseudo-element Injection (::after)
                if (box.Style != null && box.Style.After != null)
                {
                    var afterStyle = box.Style.After;
                    if (afterStyle.Content != "none")
                    {
                        var afterNode = CreatePseudoElement(afterStyle, box);
                        if (afterNode != null) box.AddChild(afterNode);
                    }
                }
            }

            return renderNode;
        }

        private static RenderObject CreatePseudoElement(CssComputed style, RenderBox parent)
        {
            var pseudoBox = new RenderBox { Node = parent.Node }; // Share node for context
            pseudoBox.Style = style;
            
            // Ensure colors are brushes
            if (style.BackgroundColor.HasValue && style.Background == null)
                style.Background = new SolidColorBrush(style.BackgroundColor.Value);
            if (style.ForegroundColor.HasValue && style.Foreground == null)
                style.Foreground = new SolidColorBrush(style.ForegroundColor.Value);
            if (style.BorderBrushColor.HasValue && style.BorderBrush == null)
                style.BorderBrush = new SolidColorBrush(style.BorderBrushColor.Value);

            // Handle Content
            string content = style.Content;
            if (!string.IsNullOrEmpty(content) && content != "none")
            {
                // Strip quotes
                if ((content.StartsWith("\"") && content.EndsWith("\"")) || 
                    (content.StartsWith("'") && content.EndsWith("'")))
                {
                    content = content.Substring(1, content.Length - 2);
                }
                
                if (!string.IsNullOrEmpty(content))
                {
                    var textNode = new RenderText { Text = content, Style = style, Parent = pseudoBox };
                    pseudoBox.AddChild(textNode);
                }
            }
            
            pseudoBox.Parent = parent;
            return pseudoBox;
        }


        private static void ApplyUserAgentStyles(RenderBox box)
        {
            if (box == null || box.Node == null || box.Style == null) return;

            var tag = box.Node.Tag?.ToUpperInvariant();
            if (string.IsNullOrEmpty(tag)) return;

            // Display Defaults
            if (box.Style.Display == null)
            {
                if (tag == "DIV" || tag == "P" || tag == "H1" || tag == "H2" || tag == "H3" || tag == "H4" || tag == "H5" || tag == "H6" || tag == "UL" || tag == "OL" || tag == "HEADER" || tag == "FOOTER" || tag == "MAIN" || tag == "SECTION" || tag == "ARTICLE" || tag == "NAV" || tag == "HR" || tag == "PRE" || tag == "BLOCKQUOTE" || tag == "DT" || tag == "DD")
                {
                    box.Style.Display = "block";
                }
                else if (tag == "LI")
                {
                    // Default to block, but if inside NAV or FOOTER (tag, id, or class), prefer inline-block
                    bool inNavOrFooter = false;
                    var p = box.Node;
                    while (p != null)
                    {
                         var pt = p.Tag?.ToUpperInvariant();
                         var pid = p.Id?.ToLowerInvariant() ?? "";
                         var pclass = p.GetAttribute("class")?.ToLowerInvariant() ?? "";
                         
                         if (pt == "NAV" || pt == "FOOTER" || 
                             pid.Contains("nav") || pid.Contains("menu") || pid.Contains("footer") ||
                             pclass.Contains("nav") || pclass.Contains("menu") || pclass.Contains("footer"))
                         {
                             inNavOrFooter = true;
                             break;
                         }
                         if (pt == "BODY") break;
                         p = p.Parent;
                    }

                    if (inNavOrFooter) 
                    {
                        box.Style.Display = "inline-block";
                        // Also force list-style-type to none if not specified
                        if (box.Style.ListStyleType == null) box.Style.ListStyleType = "none";
                    }
                    else 
                    {
                        box.Style.Display = "block";
                    }
                }
                else if (tag == "TABLE")
                {
                    box.Style.Display = "flex";
                    box.Style.FlexDirection = "column";
                }
                else if (tag == "TR")
                {
                    box.Style.Display = "flex";
                    box.Style.FlexDirection = "row";
                }
                else if (tag == "INPUT" || tag == "BUTTON" || tag == "SELECT" || tag == "IMG" || tag == "TEXTAREA")
                {
                    box.Style.Display = "inline-block";
                }
                else if (tag == "A" || tag == "SPAN" || tag == "B" || tag == "I" || tag == "STRONG" || tag == "EM" || tag == "LABEL" || tag == "CODE" || tag == "TH" || tag == "TD")
                {
                    if (tag == "TH" || tag == "TD")
                    {
                        box.Style.Display = "block"; // Flex items should be block-ified usually, or just let them be.
                        // But we want them to be treated as boxes in the flex row.
                        // Default is inline, which might cause issues if they have block children?
                        // Let's set to block to be safe as flex items.
                        box.Style.Display = "block";
                        // Add padding for table cells
                        if (IsZero(box.Style.Padding)) box.Style.Padding = new Thickness(4);
                        // Make cells grow to fill row (improves alignment)
                        if (!box.Style.FlexGrow.HasValue) box.Style.FlexGrow = 1;
                        // Start with 0 width to ensure equal distribution (like flex-basis: 0)
                        if (!box.Style.Width.HasValue && !box.Style.WidthPercent.HasValue) box.Style.Width = 0;
                    }
                    else
                    {
                        box.Style.Display = "inline";
                    }
                }
                else
                {
                    box.Style.Display = "inline"; // Default to inline for unknown
                }
            }

            // Margin Defaults (if not set)
            if (IsZero(box.Style.Margin))
            {
                if (tag == "BODY") box.Style.Margin = new Thickness(8);
                else if (tag == "P") box.Style.Margin = new Thickness(0, 16, 0, 16);
                else if (tag == "H1") box.Style.Margin = new Thickness(0, 21, 0, 21);
                else if (tag == "H2") box.Style.Margin = new Thickness(0, 19, 0, 19);
                else if (tag == "H3") box.Style.Margin = new Thickness(0, 18, 0, 18);
                else if (tag == "UL" || tag == "OL") box.Style.Margin = new Thickness(20, 16, 0, 16);
                else if (tag == "HR") box.Style.Margin = new Thickness(0, 8, 0, 8);
                else if (tag == "BLOCKQUOTE") box.Style.Margin = new Thickness(40, 16, 40, 16);
                else if (tag == "DD") box.Style.Margin = new Thickness(40, 0, 0, 0);
            }

            // Font Defaults
            if (tag == "H1") { if (!box.Style.FontSize.HasValue) box.Style.FontSize = 32; if (!box.Style.FontWeight.HasValue) box.Style.FontWeight = FontWeight.Bold; }
            else if (tag == "H2") { if (!box.Style.FontSize.HasValue) box.Style.FontSize = 24; if (!box.Style.FontWeight.HasValue) box.Style.FontWeight = FontWeight.Bold; }
            else if (tag == "H3") { if (!box.Style.FontSize.HasValue) box.Style.FontSize = 18; if (!box.Style.FontWeight.HasValue) box.Style.FontWeight = FontWeight.Bold; }
            else if (tag == "B" || tag == "STRONG" || tag == "TH") { if (!box.Style.FontWeight.HasValue) box.Style.FontWeight = FontWeight.Bold; }
            else if (tag == "I" || tag == "EM") { if (!box.Style.FontStyle.HasValue) box.Style.FontStyle = FontStyle.Italic; }
            else if (tag == "U") { if (box.Style.TextDecoration == null) box.Style.TextDecoration = "underline"; }
            else if (tag == "S" || tag == "STRIKE" || tag == "DEL") { if (box.Style.TextDecoration == null) box.Style.TextDecoration = "line-through"; }
            else if (tag == "MARK") 
            { 
                if (box.Style.Background == null) box.Style.Background = new SolidColorBrush(Colors.Yellow); 
                if (box.Style.Foreground == null) box.Style.Foreground = new SolidColorBrush(Colors.Black); 
            }
            else if (tag == "CODE") 
            { 
                if (box.Style.FontFamily == null) box.Style.FontFamily = new FontFamily("Consolas");
                if (box.Style.Background == null) box.Style.Background = new SolidColorBrush(Color.Parse("#EEEEEE"));
                if (IsZero(box.Style.Padding)) box.Style.Padding = new Thickness(2, 0, 2, 0);
                if (box.Style.BorderRadius == default(CornerRadius)) box.Style.BorderRadius = new CornerRadius(3);
            }
            else if (tag == "BLOCKQUOTE")
            {
                if (IsZero(box.Style.Margin)) box.Style.Margin = new Thickness(40, 16, 40, 16);
                if (box.Style.BorderBrush == null) box.Style.BorderBrush = new SolidColorBrush(Colors.Gray);
                if (IsZero(box.Style.BorderThickness)) box.Style.BorderThickness = new Thickness(4, 0, 0, 0);
                if (IsZero(box.Style.Padding)) box.Style.Padding = new Thickness(10, 0, 0, 0);
            }
            else if (tag == "A") { if (box.Style.Foreground == null) box.Style.Foreground = new SolidColorBrush(Colors.Blue); }
            else if (tag == "PRE") { if (box.Style.FontFamily == null) box.Style.FontFamily = new FontFamily("Consolas"); }

            // Special Defaults
            if (tag == "HR")
            {
                if (box.Style.Height == null) box.Style.Height = 1;
                if (box.Style.BorderThickness == null) box.Style.BorderThickness = new Thickness(1);
                if (box.Style.BorderBrush == null) box.Style.BorderBrush = new SolidColorBrush(Colors.Gray);
            }
        }

        private static bool IsZero(Thickness t)
        {
            return t.Left == 0 && t.Top == 0 && t.Right == 0 && t.Bottom == 0;
        }
    }
}

