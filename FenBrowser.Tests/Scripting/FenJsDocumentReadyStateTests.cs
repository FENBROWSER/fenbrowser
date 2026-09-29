using System;
using System.Threading.Tasks;
using FenBrowser.Core;
using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Scripting;
using Xunit;

namespace FenBrowser.Tests.Scripting
{
    /// <summary>
    /// Regression coverage for the WPT document-lifecycle gate
    /// (html/dom/documents/resource-metadata-management/document-readyState.html):
    /// created documents report their own complete readyState, readystatechange
    /// fires at the interactive and complete transitions, and DOMParser parses
    /// XML into a complete document.
    /// </summary>
    public sealed class FenJsDocumentReadyStateTests
    {
        [Fact]
        public async Task CreatedDocuments_ReportOwnCompleteReadyState()
        {
            var baseUri = new Uri("https://fen.test/ready-state-created");
            var document = new HtmlParser("<html><body></body></html>", baseUri).Parse();
            var engine = new FenJsBrowserScriptEngine(CreateHost())
            {
                Sandbox = SandboxPolicy.AllowAll
            };

            await engine.SetDomAsync(document.DocumentElement, baseUri);

            var result = engine.Evaluate(
                """
                [
                    document.implementation.createHTMLDocument().readyState,
                    document.implementation.createHTMLDocument('t').readyState,
                    document.implementation.createDocument('http://www.w3.org/1999/xhtml', 'html', null).readyState,
                    document.readyState
                ].join('|')
                """);

            Assert.Equal("complete|complete|complete|complete", result?.ToString());
        }

        [Fact]
        public async Task Readystatechange_FiresInteractiveThenCompleteInOrder()
        {
            var baseUri = new Uri("https://fen.test/ready-state-events");
            var document = new HtmlParser(
                "<html><body><script>" +
                "window.__states = [document.readyState];" +
                "document.onreadystatechange = function () {" +
                "  window.__states.push(document.readyState);" +
                "};" +
                "</script></body></html>",
                baseUri).Parse();
            var engine = new FenJsBrowserScriptEngine(CreateHost())
            {
                Sandbox = SandboxPolicy.AllowAll
            };

            await engine.SetDomAsync(document.DocumentElement, baseUri);

            Assert.Equal("loading,interactive,complete", engine.Evaluate("window.__states.join(',')")?.ToString());
        }

        [Fact]
        public void XmlDomParser_ParsesWellFormedXmlIntoCompleteDocument()
        {
            var parsed = XmlDomParser.Parse("<html><head><title>t</title></head><body><p>hi</p></body></html>");

            Assert.Equal(DocumentReadyState.Complete, parsed.ReadyState);
            Assert.Equal("application/xml", parsed.ContentType);
            Assert.Equal("html", parsed.DocumentElement.LocalName);
            Assert.Equal("t", parsed.GetElementsByTagName("title")[0].TextContent);
        }

        [Fact]
        public void XmlDomParser_PreservesSvgXhtmlElementsAndNamespacedAttributes()
        {
            const string xml = "<?xml version='1.0' encoding='UTF-8'?>\n" +
                "<svg xmlns='http://www.w3.org/2000/svg' " +
                "xmlns:h='http://www.w3.org/1999/xhtml' " +
                "xmlns:xlink='http://www.w3.org/1999/xlink'>" +
                "<h:script src='/resources/testharness.js'/>" +
                "<use xlink:href='#shape'/></svg>\n";

            var parsed = XmlDomParser.Parse(xml, "image/svg+xml");
            var root = parsed.DocumentElement;
            var script = Assert.IsType<Element>(root.FirstChild);
            var use = Assert.IsType<Element>(script.NextSibling);

            Assert.Equal("svg", root.LocalName);
            Assert.Equal("http://www.w3.org/2000/svg", root.NamespaceUri);
            Assert.Equal("script", script.LocalName);
            // tagName is the qualified name (DOM §4.9); its case follows the document type.
            Assert.Equal("h:script", script.TagName, ignoreCase: true);
            Assert.Equal("h", script.Prefix);
            Assert.Equal("http://www.w3.org/1999/xhtml", script.NamespaceUri);
            Assert.Equal("#shape", use.GetAttributeNS("http://www.w3.org/1999/xlink", "href"));
            Assert.Equal("xlink", use.Attributes.GetNamedItemNS(
                "http://www.w3.org/1999/xlink", "href").Prefix);
        }

        [Theory]
        [InlineData("image/svg+xml")]
        [InlineData(" image/svg+xml; charset=utf-8 ")]
        [InlineData("application/xhtml+xml; charset=UTF-8")]
        public void XmlDomParser_RecognizesParameterizedXmlMimeTypes(string contentType)
        {
            Assert.True(XmlDomParser.IsXmlMimeType(contentType));

            var parsed = XmlDomParser.ParseWithErrorDocument("<svg/>", contentType);
            Assert.Equal(contentType, parsed.ContentType);
        }

        [Fact]
        public void XmlDomParser_MalformedXml_YieldsParserErrorDocument()
        {
            var parsed = XmlDomParser.ParseWithErrorDocument("<html><p>unclosed");

            Assert.Equal(DocumentReadyState.Complete, parsed.ReadyState);
            Assert.Equal("parsererror", parsed.DocumentElement?.LocalName);
        }

        private static JsHostAdapter CreateHost()
        {
            return new JsHostAdapter(
                navigate: _ => { },
                post: (_, __) => { },
                status: _ => { },
                log: _ => { });
        }
    }
}
