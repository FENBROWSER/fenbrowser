using System;
using System.Threading.Tasks;
using FenBrowser.Core;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Scripting;
using Xunit;

namespace FenBrowser.Tests.Scripting
{
    // HTML 4.6.3 HTMLHyperlinkElementUtils: <a> and <area> expose the parts of their
    // resolved URL. Only href and hash existed, so hostname and pathname read undefined.
    // bing.com wraps Element.prototype.appendChild and checks every inserted script with
    // `anchor.href = script.src; anchor.hostname.length`, which threw, so no dynamically
    // inserted script ever ran - including the one that renders its Turnstile challenge.
    public sealed class HyperlinkUrlPartsTests
    {
        [Fact]
        public async Task AbsoluteHref_ExposesEveryUrlPart()
        {
            var engine = await CreateEngineAsync();

            var parts = engine.Evaluate(
                "(function () {" +
                "  var a = document.createElement('a');" +
                "  a.href = 'https://user:pw@challenges.cloudflare.com:8443/turnstile/v0/api.js?x=1#frag';" +
                "  return [a.protocol, a.username, a.password, a.host, a.hostname, a.port, a.pathname, a.search, a.hash, a.origin].join('|');" +
                "})()")?.ToString();

            Assert.Equal(
                "https:|user|pw|challenges.cloudflare.com:8443|challenges.cloudflare.com|8443|/turnstile/v0/api.js|?x=1|#frag|https://challenges.cloudflare.com:8443",
                parts);
        }

        [Fact]
        public async Task RelativeHref_ResolvesAgainstTheDocument()
        {
            var engine = await CreateEngineAsync();

            var parts = engine.Evaluate(
                "(function () {" +
                "  var area = document.createElement('area');" +
                "  area.setAttribute('href', '/rel/path?q=2');" +
                "  return [area.protocol, area.host, area.pathname, area.search, area.port].join('|');" +
                "})()")?.ToString();

            Assert.Equal("https:|parent.test|/rel/path|?q=2|", parts);
        }

        [Fact]
        public async Task MissingHref_GivesEmptyPartsAndAColonProtocol()
        {
            var engine = await CreateEngineAsync();

            var parts = engine.Evaluate(
                "(function () {" +
                "  var a = document.createElement('a');" +
                "  return [a.protocol, a.hostname, a.pathname, a.origin].join('|');" +
                "})()")?.ToString();

            Assert.Equal(":|||", parts);
        }

        [Fact]
        public async Task ScriptInsertionThroughAHostnameCheckingWrapper_Succeeds()
        {
            var engine = await CreateEngineAsync();

            var inserted = engine.Evaluate(
                "(function () {" +
                "  var probe = document.createElement('A');" +
                "  var original = Element.prototype.appendChild;" +
                "  Element.prototype.appendChild = function (child) {" +
                "    if (child.tagName === 'SCRIPT') {" +
                "      probe.href = child.src;" +
                "      if (!(probe.href.length > 0 && probe.hostname.length > 0)) { return null; }" +
                "    }" +
                "    return original.apply(this, arguments);" +
                "  };" +
                "  var script = document.createElement('script');" +
                "  script.src = 'https://r.example.test/bundle.js';" +
                "  document.body.appendChild(script);" +
                "  return String(script.parentNode === document.body);" +
                "})()")?.ToString();

            Assert.Equal("true", inserted);
        }

        private static async Task<FenJsBrowserScriptEngine> CreateEngineAsync()
        {
            var baseUri = new Uri("https://parent.test/page");
            var document = new HtmlParser("<html><body></body></html>", baseUri).Parse();
            var engine = new FenJsBrowserScriptEngine(
                new JsHostAdapter(navigate: _ => { }, post: (_, __) => { }, status: _ => { }, log: _ => { }))
            {
                Sandbox = SandboxPolicy.AllowAll
            };
            await engine.SetDomAsync(document.DocumentElement, baseUri);
            return engine;
        }
    }
}
