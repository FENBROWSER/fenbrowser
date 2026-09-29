using System;
using System.Globalization;
using System.Threading.Tasks;
using FenBrowser.Core;
using FenBrowser.Core.Dom.V2;
using FenBrowser.FenEngine.Scripting;
using Xunit;

namespace FenBrowser.Tests.Scripting
{
    /// <summary>
    /// SVGGeometryElement.getTotalLength and getPointAtLength (SVG 2 §9.1), measured
    /// on the outline the renderer paints.
    /// </summary>
    public sealed class SvgGeometryElementTests
    {
        private const string Shapes =
            "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 200 100'>" +
            "<line id='line' x2='30' y2='40'/>" +
            "<line id='percent' x2='50%'/>" +
            "<rect id='rect' x='5' y='5' width='10' height='20'/>" +
            "<rect id='rounded' x='5' y='5' width='10' height='20' rx='2'/>" +
            "<circle id='styled' r='1' style='r: 5px'/>" +
            "<circle id='empty' r='0'/>" +
            "<polygon id='polygon' points='0,0 10,0 10,10'/>" +
            "<path id='path' d='M0 0 H10 M20 0 H25'/>" +
            "</svg>";

        [Theory]
        [InlineData("line", 50)]
        [InlineData("percent", 100)]
        [InlineData("rect", 60)]
        [InlineData("styled", 31.4159)]
        [InlineData("empty", 0)]
        [InlineData("polygon", 34.1421)]
        [InlineData("path", 15)]
        public async Task GetTotalLength_MeasuresTheOutline(string id, double expected)
        {
            var engine = await CreateAsync();

            double length = double.Parse(
                engine.Evaluate($"String(document.getElementById('{id}').getTotalLength())")!.ToString()!,
                CultureInfo.InvariantCulture);

            Assert.Equal(expected, length, 2);
        }

        [Fact]
        public async Task InlineStyle_SetsOnlyTheCssGeometryProperties()
        {
            var engine = await CreateAsync();

            var result = engine.Evaluate(
                """
                var circle = document.getElementById('empty');
                circle.style.r = '8px';
                var line = document.getElementById('line');
                line.setAttribute('style', 'x2: 0px; y2: 8px');
                Math.round(circle.getTotalLength() * 1000) / 1000 + '|' + line.getTotalLength();
                """);

            Assert.Equal("50.265|50", result?.ToString());
        }

        [Fact]
        public async Task GetPointAtLength_WalksEveryContourAndClamps()
        {
            var engine = await CreateAsync();

            var result = engine.Evaluate(
                """
                function at(id, d) {
                    var p = document.getElementById(id).getPointAtLength(d);
                    return Math.round(p.x * 100) / 100 + ',' + Math.round(p.y * 100) / 100;
                }
                [at('line', 25), at('path', 12), at('path', 99), at('line', -5), at('rounded', 0)].join('|');
                """);

            Assert.Equal("15,20|22,0|25,0|0,0|7,5", result?.ToString());
        }

        [Fact]
        public async Task GetPointAtLength_RejectsANonFiniteDistance()
        {
            var engine = await CreateAsync();

            var result = engine.Evaluate(
                """
                var name = 'none';
                try { document.getElementById('line').getPointAtLength(NaN); } catch (e) { name = e.name; }
                name + '|' + typeof document.documentElement.getTotalLength;
                """);

            Assert.Equal("TypeError|undefined", result?.ToString());
        }

        private static async Task<FenJsBrowserScriptEngine> CreateAsync()
        {
            var document = XmlDomParser.Parse(Shapes, "image/svg+xml");
            var engine = new FenJsBrowserScriptEngine(new JsHostAdapter(
                navigate: _ => { }, post: (_, __) => { }, status: _ => { }, log: _ => { }))
            {
                Sandbox = SandboxPolicy.AllowAll
            };
            await engine.SetDomAsync(document.DocumentElement, new Uri("https://fen.test/shapes.svg"));
            return engine;
        }
    }
}
