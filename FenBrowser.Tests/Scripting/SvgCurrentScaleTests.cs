using System;
using System.Threading.Tasks;
using FenBrowser.Core;
using FenBrowser.Core.Dom.V2;
using FenBrowser.FenEngine.Rendering;
using FenBrowser.FenEngine.Scripting;
using Xunit;

namespace FenBrowser.Tests.Scripting
{
    /// <summary>
    /// SVGSVGElement.currentScale and currentTranslate (SVG 2 §5.1.1): the zoom and
    /// pan of the outermost svg element, which painting applies to the document root.
    /// </summary>
    public sealed class SvgCurrentScaleTests
    {
        [Fact]
        public async Task CurrentScaleAndTranslate_DriveTheRootZoomAndPan()
        {
            var (engine, document) = await CreateAsync();

            var result = engine.Evaluate(
                """
                var svg = document.documentElement;
                var out = [svg.currentScale, svg.currentTranslate === svg.currentTranslate];
                svg.currentScale = 0.5;
                svg.currentTranslate.x = 10;
                svg.currentTranslate.y = -4;
                out.push(svg.currentScale, svg.currentTranslate.x, svg.currentTranslate.y);
                out.join('|');
                """);

            Assert.Equal("1|true|0.5|10|-4", result?.ToString());
            var zoom = SvgZoomAndPanState.ForPainting(document.DocumentElement);
            Assert.NotNull(zoom);
            Assert.Equal(0.5f, zoom!.Value.Scale);
            Assert.Equal(10f, zoom.Value.TranslateX);
            Assert.Equal(-4f, zoom.Value.TranslateY);
        }

        [Fact]
        public async Task NestedSvg_KeepsScaleOneAndIgnoresChanges()
        {
            var (engine, document) = await CreateAsync();

            var result = engine.Evaluate(
                """
                var inner = document.getElementById('inner');
                inner.currentScale = 3;
                inner.currentTranslate.x = 7;
                [inner.currentScale, inner.currentTranslate.x, document.documentElement.currentScale].join('|');
                """);

            Assert.Equal("1|7|1", result?.ToString());
            Assert.Null(SvgZoomAndPanState.ForPainting(document.GetElementById("inner")));
            Assert.Null(SvgZoomAndPanState.ForPainting(document.DocumentElement));
        }

        [Fact]
        public async Task NonFiniteScale_ThrowsTypeError()
        {
            var (engine, _) = await CreateAsync();

            var result = engine.Evaluate(
                """
                var name = 'none';
                try { document.documentElement.currentScale = Infinity; } catch (e) { name = e.name; }
                name + '|' + document.documentElement.currentScale;
                """);

            Assert.Equal("TypeError|1", result?.ToString());
        }

        private static async Task<(FenJsBrowserScriptEngine Engine, Document Document)> CreateAsync()
        {
            var document = XmlDomParser.Parse(
                "<svg xmlns='http://www.w3.org/2000/svg'><svg id='inner'/></svg>", "image/svg+xml");
            var engine = new FenJsBrowserScriptEngine(new JsHostAdapter(
                navigate: _ => { }, post: (_, __) => { }, status: _ => { }, log: _ => { }))
            {
                Sandbox = SandboxPolicy.AllowAll
            };
            await engine.SetDomAsync(document.DocumentElement, new Uri("https://fen.test/zoom.svg"));
            return (engine, document);
        }
    }
}
