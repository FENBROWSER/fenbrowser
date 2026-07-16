using System;
using System.Threading.Tasks;
using FenBrowser.Core;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Core.Interfaces;
using FenBrowser.FenEngine.Scripting;
using Xunit;

namespace FenBrowser.Tests.Scripting;

public sealed class DomTokenListValueBindingTests
{
    [Fact]
    public async Task ValueAssignment_UpdatesTheLiteralAssociatedAttribute()
    {
        var baseUri = new Uri("https://fixture.test/dom-token-list-value.html");
        try
        {
            BrowserScriptEngineRuntime.Reset();
            var document = new HtmlParser(
                "<html><body><div id='host' class='   a  a b '></div></body></html>",
                baseUri).Parse();
            var engine = new FenJsBrowserScriptEngine(CreateHost())
            {
                Sandbox = SandboxPolicy.AllowAll
            };

            await engine.SetDomAsync(document.DocumentElement, baseUri);

            var result = engine.Evaluate(
                "var host=document.getElementById('host');" +
                "var list=host.classList;" +
                "list.value=' foo bar foo ';" +
                "[list.value,host.getAttribute('class'),host.className,host.classList===list].join('|');");

            Assert.Equal(" foo bar foo | foo bar foo | foo bar foo |true", result?.ToString());
        }
        finally
        {
            BrowserScriptEngineRuntime.Reset();
        }
    }

    [Fact]
    public async Task ValueAssignment_ParsesAnOrderedSetWithoutDuplicateTokens()
    {
        var result = await EvaluateAsync(
            "var list=document.getElementById('host').classList;" +
            "list.value=' foo bar foo ';" +
            "[list.length,list.item(0),list.item(1),list.value].join('|');");

        Assert.Equal("2|foo|bar| foo bar foo ", result);
    }

    private static async Task<string> EvaluateAsync(string script)
    {
        var baseUri = new Uri("https://fixture.test/dom-token-list-value.html");
        try
        {
            BrowserScriptEngineRuntime.Reset();
            var document = new HtmlParser(
                "<html><body><div id='host' class='a'></div></body></html>",
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
