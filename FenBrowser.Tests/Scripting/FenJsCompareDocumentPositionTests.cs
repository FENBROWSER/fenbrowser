using System;
using System.Threading.Tasks;
using FenBrowser.Core;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Scripting;
using Xunit;

namespace FenBrowser.Tests.Scripting;

public sealed class FenJsCompareDocumentPositionTests
{
    [Fact]
    public async Task CompareDocumentPosition_ExposesNodeRelationsAndConstants()
    {
        var result = await EvaluateAsync(
            "var body=document.body;" +
            "var first=document.getElementById('first');" +
            "var second=document.getElementById('second');" +
            "var text=first.firstChild;" +
            "var fragment=document.createDocumentFragment();" +
            "var detached=document.createElement('div'); fragment.appendChild(detached);" +
            "var disconnected=first.compareDocumentPosition(detached);" +
            "[typeof Node.prototype.compareDocumentPosition," +
            "Object.prototype.hasOwnProperty.call(Node.prototype,'compareDocumentPosition')," +
            "body.compareDocumentPosition(first),first.compareDocumentPosition(body)," +
            "first.compareDocumentPosition(second),second.compareDocumentPosition(first)," +
            "first.compareDocumentPosition(first),text.compareDocumentPosition(second)," +
            "document.compareDocumentPosition(first),fragment.compareDocumentPosition(detached)," +
            "disconnected & Node.DOCUMENT_POSITION_DISCONNECTED," +
            "disconnected & Node.DOCUMENT_POSITION_IMPLEMENTATION_SPECIFIC," +
            "Node.DOCUMENT_POSITION_CONTAINED_BY].join('|');");

        Assert.Equal("function|true|20|10|4|2|0|4|20|20|1|32|16", result);
    }

    [Fact]
    public async Task CompareDocumentPosition_RejectsNonNodeArguments()
    {
        var result = await EvaluateAsync(
            "try { document.body.compareDocumentPosition(null); 'no-error'; }" +
            "catch (error) { String(error && error.name) + '|' + String(error && error.message); }");

        Assert.Contains("TypeError", result, StringComparison.Ordinal);
        Assert.Contains("parameter 1 is not of type 'Node'", result, StringComparison.Ordinal);
    }

    private static async Task<string> EvaluateAsync(string script)
    {
        var baseUri = new Uri("https://fixture.test/compare-document-position.html");
        try
        {
            BrowserScriptEngineRuntime.Reset();
            var document = new HtmlParser(
                "<html><body><div id='first'>text</div><div id='second'></div></body></html>",
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
