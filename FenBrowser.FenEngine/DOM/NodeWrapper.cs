using FenBrowser.Core.Dom;
using FenBrowser.FenEngine.Core;
using FenBrowser.FenEngine.Core.Interfaces;
using System;

namespace FenBrowser.FenEngine.DOM
{
    public class NodeWrapper : IObject
    {
        protected readonly Node _node;
        protected readonly IExecutionContext _context;
        public object NativeObject { get; set; }

        public NodeWrapper(Node node, IExecutionContext context)
        {
            _node = node ?? throw new ArgumentNullException(nameof(node));
            _context = context ?? throw new ArgumentNullException(nameof(context));
        }

        internal Node Node => _node;

        public virtual FenValue Get(string key, IExecutionContext context = null)
        {
            switch (key)
            {
                case "nodeType":
                    return FenValue.FromNumber((int)_node.NodeType);
                
                case "nodeName":
                    return FenValue.FromString(_node.NodeName);
                
                case "nodeValue":
                    return FenValue.FromString(_node.NodeValue);
                
                case "parentNode":
                    return WrapNode(_node.Parent);
                
                case "childNodes":
                    return FenValue.FromObject(new NodeListWrapper(_node.Children, _context));
                
                case "firstChild":
                    return WrapNode(_node.FirstChild);
                
                case "lastChild":
                    return WrapNode(_node.LastChild);
                
                case "previousSibling":
                    return WrapNode(_node.PreviousSibling);
                
                case "nextSibling":
                    return WrapNode(_node.NextSibling);
                
                case "ownerDocument":
                    return WrapNode(_node.OwnerDocument); // Needs DocumentWrapper factory logic ideally
                
                case "textContent":
                    return FenValue.FromString(_node.TextContent);

                // Methods
                case "appendChild":
                    return FenValue.FromFunction(new FenFunction("appendChild", AppendChild));
                case "removeChild":
                    return FenValue.FromFunction(new FenFunction("removeChild", RemoveChild));
                case "replaceChild":
                    return FenValue.FromFunction(new FenFunction("replaceChild", ReplaceChild));
                case "insertBefore":
                    return FenValue.FromFunction(new FenFunction("insertBefore", InsertBefore));
                case "cloneNode":
                    return FenValue.FromFunction(new FenFunction("cloneNode", CloneNode));
                case "hasChildNodes":
                    return FenValue.FromFunction(new FenFunction("hasChildNodes", (args, thisVal) => FenValue.FromBoolean(_node.HasChildNodes)));
                
                // EventTarget
                case "addEventListener":
                    return FenValue.FromFunction(new FenFunction("addEventListener", AddEventListener));
                case "removeEventListener":
                    return FenValue.FromFunction(new FenFunction("removeEventListener", RemoveEventListener));
                case "dispatchEvent":
                    return FenValue.FromFunction(new FenFunction("dispatchEvent", DispatchEvent));

                // Constants (exposed on prototype usually, but useful on instance for checking)
                case "ELEMENT_NODE": return FenValue.FromNumber(1);
                case "TEXT_NODE": return FenValue.FromNumber(3);
                case "COMMENT_NODE": return FenValue.FromNumber(8);
                case "DOCUMENT_NODE": return FenValue.FromNumber(9);
            }
            return FenValue.Undefined;
        }

        public virtual void Set(string key, FenValue value, IExecutionContext context = null)
        {
            switch(key)
            {
                case "nodeValue":
                    _node.NodeValue = value.ToString();
                    break;
                case "textContent":
                    _node.TextContent = value.ToString();
                    break;
            }
        }

        public virtual System.Collections.Generic.IEnumerable<string> Keys()
        {
            yield return "nodeType";
            yield return "nodeName";
            yield return "nodeValue";
            yield return "parentNode";
            yield return "childNodes";
            yield return "firstChild";
            yield return "lastChild";
            yield return "nextSibling";
            yield return "previousSibling";
            yield return "ownerDocument";
            yield return "textContent";
            yield return "appendChild";
            yield return "removeChild";
        }

        public virtual bool Has(string key)
        {
             // Simplified check
             return Get(key) != FenValue.Undefined;
        }

        // --- Helpers ---

        protected FenValue WrapNode(Node n) => DomWrapperFactory.Wrap(n, _context);

        // --- Methods ---

        private FenValue AppendChild(FenValue[] args, FenValue thisVal)
        {
            if (args.Length == 0) return FenValue.Null; // Throw error ideally
            var nodeWrapper = args[0].AsObject() as NodeWrapper;
            var elementWrapper = args[0].AsObject() as ElementWrapper; 
            
            Node child = nodeWrapper?._node ?? elementWrapper?.Element; // Handle both until ElementWrapper inherits
            
            if (child != null)
            {
                _node.AppendChild(child);
                return args[0];
            }
            return FenValue.Null;
        }

        private FenValue RemoveChild(FenValue[] args, FenValue thisVal)
        {
            if (args.Length == 0) return FenValue.Null;
            var wrapper = args[0].AsObject();
            Node child = (wrapper as NodeWrapper)?._node ?? (wrapper as ElementWrapper)?.Element;

            if (child != null)
            {
                // Verify parent? Node.Remove() handles hierarchy but RemoveChild implies direct child.
                if (child.Parent == _node)
                {
                    child.Remove();
                    return args[0];
                }
            }
            return FenValue.Null; // Or throw NotFoundError
        }

        private FenValue ReplaceChild(FenValue[] args, FenValue thisVal)
        {
            // newChild, oldChild
            if (args.Length < 2) return FenValue.Null;
            
            var newW = args[0].AsObject();
            Node newNode = (newW as NodeWrapper)?._node ?? (newW as ElementWrapper)?.Element;

            var oldW = args[1].AsObject();
            Node oldNode = (oldW as NodeWrapper)?._node ?? (oldW as ElementWrapper)?.Element;

            if (newNode != null && oldNode != null && oldNode.Parent == _node)
            {
                _node.InsertBefore(newNode, oldNode);
                oldNode.Remove();
                return args[1]; // Returns old child
            }
            return FenValue.Null;
        }

        private FenValue InsertBefore(FenValue[] args, FenValue thisVal)
        {
             // newNode, referenceNode
            if (args.Length < 2) return FenValue.Null;

            var newW = args[0].AsObject();
            Node newNode = (newW as NodeWrapper)?._node ?? (newW as ElementWrapper)?.Element;
            
            Node refNode = null;
            if (args[1] != null)
            {
                var refW = args[1].AsObject();
                refNode = (refW as NodeWrapper)?._node ?? (refW as ElementWrapper)?.Element;
            }

            if (newNode != null)
            {
                _node.InsertBefore(newNode, refNode);
                return args[0];
            }
            return FenValue.Null;
        }
        
        private FenValue CloneNode(FenValue[] args, FenValue thisVal)
        {
            bool deep = false;
            if (args.Length > 0) deep = args[0].ToBoolean();
            
            var clone = _node.CloneNode(deep);
            return WrapNode(clone);
        }

        private FenValue AddEventListener(FenValue[] args, FenValue thisVal)
        {
            if (args.Length >= 2)
            {
                string type = args[0].ToString();
                // Store wrapper as callback or actual function?
                // Node expects object callback.
                _node.AddEventListener(type, args[1]); 
            }
            return FenValue.Undefined;
        }

        private FenValue RemoveEventListener(FenValue[] args, FenValue thisVal)
        {
             if (args.Length >= 2)
            {
                string type = args[0].ToString();
                _node.RemoveEventListener(type, args[1]);
            }
            return FenValue.Undefined;
        }
        
        private FenValue DispatchEvent(FenValue[] args, FenValue thisVal)
        {
             // Todo: Implement bridging to EventTarget.DispatchEvent
             return FenValue.FromBoolean(true); 
        }
        public virtual bool Has(string key, IExecutionContext context = null)
        {
             return !Get(key, context).IsUndefined;
        }

        public virtual System.Collections.Generic.IEnumerable<string> Keys(IExecutionContext context = null)
        {
             return new[] { "nodeName", "nodeType", "nodeValue", "textContent", "parentNode", "childNodes", "firstChild", "lastChild", "previousSibling", "nextSibling", "ownerDocument", "appendChild", "removeChild", "replaceChild", "insertBefore", "cloneNode", "hasChildNodes", "addEventListener", "removeEventListener", "dispatchEvent" };
        }

        public virtual bool Delete(string key, IExecutionContext context = null) => false;
        
        protected IObject _prototype;
        public IObject GetPrototype() => _prototype;
        public void SetPrototype(IObject prototype) => _prototype = prototype;
    }
}
