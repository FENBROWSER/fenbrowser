using FenBrowser.Core;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Core.Interfaces;
using FenBrowser.FenEngine.Scripting;
using Xunit;

namespace FenBrowser.Tests.Scripting;

[Collection("Engine Tests")]
public sealed class FenJsCspNonceTests
{
    [Fact]
    public async Task SetDomAsync_PassesElementNonceToExternalScriptFetcher()
    {
        var baseUri = new Uri("https://example.com/app/index.html");
        var document = new HtmlParser(
            "<html><body><script nonce=\"approved\" src=\"/assets/bootstrap.js\"></script></body></html>",
            baseUri).Parse();
        var engine = new FenJsBrowserScriptEngine(CreateHost())
        {
            AllowExternalScripts = true,
            Sandbox = SandboxPolicy.AllowAll
        };
        string observedNonce = null;
        engine.ExternalScriptFetcherWithNonce = (_, _, nonce) =>
        {
            observedNonce = nonce;
            return Task.FromResult("globalThis.__nonceAuthorized = true;");
        };

        await engine.SetDomAsync(document.DocumentElement, baseUri);

        Assert.Equal("approved", observedNonce);
        Assert.Equal(true, engine.Evaluate("globalThis.__nonceAuthorized"));
    }

    private static JsHostAdapter CreateHost()
    {
        return new JsHostAdapter(
            navigate: _ => { },
            post: (_, _) => { },
            status: _ => { },
            log: _ => { });
    }
}
