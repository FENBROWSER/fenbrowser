using FenBrowser.Core.Dom;
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
                    return FenValue.FromString(_textNode.Data); // Simplified
                case "splitText":
                    // Todo: Implement SplitText
                    return FenValue.Null; 
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
