using System;
using System.Threading.Tasks;
using FenBrowser.Core;
using FenBrowser.Core.Dom.V2;
using FenBrowser.FenEngine.Scripting;
using Xunit;

namespace FenBrowser.Tests.Scripting
{
    /// <summary>
    /// SVG DOM transform lists (SVG 2 §8.13–8.16, §4.5.10): the transform,
    /// gradientTransform and patternTransform reflections and the SVGSVGElement
    /// factories, which scripted paint-server and geometry tests drive.
    /// </summary>
    public sealed class SvgTransformListTests
    {
        [Fact]
        public async Task AppendItem_SerializesTheListIntoTheAttribute()
        {
            var (engine, document) = await CreateAsync();

            var result = engine.Evaluate(
                """
                var t = document.documentElement.createSVGTransform();
                var out = [t.type, t.matrix.a];
                t.setTranslate(0.5, 0);
                var list = document.getElementById('lg').gradientTransform.baseVal;
                var added = list.appendItem(t);
                out.push(list.numberOfItems, list.length, t.type, added.matrix.e);
                out.join('|');
                """);

            Assert.Equal("1|1|1|1|2|0.5", result?.ToString());
            Assert.Equal("translate(0.5 0)", document.GetElementById("lg").GetAttribute("gradientTransform"));
        }

        [Fact]
        public async Task Clear_LeavesAnEmptyAttribute()
        {
            var (engine, document) = await CreateAsync();

            engine.Evaluate("document.getElementById('p').patternTransform.baseVal.clear();");

            Assert.Equal(string.Empty, document.GetElementById("p").GetAttribute("patternTransform"));
        }

        [Fact]
        public async Task TheListReadsTheAttributeAndFollowsItsChanges()
        {
            var (engine, _) = await CreateAsync();

            var result = engine.Evaluate(
                """
                var r = document.getElementById('r');
                var list = r.transform.baseVal;
                var out = [r.transform === r.transform, list === r.transform.baseVal, list.numberOfItems];
                var first = list.getItem(0);
                out.push(first.type, first.angle, list[1].type);
                r.setAttribute('transform', 'bogus');
                out.push(list.numberOfItems);
                r.setAttribute('transform', 'skewX(30)');
                out.push(list.getItem(0).type, list.getItem(0).angle);
                out.join('|');
                """);

            Assert.Equal("true|true|2|4|90|3|0|5|30", result?.ToString());
        }

        [Fact]
        public async Task ItemMutations_WriteThrough()
        {
            var (engine, document) = await CreateAsync();

            engine.Evaluate(
                """
                var list = document.getElementById('r').transform.baseVal;
                list.getItem(1).setScale(3, 4);
                list.getItem(0).matrix.e = 7;
                """);

            Assert.Equal("matrix(0 1 -1 0 7 0) scale(3 4)",
                document.GetElementById("r").GetAttribute("transform"));
        }

        [Fact]
        public async Task InsertRemoveReplaceAndConsolidate()
        {
            var (engine, document) = await CreateAsync();

            var result = engine.Evaluate(
                """
                var svg = document.documentElement;
                var list = document.getElementById('g').transform.baseVal;
                var a = svg.createSVGTransform(); a.setTranslate(10, 0);
                var b = svg.createSVGTransform(); b.setScale(2, 2);
                list.appendItem(a);
                list.insertItemBefore(b, 0);
                var out = [list.numberOfItems, list.getItem(0).type];
                list.replaceItem(a, 0);
                out.push(list.getItem(0) !== a, list.getItem(0).type);
                var removed = list.removeItem(1);
                out.push(removed === a, list.numberOfItems);
                list.appendItem(b);
                var c = list.consolidate();
                out.push(list.numberOfItems, c.type, c.matrix.a, c.matrix.e);
                out.join('|');
                """);

            Assert.Equal("2|3|true|2|true|1|1|1|2|10", result?.ToString());
            Assert.Equal("matrix(2 0 0 2 10 0)", document.GetElementById("g").GetAttribute("transform"));
        }

        [Fact]
        public async Task Errors_FollowTheDomExceptionContract()
        {
            var (engine, _) = await CreateAsync();

            var result = engine.Evaluate(
                """
                function name(f) { try { f(); return 'none'; } catch (e) { return e.name; } }
                var anim = document.getElementById('r').transform;
                [
                    name(function () { anim.baseVal.getItem(5); }),
                    name(function () { anim.animVal.clear(); }),
                    name(function () { anim.animVal.getItem(0).setScale(1, 1); }),
                    name(function () { anim.baseVal.appendItem({}); }),
                    name(function () { document.documentElement.createSVGTransform().setTranslate(NaN, 0); })
                ].join('|');
                """);

            Assert.Equal("IndexSizeError|NoModificationAllowedError|NoModificationAllowedError|TypeError|TypeError",
                result?.ToString());
        }

        [Fact]
        public async Task OnlyTheReflectingElementsExposeTheLists()
        {
            var (engine, _) = await CreateAsync();

            var result = engine.Evaluate(
                """
                [typeof document.getElementById('lg').transform,
                 typeof document.getElementById('r').gradientTransform,
                 typeof document.getElementById('g').createSVGTransform].join('|');
                """);

            Assert.Equal("undefined|undefined|undefined", result?.ToString());
        }

        private static async Task<(FenJsBrowserScriptEngine Engine, Document Document)> CreateAsync()
        {
            var document = XmlDomParser.Parse(
                "<svg xmlns='http://www.w3.org/2000/svg'>" +
                "<linearGradient id='lg'/>" +
                "<pattern id='p' patternTransform='scale(2)'/>" +
                "<rect id='r' transform='rotate(90), scale(2)'/>" +
                "<g id='g'/></svg>",
                "image/svg+xml");
            var engine = new FenJsBrowserScriptEngine(new JsHostAdapter(
                navigate: _ => { }, post: (_, __) => { }, status: _ => { }, log: _ => { }))
            {
                Sandbox = SandboxPolicy.AllowAll
            };
            await engine.SetDomAsync(document.DocumentElement, new Uri("https://fen.test/doc.svg"));
            return (engine, document);
        }
    }
}
