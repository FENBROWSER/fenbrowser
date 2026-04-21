// =============================================================================
// UsedValueComparator.cs
// Cross-Browser Computed Value Comparison Tool
// 
// PURPOSE: Compare FenBrowser's computed CSS values against Chromium DevTools
// USAGE: Export Chromium computed styles to JSON, then compare with FenBrowser
// OUTPUT: Markdown report highlighting mismatches
// =============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Css;
using FenBrowser.Core.Logging;

namespace FenBrowser.FenEngine.Testing
{
    /// <summary>
    /// Compares FenBrowser's computed CSS values against Chromium's DevTools output.
    /// Essential for identifying layout discrepancies with reference browser.
    /// </summary>
    public static class UsedValueComparator
    {
        public class ComparisonReport
        {
            public int TotalElements { get; set; }
            public int MatchedElements { get; set; }
            public int UnmatchedElements { get; set; }
            public List<ElementMismatch> Mismatches { get; set; } = new List<ElementMismatch>();

            public string ToMarkdown()
            {
                var sb = new StringBuilder();
                sb.AppendLine("# CSS Computed Value Comparison Report");
                sb.AppendLine();
                sb.AppendLine($"**Total Elements**: {TotalElements}");
                sb.AppendLine($"**Matched**: {MatchedElements}");
                sb.AppendLine($"**Unmatched**: {UnmatchedElements}");
                sb.AppendLine($"**Mismatches**: {Mismatches.Count}");
                sb.AppendLine();

                if (Mismatches.Count > 0)
                {
                    sb.AppendLine("## Mismatches");
                    sb.AppendLine();
                    sb.AppendLine("| Element | Property | Chromium | FenBrowser | Difference |");
                    sb.AppendLine("|---------|----------|----------|------------|------------|");

                    foreach (var mismatch in Mismatches)
                    {
                        foreach (var prop in mismatch.PropertyMismatches)
                        {
                            sb.AppendLine($"| {mismatch.Selector} | `{prop.Property}` | `{prop.ChromiumValue}` | `{prop.FenValue}` | {prop.DifferenceDescription} |");
                        }
                    }
                }
                else
                {
                    sb.AppendLine("✅ **All values match!**");
                }

                return sb.ToString();
            }

            public string ToConsole()
            {
                var sb = new StringBuilder();
                sb.AppendLine("╔═══════════════════════════════════════════════════════════════╗");
                sb.AppendLine("║     CSS COMPUTED VALUE COMPARISON REPORT                      ║");
                sb.AppendLine("╚═══════════════════════════════════════════════════════════════╝");
                sb.AppendLine();
                sb.AppendLine($"  Total Elements:    {TotalElements}");
                sb.AppendLine($"  Matched:           {MatchedElements} ✓");
                sb.AppendLine($"  Unmatched:         {UnmatchedElements} ✗");
                sb.AppendLine($"  Property Mismatches: {Mismatches.Sum(m => m.PropertyMismatches.Count)}");
                sb.AppendLine();

                if (Mismatches.Count > 0)
                {
                    sb.AppendLine("MISMATCHES:");
                    foreach (var mismatch in Mismatches.Take(10))  // Show top 10
                    {
                        sb.AppendLine($"\n  {mismatch.Selector}:");
                        foreach (var prop in mismatch.PropertyMismatches.Take(5))
                        {
                            sb.AppendLine($"    • {prop.Property}: Chromium={prop.ChromiumValue} | Fen={prop.FenValue}");
                        }
                    }
                    if (Mismatches.Count > 10)
                        sb.AppendLine($"\n  ... and {Mismatches.Count - 10} more elements with mismatches");
                }
                else
                {
                    sb.AppendLine("✅ ALL VALUES MATCH!");
                }

                return sb.ToString();
            }
        }

        public class ElementMismatch
        {
            public string Selector { get; set; }
            public List<PropertyMismatch> PropertyMismatches { get; set; } = new List<PropertyMismatch>();
        }

        public class PropertyMismatch
        {
            public string Property { get; set; }
            public string ChromiumValue { get; set; }
            public string FenValue { get; set; }
            public string DifferenceDescription { get; set; }
        }

        /// <summary>
        /// Compare FenBrowser against Chromium exported JSON.
        /// JSON format: { "selector": { "property": "value", ... }, ... }
        /// </summary>
        public static ComparisonReport Compare(
            string chromiumJsonPath,
            Element fenElementRoot,
            IReadOnlyDictionary<Node, CssComputed> fenStyles)
        {
            var report = new ComparisonReport();

            try
            {
                // Load Chromium data
                var chromiumJson = File.ReadAllText(chromiumJsonPath);
                var chromiumData = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(chromiumJson);

                if (chromiumData == null)
                {
                    global::FenBrowser.Core.EngineLogCompat.Error("[Comparator] Failed to parse Chromium JSON", LogCategory.Verification);
                    return report;
                }

                report.TotalElements = chromiumData.Count;

                // Compare each element
                foreach (var kvp in chromiumData)
                {
                    string selector = kvp.Key;
                    var chromiumProps = kvp.Value;

                    // Find matching element in FenBrowser
                    var fenElement = FindElementBySelector(fenElementRoot, selector);
                    if (fenElement == null)
                    {
                        report.UnmatchedElements++;
                        continue;
                    }

                    report.MatchedElements++;

                    // Get FenBrowser computed style
                    if (!fenStyles.TryGetValue(fenElement, out var fenStyle))
                        continue;

                    // Compare properties
                    var mismatches = new List<PropertyMismatch>();

                    foreach (var chromiumProp in chromiumProps)
                    {
                        string prop = chromiumProp.Key;
                        string chromiumValue = chromiumProp.Value;
                        string fenValue = GetPropertyValue(fenStyle, prop);

                        if (!ValuesMatch(prop, chromiumValue, fenValue))
                        {
                            mismatches.Add(new PropertyMismatch
                            {
                                Property = prop,
                                ChromiumValue = chromiumValue,
                                FenValue = fenValue,
                                DifferenceDescription = CalculateDifference(prop, chromiumValue, fenValue)
                            });
                        }
                    }

                    if (mismatches.Count > 0)
                    {
                        report.Mismatches.Add(new ElementMismatch
                        {
                            Selector = selector,
                            PropertyMismatches = mismatches
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                global::FenBrowser.Core.EngineLogCompat.Error($"[Comparator] Comparison failed: {ex.Message}", LogCategory.Verification);
            }

            return report;
        }

        // ========================================================================
        // INTERNAL: Matching & Comparison Logic
        // ========================================================================

        private static Element FindElementBySelector(Element root, string selector)
        {
            // Simple selector matching: #id, .class, tag, tag#id, tag.class
            
            if (selector.StartsWith("#"))
            {
                // ID selector
                string id = selector.Substring(1);
                return FindById(root, id);
            }
            else if (selector.StartsWith("."))
            {
                // Class selector (first match)
                string className = selector.Substring(1);
                return FindByClass(root, className);
            }
            else if (selector.Contains("#"))
            {
                // tag#id
                var parts = selector.Split('#');
                string tag = parts[0];
                string id = parts[1];
                var elem = FindById(root, id);
                return elem?.TagName?.Equals(tag, StringComparison.OrdinalIgnoreCase) == true ? elem : null;
            }
            else
            {
                // Tag selector (first match)
                return FindByTagName(root, selector);
            }
        }

        private static Element FindById(Element root, string id)
        {
            if (root.GetAttribute("id") == id)
                return root;

            if (root.Children != null)
            {
                foreach (var child in root.Children)
                {
                    if (child is Element elem)
                    {
                        var found = FindById(elem, id);
                        if (found != null)
                            return found;
                    }
                }
            }

            return null;
        }

        private static Element FindByClass(Element root, string className)
        {
            var classes = root.GetAttribute("class")?.Split(' ');
            if (classes != null && classes.Contains(className))
                return root;

            if (root.Children != null)
            {
                foreach (var child in root.Children)
                {
                    if (child is Element elem)
                    {
                        var found = FindByClass(elem, className);
                        if (found != null)
                            return found;
                    }
                }
            }

            return null;
        }

        private static Element FindByTagName(Element root, string tagName)
        {
            if (root.TagName?.Equals(tagName, StringComparison.OrdinalIgnoreCase) == true)
                return root;

            if (root.Children != null)
            {
                foreach (var child in root.Children)
                {
                    if (child is Element elem)
                    {
                        var found = FindByTagName(elem, tagName);
                        if (found != null)
                            return found;
                    }
                }
            }

            return null;
        }

        private static string GetPropertyValue(CssComputed style, string property)
        {
            // Map property names to CssComputed fields
            return property.ToLowerInvariant() switch
            {
                "display" => style.Display,
                "position" => style.Position,
                "width" => style.Width?.ToString(),
                "height" => style.Height?.ToString(),
                "margin-top" => style.Margin != null ? style.Margin.Top.ToString() : "undefined",
                "margin-right" => style.Margin != null ? style.Margin.Right.ToString() : "undefined",
                "margin-bottom" => style.Margin != null ? style.Margin.Bottom.ToString() : "undefined",
                "margin-left" => style.Margin != null ? style.Margin.Left.ToString() : "undefined",
                "padding-top" => style.Padding != null ? style.Padding.Top.ToString() : "undefined",
                "padding-right" => style.Padding != null ? style.Padding.Right.ToString() : "undefined",
                "padding-bottom" => style.Padding != null ? style.Padding.Bottom.ToString() : "undefined",
                "padding-left" => style.Padding != null ? style.Padding.Left.ToString() : "undefined",
                "line-height" => style.LineHeight?.ToString(),
                "font-size" => style.FontSize?.ToString(),
                "bottom" => style.Bottom?.ToString(),
                "top" => style.Top?.ToString(),
                "left" => style.Left?.ToString(),
                "right" => style.Right?.ToString(),
                "float" => style.Float,
                _ => "undefined"
            };
        }

        private static bool ValuesMatch(string property, string chromiumValue, string fenValue)
        {
            if (chromiumValue == fenValue)
                return true;

            // Normalize and compare
            string normChromium = NormalizeValue(chromiumValue);
            string normFen = NormalizeValue(fenValue);

            if (normChromium == normFen)
                return true;

            // Numeric comparison with tolerance
            if (TryParseNumeric(normChromium, out float chromiumNum) &&
                TryParseNumeric(normFen, out float fenNum))
            {
                float tolerance = Math.Max(1.0f, chromiumNum * 0.01f);  // 1px or 1%
                return Math.Abs(chromiumNum - fenNum) <= tolerance;
            }

            return false;
        }

        private static string NormalizeValue(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return "undefined";

            return value.Trim().ToLowerInvariant();
        }

        private static bool TryParseNumeric(string value, out float result)
        {
            result = 0;

            // Remove units (px, em, %, etc)
            value = System.Text.RegularExpressions.Regex.Replace(value, @"[a-z%]+$", "");

            return float.TryParse(value, out result);
        }

        private static string CalculateDifference(string property, string chromiumValue, string fenValue)
        {
            if (TryParseNumeric(chromiumValue, out float chromiumNum) &&
                TryParseNumeric(fenValue, out float fenNum))
            {
                float diff = fenNum - chromiumNum;
                float pctDiff = chromiumNum != 0 ? (diff / chromiumNum) * 100 : 0;
                return $"{diff:+0.0;-0.0}px ({pctDiff:+0.0;-0.0}%)";
            }

            return "type mismatch";
        }
    }
}

