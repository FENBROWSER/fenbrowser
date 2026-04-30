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

        [Fact]
        public void ApplyProperty_TransformLonghands_ComposeEffectiveTransform()
        {
            var style = new CssComputed();

            CssStyleApplicator.ApplyProperty(style, "translate", "-50% -50%");
            CssStyleApplicator.ApplyProperty(style, "rotate", "10deg");
            CssStyleApplicator.ApplyProperty(style, "scale", "1.2");
            CssStyleApplicator.ApplyProperty(style, "transform", "translateX(12px)");

            Assert.Equal(
                "translate(-50% -50%) rotate(10deg) scale(1.2) translateX(12px)",
                style.Transform);
        }

        [Fact]
        public void ApplyProperty_ObjectFitAndObjectPosition_UpdateTypedValues()
        {
            var style = new CssComputed();

            CssStyleApplicator.ApplyProperty(style, "object-fit", "cover");
            CssStyleApplicator.ApplyProperty(style, "object-position", "50% 30%");

            Assert.Equal("cover", style.ObjectFit);
            Assert.Equal("50% 30%", style.ObjectPosition);
        }
    }
}
