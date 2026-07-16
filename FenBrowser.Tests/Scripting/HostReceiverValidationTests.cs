using FenBrowser.Core;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Scripting;
using FenBrowser.Tests.Logging;

namespace FenBrowser.Tests.Scripting;

[Collection(EngineLogTestCollection.Name)]
public sealed class HostReceiverValidationTests
{
    [Fact]
    public async Task ElementGetAttribute_UsesCallReceiverAndRejectsNonElementReceiver()
    {
        // Web IDL operation invocation requires the receiver to implement the
        // operation's interface; otherwise the binding must throw TypeError.
        var baseUri = new Uri("https://fixture.test/host-receiver.html");
        try
        {
            BrowserScriptEngineRuntime.Reset();
            var document = new HtmlParser(
                "<html><body><div id='first'></div><div id='second'></div></body></html>",
                baseUri).Parse();
            var engine = new FenJsBrowserScriptEngine(CreateHost())
            {
                Sandbox = SandboxPolicy.AllowAll
            };

            await engine.SetDomAsync(document.DocumentElement, baseUri);
            var result = engine.Evaluate(
                "(function(){" +
                "var first=document.getElementById('first');" +
                "var second=document.getElementById('second');" +
                "var getAttribute=first.getAttribute;" +
                "var receiverValue=getAttribute.call(second,'id');" +
                "var wrongReceiverThrew=false;" +
                "try{getAttribute.call(document,'id');}" +
                "catch(e){wrongReceiverThrew=e instanceof TypeError;}" +
                "return receiverValue+'|'+String(wrongReceiverThrew);" +
                "})();");

            Assert.Equal("second|true", result?.ToString());
        }
        finally
        {
            BrowserScriptEngineRuntime.Reset();
        }
    }

    private static JsHostAdapter CreateHost() =>
        new(
            navigate: _ => { },
            post: (_, _) => { },
            status: _ => { },
            log: _ => { });
}
