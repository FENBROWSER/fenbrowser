using System;
using System.Threading.Tasks;
using FenBrowser.Core;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Scripting;
using Xunit;

namespace FenBrowser.Tests.Scripting;

/// <summary>
/// DOM 4.2.8 ChildNode: before(), after() and replaceWith(). Core implements
/// the mixin, but the bridge only surfaced remove(), so the other three read as
/// undefined - MediaWiki asked for replaceWith on an anchor. The expected
/// markup below is what Chrome 152 produces for the same sequence, including
/// string arguments becoming Text nodes.
/// </summary>
public sealed class FenJsChildNodeMutationTests
{
    [Fact]
    public async Task ReplaceWith_IsAFunctionOnElements()
    {
        Assert.Equal(
            "function",
            await EvaluateAsync("typeof document.getElementById('a').replaceWith;"));
    }

    [Fact]
    public async Task BeforeAndAfter_AreFunctionsOnElements()
    {
        Assert.Equal(
            "function|function",
            await EvaluateAsync(
                "typeof document.getElementById('a').before + '|' +" +
                "typeof document.getElementById('a').after;"));
    }

    [Fact]
    public async Task ReplaceWith_SwapsInAnElement()
    {
        Assert.Equal(
            "<i id=\"n\"></i><span id=\"b\">B</span>",
            await EvaluateAsync(
                "var n = document.createElement('i'); n.id = 'n';" +
                "document.getElementById('a').replaceWith(n);" +
                "document.getElementById('p').innerHTML;"));
    }

    [Fact]
    public async Task Before_InsertsAStringAsATextNode()
    {
        Assert.Equal(
            "<span id=\"a\">A</span>X<span id=\"b\">B</span>",
            await EvaluateAsync(
                "document.getElementById('b').before('X');" +
                "document.getElementById('p').innerHTML;"));
    }

    [Fact]
    public async Task After_InsertsAStringAsATextNode()
    {
        Assert.Equal(
            "<span id=\"a\">A</span><span id=\"b\">B</span>Y",
            await EvaluateAsync(
                "document.getElementById('b').after('Y');" +
                "document.getElementById('p').innerHTML;"));
    }

    [Fact]
    public async Task ReplaceWith_AcceptsAMixOfStringsAndNodes()
    {
        Assert.Equal(
            "<span id=\"a\">A</span>Q<u></u>",
            await EvaluateAsync(
                "var z = document.createElement('u');" +
                "document.getElementById('b').replaceWith('Q', z);" +
                "document.getElementById('p').innerHTML;"));
    }

    [Fact]
    public async Task ReplaceWith_WithNoArgumentsRemovesTheNode()
    {
        Assert.Equal(
            "<span id=\"b\">B</span>",
            await EvaluateAsync(
                "document.getElementById('a').replaceWith();" +
                "document.getElementById('p').innerHTML;"));
    }

    [Fact]
    public async Task ChildNodeMethodsAreAvailableOnTextNodes()
    {
        Assert.Equal(
            "function|function|function",
            await EvaluateAsync(
                "var t = document.getElementById('a').firstChild;" +
                "typeof t.before + '|' + typeof t.after + '|' + typeof t.replaceWith;"));
    }

    private static async Task<string> EvaluateAsync(string script)
    {
        var baseUri = new Uri("https://fixture.test/child-node.html");
        try
        {
            BrowserScriptEngineRuntime.Reset();
            var document = new HtmlParser(
                "<html><body><div id='p'><span id='a'>A</span><span id='b'>B</span></div></body></html>",
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
