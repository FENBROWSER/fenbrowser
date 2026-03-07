using FenBrowser.Core.Dom.V2;
using FenBrowser.FenEngine.Core;
using FenBrowser.FenEngine.Core.Interfaces;

using Range = FenBrowser.Core.Dom.V2.Range;

namespace FenBrowser.FenEngine.DOM
{
    public class RangeWrapper : IObject
    {
        private static readonly string[] BuiltInKeys =
        {
            "startContainer",
            "startOffset",
            "endContainer",
            "endOffset",
            "collapsed",
            "commonAncestorContainer",
            "setStart",
            "setEnd",
            "setStartBefore",
            "setStartAfter",
            "setEndBefore",
            "setEndAfter",
            "collapse",
            "selectNode",
            "selectNodeContents",
            "compareBoundaryPoints",
            "deleteContents",
            "cloneContents",
            "extractContents",
            "insertNode",
            "surroundContents",
            "cloneRange",
            "detach",
            "isPointInRange",
            "comparePoint",
            "intersectsNode",
            "toString"
        };

        private readonly Range _range;
        private readonly IExecutionContext _context;
        private readonly Dictionary<string, PropertyDescriptor> _expandos = new(System.StringComparer.Ordinal);
        public object NativeObject { get; set; }
        private IObject _prototype;

        public RangeWrapper(Range range, IExecutionContext context)
        {
            _range = range;
            _context = context;
            NativeObject = range;
        }

        public FenValue Get(string key, IExecutionContext context = null)
        {
            _context?.CheckExecutionTimeLimit();

            if (_expandos.TryGetValue(key, out var expando) && expando.Value.HasValue)
                return expando.Value.Value;

            switch(key)
            {
                case "startContainer": return DomWrapperFactory.Wrap(_range.StartContainer, _context);
                case "startOffset": return FenValue.FromNumber(_range.StartOffset);
                case "endContainer": return DomWrapperFactory.Wrap(_range.EndContainer, _context);
                case "endOffset": return FenValue.FromNumber(_range.EndOffset);
                case "collapsed": return FenValue.FromBoolean(_range.Collapsed);
                case "commonAncestorContainer": return DomWrapperFactory.Wrap(_range.CommonAncestorContainer, _context);
                
                case "setStart": return FenValue.FromFunction(new FenFunction("setStart", (args, _) => CallRangeMethod(_range.SetStart, args)));
                case "setEnd": return FenValue.FromFunction(new FenFunction("setEnd", (args, _) => CallRangeMethod(_range.SetEnd, args)));
                case "setStartBefore": return FenValue.FromFunction(new FenFunction("setStartBefore", (args, _) => CallNodeMethod(_range.SetStartBefore, args)));
                case "setStartAfter": return FenValue.FromFunction(new FenFunction("setStartAfter", (args, _) => CallNodeMethod(_range.SetStartAfter, args)));
                case "setEndBefore": return FenValue.FromFunction(new FenFunction("setEndBefore", (args, _) => CallNodeMethod(_range.SetEndBefore, args)));
                case "setEndAfter": return FenValue.FromFunction(new FenFunction("setEndAfter", (args, _) => CallNodeMethod(_range.SetEndAfter, args)));
                case "collapse": return FenValue.FromFunction(new FenFunction("collapse", (args, _) => { _range.Collapse(args.Length > 0 ? args[0].ToBoolean() : true); return FenValue.Undefined; }));
                case "selectNode": return FenValue.FromFunction(new FenFunction("selectNode", (args, _) => CallNodeMethod(_range.SelectNode, args)));
                case "selectNodeContents": return FenValue.FromFunction(new FenFunction("selectNodeContents", (args, _) => CallNodeMethod(_range.SelectNodeContents, args)));
                case "compareBoundaryPoints": return FenValue.FromFunction(new FenFunction("compareBoundaryPoints", (args, _) => CompareBoundaryPoints(args)));
                case "deleteContents": return FenValue.FromFunction(new FenFunction("deleteContents", (args, _) => { _range.DeleteContents(); return FenValue.Undefined; }));
                case "cloneContents": return FenValue.FromFunction(new FenFunction("cloneContents", (args, _) => DomWrapperFactory.Wrap(_range.CloneContents(), _context)));
                case "extractContents": return FenValue.FromFunction(new FenFunction("extractContents", (args, _) => DomWrapperFactory.Wrap(_range.ExtractContents(), _context)));
                case "insertNode": return FenValue.FromFunction(new FenFunction("insertNode", (args, _) => CallNodeMethod(_range.InsertNode, args)));
                case "surroundContents": return FenValue.FromFunction(new FenFunction("surroundContents", (args, _) => CallNodeMethod(_range.SurroundContents, args)));
                case "cloneRange": return FenValue.FromFunction(new FenFunction("cloneRange", (args, _) => FenValue.FromObject(new RangeWrapper(_range.CloneRange(), _context))));
                case "detach": return FenValue.FromFunction(new FenFunction("detach", (args, _) => { _range.Detach(); return FenValue.Undefined; }));
                case "isPointInRange": return FenValue.FromFunction(new FenFunction("isPointInRange", (args, _) => IsPointInRange(args)));
                case "comparePoint": return FenValue.FromFunction(new FenFunction("comparePoint", (args, _) => ComparePoint(args)));
                case "intersectsNode": return FenValue.FromFunction(new FenFunction("intersectsNode", (args, _) => IntersectsNode(args)));
                case "toString": return FenValue.FromFunction(new FenFunction("toString", (args, _) => FenValue.FromString(_range.ToString())));
            }
            return FenValue.Undefined;
        }

        private FenValue CallRangeMethod(System.Action<Node, int> method, FenValue[] args)
        {
            if (args.Length < 2) return FenValue.Undefined;
            var node = TryGetNode(args[0]);
 
            if (node != null && args[1].IsNumber)
            {
                method(node, (int)args[1].ToNumber());
            }
            return FenValue.Undefined;
        }


        private FenValue CallNodeMethod(System.Action<Node> method, FenValue[] args)
        {
             if (args.Length < 1) return FenValue.Undefined;
             var node = TryGetNode(args[0]);

             if (node != null) method(node);
             return FenValue.Undefined;
        }

        public IObject GetPrototype() => _prototype;
        public void SetPrototype(IObject prototype) => _prototype = prototype;
        public bool DefineOwnProperty(string key, PropertyDescriptor desc)
        {
            if (IsBuiltInKey(key))
                return false;

            if (desc.IsAccessor)
                return false;

            if (_expandos.TryGetValue(key, out var existing) && existing.Configurable == false)
            {
                if (desc.Configurable == true)
                    return false;

                if (desc.Enumerable.HasValue && desc.Enumerable != existing.Enumerable)
                    return false;

                if (existing.Writable == false)
                {
                    if (desc.Writable == true)
                        return false;

                    if (desc.Value.HasValue && (!existing.Value.HasValue || !existing.Value.Value.StrictEquals(desc.Value.Value)))
                        return false;
                }
            }

            _expandos[key] = MergeDescriptor(existing, desc);
            return true;
        }

        public bool Delete(string key, IExecutionContext context = null)
        {
            if (IsBuiltInKey(key))
                return false;

            if (!_expandos.TryGetValue(key, out var desc))
                return true;

            if (desc.Configurable == false)
                return false;

            return _expandos.Remove(key);
        }

        public IEnumerable<string> Keys(IExecutionContext context = null)
        {
            foreach (var key in BuiltInKeys)
                yield return key;

            foreach (var key in _expandos.Keys)
                yield return key;
        }

        public void Set(string key, FenValue value, IExecutionContext context = null)
        {
            if (IsBuiltInKey(key))
                return;

            if (_expandos.TryGetValue(key, out var desc))
            {
                if (desc.Writable == false)
                    return;

                desc.Value = value;
                if (!desc.Writable.HasValue)
                    desc.Writable = true;
                if (!desc.Enumerable.HasValue)
                    desc.Enumerable = true;
                if (!desc.Configurable.HasValue)
                    desc.Configurable = true;

                _expandos[key] = desc;
                return;
            }

            _expandos[key] = PropertyDescriptor.DataDefault(value);
        }

        public bool Has(string key, IExecutionContext context = null)
            => IsBuiltInKey(key) || _expandos.ContainsKey(key);

        private FenValue CompareBoundaryPoints(FenValue[] args)
        {
            if (args.Length < 2 || !args[0].IsNumber)
                return FenValue.Undefined;

            if (args[1].AsObject() is not RangeWrapper other)
                return FenValue.Undefined;

            return FenValue.FromNumber(_range.CompareBoundaryPoints((ushort)args[0].ToNumber(), other._range));
        }

        private FenValue IsPointInRange(FenValue[] args)
        {
            if (args.Length < 2 || !args[1].IsNumber)
                return FenValue.Undefined;

            var node = TryGetNode(args[0]);
            if (node == null)
                return FenValue.Undefined;

            return FenValue.FromBoolean(_range.IsPointInRange(node, (int)args[1].ToNumber()));
        }

        private FenValue ComparePoint(FenValue[] args)
        {
            if (args.Length < 2 || !args[1].IsNumber)
                return FenValue.Undefined;

            var node = TryGetNode(args[0]);
            if (node == null)
                return FenValue.Undefined;

            return FenValue.FromNumber(_range.ComparePoint(node, (int)args[1].ToNumber()));
        }

        private FenValue IntersectsNode(FenValue[] args)
        {
            if (args.Length < 1)
                return FenValue.Undefined;

            var node = TryGetNode(args[0]);
            if (node == null)
                return FenValue.Undefined;

            return FenValue.FromBoolean(_range.IntersectsNode(node));
        }

        private static Node TryGetNode(FenValue value)
        {
            var obj = value.AsObject();
            if (obj == null)
                return null;

            if (obj is NodeWrapper nw)
                return nw.Node;

            if (obj is DocumentWrapper dw)
                return dw.Node;

            if (obj is FenObject fenObj)
                return fenObj.NativeObject as Node;

            return null;
        }

        private static bool IsBuiltInKey(string key)
        {
            switch (key)
            {
                case "startContainer":
                case "startOffset":
                case "endContainer":
                case "endOffset":
                case "collapsed":
                case "commonAncestorContainer":
                case "setStart":
                case "setEnd":
                case "setStartBefore":
                case "setStartAfter":
                case "setEndBefore":
                case "setEndAfter":
                case "collapse":
                case "selectNode":
                case "selectNodeContents":
                case "compareBoundaryPoints":
                case "deleteContents":
                case "cloneContents":
                case "extractContents":
                case "insertNode":
                case "surroundContents":
                case "cloneRange":
                case "detach":
                case "isPointInRange":
                case "comparePoint":
                case "intersectsNode":
                case "toString":
                    return true;
                default:
                    return false;
            }
        }

        private static PropertyDescriptor MergeDescriptor(PropertyDescriptor existing, PropertyDescriptor update)
        {
            return new PropertyDescriptor
            {
                Value = update.Value ?? existing.Value ?? FenValue.Undefined,
                Writable = update.Writable ?? existing.Writable ?? true,
                Enumerable = update.Enumerable ?? existing.Enumerable ?? true,
                Configurable = update.Configurable ?? existing.Configurable ?? true,
                Getter = null,
                Setter = null
            };
        }
    }
}
