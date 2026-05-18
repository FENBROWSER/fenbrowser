using System.Linq;
using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Parsing;
using Xunit;

namespace FenBrowser.Tests.Core.Parsing
{
    public class AfterHeadParsingTests
    {
        private static Document Parse(string html)
        {
            var parser = new HtmlParser(html);
            return parser.Parse();
        }

        [Fact]
        public void BasefontAfterHead_IsHandledAsHeadContent_NotBodyContent()
        {
            var doc = Parse("<html><head></head><basefont color='red'><body><p>x</p></body></html>");
            var html = doc.DocumentElement;
            Assert.NotNull(html);

            var head = html.Children.FirstOrDefault(c => (c as Element)?.TagName == "HEAD") as Element;
            var body = html.Children.FirstOrDefault(c => (c as Element)?.TagName == "BODY") as Element;
            Assert.NotNull(head);
            Assert.NotNull(body);

            Assert.Contains(doc.Descendants().OfType<Element>(), e => e.TagName == "BASEFONT");
            Assert.DoesNotContain(body.Descendants().OfType<Element>(), e => e.TagName == "BASEFONT");
        }

        [Fact]
        public void UppercaseBasefontAfterHead_IsHandledAsHeadContent_NotBodyContent()
        {
            var doc = Parse("<HTML><HEAD></HEAD><BASEFONT COLOR='red'><BODY><P>x</P></BODY></HTML>");
            var html = doc.DocumentElement;
            Assert.NotNull(html);

            var body = html.Children.FirstOrDefault(c => (c as Element)?.TagName == "BODY") as Element;
            Assert.NotNull(body);

            Assert.Contains(doc.Descendants().OfType<Element>(), e => e.TagName == "BASEFONT");
            Assert.DoesNotContain(body.Descendants().OfType<Element>(), e => e.TagName == "BASEFONT");
        }

        [Fact]
        public void UppercaseMetaInHead_IsHandledInHead()
        {
            var doc = Parse("<HTML><HEAD><META CHARSET='utf-8'></HEAD><BODY><P>x</P></BODY></HTML>");
            var html = doc.DocumentElement;
            Assert.NotNull(html);

            var head = html.Children.FirstOrDefault(c => (c as Element)?.TagName == "HEAD") as Element;
            var body = html.Children.FirstOrDefault(c => (c as Element)?.TagName == "BODY") as Element;
            Assert.NotNull(head);
            Assert.NotNull(body);

            Assert.Contains(head.Descendants().OfType<Element>(), e => e.TagName == "META");
            Assert.DoesNotContain(body.Descendants().OfType<Element>(), e => e.TagName == "META");
        }

        [Fact]
        public void MixedCaseStartupTags_CreateSingleHtmlAndHead()
        {
            var doc = Parse("<HtMl><HeAd><TiTlE>x</TiTlE></HeAd><BoDy><p>ok</p></BoDy></HtMl>");
            var html = doc.DocumentElement;
            Assert.NotNull(html);
            Assert.Equal("HTML", html.TagName);

            var elementChildren = html.ChildNodes.OfType<Element>().ToList();
            Assert.Single(elementChildren.Where(e => e.TagName == "HEAD"));
            Assert.Single(elementChildren.Where(e => e.TagName == "BODY"));
        }
    }
}
