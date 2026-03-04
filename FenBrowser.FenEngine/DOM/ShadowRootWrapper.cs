using FenBrowser.Core.Dom.V2;
using FenBrowser.FenEngine.Core;
using FenBrowser.FenEngine.Core.Interfaces;
using System.Linq;

namespace FenBrowser.FenEngine.DOM
{
    // Inherit from NodeWrapper since DocumentFragmentWrapper might not exist
    // or we can implement it here.
    public class ShadowRootWrapper : NodeWrapper
    {
        private readonly ShadowRoot _shadowRoot;

        public ShadowRootWrapper(ShadowRoot shadowRoot, IExecutionContext context)
            : base(shadowRoot, context)
        {
            _shadowRoot = shadowRoot;
        }

        public override FenValue Get(string key, IExecutionContext context = null)
        {
            var normalized = (key ?? string.Empty).ToLowerInvariant();
            switch (normalized)
            {
                case "mode":
                    return FenValue.FromString(_shadowRoot.Mode.ToString().ToLowerInvariant());

                case "host":
                    return DomWrapperFactory.Wrap(_shadowRoot.Host, _context);

                case "firstelementchild":
                    return DomWrapperFactory.Wrap(_shadowRoot.ChildNodes.OfType<Element>().FirstOrDefault(), _context);

                case "lastelementchild":
                    return DomWrapperFactory.Wrap(_shadowRoot.ChildNodes.OfType<Element>().LastOrDefault(), _context);

                case "children":
                    var children = new FenObject();
                    int ci = 0;
                    foreach (var child in _shadowRoot.ChildNodes.OfType<Element>())
                    {
                        children.Set(ci.ToString(), DomWrapperFactory.Wrap(child, _context));
                        ci++;
                    }
                    children.Set("length", FenValue.FromNumber(ci));
                    return FenValue.FromObject(children);

                case "childelementcount":
                    return FenValue.FromNumber(_shadowRoot.ChildNodes.OfType<Element>().Count());

                case "getelementbyid":
                    return FenValue.FromObject(new FenFunction("getElementById", (args, ctx) =>
                    {
                        if (args.Length == 0) return FenValue.Null;
                        var id = args[0].AsString();
                        var element = FindElementById(_shadowRoot, id);
                        return DomWrapperFactory.Wrap(element, _context);
                    }));
            }

            return base.Get(key, context);
        }

        private Element FindElementById(Node root, string id)
        {
            if (root.ChildNodes == null) return null;

            var queue = new System.Collections.Generic.Queue<Node>();
            foreach (var c in root.ChildNodes) queue.Enqueue(c);

            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                if (current is Element el && el.GetAttribute("id") == id)
                {
                    return el;
                }

                if (current.ChildNodes != null)
                {
                    foreach (var c in current.ChildNodes) queue.Enqueue(c);
                }
            }

            return null;
        }
    }
}
