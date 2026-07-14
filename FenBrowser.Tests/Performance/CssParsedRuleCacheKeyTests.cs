using System;
using System.Collections.Generic;
using System.Linq;
using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Rendering;
using NewCss = FenBrowser.FenEngine.Rendering.Css;
using Xunit;
using Xunit.Abstractions;

namespace FenBrowser.Tests.Performance
{
    public sealed class CssParsedRuleCacheKeyTests
    {
        private readonly ITestOutputHelper _output;

        public CssParsedRuleCacheKeyTests(ITestOutputHelper output)
        {
            _output = output;
        }

        [Fact]
        public void CacheKey_PreservesSourceOrder_ForDuplicateCssText()
        {
            CssLoader.ClearCaches();
            var (box, baseUri) = CreateBox("https://cache-order.test/");
            const string duplicateCss = ".box { color: red; }";
            var sources = new List<CssLoader.CssSource>
            {
                CreateSource(duplicateCss, baseUri, CssLoader.CssOrigin.Inline, 0),
                CreateSource(".box { color: green; }", baseUri, CssLoader.CssOrigin.Inline, 1),
                CreateSource(duplicateCss, baseUri, CssLoader.CssOrigin.Inline, 2)
            };

            var matched = CssLoader.GetMatchedRules(box, sources)
                .Where(match => match.Rule is NewCss.CssStyleRule)
                .ToList();

            Assert.Equal(3, matched.Count);
            Assert.Equal(
                new[] { 0, 10_000, 20_000 },
                matched.Select(match => ((NewCss.CssStyleRule)match.Rule).Order).ToArray());
            Assert.Equal(2, matched[^1].Source.SourceOrder);
        }

        [Fact]
        public void CacheKey_PreservesOrigin_ForDuplicateCssText()
        {
            CssLoader.ClearCaches();
            var (box, baseUri) = CreateBox("https://cache-origin.test/");
            const string duplicateCss = ".box { color: red; }";
            var sources = new List<CssLoader.CssSource>
            {
                CreateSource(duplicateCss, baseUri, CssLoader.CssOrigin.UserAgent, 0),
                CreateSource(duplicateCss, baseUri, CssLoader.CssOrigin.Inline, 1)
            };

            var matched = CssLoader.GetMatchedRules(box, sources)
                .Where(match => match.Rule is NewCss.CssStyleRule)
                .ToList();

            Assert.Equal(2, matched.Count);
            Assert.Equal(NewCss.CssOrigin.UserAgent, ((NewCss.CssStyleRule)matched[0].Rule).Origin);
            Assert.Equal(NewCss.CssOrigin.Author, ((NewCss.CssStyleRule)matched[1].Rule).Origin);
            Assert.Equal(1, matched[^1].Source.SourceOrder);
        }

        [Fact]
        public void CacheKey_PreservesBaseUri_ForRelativeUrls()
        {
            CssLoader.ClearCaches();
            const string css = ".box { background-image: url('assets/bg.png'); }";
            var (firstBox, firstBaseUri) = CreateBox("https://first.example.test/path-a/");
            var firstSources = new List<CssLoader.CssSource>
            {
                CreateSource(css, firstBaseUri, CssLoader.CssOrigin.Inline, 0)
            };
            var firstRule = CssLoader.GetMatchedRules(firstBox, firstSources)
                .Select(match => match.Rule)
                .OfType<NewCss.CssStyleRule>()
                .First();

            var (secondBox, secondBaseUri) = CreateBox("https://second.example.test/path-b/");
            var secondSources = new List<CssLoader.CssSource>
            {
                CreateSource(css, secondBaseUri, CssLoader.CssOrigin.Inline, 0)
            };
            var secondRule = CssLoader.GetMatchedRules(secondBox, secondSources)
                .Select(match => match.Rule)
                .OfType<NewCss.CssStyleRule>()
                .First();

            Assert.Equal(firstBaseUri, firstRule.BaseUri);
            Assert.Equal(secondBaseUri, secondRule.BaseUri);
        }

        [Fact]
        public void CacheHit_DoesNotNeedToCopyStylesheetIntoLookupKey()
        {
            CssLoader.ClearCaches();
            var (box, baseUri) = CreateBox("https://cache-allocation.test/");
            string css = ".box{color:red}/*" + new string('x', 32_768) + "*/";
            var sources = new List<CssLoader.CssSource>
            {
                CreateSource(css, baseUri, CssLoader.CssOrigin.Inline, 0)
            };

            GC.KeepAlive(CssLoader.GetMatchedRules(box, sources));
            List<CssLoader.MatchedRule> matched = null;
            long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            for (var iteration = 0; iteration < 100; iteration++)
            {
                matched = CssLoader.GetMatchedRules(box, sources);
            }

            long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
            _output.WriteLine($"100 warmed cache hits allocated {allocated:N0} B.");
            Assert.Single(matched!);
            Assert.InRange(allocated, 1, 14_000);
        }

        private static (Element Box, Uri BaseUri) CreateBox(string baseUriText)
        {
            var baseUri = new Uri(baseUriText);
            var document = new HtmlParser(
                "<!doctype html><html><body><div class='box'>x</div></body></html>",
                baseUri).Parse();
            var root = document.DocumentElement ?? document.Children.OfType<Element>().First();
            var box = root.Descendants().OfType<Element>().First(element => element.ClassList.Contains("box"));
            return (box, baseUri);
        }

        private static CssLoader.CssSource CreateSource(
            string css,
            Uri baseUri,
            CssLoader.CssOrigin origin,
            int sourceOrder)
        {
            return new CssLoader.CssSource
            {
                CssText = css,
                BaseUri = baseUri,
                Origin = origin,
                SourceOrder = sourceOrder
            };
        }
    }
}
