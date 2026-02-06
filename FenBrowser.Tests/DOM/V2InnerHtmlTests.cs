using FenBrowser.Core.Dom.V2;
using Xunit;

namespace FenBrowser.Tests.Dom
{
    public class V2InnerHtmlTests
    {
        [Fact]
        public void Element_InnerHtml_ParsesMarkupIntoChildNodes()
        {
            var doc = Document.CreateHtmlDocument();
            var host = doc.CreateElement("div");
            doc.Body.AppendChild(host);

            host.InnerHTML = "<span id='x'>Hello <b>world</b></span><!--note-->";

            var span = host.FirstElementChild;
            Assert.NotNull(span);
            Assert.Equal("span", span.LocalName);
            Assert.Equal("x", span.GetAttribute("id"));
            Assert.Equal("Hello world", span.TextContent);
            Assert.Equal("Hello world", host.TextContent);
        }

        [Fact]
        public void ShadowRoot_InnerHtml_ParsesMarkupIntoShadowTree()
        {
            var doc = Document.CreateHtmlDocument();
            var host = doc.CreateElement("div");
            doc.Body.AppendChild(host);

            var shadow = host.AttachShadow(new ShadowRootInit
            {
                Mode = ShadowRootMode.Open,
                DelegatesFocus = false,
                SlotAssignment = SlotAssignmentMode.Named
            });

            shadow.InnerHTML = "<section><p>Shadow text</p></section>";

            Assert.Equal("section", shadow.FirstElementChild?.LocalName);
            Assert.Equal("Shadow text", shadow.TextContent);
        }
    }
}
