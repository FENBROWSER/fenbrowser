using System;
using System.Threading.Tasks;
using FenBrowser.Core;
using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Scripting;
using Xunit;

namespace FenBrowser.Tests.Scripting;

public sealed class IFrameRealmSecurityTests
{
    [Fact]
    public async Task CrossOriginFrame_UsesOpaqueWindowProxiesButCanPostMessages()
    {
        var parentUri = new Uri("https://parent.test/page");
        var parentDocument = new HtmlParser(
            "<html><body><div id='parent-marker'></div><iframe id='child' src='https://child.test/frame'></iframe>" +
            "<script>addEventListener('message',function(e){window.__message=e.data+'|'+e.origin;window.__messageSource=e.source;});</script>" +
            "</body></html>",
            parentUri).Parse();
        var engine = CreateEngine();
        await engine.SetDomAsync(parentDocument.DocumentElement, parentUri);

        var childUri = new Uri("https://child.test/frame");
        var childDocument = new HtmlParser(
            "<html><body id='child-body'><script>" +
            "window.__shape=[window!==parent,window===self,window.document===document,document.body.id," +
            "typeof parent.document,typeof parent.location,typeof parent.postMessage,window.frameElement===null].join('|');" +
            "document.body.setAttribute('data-shape',window.__shape);" +
            "parent.postMessage(window.__shape,'https://parent.test');" +
            "</script></body></html>",
            childUri).Parse();
        var frame = Assert.IsType<Element>(parentDocument.GetElementById("child"));
        frame.AppendChild(childDocument);

        await engine.SetSubdocumentDomAsync(childDocument.DocumentElement, childUri);
        const string expectedShape = "true|true|true|child-body|undefined|undefined|function|true";
        Assert.Equal(expectedShape, childDocument.Body.GetAttribute("data-shape"));
        await WaitForValueAsync(engine, "String(globalThis.__message || '')", expectedShape + "|https://child.test");

        Assert.Equal("true", engine.Evaluate("String(document.getElementById('child').contentDocument===null)")?.ToString());
        Assert.Equal("undefined", engine.Evaluate("typeof document.getElementById('child').contentWindow.document")?.ToString());
        Assert.Equal(expectedShape + "|https://child.test", engine.Evaluate("String(globalThis.__message)")?.ToString());
        Assert.Equal(
            "true",
            engine.Evaluate("String(globalThis.__messageSource===document.getElementById('child').contentWindow)")?.ToString());
    }

    [Fact]
    public async Task SameOriginFrame_ExposesItsIndependentDocumentAndParentDocument()
    {
        var parentUri = new Uri("https://same.test/page");
        var parentDocument = new HtmlParser(
            "<html><body><div id='parent-marker'></div><iframe id='child' src='/frame'></iframe></body></html>",
            parentUri).Parse();
        var engine = CreateEngine();
        await engine.SetDomAsync(parentDocument.DocumentElement, parentUri);

        var childUri = new Uri("https://same.test/frame");
        var childDocument = new HtmlParser(
            "<html><body id='child-body'><script>window.__shape=[window!==parent,window===self," +
            "window.document===document,document.body.id,parent.document.getElementById('parent-marker').id," +
            "window.frameElement.id].join('|');</script></body></html>",
            childUri).Parse();
        var frame = Assert.IsType<Element>(parentDocument.GetElementById("child"));
        frame.AppendChild(childDocument);

        await engine.SetSubdocumentDomAsync(childDocument.DocumentElement, childUri);

        Assert.Equal(
            "true|true|true|child-body|parent-marker|child",
            engine.Evaluate("String(document.getElementById('child').contentWindow.__shape)")?.ToString());
        Assert.Equal("false", engine.Evaluate("String(document.getElementById('child').contentDocument===document)")?.ToString());
    }

    private static FenJsBrowserScriptEngine CreateEngine() => new(CreateHost())
    {
        Sandbox = SandboxPolicy.AllowAll
    };

    private static JsHostAdapter CreateHost() => new(
        navigate: _ => { },
        post: (_, _) => { },
        status: _ => { },
        log: _ => { });

    private static async Task WaitForValueAsync(
        FenJsBrowserScriptEngine engine,
        string expression,
        string expected)
    {
        var deadline = DateTime.UtcNow.AddSeconds(1);
        while (DateTime.UtcNow < deadline)
        {
            if (engine.Evaluate(expression)?.ToString() == expected)
            {
                return;
            }

            await Task.Delay(25);
        }
    }
}
