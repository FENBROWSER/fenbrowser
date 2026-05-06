using System;
using FenBrowser.Core;
using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Engine;
using FenBrowser.FenEngine.Core;
using FenBrowser.FenEngine.Core.EventLoop;
using Xunit;

namespace FenBrowser.Tests.Engine
{
    [Collection("Engine Tests")]
    public class HostApiHardeningTests
    {
        public HostApiHardeningTests()
        {
            EngineContext.Reset();
            EventLoopCoordinator.ResetInstance();
        }

        [Fact]
        public void RequestIdleCallback_InvalidCallback_ThrowsTypeError()
        {
            var runtime = new FenRuntime();

            runtime.ExecuteSimple(@"
                var idleCallbackError = '';
                try {
                    requestIdleCallback(42);
                } catch (e) {
                    idleCallbackError = String(e && e.message ? e.message : e);
                }
            ");

            var errorText = runtime.GetGlobal("idleCallbackError").ToString();
            Assert.Contains("callback must be a function", errorText, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void WindowOpen_NoOpenerFeature_ReturnsNullAndNavigates()
        {
            var runtime = new FenRuntime();
            runtime.SetDom(Document.CreateHtmlDocument(), new Uri("https://example.test/start"));
            var previousPopupSetting = BrowserSettings.Instance.BlockPopups;
            BrowserSettings.Instance.BlockPopups = false;

            Uri requested = null;
            runtime.NavigationRequested = uri => requested = uri;

            try
            {
                runtime.ExecuteSimple(@"
                    var openReturnedNull = window.open('/next', '_blank', 'noopener=1') === null;
                    var openHref = location.href;
                ");

                Assert.True(runtime.GetGlobal("openReturnedNull").ToBoolean());
                Assert.NotNull(requested);
                Assert.Equal("https://example.test/next", requested.AbsoluteUri);
                Assert.Equal("https://example.test/next", runtime.GetGlobal("openHref").ToString());
            }
            finally
            {
                BrowserSettings.Instance.BlockPopups = previousPopupSetting;
            }
        }

        [Fact]
        public void WindowOpen_BlocksUnsafeJavascriptScheme()
        {
            var runtime = new FenRuntime();
            runtime.SetDom(Document.CreateHtmlDocument(), new Uri("https://example.test/start"));
            var previousPopupSetting = BrowserSettings.Instance.BlockPopups;
            BrowserSettings.Instance.BlockPopups = false;

            Uri requested = null;
            runtime.NavigationRequested = uri => requested = uri;

            try
            {
                runtime.ExecuteSimple(@"
                    var openBlocked = window.open('javascript:alert(1)') === null;
                    var currentHref = location.href;
                ");

                Assert.True(runtime.GetGlobal("openBlocked").ToBoolean());
                Assert.Null(requested);
                Assert.Equal("https://example.test/start", runtime.GetGlobal("currentHref").ToString());
            }
            finally
            {
                BrowserSettings.Instance.BlockPopups = previousPopupSetting;
            }
        }
    }
}
