using System;
using System.Threading;
using System.Threading.Tasks;
using FenBrowser.Core;
using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Scripting;
using Xunit;

namespace FenBrowser.Tests.Scripting;

public sealed class IFrameInterpreterIsolationTests
{
    [Fact]
    public async Task AttachedFrame_ExecutesInIndependentInterpreterAndPublishesWindowState()
    {
        var parentUri = new Uri("https://same.test/page");
        var parentDocument = new HtmlParser(
            "<html><body><iframe id='child' name='challenge-frame' src='/frame'></iframe>" +
            "<script>Array.prototype.parentRealmMark='parent';</script></body></html>",
            parentUri).Parse();
        var engine = CreateEngine();
        await engine.SetDomAsync(parentDocument.DocumentElement, parentUri);

        var childUri = new Uri("https://same.test/frame");
        var childDocument = new HtmlParser(
            "<html><body><script>" +
            "Array.prototype.childRealmMark='child';" +
            "window.__realmShape=[typeof Array.prototype.parentRealmMark,window!==parent," +
            "window===self,window.document===document,parent.document.body!==document.body," +
            "window.name,parent.frames[window.name]===window].join('|');" +
            "</script></body></html>",
            childUri).Parse();
        var frame = Assert.IsType<Element>(parentDocument.GetElementById("child"));
        frame.AppendChild(childDocument);

        await engine.SetSubdocumentDomAsync(childDocument.DocumentElement, childUri);

        Assert.Equal(
            "undefined|true|true|true|true|challenge-frame|true",
            engine.Evaluate("String(document.getElementById('child').contentWindow.__realmShape)")?.ToString());
        Assert.Equal("undefined", engine.Evaluate("typeof Array.prototype.childRealmMark")?.ToString());
        Assert.Equal("parent", engine.Evaluate("String(Array.prototype.parentRealmMark)")?.ToString());
    }

    [Fact]
    public async Task AttachedFrame_PostMessageStructuredDataCrossesRealmBoundary()
    {
        var parentUri = new Uri("https://parent.test/page");
        var parentDocument = new HtmlParser(
            "<html><body><iframe id='child' src='https://child.test/frame'></iframe>" +
            "<script>addEventListener('message',function(e){window.__realmMessage=e.data.kind+'|'+e.data.value+'|'+e.origin;});</script>" +
            "</body></html>",
            parentUri).Parse();
        var engine = CreateEngine();
        await engine.SetDomAsync(parentDocument.DocumentElement, parentUri);

        var childUri = new Uri("https://child.test/frame");
        var childDocument = new HtmlParser(
            "<html><body><script>window.__childSecret='private';" +
            "parent.postMessage({kind:'ready',value:7},'https://parent.test');</script></body></html>",
            childUri).Parse();
        var frame = Assert.IsType<Element>(parentDocument.GetElementById("child"));
        frame.AppendChild(childDocument);

        await engine.SetSubdocumentDomAsync(childDocument.DocumentElement, childUri);
        await WaitForValueAsync(engine, "String(globalThis.__realmMessage || '')", "ready|7|https://child.test");

        Assert.Equal(
            "ready|7|https://child.test",
            engine.Evaluate("String(globalThis.__realmMessage)")?.ToString());
        Assert.Equal("undefined", engine.Evaluate("typeof globalThis.__childSecret")?.ToString());
    }

    [Fact]
    public async Task AttachedFrame_RoutesElementEventsToOwningInterpreter()
    {
        var parentUri = new Uri("https://same.test/page");
        var parentDocument = new HtmlParser(
            "<html><body><iframe id='child' src='/frame'></iframe></body></html>",
            parentUri).Parse();
        var engine = CreateEngine();
        await engine.SetDomAsync(parentDocument.DocumentElement, parentUri);
        var childUri = new Uri("https://same.test/frame");
        var childDocument = new HtmlParser(
            "<html><body><button id='send'>Send</button><script>" +
            "document.getElementById('send').addEventListener('click',function(e){window.__clicked='yes';e.preventDefault();});" +
            "</script></body></html>",
            childUri).Parse();
        var frame = Assert.IsType<Element>(parentDocument.GetElementById("child"));
        frame.AppendChild(childDocument);
        await engine.SetSubdocumentDomAsync(childDocument.DocumentElement, childUri);

        Assert.Equal("function", engine.EvaluateInSubdocumentForTest(
            childDocument,
            "typeof document.getElementById('send').addEventListener")?.ToString());
        Assert.False(engine.DispatchEventForElement(childDocument.GetElementById("send"), "click"));
        Assert.Equal("yes", engine.EvaluateInSubdocumentForTest(childDocument, "String(window.__clicked)")?.ToString());
        Assert.Equal(
            "yes",
            engine.Evaluate("String(document.getElementById('child').contentWindow.__clicked)")?.ToString());
    }

    [Fact]
    public async Task ParentNavigation_ReleasesDetachedFrameTimers()
    {
        var renderRequests = 0;
        var parentUri = new Uri("https://same.test/page");
        var parentDocument = new HtmlParser(
            "<html><body><iframe id='child' src='/frame'></iframe></body></html>",
            parentUri).Parse();
        var engine = CreateEngine();
        engine.RequestRender = () => Interlocked.Increment(ref renderRequests);
        await engine.SetDomAsync(parentDocument.DocumentElement, parentUri);

        var childUri = new Uri("https://same.test/frame");
        var childDocument = new HtmlParser(
            "<html><body><script>setInterval(function(){window.__ticks=(window.__ticks||0)+1;},4);</script></body></html>",
            childUri).Parse();
        var frame = Assert.IsType<Element>(parentDocument.GetElementById("child"));
        frame.AppendChild(childDocument);
        await engine.SetSubdocumentDomAsync(childDocument.DocumentElement, childUri);

        var deadline = DateTime.UtcNow.AddSeconds(1);
        while (Volatile.Read(ref renderRequests) < 3 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }
        Assert.True(Volatile.Read(ref renderRequests) >= 3);

        var replacementDocument = new HtmlParser("<html><body>replacement</body></html>", parentUri).Parse();
        await engine.SetDomAsync(replacementDocument.DocumentElement, parentUri);
        await Task.Delay(50);
        var settledRenderRequests = Volatile.Read(ref renderRequests);
        await Task.Delay(100);

        Assert.Equal(settledRenderRequests, Volatile.Read(ref renderRequests));
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
