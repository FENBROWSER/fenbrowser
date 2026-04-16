using System;
using System.Reflection;
using FenBrowser.Core.Dom.V2;
using FenBrowser.FenEngine.Rendering;
using Xunit;

namespace FenBrowser.Tests.Engine
{
    public class CustomHtmlEngineFallbackPromotionTests
    {
        private static int InvokePromoteHiddenFallbackContent(Node domRoot, Uri baseUri)
        {
            var method = typeof(CustomHtmlEngine).GetMethod(
                "PromoteHiddenFallbackContent",
                BindingFlags.NonPublic | BindingFlags.Static);

            Assert.NotNull(method);
            var result = method.Invoke(null, new object[] { domRoot, baseUri });
            return Assert.IsType<int>(result);
        }

        private static bool InvokeIsGoogleHost(Uri baseUri)
        {
            var method = typeof(CustomHtmlEngine).GetMethod(
                "IsGoogleHost",
                BindingFlags.NonPublic | BindingFlags.Static);

            Assert.NotNull(method);
            var result = method.Invoke(null, new object[] { baseUri });
            return Assert.IsType<bool>(result);
        }

        private static int InvokeRemoveGoogleAccessTroubleBanners(Node domRoot, Uri baseUri)
        {
            var method = typeof(CustomHtmlEngine).GetMethod(
                "RemoveGoogleAccessTroubleBanners",
                BindingFlags.NonPublic | BindingFlags.Static);

            Assert.NotNull(method);
            var result = method.Invoke(null, new object[] { domRoot, baseUri });
            return Assert.IsType<int>(result);
        }

        private static int InvokeRemoveGoogleTroubleBannerArtifacts(Node domRoot, Uri baseUri)
        {
            var method = typeof(CustomHtmlEngine).GetMethod(
                "RemoveGoogleTroubleBannerArtifacts",
                BindingFlags.NonPublic | BindingFlags.Static);

            Assert.NotNull(method);
            var result = method.Invoke(null, new object[] { domRoot, baseUri });
            return Assert.IsType<int>(result);
        }

        private static Element BuildHiddenFallbackCandidate()
        {
            var root = new Element("div");
            var fallback = new Element("div");
            fallback.SetAttribute("style", "display:none");
            fallback.TextContent = "If you're having trouble accessing Google Search, please click here, or send feedback.";
            fallback.AppendChild(new Element("a"));
            root.AppendChild(fallback);
            return root;
        }

        [Fact]
        public void IsGoogleHost_RecognizesCommonGoogleHostPatterns()
        {
            Assert.True(InvokeIsGoogleHost(new Uri("https://www.google.com/")));
            Assert.True(InvokeIsGoogleHost(new Uri("https://www.google.co.in/")));
            Assert.False(InvokeIsGoogleHost(new Uri("https://www.example.com/")));
        }

        [Fact]
        public void PromoteHiddenFallbackContent_SkipsGoogleWarningFallbackBlocksOnGoogleHosts()
        {
            var root = BuildHiddenFallbackCandidate();
            var promoted = InvokePromoteHiddenFallbackContent(root, new Uri("https://www.google.com/"));
            Assert.Equal(0, promoted);

            var fallback = Assert.IsType<Element>(root.ChildNodes[0]);
            Assert.Contains("display:none", fallback.GetAttribute("style"), StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void PromoteHiddenFallbackContent_PromotesWarningFallbackBlocksOnNonGoogleHosts()
        {
            var root = BuildHiddenFallbackCandidate();
            var promoted = InvokePromoteHiddenFallbackContent(root, new Uri("https://example.test/"));
            Assert.Equal(1, promoted);

            var fallback = Assert.IsType<Element>(root.ChildNodes[0]);
            var style = fallback.GetAttribute("style") ?? string.Empty;
            Assert.DoesNotContain("display:none", style, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void RemoveGoogleAccessTroubleBanners_RemovesTroubleMessageOnGoogleHosts()
        {
            var root = new Element("div");
            var banner = new Element("div");
            banner.TextContent = "If you're having trouble accessing Google Search, please click here, or send feedback.";
            root.AppendChild(banner);

            var removed = InvokeRemoveGoogleAccessTroubleBanners(root, new Uri("https://www.google.com/"));
            Assert.Equal(1, removed);
            Assert.Empty(root.ChildNodes);
        }

        [Fact]
        public void RemoveGoogleAccessTroubleBanners_DoesNotRemoveOnNonGoogleHosts()
        {
            var root = new Element("div");
            var banner = new Element("div");
            banner.TextContent = "If you're having trouble accessing Google Search, please click here, or send feedback.";
            root.AppendChild(banner);

            var removed = InvokeRemoveGoogleAccessTroubleBanners(root, new Uri("https://example.test/"));
            Assert.Equal(0, removed);
            Assert.Single(root.ChildNodes);
        }

        [Fact]
        public void RemoveGoogleTroubleBannerArtifacts_RemovesTroubleDivAndUnhideScriptOnGoogleHosts()
        {
            var root = new Element("div");
            var script = new Element("script");
            script.TextContent = "setTimeout(function(){document.getElementById('yvlrue').setAttribute('style','');navigator.sendBeacon('/gen_204?cad=sg_trbl')},2000);";
            var banner = new Element("div");
            banner.SetAttribute("id", "yvlrue");
            banner.SetAttribute("style", "display:none");
            banner.TextContent = "If you're having trouble accessing Google Search, please click here, or send feedback.";
            root.AppendChild(script);
            root.AppendChild(banner);

            var removed = InvokeRemoveGoogleTroubleBannerArtifacts(root, new Uri("https://www.google.com/search?q=test"));
            Assert.Equal(2, removed);
            Assert.Empty(root.ChildNodes);
        }

        [Fact]
        public void RemoveGoogleTroubleBannerArtifacts_DoesNotRemoveOnNonGoogleHosts()
        {
            var root = new Element("div");
            var script = new Element("script");
            script.TextContent = "setTimeout(function(){document.getElementById('yvlrue').setAttribute('style','')},2000);";
            var banner = new Element("div");
            banner.SetAttribute("id", "yvlrue");
            root.AppendChild(script);
            root.AppendChild(banner);

            var removed = InvokeRemoveGoogleTroubleBannerArtifacts(root, new Uri("https://example.test/search?q=test"));
            Assert.Equal(0, removed);
            Assert.Equal(2, root.ChildNodes.Length);
        }
    }
}
