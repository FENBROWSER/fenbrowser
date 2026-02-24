using System.Collections.Generic;
using FenBrowser.Core.Engine;
using Xunit;

namespace FenBrowser.Tests.Core
{
    public class NavigationSubresourceTrackerTests
    {
        [Fact]
        public void Tracker_TracksPendingLoads_PerNavigationId()
        {
            var tracker = new NavigationSubresourceTracker();
            const long navA = 101;
            const long navB = 202;

            tracker.ResetNavigation(navA);
            tracker.ResetNavigation(navB);

            tracker.MarkLoadStarted(navA);
            tracker.MarkLoadStarted(navA);
            tracker.MarkLoadStarted(navB);

            Assert.Equal(2, tracker.GetPendingCount(navA));
            Assert.Equal(1, tracker.GetPendingCount(navB));

            tracker.MarkLoadCompleted(navA);
            tracker.MarkLoadCompleted(navB);

            Assert.Equal(1, tracker.GetPendingCount(navA));
            Assert.Equal(0, tracker.GetPendingCount(navB));
        }

        [Fact]
        public void Tracker_AbandonNavigation_ClearsPendingWithoutCrossImpact()
        {
            var tracker = new NavigationSubresourceTracker();
            const long navA = 1;
            const long navB = 2;

            tracker.ResetNavigation(navA);
            tracker.ResetNavigation(navB);
            tracker.MarkLoadStarted(navA);
            tracker.MarkLoadStarted(navB);

            tracker.AbandonNavigation(navA);

            Assert.Equal(0, tracker.GetPendingCount(navA));
            Assert.Equal(1, tracker.GetPendingCount(navB));
        }

        [Fact]
        public void Tracker_EmitsPendingCountEvents_WithNavigationId()
        {
            var tracker = new NavigationSubresourceTracker();
            var observed = new List<(long navId, int pending)>();
            tracker.PendingCountChanged += (navId, pending) => observed.Add((navId, pending));

            const long navId = 42;
            tracker.ResetNavigation(navId);
            tracker.MarkLoadStarted(navId);
            tracker.MarkLoadCompleted(navId);

            Assert.Contains((navId, 0), observed);
            Assert.Contains((navId, 1), observed);
        }
    }
}

