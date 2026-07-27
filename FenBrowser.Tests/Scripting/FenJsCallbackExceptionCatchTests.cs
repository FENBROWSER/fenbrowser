using FenBrowser.Core;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Scripting;

namespace FenBrowser.Tests.Scripting;

public sealed class FenJsCallbackExceptionCatchTests
{
    [Fact]
    public async Task TimerCanCatchExceptionThrownThroughFunctionCall()
    {
        var baseUri = new Uri("https://fixture.test/callback-catch.html");
        var document = new HtmlParser(
            "<html><body><script>" +
            "setTimeout(function(){" +
            "var caught=false;" +
            "try{(function(){throw new Error('expected');}).call({});}catch(e){caught=e.message==='expected';}" +
            "globalThis.__callbackCatchResult=caught;" +
            "},1);" +
            "</script></body></html>",
            baseUri).Parse();
        var engine = new FenJsBrowserScriptEngine(CreateHost())
        {
            Sandbox = SandboxPolicy.AllowAll
        };

        await engine.SetDomAsync(document.DocumentElement, baseUri);

        Assert.True(SpinWait.SpinUntil(
            () => engine.GetEventLoopSnapshot().TimersExecuted > 0,
            millisecondsTimeout: 1000));
        Assert.Equal("true", engine.Evaluate("String(globalThis.__callbackCatchResult)")?.ToString());
        Assert.Equal(0, engine.GetEventLoopSnapshot().CallbackFailures);
    }

    [Fact]
    public async Task IndexedDbSuccessListenerExceptionContinuesDispatchAndAbortsTransaction()
    {
        var baseUri = new Uri("https://fixture.test/indexeddb-listener-exception.html");
        var document = new HtmlParser(
            "<html><body><script>" +
            "globalThis.__idbAbortResult='pending';" +
            "var open=indexedDB.open('listener-exception',1);" +
            "open.onupgradeneeded=function(){open.result.createObjectStore('store');};" +
            "open.onsuccess=function(){" +
            "var secondCalled=false;" +
            "var tx=open.result.transaction('store','readonly');" +
            "tx.oncomplete=function(){globalThis.__idbAbortResult='completed';};" +
            "tx.onabort=function(){globalThis.__idbAbortResult=String(secondCalled)+':'+tx.error.name;};" +
            "var request=tx.objectStore('store').get(0);" +
            "request.addEventListener('success',function(){throw new Error('expected');});" +
            "request.addEventListener('success',function(){secondCalled=true;});" +
            "};" +
            "</script></body></html>",
            baseUri).Parse();
        var engine = new FenJsBrowserScriptEngine(CreateHost())
        {
            Sandbox = SandboxPolicy.AllowAll
        };

        await engine.SetDomAsync(document.DocumentElement, baseUri);

        Assert.True(SpinWait.SpinUntil(
            () => string.Equals(
                engine.Evaluate("String(globalThis.__idbAbortResult)")?.ToString(),
                "true:AbortError",
                StringComparison.Ordinal),
            millisecondsTimeout: 1000));
        Assert.Equal(0, engine.GetEventLoopSnapshot().CallbackFailures);
    }

    private static JsHostAdapter CreateHost()
        => new(
            navigate: _ => { },
            post: (_, _) => { },
            status: _ => { },
            log: _ => { });
}
