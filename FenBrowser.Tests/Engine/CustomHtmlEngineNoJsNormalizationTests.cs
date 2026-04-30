using System;
using System.Linq;
using System.Threading.Tasks;
using FenBrowser.Core.Dom.V2;
using FenBrowser.FenEngine.Rendering;
using Xunit;

namespace FenBrowser.Tests.Engine
{
    public class CustomHtmlEngineNoJsNormalizationTests
    {
        [Fact]
        public async Task RenderAsync_WithJavaScriptEnabled_RemovesNoJsClassTokens()
        {
            const string html = @"<!DOCTYPE html>
<html class='no-js'>
<head><title>no-js normalization</title></head>
<body>
  <nav id='globalnav' class='globalnav no-js'></nav>
  <footer class='no-js'></footer>
</body>
</html>";

            using var engine = new CustomHtmlEngine
            {
                EnableJavaScript = true
            };

            await engine.RenderAsync(
                html,
                new Uri("https://normalize.test/"),
                _ => Task.FromResult(string.Empty),
                _ => Task.FromResult<System.IO.Stream>(null),
                _ => { },
                viewportWidth: 1200,
                viewportHeight: 800,
                forceJavascript: true);

            var root = Assert.IsType<Element>(engine.GetActiveDom());
            Assert.False(HasClassToken(root.ClassName, "no-js"));
            Assert.True(HasClassToken(root.ClassName, "js"));

            var nav = root.Descendants().OfType<Element>()
                .First(e => string.Equals(e.TagName, "NAV", StringComparison.OrdinalIgnoreCase));
            Assert.False(HasClassToken(nav.ClassName, "no-js"));

            var footer = root.Descendants().OfType<Element>()
                .First(e => string.Equals(e.TagName, "FOOTER", StringComparison.OrdinalIgnoreCase));
            Assert.False(HasClassToken(footer.ClassName, "no-js"));
        }

        private static bool HasClassToken(string classValue, string token)
        {
            if (string.IsNullOrWhiteSpace(classValue) || string.IsNullOrWhiteSpace(token))
            {
                return false;
            }

            var parts = classValue.Split(new[] { ' ', '\t', '\r', '\n', '\f' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < parts.Length; i++)
            {
                if (string.Equals(parts[i], token, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
