using System;
using System.Linq;
using System.Threading.Tasks;
using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Rendering;
using FenBrowser.FenEngine.Rendering.Css;
using NewCss = FenBrowser.FenEngine.Rendering.Css;
using Xunit;

namespace FenBrowser.Tests.Engine
{
    public class CssLoaderIsolationRegressionTests
    {
        [Fact]
        public async Task ParseCache_RespectsBaseUri_ForRelativeBackgroundImageUrls()
        {
            const string html = @"<!doctype html><html><head></head><body><div id='hero'>hero</div></body></html>";
            const string css = "#hero { background-image: url('assets/bg.png'); }";

            var firstBaseUri = new Uri("https://first.example.test/path-a/");
            var secondBaseUri = new Uri("https://second.example.test/path-b/");

            var firstDoc = new HtmlParser(html, firstBaseUri).Parse();
            var firstRoot = firstDoc.DocumentElement ?? firstDoc.Children.OfType<Element>().First();
            var firstHero = firstRoot.Descendants().OfType<Element>().First(e => string.Equals(e.Id, "hero", StringComparison.Ordinal));
            var firstSources = new System.Collections.Generic.List<CssLoader.CssSource>
            {
                new CssLoader.CssSource
                {
                    CssText = css,
                    Origin = CssLoader.CssOrigin.Inline,
                    SourceOrder = 0,
                    BaseUri = firstBaseUri
                }
            };
            var firstMatch = CssLoader.GetMatchedRules(firstHero, firstSources)
                .Select(m => m.Rule)
                .OfType<NewCss.CssStyleRule>()
                .First();
            Assert.Equal(firstBaseUri, firstMatch.BaseUri);

            var secondDoc = new HtmlParser(html, secondBaseUri).Parse();
            var secondRoot = secondDoc.DocumentElement ?? secondDoc.Children.OfType<Element>().First();
            var secondHero = secondRoot.Descendants().OfType<Element>().First(e => string.Equals(e.Id, "hero", StringComparison.Ordinal));
            var secondSources = new System.Collections.Generic.List<CssLoader.CssSource>
            {
                new CssLoader.CssSource
                {
                    CssText = css,
                    Origin = CssLoader.CssOrigin.Inline,
                    SourceOrder = 0,
                    BaseUri = secondBaseUri
                }
            };
            var secondMatch = CssLoader.GetMatchedRules(secondHero, secondSources)
                .Select(m => m.Rule)
                .OfType<NewCss.CssStyleRule>()
                .First();
            Assert.Equal(secondBaseUri, secondMatch.BaseUri);
        }

    }
}
