using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FenBrowser.Core.Css;
using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Layout.Tree;
using FenBrowser.FenEngine.Rendering;
using FenBrowser.FenEngine.Rendering.UserAgent;
using Xunit;

namespace FenBrowser.Tests.Engine
{
    public class WebCompatibilityPolicyTests
    {
        [Fact]
        public async Task ComputeAsync_DoesNotInjectSemanticContainerMaxWidthFallback()
        {
            string html = @"
<!doctype html>
<html>
<body>
  <article id='a'>A</article>
  <nav id='n'>N</nav>
  <main id='m'>M</main>
</body>
</html>";

            var parser = new HtmlParser(html);
            var doc = parser.Parse();
            var root = doc.Children.OfType<Element>().First(e => e.TagName == "HTML");
            var computed = await CssLoader.ComputeAsync(root, new Uri("https://test.local"), null);

            var article = doc.Descendants().OfType<Element>().First(e => e.Id == "a");
            var nav = doc.Descendants().OfType<Element>().First(e => e.Id == "n");
            var main = doc.Descendants().OfType<Element>().First(e => e.Id == "m");

            Assert.False(computed[article].MaxWidth.HasValue);
            Assert.False(computed[nav].MaxWidth.HasValue);
            Assert.False(computed[main].MaxWidth.HasValue);
        }

        [Fact]
        public async Task ComputeAsync_DoesNotOverrideAuthorAnchorTextDecoration()
        {
            string html = @"
<!doctype html>
<html>
<body>
  <a id='lnk' href='https://example.test' style='text-decoration: underline;'>Link</a>
</body>
</html>";

            var parser = new HtmlParser(html);
            var doc = parser.Parse();
            var root = doc.Children.OfType<Element>().First(e => e.TagName == "HTML");
            var computed = await CssLoader.ComputeAsync(root, new Uri("https://test.local"), null);

            var link = doc.Descendants().OfType<Element>().First(e => e.Id == "lnk");
            var style = computed[link];

            Assert.Equal("underline", style.TextDecoration, ignoreCase: true);
        }

        [Fact]
        public void BoxTreeBuilder_CustomElementsDefaultToInlineWithoutAuthorCss()
        {
            var root = new Element("div");
            var custom = new Element("g-popup");
            root.AppendChild(custom);

            var styles = new Dictionary<Node, CssComputed>
            {
                [root] = new CssComputed { Display = "block" }
            };

            var builder = new BoxTreeBuilder(styles);
            var rootBox = builder.Build(root);

            Assert.NotNull(rootBox);
            Assert.Single(rootBox.Children);
            Assert.IsType<InlineBox>(rootBox.Children[0]);
        }

        [Fact]
        public void UAStyleProvider_DoesNotInjectSiteSpecificSignInStyling()
        {
            var anchor = new Element("a");
            anchor.SetAttribute("aria-label", "Sign in");

            var style = new CssComputed();
            UAStyleProvider.Apply(anchor, ref style);

            Assert.False(style.BackgroundColor.HasValue);
            Assert.Equal(0, style.Padding.Left);
            Assert.Equal(0, style.Padding.Right);
            Assert.False(style.FontWeight.HasValue);
        }
    }
}
