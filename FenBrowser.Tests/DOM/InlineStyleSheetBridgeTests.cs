using FenBrowser.Core.Dom.V2;
using FenRuntimeCore = FenBrowser.FenEngine.Core.FenRuntime;
using Xunit;

namespace FenBrowser.Tests.DOM
{
    public class InlineStyleSheetBridgeTests
    {
        [Fact]
        public void StyleElement_Sheet_InsertRule_IsAvailable()
        {
            var runtime = new FenRuntimeCore();
            var document = Document.CreateHtmlDocument();
            runtime.SetDom(document);

            runtime.ExecuteSimple(@"
                var style = document.createElement('style');
                document.body.appendChild(style);
                var sheet = style.sheet;
                window.__sheetType = typeof sheet;
                window.__insertRuleType = sheet ? typeof sheet.insertRule : 'undefined';
                if (sheet && typeof sheet.insertRule === 'function') {
                  sheet.insertRule('body { color: red; }', 0);
                }
                window.__rulesLength = sheet && sheet.cssRules ? sheet.cssRules.length : -1;
            ");

            var window = runtime.GetGlobal("window").AsObject();
            Assert.NotNull(window);
            Assert.Equal("object", window.Get("__sheetType").ToString());
            Assert.Equal("function", window.Get("__insertRuleType").ToString());
            Assert.Equal(1, (int)window.Get("__rulesLength").ToNumber());
        }
    }
}
