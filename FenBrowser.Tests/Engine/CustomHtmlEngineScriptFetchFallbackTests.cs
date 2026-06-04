using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using FenBrowser.Core.Dom.V2;
using FenBrowser.FenEngine.Rendering;
using Xunit;

namespace FenBrowser.Tests.Engine;

public class CustomHtmlEngineScriptFetchFallbackTests
{
    [Fact]
    public async Task RenderAsync_UsesNetworkFetchForExternalScripts_WhenScriptFetcherIsUnset()
    {
        const string html = """
<!DOCTYPE html>
<html>
<body>
  <div id="state">pending</div>
  <script src="/assets/app.js"></script>
</body>
</html>
""";

        using var engine = new CustomHtmlEngine
        {
            EnableJavaScript = true,
            FetchHandler = request =>
            {
                if (request.RequestUri is not null &&
                    string.Equals(request.RequestUri.AbsolutePath, "/assets/app.js", StringComparison.Ordinal))
                {
                    return Task.FromResult(CreateResponse("document.getElementById('state').textContent = 'loaded';"));
                }

                return Task.FromResult(CreateResponse(string.Empty, HttpStatusCode.NotFound));
            }
        };

        await engine.RenderAsync(
            html,
            new Uri("https://example.test/"),
            _ => Task.FromResult(string.Empty),
            _ => Task.FromResult<Stream>(null),
            _ => { },
            viewportWidth: 1280,
            viewportHeight: 720);

        var activeRoot = Assert.IsType<Element>(engine.GetActiveDom());
        var state = activeRoot.Descendants().OfType<Element>()
            .First(e => string.Equals(e.GetAttribute("id"), "state", StringComparison.Ordinal));

        Assert.Equal("loaded", state.TextContent);
    }

    private static HttpResponseMessage CreateResponse(string content, HttpStatusCode statusCode = HttpStatusCode.OK)
    {
        return new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(content, Encoding.UTF8, "application/javascript")
        };
    }
}
