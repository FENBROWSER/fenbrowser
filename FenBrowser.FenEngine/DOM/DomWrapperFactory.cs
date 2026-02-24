using System.Runtime.CompilerServices;
using FenBrowser.Core.Dom.V2;
using FenBrowser.FenEngine.Core;
using FenBrowser.FenEngine.Core.Interfaces;

namespace FenBrowser.FenEngine.DOM
{
    public static class DomWrapperFactory
    {
        // Identity cache: same Node → same IObject wrapper (satisfies Web IDL "same object" requirement).
        // ConditionalWeakTable keeps weak references to keys so entries are automatically removed
        // when the Node is garbage-collected.
        private static readonly ConditionalWeakTable<Node, IObject> _wrapperCache
            = new ConditionalWeakTable<Node, IObject>();

        public static FenValue Wrap(Node node, IExecutionContext context)
        {
            if (node == null) return FenValue.Null;

            if (_wrapperCache.TryGetValue(node, out var cached))
                return FenValue.FromObject(cached);

            IObject wrapper;
            if (node is Document doc)
                wrapper = new DocumentWrapper(doc, context);
            else if (node is Element element)
                wrapper = new ElementWrapper(element, context);
            else if (node is Text text)
                wrapper = new TextWrapper(text, context);
            else if (node is Comment comment)
                wrapper = new CommentWrapper(comment, context);
            else if (node is ShadowRoot shadow)
                wrapper = new ShadowRootWrapper(shadow, context);
            else if (node is DocumentFragment fragment)
                wrapper = new NodeWrapper(fragment, context);
            else
                return FenValue.Null;

            _wrapperCache.Add(node, wrapper);
            return FenValue.FromObject(wrapper);
        }

        /// <summary>Clear the identity cache on page navigation so stale wrappers are not reused.</summary>
        public static void ClearCache() => _wrapperCache.Clear();
    }
}
