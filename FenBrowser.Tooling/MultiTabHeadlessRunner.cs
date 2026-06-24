using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using FenBrowser.Host.Tabs;

namespace FenBrowser.Tooling
{
    /// <summary>
    /// Headless multi-tab runner that simulates the real browser's tab
    /// creation + navigation pattern to diagnose second-tab freeze.
    /// </summary>
    public static class MultiTabHeadlessRunner
    {
        public static async Task<int> RunAsync(string[] args)
        {
            var log = new ConcurrentQueue<(long ms, string msg)>();
            var sw = Stopwatch.StartNew();
            void L(string msg) { var t = sw.ElapsedMilliseconds; log.Enqueue((t, msg)); Console.WriteLine($"[{t,7}ms] {msg}"); }

            L("=== Multi-Tab Headless Diagnostic ===");

            // Simulate: Tab 1 = google.com (default), Tab 2 = github.com
            var tab1Url = args.Length > 0 ? args[0] : "https://www.google.com";
            var tab2Url = args.Length > 1 ? args[1] : "https://github.com";

            L($"Tab1 URL: {tab1Url}");
            L($"Tab2 URL: {tab2Url}");

            // --- Phase 1: Create Tab 1 (like browser startup) ---
            L("Phase 1: Creating Tab 1...");
            BrowserTab tab1 = new BrowserTab();
            L($"  Tab1 created (Id={tab1.Id})");
            tab1.Browser.UpdateViewport(new SkiaSharp.SKSize(1280, 720));
            L("  Tab1 viewport set (1280x720)");

            // Navigate tab1 (fire-and-forget, like the real browser)
            L($"  Tab1: Starting navigation to {tab1Url}...");
            var tab1NavTask = tab1.NavigateAsync(tab1Url);
            L($"  Tab1: NavigateAsync returned (fire-and-forget)");

            // Let tab1 start loading
            await Task.Delay(2000);
            L($"  Tab1 after 2s: Url={TruncateUrl(tab1.Url)}, IsLoading={tab1.IsLoading}");

            // --- Phase 2: Create Tab 2 (like Ctrl+T + type URL) ---
            L("Phase 2: Creating Tab 2...");
            var tab2CreateSw = Stopwatch.StartNew();
            BrowserTab tab2 = new BrowserTab();
            L($"  Tab2 created in {tab2CreateSw.ElapsedMilliseconds}ms (Id={tab2.Id})");
            tab2.Browser.UpdateViewport(new SkiaSharp.SKSize(1280, 720));
            L("  Tab2 viewport set (1280x720)");

            L($"  Tab2: Starting navigation to {tab2Url}...");
            var tab2NavSw = Stopwatch.StartNew();
            var tab2NavTask = tab2.NavigateAsync(tab2Url);
            L($"  Tab2: NavigateAsync returned in {tab2NavSw.ElapsedMilliseconds}ms (fire-and-forget)");

            // --- Phase 3: Monitor both tabs ---
            L("Phase 3: Monitoring both tabs...");
            var monitorCts = new CancellationTokenSource();
            var monitorTask = Task.Run(async () =>
            {
                while (!monitorCts.Token.IsCancellationRequested)
                {
                    await Task.Delay(500, monitorCts.Token);
                    L($"  STATUS: Tab1 Url={TruncateUrl(tab1.Url)} Loading={tab1.IsLoading} | Tab2 Url={TruncateUrl(tab2.Url)} Loading={tab2.IsLoading}");
                }
            });

            // Wait for both navigations (120s max each — github.com is heavy)
            var t1Timeout = Task.Delay(120_000);
            var t2Timeout = Task.Delay(120_000);
            var t1Done = await Task.WhenAny(tab1NavTask, t1Timeout);
            var t2Done = await Task.WhenAny(tab2NavTask, t2Timeout);

            monitorCts.Cancel();
            try { await monitorTask; } catch (OperationCanceledException) { }

            if (t1Done == t1Timeout)
                L("WARNING: Tab1 navigation TIMED OUT after 60s!");
            else
                L($"Tab1: Navigation COMPLETE. Final URL={tab1.Url}");

            if (t2Done == t2Timeout)
                L("WARNING: Tab2 navigation TIMED OUT after 60s!");
            else
                L($"Tab2: Navigation COMPLETE. Final URL={tab2.Url}");

            // --- Phase 4: Wait for DOM settle ---
            L("Phase 4: Waiting for DOM settle (15s)...");
            await Task.Delay(15_000);
            L($"  Tab1 final: Url={tab1.Url}, Loading={tab1.IsLoading}");
            L($"  Tab2 final: Url={tab2.Url}, Loading={tab2.IsLoading}");

            // --- Phase 5: Shutdown ---
            L("Phase 5: Shutting down...");
            ShutdownTab(tab1);
            ShutdownTab(tab2);
            L("Both tabs shut down.");

            // --- Summary ---
            L("");
            L("=== FULL TIMELINE ===");
            foreach (var (ms, msg) in log)
            {
                Console.WriteLine($"[{ms,7}ms] {msg}");
            }

            return 0;
        }

        private static string TruncateUrl(string url, int maxLen = 50)
        {
            if (string.IsNullOrEmpty(url)) return "(empty)";
            return url.Length > maxLen ? url.Substring(0, maxLen) + "..." : url;
        }

        private static void ShutdownTab(BrowserTab tab)
        {
            if (tab == null) return;
            try
            {
                var runningField = typeof(FenBrowser.Host.BrowserIntegration)
                    .GetField("_running", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                var wakeEventField = typeof(FenBrowser.Host.BrowserIntegration)
                    .GetField("_wakeEvent", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                var engineThreadField = typeof(FenBrowser.Host.BrowserIntegration)
                    .GetField("_engineThread", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                var currentFrameField = typeof(FenBrowser.Host.BrowserIntegration)
                    .GetField("_currentFrame", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                var currentSeedField = typeof(FenBrowser.Host.BrowserIntegration)
                    .GetField("_currentFrameSeedImage", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

                runningField?.SetValue(tab.Browser, false);
                (wakeEventField?.GetValue(tab.Browser) as AutoResetEvent)?.Set();
                (engineThreadField?.GetValue(tab.Browser) as Thread)?.Join(TimeSpan.FromSeconds(5));
                (currentFrameField?.GetValue(tab.Browser) as SkiaSharp.SKPicture)?.Dispose();
                (currentSeedField?.GetValue(tab.Browser) as SkiaSharp.SKImage)?.Dispose();
            }
            catch { /* best-effort cleanup */ }
        }
    }
}
