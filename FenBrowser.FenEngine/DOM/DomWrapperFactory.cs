using FenBrowser.Core.Dom;
using FenBrowser.FenEngine.Core;
using FenBrowser.FenEngine.Core.Interfaces;

namespace FenBrowser.FenEngine.DOM
{
    public static class DomWrapperFactory
    {
        public static FenValue Wrap(Node node, IExecutionContext context)
        {
            if (node  == null) return FenValue.Null;

            // TODO: Add identity map caching (WeakReference) to ensure same wrapper for same node
            
            if (node is Document doc)
            {
                return FenValue.FromObject(new DocumentWrapper(doc, context));
            }
            else if (node is Element element)
            {
                return FenValue.FromObject(new ElementWrapper(element, context));
            }
            else if (node is Text text)
            {
                return FenValue.FromObject(new TextWrapper(text, context));
            }
            else if (node is Comment comment)
            {
                return FenValue.FromObject(new CommentWrapper(comment, context));
            }
            else if (node is ShadowRoot shadow)
            {
                 return FenValue.FromObject(new ShadowRootWrapper(shadow, context));
            }
            else if (node is DocumentFragment fragment)
            {
                 return FenValue.FromObject(new NodeWrapper(fragment, context));
            }
            // return FenValue.FromObject(new DocumentFragmentWrapper(fragment, context));
            // Placeholder if wrapper absent
            
            // Generic fallback?
            return FenValue.Null;
        }
    }
}
