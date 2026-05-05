using System;
using System.Reflection;
using System.Threading.Tasks;
using FenBrowser.Core.Dom.V2;
using FenBrowser.FenEngine.Rendering;
using Xunit;

namespace FenBrowser.Tests.Interaction
{
    [Collection("Engine Tests")]
    public class LinkInteractionContractTests
    {
        [Fact]
        public async Task HandleElementClick_AnchorWithFragment_NavigatesFromCurrentDocument()
        {
            var host = new BrowserHost();
            SetCurrentUri(host, new Uri("about:blank"));

            var link = new Element("a");
            link.SetAttribute("href", "#section-1");

            await host.HandleElementClick(link);

            Assert.NotNull(host.CurrentUri);
            Assert.Equal("about:blank#section-1", host.CurrentUri!.AbsoluteUri);
        }

        [Fact]
        public async Task HandleElementClick_AnchorWithoutHref_DoesNotNavigate()
        {
            var host = new BrowserHost();
            SetCurrentUri(host, new Uri("about:blank#start"));

            var link = new Element("a");

            await host.HandleElementClick(link);

            Assert.NotNull(host.CurrentUri);
            Assert.Equal("about:blank#start", host.CurrentUri!.AbsoluteUri);
        }

        private static void SetCurrentUri(BrowserHost host, Uri uri)
        {
            var currentField = typeof(BrowserHost).GetField("_current", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(currentField);
            currentField!.SetValue(host, uri);
        }
    }
}
