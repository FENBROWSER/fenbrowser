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

    [Fact]
    public async Task Closest_Matches_And_Dataset_Work_For_Common_Delegation_Patterns()
    {
        var baseUri = new Uri("https://example.com/index.html");
        var parser = new HtmlParser("<html><body><div id='root' data-vt-d='1'><span id='child'></span></div></body></html>", baseUri);
        var doc = parser.Parse();
        var engine = new JavaScriptEngine(CreateHost());

        await engine.SetDomAsync(doc.DocumentElement, baseUri);

        Assert.Equal("function", engine.Evaluate("typeof document.getElementById('child').closest")?.ToString());
        Assert.Equal("function", engine.Evaluate("typeof document.getElementById('root').matches")?.ToString());
        Assert.Equal("object", engine.Evaluate("typeof document.getElementById('root').dataset")?.ToString());

        Assert.Equal("true", engine.Evaluate(@"
            (function () {
                var child = document.getElementById('child');
                var host = child.closest('[data-vt-d]');
                if (!host) return 'false';
                return String(host.matches('#root') && host.dataset.vtD === '1');
            })();
        ")?.ToString());

        Assert.Equal("ok", engine.Evaluate(@"
            (function () {
                var host = document.getElementById('root');
                host.dataset.vtFlag = 'ok';
                return host.getAttribute('data-vt-flag');
            })();
        ")?.ToString());
    }

    [Fact]
    public async Task QuerySelector_Matches_Closest_Share_Level4SelectorBehavior()
    {
        var baseUri = new Uri("https://example.com/index.html");
        var parser = new HtmlParser(
            "<html><body>" +
            "<section id='scope'>" +
            "  <ul>" +
            "    <li id='n1' class='keep'>one</li>" +
            "    <li id='n2'>two</li>" +
            "    <li id='n3' class='keep'>three</li>" +
            "    <li id='n4' class='keep'>four</li>" +
            "  </ul>" +
            "  <article id='article'><span id='leaf' class='leaf'>leaf</span></article>" +
            "</section>" +
            "</body></html>",
            baseUri);
        var doc = parser.Parse();
        var engine = new JavaScriptEngine(CreateHost());

        await engine.SetDomAsync(doc.DocumentElement, baseUri);

        var bridgeSelection = engine.QuerySelector("li:nth-child(2 of .keep)");
        Assert.True(bridgeSelection.IsObject);
        Assert.Equal("n3", bridgeSelection.AsObject().Get("id").ToString());

        Assert.Equal("n3", engine.Evaluate("document.querySelector('li:nth-child(2 of .keep)').id")?.ToString());
        Assert.Equal("true", engine.Evaluate("String(document.getElementById('n3').matches('li:nth-child(2 of .keep)'))")?.ToString());
        Assert.Equal("scope", engine.Evaluate("document.getElementById('leaf').closest('section:has(> article .leaf)').id")?.ToString());
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
