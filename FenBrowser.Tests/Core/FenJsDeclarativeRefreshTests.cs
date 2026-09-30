using FenBrowser.Core;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Scripting;
using Xunit;

namespace FenBrowser.Tests.Core;

/// <summary>
/// HTML §4.2.5.3 Refresh state. github.com's "Continue with Google" answers with a blank
/// "Redirecting to Google" page that moves on only through
/// &lt;meta http-equiv="refresh" content="0;url=https://accounts.google.com/..."&gt;.
/// </summary>
public sealed class FenJsDeclarativeRefreshTests
{
    [Theory]
    [InlineData("0;url=https://accounts.google.com/o/oauth2/v2/auth?a=1&b=2", 0, "https://accounts.google.com/o/oauth2/v2/auth?a=1&b=2")]
    [InlineData("0; url=/next", 0, "/next")]
    [InlineData("5, URL = 'next.html' trailing", 5, "next.html")]
    [InlineData("3.9;url=\"a b\"", 3, "a b")]
    [InlineData("10", 10, null)]
    [InlineData("  2  ", 2, null)]
    [InlineData(".5;/x", 0, "/x")]
    public void Content_ParsesDelayAndUrl(string content, double delay, string? url)
    {
        Assert.True(FenJsBrowserScriptEngine.TryParseDeclarativeRefresh(content, out var parsedDelay, out var parsedUrl));
        Assert.Equal(delay, parsedDelay);
        Assert.Equal(url, parsedUrl);
    }

    [Theory]
    [InlineData("")]
    [InlineData("url=/next")]
    [InlineData("-1;url=/next")]
    [InlineData("5x;url=/next")]
    public void Content_WithoutALeadingDelay_IsIgnored(string content)
    {
        Assert.False(FenJsBrowserScriptEngine.TryParseDeclarativeRefresh(content, out _, out _));
    }

    [Fact]
    public async Task RefreshMeta_NavigatesOnceTheDocumentHasLoaded()
    {
        var navigated = new TaskCompletionSource<Uri>(TaskCreationOptions.RunContinuationsAsynchronously);
        await CreateEngineAsync(
            "<html><head><title>Redirecting to Google</title>" +
            "<meta http-equiv=\"refresh\" content=\"0;url=https://accounts.google.com/o/oauth2/v2/auth?client_id=x\">" +
            "<meta http-equiv=\"refresh\" content=\"0;url=https://example.test/second\">" +
            "</head><body></body></html>",
            uri => navigated.TrySetResult(uri));

        var target = await navigated.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("https://accounts.google.com/o/oauth2/v2/auth?client_id=x", target.AbsoluteUri);
    }

    [Fact]
    public async Task RelativeRefreshUrl_ResolvesAgainstTheDocument()
    {
        var navigated = new TaskCompletionSource<Uri>(TaskCreationOptions.RunContinuationsAsynchronously);
        await CreateEngineAsync(
            "<html><head><meta http-equiv=REFRESH content=\"0; URL=next?x=1\"></head><body></body></html>",
            uri => navigated.TrySetResult(uri));

        var target = await navigated.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("https://example.test/dir/next?x=1", target.AbsoluteUri);
    }

    [Fact]
    public async Task OtherHttpEquivValues_DoNotNavigate()
    {
        var navigated = false;
        await CreateEngineAsync(
            "<html><head><meta http-equiv=\"content-type\" content=\"0;url=/x\"></head><body></body></html>",
            _ => navigated = true);
        await Task.Delay(200);
        Assert.False(navigated);
    }

    private static async Task<FenJsBrowserScriptEngine> CreateEngineAsync(string html, Action<Uri> navigate)
    {
        var baseUri = new Uri("https://example.test/dir/page.html");
        var document = new HtmlParser(html, baseUri).Parse();
        var engine = new FenJsBrowserScriptEngine(new JsHostAdapter(
            navigate: navigate,
            post: (_, _) => { },
            status: _ => { },
            log: _ => { }))
        {
            Sandbox = SandboxPolicy.AllowAll
        };
        await engine.SetDomAsync(document.DocumentElement, baseUri);
        return engine;
    }
}
