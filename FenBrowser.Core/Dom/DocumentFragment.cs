namespace FenBrowser.Core.Dom
{
    public class DocumentFragment : Node
    {
        public override NodeType NodeType => NodeType.DocumentFragment;
        public override string NodeName => "#document-fragment";

        public override Node CloneNode(bool deep)
        {
            var fragment = new DocumentFragment();
            if (deep)
            {
                foreach (var child in Children)
                {
                    fragment.AppendChild(child.CloneNode(true));
                }
            }
            return fragment;
        }
    }
}
