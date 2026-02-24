using System.Collections.Generic;
using FenBrowser.Core.Engine;
using Xunit;

namespace FenBrowser.Tests.Core
{
    public class NavigationLifecycleTrackerTests
    {
        [Fact]
        public void Tracker_EmitsDeterministicSuccessfulFlow()
        {
            var tracker = new NavigationLifecycleTracker();
            var phases = new List<NavigationLifecyclePhase>();
            tracker.Transitioned += t => phases.Add(t.Phase);

            var navId = tracker.BeginNavigation("https://example.com", isUserInput: true);
            Assert.True(tracker.MarkFetching(navId, "https://example.com"));
            Assert.True(tracker.MarkResponseReceived(navId, "Success", "https://example.com/home"));
            Assert.True(tracker.MarkCommitting(navId, "https://example.com/home"));
            Assert.True(tracker.MarkInteractive(navId));
            Assert.True(tracker.MarkComplete(navId, "done"));

            Assert.Equal(
                new[]
                {
                    NavigationLifecyclePhase.Requested,
                    NavigationLifecyclePhase.Fetching,
                    NavigationLifecyclePhase.ResponseReceived,
                    NavigationLifecyclePhase.Committing,
                    NavigationLifecyclePhase.Interactive,
                    NavigationLifecyclePhase.Complete
                },
                phases);

            var snapshot = tracker.GetSnapshot();
            Assert.Equal(navId, snapshot.NavigationId);
            Assert.Equal(NavigationLifecyclePhase.Complete, snapshot.Phase);
            Assert.Equal("https://example.com/home", snapshot.EffectiveUrl);
            Assert.Equal("Success", snapshot.ResponseStatus);
            Assert.Equal("done", snapshot.Detail);
            Assert.True(snapshot.IsUserInput);
        }

        [Fact]
        public void Tracker_RejectsInvalidTransitionOrder()
        {
            var tracker = new NavigationLifecycleTracker();
            var navId = tracker.BeginNavigation("https://example.com", isUserInput: false);

            Assert.False(tracker.MarkComplete(navId));
            Assert.True(tracker.MarkFetching(navId));
            Assert.False(tracker.MarkInteractive(navId));
            Assert.True(tracker.MarkResponseReceived(navId, "Success"));
            Assert.True(tracker.MarkCommitting(navId));
            Assert.True(tracker.MarkInteractive(navId));
            Assert.True(tracker.MarkComplete(navId));
        }

        [Fact]
        public void Tracker_IgnoresStaleNavigationIdAfterNewBegin()
        {
            var tracker = new NavigationLifecycleTracker();
            var firstId = tracker.BeginNavigation("https://a.example", isUserInput: true);
            Assert.True(tracker.MarkFetching(firstId));

            var secondId = tracker.BeginNavigation("https://b.example", isUserInput: false);
            Assert.True(secondId > firstId);

            Assert.False(tracker.MarkResponseReceived(firstId, "Success"));
            Assert.True(tracker.MarkFetching(secondId));
            Assert.True(tracker.MarkFailed(secondId, "network"));

            var snapshot = tracker.GetSnapshot();
            Assert.Equal(secondId, snapshot.NavigationId);
            Assert.Equal(NavigationLifecyclePhase.Failed, snapshot.Phase);
            Assert.Equal("network", snapshot.Detail);
        }

        [Fact]
        public void Tracker_AllowsCancellationFromInFlightPhases()
        {
            var tracker = new NavigationLifecycleTracker();
            var navId = tracker.BeginNavigation("https://cancel.example", isUserInput: false);
            Assert.True(tracker.MarkFetching(navId));
            Assert.True(tracker.MarkCancelled(navId, "superseded"));

            var snapshot = tracker.GetSnapshot();
            Assert.Equal(NavigationLifecyclePhase.Cancelled, snapshot.Phase);
            Assert.Equal("superseded", snapshot.Detail);
        }

        [Fact]
        public void Tracker_CapturesRedirectAndCommitMetadata()
        {
            var tracker = new NavigationLifecycleTracker();
            var navId = tracker.BeginNavigation("http://example.com", isUserInput: true);

            Assert.True(tracker.MarkFetching(navId, "http://example.com"));
            Assert.True(tracker.MarkResponseReceived(
                navId,
                "Success",
                "https://www.example.com/home",
                isRedirect: true,
                redirectCount: 2,
                detail: "status=Success;redirects=2"));
            Assert.True(tracker.MarkCommitting(navId, "https://www.example.com/home", commitSource: "network-document"));
            Assert.True(tracker.MarkInteractive(navId, "interactive=dom-rendered"));
            Assert.True(tracker.MarkComplete(navId, "document-complete;subresources=settled"));

            var snapshot = tracker.GetSnapshot();
            Assert.Equal(NavigationLifecyclePhase.Complete, snapshot.Phase);
            Assert.True(snapshot.IsRedirect);
            Assert.Equal(2, snapshot.RedirectCount);
            Assert.Equal("network-document", snapshot.CommitSource);
            Assert.Equal("https://www.example.com/home", snapshot.EffectiveUrl);
        }
    }
}
