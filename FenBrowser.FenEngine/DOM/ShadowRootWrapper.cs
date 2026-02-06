using FenBrowser.Core.Dom.V2;
using FenBrowser.FenEngine.Core;
using FenBrowser.FenEngine.Core.Interfaces;

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
            switch (key)
            {
                case "mode":
                    return FenValue.FromString(_shadowRoot.Mode.ToString().ToLowerInvariant());
                case "host":
                    return DomWrapperFactory.Wrap(_shadowRoot.Host, _context); // Wraps the host element
                case "getElementById":
                    return FenValue.FromObject(new FenFunction("getElementById", (args, ctx) =>
                    {
                        if (args.Length == 0) return FenValue.Null;
                        string id = args[0].AsString();
                        
                        // Scoped ID Search: Search ONLY within this Shadow Root
                        var element = FindElementById(_shadowRoot, id);
                        return DomWrapperFactory.Wrap(element, _context);
                    }));
            }
            return base.Get(key, context);
        }
        
        private Element FindElementById(Node root, string id)
        {
            // BFS or DFS search restricted to this tree
            // IMPORTANT: Do not cross boundary into nested Shadow Roots (unless spec says so, typically encapsulation blocks it)
            // But we must search descendants.
            
            if (root.ChildNodes == null) return null;
            
            // Queue for BFS
            var queue = new System.Collections.Generic.Queue<Node>();
            foreach(var c in root.ChildNodes) queue.Enqueue(c);
            
            while(queue.Count > 0)
            {
                var current = queue.Dequeue();
                if (current is Element el && el.GetAttribute("id") == id)
                {
                    return el;
                }
                
                // Don't recurse into nested Shadow Roots?
                // The DOM tree structure in FenEngine: Shadow Roots are not usually children in `ChildNodes` list of Host?
                // `Element.ShadowRoot` property holds it.
                // So normal traversal of `ChildNodes` stays in light DOM (of the shadow tree).
                // So we are safe just walking `ChildNodes`.
                
                if (current.ChildNodes != null)
                {
                    foreach (var c in current.ChildNodes) queue.Enqueue(c);
                }
            }
            return null;
        }
    }
}
