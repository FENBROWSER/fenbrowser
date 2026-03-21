using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using FenBrowser.Core.Dom.V2;
using FenBrowser.FenEngine.Rendering;
using Xunit;

namespace FenBrowser.Tests.Rendering
{
    [Collection("Engine Tests")]
    public class BrowserHostShadowDomTests
    {
        [Fact]
        public async Task GetShadowRootAsync_ReturnsRegisteredShadowRootId_ForOpenShadowRoot()
        {
            using var host = new BrowserHost();
            var document = Document.CreateHtmlDocument();
            var shadowHost = document.CreateElement("div");
            document.Body.AppendChild(shadowHost);
            shadowHost.AttachShadow(new ShadowRootInit { Mode = ShadowRootMode.Open });
            RegisterNode(host, "host-1", shadowHost);

            var shadowId = await host.GetShadowRootAsync("host-1");

            Assert.False(string.IsNullOrWhiteSpace(shadowId));
            Assert.IsType<ShadowRoot>(GetShadowRootMap(host)[shadowId]);
        }

        [Fact]
        public async Task FindElementAsync_AllowsSearchWithinRegisteredShadowRoot()
        {
            using var host = new BrowserHost();
            var document = Document.CreateHtmlDocument();
            var shadowHost = document.CreateElement("div");
            var lightChild = document.CreateElement("span");
            lightChild.SetAttribute("id", "light-child");
            shadowHost.AppendChild(lightChild);
            document.Body.AppendChild(shadowHost);

            var shadowRoot = shadowHost.AttachShadow(new ShadowRootInit { Mode = ShadowRootMode.Open });
            var shadowChild = document.CreateElement("span");
            shadowChild.SetAttribute("id", "shadow-child");
            shadowChild.TextContent = "inside shadow";
            shadowRoot.AppendChild(shadowChild);

            RegisterNode(host, "host-1", shadowHost);

            var shadowId = await host.GetShadowRootAsync("host-1");
            var foundElementId = await host.FindElementAsync("css selector", "#shadow-child", shadowId);

            Assert.False(string.IsNullOrWhiteSpace(foundElementId));
            Assert.Same(shadowChild, GetElementMap(host)[foundElementId]);
            Assert.Equal("inside shadow", await host.GetElementTextAsync(foundElementId));
            Assert.Null(await host.FindElementAsync("css selector", "#light-child", shadowId));
        }

        private static void RegisterNode(BrowserHost host, string nodeId, Element node)
        {
            GetElementMap(host)[nodeId] = node;
        }

        private static Dictionary<string, Element> GetElementMap(BrowserHost host)
        {
            var field = typeof(BrowserHost).GetField("_elementMap", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(field);

            var map = field!.GetValue(host) as Dictionary<string, Element>;
            Assert.NotNull(map);
            return map!;
        }

        private static Dictionary<string, ShadowRoot> GetShadowRootMap(BrowserHost host)
        {
            var field = typeof(BrowserHost).GetField("_shadowRootMap", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(field);

            var map = field!.GetValue(host) as Dictionary<string, ShadowRoot>;
            Assert.NotNull(map);
            return map!;
        }
    }
}
