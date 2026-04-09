using System;
using FenBrowser.Core.Dom.V2;
using FenBrowser.FenEngine.Core;
using FenBrowser.FenEngine.Core.Interfaces;
using FenBrowser.FenEngine.Errors;

namespace FenBrowser.FenEngine.DOM
{
    public class TextWrapper : NodeWrapper
    {
        private readonly Text _textNode;

        public TextWrapper(Text textNode, IExecutionContext context)
            : base(textNode, context)
        {
            _textNode = textNode;
        }

        public override FenValue Get(string key, IExecutionContext context = null)
        {
            switch (key)
            {
                case "data":
                    return FenValue.FromString(_textNode.Data);
                case "length":
                    return FenValue.FromNumber((_textNode.Data ?? string.Empty).Length);
                case "wholeText":
                    return FenValue.FromString(_textNode.WholeText);
                case "splitText":
                    return FenValue.FromFunction(new FenFunction("splitText", (args, thisVal) =>
                    {
                        int offset = args.Length > 0 ? (int)args[0].ToNumber() : 0;
                        try
                        {
                            var newText = _textNode.SplitText(offset);
                            return WrapNode(newText);
                        }
                        catch (FenBrowser.Core.Dom.V2.DomException ex)
                        {
                            throw new InvalidOperationException(ex.Message);
                        }
                    }));
                case "substringData":
                    return FenValue.FromFunction(new FenFunction("substringData", (args, thisVal) =>
                    {
                        if (args.Length < 2) throw new FenTypeError("Wrong number of arguments for CharacterData.substringData");
                        return FenValue.FromString(_textNode.SubstringData((int)args[0].ToNumber(), (int)args[1].ToNumber()));
                    }));
                case "appendData":
                    return FenValue.FromFunction(new FenFunction("appendData", (args, thisVal) =>
                    {
                        if (args.Length < 1) throw new FenTypeError("Wrong number of arguments for CharacterData.appendData");
                        _textNode.AppendData(args[0].ToString());
                        return FenValue.Undefined;
                    }));
                case "insertData":
                    return FenValue.FromFunction(new FenFunction("insertData", (args, thisVal) =>
                    {
                        if (args.Length < 2) throw new FenTypeError("Wrong number of arguments for CharacterData.insertData");
                        _textNode.InsertData((int)args[0].ToNumber(), args[1].ToString());
                        return FenValue.Undefined;
                    }));
                case "deleteData":
                    return FenValue.FromFunction(new FenFunction("deleteData", (args, thisVal) =>
                    {
                        if (args.Length < 2) throw new FenTypeError("Wrong number of arguments for CharacterData.deleteData");
                        _textNode.DeleteData((int)args[0].ToNumber(), (int)args[1].ToNumber());
                        return FenValue.Undefined;
                    }));
                case "replaceData":
                    return FenValue.FromFunction(new FenFunction("replaceData", (args, thisVal) =>
                    {
                        if (args.Length < 3) throw new FenTypeError("Wrong number of arguments for CharacterData.replaceData");
                        _textNode.ReplaceData((int)args[0].ToNumber(), (int)args[1].ToNumber(), args[2].ToString());
                        return FenValue.Undefined;
                    }));
            }

            return base.Get(key, context);
        }

        public override void Set(string key, FenValue value, IExecutionContext context = null)
        {
            if (key == "data")
            {
                _textNode.Data = value.ToString();
                return;
            }

            base.Set(key, value, context);
        }
    }
}
