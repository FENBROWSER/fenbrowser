using Xunit;
using FenBrowser.FenEngine.Layout;

namespace FenBrowser.Tests.Layout
{
    public class MarginCollapseTests
    {
        [Fact]
        public void Collapse_Positivemargins_ReturnsMax()
        {
            Assert.Equal(20f, MarginCollapseComputer.Collapse(10f, 20f));
            Assert.Equal(30f, MarginCollapseComputer.Collapse(30f, 10f));
            Assert.Equal(10f, MarginCollapseComputer.Collapse(10f, 10f));
        }

        [Fact]
        public void Collapse_NegativeMargins_ReturnsMin()
        {
            Assert.Equal(-20f, MarginCollapseComputer.Collapse(-10f, -20f));
            Assert.Equal(-30f, MarginCollapseComputer.Collapse(-30f, -10f));
            Assert.Equal(-10f, MarginCollapseComputer.Collapse(-10f, -10f));
        }

        [Fact]
        public void Collapse_MixedMargins_ReturnsSum()
        {
            Assert.Equal(10f, MarginCollapseComputer.Collapse(20f, -10f));
            Assert.Equal(-10f, MarginCollapseComputer.Collapse(10f, -20f));
            Assert.Equal(0f, MarginCollapseComputer.Collapse(10f, -10f));
        }

        [Fact]
        public void Collapse_ZeroAndPositive_ReturnsPositive()
        {
            Assert.Equal(10f, MarginCollapseComputer.Collapse(0f, 10f));
            Assert.Equal(10f, MarginCollapseComputer.Collapse(10f, 0f));
        }

        [Fact]
        public void Collapse_ZeroAndNegative_ReturnsNegative()
        {
            Assert.Equal(-10f, MarginCollapseComputer.Collapse(0f, -10f));
            Assert.Equal(-10f, MarginCollapseComputer.Collapse(-10f, 0f));
        }

        [Fact]
        public void Tracker_SimpleSiblings_CollapseCorrectly()
        {
            var tracker = new MarginCollapseTracker();
            
            // First child: MT=10, MB=20
            // Default PreventParentCollapse=false, MT collapses with parent.
            // Spacing should be 0.
            float s1 = tracker.AddMargin(10f, 20f, isFirst: true, isEmpty: false);
            Assert.Equal(0f, s1);
            
            // Check implicit state: StartMargin should be 10.
            tracker.Finish(out float start, out float end);
            Assert.Equal(10f, start);
            
            // Second child: MT=15, MB=30
            // Collapses with previous MB=20.
            // max(20, 15) = 20.
            // Spacing should be 20.
            float s2 = tracker.AddMargin(15f, 30f, isFirst: false, isEmpty: false);
            Assert.Equal(20f, s2);
            
            tracker.Finish(out start, out end);
            Assert.Equal(10f, start);
            Assert.Equal(30f, end); // Last child bottom
        }

        [Fact]
        public void Tracker_ParentPreventCollapse_BehavesCorrectly()
        {
            var tracker = new MarginCollapseTracker { PreventParentCollapse = true };
            
            // First child: MT=10, MB=20
            // Parent has border/padding. Child MT pushes against it.
            // Spacing = 10.
            float s1 = tracker.AddMargin(10f, 20f, isFirst: true, isEmpty: false);
            Assert.Equal(10f, s1);
            
            tracker.Finish(out float start, out float end);
            Assert.Equal(0f, start); // Parent border separates margins
            
            // Second child: MT=15, MB=30
            // Previous MB=20. Collapse(20, 15) = 20.
            float s2 = tracker.AddMargin(15f, 30f, isFirst: false, isEmpty: false);
            Assert.Equal(20f, s2);
        }

        [Fact]
        public void Tracker_EmptyBlock_CollapsesThrough()
        {
            var tracker = new MarginCollapseTracker();
            
            // Empty child: MT=10, MB=10
            // Collapses through. Pending becomes max(10, 10) = 10.
            // Spacing = 0.
            float s1 = tracker.AddMargin(10f, 10f, isFirst: true, isEmpty: true);
            Assert.Equal(0f, s1);
            
            // Pending should be 10.
            tracker.Finish(out float start, out float end);
            Assert.Equal(10f, start);
            Assert.Equal(10f, end);
            Assert.False(tracker.HasContent);

            // Second child (Non-Empty): MT=20, MB=30
            // Collapses with Pending (10).
            // max(10, 20) = 20.
            // This is the first "content". So it collapses with Parent Top (which includes empty block).
            // StartMargin = 20.
            // Spacing = 0.
            float s2 = tracker.AddMargin(20f, 30f, isFirst: false, isEmpty: false);
            Assert.Equal(0f, s2);
            
            tracker.Finish(out start, out end);
            Assert.Equal(20f, start);
            Assert.Equal(30f, end);
            Assert.True(tracker.HasContent);
        }
    }
}
