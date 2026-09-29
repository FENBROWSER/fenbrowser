using System;
using System.Threading.Tasks;
using FenBrowser.Core;
using FenBrowser.Core.Dom.V2;
using FenBrowser.FenEngine.Scripting;
using Xunit;

namespace FenBrowser.Tests.Scripting
{
    /// <summary>
    /// DOM Standard namespaced attribute methods on Element. SVG scripts use them for
    /// xlink:href and xml:lang, and they were missing from the script bindings.
    /// </summary>
    public sealed class NamespacedAttributeTests
    {
        private const string Xlink = "http://www.w3.org/1999/xlink";

        [Fact]
        public async Task NamespacedAttributes_RoundTrip()
        {
            var engine = await CreateAsync();

            var result = engine.Evaluate(
                $$"""
                var use = document.getElementById('u');
                var out = [];
                use.setAttributeNS('{{Xlink}}', 'xlink:href', '#a');
                out.push(use.getAttributeNS('{{Xlink}}', 'href'));
                out.push(use.hasAttributeNS('{{Xlink}}', 'href'));
                use.removeAttributeNS('{{Xlink}}', 'href');
                out.push(use.hasAttributeNS('{{Xlink}}', 'href'));
                out.push(String(use.getAttributeNS('{{Xlink}}', 'href')));
                use.setAttributeNS(null, 'x', '5');
                out.push(use.getAttribute('x'));
                out.push(use.getAttributeNS('', 'x'));
                out.join('|');
                """);

            Assert.Equal("#a|true|false|null|5|5", result?.ToString());
        }

        [Fact]
        public async Task SetAttributeNS_WithAnEmptyName_ThrowsInvalidCharacterError()
        {
            var engine = await CreateAsync();

            var result = engine.Evaluate(
                """
                var name = 'none';
                try { document.getElementById('u').setAttributeNS(null, '', 'v'); }
                catch (e) { name = e.name; }
                name;
                """);

            Assert.Equal("InvalidCharacterError", result?.ToString());
        }

        private static async Task<FenJsBrowserScriptEngine> CreateAsync()
        {
            var document = XmlDomParser.Parse(
                $"<svg xmlns='http://www.w3.org/2000/svg' xmlns:xlink='{Xlink}'><use id='u'/></svg>",
                "image/svg+xml");
            var engine = new FenJsBrowserScriptEngine(new JsHostAdapter(
                navigate: _ => { }, post: (_, __) => { }, status: _ => { }, log: _ => { }))
            {
                Sandbox = SandboxPolicy.AllowAll
            };
            await engine.SetDomAsync(document.DocumentElement, new Uri("https://fen.test/doc.svg"));
            return engine;
        }
    }
}
