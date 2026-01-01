namespace FenBrowser.Core.Dom
{
    public class Document : Element
    {
        public override NodeType NodeType => NodeType.Document;
        public override string NodeName => "#document";

        // Note: ActiveElement is inherited from Element base class
        
        // Root element shortcut (usually the HTML element)
        public Element DocumentElement 
        {
            get
            {
                foreach(var child in Children)
                {
                    if (child is Element el && el.TagName == "HTML") return el;
                }
                // Fallback if no HTML tag found (shouldn't happen in valid parse)
                return Children.Count > 0 ? Children[0] as Element : null;
            }
        }

        public Document() : base("#document")
        {
            OwnerDocument = this;
        }


        public Element CreateElement(string tagName)
        {
            var el = new Element(tagName);
            el.OwnerDocument = this;
            return el;
        }

        public Text CreateTextNode(string data)
        {
            var t = new Text(data);
            t.OwnerDocument = this;
            return t;
        }

        public Comment CreateComment(string data)
        {
            var c = new Comment(data);
            c.OwnerDocument = this;
            return c;
        }
        
        public DocumentFragment CreateDocumentFragment()
        {
            var df = new DocumentFragment();
            df.OwnerDocument = this;
            return df;
        }

        public DocumentType CreateDocumentType(string name, string publicId = null, string systemId = null)
        {
            var dt = new DocumentType(name, publicId, systemId);
            dt.OwnerDocument = this;
            return dt;
        }


        public QuirksMode Mode { get; set; } = QuirksMode.Quirks;

        public override Node CloneNode(bool deep)
        {
            var doc = new Document();
            doc.Mode = this.Mode;
            // TODO: Copy other properties like baseURI, encoding, etc if we added them
            
            if (deep)
            {
                foreach (var child in Children)
                {
                    doc.AppendChild(child.CloneNode(true));
                }
            }
            return doc;
        }
    }

    public enum QuirksMode
    {
        NoQuirks,
        Quirks,
        LimitedQuirks
    }
}
