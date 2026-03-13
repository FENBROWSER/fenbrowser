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
    }
}
