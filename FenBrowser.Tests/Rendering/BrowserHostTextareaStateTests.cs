using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using FenBrowser.Core.Dom.V2;
using FenBrowser.FenEngine.Rendering;
using Xunit;

namespace FenBrowser.Tests.Rendering
{
    [Collection("Engine Tests")]
    public class BrowserHostTextareaStateTests
    {
        [Fact]
        public async Task SendKeysAndClear_SynchronizeTextareaAttributeAndTextContent()
        {
            var host = new BrowserHost();
            var textarea = new Element("textarea");
            RegisterElement(host, "textarea-1", textarea);

            await host.SendKeysToElementAsync("textarea-1", "fenbrowser");

            Assert.Equal("fenbrowser", textarea.GetAttribute("value"));
            Assert.Equal("fenbrowser", textarea.TextContent);

            await host.ClearElementAsync("textarea-1");

            Assert.Equal(string.Empty, textarea.GetAttribute("value"));
            Assert.Equal(string.Empty, textarea.TextContent);
        }

        [Fact]
        public async Task HandleKeyPress_SynchronizesTextareaAttributeAndTextContent()
        {
            var host = new BrowserHost();
            var textarea = new Element("textarea");
            textarea.SetAttribute("value", string.Empty);
            textarea.TextContent = string.Empty;
            SetFocusedElement(host, textarea);

            await host.HandleKeyPress("f");
            await host.HandleKeyPress("e");
            await host.HandleKeyPress("n");

            Assert.Equal("fen", textarea.GetAttribute("value"));
            Assert.Equal("fen", textarea.TextContent);
        }

        private static void RegisterElement(BrowserHost host, string elementId, Element element)
        {
            var mapField = typeof(BrowserHost).GetField("_elementMap", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(mapField);

            var elementMap = mapField!.GetValue(host) as Dictionary<string, Element>;
            Assert.NotNull(elementMap);

            elementMap![elementId] = element;
        }

        private static void SetFocusedElement(BrowserHost host, Element element)
        {
            var focusedField = typeof(BrowserHost).GetField("_focusedElement", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(focusedField);
            focusedField!.SetValue(host, element);
        }
    }
}
