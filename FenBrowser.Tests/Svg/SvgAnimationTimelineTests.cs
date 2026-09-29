using System;
using FenBrowser.Core.Dom.V2;
using FenBrowser.FenEngine.Rendering;
using Xunit;

namespace FenBrowser.Tests.Svg
{
    /// <summary>
    /// The SMIL document timeline kept beside the DOM: DOM instance times become
    /// clock values in the begin and end lists painting renders, bounded in count
    /// and never negative, and only animated subtrees depend on time.
    /// </summary>
    public sealed class SvgAnimationTimelineTests
    {
        private const string Markup =
            "<svg xmlns='http://www.w3.org/2000/svg'>" +
            "<rect id='r'><animate id='a' attributeName='x' to='5' dur='1s' begin='indefinite'/></rect>" +
            "<svg id='inner'><set id='s' href='#r' attributeName='y' to='1'/></svg>" +
            "</svg>";

        [Fact]
        public void InstanceTimes_AreBoundedAndNeverNegative()
        {
            var document = XmlDomParser.Parse(Markup, "image/svg+xml");
            var animate = document.GetElementById("a");
            for (int i = 0; i < SvgAnimationTimeline.MaxInstanceTimes + 10; i++)
            {
                SvgAnimationTimeline.AddInstanceTime(animate, i, isEnd: false);
            }
            SvgAnimationTimeline.AddInstanceTime(animate, -4, isEnd: true);

            string begin = SvgAnimationTimeline.ComposeTimingAttribute(animate, isEnd: false);
            Assert.Equal(SvgAnimationTimeline.MaxInstanceTimes + 1, begin.Split(';').Length);
            Assert.StartsWith("indefinite;10s;", begin, StringComparison.Ordinal);
            Assert.Equal("0s", SvgAnimationTimeline.ComposeTimingAttribute(animate, isEnd: true));
        }

        [Fact]
        public void HasAnimations_FindsAnimationElementsOnly()
        {
            var animated = XmlDomParser.Parse(Markup, "image/svg+xml").DocumentElement;
            var still = XmlDomParser.Parse(
                "<svg xmlns='http://www.w3.org/2000/svg'><rect/></svg>", "image/svg+xml").DocumentElement;

            Assert.True(SvgAnimationTimeline.HasAnimations(animated));
            Assert.False(SvgAnimationTimeline.HasAnimations(still));
            Assert.Same(animated, SvgAnimationTimeline.TimeContainerOf(animated.OwnerDocument.GetElementById("s")));
            Assert.Equal(0d, SvgAnimationTimeline.CurrentTime(null));
        }

        [Fact]
        public void PauseSeekAndUnpause_KeepTheTime()
        {
            var svg = XmlDomParser.Parse(Markup, "image/svg+xml").DocumentElement;

            SvgAnimationTimeline.Pause(svg);
            SvgAnimationTimeline.Seek(svg, 7.25);
            Assert.Equal(7.25, SvgAnimationTimeline.CurrentTime(svg));
            SvgAnimationTimeline.Seek(svg, double.NaN);
            Assert.Equal(0d, SvgAnimationTimeline.CurrentTime(svg));
            SvgAnimationTimeline.Seek(svg, 3);
            SvgAnimationTimeline.Unpause(svg);
            Assert.InRange(SvgAnimationTimeline.CurrentTime(svg), 3d, 60d);
        }
    }
}
