using System;
using FenBrowser.Core;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Scripting;
using Xunit;

namespace FenBrowser.Tests.Scripting;

[Collection("Engine Tests")]
public sealed class DocumentHasFocusTests : IDisposable
{
    public DocumentHasFocusTests()
    {
        BrowserScriptEngineRuntime.Reset();
    }

    public void Dispose()
    {
        BrowserScriptEngineRuntime.Reset();
    }

    private static JsHostAdapter CreateHost()
        => new(
            navigate: _ => { },
            post: (_, _) => { },
            status: _ => { },
            log: _ => { });

    [Fact]
    public void HasFocus_IsFunctionAndReturnsBooleanForActiveDocument()
    {
        var baseUri = new Uri("https://focus.test/page");
        var document = new HtmlParser("<html><body></body></html>", baseUri).Parse();
        var engine = new FenJsBrowserScriptEngine(CreateHost())
        {
            Sandbox = SandboxPolicy.AllowAll
        };
        engine.SetDomAsync(document.DocumentElement, baseUri).GetAwaiter().GetResult();

        Assert.Equal("function", engine.Evaluate("typeof document.hasFocus")?.ToString());
        Assert.Equal("True", engine.Evaluate("document.hasFocus()")?.ToString());
    }
}
