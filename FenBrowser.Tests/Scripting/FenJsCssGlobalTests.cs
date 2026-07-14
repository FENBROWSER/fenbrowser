using System;
using System.Threading.Tasks;
using FenBrowser.Core;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Scripting;
using Xunit;

namespace FenBrowser.Tests.Scripting
{
    public sealed class FenJsCssGlobalTests
    {
        [Fact]
        public async Task CssGlobal_ExposesSupportsAndEscape()
        {
            var baseUri = new Uri("https://www.google.com/");
            var document = new HtmlParser("<html><body><div id='app'></div></body></html>", baseUri).Parse();
            var engine = new FenJsBrowserScriptEngine(CreateHost())
            {
                Sandbox = SandboxPolicy.AllowAll
            };

            await engine.SetDomAsync(document.DocumentElement, baseUri);

            var result = engine.Evaluate(
                """
                [
                    typeof CSS,
                    typeof CSS.supports,
                    String(CSS.supports('display', 'grid')),
                    String(CSS.supports('(display: grid)')),
                    String(CSS.supports('display', '')),
                    CSS.escape('a b')
                ].join('|');
                """);

            Assert.Equal("object|function|true|true|false|a\\ b", result?.ToString());
        }

        [Fact]
        public async Task DocumentStyleSheets_ExposesEmptyListShape()
        {
            var baseUri = new Uri("https://www.google.com/");
            var document = new HtmlParser("<html><head><style>body{display:block}</style></head><body></body></html>", baseUri).Parse();
            var engine = new FenJsBrowserScriptEngine(CreateHost())
            {
                Sandbox = SandboxPolicy.AllowAll
            };

            await engine.SetDomAsync(document.DocumentElement, baseUri);

            var result = engine.Evaluate(
                """
                [
                    typeof document.styleSheets,
                    String(document.styleSheets.length),
                    String(document.styleSheets.item(0))
                ].join('|');
                """);

            Assert.Equal("object|0|null", result?.ToString());
        }

        private static JsHostAdapter CreateHost()
        {
            return new JsHostAdapter(
                navigate: _ => { },
                post: (_, __) => { },
                status: _ => { },
                log: _ => { });
        }
    }
}
