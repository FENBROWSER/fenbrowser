using System;
using System.Threading.Tasks;
using FenBrowser.Core;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Scripting;
using Xunit;

namespace FenBrowser.Tests.Scripting
{
    public sealed class FenJsGitHubDomSurfaceTests
    {
        [Fact]
        public async Task MetaElements_ReflectNameAndContentAttributes()
        {
            var baseUri = new Uri("https://github.com/");
            var document = new HtmlParser(
                "<html><head><meta name='theme-color' content='#000240'></head><body></body></html>",
                baseUri).Parse();
            var engine = new FenJsBrowserScriptEngine(CreateHost())
            {
                Sandbox = SandboxPolicy.AllowAll
            };

            await engine.SetDomAsync(document.DocumentElement, baseUri);

            var result = engine.Evaluate(
                """
                (function () {
                    var parsed = document.querySelector('meta[name="theme-color"]');
                    var created = document.createElement('meta');
                    created.name = 'runtime-token';
                    created.content = 'alpha-beta';
                    document.head.appendChild(created);

                    return [
                        parsed.name,
                        parsed.content.replace('#', ''),
                        created.getAttribute('name'),
                        created.content.replace('alpha', 'omega')
                    ].join('|');
                })();
                """);

            Assert.Equal("theme-color|000240|runtime-token|omega-beta", result?.ToString());
        }

        [Fact]
        public async Task MutationObserverObserve_AttributeFilterDefaultsAttributes()
        {
            var baseUri = new Uri("https://github.com/");
            var document = new HtmlParser("<html><body><div id='target'></div></body></html>", baseUri).Parse();
            var engine = new FenJsBrowserScriptEngine(CreateHost())
            {
                Sandbox = SandboxPolicy.AllowAll
            };

            await engine.SetDomAsync(document.DocumentElement, baseUri);

            var result = engine.Evaluate(
                """
                (function () {
                    var observer = new MutationObserver(function () {});
                    observer.observe(document.getElementById('target'), {
                        attributeFilter: ['data-selected']
                    });
                    return 'ok';
                })();
                """);

            Assert.Equal("ok", result?.ToString());
        }

        [Fact]
        public async Task UrlSearchParams_ParsesAndMutatesQueryPairs()
        {
            var baseUri = new Uri("https://github.com/?q=fen+browser&q=second&empty=");
            var document = new HtmlParser("<html><body></body></html>", baseUri).Parse();
            var engine = new FenJsBrowserScriptEngine(CreateHost())
            {
                Sandbox = SandboxPolicy.AllowAll
            };

            await engine.SetDomAsync(document.DocumentElement, baseUri);

            var result = engine.Evaluate(
                """
                (function () {
                    var params = new URLSearchParams(location.search);
                    params.append('new key', 'a b');
                    params.set('empty', 'filled');

                    var seen = [];
                    params.forEach(function (value, key) {
                        seen.push(key + '=' + value);
                    });

                    return [
                        typeof URLSearchParams,
                        params.get('q'),
                        params.getAll('q').join(','),
                        params.has('empty'),
                        params.get('empty'),
                        seen.indexOf('q=fen browser') >= 0,
                        params.toString().indexOf('new+key=a+b') >= 0
                    ].join('|');
                })();
                """);

            Assert.Equal("function|fen browser|fen browser,second|true|filled|true|true", result?.ToString());
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
