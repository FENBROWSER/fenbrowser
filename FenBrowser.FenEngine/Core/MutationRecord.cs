using System.Collections.Generic;
using FenBrowser.Core;

namespace FenBrowser.FenEngine.Core
{
    public class MutationRecord
    {
        public string Type { get; set; }
        public LiteElement Target { get; set; }
        public List<LiteElement> AddedNodes { get; set; }
        public List<LiteElement> RemovedNodes { get; set; }
        public LiteElement PreviousSibling { get; set; }
        public LiteElement NextSibling { get; set; }
        public string AttributeName { get; set; }
        public string AttributeNamespace { get; set; }
        public string OldValue { get; set; }

        // Helper for legacy support if needed
        public Dictionary<string, string> Attrs { get; set; }
    }
}
