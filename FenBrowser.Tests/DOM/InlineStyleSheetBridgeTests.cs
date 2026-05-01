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

        [Fact]
        public void StyleElement_Sheet_ObjectAndCssRulesIdentity_AreStableAndLive()
        {
            var runtime = new FenRuntimeCore();
            var document = Document.CreateHtmlDocument();
            runtime.SetDom(document);

            runtime.ExecuteSimple(@"
                var style = document.createElement('style');
                style.textContent = 'body { color: red; }';
                document.body.appendChild(style);

                var sheetA = style.sheet;
                var sheetB = style.sheet;
                var rules = sheetA.cssRules;

                window.__sameSheetObject = sheetA === sheetB;
                window.__sameRulesObjectBeforeMutation = rules === sheetA.cssRules;
                window.__rulesLengthBefore = rules.length;

                sheetA.insertRule('div { color: blue; }', 1);
                window.__rulesLengthAfterInsert = rules.length;

                sheetA.deleteRule(0);
                window.__rulesLengthAfterDelete = rules.length;
                window.__sameRulesObjectAfterMutation = rules === sheetA.cssRules;
                window.__firstRuleCssText = rules.item(0) ? rules.item(0).cssText : '';
            ");

            var window = runtime.GetGlobal("window").AsObject();
            Assert.NotNull(window);
            Assert.Equal("true", window.Get("__sameSheetObject").ToString());
            Assert.Equal("true", window.Get("__sameRulesObjectBeforeMutation").ToString());
            Assert.Equal(1, (int)window.Get("__rulesLengthBefore").ToNumber());
            Assert.Equal(2, (int)window.Get("__rulesLengthAfterInsert").ToNumber());
            Assert.Equal(1, (int)window.Get("__rulesLengthAfterDelete").ToNumber());
            Assert.Equal("true", window.Get("__sameRulesObjectAfterMutation").ToString());
            Assert.Contains("div", window.Get("__firstRuleCssText").ToString(), System.StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void StyleElement_Sheet_InsertRule_WithoutIndex_DefaultsToZero()
        {
            var runtime = new FenRuntimeCore();
            var document = Document.CreateHtmlDocument();
            runtime.SetDom(document);

            runtime.ExecuteSimple(@"
                var style = document.createElement('style');
                style.textContent = 'body { color: red; }';
                document.body.appendChild(style);
                var sheet = style.sheet;
                sheet.insertRule('div { color: blue; }');
                window.__firstRuleCssText = sheet.cssRules.item(0) ? sheet.cssRules.item(0).cssText : '';
                window.__secondRuleCssText = sheet.cssRules.item(1) ? sheet.cssRules.item(1).cssText : '';
            ");

            var window = runtime.GetGlobal("window").AsObject();
            Assert.NotNull(window);
            Assert.Contains("div", window.Get("__firstRuleCssText").ToString(), System.StringComparison.OrdinalIgnoreCase);
            Assert.Contains("body", window.Get("__secondRuleCssText").ToString(), System.StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void StyleElement_Sheet_InsertRule_OutOfRange_ThrowsIndexSizeError()
        {
            var runtime = new FenRuntimeCore();
            var document = Document.CreateHtmlDocument();
            runtime.SetDom(document);

            runtime.ExecuteSimple(@"
                var style = document.createElement('style');
                style.textContent = 'body { color: red; }';
                document.body.appendChild(style);
                var sheet = style.sheet;
                try {
                    sheet.insertRule('div { color: blue; }', 5);
                    window.__insertErrorName = '';
                    window.__insertErrorMessage = '';
                } catch (e) {
                    window.__insertErrorName = e && e.name ? String(e.name) : '';
                    window.__insertErrorMessage = e && e.message ? String(e.message) : String(e);
                }
                window.__rulesLengthAfterInsert = sheet && sheet.cssRules ? sheet.cssRules.length : -1;
            ");

            var window = runtime.GetGlobal("window").AsObject();
            Assert.NotNull(window);
            Assert.Equal("RangeError", window.Get("__insertErrorName").ToString());
            Assert.Contains("IndexSizeError", window.Get("__insertErrorMessage").ToString());
            Assert.Equal(1, (int)window.Get("__rulesLengthAfterInsert").ToNumber());
        }

        [Fact]
        public void StyleElement_Sheet_DeleteRule_OutOfRange_ThrowsIndexSizeError()
        {
            var runtime = new FenRuntimeCore();
            var document = Document.CreateHtmlDocument();
            runtime.SetDom(document);

            runtime.ExecuteSimple(@"
                var style = document.createElement('style');
                style.textContent = 'body { color: red; } div { color: blue; }';
                document.body.appendChild(style);
                var sheet = style.sheet;
                try {
                    sheet.deleteRule(99);
                    window.__deleteErrorName = '';
                    window.__deleteErrorMessage = '';
                } catch (e) {
                    window.__deleteErrorName = e && e.name ? String(e.name) : '';
                    window.__deleteErrorMessage = e && e.message ? String(e.message) : String(e);
                }
                window.__rulesLengthAfterDelete = sheet && sheet.cssRules ? sheet.cssRules.length : -1;
            ");

            var window = runtime.GetGlobal("window").AsObject();
            Assert.NotNull(window);
            Assert.Equal("RangeError", window.Get("__deleteErrorName").ToString());
            Assert.Contains("IndexSizeError", window.Get("__deleteErrorMessage").ToString());
            Assert.Equal(2, (int)window.Get("__rulesLengthAfterDelete").ToNumber());
        }

        [Fact]
        public void StyleElement_Sheet_InsertRule_MultipleRules_ThrowsSyntaxError()
        {
            var runtime = new FenRuntimeCore();
            var document = Document.CreateHtmlDocument();
            runtime.SetDom(document);

            runtime.ExecuteSimple(@"
                var style = document.createElement('style');
                document.body.appendChild(style);
                var sheet = style.sheet;
                try {
                    sheet.insertRule('a { color: red; } b { color: blue; }', 0);
                    window.__syntaxErrorName = '';
                    window.__syntaxErrorMessage = '';
                } catch (e) {
                    window.__syntaxErrorName = e && e.name ? String(e.name) : '';
                    window.__syntaxErrorMessage = e && e.message ? String(e.message) : String(e);
                }
                window.__rulesLengthAfterSyntaxError = sheet && sheet.cssRules ? sheet.cssRules.length : -1;
            ");

            var window = runtime.GetGlobal("window").AsObject();
            Assert.NotNull(window);
            Assert.Equal("SyntaxError", window.Get("__syntaxErrorName").ToString());
            Assert.Contains("insertRule requires exactly one rule", window.Get("__syntaxErrorMessage").ToString());
            Assert.Equal(0, (int)window.Get("__rulesLengthAfterSyntaxError").ToNumber());
        }
    }
}
