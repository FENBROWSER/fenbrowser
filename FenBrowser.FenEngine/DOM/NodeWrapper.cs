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

        protected static Node UnwrapNode(IObject wrapper)
        {
            if (wrapper is AttrWrapper)
            {
                throw new DomException("HierarchyRequestError", "Attributes cannot be inserted into the child node list.");
            }

            return (wrapper as NodeWrapper)?._node ?? (wrapper as ElementWrapper)?.Element;
        }

        internal virtual Node AppendChildFromBinding(Node child)
        {
            if (child == null)
            {
                return null;
            }

            if (_node is ContainerNode container)
            {
                container.AppendChild(child);
                return child;
            }

            throw new DomException("HierarchyRequestError", "This node type cannot have children.");
        }

        internal virtual Node RemoveChildFromBinding(Node child)
        {
            if (child == null)
            {
                return null;
            }

            if (_node is ContainerNode container)
            {
                return container.RemoveChild(child);
            }

            if (child.ParentNode == _node)
            {
                throw new DomException("HierarchyRequestError", "This node type cannot have children.");
            }

            throw new DomException("NotFoundError", "The node to be removed is not a child of this node.");
        }

        internal virtual Node ReplaceChildFromBinding(Node newNode, Node oldNode)
        {
            if (newNode == null || oldNode == null)
            {
                return null;
            }

            if (_node is ContainerNode container)
            {
                container.ReplaceChild(newNode, oldNode);
                return oldNode;
            }

            throw new DomException("HierarchyRequestError", "This node type cannot have children.");
        }

        internal virtual Node InsertBeforeFromBinding(Node newNode, Node referenceNode)
        {
            if (newNode == null)
            {
                return null;
            }

            if (_node is ContainerNode container)
            {
                return container.InsertBefore(newNode, referenceNode);
            }

            throw new DomException("HierarchyRequestError", "This node type cannot have children.");
        }

        private FenValue Append(FenValue[] args, FenValue thisVal)
        {
            if (!(_node is ContainerNode))
            {
                return FenValue.Undefined;
            }

            foreach (var arg in args)
            {
                try
                {
                    Node child;
                    if (arg.IsObject)
                    {
                        child = UnwrapNode(arg.AsObject());
                    }
                    else
                    {
                        child = new Text(arg.ToString());
                    }

                    if (child != null)
                    {
                        AppendChildFromBinding(child);
                    }
                }
                catch (Exception ex)
                {
                    FenBrowser.Core.FenLogger.Error($"Append failed: {ex.Message}", FenBrowser.Core.Logging.LogCategory.JavaScript);
                }
            }

            return FenValue.Undefined;
        }

        private FenValue AppendChild(FenValue[] args, FenValue thisVal)
        {
            if (args.Length == 0) return FenValue.Null;
            var child = UnwrapNode(args[0].AsObject());

            if (child != null)
            {
                try
                {
                    AppendChildFromBinding(child);
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
            var child = UnwrapNode(args[0].AsObject());

            if (child != null)
            {
                try
                {
                    var removed = RemoveChildFromBinding(child);
                    return DomWrapperFactory.Wrap(removed, _context);
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
                if (_node?.ParentNode != null)
                {
                    var parentWrapper = DomWrapperFactory.Wrap(_node.ParentNode, _context).AsObject() as NodeWrapper;
                    parentWrapper?.RemoveChildFromBinding(_node);
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

            var newNode = UnwrapNode(args[0].AsObject());
            var oldNode = UnwrapNode(args[1].AsObject());

            if (newNode != null && oldNode != null)
            {
                try
                {
                    ReplaceChildFromBinding(newNode, oldNode);
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

            var newNode = UnwrapNode(args[0].AsObject());

            Node refNode = null;
            if (!args[1].IsUndefined && !args[1].IsNull)
            {
                refNode = UnwrapNode(args[1].AsObject());
            }

            if (newNode != null)
            {
                try
                {
                    InsertBeforeFromBinding(newNode, refNode);
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
            if (args.Length < 2)
            {
                return FenValue.Undefined;
            }

            var type = args[0].ToString();
            var callback = args[1];
            var callbackIsValid = callback.IsFunction || (callback.IsObject && !callback.IsNull);
            if (string.IsNullOrWhiteSpace(type) || !callbackIsValid || callback.IsUndefined || callback.IsNull)
            {
                return FenValue.Undefined;
            }

            var capture = false;
            var once = false;
            var passive = false;
            if (args.Length >= 3)
            {
                if (args[2].IsBoolean)
                {
                    capture = args[2].ToBoolean();
                }
                else if (args[2].IsObject)
                {
                    var opts = args[2].AsObject();
                    var cap = opts.Get("capture", _context);
                    capture = cap.IsBoolean && cap.ToBoolean();
                    var one = opts.Get("once", _context);
                    once = one.IsBoolean && one.ToBoolean();
                    var pas = opts.Get("passive", _context);
                    passive = pas.IsBoolean && pas.ToBoolean();
                }
            }

            EventTarget.Registry.Add(_node, type, callback, capture, once, passive);
            return FenValue.Undefined;
        }

        private FenValue RemoveEventListener(FenValue[] args, FenValue thisVal)
        {
            if (args.Length < 2)
            {
                return FenValue.Undefined;
            }

            var type = args[0].ToString();
            var callback = args[1];
            var capture = false;
            if (args.Length >= 3)
            {
                if (args[2].IsBoolean)
                {
                    capture = args[2].ToBoolean();
                }
                else if (args[2].IsObject)
                {
                    var cap = args[2].AsObject().Get("capture", _context);
                    capture = cap.IsBoolean && cap.ToBoolean();
                }
            }

            EventTarget.Registry.Remove(_node, type, callback, capture);
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

            DispatchNodeTargetListeners(eventObj);
            var defaultPrevented = eventObj.Get("defaultPrevented", _context);
            if (eventObj.Get("cancelBubble", _context).IsBoolean && eventObj.Get("cancelBubble", _context).ToBoolean())
            {
                return FenValue.FromBoolean(!(defaultPrevented.IsBoolean && defaultPrevented.ToBoolean()));
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

        private void DispatchNodeTargetListeners(DomEvent eventObj)
        {
            if (eventObj == null || string.IsNullOrWhiteSpace(eventObj.Type))
            {
                return;
            }

            eventObj.Set("target", FenValue.FromObject(this), _context);
            eventObj.Set("srcElement", FenValue.FromObject(this), _context);
            eventObj.Set("currentTarget", FenValue.FromObject(this), _context);
            eventObj.Set("eventPhase", FenValue.FromNumber(DomEvent.AT_TARGET), _context);

            InvokeNodeListeners(eventObj, capturePhase: true);
            InvokeNodeListeners(eventObj, capturePhase: false);

            eventObj.Set("currentTarget", FenValue.Null, _context);
            eventObj.Set("eventPhase", FenValue.FromNumber(DomEvent.NONE), _context);
        }

        private void InvokeNodeListeners(DomEvent eventObj, bool capturePhase)
        {
            foreach (var listener in EventTarget.Registry.Get(_node, eventObj.Type, capturePhase))
            {
                FenFunction callbackFn = null;
                var callbackThis = FenValue.FromObject(this);
                var callback = listener.Callback;

                if (callback.IsFunction)
                {
                    callbackFn = callback.AsFunction() as FenFunction;
                }
                else if (callback.IsObject)
                {
                    var handleEvent = callback.AsObject().Get("handleEvent", _context);
                    if (handleEvent.IsFunction)
                    {
                        callbackFn = handleEvent.AsFunction() as FenFunction;
                        callbackThis = callback;
                    }
                }

                if (callbackFn == null)
                {
                    continue;
                }

                if (listener.Once)
                {
                    EventTarget.Registry.RemoveOnce(_node, eventObj.Type, listener);
                }

                callbackFn.Invoke(new[] { FenValue.FromObject(eventObj) }, _context, callbackThis);

                var cancelBubbleVal = eventObj.Get("cancelBubble", _context);
                if (cancelBubbleVal.IsBoolean && cancelBubbleVal.ToBoolean())
                {
                    break;
                }
            }
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





