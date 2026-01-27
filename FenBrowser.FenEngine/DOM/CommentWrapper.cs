using FenBrowser.Core.Dom;
using FenBrowser.FenEngine.Core;
using FenBrowser.FenEngine.Core.Interfaces;

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
