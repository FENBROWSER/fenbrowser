using FenBrowser.Core;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Scripting;
using Xunit;

namespace FenBrowser.Tests.Core;

public sealed class FenJsFileApiTests
{
    [Fact]
    public async Task FileConstructorIsGloballyBoundAndExposesMetadata()
    {
        var baseUri = new Uri("https://example.test/");
        var document = new HtmlParser("<html><body></body></html>", baseUri).Parse();
        var engine = new FenJsBrowserScriptEngine(CreateHost())
        {
            Sandbox = SandboxPolicy.AllowAll
        };

        await engine.SetDomAsync(document.DocumentElement, baseUri);

        var result = engine.Evaluate(
            """
            (function () {
                var file = new File(['abc'], 'sample.txt', {
                    type: 'Text/Plain',
                    lastModified: 123
                });
                return typeof File === 'function' &&
                    file instanceof File &&
                    file instanceof Blob &&
                    file.name === 'sample.txt' &&
                    file.size === 3 &&
                    file.type === 'text/plain' &&
                    file.lastModified === 123;
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
