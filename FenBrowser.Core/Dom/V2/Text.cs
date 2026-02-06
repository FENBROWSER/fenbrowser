// WHATWG DOM Living Standard compliant implementation
// FenBrowser.Core.Dom.V2 - Production-grade DOM

using System;
using System.Text;

namespace FenBrowser.Core.Dom.V2
{
    /// <summary>
    /// DOM Living Standard: Text interface.
    /// https://dom.spec.whatwg.org/#interface-text
    ///
    /// Represents text content in a document.
    /// This node CANNOT have children.
    /// </summary>
    public sealed class Text : CharacterData, ISlottable
    {
        public override NodeType NodeType => NodeType.Text;
        public override string NodeName => "#text";

        /// <summary>
        /// Creates a new Text node with the given data.
        /// </summary>
        public Text(string data = "", Document owner = null)
            : base(data, owner)
        {
            _flags |= NodeFlags.IsText;
        }

        /// <summary>
        /// Returns the combined text of this node and all adjacent text nodes.
        /// https://dom.spec.whatwg.org/#dom-text-wholetext
        /// </summary>
        public string WholeText
        {
            get
            {
                var sb = new StringBuilder();

                // Collect preceding text nodes
                var preceding = new System.Collections.Generic.Stack<Text>();
                for (var node = _previousSibling; node is Text t; node = node._previousSibling)
                    preceding.Push(t);

                while (preceding.Count > 0)
                    sb.Append(preceding.Pop().Data);

                // Add this node's text
                sb.Append(Data);

                // Collect following text nodes
                for (var node = _nextSibling; node is Text t; node = node._nextSibling)
                    sb.Append(t.Data);

                return sb.ToString();
            }
        }

        /// <summary>
        /// Splits this text node at the given offset.
        /// https://dom.spec.whatwg.org/#dom-text-splittext
        /// </summary>
        public Text SplitText(int offset)
        {
            var data = Data;
            if (offset < 0 || offset > data.Length)
                throw new DomException("IndexSizeError", "Offset is out of range");

            // Get the text after the split point
            var newData = data.Substring(offset);

            // Update this node's data to only include text before split
            Data = data.Substring(0, offset);

            // Create a new text node with the remaining text
            var newText = new Text(newData, _ownerDocument);

            // Insert the new text node after this one
            if (_parentNode is ContainerNode parent)
            {
                parent.InsertBefore(newText, _nextSibling);
            }

            return newText;
        }

        // --- ISlottable Implementation ---

        /// <summary>
        /// Returns the slot this node is assigned to (for shadow DOM).
        /// https://dom.spec.whatwg.org/#dom-slottable-assignedslot
        /// </summary>
        public Element AssignedSlot
        {
            get
            {
                // TODO: Implement slot assignment for shadow DOM
                return null;
            }
        }

        // --- Cloning ---

        public override Node CloneNode(bool deep = false)
        {
            return new Text(Data, _ownerDocument);
        }

        // --- Equality ---

        public override bool IsEqualNode(Node other)
        {
            if (other is not Text otherText)
                return false;
            return Data == otherText.Data;
        }

        public override string ToString() => $"Text: \"{TruncateForDisplay(Data, 50)}\"";

        private static string TruncateForDisplay(string s, int maxLength)
        {
            if (s == null) return "";
            if (s.Length <= maxLength) return s;
            return s.Substring(0, maxLength - 3) + "...";
        }
    }

    /// <summary>
    /// ISlottable interface for shadow DOM slot assignment.
    /// Implemented by Element and Text.
    /// </summary>
    public interface ISlottable
    {
        Element AssignedSlot { get; }
    }
}
