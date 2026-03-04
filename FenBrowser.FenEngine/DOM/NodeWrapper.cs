using FenBrowser.Core.Dom.V2;
using FenBrowser.FenEngine.Core;
using FenBrowser.FenEngine.Core.Interfaces;
using FenBrowser.FenEngine.Errors;
using System;
using System.Collections.Generic;
using System.Linq;

namespace FenBrowser.FenEngine.DOM
{
    public class NodeWrapper : IObject
    {
        protected readonly Node _node;
        protected readonly IExecutionContext _context;
        private readonly Dictionary<string, FenValue> _expandoProperties = new Dictionary<string, FenValue>(StringComparer.Ordinal);
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
                case "append":
                    return FenValue.FromFunction(new FenFunction("append", Append));
                case "removeChild":
                    return FenValue.FromFunction(new FenFunction("removeChild", RemoveChild));
                case "remove":
                    return FenValue.FromFunction(new FenFunction("remove", Remove));
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

                // Constants
                case "ELEMENT_NODE": return FenValue.FromNumber(1);
                case "TEXT_NODE": return FenValue.FromNumber(3);
                case "COMMENT_NODE": return FenValue.FromNumber(8);
                case "DOCUMENT_NODE": return FenValue.FromNumber(9);
            }

            if (_expandoProperties.TryGetValue(key, out var expando))
            {
                return expando;
            }

            return FenValue.Undefined;
        }

        public virtual void Set(string key, FenValue value, IExecutionContext context = null)
        {
            switch (key)
            {
                case "nodeValue":
                    _node.NodeValue = value.ToString();
                    return;
                case "textContent":
                    _node.TextContent = value.ToString();
                    return;
            }

            _expandoProperties[key] = value;
        }

        protected FenValue WrapNode(Node n) => DomWrapperFactory.Wrap(n, _context);

        private FenValue Append(FenValue[] args, FenValue thisVal)
        {
            if (!(_node is ContainerNode container))
            {
                return FenValue.Undefined;
            }

            foreach (var arg in args)
            {
                if (arg.IsObject)
                {
                    var wrapper = arg.AsObject();
                    var child = (wrapper as NodeWrapper)?._node ?? (wrapper as ElementWrapper)?.Element;
                    if (child != null)
                    {
                        container.AppendChild(child);
                    }
                }
                else
                {
                    container.AppendChild(new Text(arg.ToString()));
                }
            }

            return FenValue.Undefined;
        }

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

                    if (child.ParentNode == _node)
                    {
                        throw new DomException("HierarchyRequestError", "This node type cannot have children.");
                    }

                    throw new DomException("NotFoundError", "The node to be removed is not a child of this node.");
                }
                catch
                {
                    return FenValue.Null;
                }
            }
            return FenValue.Null;
        }

        private FenValue Remove(FenValue[] args, FenValue thisVal)
        {
            try
            {
                if (_node?.ParentNode is ContainerNode parent)
                {
                    parent.RemoveChild(_node);
                }
            }
            catch
            {
            }

            return FenValue.Undefined;
        }

        private FenValue ReplaceChild(FenValue[] args, FenValue thisVal)
        {
            if (args.Length < 2) return FenValue.Null;

            var newW = args[0].AsObject();
            if (newW is AttrWrapper)
            {
                throw new DomException("HierarchyRequestError", "Attributes cannot be inserted into the child node list.");
            }
            Node newNode = (newW as NodeWrapper)?._node ?? (newW as ElementWrapper)?.Element;

            var oldW = args[1].AsObject();
            Node oldNode = (oldW as NodeWrapper)?._node ?? (oldW as ElementWrapper)?.Element;

            if (newNode != null && oldNode != null && _node is ContainerNode container)
            {
                try
                {
                    container.ReplaceChild(newNode, oldNode);
                    return args[1];
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
            if (args.Length < 2) return FenValue.Null;

            var newW = args[0].AsObject();
            if (newW is AttrWrapper)
            {
                throw new DomException("HierarchyRequestError", "Attributes cannot be inserted into the child node list.");
            }
            Node newNode = (newW as NodeWrapper)?._node ?? (newW as ElementWrapper)?.Element;

            Node refNode = null;
            if (!args[1].IsUndefined && !args[1].IsNull)
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
            return FenValue.Undefined;
        }

        private FenValue RemoveEventListener(FenValue[] args, FenValue thisVal)
        {
            return FenValue.Undefined;
        }

        private FenValue DispatchEvent(FenValue[] args, FenValue thisVal)
        {
            if (args.Length == 0 || !args[0].IsObject)
            {
                throw new FenTypeError("TypeError: Failed to execute 'dispatchEvent': parameter 1 is not of type 'Event'.");
            }

            var eventObj = args[0].AsObject() as DomEvent;
            if (eventObj == null)
            {
                var evtLike = args[0].AsObject() as FenObject;
                if (evtLike == null) return FenValue.FromBoolean(false);

                var typeVal = evtLike.Get("type");
                var type = !typeVal.IsUndefined ? typeVal.ToString() : string.Empty;
                var bubbles = evtLike.Get("bubbles").ToBoolean();
                var cancelable = evtLike.Get("cancelable").ToBoolean();
                var composed = evtLike.Get("composed").ToBoolean();
                eventObj = new DomEvent(type, bubbles, cancelable, composed, _context);
            }

            if (_node is Element elementTarget)
            {
                return FenValue.FromBoolean(EventTarget.DispatchEvent(elementTarget, eventObj, _context));
            }

            if (eventObj.Bubbles && _node.ParentNode is Element parentElement)
            {
                var parentWrapped = DomWrapperFactory.Wrap(parentElement, _context);
                if (parentWrapped.IsObject)
                {
                    var dispatchFn = parentWrapped.AsObject().Get("dispatchEvent", _context);
                    if (dispatchFn.IsFunction)
                    {
                        return dispatchFn.AsFunction().Invoke(new[] { args[0] }, _context, parentWrapped);
                    }
                }

                return FenValue.FromBoolean(EventTarget.DispatchEvent(parentElement, eventObj, _context));
            }

            return FenValue.FromBoolean(true);
        }

        public virtual bool Has(string key, IExecutionContext context = null)
        {
            return !Get(key, context).IsUndefined;
        }

        public virtual IEnumerable<string> Keys(IExecutionContext context = null)
        {
            var builtins = new[]
            {
                "nodeName", "nodeType", "nodeValue", "textContent", "parentNode", "childNodes", "firstChild",
                "lastChild", "previousSibling", "nextSibling", "ownerDocument", "appendChild", "append",
                "removeChild", "remove", "replaceChild", "insertBefore", "cloneNode", "hasChildNodes",
                "addEventListener", "removeEventListener", "dispatchEvent"
            };
            return builtins.Concat(_expandoProperties.Keys);
        }

        public virtual bool Delete(string key, IExecutionContext context = null) => _expandoProperties.Remove(key);

        protected IObject _prototype;
        public IObject GetPrototype() => _prototype;
        public void SetPrototype(IObject prototype) => _prototype = prototype;

        public virtual bool DefineOwnProperty(string key, PropertyDescriptor desc) => false;
    }
}





