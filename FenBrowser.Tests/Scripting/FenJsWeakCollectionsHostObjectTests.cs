using System;
using System.Threading.Tasks;
using FenBrowser.Core;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Scripting;
using Xunit;

namespace FenBrowser.Tests.Scripting
{
    public sealed class FenJsWeakCollectionsHostObjectTests
    {
        [Fact]
        public async Task DomHostObjects_CanBeWeakCollectionKeysAndTargets()
        {
            var baseUri = new Uri("https://www.youtube.com/");
            var document = new HtmlParser("<html><body></body></html>", baseUri).Parse();
            var engine = new FenJsBrowserScriptEngine(CreateHost())
            {
                Sandbox = SandboxPolicy.AllowAll
            };

            await engine.SetDomAsync(document.DocumentElement, baseUri);

            var result = engine.Evaluate(
                """
                (function () {
                    var el = document.createElement('div');
                    var frag = document.createDocumentFragment();
                    var map = new WeakMap();
                    var set = new WeakSet();
                    var constructed = new WeakMap([[frag, 'fragment']]);
                    var ref = new WeakRef(el);
                    var registry = new FinalizationRegistry(function () {});

                    map.set(el, 'element');
                    set.add(el);
                    registry.register(el, 'held', el);

                    return [
                        map.get(el),
                        String(map.has(el)),
                        String(set.has(el)),
                        constructed.get(frag),
                        String(ref.deref() === el),
                        String(registry.unregister(el)),
                        String(map.delete(el)),
                        String(set.delete(el)),
                        String(map.has(el)),
                        String(set.has(el))
                    ].join('|');
                })();
                """);

            Assert.Equal("element|true|true|fragment|true|true|true|true|false|false", result?.ToString());
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
