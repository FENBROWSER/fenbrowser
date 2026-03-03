// FenBrowser.Core.Accessibility — immutable accessibility tree node

using System;
using System.Collections.Generic;
using FenBrowser.Core.Dom.V2;

namespace FenBrowser.Core.Accessibility
{
    /// <summary>
    /// An immutable snapshot of the computed accessibility properties for one DOM element.
    /// Built by <see cref="AccessibilityTreeBuilder"/> and cached by <see cref="AccessibilityTree"/>.
    /// </summary>
    public sealed class AccessibilityNode
    {
        /// <summary>The DOM element this node represents.</summary>
        public Element SourceElement { get; }

        /// <summary>The computed ARIA role.</summary>
        public AriaRole Role { get; }

        /// <summary>The computed accessible name (AccName 1.1).</summary>
        public string Name { get; }

        /// <summary>The computed accessible description (AccName 1.1).</summary>
        public string Description { get; }

        /// <summary>
        /// True when the element or an ancestor has aria-hidden="true", or CSS display/visibility hide it.
        /// </summary>
        public bool IsHidden { get; }

        /// <summary>Child nodes in the accessibility tree (already excludes hidden/presentation nodes).</summary>
        public IReadOnlyList<AccessibilityNode> Children { get; }

        /// <summary>
        /// Collected ARIA state/property values present on the element (e.g. "aria-checked" → "true").
        /// Only includes properties actually set on the source element.
        /// </summary>
        public IReadOnlyDictionary<string, string> States { get; }

        internal AccessibilityNode(
            Element sourceElement,
            AriaRole role,
            string name,
            string description,
            bool isHidden,
            IReadOnlyList<AccessibilityNode> children,
            IReadOnlyDictionary<string, string> states)
        {
            SourceElement = sourceElement;
            Role = role;
            Name = name ?? "";
            Description = description ?? "";
            IsHidden = isHidden;
            Children = children ?? Array.Empty<AccessibilityNode>();
            States = states ?? EmptyStates;
        }

        private static readonly IReadOnlyDictionary<string, string> EmptyStates =
            new Dictionary<string, string>();

        public override string ToString() =>
            $"[{Role}] \"{Name}\"{(IsHidden ? " (hidden)" : "")}";
    }
}
