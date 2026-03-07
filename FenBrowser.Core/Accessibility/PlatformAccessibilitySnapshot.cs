using System;
using System.Collections.Generic;

namespace FenBrowser.Core.Accessibility
{
    public enum AccessibilityTargetPlatform
    {
        WindowsUia,
        LinuxAtSpi,
        MacOsNsAccessibility
    }

    public sealed class PlatformAccessibilitySnapshot
    {
        public AccessibilityTargetPlatform Platform { get; init; }
        public IReadOnlyList<PlatformAccessibilityNode> Nodes { get; init; } = Array.Empty<PlatformAccessibilityNode>();
        public bool IsValid { get; init; }
        public IReadOnlyList<string> ValidationErrors { get; init; } = Array.Empty<string>();
    }

    public sealed class PlatformAccessibilityNode
    {
        public int Id { get; init; }
        public int ParentId { get; init; }
        public string PlatformRole { get; init; }
        public string Name { get; init; }
        public string Description { get; init; }
        public bool IsHidden { get; init; }
        public IReadOnlyDictionary<string, string> Attributes { get; init; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    public static class PlatformAccessibilitySnapshotBuilder
    {
        public static PlatformAccessibilitySnapshot Build(AccessibilityTree tree, AccessibilityTargetPlatform platform)
        {
            if (tree == null) throw new ArgumentNullException(nameof(tree));

            var nodes = new List<PlatformAccessibilityNode>();
            var errors = new List<string>();
            if (tree.Root != null)
            {
                BuildNode(tree.Root, 0, platform, nodes);
            }

            Validate(nodes, errors);

            return new PlatformAccessibilitySnapshot
            {
                Platform = platform,
                Nodes = nodes,
                IsValid = errors.Count == 0,
                ValidationErrors = errors
            };
        }

        private static int BuildNode(
            AccessibilityNode node,
            int parentId,
            AccessibilityTargetPlatform platform,
            List<PlatformAccessibilityNode> nodes)
        {
            var id = GetNodeId(node);
            var attributes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kvp in node.States)
            {
                attributes[kvp.Key] = kvp.Value ?? string.Empty;
            }

            if (!string.IsNullOrWhiteSpace(node.Description))
            {
                attributes[platform switch
                {
                    AccessibilityTargetPlatform.WindowsUia => "FullDescription",
                    AccessibilityTargetPlatform.LinuxAtSpi => "description",
                    _ => "AXDescription"
                }] = node.Description;
            }

            nodes.Add(new PlatformAccessibilityNode
            {
                Id = id,
                ParentId = parentId,
                PlatformRole = MapRole(node.Role, platform),
                Name = node.Name ?? string.Empty,
                Description = node.Description ?? string.Empty,
                IsHidden = node.IsHidden,
                Attributes = attributes
            });

            foreach (var child in node.Children)
            {
                BuildNode(child, id, platform, nodes);
            }

            return id;
        }

        private static void Validate(List<PlatformAccessibilityNode> nodes, List<string> errors)
        {
            if (nodes.Count == 0)
            {
                errors.Add("snapshot-empty");
                return;
            }

            var ids = new HashSet<int>();
            foreach (var node in nodes)
            {
                if (!ids.Add(node.Id))
                {
                    errors.Add($"duplicate-node-id:{node.Id}");
                }

                if (string.IsNullOrWhiteSpace(node.PlatformRole))
                {
                    errors.Add($"missing-role:{node.Id}");
                }
            }

            if (nodes[0].ParentId != 0)
            {
                errors.Add("root-parent-id-must-be-zero");
            }
        }

        private static string MapRole(AriaRole role, AccessibilityTargetPlatform platform)
        {
            return platform switch
            {
                AccessibilityTargetPlatform.WindowsUia => role switch
                {
                    AriaRole.Button => "Button",
                    AriaRole.Link => "Hyperlink",
                    AriaRole.Checkbox => "CheckBox",
                    AriaRole.Textbox => "Edit",
                    AriaRole.Img => "Image",
                    AriaRole.List => "List",
                    AriaRole.Listitem => "ListItem",
                    AriaRole.Dialog => "Window",
                    AriaRole.Document => "Document",
                    _ => "Custom"
                },
                AccessibilityTargetPlatform.LinuxAtSpi => role.ToString().ToLowerInvariant(),
                _ => role switch
                {
                    AriaRole.Button => "AXButton",
                    AriaRole.Link => "AXLink",
                    AriaRole.Checkbox => "AXCheckBox",
                    AriaRole.Textbox => "AXTextField",
                    AriaRole.Img => "AXImage",
                    AriaRole.List => "AXList",
                    AriaRole.Listitem => "AXRow",
                    AriaRole.Dialog => "AXWindow",
                    AriaRole.Document => "AXDocument",
                    _ => "AXGroup"
                }
            };
        }

        private static int GetNodeId(AccessibilityNode node)
        {
            unchecked
            {
                var sourceHash = node.SourceElement != null
                    ? System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(node.SourceElement)
                    : 0;
                return sourceHash != 0
                    ? sourceHash
                    : HashCode.Combine((int)node.Role, node.Name ?? string.Empty, node.Description ?? string.Empty);
            }
        }
    }
}
