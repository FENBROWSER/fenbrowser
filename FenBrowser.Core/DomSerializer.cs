using System;
using System.Collections.Generic;
using System.Text;

namespace FenBrowser.Core
{
    /// <summary>
    /// Utility to serialize LiteElement trees back to HTML strings.
    /// Used for DOM comparison and debugging.
    /// </summary>
    public static class DomSerializer
    {
        private static readonly HashSet<string> VoidElements = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "area", "base", "br", "col", "embed", "hr", "img", "input",
            "link", "meta", "param", "source", "track", "wbr"
        };

        /// <summary>
        /// Serialize a LiteElement tree to an HTML string.
        /// </summary>
        public static string Serialize(LiteElement root, bool prettyPrint = true)
        {
            if (root == null) return "";
            
            var sb = new StringBuilder();
            SerializeNode(root, sb, 0, prettyPrint);
            return sb.ToString();
        }

        private static void SerializeNode(LiteElement node, StringBuilder sb, int depth, bool prettyPrint)
        {
            if (node == null) return;

            string indent = prettyPrint ? new string(' ', depth * 2) : "";
            string newline = prettyPrint ? "\n" : "";

            // Handle special node types
            if (node.NodeType == NodeType.Text)
            {
                var text = node.Text?.Trim();
                if (!string.IsNullOrEmpty(text))
                {
                    sb.Append(indent);
                    sb.Append(EscapeHtml(text));
                    sb.Append(newline);
                }
                return;
            }

            if (node.NodeType == NodeType.Comment)
            {
                sb.Append(indent);
                sb.Append("<!-- ");
                sb.Append(node.Text ?? "");
                sb.Append(" -->");
                sb.Append(newline);
                return;
            }

            if (node.NodeType == NodeType.DocumentType)
            {
                sb.Append("<!DOCTYPE html>");
                sb.Append(newline);
                return;
            }

            // Skip document root tag itself, just process children
            if (node.Tag == "#document" || node.Tag == "#document-fragment")
            {
                foreach (var child in node.Children ?? new List<LiteElement>())
                {
                    SerializeNode(child, sb, depth, prettyPrint);
                }
                return;
            }

            // Regular element
            sb.Append(indent);
            sb.Append("<");
            sb.Append(node.Tag ?? "unknown");

            // Serialize attributes
            if (node.AttrRaw != null && node.AttrRaw.Count > 0)
            {
                foreach (var kvp in node.AttrRaw)
                {
                    sb.Append(" ");
                    sb.Append(kvp.Key);
                    sb.Append("=\"");
                    sb.Append(EscapeHtml(kvp.Value ?? ""));
                    sb.Append("\"");
                }
            }

            // Void elements (self-closing)
            if (VoidElements.Contains(node.Tag ?? ""))
            {
                sb.Append(" />");
                sb.Append(newline);
                return;
            }

            sb.Append(">");
            
            // Check if we have only text children (inline content)
            bool hasElementChildren = false;
            if (node.Children != null)
            {
                foreach (var child in node.Children)
                {
                    if (child.NodeType != NodeType.Text)
                    {
                        hasElementChildren = true;
                        break;
                    }
                }
            }

            if (hasElementChildren)
            {
                sb.Append(newline);
                foreach (var child in node.Children ?? new List<LiteElement>())
                {
                    SerializeNode(child, sb, depth + 1, prettyPrint);
                }
                sb.Append(indent);
            }
            else if (node.Children != null && node.Children.Count > 0)
            {
                // Inline text content
                foreach (var child in node.Children)
                {
                    if (child.NodeType == NodeType.Text && !string.IsNullOrEmpty(child.Text))
                    {
                        sb.Append(EscapeHtml(child.Text.Trim()));
                    }
                }
            }

            sb.Append("</");
            sb.Append(node.Tag ?? "unknown");
            sb.Append(">");
            sb.Append(newline);
        }

        private static string EscapeHtml(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            return text
                .Replace("&", "&amp;")
                .Replace("<", "&lt;")
                .Replace(">", "&gt;")
                .Replace("\"", "&quot;");
        }

        /// <summary>
        /// Get statistics about a DOM tree.
        /// </summary>
        public static DomStats GetStats(LiteElement root)
        {
            var stats = new DomStats();
            CountNodes(root, stats);
            return stats;
        }

        private static void CountNodes(LiteElement node, DomStats stats)
        {
            if (node == null) return;

            switch (node.NodeType)
            {
                case NodeType.Element:
                    stats.ElementCount++;
                    if (node.AttrRaw != null)
                        stats.AttributeCount += node.AttrRaw.Count;
                    break;
                case NodeType.Text:
                    if (!string.IsNullOrWhiteSpace(node.Text))
                        stats.TextNodeCount++;
                    break;
            }

            if (node.Children != null)
            {
                foreach (var child in node.Children)
                {
                    CountNodes(child, stats);
                }
            }
        }
    }

    /// <summary>
    /// Statistics about a DOM tree.
    /// </summary>
    public class DomStats
    {
        public int ElementCount { get; set; }
        public int TextNodeCount { get; set; }
        public int AttributeCount { get; set; }

        public override string ToString()
        {
            return $"Elements: {ElementCount}, Text Nodes: {TextNodeCount}, Attributes: {AttributeCount}";
        }
    }
}
