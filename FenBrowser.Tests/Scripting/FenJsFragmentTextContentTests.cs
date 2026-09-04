using System;
using System.Threading.Tasks;
using FenBrowser.Core;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Scripting;
using Xunit;

namespace FenBrowser.Tests.Scripting;

/// <summary>
/// DOM 4.4: the textContent setter is "string replace all" on DocumentFragment
/// exactly as on Element. The host bridge exposed the read but had no write
/// case, so an ordinary assignment raised "refused by embedder" - observed on a
/// plain en.wikipedia.org load.
/// </summary>
public sealed class FenJsFragmentTextContentTests
{
    [Fact]
    public async Task FragmentTextContent_IsWritable()
    {
        var result = await EvaluateAsync(
            "var fragment = document.createDocumentFragment();" +
            "fragment.textContent = 'hello';" +
            "fragment.textContent;");

        Assert.Equal("hello", result);
    }

    [Fact]
    public async Task FragmentTextContent_ReplacesExistingChildren()
    {
        var result = await EvaluateAsync(
            "var fragment = document.createDocumentFragment();" +
            "fragment.appendChild(document.createElement('span'));" +
            "fragment.appendChild(document.createElement('b'));" +
            "fragment.textContent = 'replaced';" +
            "fragment.childNodes.length + '|' + fragment.textContent;");

        Assert.Equal("1|replaced", result);
    }

    [Fact]
    public async Task FragmentTextContent_EmptyStringClearsChildren()
    {
        var result = await EvaluateAsync(
            "var fragment = document.createDocumentFragment();" +
            "fragment.appendChild(document.createElement('span'));" +
            "fragment.textContent = '';" +
            "fragment.childNodes.length + '|' + JSON.stringify(fragment.textContent);");

        Assert.Equal("0|\"\"", result);
    }

    [Fact]
    public async Task FragmentTextContent_AssignmentDoesNotThrow()
    {
        var result = await EvaluateAsync(
            "try {" +
            "  var fragment = document.createDocumentFragment();" +
            "  fragment.textContent = 'ok';" +
            "  'assigned';" +
            "} catch (error) { 'threw:' + String(error && error.message); }");

        Assert.Equal("assigned", result);
    }

    private static async Task<string> EvaluateAsync(string script)
    {
        var baseUri = new Uri("https://fixture.test/fragment-text-content.html");
        try
        {
            BrowserScriptEngineRuntime.Reset();
            var document = new HtmlParser(
                "<html><body><div id='host'></div></body></html>",
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
