namespace FenBrowser.Core.Dom.V2
{
    /// <summary>
    /// HTML template element whose inert contents live in a separate document fragment.
    /// </summary>
    public sealed class HtmlTemplateElement : Element
    {
        public HtmlTemplateElement(Document owner = null)
            : base("template", owner, Namespaces.Html)
        {
            Content = new DocumentFragment(owner?.GetTemplateContentsOwnerDocument());
        }

        public DocumentFragment Content { get; }
    }
}
