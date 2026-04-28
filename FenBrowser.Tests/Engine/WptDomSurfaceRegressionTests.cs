using System;
using System.Threading.Tasks;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Scripting;
using Xunit;

namespace FenBrowser.Tests.Engine;

public class WptDomSurfaceRegressionTests
{
    [Fact]
    public async Task AttributeNodes_AreCreatable_AsNodeInstances_AndRejectChildInsertion()
    {
        var baseUri = new Uri("https://example.com/index.html");
        var parser = new HtmlParser("<html><body></body></html>", baseUri);
        var doc = parser.Parse();
        var engine = new JavaScriptEngine(CreateHost());

        await engine.SetDomAsync(doc.DocumentElement, baseUri);

        Assert.Equal("function", engine.Evaluate("typeof document.createAttribute")?.ToString());
        Assert.Equal(true, engine.Evaluate("document.createAttribute('x') instanceof Node"));
        Assert.Equal(true, engine.Evaluate("Attr.prototype instanceof Node"));
        Assert.Equal("HierarchyRequestError", engine.Evaluate(@"
            (function () {
                var parent = document.createElement('div');
                var attribute = document.createAttribute('x');
                try { parent.appendChild(attribute); return 'no-error'; }
                catch (e) { return e && e.name ? e.name : String(e); }
            })();
        ")?.ToString());
    }

    [Fact]
    public async Task CharacterData_Methods_AreExposed_On_Text_And_Comment()
    {
        var baseUri = new Uri("https://example.com/index.html");
        var parser = new HtmlParser("<html><body></body></html>", baseUri);
        var doc = parser.Parse();
        var engine = new JavaScriptEngine(CreateHost());

        await engine.SetDomAsync(doc.DocumentElement, baseUri);

        Assert.Equal("function", engine.Evaluate("typeof document.createTextNode('test').appendData")?.ToString());
        Assert.Equal("function", engine.Evaluate("typeof document.createComment('test').replaceData")?.ToString());
        Assert.Equal("testbar", engine.Evaluate(@"
            (function () {
                var node = document.createTextNode('test');
                node.appendData('bar');
                return node.data;
            })();
        ")?.ToString());
        Assert.Equal("teXXt", engine.Evaluate(@"
            (function () {
                var node = document.createComment('test');
                node.replaceData(2, 1, 'XX');
                return node.data;
            })();
        ")?.ToString());
    }

    [Fact]
    public async Task Collection_And_Fragment_Interfaces_Use_Real_Prototype_Chains()
    {
        var baseUri = new Uri("https://example.com/index.html");
        var parser = new HtmlParser("<html><body><div></div><div></div></body></html>", baseUri);
        var doc = parser.Parse();
        var engine = new JavaScriptEngine(CreateHost());

        await engine.SetDomAsync(doc.DocumentElement, baseUri);

        Assert.Equal("function", engine.Evaluate("typeof NodeList")?.ToString());
        Assert.Equal("function", engine.Evaluate("typeof HTMLCollection")?.ToString());
        Assert.Equal("function", engine.Evaluate("typeof DocumentFragment")?.ToString());
        Assert.Equal("function", engine.Evaluate("typeof CharacterData")?.ToString());
        Assert.Equal("true", engine.Evaluate("String(document.querySelectorAll('div') instanceof NodeList)")?.ToString());
        Assert.Equal("true", engine.Evaluate("String(document.getElementsByTagName('div') instanceof HTMLCollection)")?.ToString());
        Assert.Equal("true", engine.Evaluate("String(document.createDocumentFragment() instanceof DocumentFragment)")?.ToString());
        Assert.Equal("true", engine.Evaluate("String(DocumentFragment.prototype instanceof Node)")?.ToString());
        Assert.Equal("true", engine.Evaluate("String(document.createTextNode('x') instanceof CharacterData)")?.ToString());
        Assert.Equal("true", engine.Evaluate("String(Comment.prototype instanceof CharacterData)")?.ToString());
    }

    [Fact]
    public async Task DomTokenList_IsBranded_And_ToggleAttribute_IsExposed()
    {
        var baseUri = new Uri("https://example.com/index.html");
        var parser = new HtmlParser("<html><body><div id='host' class='a b'></div></body></html>", baseUri);
        var doc = parser.Parse();
        var engine = new JavaScriptEngine(CreateHost());

        await engine.SetDomAsync(doc.DocumentElement, baseUri);

        Assert.Equal("[object DOMTokenList]", engine.Evaluate("Object.prototype.toString.call(document.getElementById('host').classList)")?.ToString());
        Assert.Equal("function", engine.Evaluate("typeof document.getElementById('host').toggleAttribute")?.ToString());
        Assert.Equal(true, engine.Evaluate(@"
            (function () {
                var host = document.getElementById('host');
                host.toggleAttribute('hidden', true);
                return host.hasAttribute('hidden');
            })();
        "));
    }

    private static JsHostAdapter CreateHost()
    {
        return new JsHostAdapter(
            navigate: _ => { },
            post: (_, __) => { },
            status: _ => { },
            log: _ => { });
    }
}
