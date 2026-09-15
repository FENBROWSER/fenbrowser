using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Engine;
using FenBrowser.Core.Parsing;
using Xunit;

namespace FenBrowser.Tests.Core
{
    /// <summary>
    /// The DOM's style-dirty marking has to cover every selector whose result a
    /// mutation can change: a sibling's presence for :first-child/:last-child/
    /// :empty and the + and ~ combinators (Selectors 4 §14-15), and the whole
    /// document for a change to a stylesheet-bearing subtree (CSSOM §6.1).
    /// </summary>
    public class StyleInvalidationTests
    {
        private static Document Parse(string html) => HtmlParser.ParseDocument(html);

        private static void Clean(Node node)
        {
            node.ClearDirty(InvalidationKind.Style);
            foreach (var child in node.ChildNodes)
            {
                Clean(child);
            }
        }

        // Shadow content renders in its host's flat tree (CSS Scoping 1 §2), so a change
        // inside a shadow root has to reach the page root like any other. The walk followed
        // parent nodes, and a shadow root has none: Cloudflare Turnstile's iframe, resized
        // inside a closed shadow root, never got restyled.
        [Theory]
        [InlineData(ShadowRootMode.Open)]
        [InlineData(ShadowRootMode.Closed)]
        public void StyleChangeInsideAShadowTreeMarksTheHostAndTheDocumentRoot(ShadowRootMode mode)
        {
            var doc = Parse("<body><div id='host'></div></body>");
            var host = doc.GetElementById("host");
            var shadowRoot = host.AttachShadow(new ShadowRootInit { Mode = mode });
            var box = doc.CreateElement("div");
            shadowRoot.AppendChild(box);
            Clean(doc);
            Clean(shadowRoot);

            box.SetAttribute("style", "width: 300px; height: 65px");

            Assert.True(box.StyleDirty);
            Assert.True(host.ChildStyleDirty);
            Assert.True(doc.DocumentElement.ChildStyleDirty);
        }

        [Fact]
        public void InsertingAChildMarksTheParentSoSiblingSelectorsAreReevaluated()
        {
            var doc = Parse("<body><p id='a'></p></body>");
            Clean(doc);
            var body = doc.Body;

            body.InsertBefore(doc.CreateElement("p"), body.FirstChild);

            Assert.True(body.StyleDirty);
            Assert.True(doc.DocumentElement.ChildStyleDirty);
        }

        [Fact]
        public void RemovingAChildMarksTheParent()
        {
            var doc = Parse("<body><p></p><p id='b'></p></body>");
            Clean(doc);
            var body = doc.Body;

            body.RemoveChild(body.LastChild);

            Assert.True(body.StyleDirty);
        }

        [Fact]
        public void TextAppearingInAnElementMarksIt()
        {
            var doc = Parse("<body><p id='p'></p></body>");
            var p = doc.GetElementById("p");
            var text = doc.CreateTextNode("");
            p.AppendChild(text);
            Clean(doc);

            text.Data = "x";

            Assert.True(p.StyleDirty);
        }

        [Fact]
        public void EditingStyleElementTextMarksTheDocumentRoot()
        {
            var doc = Parse("<head><style>p{}</style></head><body><p></p></body>");
            Clean(doc);
            var style = doc.GetElementsByTagName("style")[0];

            style.AppendChild(doc.CreateTextNode("p { color: red }"));

            Assert.True(doc.DocumentElement.StyleDirty);
        }

        [Fact]
        public void InsertingAStylesheetLinkMarksTheDocumentRoot()
        {
            var doc = Parse("<head></head><body></body>");
            Clean(doc);
            var link = doc.CreateElement("link");
            link.SetAttribute("rel", "stylesheet");

            doc.Head.AppendChild(link);

            Assert.True(doc.DocumentElement.StyleDirty);
        }

        [Fact]
        public void ChildDirtyPropagatesPastAnAlreadyMarkedAncestor()
        {
            // A style pass clears a subtree it cascaded while a node above it can
            // still be marked; the next mutation below must reach the root anyway.
            var doc = Parse("<body><div id='d'><p id='p'></p></div></body>");
            Clean(doc);
            var div = doc.GetElementById("d");
            var p = doc.GetElementById("p");
            div.MarkDirty(InvalidationKind.Style);
            doc.DocumentElement.ClearDirty(InvalidationKind.Style);
            doc.Body.ClearDirty(InvalidationKind.Style);

            p.MarkDirty(InvalidationKind.Style);

            Assert.True(doc.Body.ChildStyleDirty);
            Assert.True(doc.DocumentElement.ChildStyleDirty);
        }

        [Fact]
        public void StyleMutationSequenceAdvancesEvenWhenTheNodeIsAlreadyDirty()
        {
            var doc = Parse("<body><p id='p'></p></body>");
            var p = doc.GetElementById("p");
            p.MarkDirty(InvalidationKind.Style);
            var before = Node.StyleMutationSequence;

            p.MarkDirty(InvalidationKind.Style);

            Assert.True(Node.StyleMutationSequence > before);
        }
    }
}
