using System;
using System.Threading.Tasks;
using FenBrowser.Core;
using FenBrowser.Core.Dom.V2;
using FenBrowser.FenEngine.Scripting;
using Xunit;

namespace FenBrowser.Tests.Scripting
{
    /// <summary>
    /// SVG load events: once the document loads, each svg element receives a
    /// non-bubbling load, innermost first, so &lt;svg onload&gt; handlers run.
    /// </summary>
    public sealed class SvgLoadEventTests
    {
        [Fact]
        public async Task EverySvgElement_GetsOneNonBubblingLoad_InnermostFirst()
        {
            var document = XmlDomParser.Parse(
                "<svg xmlns='http://www.w3.org/2000/svg' onload=\"globalThis.__order = (globalThis.__order || '') + 'outer;'\">" +
                "<g><svg id='inner' onload=\"globalThis.__order = (globalThis.__order || '') + 'inner;'\"/></g>" +
                "<script>document.documentElement.addEventListener('load', function (e) {" +
                " globalThis.__phases = (globalThis.__phases || '') + e.eventPhase + (e.target === this ? 't' : 'b') + ';'; });" +
                "</script></svg>",
                "image/svg+xml");
            var engine = new FenJsBrowserScriptEngine(new JsHostAdapter(
                navigate: _ => { }, post: (_, __) => { }, status: _ => { }, log: _ => { }))
            {
                Sandbox = SandboxPolicy.AllowAll
            };

            await engine.SetDomAsync(document.DocumentElement, new Uri("https://fen.test/load.svg"));

            Assert.Equal("inner;outer;", engine.Evaluate("globalThis.__order")?.ToString());
            // The inner load does not bubble to the root's listener: it sees only its own.
            Assert.Equal("2t;", engine.Evaluate("globalThis.__phases")?.ToString());
        }
    }
}
