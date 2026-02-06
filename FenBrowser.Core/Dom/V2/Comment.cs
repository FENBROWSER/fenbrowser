// WHATWG DOM Living Standard compliant implementation
// FenBrowser.Core.Dom.V2 - Production-grade DOM

namespace FenBrowser.Core.Dom.V2
{
    /// <summary>
    /// DOM Living Standard: Comment interface.
    /// https://dom.spec.whatwg.org/#interface-comment
    ///
    /// Represents an HTML/XML comment.
    /// This node CANNOT have children.
    /// </summary>
    public sealed class Comment : CharacterData
    {
        public override NodeType NodeType => NodeType.Comment;
        public override string NodeName => "#comment";

        /// <summary>
        /// Creates a new Comment node with the given data.
        /// </summary>
        public Comment(string data = "", Document owner = null)
            : base(data, owner)
        {
            _flags |= NodeFlags.IsComment;
        }

        // --- Cloning ---

        public override Node CloneNode(bool deep = false)
        {
            return new Comment(Data, _ownerDocument);
        }

        // --- Equality ---

        public override bool IsEqualNode(Node other)
        {
            if (other is not Comment otherComment)
                return false;
            return Data == otherComment.Data;
        }

        public override string ToString() => $"Comment: <!-- {TruncateForDisplay(Data, 40)} -->";

        private static string TruncateForDisplay(string s, int maxLength)
        {
            if (s == null) return "";
            if (s.Length <= maxLength) return s;
            return s.Substring(0, maxLength - 3) + "...";
        }
    }
}
