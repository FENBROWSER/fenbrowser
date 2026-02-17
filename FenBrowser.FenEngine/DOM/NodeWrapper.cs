using FenBrowser.Core.Dom.V2;
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
                    return WrapNode(_node.ParentNode);
                
                case "childNodes":
                    return FenValue.FromObject(new NodeListWrapper(_node.ChildNodes, _context));
                
                case "firstChild":
                    return WrapNode(_node.FirstChild);
                
                case "lastChild":
                    return WrapNode(_node.LastChild);
                
                case "previousSibling":
                    return WrapNode(_node.PreviousSibling);
                
                case "nextSibling":
                    return WrapNode(_node.NextSibling);
                
                case "ownerDocument":
                    return WrapNode(_node.OwnerDocument); 
                
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
        
        // --- Helpers ---

        protected FenValue WrapNode(Node n) => DomWrapperFactory.Wrap(n, _context);

        // --- Methods ---

        private FenValue AppendChild(FenValue[] args, FenValue thisVal)
        {
            if (args.Length == 0) return FenValue.Null; 
            var wrapper = args[0].AsObject();
            Node child = (wrapper as NodeWrapper)?._node ?? (wrapper as ElementWrapper)?.Element;
            
            if (child != null && _node is ContainerNode container)
            {
                try
                {
                    container.AppendChild(child);
                    return args[0];
                }
                catch (Exception ex)
                {
                    FenBrowser.Core.FenLogger.Error($"AppendChild failed: {ex.Message}", FenBrowser.Core.Logging.LogCategory.JavaScript);
                }
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
                try 
                {
                    if (_node is ContainerNode container)
                    {
                        var removed = container.RemoveChild(child);
                        return DomWrapperFactory.Wrap(removed, _context);
                    }
                    else if (child.ParentNode == _node)
                    {
                         // Fallback for non-ContainerNode parents? Node doesn't support children generally.
                         // But if it happened somehow (e.g. child claims parent), try Remove()
                         // child.Remove() is effectively child.ParentNode.RemoveChild(child)
                         // So this branch might be unreachable/redundant for spec-compliant DOM.
                         throw new DomException("HierarchyRequestError", "This node type cannot have children.");
                    }
                    else 
                    {
                         throw new DomException("NotFoundError", "The node to be removed is not a child of this node.");
                    }
                }
                catch
                {
                    return FenValue.Null;
                }
            }
            return FenValue.Null;
        }

        private FenValue ReplaceChild(FenValue[] args, FenValue thisVal)
        {
            // newChild, oldChild
            if (args.Length < 2) return FenValue.Null;
            
            var newW = args[0].AsObject();
            Node newNode = (newW as NodeWrapper)?._node ?? (newW as ElementWrapper)?.Element;

            var oldW = args[1].AsObject();
            Node oldNode = (oldW as NodeWrapper)?._node ?? (oldW as ElementWrapper)?.Element;

            if (newNode != null && oldNode != null && _node is ContainerNode container)
            {
                try 
                {
                    container.ReplaceChild(newNode, oldNode);
                    return args[1]; // Returns old child
                }
                catch
                {
                     return FenValue.Null;
                }
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
            if (args[1] != null && !args[1].IsNull)
            {
                var refW = args[1].AsObject();
                refNode = (refW as NodeWrapper)?._node ?? (refW as ElementWrapper)?.Element;
            }

            if (newNode != null && _node is ContainerNode container)
            {
                try
                {
                    container.InsertBefore(newNode, refNode);
                    return args[0];
                }
                catch
                {
                    return FenValue.Null;
                }
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
            // Delegated to EventTarget logic if Node inherits EventTarget. 
            // In V2, Node usually inherits EventTarget.
            // But if there's no direct method, we might need ElementWrapper's registry or similar.
            // Assuming for now Node might not be where we attach listeners in JS for *all* nodes, 
            // but usually we do. 
            // Let's assume the ElementWrapper registry handles Elements, what about Text nodes?
            // Usually events bubble through Text nodes but listeners are on Elements/Document.
            // If Node is Element, we are good (captured by ElementWrapper override if virtual).
            // Wait, ElementWrapper overrides Get? Yes.
            // So this base implementation is for non-Element nodes (Text, Comment, DocumentFragment usually).
            // Keep minimal or no-op if not supported.
            return FenValue.Undefined;
        }

        private FenValue RemoveEventListener(FenValue[] args, FenValue thisVal)
        {
             return FenValue.Undefined;
        }
        
        private FenValue DispatchEvent(FenValue[] args, FenValue thisVal)
        {
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

        public virtual bool DefineOwnProperty(string key, PropertyDescriptor desc) => false; // Nodes are generally read-only for props
    }
}
