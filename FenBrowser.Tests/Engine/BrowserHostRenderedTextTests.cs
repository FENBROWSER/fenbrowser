using System;
using System.Linq;
using System.Reflection;
using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Rendering;
using Xunit;

namespace FenBrowser.Tests.Engine
{
    [Collection("Engine Tests")]
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

        [Fact]
        public void GetTextContent_IncludesSupplementalVisibleControlText()
        {
            using var host = new BrowserHost();
            var parser = new HtmlParser(
                "<html><body>" +
                "<input placeholder='Search the web' />" +
                "<textarea placeholder='Write a reply'></textarea>" +
                "<button aria-label='Open apps'></button>" +
                "<div aria-label='Settings'></div>" +
                "<input type='password' value='secret' />" +
                "</body></html>");
            var doc = parser.Parse();
            SetActiveDom(host, doc.DocumentElement);

            var renderedText = host.GetTextContent();

            Assert.Contains("Search the web", renderedText, StringComparison.Ordinal);
            Assert.Contains("Write a reply", renderedText, StringComparison.Ordinal);
            Assert.Contains("Open apps", renderedText, StringComparison.Ordinal);
            Assert.Contains("Settings", renderedText, StringComparison.Ordinal);
            Assert.DoesNotContain("secret", renderedText, StringComparison.Ordinal);
        }

        [Fact]
        public void GetTextContent_DoesNotDuplicateAriaFallback_WhenVisibleTextExists()
        {
            using var host = new BrowserHost();
            var parser = new HtmlParser(
                "<html><body><button aria-label='Should not appear'><span>Search</span></button></body></html>");
            var doc = parser.Parse();
            SetActiveDom(host, doc.DocumentElement);

            var renderedText = host.GetTextContent();
            var lines = renderedText
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Trim())
                .ToArray();

            Assert.Single(lines);
            Assert.Equal("Search", lines[0]);
            Assert.DoesNotContain("Should not appear", renderedText, StringComparison.Ordinal);
        }

        private static void SetActiveDom(BrowserHost host, Node node)
        {
            var engineField = typeof(BrowserHost).GetField("_engine", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(engineField);

            var engine = engineField!.GetValue(host);
            Assert.NotNull(engine);

            var activeDomField = engine.GetType().GetField("_activeDom", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(activeDomField);
            activeDomField!.SetValue(engine, node);
        }
    }
}
