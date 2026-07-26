using FenBrowser.Core;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Scripting;
using Xunit;

namespace FenBrowser.Tests.Core;

public sealed class FenJsCryptoTests
{
    [Fact]
    public async Task RandomUuidReturnsRfc4122Version4Shape()
    {
        var baseUri = new Uri("https://example.test/");
        var document = new HtmlParser("<html><body></body></html>", baseUri).Parse();
        var engine = new FenJsBrowserScriptEngine(CreateHost())
        {
            Sandbox = SandboxPolicy.AllowAll
        };

        await engine.SetDomAsync(document.DocumentElement, baseUri);

        var result = engine.Evaluate("""
            (function () {
                var uuid = crypto.randomUUID();
                return uuid.length === 36 &&
                    uuid.charAt(8) === '-' &&
                    uuid.charAt(13) === '-' &&
                    uuid.charAt(18) === '-' &&
                    uuid.charAt(23) === '-' &&
                    uuid.charAt(14) === '4' &&
                    '89ab'.indexOf(uuid.charAt(19)) >= 0;
            })();
            """);

        Assert.Equal("True", result?.ToString());
    }

    private static JsHostAdapter CreateHost()
        => new(
            navigate: _ => { },
            post: (_, _) => { },
            status: _ => { },
            log: _ => { });
}
