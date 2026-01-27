using FenBrowser.Core.Dom;
using FenBrowser.FenEngine.Core;
using FenBrowser.FenEngine.Core.Interfaces;

using Range = FenBrowser.Core.Dom.Range;

namespace FenBrowser.FenEngine.DOM
{
    public class RangeWrapper : IObject
    {
        private readonly Range _range;
        private readonly IExecutionContext _context;
        public object NativeObject { get; set; }

        public RangeWrapper(Range range, IExecutionContext context)
        {
            _range = range;
            _context = context;
        }

        public FenValue Get(string key, IExecutionContext context = null)
        {
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
                case "deleteContents": return FenValue.FromFunction(new FenFunction("deleteContents", (args, _) => { _range.DeleteContents(); return FenValue.Undefined; }));
                case "cloneContents": return FenValue.FromFunction(new FenFunction("cloneContents", (args, _) => DomWrapperFactory.Wrap(_range.CloneContents(), _context)));
                case "extractContents": return FenValue.FromFunction(new FenFunction("extractContents", (args, _) => DomWrapperFactory.Wrap(_range.ExtractContents(), _context)));
                case "insertNode": return FenValue.FromFunction(new FenFunction("insertNode", (args, _) => CallNodeMethod(_range.InsertNode, args)));
                case "surroundContents": return FenValue.FromFunction(new FenFunction("surroundContents", (args, _) => CallNodeMethod(_range.SurroundContents, args)));
                case "cloneRange": return FenValue.FromFunction(new FenFunction("cloneRange", (args, _) => FenValue.FromObject(new RangeWrapper(_range.CloneRange(), _context))));
                case "detach": return FenValue.FromFunction(new FenFunction("detach", (args, _) => { _range.Detach(); return FenValue.Undefined; }));
                case "toString": return FenValue.FromFunction(new FenFunction("toString", (args, _) => FenValue.FromString(_range.ToString())));
            }
            return FenValue.Undefined;
        }

        private FenValue CallRangeMethod(System.Action<Node, int> method, FenValue[] args)
        {
            if (args.Length < 2) return FenValue.Undefined;
            var obj = args[0].AsObject();
            Node node = null;
             
            if (obj is NodeWrapper nw) node = nw.Node;
            else if (obj is DocumentWrapper dw) node = dw.Node;
 
            if (node != null && args[1].IsNumber)
            {
                method(node, (int)args[1].ToNumber());
            }
            return FenValue.Undefined;
        }


        private FenValue CallNodeMethod(System.Action<Node> method, FenValue[] args)
        {
             if (args.Length < 1) return FenValue.Undefined;
             var obj = args[0].AsObject();
             Node node = null;
             
             if (obj is NodeWrapper nw) node = nw.Node;
             else if (obj is DocumentWrapper dw) node = dw.Node;

             if (node != null) method(node);
             return FenValue.Undefined;
        }


        

        public IObject _prototype;
        public IObject GetPrototype() => _prototype;
        public void SetPrototype(IObject prototype) => _prototype = prototype;
        public bool Delete(string key, IExecutionContext context = null) => false;
        public System.Collections.Generic.IEnumerable<string> Keys(IExecutionContext context = null) => new string[0];
        public void Set(string key, FenValue value, IExecutionContext context = null) { }
        public bool Has(string key, IExecutionContext context = null) => false;
    }
}
