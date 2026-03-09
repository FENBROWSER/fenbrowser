using FenBrowser.Core.Dom.V2;
using FenBrowser.FenEngine.Core;
using FenBrowser.FenEngine.Core.Interfaces;

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
                    return FenValue.FromNumber((_textNode.Data ?? "").Length);

                case "wholeText":
                    // DOM Living Standard §Text.wholeText — concatenate contiguous Text siblings.
                    return FenValue.FromString(_textNode.WholeText);

                case "splitText":
                    // DOM Living Standard §Text.splitText(offset).
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
