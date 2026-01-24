using FenBrowser.Core.Dom;
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

        public override IValue Get(string key, IExecutionContext context = null)
        {
            switch (key)
            {
                case "mode":
                    return FenValue.FromString(_shadowRoot.Mode);
                case "host":
                    return DomWrapperFactory.Wrap(_shadowRoot.Host, _context); // Wraps the host element
                case "innerHTML":
                    // Handled by NodeWrapper usually if mapped, but NodeWrapper might not have innerHTML logic 
                    // tailored for Fragments, but ElementWrapper does. 
                    // Basic NodeWrapper uses general props. 
                    // If we need innerHTML support on ShadowRoot, we might need to add it or implement a Fragment wrapper.
                    // For now, let's just delegate to base.
                    break;
            }
            return base.Get(key, context);
        }
    }
}
