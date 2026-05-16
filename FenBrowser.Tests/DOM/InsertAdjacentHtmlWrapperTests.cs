using FenBrowser.Core.Dom.V2;
using FenBrowser.FenEngine.Core;
using Xunit;

namespace FenBrowser.Tests.DOM
{
    public class InsertAdjacentHtmlWrapperTests
    {
        [Fact]
        public void InsertAdjacentHTML_BeforeEnd_MutatesDomViaDocumentWrapper()
        {
            var runtime = new FenRuntime();
            var document = Document.CreateHtmlDocument();
            runtime.SetDom(document);

            var anchor = document.CreateElement("a");
            anchor.SetAttribute("id", "javascript-detection");
            var noJsSpan = document.CreateElement("span");
            noJsSpan.SetAttribute("class", "detection-message no-javascript");
            noJsSpan.TextContent = "No - JavaScript is not enabled";
            anchor.AppendChild(noJsSpan);
            document.Body!.AppendChild(anchor);

            runtime.ExecuteSimple(@"
                var el = document.getElementById('javascript-detection');
                el.insertAdjacentHTML('beforeend', '<span class=""yes-js"">Yes - JavaScript is enabled</span>');
            ");

            var target = document.GetElementById("javascript-detection");
            Assert.NotNull(target);

            string allText = target!.TextContent ?? string.Empty;
            Assert.Contains("Yes - JavaScript is enabled", allText);
        }

        [Fact]
        public void InsertAdjacentHTML_IsExposedAsFunction_OnElementReturnedByGetElementById()
        {
            var runtime = new FenRuntime();
            var document = Document.CreateHtmlDocument();
            runtime.SetDom(document);

            var target = document.CreateElement("div");
            target.SetAttribute("id", "probe");
            document.Body!.AppendChild(target);

            runtime.ExecuteSimple(@"
                var el = document.getElementById('probe');
                window.__typeofInsertAdjacentHTML = typeof el.insertAdjacentHTML;
            ");

            Assert.Equal("function", runtime.GetGlobal("__typeofInsertAdjacentHTML").ToString());
        }
    }
}
