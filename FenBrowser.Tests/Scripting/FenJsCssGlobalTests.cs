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
        public async Task DocumentStyleSheets_ExposesTheDocumentsOwnStyleSheets()
        {
            var baseUri = new Uri("https://www.google.com/");
            var document = new HtmlParser("<html><head><style>body{display:block}</style></head><body></body></html>", baseUri).Parse();
            var engine = new FenJsBrowserScriptEngine(CreateHost())
            {
                Sandbox = SandboxPolicy.AllowAll
            };

            await engine.SetDomAsync(document.DocumentElement, baseUri);

            // This used to assert "object|0|null": the list was a hardcoded empty
            // stub, so a document that plainly had a stylesheet reported none.
            var result = engine.Evaluate(
                """
                [
                    typeof document.styleSheets,
                    String(document.styleSheets.length),
                    String(document.styleSheets.item(0) === document.styleSheets[0]),
                    document.styleSheets[0].cssRules[0].selectorText,
                    String(document.styleSheets.item(1))
                ].join('|');
                """);

            Assert.Equal("object|1|true|body|null", result?.ToString());
        }

        [Fact]
        public async Task SvgGeometryStyleProperties_ValidateAndSerializeLengthPercentages()
        {
            var baseUri = new Uri("https://example.com/geometry.svg");
            var document = new HtmlParser("<html><body><div id='target'></div></body></html>", baseUri).Parse();
            var engine = new FenJsBrowserScriptEngine(CreateHost())
            {
                Sandbox = SandboxPolicy.AllowAll
            };

            await engine.SetDomAsync(document.DocumentElement, baseUri);

            var result = engine.Evaluate(
                """
                (function () {
                    var style = document.getElementById('target').style;
                    var values = [];
                    style.cx = '0'; values.push(style.getPropertyValue('cx'));
                    style.cx = '-1px'; values.push(style.cx);
                    style.cx = 'calc(2em + 3ex)'; values.push(style.cx);
                    style.cx = '4%'; values.push(style.cx);
                    style.cx = '5ch'; values.push(style.cx);
                    style.rx = 'auto'; values.push(style.rx);
                    style.r = '-1px'; values.push(style.r);
                    style.cx = '10'; values.push(style.cx);
                    style.cx = ''; values.push(style.getPropertyValue('cx'));
                    return values.join('|');
                })();
                """);

            Assert.Equal("0px|-1px|calc(2em + 3ex)|4%|5ch|auto||5ch|", result?.ToString());
        }

        [Fact]
        public async Task GetComputedStyle_ResolvesSvgGeometryFontLengthsAndClampsRadii()
        {
            var baseUri = new Uri("https://example.com/geometry.svg");
            var document = new HtmlParser(
                "<html><body><div id='target' style='font-size:40px'></div></body></html>",
                baseUri).Parse();
            var engine = new FenJsBrowserScriptEngine(CreateHost())
            {
                Sandbox = SandboxPolicy.AllowAll
            };

            await engine.SetDomAsync(document.DocumentElement, baseUri);

            var result = engine.Evaluate(
                """
                (function () {
                    var target = document.getElementById('target');
                    var values = [];
                    values.push(String('cx' in getComputedStyle(target)));
                    target.style.cx = '0.5em'; values.push(getComputedStyle(target).cx);
                    target.style.cx = 'calc(10px + 0.5em)'; values.push(getComputedStyle(target).cx);
                    target.style.cx = '40%'; values.push(getComputedStyle(target).cx);
                    target.style.r = 'calc(10px - 0.5em)'; values.push(getComputedStyle(target).r);
                    values.push(getComputedStyle(target).rx);
                    return values.join('|');
                })();
                """);

            Assert.Equal("true|20px|30px|40%|0px|auto", result?.ToString());
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
