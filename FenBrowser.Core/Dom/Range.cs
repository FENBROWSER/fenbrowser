using System;
using System.Collections.Generic;
using FenBrowser.Core.Dom;

namespace FenBrowser.Core.Dom
{
    /// <summary>
    /// DOM Level 2 Range implementation.
    /// Represents a fragment of a document that can contain nodes and parts of text nodes.
    /// </summary>
    public class Range
    {
        public Node StartContainer { get; private set; }
        public int StartOffset { get; private set; }
        public Node EndContainer { get; private set; }
        public int EndOffset { get; private set; }
        public bool Collapsed => StartContainer == EndContainer && StartOffset == EndOffset;
        
        public Node CommonAncestorContainer
        {
            get
            {
                if (StartContainer == null || EndContainer == null) return null;
                if (StartContainer == EndContainer) return StartContainer;

                // Find common ancestor
                var startAncestors = new HashSet<Node>(StartContainer.Ancestors());
                startAncestors.Add(StartContainer);

                var curr = EndContainer;
                while (curr != null)
                {
                    if (startAncestors.Contains(curr)) return curr;
                    curr = curr.Parent;
                }
                return null;
            }
        }

        public Range(Document doc)
        {
            if (doc == null) throw new ArgumentNullException(nameof(doc));
            StartContainer = doc;
            StartOffset = 0;
            EndContainer = doc;
            EndOffset = 0;
        }

        public void SetStart(Node node, int offset)
        {
            if (node == null) throw new InvalidOperationException("Node cannot be null");
            StartContainer = node;
            StartOffset = offset;
            
            // If start is after end, collapse to start
            // Simplified check (assumes same document order for now)
            if (StartContainer == EndContainer && StartOffset > EndOffset)
            {
                Collapse(true);
            }
        }

        public void SetEnd(Node node, int offset)
        {
            if (node == null) throw new InvalidOperationException("Node cannot be null");
            EndContainer = node;
            EndOffset = offset;
            
            // If end is before start, collapse to end (simplified)
            // Ideally we check document order
        }

        public void SetStartBefore(Node node)
        {
            if (node.Parent == null) throw new InvalidOperationException("Node has no parent");
            var index = node.Parent.Children.IndexOf(node);
            SetStart(node.Parent, index);
        }

        public void SetStartAfter(Node node)
        {
            if (node.Parent == null) throw new InvalidOperationException("Node has no parent");
            var index = node.Parent.Children.IndexOf(node);
            SetStart(node.Parent, index + 1);
        }

        public void SetEndBefore(Node node)
        {
            if (node.Parent == null) throw new InvalidOperationException("Node has no parent");
            var index = node.Parent.Children.IndexOf(node);
            SetEnd(node.Parent, index);
        }

        public void SetEndAfter(Node node)
        {
            if (node.Parent == null) throw new InvalidOperationException("Node has no parent");
            var index = node.Parent.Children.IndexOf(node);
            SetEnd(node.Parent, index + 1);
        }

        public void Collapse(bool toStart)
        {
            if (toStart)
            {
                EndContainer = StartContainer;
                EndOffset = StartOffset;
            }
            else
            {
                StartContainer = EndContainer;
                StartOffset = EndOffset;
            }
        }

        public void SelectNode(Node node)
        {
            if (node.Parent == null) throw new InvalidOperationException("Node has no parent");
            var index = node.Parent.Children.IndexOf(node);
            SetStart(node.Parent, index);
            SetEnd(node.Parent, index + 1);
        }

        public void SelectNodeContents(Node node)
        {
            StartContainer = node;
            StartOffset = 0;
            EndContainer = node;
            EndOffset = node.Children.Count; // Or text length
            if (node.NodeType == NodeType.Text) EndOffset = node.NodeValue?.Length ?? 0;
        }

        public void DeleteContents()
        {
            // Placeholder: minimal implementation
            if (Collapsed) return;
            // TODO: properly remove nodes between start and end
        }

        public DocumentFragment CloneContents()
        {
            // Placeholder
            return new DocumentFragment(); // Return empty for now
        }

        public DocumentFragment ExtractContents()
        {
             // Placeholder
            return new DocumentFragment();
        }

        public void InsertNode(Node node)
        {
            // Placeholder: Insert at Start
            if (StartContainer is Element || StartContainer is DocumentFragment)
            {
                 // Insert at offset
                 // Requires modifying Children list directly or exposing Insert methods on Node
            }
        }

        public void SurroundContents(Node newParent)
        {
            // Placeholder
        }

        public Range CloneRange()
        {
            var r = new Range(StartContainer.OwnerDocument ?? (StartContainer as Document));
            r.StartContainer = StartContainer;
            r.StartOffset = StartOffset;
            r.EndContainer = EndContainer;
            r.EndOffset = EndOffset;
            return r;
        }

        public void Detach()
        {
            // No-op in modern DOM
        }
        
        public override string ToString()
        {
            // TODO: Return text content within range
            return "";
        }
    }
}
