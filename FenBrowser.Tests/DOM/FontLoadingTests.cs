using FenBrowser.Core.Dom.V2;
using FenBrowser.FenEngine.Core;
using FenBrowser.FenEngine.Core.EventLoop;
using FenBrowser.FenEngine.Core.Types;
using Xunit;

namespace FenBrowser.Tests.DOM
{
    public class FontLoadingTests
    {
        [Fact]
        public void DocumentFonts_TracksCssConnectedFacesAndConstructorSurface()
        {
            var runtime = new FenRuntime();
            var document = Document.CreateHtmlDocument();
            var style = document.CreateElement("style");
            style.SetAttribute("id", "font-style");
            style.TextContent = "@font-face { font-family: \"WebFont\"; src: url(\"file://missing.otf\"); }";
            document.Head!.AppendChild(style);
            runtime.SetDom(document);

            runtime.ExecuteSimple(@"
                var __fontsSize = document.fonts.size;
                var __fontsStatus = document.fonts.status;
                var __fontsReadyType = typeof document.fonts.ready;
                var __fontsReadyResolved = false;
                document.fonts.ready.then(function (fonts) {
                    __fontsReadyResolved = fonts === document.fonts;
                });
                var __fontsCheck = document.fonts.check('16px WebFont');
                var __fontsAddEventListenerType = typeof document.fonts.addEventListener;
                var __cssFace = document.fonts.keys().next().value;
                var __hasCssFace = document.fonts.has(__cssFace);
                var __ctorError = '';
                try { new document.fonts.constructor([]); } catch (e) { __ctorError = e.name || String(e); }
            ");

            EventLoopCoordinator.Instance.RunUntilEmpty();

            document.Head!.RemoveChild(style);
            runtime.ExecuteSimple("var __fontsSizeAfterRemove = document.fonts.size;");

            Assert.Equal(1, runtime.GetGlobal("__fontsSize").ToNumber());
            Assert.Equal("loaded", runtime.GetGlobal("__fontsStatus").ToString());
            Assert.Equal("object", runtime.GetGlobal("__fontsReadyType").ToString());
            Assert.True(runtime.GetGlobal("__fontsReadyResolved").ToBoolean());
            Assert.True(runtime.GetGlobal("__fontsCheck").ToBoolean());
            Assert.Equal("function", runtime.GetGlobal("__fontsAddEventListenerType").ToString());
            Assert.True(runtime.GetGlobal("__hasCssFace").ToBoolean());
            Assert.Equal("TypeError", runtime.GetGlobal("__ctorError").ToString());
            Assert.Equal(0, runtime.GetGlobal("__fontsSizeAfterRemove").ToNumber());
        }

        [Fact]
        public void FontFace_LoadedPromiseAndDescriptorValidationMatchWptNeeds()
        {
            var runtime = new FenRuntime();
            runtime.SetDom(Document.CreateHtmlDocument());

            runtime.ExecuteSimple(@"
                var __variation = new FontFace('wght', 'local(""Ahem"")', { variationSettings: ""'wght' 850"" }).variationSettings;
                var __setterError = '';
                var __loaded = new FontFace('TestFontFace', 'local(""nonexistentfont-9a1a9f78-c8d4-11e9-af16-448a5b2c326f"")').loaded;
                var face = new FontFace('metrics', 'url(font.woff)', { ascentOverride: '-50%' });
                var __loadPromise = face.load();
                try { face.lineGapOverride = '10px'; } catch (e) { __setterError = e.message || e.name || String(e); }
            ");

            var loaded = Assert.IsType<JsPromise>(runtime.GetGlobal("__loaded").AsObject());
            var loadPromise = Assert.IsType<JsPromise>(runtime.GetGlobal("__loadPromise").AsObject());
            var loadedError = Assert.IsType<FenObject>(loaded.Result.AsObject());
            var loadError = Assert.IsType<FenObject>(loadPromise.Result.AsObject());

            Assert.True(loaded.IsRejected);
            Assert.Equal("NetworkError", loadedError.Get("name").ToString());
            Assert.True(loadPromise.IsRejected);
            Assert.Equal("SyntaxError", loadError.Get("name").ToString());
            Assert.Equal("\"wght\" 850", runtime.GetGlobal("__variation").ToString());
            Assert.Contains("SyntaxError", runtime.GetGlobal("__setterError").ToString());
        }
    }
}
