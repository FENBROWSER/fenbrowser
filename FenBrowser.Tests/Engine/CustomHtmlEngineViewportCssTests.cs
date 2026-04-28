using System;
using System.Linq;
using System.Threading.Tasks;
using FenBrowser.Core.Dom.V2;
using FenBrowser.FenEngine.Rendering;
using Xunit;

namespace FenBrowser.Tests.Engine
{
    public class CustomHtmlEngineViewportCssTests
    {
        [Fact]
        public async Task RenderAsync_UsesViewportAwareCss_OnFirstRender()
        {
            const string html = @"<!DOCTYPE html>
<html>
<head>
  <style>
    body { background: rgb(255, 0, 0); }
    @media (min-width: 1000px) {
      body { background: rgb(0, 128, 0); }
    }
  </style>
</head>
<body><main>viewport css</main></body>
</html>";

            using var engine = new CustomHtmlEngine
            {
                EnableJavaScript = false
            };

            await engine.RenderAsync(
                html,
                new Uri("https://viewport.test/"),
                _ => Task.FromResult(string.Empty),
                _ => Task.FromResult<System.IO.Stream>(null),
                _ => { },
                viewportWidth: 1200,
                viewportHeight: 800);

            var activeRoot = Assert.IsType<Element>(engine.GetActiveDom());
            var body = activeRoot.Descendants().OfType<Element>()
                .First(e => string.Equals(e.TagName, "BODY", StringComparison.OrdinalIgnoreCase));

            Assert.True(engine.LastComputedStyles.TryGetValue(body, out var style));
            Assert.NotNull(style);
            Assert.True(style!.BackgroundColor.HasValue);
            Assert.Equal((byte)0, style.BackgroundColor.Value.Red);
            Assert.Equal((byte)128, style.BackgroundColor.Value.Green);
            Assert.Equal((byte)0, style.BackgroundColor.Value.Blue);
        }
    }
}