using FenBrowser.Core.Dom.V2;
using FenRuntimeCore = FenBrowser.FenEngine.Core.FenRuntime;
using Xunit;

namespace FenBrowser.Tests.DOM
{
    public class EventHandlerPropertyCompatibilityTests
    {
        [Fact]
        public void Element_EventHandlerProperties_DefaultToNull_AndNormalizeNonCallableValues()
        {
            var runtime = new FenRuntimeCore();
            var document = Document.CreateHtmlDocument();
            runtime.SetDom(document);

            runtime.ExecuteSimple(@"
                var div = document.createElement('div');
                window.__defaultHandlersNull =
                    div.onanimationstart === null &&
                    div.ontransitionend === null &&
                    div.onwebkitanimationend === null;

                div.onanimationstart = function () {};
                window.__functionAssignmentWorks = typeof div.onanimationstart === 'function';

                div.onanimationstart = 42;
                window.__nonCallableAssignmentNormalized = div.onanimationstart === null;
            ");

            var window = runtime.GetGlobal("window").AsObject();
            Assert.NotNull(window);
            Assert.True(window.Get("__defaultHandlersNull").ToBoolean());
            Assert.True(window.Get("__functionAssignmentWorks").ToBoolean());
            Assert.True(window.Get("__nonCallableAssignmentNormalized").ToBoolean());
        }

        [Fact]
        public void Window_EventHandlerProperties_DefaultToNull()
        {
            var runtime = new FenRuntimeCore();
            var document = Document.CreateHtmlDocument();
            runtime.SetDom(document);

            runtime.ExecuteSimple(@"
                window.__windowHandlersNull =
                    window.onanimationstart === null &&
                    window.ontransitionend === null &&
                    window.onwebkitanimationend === null;
            ");

            var window = runtime.GetGlobal("window").AsObject();
            Assert.NotNull(window);
            Assert.True(window.Get("__windowHandlersNull").ToBoolean());
        }
    }
}
