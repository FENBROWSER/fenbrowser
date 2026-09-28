using System;
using System.Linq;
using System.Threading.Tasks;
using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Rendering;
using Xunit;

namespace FenBrowser.Tests.Core
{
    /// <summary>
    /// CSS Variables 1 §3: `var(--x,)` has an empty fallback, which substitutes nothing;
    /// only a var() without a fallback is guaranteed-invalid. Tailwind v4 composes
    /// `filter` from nine such references, and x.com's `dark:invert` QR code relies on it.
    /// </summary>
    public class CssVarEmptyFallbackTests
    {
        [Fact]
        public async Task EmptyFallback_SubstitutesNothing_MissingFallback_IsInvalid()
        {
            CssLoader.ClearCaches();

            const string html = @"
<!doctype html>
<html>
<head>
  <style>
    * { --tw-blur: initial; --tw-invert: initial; }
    .invert { --tw-invert: invert(100%); filter: var(--tw-blur,) var(--tw-invert,) var(--tw-sepia,); }
    #none { filter: var(--tw-missing); }
  </style>
</head>
<body><img id='t' class='invert' alt=''><div id='none'></div></body>
</html>";

            var doc = new HtmlParser(html).Parse();
            var root = doc.Children.OfType<Element>().First(e => e.TagName == "HTML");
            var computed = await CssLoader.ComputeAsync(root, new Uri("https://test.local"), null);
            var target = doc.Descendants().OfType<Element>().First(e => e.Id == "t");
            var noFallback = doc.Descendants().OfType<Element>().First(e => e.Id == "none");

            Assert.Equal("invert(100%)", computed[target].Filter?.Trim());
            Assert.True(string.IsNullOrWhiteSpace(computed[noFallback].Filter) ||
                        string.Equals(computed[noFallback].Filter, "none", StringComparison.OrdinalIgnoreCase),
                        $"a var() with no fallback is invalid at computed-value time, got '{computed[noFallback].Filter}'");
        }
    }
}
