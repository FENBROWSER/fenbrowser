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

    [Fact]
    public async Task CrossOriginFrame_TransfersMessagePortsBetweenOwningRealms()
    {
        var parentUri = new Uri("https://parent.test/page");
        var parentDocument = new HtmlParser(
            "<html><body><iframe id='child' src='https://child.test/frame'></iframe><script>" +
            "addEventListener('message',function(event){" +
            "window.__portCount=event.ports.length;" +
            "var response=new MessageChannel();" +
            "response.port1.onmessage=function(reply){window.__nestedReply=reply.data;};" +
            "event.ports[0].onmessage=function(reply){window.__directReply=reply.data;};" +
            "event.ports[0].postMessage('parent-ack',[response.port2]);" +
            "});</script></body></html>",
            parentUri).Parse();
        var engine = CreateEngine();
        await engine.SetDomAsync(parentDocument.DocumentElement, parentUri);

        var childUri = new Uri("https://child.test/frame");
        var childDocument = new HtmlParser(
            "<html><body><script>" +
            "var channel=new MessageChannel();window.__channel=channel;" +
            "channel.port1.onmessage=function(event){" +
            "window.__childReply=event.data+'|'+event.ports.length+'|'+String(event.source===null);" +
            "event.ports[0].postMessage('frame-ack');this.postMessage('frame-direct');};" +
            "parent.postMessage('handshake','https://parent.test',[channel.port2]);" +
            "</script></body></html>",
            childUri).Parse();
        var frame = Assert.IsType<Element>(parentDocument.GetElementById("child"));
        frame.AppendChild(childDocument);
        await engine.SetSubdocumentDomAsync(childDocument.DocumentElement, childUri);

        await WaitForValueAsync(engine, "String(globalThis.__nestedReply || '')", "frame-ack");

        Assert.Equal("1", engine.Evaluate("String(globalThis.__portCount)")?.ToString());
        Assert.Equal("frame-ack", engine.Evaluate("String(globalThis.__nestedReply)")?.ToString());
        Assert.Equal("frame-direct", engine.Evaluate("String(globalThis.__directReply)")?.ToString());
        Assert.Equal(
            "parent-ack|1|true",
            engine.EvaluateInSubdocumentForTest(childDocument, "String(window.__childReply)")?.ToString());
    }

    [Fact]
    public async Task CrossOriginFrame_PreservesQueuedMessageOrderInOwningRealm()
    {
        var parentUri = new Uri("https://parent.test/page");
        var parentDocument = new HtmlParser(
            "<html><body><iframe id='child' src='https://child.test/frame'></iframe></body></html>",
            parentUri).Parse();
        var engine = CreateEngine();
        await engine.SetDomAsync(parentDocument.DocumentElement, parentUri);

        var childUri = new Uri("https://child.test/frame");
        var childDocument = new HtmlParser(
            "<html><body><script>window.__order=[];" +
            "addEventListener('message',function(event){window.__order.push(event.data);});" +
            "</script></body></html>",
            childUri).Parse();
        var frame = Assert.IsType<Element>(parentDocument.GetElementById("child"));
        frame.AppendChild(childDocument);
        await engine.SetSubdocumentDomAsync(childDocument.DocumentElement, childUri);

        engine.Evaluate(
            "var target=document.getElementById('child').contentWindow;" +
            "for(var i=0;i<20;i++)target.postMessage(i,'https://child.test');");

        var deadline = DateTime.UtcNow.AddSeconds(1);
        while (DateTime.UtcNow < deadline &&
               engine.EvaluateInSubdocumentForTest(childDocument, "window.__order.length")?.ToString() != "20")
        {
            await Task.Delay(25);
        }

        Assert.Equal(
            "0,1,2,3,4,5,6,7,8,9,10,11,12,13,14,15,16,17,18,19",
            engine.EvaluateInSubdocumentForTest(childDocument, "window.__order.join(',')")?.ToString());
    }

    [Fact]
    public async Task FrameLocationNavigation_ReloadsFrameWithoutNavigatingTopLevel()
    {
        Uri topLevelNavigation = null;
        var host = new JsHostAdapter(
            navigate: uri => topLevelNavigation = uri,
            post: (_, _) => { },
            status: _ => { },
            log: _ => { });
        var engine = new FenJsBrowserScriptEngine(host) { Sandbox = SandboxPolicy.AllowAll };
        var parentUri = new Uri("https://parent.test/page");
        var parentDocument = new HtmlParser(
            "<html><body><iframe id='child' src='https://child.test/frame'></iframe></body></html>",
            parentUri).Parse();
        await engine.SetDomAsync(parentDocument.DocumentElement, parentUri);

        var navigation = new TaskCompletionSource<(Element Frame, Uri Uri)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        engine.FrameElementLoader = (frame, uri) =>
        {
            navigation.TrySetResult((frame, uri));
            return Task.CompletedTask;
        };
        var childUri = new Uri("https://child.test/frame");
        var childDocument = new HtmlParser(
            "<html><body><script>location.assign('/next');</script></body></html>",
            childUri).Parse();
        var frameElement = Assert.IsType<Element>(parentDocument.GetElementById("child"));
        frameElement.AppendChild(childDocument);

        await engine.SetSubdocumentDomAsync(childDocument.DocumentElement, childUri);
        var completed = await Task.WhenAny(navigation.Task, Task.Delay(1000));

        Assert.Same(navigation.Task, completed);
        var frameNavigation = await navigation.Task;
        Assert.Same(frameElement, frameNavigation.Frame);
        Assert.Equal("https://child.test/next", frameNavigation.Uri.AbsoluteUri);
        Assert.Equal("https://child.test/next", frameElement.GetAttribute("src"));
        Assert.Null(topLevelNavigation);
    }

    [Fact]
    public async Task FrameFormSubmission_NavigatesOwningFrameWithoutTouchingTopLevel()
    {
        Uri topLevelNavigation = null;
        var host = new JsHostAdapter(
            navigate: uri => topLevelNavigation = uri,
            post: (_, _) => { },
            status: _ => { },
            log: _ => { });
        var engine = new FenJsBrowserScriptEngine(host) { Sandbox = SandboxPolicy.AllowAll };
        var parentUri = new Uri("https://parent.test/page");
        var parentDocument = new HtmlParser(
            "<html><body><iframe id='child' src='https://child.test/frame'></iframe></body></html>",
            parentUri).Parse();
        await engine.SetDomAsync(parentDocument.DocumentElement, parentUri);

        var navigation = new TaskCompletionSource<(Element Frame, Uri Uri)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        engine.FrameElementLoader = (frame, uri) =>
        {
            navigation.TrySetResult((frame, uri));
            return Task.CompletedTask;
        };
        var childUri = new Uri("https://child.test/frame");
        var childDocument = new HtmlParser(
            "<html><body><form id='challenge' action='/verify'><input name='token' value='abc 123'></form>" +
            "<script>document.getElementById('challenge').submit();</script></body></html>",
            childUri).Parse();
        var frameElement = Assert.IsType<Element>(parentDocument.GetElementById("child"));
        frameElement.AppendChild(childDocument);

        await engine.SetSubdocumentDomAsync(childDocument.DocumentElement, childUri);
        var completed = await Task.WhenAny(navigation.Task, Task.Delay(1000));

        Assert.Same(navigation.Task, completed);
        var frameNavigation = await navigation.Task;
        Assert.Same(frameElement, frameNavigation.Frame);
        Assert.Equal("https://child.test/verify?token=abc%20123", frameNavigation.Uri.AbsoluteUri);
        Assert.Null(topLevelNavigation);
    }

    [Fact]
    public async Task FrameResize_UpdatesOwnedViewportAndDispatchesResize()
    {
        var parentUri = new Uri("https://same.test/page");
        var parentDocument = new HtmlParser(
            "<html><body><iframe id='child' src='/frame' style='width:300px;height:150px'></iframe></body></html>",
            parentUri).Parse();
        var engine = CreateEngine();
        await engine.SetDomAsync(parentDocument.DocumentElement, parentUri);

        var childUri = new Uri("https://same.test/frame");
        var childDocument = new HtmlParser(
            "<html><body><script>window.__resizeCount=0;" +
            "addEventListener('resize',function(){window.__resizeCount++;" +
            "window.__resizeShape=innerWidth+'x'+innerHeight;});</script></body></html>",
            childUri).Parse();
        var frame = Assert.IsType<Element>(parentDocument.GetElementById("child"));
        frame.AppendChild(childDocument);
        await engine.SetSubdocumentDomAsync(childDocument.DocumentElement, childUri);

        engine.Evaluate(
            "var frame=document.getElementById('child');" +
            "frame.style.width='640px';frame.style.height='420px';");

        var deadline = DateTime.UtcNow.AddSeconds(1);
        while (DateTime.UtcNow < deadline &&
               engine.EvaluateInSubdocumentForTest(childDocument, "String(window.__resizeShape || '')")?.ToString() != "640x420")
        {
            await Task.Delay(25);
        }

        Assert.Equal("640", engine.EvaluateInSubdocumentForTest(childDocument, "String(innerWidth)")?.ToString());
        Assert.Equal("420", engine.EvaluateInSubdocumentForTest(childDocument, "String(innerHeight)")?.ToString());
        Assert.Equal("640x420", engine.EvaluateInSubdocumentForTest(childDocument, "String(window.__resizeShape)")?.ToString());
        Assert.Equal(
            "640x420",
            engine.Evaluate(
                "var frame=document.getElementById('child').contentWindow;" +
                "String(frame.innerWidth)+'x'+String(frame.innerHeight)")?.ToString());
    }

    [Fact]
    public async Task FrameScroll_UsesOwnedRendererStateAndDispatchesScroll()
    {
        var parentUri = new Uri("https://same.test/page");
        var parentDocument = new HtmlParser(
            "<html><body><iframe id='child' src='/frame'></iframe>" +
            "<script>window.__topMarker='unchanged';</script></body></html>",
            parentUri).Parse();
        var engine = CreateEngine();
        var scrollX = 4d;
        var scrollY = 8d;
        Element writtenFrame = null;
        engine.FrameScrollReader = _ => (scrollX, scrollY);
        engine.FrameScrollWriter = (frame, x, y) =>
        {
            writtenFrame = frame;
            scrollX = Math.Min(500, Math.Max(0, x));
            scrollY = Math.Min(500, Math.Max(0, y));
        };
        await engine.SetDomAsync(parentDocument.DocumentElement, parentUri);

        var childUri = new Uri("https://same.test/frame");
        var childDocument = new HtmlParser(
            "<html><body><script>window.__scrollCount=0;" +
            "addEventListener('scroll',function(){window.__scrollCount++;});</script></body></html>",
            childUri).Parse();
        var frame = Assert.IsType<Element>(parentDocument.GetElementById("child"));
        frame.AppendChild(childDocument);
        await engine.SetSubdocumentDomAsync(childDocument.DocumentElement, childUri);

        engine.EvaluateInSubdocumentForTest(
            childDocument,
            "scrollTo({left:10,top:120});scrollBy(5,30);");
        await WaitForValueAsync(
            engine,
            "String(document.getElementById('child').contentWindow.__scrollCount || 0)",
            "2");

        Assert.Same(frame, writtenFrame);
        Assert.Equal(
            "15|150|15|150",
            engine.EvaluateInSubdocumentForTest(
                childDocument,
                "[scrollX,scrollY,pageXOffset,pageYOffset].join('|')")?.ToString());
        Assert.Equal("unchanged", engine.Evaluate("String(window.__topMarker)")?.ToString());

        scrollX = 22;
        scrollY = 240;
        engine.NotifyFrameScrollChanged(frame);
        await WaitForValueAsync(
            engine,
            "String(document.getElementById('child').contentWindow.__scrollCount || 0)",
            "3");

        Assert.Equal(
            "22|240",
            engine.EvaluateInSubdocumentForTest(childDocument, "scrollX+'|'+scrollY")?.ToString());
        Assert.Equal(
            "22|240",
            engine.Evaluate(
                "var child=document.getElementById('child').contentWindow;child.scrollX+'|'+child.scrollY")?.ToString());
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
