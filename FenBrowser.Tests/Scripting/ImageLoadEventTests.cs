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
    /// Image load and error events (HTML "update the image data", SVG 2 image
    /// element). The engine never fired them, so img.onload handlers and SVG
    /// &lt;image onload&gt; never ran.
    /// </summary>
    public sealed class ImageLoadEventTests
    {
        private const string Pixel =
            "data:image/svg+xml,%3Csvg xmlns='http://www.w3.org/2000/svg' width='4' height='4'/%3E";

        [Fact]
        public async Task HtmlImage_FiresLoadAgainWhenItsSourceChanges()
        {
            var document = new HtmlParser(
                "<html><body><img id='i'>" +
                "<script>var img = document.getElementById('i'); globalThis.__loads = 0;" +
                "img.onload = function () { globalThis.__loads++; };" +
                $"img.src = \"{Pixel}\";</script></body></html>",
                new Uri("https://fen.test/page")).Parse();

            var engine = await RunAsync(document.DocumentElement);
            await WaitForAsync(engine, "globalThis.__loads === 1");

            engine.Evaluate($"document.getElementById('i').setAttribute('src', \"{Pixel}#second\")");
            await WaitForAsync(engine, "globalThis.__loads === 2");
        }

        [Fact]
        public async Task UndecodableImage_FiresError()
        {
            var document = new HtmlParser(
                "<html><body><img id='i' src='data:image/png;base64,AAAA' " +
                "onerror=\"globalThis.__error = 'error'\"></body></html>",
                new Uri("https://fen.test/page")).Parse();

            var engine = await RunAsync(document.DocumentElement);

            await WaitForAsync(engine, "globalThis.__error === 'error'");
        }

        private static async Task<FenJsBrowserScriptEngine> RunAsync(Element root)
        {
            var engine = new FenJsBrowserScriptEngine(new JsHostAdapter(
                navigate: _ => { }, post: (_, __) => { }, status: _ => { }, log: _ => { }))
            {
                Sandbox = SandboxPolicy.AllowAll
            };
            await engine.SetDomAsync(root, new Uri("https://fen.test/doc"));
            return engine;
        }

        private static async Task WaitForAsync(FenJsBrowserScriptEngine engine, string condition)
        {
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < deadline)
            {
                if (engine.Evaluate("String(" + condition + ")")?.ToString() == "true") return;
                await Task.Delay(20);
            }
            Assert.Fail("condition never became true: " + condition);
        }
    }
}
