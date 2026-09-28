using System;
using System.Threading.Tasks;
using FenBrowser.Core;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Scripting;
using Xunit;

namespace FenBrowser.Tests.Scripting;

/// <summary>
/// Element and Node members real pages read that the bridge did not answer.
/// Expected values were taken from Chrome 152: accessKey is "" when the
/// attribute is absent, contentEditable is the string "inherit" (not a
/// boolean), and namespaceURI is the HTML namespace for HTML elements.
/// </summary>
public sealed class FenJsElementSurfaceMembersTests
{
    [Fact]
    public async Task NamespaceUri_IsTheHtmlNamespaceForHtmlElements()
    {
        var result = await EvaluateAsync("document.getElementById('host').namespaceURI;");

        Assert.Equal("http://www.w3.org/1999/xhtml", result);
    }

    [Fact]
    public async Task NamespaceUri_IsPresentOnTheDocumentElement()
    {
        var result = await EvaluateAsync("document.documentElement.namespaceURI;");

        Assert.Equal("http://www.w3.org/1999/xhtml", result);
    }

    [Fact]
    public async Task AccessKey_IsEmptyWhenTheAttributeIsAbsent()
    {
        var result = await EvaluateAsync(
            "var a = document.getElementById('host');" +
            "typeof a.accessKey + '|' + JSON.stringify(a.accessKey);");

        Assert.Equal("string|\"\"", result);
    }

    [Fact]
    public async Task AccessKey_ReflectsTheAttribute()
    {
        var result = await EvaluateAsync("document.getElementById('keyed').accessKey;");

        Assert.Equal("k", result);
    }

    [Fact]
    public async Task ContentEditable_IsInheritWhenTheAttributeIsAbsent()
    {
        var result = await EvaluateAsync("document.getElementById('host').contentEditable;");

        Assert.Equal("inherit", result);
    }

    [Fact]
    public async Task ContentEditable_ReflectsAnAuthoredValue()
    {
        var result = await EvaluateAsync("document.getElementById('editable').contentEditable;");

        Assert.Equal("true", result);
    }

    [Fact]
    public async Task ContentEditable_TreatsAnEmptyAttributeAsTrue()
    {
        var result = await EvaluateAsync("document.getElementById('bare-editable').contentEditable;");

        Assert.Equal("true", result);
    }

    [Fact]
    public async Task IsContentEditable_FollowsTheNearestAuthoredState()
    {
        var result = await EvaluateAsync(
            "var e = document.getElementById('editable'), c = document.createElement('i');" +
            "e.appendChild(c);" +
            "[document.getElementById('host').isContentEditable, e.isContentEditable, c.isContentEditable," +
            " document.getElementById('locked').isContentEditable].join('|');");

        Assert.Equal("false|true|true|false", result);
    }

    [Fact]
    public async Task ContentEditable_SetterWritesTheAttributeAndRejectsOtherValues()
    {
        // w3schools' tryit page toggles body.contentEditable = true then false.
        var result = await EvaluateAsync(
            "var h = document.getElementById('host'), out = [];" +
            "h.contentEditable = true; out.push(h.getAttribute('contenteditable'), h.isContentEditable);" +
            "h.contentEditable = 'FALSE'; out.push(h.getAttribute('contenteditable'), h.isContentEditable);" +
            "h.contentEditable = 'inherit'; out.push(h.hasAttribute('contenteditable'), h.contentEditable);" +
            "try { h.contentEditable = 'maybe'; out.push('no throw'); } catch (e) { out.push(e.name); }" +
            "out.join('|');");

        Assert.Equal("true|true|false|false|false|inherit|SyntaxError", result);
    }

    [Fact]
    public async Task Draggable_DefaultsToImagesAndLinksAndReflectsTheAttribute()
    {
        var result = await EvaluateAsync(
            "var img = document.createElement('img'), a = document.createElement('a'), d = document.getElementById('host');" +
            "var bare = a.draggable; a.href = '/x';" +
            "var out = [d.draggable, img.draggable, bare, a.draggable];" +
            "d.draggable = true; out.push(d.getAttribute('draggable'), d.draggable);" +
            "img.draggable = false; out.push(img.draggable);" +
            "out.join('|');");

        Assert.Equal("false|true|false|true|true|true|false", result);
    }

    [Fact]
    public async Task GetRootNode_ReturnsTheDocumentForAConnectedElement()
    {
        var result = await EvaluateAsync(
            "var r = document.getElementById('host').getRootNode();" +
            "(r === document) + '|' + (typeof document.getElementById('host').getRootNode);");

        Assert.Equal("true|function", result);
    }

    [Fact]
    public async Task GetRootNode_ReturnsTheDetachedSubtreeRoot()
    {
        var result = await EvaluateAsync(
            "var outer = document.createElement('div');" +
            "var inner = document.createElement('span');" +
            "outer.appendChild(inner);" +
            "(inner.getRootNode() === outer);");

        Assert.Equal("True", result, ignoreCase: true);
    }

    [Fact]
    public async Task GetRootNode_AcceptsAnOptionsDictionary()
    {
        var result = await EvaluateAsync(
            "(document.getElementById('host').getRootNode({ composed: true }) === document);");

        Assert.Equal("True", result, ignoreCase: true);
    }

    [Fact]
    public async Task GetRootNode_IsAvailableOnADocumentFragment()
    {
        var result = await EvaluateAsync(
            "var f = document.createDocumentFragment();" +
            "var c = document.createElement('i');" +
            "f.appendChild(c);" +
            "(c.getRootNode() === f) + '|' + (f.getRootNode() === f);");

        Assert.Equal("true|true", result);
    }

    [Fact]
    public async Task GetRootNode_IsAvailableOnATextNode()
    {
        var result = await EvaluateAsync(
            "var t = document.createTextNode('x');" +
            "document.getElementById('host').appendChild(t);" +
            "(t.getRootNode() === document);");

        Assert.Equal("True", result, ignoreCase: true);
    }

    [Fact]
    public async Task GetRootNode_IsAvailableOnTheDocument()
    {
        var result = await EvaluateAsync("(document.getRootNode() === document);");

        Assert.Equal("True", result, ignoreCase: true);
    }

    private static async Task<string> EvaluateAsync(string script)
    {
        var baseUri = new Uri("https://fixture.test/element-surface.html");
        try
        {
            BrowserScriptEngineRuntime.Reset();
            var document = new HtmlParser(
                "<html><body>" +
                "<div id='host'></div>" +
                "<a id='keyed' accesskey='k'>link</a>" +
                "<div id='editable' contenteditable='true'></div>" +
                "<div id='bare-editable' contenteditable></div>" +
                "<div contenteditable='true'><p id='locked' contenteditable='false'></p></div>" +
                "</body></html>",
                baseUri).Parse();
            var engine = new FenJsBrowserScriptEngine(CreateHost())
            {
                Sandbox = SandboxPolicy.AllowAll
            };

            await engine.SetDomAsync(document.DocumentElement, baseUri);
            return engine.Evaluate(script)?.ToString();
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
