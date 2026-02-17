using FenBrowser.Core.Dom.V2;
using System;
using System.Collections.Generic;
using System.Text;

namespace FenBrowser.Core
{
    /// <summary>
    /// Utility to serialize Element trees back to HTML strings.
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
        /// Serialize a Element tree to an HTML string.
        /// </summary>
        public static string Serialize(Node root, bool prettyPrint = true)
        {
            if (root == null) return "";
            
            var sb = new StringBuilder();
            SerializeNode(root, sb, 0, prettyPrint);
            return sb.ToString();
        }

        private static void SerializeNode(Node node, StringBuilder sb, int depth, bool prettyPrint)
        {
            if (node == null) return;

            string indent = prettyPrint ? new string(' ', depth * 2) : "";
            string newline = prettyPrint ? "\n" : "";

            // Handle special node types
            if (node.NodeType == NodeType.Text)
            {
                var text = node.NodeValue?.Trim();
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
                sb.Append(node.NodeValue ?? "");
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
            if (node.NodeName == "#document" || node.NodeName == "#document-fragment")
            {
                var docChildren = node.ChildNodes;
                for (int i = 0; i < docChildren.Length; i++)
                {
                    SerializeNode(docChildren[i], sb, depth, prettyPrint);
                }
                return;
            }

            // Regular element
            sb.Append(indent);
            sb.Append("<");
            sb.Append(node.NodeName ?? "unknown");

            // Serialize attributes
            if ((node as Element)?.Attributes != null && (node as Element)?.Attributes.Length > 0)
            {
                foreach (var attr in (node as Element)?.Attributes)
                {
                    sb.Append(" ");
                    sb.Append(attr.Name);
                    sb.Append("=\"");
                    sb.Append(EscapeHtml(attr.Value ?? ""));
                    sb.Append("\"");
                }
            }

            // Void elements (self-closing)
            if (VoidElements.Contains(node.NodeName ?? ""))
            {
                sb.Append(" />");
                sb.Append(newline);
                return;
            }

            sb.Append(">");
            
            // Check if we have only text children (inline content)
            bool hasElementChildren = false;
            // Use ChildNodes property in V2 or extension helper
            var children = node.ChildNodes;
            if (children.Length > 0)
            {
                for (int i=0; i < children.Length; i++)
                {
                    if (children[i].NodeType != NodeType.Text)
                    {
                        hasElementChildren = true;
                        break;
                    }
                }
            }

            if (hasElementChildren)
            {
                sb.Append(newline);
                // V2 uses ChildNodes iterator/indexer
                for (int i = 0; i < children.Length; i++)
                {
                    SerializeNode(children[i], sb, depth + 1, prettyPrint);
                }
                sb.Append(indent);
            }
            else if (children.Length > 0)
            {
                // Inline text content
                for (int i = 0; i < children.Length; i++)
                {
                    var child = children[i];
                    if (child.NodeType == NodeType.Text && !string.IsNullOrEmpty(child.NodeValue))
                    {
                        sb.Append(EscapeHtml(child.NodeValue.Trim()));
                    }
                }
            }

            sb.Append("</");
            sb.Append(node.NodeName ?? "unknown");
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
        public static DomStats GetStats(Element root)
        {
            var stats = new DomStats();
            CountNodes(root, stats);
            return stats;
        }

        private static void CountNodes(Node node, DomStats stats)
        {
            if (node == null) return;

            switch (node.NodeType)
            {
                case NodeType.Element:
                    stats.ElementCount++;
                    if ((node as Element)?.Attributes != null)
                        stats.AttributeCount += ((Element)node).Attributes.Length;
                    break;
                case NodeType.Text:
                    if (!string.IsNullOrWhiteSpace(node.NodeValue))
                        stats.TextNodeCount++;
                    break;
            }

            if (node.HasChildNodes)
            {
                var children = node.ChildNodes;
                for (int i = 0; i < children.Length; i++)
                {
                    CountNodes(children[i], stats);
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

