using System.Linq;
using System.Reflection;
using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Rendering;
using Xunit;

namespace FenBrowser.Tests.Engine
{
    public class BrowserHostRenderedTextTests
    {
        [Fact]
        public void RenderedTextDump_SkipsTextInsideNonRenderedAncestors()
        {
            var parser = new HtmlParser(
                "<html><head><title>Hidden title</title><style>.x{color:red}</style></head><body><script>console.log('hidden');</script><p>Visible text</p></body></html>");
            var doc = parser.Parse();

            var textNodes = doc.SelfAndDescendants().OfType<Text>().ToList();
            var visibleText = textNodes.Single(t => t.TextContent.Contains("Visible text"));
            var scriptText = textNodes.Single(t => t.TextContent.Contains("console.log"));
            var titleText = textNodes.Single(t => t.TextContent.Contains("Hidden title"));

            var method = typeof(BrowserHost).GetMethod("ShouldSkipRenderedTextNode", BindingFlags.NonPublic | BindingFlags.Static);

            Assert.NotNull(method);
            Assert.False((bool)method!.Invoke(null, new object[] { visibleText })!);
            Assert.True((bool)method.Invoke(null, new object[] { scriptText })!);
            Assert.True((bool)method.Invoke(null, new object[] { titleText })!);
        }
    }
}
