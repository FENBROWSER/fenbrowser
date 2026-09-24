using System;
using FenBrowser.Core;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Scripting;
using Xunit;

namespace FenBrowser.Tests.Scripting;

// WebIDL 3.2.10: a DOMString argument is converted with ECMA-262 ToString.
[Collection("Engine Tests")]
public sealed class HostStringConversionTests : IDisposable
{
    public HostStringConversionTests()
    {
        BrowserScriptEngineRuntime.Reset();
    }

    public void Dispose()
    {
        BrowserScriptEngineRuntime.Reset();
    }

    private static FenJsBrowserScriptEngine Load(string html)
    {
        var baseUri = new Uri("https://conv.test/dir/page.html");
        var document = new HtmlParser(html, baseUri).Parse();
        var engine = new FenJsBrowserScriptEngine(new JsHostAdapter(
            navigate: _ => { },
            post: (_, _) => { },
            status: _ => { },
            log: _ => { }))
        {
            Sandbox = SandboxPolicy.AllowAll
        };
        engine.SetDomAsync(document.DocumentElement, baseUri).GetAwaiter().GetResult();
        return engine;
    }

    [Fact]
    public void ObjectArgumentsGoThroughTheirOwnToString()
    {
        var engine = Load("<!doctype html><html><body></body></html>");

        Assert.Equal("custom|https://conv.test/x.css|1e+21", engine.Evaluate(@"
            var e = document.createElement('div');
            e.setAttribute('data-a', { toString: function () { return 'custom'; } });
            var l = document.createElement('link');
            l.href = new URL('/x.css', location.href);
            e.setAttribute('data-n', 1e21);
            [e.getAttribute('data-a'), l.getAttribute('href'), e.getAttribute('data-n')].join('|')")?.ToString());
    }

    [Fact]
    public void AThrowingToStringPropagatesToTheCaller()
    {
        var engine = Load("<!doctype html><html><body></body></html>");

        Assert.Equal("boom", engine.Evaluate(@"
            try {
                document.createElement('div').setAttribute('x', { toString: function () { throw new Error('boom'); } });
                'none';
            } catch (e) { e.message; }")?.ToString());
    }

    [Fact]
    public void CompatModeReportsQuirksMode()
    {
        Assert.Equal("BackCompat", Load("<html><body></body></html>").Evaluate("document.compatMode")?.ToString());
        Assert.Equal("CSS1Compat", Load("<!doctype html><html><body></body></html>").Evaluate("document.compatMode")?.ToString());
    }

    [Fact]
    public void AboutBlankIframeDocumentsResolveAgainstTheirCreator()
    {
        var engine = Load("<!doctype html><html><body></body></html>");

        Assert.Equal("https://conv.test/dir/style.css", engine.Evaluate(@"
            var f = document.createElement('iframe');
            document.body.appendChild(f);
            var l = f.contentDocument.createElement('link');
            l.setAttribute('href', 'style.css');
            f.contentDocument.head.appendChild(l);
            l.href")?.ToString());
    }
}
