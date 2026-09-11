using System.Diagnostics;
using FenBrowser.Core;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Scripting;
using Xunit;

namespace FenBrowser.Tests.Core;

/// <summary>
/// HTML "update the image data": an img whose src becomes available gets a
/// queued <c>load</c> event, a failed one gets <c>error</c>, and the events
/// reach listeners attached after src was set in the same script.
/// </summary>
// Decodes images through the process-wide ImageLoader, whose counters other
// tests assert; keep it off the parallel schedule with them.
[Collection("Synthetic CAPTCHA")]
public sealed class FenJsImageLoadEventTests
{
    // 1x1 transparent PNG.
    private const string OnePixelPng =
        "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==";

    [Fact]
    public async Task ScriptCreatedImageFiresLoadAfterListenerIsAttached()
    {
        var engine = await CreateEngineAsync("<html><body></body></html>");

        engine.Evaluate($$"""
            globalThis.__events = [];
            var img = document.createElement('img');
            img.src = '{{OnePixelPng}}';
            img.addEventListener('load', function (e) {
                globalThis.__events.push('load:' + e.type + ':' + (e.target === img) + ':' + img.complete + ':' + img.naturalWidth + 'x' + img.naturalHeight);
            });
            img.addEventListener('error', function () { globalThis.__events.push('error'); });
            document.body.appendChild(img);
            globalThis.__sync = globalThis.__events.length;
            """);

        var events = await WaitForAsync(engine, "globalThis.__events.length > 0 ? globalThis.__events.join(',') : ''");
        Assert.Equal("load:load:true:true:1x1", events);
        Assert.Equal("0", engine.Evaluate("String(globalThis.__sync)")?.ToString());
    }

    [Fact]
    public async Task UndecodableImageFiresError()
    {
        var engine = await CreateEngineAsync("<html><body></body></html>");

        engine.Evaluate("""
            globalThis.__events = [];
            var img = new Image();
            img.onerror = function () { globalThis.__events.push('error'); };
            img.onload = function () { globalThis.__events.push('load'); };
            img.src = 'data:image/png;base64,AAAA';
            """);

        var events = await WaitForAsync(engine, "globalThis.__events.length > 0 ? globalThis.__events.join(',') : ''");
        Assert.Equal("error", events);
    }

    [Fact]
    public async Task ParserCreatedImageFiresLoadIntoItsMarkupHandler()
    {
        // A parser-created image's request starts when the realm attaches; its
        // markup onload handler exists from the start, so it must see the event.
        var engine = await CreateEngineAsync(
            $"<html><body><img id=\"i\" src=\"{OnePixelPng}\" onload=\"globalThis.__events = ['load:' + this.id + ':' + this.complete]\"></body></html>");

        var events = await WaitForAsync(engine, "globalThis.__events ? globalThis.__events.join(',') : ''");
        Assert.Equal("load:i:true", events);
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

    private static async Task<FenJsBrowserScriptEngine> CreateEngineAsync(string html)
    {
        var baseUri = new Uri("https://example.test/");
        var document = new HtmlParser(html, baseUri).Parse();
        var engine = new FenJsBrowserScriptEngine(CreateHost())
        {
            Sandbox = SandboxPolicy.AllowAll
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
