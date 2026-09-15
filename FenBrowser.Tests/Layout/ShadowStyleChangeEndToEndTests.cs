using System;
using System.Diagnostics;
using System.Threading.Tasks;
using FenBrowser.Core.Dom.V2;
using FenBrowser.FenEngine.Rendering;
using FenBrowser.FenEngine.Scripting;
using Xunit;

namespace FenBrowser.Tests.Layout
{
    /// <summary>
    /// Cloudflare Turnstile creates its iframe at 0x0 inside a closed shadow root and resizes
    /// it from script once its challenge frame answers. Content inserted into a shadow root
    /// and later restyled has to get computed styles through the incremental recascade, the
    /// same as a light-tree element does. The recascade stopped at the shadow root and, when
    /// rooted at the host, cascaded only its light children.
    /// </summary>
    [Collection("Engine Tests")]
    public sealed class ShadowStyleChangeEndToEndTests
    {
        private static readonly TimeSpan SettleTimeout = TimeSpan.FromSeconds(8);

        [Fact]
        public async Task ScriptResizeInsideAClosedShadowRoot_ReachesTheComputedStyle()
        {
            BrowserScriptEngineRuntime.Reset();
            try
            {
                using var browser = new BrowserHost();
                Assert.True(await browser.NavigateAsync(
                    "data:text/html,<body style='margin:0'><div id='light'></div><div id='host'></div></body>"));

                await browser.ExecuteScriptAsync(@"
                    (function () {
                        document.getElementById('light').style.cssText = 'width:0;height:0';
                        var root = document.getElementById('host').attachShadow({ mode: 'closed' });
                        var box = document.createElement('div');
                        box.style.cssText = 'width:0;height:0';
                        root.appendChild(box);
                        window.__box = box;
                        return 1;
                    })();");

                var document = browser.GetDomRoot().OwnerDocument;
                var light = document.GetElementById("light");
                var host = document.GetElementById("host");
                var box = Assert.IsType<Element>(host.GetAttachedShadowRoot()?.FirstChild);

                Assert.True(
                    await WaitForAsync(() => StyleWidth(browser, light) == 0 && StyleWidth(browser, box) == 0),
                    $"inserted: light {Describe(browser, light)}; shadow box {Describe(browser, box)}");

                await browser.ExecuteScriptAsync(
                    "document.getElementById('light').style.cssText = 'width:300px;height:65px';" +
                    "window.__box.style.cssText = 'width:300px;height:65px'; 1;");

                Assert.True(
                    await WaitForAsync(() => StyleWidth(browser, light) == 300 && StyleWidth(browser, box) == 300),
                    $"resized: light {Describe(browser, light)}; shadow box {Describe(browser, box)}");
            }
            finally
            {
                BrowserScriptEngineRuntime.Reset();
            }
        }

        private static double? StyleWidth(BrowserHost browser, Element element)
        {
            var styles = browser.ComputedStyles;
            return styles != null && styles.TryGetValue(element, out var style) ? style?.Width : null;
        }

        private static string Describe(BrowserHost browser, Element element)
        {
            var styles = browser.ComputedStyles;
            if (styles == null)
            {
                return "<no styles>";
            }

            return styles.TryGetValue(element, out var style) && style != null
                ? $"width={style.Width} height={style.Height}"
                : $"<no entry of {styles.Count}>";
        }

        private static async Task<bool> WaitForAsync(Func<bool> condition)
        {
            var watch = Stopwatch.StartNew();
            while (watch.Elapsed < SettleTimeout)
            {
                if (condition())
                {
                    return true;
                }

                await Task.Delay(50);
            }

            return condition();
        }
    }
}
