using FenBrowser.Core.Dom.V2;
using FenBrowser.FenEngine.Core;
using Xunit;

namespace FenBrowser.Tests.DOM
{
    public class HighlightApiTests
    {
        [Fact]
        public void HighlightRegistry_And_SetlikeSurface_HandleChunk153Scenarios()
        {
            var runtime = new FenRuntime();
            var document = Document.CreateHtmlDocument();
            var host = document.CreateElement("div");
            host.SetAttribute("id", "host");
            host.TextContent = "abc";
            document.Body!.AppendChild(host);
            runtime.SetDom(document);

            runtime.ExecuteSimple(@"
                var host = document.getElementById('host');
                var staticRange = new StaticRange({ startContainer: host, startOffset: 0, endContainer: host, endOffset: 1 });
                var dynamicRange = new Range();
                dynamicRange.setStart(host, 0);
                dynamicRange.setEnd(host, 1);

                var highlight = new Highlight(staticRange);
                var iterator = highlight[Symbol.iterator]();
                highlight.add(dynamicRange);

                var firstValue = iterator.next().value;
                var secondValue = iterator.next().value;

                CSS.highlights.set('example', highlight);

                var __highlightSize = highlight.size;
                var __highlightFirstVisited = firstValue === staticRange;
                var __highlightSecondVisited = secondValue === dynamicRange;
                var __highlightHasDynamic = highlight.has(dynamicRange);
                var __registrySize = CSS.highlights.size;
                var __registryHasExample = CSS.highlights.has('example');
                var __registryEntryName = CSS.highlights.entries().next().value[0];
                var __defaultType = highlight.type;

                highlight.priority = 10;
                highlight.type = 'grammar-error';
                highlight.type = 'Spelling-error';

                var __priority = highlight.priority;
                var __type = highlight.type;
            ");

            Assert.Equal(2, runtime.GetGlobal("__highlightSize").ToNumber());
            Assert.True(runtime.GetGlobal("__highlightFirstVisited").ToBoolean());
            Assert.True(runtime.GetGlobal("__highlightSecondVisited").ToBoolean());
            Assert.True(runtime.GetGlobal("__highlightHasDynamic").ToBoolean());
            Assert.Equal(1, runtime.GetGlobal("__registrySize").ToNumber());
            Assert.True(runtime.GetGlobal("__registryHasExample").ToBoolean());
            Assert.Equal("example", runtime.GetGlobal("__registryEntryName").ToString());
            Assert.Equal("highlight", runtime.GetGlobal("__defaultType").ToString());
            Assert.Equal(10, runtime.GetGlobal("__priority").ToNumber());
            Assert.Equal("grammar-error", runtime.GetGlobal("__type").ToString());
        }

        [Fact]
        public void HighlightRegistry_MaplikeSpread_RemainsStableWhenMapPrototypeIsTampered()
        {
            var runtime = new FenRuntime();
            var document = Document.CreateHtmlDocument();
            runtime.SetDom(document);

            runtime.ExecuteSimple(@"
                delete Map.prototype.size;
                Map.prototype.entries = null;
                Map.prototype.forEach = undefined;
                Map.prototype.get = 'foo';
                Map.prototype.has = 0;
                Map.prototype.keys = Symbol();
                Map.prototype.values = 1;
                Map.prototype[Symbol.iterator] = true;
                Map.prototype.clear = false;
                Map.prototype.delete = '';
                Map.prototype.set = 3.14;

                var highlight = new Highlight(new StaticRange({
                    startContainer: document.body,
                    endContainer: document.body,
                    startOffset: 0,
                    endOffset: 0
                }));

                CSS.highlights.set('foo', highlight);
                var __entryName = [...CSS.highlights][0][0];
                var __entryValue = [...CSS.highlights][0][1];
                var __valueEntry = [...CSS.highlights.values()][0];
            ");

            Assert.Equal("foo", runtime.GetGlobal("__entryName").ToString());
            Assert.True(runtime.GetGlobal("__entryValue").StrictEquals(runtime.GetGlobal("__valueEntry")));
        }

        [Fact]
        public void GetComputedStyle_HighlightPseudo_ResolvesColorsAndRelativeLengths()
        {
            var runtime = new FenRuntime();
            var document = Document.CreateHtmlDocument();
            document.DocumentElement!.SetAttribute("style", "font-size: 16px;");

            var style = document.CreateElement("style");
            style.TextContent = @"
                #target::highlight(foo) { background-color: green; color: lime; }
                #target::highlight(bar) { background-color: cyan; color: fuchsia; }
                ::highlight(highlight1) {
                    text-underline-offset: 0.5em;
                    text-decoration-thickness: 0.25rem;
                }
                #target::highlight(highlight1) {
                    text-underline-offset: 1em;
                    text-decoration-thickness: 0.125rem;
                }";
            document.Head!.AppendChild(style);

            var target = document.CreateElement("div");
            target.SetAttribute("id", "target");
            target.SetAttribute("style", "font-size: 20px;");
            target.TextContent = "target";
            document.Body!.AppendChild(target);
            runtime.SetDom(document);

            runtime.ExecuteSimple(@"
                var foo = getComputedStyle(document.getElementById('target'), '::highlight(foo)');
                var bar = getComputedStyle(document.getElementById('target'), '::highlight(bar)');
                var highlight = getComputedStyle(document.getElementById('target'), '::highlight(highlight1)');
                var invalid = getComputedStyle(document.getElementById('target'), '::highlight(foo):');

                var __fooBackground = foo.backgroundColor;
                var __fooColor = foo.color;
                var __barBackground = bar.backgroundColor;
                var __barColor = bar.color;
                var __highlightOffset = highlight.textUnderlineOffset;
                var __highlightThickness = highlight.textDecorationThickness;
                var __invalidLength = invalid.length;
            ");

            Assert.Equal("rgb(0, 128, 0)", runtime.GetGlobal("__fooBackground").ToString());
            Assert.Equal("rgb(0, 255, 0)", runtime.GetGlobal("__fooColor").ToString());
            Assert.Equal("rgb(0, 255, 255)", runtime.GetGlobal("__barBackground").ToString());
            Assert.Equal("rgb(255, 0, 255)", runtime.GetGlobal("__barColor").ToString());
            Assert.Equal("20px", runtime.GetGlobal("__highlightOffset").ToString());
            Assert.Equal("2px", runtime.GetGlobal("__highlightThickness").ToString());
            Assert.Equal(0, runtime.GetGlobal("__invalidLength").ToNumber());
        }

        [Fact]
        public void GetComputedStyle_HighlightPseudo_UsesStylesheetFontSizeAndSpecificSelectors()
        {
            var runtime = new FenRuntime();
            var document = Document.CreateHtmlDocument();

            var style = document.CreateElement("style");
            style.TextContent = @"
                :root { font-size: 16px; }
                div { font-size: 20px; }
                ::highlight(highlight1) {
                    text-underline-offset: 0.5em;
                    text-decoration-line: underline;
                    text-decoration-color: green;
                    text-decoration-thickness: 0.25rem;
                }
                #h2::highlight(highlight1) {
                    text-underline-offset: 1em;
                    text-decoration-line: underline;
                    text-decoration-color: blue;
                    text-decoration-thickness: 0.125rem;
                }";
            document.Head!.AppendChild(style);

            var h1 = document.CreateElement("div");
            h1.SetAttribute("id", "h1");
            h1.TextContent = "A";
            var h2 = document.CreateElement("div");
            h2.SetAttribute("id", "h2");
            h2.TextContent = "B";
            document.Body!.AppendChild(h1);
            document.Body!.AppendChild(h2);

            runtime.SetDom(document);

            runtime.ExecuteSimple(@"
                var r1 = new Range();
                r1.setStart(document.getElementById('h1'), 0);
                r1.setEnd(document.getElementById('h1'), 1);
                var r2 = new Range();
                r2.setStart(document.getElementById('h2'), 0);
                r2.setEnd(document.getElementById('h2'), 1);
                CSS.highlights.set('highlight1', new Highlight(r1, r2));

                var rootStyle = getComputedStyle(document.documentElement, '::highlight(highlight1)');
                var h1Style = getComputedStyle(document.getElementById('h1'), '::highlight(highlight1)');
                var h2Style = getComputedStyle(document.getElementById('h2'), '::highlight(highlight1)');

                var __rootOffset = rootStyle.textUnderlineOffset;
                var __rootThickness = rootStyle.textDecorationThickness;
                var __h1Offset = h1Style.textUnderlineOffset;
                var __h1Thickness = h1Style.textDecorationThickness;
                var __h2Offset = h2Style.textUnderlineOffset;
                var __h2Thickness = h2Style.textDecorationThickness;
            ");

            Assert.Equal("8px", runtime.GetGlobal("__rootOffset").ToString());
            Assert.Equal("4px", runtime.GetGlobal("__rootThickness").ToString());
            Assert.Equal("10px", runtime.GetGlobal("__h1Offset").ToString());
            Assert.Equal("4px", runtime.GetGlobal("__h1Thickness").ToString());
            Assert.Equal("20px", runtime.GetGlobal("__h2Offset").ToString());
            Assert.Equal("2px", runtime.GetGlobal("__h2Thickness").ToString());
        }

        [Fact]
        public void OffsetDimensions_StretchGridChildren_ToParentTrackSize()
        {
            var runtime = new FenRuntime();
            var document = Document.CreateHtmlDocument();
            var grid = document.CreateElement("div");
            grid.SetAttribute("id", "grid");
            grid.SetAttribute("style", "display: grid; width: 400px; height: 400px;");

            var item = document.CreateElement("div");
            item.SetAttribute("id", "item");
            item.TextContent = "table cell";
            grid.AppendChild(item);
            document.Body!.AppendChild(grid);
            runtime.SetDom(document);

            runtime.ExecuteSimple(@"
                var item = document.getElementById('item');
                var __offsetWidth = item.offsetWidth;
                var __offsetHeight = item.offsetHeight;
            ");

            Assert.Equal(400, runtime.GetGlobal("__offsetWidth").ToNumber());
            Assert.Equal(400, runtime.GetGlobal("__offsetHeight").ToNumber());
        }
    }
}
