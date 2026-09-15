using System;
using System.Diagnostics;
using System.Threading.Tasks;
using FenBrowser.FenEngine.Rendering;
using FenBrowser.FenEngine.Scripting;
using Xunit;

namespace FenBrowser.Tests.Scripting;

/// <summary>
/// File API 8.3 blob URL store. URL.createObjectURL returned an unregistered random string,
/// so a Worker started from it never ran and fetch() of it failed. Cloudflare Turnstile's
/// challenge frame starts a worker from a blob URL, and bing.com's challenge stayed on
/// "Verifying...".
/// </summary>
[Collection("Engine Tests")]
public sealed class BlobUrlStoreTests
{
    private static readonly TimeSpan SettleTimeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task WorkerStartedFromABlobUrl_RunsTheBlobScript()
    {
        BrowserScriptEngineRuntime.Reset();
        try
        {
            using var browser = new BrowserHost();
            Assert.True(await browser.NavigateAsync("data:text/html,<body></body>"));

            await browser.ExecuteScriptAsync(@"
                (function () {
                    window.__reply = null;
                    var code = 'onmessage = function (e) { postMessage(e.data + 22); };';
                    var url = URL.createObjectURL(new Blob([code], { type: 'text/javascript' }));
                    var worker = new Worker(url);
                    worker.onmessage = function (e) { window.__reply = String(e.data); };
                    worker.postMessage(20);
                    return 1;
                })();");

            Assert.Equal("42", await WaitForAsync(browser, "window.__reply", "42"));
        }
        finally
        {
            BrowserScriptEngineRuntime.Reset();
        }
    }

    [Fact]
    public async Task FetchOfABlobUrl_ReturnsItsContent_UntilRevoked()
    {
        BrowserScriptEngineRuntime.Reset();
        try
        {
            using var browser = new BrowserHost();
            Assert.True(await browser.NavigateAsync("data:text/html,<body></body>"));

            var url = (await browser.ExecuteScriptAsync(@"
                (function () {
                    window.__text = null;
                    window.__revoked = null;
                    var url = URL.createObjectURL(new Blob(['hello blob'], { type: 'text/plain' }));
                    fetch(url).then(function (r) { return r.text().then(function (t) { window.__text = r.headers.get('content-type') + '|' + t; }); },
                        function (e) { window.__text = 'rejected ' + e.message; });
                    return url;
                })();"))?.ToString();

            Assert.StartsWith("blob:", url);
            Assert.Equal("text/plain|hello blob", await WaitForAsync(browser, "window.__text", "text/plain|hello blob"));

            await browser.ExecuteScriptAsync($@"
                (function () {{
                    URL.revokeObjectURL('{url}');
                    fetch('{url}').then(function () {{ window.__revoked = 'resolved'; }}, function (e) {{ window.__revoked = e.name; }});
                    return 1;
                }})();");

            Assert.Equal("TypeError", await WaitForAsync(browser, "window.__revoked", "TypeError"));
        }
        finally
        {
            BrowserScriptEngineRuntime.Reset();
        }
    }

    private static async Task<string> WaitForAsync(BrowserHost browser, string expression, string expected)
    {
        var watch = Stopwatch.StartNew();
        string last = null;
        while (watch.Elapsed < SettleTimeout)
        {
            last = (await browser.ExecuteScriptAsync(expression))?.ToString();
            if (string.Equals(last, expected, StringComparison.Ordinal))
            {
                return last;
            }

            await Task.Delay(50);
        }

        return last;
    }
}
