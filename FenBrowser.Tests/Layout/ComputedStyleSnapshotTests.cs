using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using FenBrowser.Core.Css;
using FenBrowser.Core.Dom.V2;
using FenBrowser.FenEngine.Rendering;
using FenBrowser.FenEngine.Scripting;
using Xunit;

namespace FenBrowser.Tests.Layout
{
    /// <summary>
    /// The renderer takes the published computed-style dictionary without a lock and enumerates
    /// it for the whole frame. The incremental recascade wrote its results into that same
    /// instance from another thread, and on bing.com a frame threw "Collection was modified;
    /// enumeration operation may not execute". A recascade has to publish a new dictionary and
    /// leave the one a frame already holds untouched.
    /// </summary>
    [Collection("Engine Tests")]
    public sealed class ComputedStyleSnapshotTests
    {
        private static readonly TimeSpan SettleTimeout = TimeSpan.FromSeconds(8);

        [Fact]
        public async Task IncrementalRecascade_PublishesANewDictionary_AndLeavesTheHeldSnapshotUnchanged()
        {
            BrowserScriptEngineRuntime.Reset();
            try
            {
                using var browser = new BrowserHost();
                Assert.True(await browser.NavigateAsync(
                    "data:text/html,<body style='margin:0'><div id='box' style='width:10px;height:10px'></div></body>"));

                var box = browser.GetDomRoot().OwnerDocument.GetElementById("box");
                Assert.True(await WaitForAsync(() => WidthIn(browser.ComputedStyles, box) == 10), "initial style never arrived");

                // What a frame holds while it renders.
                Dictionary<Node, CssComputed> heldByFrame = browser.ComputedStyles;
                var heldCount = heldByFrame.Count;

                await browser.ExecuteScriptAsync("document.getElementById('box').style.width = '300px'; 1;");

                Assert.True(await WaitForAsync(() => WidthIn(browser.ComputedStyles, box) == 300), "restyle never published");

                Assert.NotSame(heldByFrame, browser.ComputedStyles);
                Assert.Equal(10d, WidthIn(heldByFrame, box));
                Assert.Equal(heldCount, heldByFrame.Count);
            }
            finally
            {
                BrowserScriptEngineRuntime.Reset();
            }
        }

        private static double? WidthIn(IReadOnlyDictionary<Node, CssComputed> styles, Element element)
            => styles != null && styles.TryGetValue(element, out var style) ? style?.Width : null;

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
