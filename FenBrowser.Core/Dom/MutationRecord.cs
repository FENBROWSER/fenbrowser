using System.Collections.Generic;

namespace FenBrowser.Core.Dom
{
    public class MutationRecord
    {
        public string Type { get; set; } // "attributes", "characterData", "childList"
        public Node Target { get; set; }
        public List<Node> AddedNodes { get; set; } = new List<Node>();
        public List<Node> RemovedNodes { get; set; } = new List<Node>();
        public Node PreviousSibling { get; set; }
        public Node NextSibling { get; set; }
        public string AttributeName { get; set; }
        public string AttributeNamespace { get; set; }
        public string OldValue { get; set; }
    }
}
