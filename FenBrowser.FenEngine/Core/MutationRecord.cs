using FenBrowser.Core.Dom;
using System.Collections.Generic;
using FenBrowser.Core;

namespace FenBrowser.FenEngine.Core
{
    public class MutationRecord
    {
        public string Type { get; set; }
        public Element Target { get; set; }
        public List<Element> AddedNodes { get; set; }
        public List<Element> RemovedNodes { get; set; }
        public Element PreviousSibling { get; set; }
        public Element NextSibling { get; set; }
        public string AttributeName { get; set; }
        public string AttributeNamespace { get; set; }
        public string OldValue { get; set; }

        // Helper for legacy support if needed
        public Dictionary<string, string> Attrs { get; set; }
    }
}

