using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using FenBrowser.Core;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Scripting;
using Xunit;

namespace FenBrowser.Tests.Scripting;

/// <summary>
/// HTML 8.1.6.2 import maps: a &lt;script type=importmap&gt; decides what a bare
/// module specifier means. github.com's every React-using chunk starts with
/// `import * as r from "react"`; without the map the engine fetched /react and
/// every module on the page failed to link.
/// </summary>
public sealed class FenJsImportMapTests
{
    [Fact]
    public async Task BareSpecifier_ResolvesThroughTheImportMap()
    {
        var baseUri = new Uri("https://fixture.test/app/index.html");
        var document = new HtmlParser(
            """
            <html><head>
            <script type="importmap">
            {
              "imports": {
                "react": "https://cdn.fixture.test/react-1.0.js",
                "pkg/": "./vendor/pkg/",
                "./local.js": "/rewritten/local.js"
              },
              "scopes": {
                "https://cdn.fixture.test/": { "react": "https://cdn.fixture.test/react-cdn.js" }
              }
            }
            </script>
            </head><body>
            <script type="module">
            import * as react from "react";
            import { name } from "pkg/util.js";
            import { where } from "./local.js";
            import { via } from "https://cdn.fixture.test/consumer.js";
            const again = await import("react");
            globalThis.__result = [react.version, name, where, via, again === react].join('|');
            </script>
            </body></html>
            """,
            baseUri).Parse();

        var sources = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["https://cdn.fixture.test/react-1.0.js"] = "export const version = 'react-map';",
            ["https://cdn.fixture.test/react-cdn.js"] = "export const version = 'react-scoped';",
            ["https://fixture.test/app/vendor/pkg/util.js"] = "export const name = 'pkg-prefix';",
            ["https://fixture.test/rewritten/local.js"] = "export const where = 'url-key-remapped';",
            ["https://cdn.fixture.test/consumer.js"] = "import { version } from 'react'; export const via = version;",
        };
        var requested = new List<string>();

        var engine = new FenJsBrowserScriptEngine(CreateHost())
        {
            Sandbox = SandboxPolicy.AllowAll,
            FetchHandler = request =>
            {
                var url = request.RequestUri?.AbsoluteUri ?? string.Empty;
                requested.Add(url);
                if (sources.TryGetValue(url, out var source))
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(source, Encoding.UTF8, "text/javascript")
                    });
                }

                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }
        };

        try
        {
            await engine.SetDomAsync(document.DocumentElement, baseUri);

            Assert.Equal(
                "react-map|pkg-prefix|url-key-remapped|react-scoped|true",
                engine.Evaluate("String(globalThis.__result)")?.ToString());
            Assert.DoesNotContain("https://fixture.test/app/react", requested);
        }
        finally
        {
            BrowserScriptEngineRuntime.Reset();
        }
    }

    private static JsHostAdapter CreateHost() =>
        new(
            navigate: _ => { },
            post: (_, _) => { },
            status: _ => { },
            log: _ => { });
}
