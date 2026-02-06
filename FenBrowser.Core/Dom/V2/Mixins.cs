// WHATWG DOM Living Standard compliant implementation
// FenBrowser.Core.Dom.V2 - Production-grade DOM

namespace FenBrowser.Core.Dom.V2
{
    /// <summary>
    /// IChildNode interface for WHATWG DOM mixin.
    /// Implemented by DocumentType, Element, CharacterData.
    /// https://dom.spec.whatwg.org/#interface-childnode
    /// </summary>
    public interface IChildNode
    {
        void Before(params Node[] nodes);
        void After(params Node[] nodes);
        void ReplaceWith(params Node[] nodes);
        void Remove();
    }

    /// <summary>
    /// INonDocumentTypeChildNode interface for WHATWG DOM mixin.
    /// Provides element sibling navigation for Element and CharacterData.
    /// https://dom.spec.whatwg.org/#interface-nondocumenttypechildnode
    /// </summary>
    public interface INonDocumentTypeChildNode
    {
        Element PreviousElementSibling { get; }
        Element NextElementSibling { get; }
    }

    /// <summary>
    /// IParentNode interface for WHATWG DOM mixin.
    /// Implemented by Document, DocumentFragment, Element.
    /// https://dom.spec.whatwg.org/#interface-parentnode
    /// </summary>
    public interface IParentNode
    {
        NodeList Children { get; }
        Element FirstElementChild { get; }
        Element LastElementChild { get; }
        int ChildElementCount { get; }
        void Prepend(params Node[] nodes);
        void Append(params Node[] nodes);
        void ReplaceChildren(params Node[] nodes);
        Element QuerySelector(string selectors);
        NodeList QuerySelectorAll(string selectors);
    }
}
