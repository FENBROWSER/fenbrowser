using FenBrowser.Core.Dom.V2;
using FenBrowser.FenEngine.Core;
using FenBrowser.FenEngine.Core.Interfaces;
using FenBrowser.FenEngine.Errors;

namespace FenBrowser.FenEngine.DOM
{
    public class CommentWrapper : NodeWrapper
    {
        private readonly Comment _commentNode;

        public CommentWrapper(Comment commentNode, IExecutionContext context) 
            : base(commentNode, context)
        {
            _commentNode = commentNode;
        }

        public override FenValue Get(string key, IExecutionContext context = null)
        {
            switch (key)
            {
                case "data":
                    return FenValue.FromString(_commentNode.Data);
                case "length":
                    return FenValue.FromNumber(_commentNode.Data?.Length ?? 0);
                case "substringData":
                    return FenValue.FromFunction(new FenFunction("substringData", (args, thisVal) =>
                    {
                        if (args.Length < 2) throw new FenTypeError("Wrong number of arguments for CharacterData.substringData");
                        return FenValue.FromString(_commentNode.SubstringData((int)args[0].ToNumber(), (int)args[1].ToNumber()));
                    }));
                case "appendData":
                    return FenValue.FromFunction(new FenFunction("appendData", (args, thisVal) =>
                    {
                        if (args.Length < 1) throw new FenTypeError("Wrong number of arguments for CharacterData.appendData");
                        _commentNode.AppendData(args[0].ToString());
                        return FenValue.Undefined;
                    }));
                case "insertData":
                    return FenValue.FromFunction(new FenFunction("insertData", (args, thisVal) =>
                    {
                        if (args.Length < 2) throw new FenTypeError("Wrong number of arguments for CharacterData.insertData");
                        _commentNode.InsertData((int)args[0].ToNumber(), args[1].ToString());
                        return FenValue.Undefined;
                    }));
                case "deleteData":
                    return FenValue.FromFunction(new FenFunction("deleteData", (args, thisVal) =>
                    {
                        if (args.Length < 2) throw new FenTypeError("Wrong number of arguments for CharacterData.deleteData");
                        _commentNode.DeleteData((int)args[0].ToNumber(), (int)args[1].ToNumber());
                        return FenValue.Undefined;
                    }));
                case "replaceData":
                    return FenValue.FromFunction(new FenFunction("replaceData", (args, thisVal) =>
                    {
                        if (args.Length < 3) throw new FenTypeError("Wrong number of arguments for CharacterData.replaceData");
                        _commentNode.ReplaceData((int)args[0].ToNumber(), (int)args[1].ToNumber(), args[2].ToString());
                        return FenValue.Undefined;
                    }));
            }
            return base.Get(key, context);
        }
        
        public override void Set(string key, FenValue value, IExecutionContext context = null)
        {
             if (key == "data")
             {
                 _commentNode.Data = value.ToString();
                 return;
             }
             base.Set(key, value, context);
        }
    }
}
