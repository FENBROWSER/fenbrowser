using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using FenBrowser.Core;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Scripting;
using Xunit;

namespace FenBrowser.Tests.Scripting;

/// <summary>
/// Fetch §5.4 Request class and §4.2 scheme fetch "data". A Request's state is its
/// internal [[request]]: fetch() sends what the Request was constructed with, not
/// what its JS-visible properties say. YouTube's fetch-tampering probe builds a
/// data: Request and shadows url/method/body with getters naming its player API;
/// a browser fetches the data: URL and the probe reads its own JSON back.
/// </summary>
public sealed class FenJsFetchRequestTests
{
    [Fact]
    public async Task Fetch_SendsTheRequestsInternalState_NotPropertiesAPageDefinedOverIt()
    {
        var sent = new List<string>();
        var engine = await StartAsync(
            """
            var json = JSON.stringify({ ok: true, n: 7 });
            var request = new Request('data:application/json;base64,' + btoa(json));
            Object.defineProperty(request, 'url', { get: function () { return 'https://fixture.test/api/player'; } });
            Object.defineProperty(request, 'method', { get: function () { return 'POST'; } });
            Object.defineProperty(request, 'body', { get: function () { return new ReadableStream(); } });
            fetch(request).then(function (response) {
                globalThis.__status = response.status;
                globalThis.__type = response.headers.get('content-type');
                return response.json();
            }).then(function (value) {
                globalThis.__probe = JSON.stringify(value) === json;
            }, function (error) {
                globalThis.__probe = 'rejected: ' + error;
            });
            """,
            sent);

        try
        {
            await WaitForGlobalAsync(engine, "globalThis.__probe !== undefined");

            Assert.Equal("true", engine.Evaluate("String(globalThis.__probe)")?.ToString());
            Assert.Equal("200", engine.Evaluate("String(globalThis.__status)")?.ToString());
            Assert.Equal("application/json", engine.Evaluate("String(globalThis.__type)")?.ToString());
            Assert.Empty(sent);
        }
        finally
        {
            BrowserScriptEngineRuntime.Reset();
        }
    }

    [Fact]
    public async Task Fetch_OfAConstructedRequest_SendsItsMethodHeadersAndBody()
    {
        var sent = new List<string>();
        var engine = await StartAsync(
            """
            var request = new Request('/api/echo', {
                method: 'post',
                headers: { 'Content-Type': 'application/json', 'X-Client': 'fen' },
                body: '{"q":1}'
            });
            globalThis.__tag = Object.prototype.toString.call(request);
            globalThis.__method = request.method;
            globalThis.__mode = request.mode;
            fetch(new Request(request)).then(function (response) { globalThis.__done = response.status; });
            """,
            sent);

        try
        {
            await WaitForGlobalAsync(engine, "globalThis.__done !== undefined");

            Assert.Equal("[object Request]", engine.Evaluate("String(globalThis.__tag)")?.ToString());
            Assert.Equal("POST", engine.Evaluate("String(globalThis.__method)")?.ToString());
            Assert.Equal("cors", engine.Evaluate("String(globalThis.__mode)")?.ToString());
            Assert.Equal(new[] { "POST /api/echo application/json fen {\"q\":1}" }, sent);
        }
        finally
        {
            BrowserScriptEngineRuntime.Reset();
        }
    }

    [Fact]
    public async Task Fetch_OfADataUrl_ReturnsItsBodyAndType_AndAMalformedOneRejects()
    {
        var sent = new List<string>();
        var engine = await StartAsync(
            """
            fetch('data:text/plain;charset=utf-8,hello%20world').then(function (response) {
                globalThis.__type = response.headers.get('content-type');
                return response.text();
            }).then(function (text) { globalThis.__text = text; });
            fetch('data:text/plain').then(function () {
                globalThis.__bad = 'resolved';
            }, function (error) {
                globalThis.__bad = error instanceof TypeError ? 'TypeError' : String(error);
            });
            """,
            sent);

        try
        {
            await WaitForGlobalAsync(engine, "globalThis.__text !== undefined && globalThis.__bad !== undefined");

            Assert.Equal("hello world", engine.Evaluate("String(globalThis.__text)")?.ToString());
            Assert.Equal("text/plain;charset=utf-8", engine.Evaluate("String(globalThis.__type)")?.ToString());
            Assert.Equal("TypeError", engine.Evaluate("String(globalThis.__bad)")?.ToString());
            Assert.Empty(sent);
        }
        finally
        {
            BrowserScriptEngineRuntime.Reset();
        }
    }

    private static async Task<FenJsBrowserScriptEngine> StartAsync(string script, List<string> sent)
    {
        var baseUri = new Uri("https://fixture.test/index.html");
        var document = new HtmlParser("<html><body><script>" + script + "</script></body></html>", baseUri).Parse();
        var engine = new FenJsBrowserScriptEngine(CreateHost())
        {
            Sandbox = SandboxPolicy.AllowAll,
            FetchHandler = async request =>
            {
                var body = request.Content != null ? await request.Content.ReadAsStringAsync() : string.Empty;
                request.Headers.TryGetValues("X-Client", out var client);
                lock (sent)
                {
                    sent.Add($"{request.Method} {request.RequestUri?.AbsolutePath} {request.Content?.Headers.ContentType?.MediaType} {string.Join(",", client ?? Array.Empty<string>())} {body}");
                }

                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(string.Empty) };
            }
        };

        await engine.SetDomAsync(document.DocumentElement, baseUri);
        return engine;
    }

    // fetch() settles in a later task, so a test waits for the page script's own signal.
    private static async Task WaitForGlobalAsync(FenJsBrowserScriptEngine engine, string expression)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            if (string.Equals(engine.Evaluate("String(" + expression + ")")?.ToString(), "true", StringComparison.Ordinal))
            {
                return;
            }

            await Task.Delay(20);
        }
    }

    private static JsHostAdapter CreateHost() =>
        new(
            navigate: _ => { },
            post: (_, _) => { },
            status: _ => { },
            log: _ => { });
}
