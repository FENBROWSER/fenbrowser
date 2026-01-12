using System.Collections.Generic;

namespace FenBrowser.Core.Dom
{
    public class Document : Element
    {
        public override NodeType NodeType => NodeType.Document;
        public override string NodeName => "#document";

        // Top Layer elements (e.g. <dialog> opened with showModal)
        // See HTML Spec: https://html.spec.whatwg.org/multipage/infrastructure.html#top-layer
        public List<Element> TopLayer { get; } = new List<Element>();

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

        // --- Final Architecture: Orchestrator Hook ---
        public event System.Action OnTreeDirty;
        
        /// <summary>
        /// Called when any node in the tree is marked dirty. 
        /// Signals the EngineLoop to schedule a frame update.
        /// </summary>
        public void NotifyTreeDirty()
        {
            OnTreeDirty?.Invoke();
        }

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

        public void DumpTree()
        {
            if (!FenBrowser.Core.Logging.DebugConfig.LogDomTree) return;
            Console.WriteLine("=== DOM TREE DUMP ===");
            DumpNode(this, 0);
            Console.WriteLine("=====================");
        }

        private void DumpNode(Element node, int depth)
        {
            var indent = new string(' ', depth * 2);
            var cls = node.GetAttribute("class") ?? "";
            var id = node.Id ?? "";
            
            if (FenBrowser.Core.Logging.DebugConfig.ShouldLog(cls) || node.Tag == "body")
            {
                 Console.WriteLine($"[DOM] {indent}{node.Tag}#{id}.{cls}");
            }
            
            foreach (var child in node.Children)
                if (child is Element e) DumpNode(e, depth + 1);
        }
    }

    public enum QuirksMode
    {
        NoQuirks,
        Quirks,
        LimitedQuirks
    }
}
