using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using FenBrowser.Core.Dom.V2;
using FenBrowser.FenEngine.Rendering;
using Xunit;

namespace FenBrowser.Tests.Rendering
{
    [Collection("Engine Tests")]
    public class BrowserHostElementPropertyTests
    {
        [Fact]
        public async Task GetElementPropertyAsync_InMemoryAttributeLookup_CompletesSynchronously()
        {
            using var host = new BrowserHost();
            var element = new Element("input");
            element.SetAttribute("value", "fenbrowser");
            GetElementMap(host)["element-1"] = element;

            var task = host.GetElementPropertyAsync("element-1", "value");

            Assert.True(task.IsCompletedSuccessfully);
            Assert.Equal("fenbrowser", await task);
        }

        [Fact]
        public async Task GetElementPropertyAsync_MissingProperty_ReturnsCompletedNull()
        {
            using var host = new BrowserHost();
            GetElementMap(host)["element-2"] = new Element("div");

            var task = host.GetElementPropertyAsync("element-2", "value");

            Assert.True(task.IsCompletedSuccessfully);
            Assert.Null(await task);
        }

        private static Dictionary<string, Element> GetElementMap(BrowserHost host)
        {
            var field = typeof(BrowserHost).GetField("_elementMap", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(field);

            var map = field!.GetValue(host) as Dictionary<string, Element>;
            Assert.NotNull(map);
            return map!;
        }
    }
}
