using System.Reflection;
using System.Threading.Tasks;
using FenBrowser.Core.Dom.V2;
using FenBrowser.FenEngine.Core;
using FenBrowser.FenEngine.Rendering;
using Xunit;
using FenEventTarget = FenBrowser.FenEngine.DOM.EventTarget;

namespace FenBrowser.Tests.Interaction
{
    [Collection("Engine Tests")]
    public class ButtonInteractionContractTests
    {
        [Fact]
        public async Task HandleKeyPress_EnterOnFocusedButton_DispatchesClick()
        {
            var host = new BrowserHost();
            var button = new Element("button");
            int clickCount = 0;

            var handler = FenValue.FromFunction(new FenFunction("buttonClick", (args, thisVal) =>
            {
                clickCount++;
                return FenValue.Undefined;
            }));

            FenEventTarget.Registry.Add(button, "click", handler, false);
            try
            {
                SetFocusedElement(host, button);
                await host.HandleKeyPress("Enter");
                Assert.Equal(1, clickCount);
            }
            finally
            {
                FenEventTarget.Registry.Remove(button, "click", handler, false);
            }
        }

        [Fact]
        public async Task HandleKeyPress_EnterOnDisabledButton_DoesNotDispatchClick()
        {
            var host = new BrowserHost();
            var button = new Element("button");
            button.SetAttribute("disabled", string.Empty);
            int clickCount = 0;

            var handler = FenValue.FromFunction(new FenFunction("buttonClickDisabled", (args, thisVal) =>
            {
                clickCount++;
                return FenValue.Undefined;
            }));

            FenEventTarget.Registry.Add(button, "click", handler, false);
            try
            {
                SetFocusedElement(host, button);
                await host.HandleKeyPress("Enter");
                Assert.Equal(0, clickCount);
            }
            finally
            {
                FenEventTarget.Registry.Remove(button, "click", handler, false);
            }
        }

        private static void SetFocusedElement(BrowserHost host, Element element)
        {
            var focusedField = typeof(BrowserHost).GetField("_focusedElement", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(focusedField);
            focusedField!.SetValue(host, element);
        }
    }
}
