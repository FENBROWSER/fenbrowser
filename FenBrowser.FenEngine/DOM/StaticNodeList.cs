using System.Collections.Generic;
using FenBrowser.Core.Dom.V2;

namespace FenBrowser.FenEngine.DOM
{
    /// <summary>
    /// Static NodeList implementation for FenEngine usage.
    /// Acts as a snapshot of nodes.
    /// </summary>
    public class StaticNodeList : NodeList
    {
        private readonly List<Node> _nodes;

        public StaticNodeList(IEnumerable<Node> nodes)
        {
            _nodes = new List<Node>(nodes);
        }

        public override int Length => _nodes.Count;

        public override Node this[int index] => (index >= 0 && index < _nodes.Count) ? _nodes[index] : null;

        public override IEnumerator<Node> GetEnumerator() => _nodes.GetEnumerator();
    }
}
