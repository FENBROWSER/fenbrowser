using System;

namespace FenBrowser.Core.Dom
{
    public class ShadowRoot : DocumentFragment
    {
        public Element Host { get; }
        public string Mode { get; }

        public ShadowRoot(Element host, string mode)
        {
            Host = host ?? throw new ArgumentNullException(nameof(host));
            if (mode != "open" && mode != "closed")
                throw new ArgumentException("ShadowRoot mode must be 'open' or 'closed'", nameof(mode));
            Mode = mode;
        }

        public override NodeType NodeType => NodeType.DocumentFragment; // ShadowRoot is a document fragment
        public override string NodeName => "#shadow-root";
    }
}
