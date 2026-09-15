using System;
using System.Threading.Tasks;
using FenBrowser.Core;
using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Rendering;
using FenBrowser.FenEngine.Scripting;
using Xunit;

namespace FenBrowser.Tests.Scripting;

/// <summary>
/// HTML 7.4.x "page load processing model for XML files": a frame whose
/// response is an XML type is parsed by the XML parser, a well-formedness
/// error leaves an error document with no scripts, and a script element runs
/// only in the HTML or SVG namespace. Every frame was parsed as HTML, so Acid3
/// test 80 saw the scripts of a malformed file and of a wrong-namespace file run.
/// </summary>
public sealed class FrameXmlDocumentTests
{
    private const string XhtmlWithScript =
        "<html xmlns=\"http://www.w3.org/1999/xhtml\"><head><title>Test</title></head>" +
        "<body><p>ok</p><script type=\"text/javascript\">globalThis.__ran = 'yes';</script></body></html>";

    private static readonly Uri FrameUri = new("https://example.test/xhtml.1");

    [Fact]
    public void XmlResponseIsParsedAsXml()
    {
        var document = BrowserHost.ParseFrameDocument(XhtmlWithScript, "text/xml", FrameUri);

        Assert.Equal("text/xml", document.ContentType);
        Assert.Equal(Namespaces.Html, document.DocumentElement.NamespaceUri);
        Assert.Equal(FrameUri.AbsoluteUri, document.URL);
        Assert.Equal(FrameUri.AbsoluteUri, document.BaseURI);
    }

    [Theory]
    [InlineData("application/xml;charset=utf-8")]
    [InlineData("image/svg+xml")]
    [InlineData("APPLICATION/XHTML+XML")]
    public void ParametersAndCaseDoNotHideAnXmlType(string contentType)
    {
        var document = BrowserHost.ParseFrameDocument(XhtmlWithScript, contentType, FrameUri);

        Assert.NotEqual("text/html", document.ContentType);
        Assert.Equal(Namespaces.Html, document.DocumentElement.NamespaceUri);
    }

    [Fact]
    public void MalformedXmlBecomesAnErrorDocument()
    {
        var document = BrowserHost.ParseFrameDocument(
            "<html xmlns=\"http://www.w3.org/1999/xhtml\"><body><p> <strong/> Parsing Test </strong> </p>" +
            "<script>globalThis.__ran = 'yes';</script></body></html>",
            "text/xml",
            FrameUri);

        Assert.Equal("parsererror", document.DocumentElement.LocalName);
    }

    [Theory]
    [InlineData("text/html")]
    [InlineData(null)]
    public void HtmlOrUnknownResponseStaysHtml(string contentType)
    {
        var document = BrowserHost.ParseFrameDocument("<p>unclosed", contentType, FrameUri);

        Assert.Equal("html", document.DocumentElement.LocalName);
        Assert.NotNull(document.Body);
    }

    [Fact]
    public async Task ScriptInAnUnknownNamespaceDoesNotRun()
    {
        var document = XmlDomParser.Parse(XhtmlWithScript.Replace("1999/xhtml\"", "1999/xhtml#\""), "text/xml");

        var engine = await CreateEngineAsync(document);

        Assert.Equal("undefined", engine.Evaluate("typeof globalThis.__ran")?.ToString());
    }

    [Fact]
    public async Task ScriptInAnXhtmlDocumentRuns()
    {
        var document = XmlDomParser.Parse(XhtmlWithScript, "text/xml");

        var engine = await CreateEngineAsync(document);

        Assert.Equal("yes", engine.Evaluate("String(globalThis.__ran)")?.ToString());
    }

    [Fact]
    public async Task SvgScriptInAnHtmlPageStillRuns()
    {
        var document = new HtmlParser(
            "<html><body><svg><script>globalThis.__svgRan = 'yes';</script></svg></body></html>",
            FrameUri).Parse();

        var engine = await CreateEngineAsync(document);

        Assert.Equal("yes", engine.Evaluate("String(globalThis.__svgRan)")?.ToString());
    }

    // Acid3 xhtml.1 end to end, minus the network: the frame document comes out
    // of the XML parser and its script reports back to the page that embeds it.
    [Fact]
    public async Task XhtmlFrameScriptReachesTheParentPage()
    {
        var pageUri = new Uri("https://example.test/test.html");
        var page = new HtmlParser(
            "<html><body><script>var notifications = {}; function notify(file) { notifications[file] = 1; }</script>" +
            "<iframe id='frame' src='https://example.test/xhtml.1'></iframe></body></html>",
            pageUri).Parse();
        var engine = new FenJsBrowserScriptEngine(CreateHost()) { Sandbox = SandboxPolicy.AllowAll };
        await engine.SetDomAsync(page.DocumentElement, pageUri);

        var frameDocument = BrowserHost.ParseFrameDocument(
            "<html xmlns=\"http://www.w3.org/1999/xhtml\">\n <head>\n  <title>Test</title>\n </head>\n <body>\n" +
            "  <p> <strong> XHTML Test </strong> </p>\n  <script type=\"text/javascript\">\n   parent.notify(\"xhtml.1\")\n  </script>\n </body>\n</html>\n",
            "text/xml",
            FrameUri);
        page.GetElementById("frame").AppendChild(frameDocument);
        await engine.SetSubdocumentDomAsync(frameDocument.DocumentElement, FrameUri);

        Assert.Equal("{\"xhtml.1\":1}", engine.Evaluate("JSON.stringify(notifications)")?.ToString());
    }

    private static async Task<FenJsBrowserScriptEngine> CreateEngineAsync(Document document)
    {
        var engine = new FenJsBrowserScriptEngine(CreateHost()) { Sandbox = SandboxPolicy.AllowAll };
        await engine.SetDomAsync(document.DocumentElement, FrameUri);
        return engine;
    }

    private static JsHostAdapter CreateHost() =>
        new(navigate: _ => { }, post: (_, _) => { }, status: _ => { }, log: _ => { });
}
