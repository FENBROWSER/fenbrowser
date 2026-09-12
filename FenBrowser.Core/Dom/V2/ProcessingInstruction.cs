// WHATWG DOM Living Standard compliant implementation
// FenBrowser.Core.Dom.V2 - Production-grade DOM

using System;

namespace FenBrowser.Core.Dom.V2
{
    /// <summary>
    /// DOM Living Standard: ProcessingInstruction interface.
    /// https://dom.spec.whatwg.org/#interface-processinginstruction
    ///
    /// A CharacterData node with a target; only XML documents create them.
    /// </summary>
    public sealed class ProcessingInstruction : CharacterData
    {
        public override NodeType NodeType => NodeType.ProcessingInstruction;
        public override string NodeName => Target;

        /// <summary>The processing instruction's target (DOM dom-processinginstruction-target).</summary>
        public string Target { get; }

        public ProcessingInstruction(string target, string data = "", Document owner = null)
            : base(data, owner)
        {
            Target = target ?? throw new ArgumentNullException(nameof(target));
        }

        public override Node CloneNode(bool deep = false)
        {
            return new ProcessingInstruction(Target, Data, _ownerDocument);
        }

        public override bool IsEqualNode(Node other)
        {
            return other is ProcessingInstruction pi &&
                   string.Equals(Target, pi.Target, StringComparison.Ordinal) &&
                   string.Equals(Data, pi.Data, StringComparison.Ordinal);
        }
    }
}
