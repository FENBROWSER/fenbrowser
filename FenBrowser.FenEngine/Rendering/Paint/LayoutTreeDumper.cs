// =============================================================================
// LayoutTreeDumper.cs
// Layout Tree Debugging & Diagnostic Tool
// 
// PURPOSE: Serialize layout tree with computed positions for debugging
// USAGE: LayoutTreeDumper.DumpTree(rootElement, boxes, styles)
// OUTPUT: JSON or indented text showing element hierarchy with box metrics
// =============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Css;
using FenBrowser.Core.Logging;
using FenBrowser.FenEngine.Layout;
using SkiaSharp;

namespace FenBrowser.FenEngine.Rendering.Paint
{
    /// <summary>
    /// Dumps the layout tree with computed box model positions for debugging.
    /// Essential tool for diagnosing layout issues by comparing against Chromium.
    /// </summary>
    public static class LayoutTreeDumper
    {
        public enum OutputFormat
        {
            Json,
            IndentedText
        }

        /// <summary>
        /// Serialize layout tree to string format.
        /// </summary>
        public static string DumpTree(
            Element root,
            IReadOnlyDictionary<Node, BoxModel> boxes,
            IReadOnlyDictionary<Node, CssComputed> styles,
            OutputFormat format = OutputFormat.IndentedText)
        {
            if (root == null) return "NULL ROOT";

            var tree = BuildTree(root, boxes, styles);

            return format == OutputFormat.Json
                ? JsonSerializer.Serialize(tree, new JsonSerializerOptions { WriteIndented = true })
                : FormatAsText(tree, 0);
        }

        /// <summary>
        /// Dump layout tree to file.
        /// </summary>
        public static void DumpToFile(
            string path,
            Element root,
            IReadOnlyDictionary<Node, BoxModel> boxes,
            IReadOnlyDictionary<Node, CssComputed> styles,
            OutputFormat format = OutputFormat.IndentedText)
        {
            try
            {
                var content = DumpTree(root, boxes, styles, format);
                File.WriteAllText(path, content);
                global::FenBrowser.Core.FenLogger.Log($"[LayoutDump] Written to: {path}", LogCategory.Rendering);
            }
            catch (Exception ex)
            {
                global::FenBrowser.Core.FenLogger.Error($"[LayoutDump] Failed to write: {ex.Message}", LogCategory.Rendering);
            }
        }

        /// <summary>
        /// Dump layout tree for a specific CSS selector subset.
        /// </summary>
        public static string DumpFiltered(
            Element root,
            IReadOnlyDictionary<Node, BoxModel> boxes,
            IReadOnlyDictionary<Node, CssComputed> styles,
            Func<Element, bool> filter,
            OutputFormat format = OutputFormat.IndentedText)
        {
            if (root == null) return "NULL ROOT";

            var tree = BuildTreeFiltered(root, boxes, styles, filter);
            
            return format == OutputFormat.Json
                ? JsonSerializer.Serialize(tree, new JsonSerializerOptions { WriteIndented = true })
                : FormatAsText(tree, 0);
        }

        // ========================================================================
        // INTERNAL: Tree Building
        // ========================================================================

        private class LayoutNode
        {
            public string TagName { get; set; }
            public string Id { get; set; }
            public string Classes { get; set; }
            
            // Box Model
            public float X { get; set; }
            public float Y { get; set; }
            public float Width { get; set; }
            public float Height { get; set; }
            
            // Detailed Box Metrics
            public float ContentWidth { get; set; }
            public float ContentHeight { get; set; }
            public string Padding { get; set; }  // "T R B L"
            public string Border { get; set; }   // "T R B L"
            public string Margin { get; set; }   // "T R B L"
            
            // Computed CSS Properties (Key ones for debugging)
            public string Display { get; set; }
            public string Position { get; set; }
            public string Float { get; set; }
            public string FlexDirection { get; set; }
            public string JustifyContent { get; set; }
            public string AlignItems { get; set; }
            public string FontSize { get; set; }
            public string LineHeight { get; set; }
            
            // Text Content (if text node)
            public string TextContent { get; set; }
            
            // Children
            public List<LayoutNode> Children { get; set; } = new List<LayoutNode>();
        }

        private static LayoutNode BuildTree(
            Node node,
            IReadOnlyDictionary<Node, BoxModel> boxes,
            IReadOnlyDictionary<Node, CssComputed> styles)
        {
            var layoutNode = new LayoutNode();

            // Element Info
            if (node is Element elem)
            {
                layoutNode.TagName = elem.TagName ?? "ELEMENT";
                layoutNode.Id = elem.GetAttribute("id");
                layoutNode.Classes = elem.GetAttribute("class");
            }
            else if (node is Text textNode)
            {
                layoutNode.TagName = "#text";
                var content = textNode.Data ?? "";
                layoutNode.TextContent = content.Length > 50 
                    ? content.Substring(0, 50) + "..." 
                    : content;
            }
            else
            {
                layoutNode.TagName = node.NodeName ?? "NODE";
            }

            // Box Model Data
            if (boxes.TryGetValue(node, out var box))
            {
                layoutNode.X = box.ContentBox.Left;
                layoutNode.Y = box.ContentBox.Top;
                layoutNode.Width = box.ContentBox.Width;
                layoutNode.Height = box.ContentBox.Height;
                
                layoutNode.ContentWidth = box.ContentBox.Width;
                layoutNode.ContentHeight = box.ContentBox.Height;
                
                var pb = box.PaddingBox;
                var cb = box.ContentBox;
                var bb = box.BorderBox;
                var mb = box.MarginBox;
                
                // Calculate padding (PaddingBox - ContentBox)
                float pT = cb.Top - pb.Top;
                float pR = pb.Right - cb.Right;
                float pB = pb.Bottom - cb.Bottom;
                float pL = cb.Left - pb.Left;
                layoutNode.Padding = $"{pT:F1} {pR:F1} {pB:F1} {pL:F1}";
                
                // Calculate border (BorderBox - PaddingBox)
                float bT = pb.Top - bb.Top;
                float bR = bb.Right - pb.Right;
                float bB = bb.Bottom - pb.Bottom;
                float bL = pb.Left - bb.Left;
                layoutNode.Border = $"{bT:F1} {bR:F1} {bB:F1} {bL:F1}";
                
                // Calculate margin (MarginBox - BorderBox)
                float mT = bb.Top - mb.Top;
                float mR = mb.Right - bb.Right;
                float mB = mb.Bottom - bb.Bottom;
                float mL = bb.Left - mb.Left;
                layoutNode.Margin = $"{mT:F1} {mR:F1} {mB:F1} {mL:F1}";
            }
            else
            {
                // No box computed
                layoutNode.X = 0;
                layoutNode.Y = 0;
                layoutNode.Width = 0;
                layoutNode.Height = 0;
            }

            // CSS Computed Properties
            if (styles.TryGetValue(node, out var style))
            {
                layoutNode.Display = style.Display;
                layoutNode.Position = style.Position;
                layoutNode.Float = style.Float;
                layoutNode.FlexDirection = style.FlexDirection;
                layoutNode.JustifyContent = style.JustifyContent;
                layoutNode.AlignItems = style.AlignItems;
                layoutNode.FontSize = style.FontSize?.ToString();
                layoutNode.LineHeight = style.LineHeight?.ToString();
            }

            // Recurse Children
            if (node.Children != null)
            {
                foreach (var child in node.Children)
                {
                    var childNode = BuildTree(child, boxes, styles);
                    layoutNode.Children.Add(childNode);
                }
            }

            return layoutNode;
        }

        private static LayoutNode BuildTreeFiltered(
            Node node,
            IReadOnlyDictionary<Node, BoxModel> boxes,
            IReadOnlyDictionary<Node, CssComputed> styles,
            Func<Element, bool> filter)
        {
            // Apply filter
            if (node is Element elem && !filter(elem))
                return null;

            var layoutNode = BuildTree(node, boxes, styles);

            // Filter children
            var filteredChildren = new List<LayoutNode>();
            foreach (var child in layoutNode.Children)
            {
                if (child != null)
                    filteredChildren.Add(child);
            }
            layoutNode.Children = filteredChildren;

            return layoutNode;
        }

        // ========================================================================
        // INTERNAL: Text Formatting
        // ========================================================================

        private static string FormatAsText(LayoutNode node, int depth)
        {
            var sb = new StringBuilder();
            var indent = new string(' ', depth * 2);

            // Node Header
            sb.Append(indent);
            sb.Append($"{node.TagName}");
            
            if (!string.IsNullOrEmpty(node.Id))
                sb.Append($" #{node.Id}");
            
            if (!string.IsNullOrEmpty(node.Classes))
                sb.Append($" .{node.Classes.Replace(" ", ".")}");
            
            sb.AppendLine();

            // Box Model
            sb.AppendLine($"{indent}  Box: ({node.X:F1}, {node.Y:F1}) {node.Width:F1}×{node.Height:F1}");
            
            if (!string.IsNullOrEmpty(node.Padding) && node.Padding != "0.0 0.0 0.0 0.0")
                sb.AppendLine($"{indent}  Padding: {node.Padding}");
            
            if (!string.IsNullOrEmpty(node.Border) && node.Border != "0.0 0.0 0.0 0.0")
                sb.AppendLine($"{indent}  Border: {node.Border}");
            
            if (!string.IsNullOrEmpty(node.Margin) && node.Margin != "0.0 0.0 0.0 0.0")
                sb.AppendLine($"{indent}  Margin: {node.Margin}");

            // CSS Properties (non-defaults only)
            if (!string.IsNullOrEmpty(node.Display) && node.Display != "block")
                sb.AppendLine($"{indent}  display: {node.Display}");
            
            if (!string.IsNullOrEmpty(node.Position) && node.Position != "static")
                sb.AppendLine($"{indent}  position: {node.Position}");
            
            if (!string.IsNullOrEmpty(node.Float) && node.Float != "none")
                sb.AppendLine($"{indent}  float: {node.Float}");
            
            if (!string.IsNullOrEmpty(node.FlexDirection))
                sb.AppendLine($"{indent}  flex-direction: {node.FlexDirection}");
            
            if (!string.IsNullOrEmpty(node.JustifyContent))
                sb.AppendLine($"{indent}  justify-content: {node.JustifyContent}");
            
            if (!string.IsNullOrEmpty(node.FontSize))
                sb.AppendLine($"{indent}  font-size: {node.FontSize}");

            // Text Content
            if (!string.IsNullOrEmpty(node.TextContent))
                sb.AppendLine($"{indent}  Text: \"{node.TextContent}\"");

            // Children
            foreach (var child in node.Children)
            {
                sb.Append(FormatAsText(child, depth + 1));
            }

            return sb.ToString();
        }
    }
}

