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
/// Fetch §4.2 scheme fetch "data": fetch() of a data: URL answers 200 with the
/// data: URL processor's body and MIME type, and a URL the processor rejects is a
/// network error. YouTube's fetch-tampering probe reads its own JSON back this way.
/// </summary>
public sealed class FenJsFetchRequestTests
{
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
