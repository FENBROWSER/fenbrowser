using System;
using System.Reflection;
using FenBrowser.Core.Css;
using FenBrowser.Core.Dom.V2;
using FenBrowser.FenEngine.Rendering;
using Xunit;

namespace FenBrowser.Tests.Rendering
{
    public class CssScrollDrivenAnimationsRuntimeTests
    {
        [Fact]
        public void ComputeScrollDrivenProgress_NamedTimeline_UsesRegisteredScrollerStateAndAnimationRange()
        {
            var engine = new CssAnimationEngine();
            var scroller = new Element("div");
            var target = new Element("div");

            var timelineStyle = new CssComputed();
            timelineStyle.Map["scroll-timeline-name"] = "--hero";
            timelineStyle.Map["scroll-timeline-axis"] = "y";
            engine.RegisterScrollTimeline(scroller, timelineStyle);

            var targetStyle = new CssComputed();
            targetStyle.Map["animation-range-start"] = "10%";
            targetStyle.Map["animation-range-end"] = "90%";
            target.SetComputedStyle(targetStyle);

            CssAnimationEngine.ScrollStateResolver = element =>
                ReferenceEquals(element, scroller) ? (200d, 800d) : (0d, 1d);

            var progress = InvokeComputeScrollDrivenProgress(engine, target, targetStyle, "--hero", 0d, 1000d);
            Assert.Equal(0.1875d, progress, 6);

            CssAnimationEngine.ScrollStateResolver = null;
            engine.Stop();
        }

        [Fact]
        public void ComputeScrollDrivenProgress_ScrollFunctionNearest_UsesOverflowAncestor()
        {
            var engine = new CssAnimationEngine();
            var scroller = new Element("div");
            var target = new Element("div");
            scroller.AppendChild(target);

            var scrollerStyle = new CssComputed
            {
                OverflowY = "auto"
            };
            scroller.SetComputedStyle(scrollerStyle);

            var targetStyle = new CssComputed();
            target.SetComputedStyle(targetStyle);

            CssAnimationEngine.ScrollStateResolver = element =>
                ReferenceEquals(element, scroller) ? (300d, 600d) : (0d, 1d);

            var progress = InvokeComputeScrollDrivenProgress(engine, target, targetStyle, "scroll(y nearest)", 0d, 1000d);
            Assert.Equal(0.5d, progress, 6);

            CssAnimationEngine.ScrollStateResolver = null;
            engine.Stop();
        }

        [Fact]
        public void ParseRangeOffset_HandlesPercentAndNamedOffsets()
        {
            Assert.Equal(0d, InvokeParseRangeOffset("normal", true), 6);
            Assert.Equal(1d, InvokeParseRangeOffset("normal", false), 6);
            Assert.Equal(0.25d, InvokeParseRangeOffset("25%", true), 6);
            Assert.Equal(0d, InvokeParseRangeOffset("entry", false), 6);
            Assert.Equal(1d, InvokeParseRangeOffset("exit", true), 6);
            Assert.Equal(0d, InvokeParseRangeOffset("invalid-token", true), 6);
            Assert.Equal(1d, InvokeParseRangeOffset("invalid-token", false), 6);
        }

        private static double InvokeComputeScrollDrivenProgress(
            CssAnimationEngine engine,
            Element element,
            CssComputed style,
            string timeline,
            double scrollOffset,
            double scrollMax)
        {
            var method = typeof(CssAnimationEngine).GetMethod(
                "ComputeScrollDrivenProgress",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(method);

            var value = method.Invoke(engine, new object[] { element, style, timeline, scrollOffset, scrollMax });
            Assert.NotNull(value);
            return (double)value;
        }

        private static double InvokeParseRangeOffset(string raw, bool isStart)
        {
            var method = typeof(CssAnimationEngine).GetMethod(
                "ParseRangeOffset",
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.NotNull(method);

            var value = method.Invoke(null, new object[] { raw, isStart });
            Assert.NotNull(value);
            return (double)value;
        }
    }
}
