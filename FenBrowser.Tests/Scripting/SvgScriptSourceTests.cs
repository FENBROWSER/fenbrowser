using System;
using System.Threading.Tasks;
using FenBrowser.Core;
using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Scripting;
using Xunit;

namespace FenBrowser.Tests.Scripting
{
    /// <summary>
    /// SVG 2 §15.2: an SVG script element names its external source with href (or
    /// the legacy xlink:href), where an HTML script uses src. Scripts in SVG
    /// documents used to run only when inline, so helpers loaded from a file
    /// (such as the WPT reftest utilities) were never defined.
    /// </summary>
    public sealed class SvgScriptSourceTests
    {
        [Theory]
        [InlineData("href")]
        [InlineData("xlink:href")]
        public async Task SvgScript_LoadsItsExternalSource(string attribute)
        {
            string xml =
                "<svg xmlns='http://www.w3.org/2000/svg' xmlns:xlink='http://www.w3.org/1999/xlink'>" +
                $"<script {attribute}='/helper.js'/>" +
                "<script>globalThis.__seen = typeof globalThis.__helper;</script></svg>";

            var engine = await RunAsync(XmlDomParser.Parse(xml, "image/svg+xml").DocumentElement);

            Assert.Equal("42", engine.Evaluate("String(globalThis.__helper)")?.ToString());
            Assert.Equal("number", engine.Evaluate("globalThis.__seen")?.ToString());
        }

        [Fact]
        public async Task HtmlScript_StillIgnoresHref()
        {
            var document = new HtmlParser(
                "<html><body><script href='/helper.js'></script></body></html>",
                new Uri("https://fen.test/page")).Parse();

            var engine = await RunAsync(document.DocumentElement);

            Assert.Equal("undefined", engine.Evaluate("typeof globalThis.__helper")?.ToString());
        }

        private static async Task<FenJsBrowserScriptEngine> RunAsync(FenBrowser.Core.Dom.V2.Element root)
        {
            var engine = new FenJsBrowserScriptEngine(new JsHostAdapter(
                navigate: _ => { }, post: (_, __) => { }, status: _ => { }, log: _ => { }))
            {
                Sandbox = SandboxPolicy.AllowAll,
                AllowExternalScripts = true,
                ExternalScriptFetcher = static (uri, _) => Task.FromResult(
                    uri.AbsolutePath.EndsWith("/helper.js", StringComparison.Ordinal)
                        ? "globalThis.__helper = 42;"
                        : string.Empty)
            };

            await engine.SetDomAsync(root, new Uri("https://fen.test/doc.svg"));
            return engine;
        }
    }
}
