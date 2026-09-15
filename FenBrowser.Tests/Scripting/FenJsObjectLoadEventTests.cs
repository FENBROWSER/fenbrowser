using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using FenBrowser.Core;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Scripting;
using Xunit;

namespace FenBrowser.Tests.Scripting;

/// <summary>
/// HTML 4.8.7 "update the object element's representation": an object whose
/// data resource loads fires <c>load</c> at the element, and one whose fetch
/// fails fires <c>error</c>. Acid3 test 65 counts an object's onload among the
/// seven its support files must deliver; test 69 timed out waiting for it.
/// </summary>
public sealed class FenJsObjectLoadEventTests
{
    [Fact]
    public async Task ScriptInsertedObjectFiresLoadAfterListenerIsAttached()
    {
        var destinations = new ConcurrentQueue<string>();
        var engine = await CreateEngineAsync("<html><body></body></html>", destinations);

        engine.Evaluate("""
            globalThis.__events = [];
            var o = document.createElement('object');
            o.onload = function (e) { globalThis.__events.push('load:' + e.type + ':' + (e.target === o)); };
            o.onerror = function () { globalThis.__events.push('error'); };
            o.data = 'support.svg';
            document.body.appendChild(o);
            globalThis.__sync = globalThis.__events.length;
            """);

        Assert.Equal("load:load:true", await WaitForAsync(engine, "globalThis.__events.join(',')"));
        Assert.Equal("0", engine.Evaluate("String(globalThis.__sync)")?.ToString());
        Assert.Contains("object", destinations);
    }

    [Fact]
    public async Task ObjectWhoseFetchFailsFiresError()
    {
        var engine = await CreateEngineAsync("<html><body></body></html>");

        engine.Evaluate("""
            globalThis.__events = [];
            var o = document.createElement('object');
            o.onload = function () { globalThis.__events.push('load'); };
            o.onerror = function () { globalThis.__events.push('error'); };
            o.data = 'missing.svg';
            document.body.appendChild(o);
            """);

        Assert.Equal("error", await WaitForAsync(engine, "globalThis.__events.join(',')"));
    }

    [Fact]
    public async Task ParserCreatedObjectFiresLoadIntoItsMarkupHandler()
    {
        var engine = await CreateEngineAsync(
            "<html><body><object id=\"o\" data=\"support.svg\" onload=\"globalThis.__events = ['load:' + this.id]\"></object></body></html>");

        Assert.Equal("load:o", await WaitForAsync(engine, "globalThis.__events ? globalThis.__events.join(',') : ''"));
    }

    private static async Task<string> WaitForAsync(FenJsBrowserScriptEngine engine, string expression)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.ElapsedMilliseconds < 5000)
        {
            var value = engine.Evaluate(expression)?.ToString();
            if (!string.IsNullOrEmpty(value))
            {
                return value;
            }

            await Task.Delay(25);
        }

        return engine.Evaluate(expression)?.ToString() ?? string.Empty;
    }

    private static async Task<FenJsBrowserScriptEngine> CreateEngineAsync(
        string html,
        ConcurrentQueue<string> destinations = null)
    {
        var baseUri = new Uri("https://example.test/");
        var document = new HtmlParser(html, baseUri).Parse();
        var engine = new FenJsBrowserScriptEngine(CreateHost())
        {
            Sandbox = SandboxPolicy.AllowAll,
            FetchHandler = request =>
            {
                if (destinations != null && request.Headers.TryGetValues("Sec-Fetch-Dest", out var values))
                {
                    foreach (var value in values)
                    {
                        destinations.Enqueue(value);
                    }
                }

                if (request.RequestUri?.AbsolutePath == "/missing.svg")
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
                }

                var content = new StringContent("<svg xmlns='http://www.w3.org/2000/svg'/>");
                content.Headers.ContentType = new MediaTypeHeaderValue("image/svg+xml");
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
            }
        };
        await engine.SetDomAsync(document.DocumentElement, baseUri);
        return engine;
    }

    private static JsHostAdapter CreateHost()
        => new(
            navigate: _ => { },
            post: (_, _) => { },
            status: _ => { },
            log: _ => { });
}
