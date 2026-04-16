using System;
using System.Reflection;
using FenBrowser.Core.Dom.V2;
using FenBrowser.FenEngine.Rendering;
using Xunit;

namespace FenBrowser.Tests.Engine
{
    public class CustomHtmlEngineGoogleSafeModeBypassTests
    {
        private static bool InvokeShouldPreferFallbackDom(string html, Uri baseUri)
        {
            var method = typeof(CustomHtmlEngine).GetMethod(
                "ShouldPreferFallbackDom",
                BindingFlags.NonPublic | BindingFlags.Static);

            Assert.NotNull(method);
            var result = method.Invoke(null, new object[] { html, baseUri });
            return Assert.IsType<bool>(result);
        }

        private static bool InvokeIsJsHeavyAppShell(Node domRoot, Uri baseUri)
        {
            var method = typeof(CustomHtmlEngine).GetMethod(
                "IsJsHeavyAppShell",
                BindingFlags.NonPublic | BindingFlags.Static);

            Assert.NotNull(method);
            var result = method.Invoke(null, new object[] { domRoot, baseUri });
            return Assert.IsType<bool>(result);
        }

        private static Node BuildHeavyAppShellDom()
        {
            var root = new Element("html");
            var body = new Element("body");
            root.AppendChild(body);

            var noscript = new Element("noscript");
            var meta = new Element("meta");
            meta.SetAttribute("http-equiv", "refresh");
            noscript.AppendChild(meta);
            body.AppendChild(noscript);

            var script = new Element("script");
            script.TextContent = new string('x', 25000);
            body.AppendChild(script);

            return root;
        }

        [Fact]
        public void ShouldPreferFallbackDom_DisabledForGoogleHosts()
        {
            var html = "<html><body><noscript><meta http-equiv=\"refresh\" content=\"0;url=/\"></noscript><script>" +
                       new string('x', 25000) +
                       "</script></body></html>";

            Assert.False(InvokeShouldPreferFallbackDom(html, new Uri("https://www.google.com/")));
            Assert.True(InvokeShouldPreferFallbackDom(html, new Uri("https://example.test/")));
        }

        [Fact]
        public void IsJsHeavyAppShell_DisabledForGoogleHosts()
        {
            var dom = BuildHeavyAppShellDom();

            Assert.False(InvokeIsJsHeavyAppShell(dom, new Uri("https://www.google.com/")));
            Assert.True(InvokeIsJsHeavyAppShell(dom, new Uri("https://example.test/")));
        }
    }
}
