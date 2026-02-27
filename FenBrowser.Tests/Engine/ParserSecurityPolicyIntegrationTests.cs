using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Rendering;
using Xunit;

namespace FenBrowser.Tests.Engine
{
    public class ParserSecurityPolicyIntegrationTests
    {
        private static readonly object CssPolicyLock = new();

        [Fact]
        public void HtmlParser_AppliesSecurityPolicyLimits()
        {
            var bodyText = new string('x', 800);
            var html = $"<!doctype html><html><body><div>{bodyText}</div></body></html>";

            var policy = new ParserSecurityPolicy
            {
                HtmlMaxTokenEmissions = 48,
                HtmlMaxOpenElementsDepth = 16
            };

            var parser = new HtmlParser(html, securityPolicy: policy);
            var doc = parser.Parse();

            Assert.NotNull(doc.DocumentElement);
            var textLen = doc.DocumentElement.TextContent?.Length ?? 0;
            Assert.True(textLen < bodyText.Length, $"Expected token emission cap to truncate parse stream. textLen={textLen}");
        }

        [Fact]
        public async Task CssLoader_UsesActiveParserSecurityPolicy_ForRuleCaps()
        {
            lock (CssPolicyLock)
            {
                CssLoader.ActiveParserSecurityPolicy = new ParserSecurityPolicy
                {
                    CssMaxRules = 1,
                    CssMaxDeclarationsPerBlock = 64
                };
            }

            try
            {
                var html = @"
<!doctype html>
<html>
<head>
  <style>
    .x { background-color: red; }
    #t { background-color: rgb(1,2,3); }
  </style>
</head>
<body><div id='t'>A</div></body>
</html>";

                var parser = new HtmlParser(html);
                var doc = parser.Parse();
                var root = doc.Children.OfType<Element>().First(e => e.TagName == "HTML");
                var computed = await CssLoader.ComputeAsync(root, new Uri("https://test.local"), null);
                var target = doc.Descendants().OfType<Element>().First(e => e.Id == "t");
                var style = computed[target];

                Assert.False(style.Map.TryGetValue("background-color", out var value) &&
                             value.Contains("rgb(1,2,3)", StringComparison.OrdinalIgnoreCase));
            }
            finally
            {
                lock (CssPolicyLock)
                {
                    CssLoader.ActiveParserSecurityPolicy = ParserSecurityPolicy.Default;
                }
            }
        }

        [Fact]
        public async Task CssLoader_UsesActiveParserSecurityPolicy_ForDeclarationCaps()
        {
            lock (CssPolicyLock)
            {
                CssLoader.ActiveParserSecurityPolicy = new ParserSecurityPolicy
                {
                    CssMaxRules = 32,
                    CssMaxDeclarationsPerBlock = 1
                };
            }

            try
            {
                var html = @"
<!doctype html>
<html>
<head>
  <style>
    #t { color: red; background-color: blue; }
  </style>
</head>
<body><div id='t'>A</div></body>
</html>";

                var parser = new HtmlParser(html);
                var doc = parser.Parse();
                var root = doc.Children.OfType<Element>().First(e => e.TagName == "HTML");
                var computed = await CssLoader.ComputeAsync(root, new Uri("https://test.local"), null);
                var target = doc.Descendants().OfType<Element>().First(e => e.Id == "t");
                var style = computed[target];

                Assert.True(style.Map.TryGetValue("color", out var color) && color.Equals("red", StringComparison.OrdinalIgnoreCase));
                Assert.False(style.Map.ContainsKey("background-color"));
            }
            finally
            {
                lock (CssPolicyLock)
                {
                    CssLoader.ActiveParserSecurityPolicy = ParserSecurityPolicy.Default;
                }
            }
        }
    }
}
