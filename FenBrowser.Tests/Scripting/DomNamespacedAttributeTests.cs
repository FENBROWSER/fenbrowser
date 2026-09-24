using System;
using FenBrowser.Core;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Scripting;
using Xunit;

namespace FenBrowser.Tests.Scripting;

// DOM 4.5 createElementNS / adoptNode and 4.9 the *AttributeNS methods.
[Collection("Engine Tests")]
public sealed class DomNamespacedAttributeTests : IDisposable
{
    public DomNamespacedAttributeTests()
    {
        BrowserScriptEngineRuntime.Reset();
    }

    public void Dispose()
    {
        BrowserScriptEngineRuntime.Reset();
    }

    private static FenJsBrowserScriptEngine Load(string html = "<html><body></body></html>")
    {
        var baseUri = new Uri("https://ns.test/page");
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
    public void CreateElementNSWithNullNamespaceKeepsTheNullNamespaceAndCase()
    {
        var engine = Load();

        Assert.Equal("null|null|Embed|Embed", engine.Evaluate(@"
            var a = document.createElementNS(null, 'Embed');
            var b = document.createElementNS('', 'x');
            [String(a.namespaceURI), String(b.namespaceURI), a.localName, a.tagName].join('|')")?.ToString());
    }

    [Fact]
    public void NamespacedAttributesAreDistinctFromNullNamespaceOnes()
    {
        var engine = Load();

        Assert.Equal("urn:v|plain|true|false|null", engine.Evaluate(@"
            var e = document.createElement('div');
            e.setAttributeNS('urn:x', 'p:id', 'urn:v');
            e.setAttributeNS(null, 'id', 'plain');
            var nsValue = e.getAttributeNS('urn:x', 'id');
            var plain = e.getAttributeNS(null, 'id');
            var has = e.hasAttributeNS('urn:x', 'id');
            e.removeAttributeNS('urn:x', 'id');
            [nsValue, plain, has, e.hasAttributeNS('urn:x', 'id'), String(e.getAttributeNS('urn:x', 'id'))].join('|')")?.ToString());
    }

    [Fact]
    public void SetAttributeNSValidatesNamesAgainstNamespaces()
    {
        var engine = Load();

        Assert.Equal("NamespaceError|NamespaceError|NamespaceError|InvalidCharacterError", engine.Evaluate(@"
            var e = document.createElement('div');
            function err(f) { try { f(); return 'none'; } catch (x) { return x.name; } }
            [err(function () { e.setAttributeNS(null, 'p:a', '1'); }),
             err(function () { e.setAttributeNS('urn:x', 'xml:a', '1'); }),
             err(function () { e.setAttributeNS('urn:x', 'xmlns', '1'); }),
             err(function () { e.setAttributeNS('urn:x', 'a b', '1'); })].join('|')")?.ToString());
    }

    [Fact]
    public void AdoptNodeRemovesTheNodeAndMovesItIntoTheDocument()
    {
        var engine = Load("<html><body><div id='host'><span id='child'></span></div></body></html>");

        Assert.Equal("true|true|false|NotSupportedError", engine.Evaluate(@"
            var other = document.implementation.createHTMLDocument('');
            var child = document.getElementById('child');
            var adopted = other.adoptNode(child);
            var moved = adopted === child && child.ownerDocument === other;
            var err;
            try { document.adoptNode(other); err = 'none'; } catch (x) { err = x.name; }
            [moved, child.parentNode === null, document.getElementById('child') !== null, err].join('|')")?.ToString());
    }
}
