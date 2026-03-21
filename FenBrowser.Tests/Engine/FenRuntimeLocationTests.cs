using System;
using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Engine;
using FenBrowser.FenEngine.Core;
using FenBrowser.FenEngine.Core.EventLoop;
using Xunit;

namespace FenBrowser.Tests.Engine
{
    [Collection("Engine Tests")]
    public class FenRuntimeLocationTests
    {
        public FenRuntimeLocationTests()
        {
            EngineContext.Reset();
            EventLoopCoordinator.ResetInstance();
        }

        [Fact]
        public void SetDom_SynchronizesLocationFromBaseUri()
        {
            var runtime = new FenRuntime();
            runtime.SetDom(Document.CreateHtmlDocument(), new Uri("https://www.google.com/search?q=test#top"));

            runtime.ExecuteSimple(@"
                var __href = location.href;
                var __origin = location.origin;
                var __pathname = location.pathname;
                var __search = location.search;
                var __hash = location.hash;
            ");

            Assert.Equal("https://www.google.com/search?q=test#top", runtime.GetGlobal("__href").ToString());
            Assert.Equal("https://www.google.com", runtime.GetGlobal("__origin").ToString());
            Assert.Equal("/search", runtime.GetGlobal("__pathname").ToString());
            Assert.Equal("?q=test", runtime.GetGlobal("__search").ToString());
            Assert.Equal("#top", runtime.GetGlobal("__hash").ToString());
        }

        [Fact]
        public void LocationReplace_RequestsHostNavigationForRelativeUrl()
        {
            var runtime = new FenRuntime();
            runtime.SetDom(Document.CreateHtmlDocument(), new Uri("https://www.google.com/search?q=test"));

            Uri requested = null;
            runtime.NavigationRequested = uri => requested = uri;

            runtime.ExecuteSimple("location.replace('/httpservice/retry/enablejs?sei=abc');");
            runtime.ExecuteSimple("var __redirectHref = location.href;");

            Assert.NotNull(requested);
            Assert.Equal("https://www.google.com/httpservice/retry/enablejs?sei=abc", requested.AbsoluteUri);
            Assert.Equal("https://www.google.com/httpservice/retry/enablejs?sei=abc", runtime.GetGlobal("__redirectHref").ToString());
        }

        [Fact]
        public void HistoryPushState_WithoutBridge_ClonesStateAndUpdatesLocation()
        {
            var runtime = new FenRuntime();
            runtime.SetDom(Document.CreateHtmlDocument(), new Uri("https://example.test/start"));

            runtime.ExecuteSimple(@"
                var state = { nested: { value: 5 } };
                history.pushState(state, '', '/app?tab=1#pane');
                state.nested.value = 9;

                window.__historyLength = history.length;
                window.__historyStateValue = history.state.nested.value;
                window.__historyHref = location.href;
            ");

            var window = runtime.GetGlobal("window").AsObject();
            Assert.NotNull(window);
            Assert.Equal(2, window.Get("__historyLength").ToNumber());
            Assert.Equal(5, window.Get("__historyStateValue").ToNumber());
            Assert.Equal("https://example.test/app?tab=1#pane", window.Get("__historyHref").ToString());
        }

        [Fact]
        public void HistoryBack_WithoutBridge_QueuesPopStateAndRestoresEntryState()
        {
            var runtime = new FenRuntime();
            runtime.SetDom(Document.CreateHtmlDocument(), new Uri("https://example.test/start"));

            runtime.ExecuteSimple(@"
                window.__popCount = 0;
                window.__eventStep = -1;
                window.addEventListener('popstate', function(e) {
                    window.__popCount++;
                    window.__eventStep = e.state.step;
                    window.__eventHref = location.href;
                });

                history.pushState({ step: 1 }, '', '/one');
                history.pushState({ step: 2 }, '', '/two');
                history.back();

                window.__beforeDrainPopCount = window.__popCount;
                window.__beforeDrainHref = location.href;
                window.__beforeDrainState = history.state.step;
            ");

            var window = runtime.GetGlobal("window").AsObject();
            Assert.NotNull(window);
            Assert.Equal(0, window.Get("__beforeDrainPopCount").ToNumber());
            Assert.Equal("https://example.test/one", window.Get("__beforeDrainHref").ToString());
            Assert.Equal(1, window.Get("__beforeDrainState").ToNumber());

            EventLoopCoordinator.Instance.RunUntilEmpty();

            Assert.Equal(1, window.Get("__popCount").ToNumber());
            Assert.Equal(1, window.Get("__eventStep").ToNumber());
            Assert.Equal("https://example.test/one", window.Get("__eventHref").ToString());
        }
    }
}
