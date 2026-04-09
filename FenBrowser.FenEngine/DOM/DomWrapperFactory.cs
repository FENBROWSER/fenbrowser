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
            {
                ApplyRuntimePrototype(cached, node, context);
                return FenValue.FromObject(cached);
            }

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
            else if (node is DocumentType documentType)
                wrapper = new DocumentTypeWrapper(documentType, context);
            else if (node is DocumentFragment fragment)
                wrapper = new NodeWrapper(fragment, context);
            else
                return FenValue.Null;

            ApplyRuntimePrototype(wrapper, node, context);
            _wrapperCache.Add(node, wrapper);
            return FenValue.FromObject(wrapper);
        }

        /// <summary>Clear the identity cache on page navigation so stale wrappers are not reused.</summary>
        public static void ClearCache() => _wrapperCache.Clear();

        private static void ApplyRuntimePrototype(IObject wrapper, Node node, IExecutionContext context)
        {
            if (wrapper == null || context?.Environment == null)
            {
                return;
            }

            var prototype = ResolvePrototype(node, context);
            if (prototype == null)
            {
                return;
            }

            if (!ReferenceEquals(wrapper.GetPrototype(), prototype))
            {
                wrapper.SetPrototype(prototype);
            }
        }

        private static IObject ResolvePrototype(Node node, IExecutionContext context)
        {
            if (node is Document)
            {
                return GetConstructorPrototype(context, "Document") ?? GetConstructorPrototype(context, "Node");
            }

            if (node is Text)
            {
                return GetConstructorPrototype(context, "Text") ?? GetConstructorPrototype(context, "Node");
            }

            if (node is DocumentType)
            {
                return GetConstructorPrototype(context, "DocumentType") ?? GetConstructorPrototype(context, "Node");
            }

            if (node is Comment)
            {
                return GetConstructorPrototype(context, "Comment") ?? GetConstructorPrototype(context, "Node");
            }

            if (node is Element element)
            {
                if (string.Equals(element.TagName, "img", System.StringComparison.OrdinalIgnoreCase))
                {
                    return GetConstructorPrototype(context, "HTMLImageElement")
                        ?? GetConstructorPrototype(context, "HTMLElement")
                        ?? GetConstructorPrototype(context, "Element")
                        ?? GetConstructorPrototype(context, "Node");
                }

                return GetConstructorPrototype(context, "HTMLElement")
                    ?? GetConstructorPrototype(context, "Element")
                    ?? GetConstructorPrototype(context, "Node");
            }

            return GetConstructorPrototype(context, "Node");
        }

        private static IObject GetConstructorPrototype(IExecutionContext context, string constructorName)
        {
            if (context?.Environment == null || string.IsNullOrWhiteSpace(constructorName))
            {
                return null;
            }

            var constructor = context.Environment.Get(constructorName);
            if (!constructor.IsFunction)
            {
                return null;
            }

            var prototype = constructor.AsFunction()?.Get("prototype", context) ?? FenValue.Undefined;
            return prototype.IsObject ? prototype.AsObject() : null;
        }
    }
}
