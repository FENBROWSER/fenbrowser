using System;
using FenBrowser.Core;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Scripting;
using Xunit;

namespace FenBrowser.Tests.Scripting;

/// <summary>
/// WebIDL 3.7: an interface's members belong on its interface prototype, which is
/// where feature detection looks for them.
/// </summary>
/// <remarks>
/// A member that answers on the instance but is missing from the prototype is
/// worse than one that is absent: it works when called and reports absent when
/// probed, so a page takes its no-support branch and nothing errors. The
/// web-components polyfill hits this directly — it decides what it can patch with
/// <c>Object.getOwnPropertyDescriptor(Element.prototype, name)</c>.
/// </remarks>
[Collection("Engine Tests")]
public sealed class InterfacePrototypeMemberTests : IDisposable
{
    public InterfacePrototypeMemberTests()
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

    private static FenJsBrowserScriptEngine CreateEngine()
    {
        var baseUri = new Uri("https://proto.test/page");
        var document = new HtmlParser(
            "<html><body><div id='d'>x</div></body></html>", baseUri).Parse();
        var engine = new FenJsBrowserScriptEngine(CreateHost())
        {
            Sandbox = SandboxPolicy.AllowAll
        };
        engine.SetDomAsync(document.DocumentElement, baseUri).GetAwaiter().GetResult();
        return engine;
    }

    [Theory]
    [InlineData("Node", "textContent")]
    [InlineData("Node", "childNodes")]
    [InlineData("Node", "parentNode")]
    [InlineData("Node", "firstChild")]
    [InlineData("Node", "isConnected")]
    [InlineData("Element", "innerHTML")]
    [InlineData("Element", "outerHTML")]
    [InlineData("Element", "id")]
    [InlineData("Element", "className")]
    [InlineData("Element", "classList")]
    [InlineData("Element", "children")]
    [InlineData("Element", "getBoundingClientRect")]
    [InlineData("Element", "insertAdjacentHTML")]
    [InlineData("Document", "body")]
    [InlineData("Document", "head")]
    [InlineData("Document", "title")]
    [InlineData("Document", "readyState")]
    [InlineData("HTMLAnchorElement", "href")]
    [InlineData("HTMLAnchorElement", "pathname")]
    [InlineData("HTMLImageElement", "src")]
    [InlineData("HTMLImageElement", "naturalWidth")]
    [InlineData("HTMLCanvasElement", "getContext")]
    [InlineData("HTMLInputElement", "value")]
    [InlineData("HTMLInputElement", "type")]
    [InlineData("HTMLTemplateElement", "content")]
    [InlineData("HTMLIFrameElement", "contentWindow")]
    [InlineData("HTMLScriptElement", "src")]
    [InlineData("HTMLFormElement", "submit")]
    public void InterfaceMember_IsDiscoverableOnItsPrototype(string interfaceName, string member)
    {
        var engine = CreateEngine();

        // Both shapes matter: `in` is the cheap test, and getOwnPropertyDescriptor
        // is what a library needs before it can wrap or replace the member.
        Assert.Equal(
            "true|true",
            engine.Evaluate(
                $"[('{member}' in {interfaceName}.prototype)," +
                $" !!Object.getOwnPropertyDescriptor({interfaceName}.prototype, '{member}')].join('|')")
                ?.ToString());
    }

    [Fact]
    public void PublishedMember_StillReadsAndWritesThroughToTheHost()
    {
        var engine = CreateEngine();

        Assert.Equal(
            "x|<b>y</b>|d",
            engine.Evaluate(
                "(function () {" +
                "  var el = document.getElementById('d');" +
                "  var before = el.textContent;" +
                "  el.innerHTML = '<b>y</b>';" +
                "  return [before, el.innerHTML, el.id].join('|');" +
                "})()")?.ToString());
    }

    [Fact]
    public void AMemberTheHostDoesNotImplement_IsNotPublished()
    {
        // The mirror-image failure. Publishing an unimplemented member would make
        // the page take the supported branch and call something answering
        // undefined, which is worse than reporting it absent.
        var engine = CreateEngine();

        Assert.Equal(
            "False",
            engine.Evaluate("'transferControlToOffscreen' in HTMLCanvasElement.prototype")?.ToString());
    }

    [Fact]
    public void ExistenceCheckOnAGeometryMember_DoesNotResolveLayout()
    {
        // CSSOM View 4: reading clientWidth resolves layout. Asking whether it
        // exists must not — probing an interface's members would otherwise cost a
        // layout flush per geometry member, on every document.
        var flushes = 0;
        var baseUri = new Uri("https://proto.test/page");
        var document = new HtmlParser(
            "<html><body><div id='d'>x</div></body></html>", baseUri).Parse();
        var engine = new FenJsBrowserScriptEngine(CreateHost())
        {
            Sandbox = SandboxPolicy.AllowAll,
            FlushPendingLayout = _ => flushes++
        };
        engine.SetDomAsync(document.DocumentElement, baseUri).GetAwaiter().GetResult();

        flushes = 0;
        Assert.Equal(
            "True",
            engine.Evaluate("'clientWidth' in document.getElementById('d')")?.ToString());
        Assert.Equal(0, flushes);
    }
}
