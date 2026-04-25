using FenBrowser.Core.Css;
using FenBrowser.Core.Dom.V2;
using FenBrowser.FenEngine.Rendering;
using Xunit;

namespace FenBrowser.Tests.Rendering
{
    public class CssTransitionEngineTests
    {
        [Fact]
        public void UpdateTransitions_ClearsPercentInsetWhenAnimatingPixelInset()
        {
            var engine = new CssTransitionEngine();
            var element = new Element("div");

            var baseline = new CssComputed
            {
                TransitionProperty = "top",
                TransitionDuration = "120ms",
                Top = 0d,
                TopPercent = 50d
            };
            engine.UpdateTransitions(element, baseline, 0);

            var next = new CssComputed
            {
                TransitionProperty = "top",
                TransitionDuration = "120ms",
                Top = 20d,
                TopPercent = 50d
            };
            engine.UpdateTransitions(element, next, 1);

            Assert.True(next.Top.HasValue);
            Assert.Null(next.TopPercent);
        }
    }
}
