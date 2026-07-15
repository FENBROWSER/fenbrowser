using System;
using System.Threading.Tasks;
using FenBrowser.Core;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Core.Interfaces;
using FenBrowser.FenEngine.Scripting;
using Xunit;

namespace FenBrowser.Tests.Scripting;

public sealed class FenJsDomCollectionIterationTests
{
    [Fact]
    public async Task HtmlCollection_ForOfUsesIndexedHostValues()
    {
        var result = await EvaluateAsync(
            "var ids=[];" +
            "for (const image of document.getElementsByTagName('img')) ids.push(image.id);" +
            "ids.join(',');");

        Assert.Equal("first,second", result);
    }

    [Fact]
    public async Task HtmlCollection_SpreadUsesHostIterator()
    {
        var result = await EvaluateAsync(
            "[...document.getElementsByTagName('img')].map(function (image) { return image.id; }).join(',');");

        Assert.Equal("first,second", result);
    }

    [Fact]
    public async Task HtmlCollection_IteratorReturnsWellFormedResults()
    {
        var result = await EvaluateAsync(
            "var iterator=document.getElementsByTagName('img')[Symbol.iterator]();" +
            "var first=iterator.next(); var second=iterator.next(); var done=iterator.next();" +
            "[iterator[Symbol.iterator]()===iterator,first.value.id,first.done," +
            "second.value.id,second.done,done.done].join('|');");

        Assert.Equal("true|first|false|second|false|true", result);
    }

    private static async Task<string> EvaluateAsync(string script)
    {
        var baseUri = new Uri("https://fixture.test/html-collection-iterator.html");
        try
        {
            BrowserScriptEngineRuntime.Reset();
            var document = new HtmlParser(
                "<html><body><img id='first'><img id='second'></body></html>",
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
