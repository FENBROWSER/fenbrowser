using FenBrowser.Core.Css;
using FenBrowser.FenEngine.Rendering.Css;
using Xunit;

namespace FenBrowser.Tests.Rendering
{
    public class CssStyleApplicatorTests
    {
        [Fact]
        public void ApplyProperty_TopAuto_ClearsTypedTopOffsets()
        {
            var style = new CssComputed
            {
                Top = 12,
                TopPercent = 25
            };

            CssStyleApplicator.ApplyProperty(style, "top", "auto");

            Assert.Null(style.Top);
            Assert.Null(style.TopPercent);
            Assert.True(style.Map.TryGetValue("top", out var value));
            Assert.Equal("auto", value);
        }
    }
}
