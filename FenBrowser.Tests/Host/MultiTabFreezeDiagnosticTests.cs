using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using FenBrowser.Host;
using FenBrowser.Host.Tabs;
using Xunit;
using Xunit.Abstractions;

namespace FenBrowser.Tests.Host
{
    /// <summary>
    /// Diagnostic tests for multi-tab freeze.
    /// Creates BrowserIntegration instances (one per "tab") and navigates them,
    /// logging timing and thread info to identify where the freeze occurs.
    /// </summary>
    public class MultiTabFreezeDiagnosticTests
    {
        private readonly ITestOutputHelper _output;

        public MultiTabFreezeDiagnosticTests(ITestOutputHelper output)
        {
            _output = output;
        }

        /// <summary>
        /// Simulates: tab 1 = google.com (default), tab 2 = github.com.
        /// Creates both tabs, updates viewport, starts navigation, and monitors for hangs.
        /// </summary>
        [Fact]
        public async Task TwoTabNavigation_Diagnostic()
        {
            var log = new ConcurrentQueue<(long ms, string msg)>();
            var sw = Stopwatch.StartNew();
            void Log(string msg) { log.Enqueue((sw.ElapsedMilliseconds, msg)); _output.WriteLine($"[{sw.ElapsedMilliseconds,6}ms] {msg}"); }

            // === Tab 1: google.com ===
            Log("Creating Tab 1 (google.com default)...");
            BrowserTab tab1 = null;
            try
            {
                tab1 = new BrowserTab();
                Log($"Tab 1 created (Id={tab1.Id})");

                tab1.Browser.TitleChanged += t => Log($"Tab1 TitleChanged: {t}");
                tab1.Browser.UrlChanged += u => Log($"Tab1 UrlChanged: {u}");
                tab1.Browser.LoadingChanged += l => Log($"Tab1 LoadingChanged: {l}");
                tab1.Browser.NeedsRepaint += () => Log($"Tab1 NeedsRepaint");
                tab1.Browser.ConsoleMessage += m => Log($"Tab1 Console: {m?.Substring(0, Math.Min(m?.Length ?? 0, 120))}");

                Log("Tab1: Updating viewport...");
                tab1.Browser.UpdateViewport(new SkiaSharp.SKSize(1280, 720));
                Log($"Tab1: HasViewport={tab1.Browser.HasViewport}");

                Log("Tab1: Starting navigation to https://www.google.com...");
                var tab1NavSw = Stopwatch.StartNew();
                var tab1NavTask = tab1.NavigateAsync("https://www.google.com");
                Log($"Tab1: NavigateAsync returned task (elapsed={tab1NavSw.ElapsedMilliseconds}ms)");

                // Don't await tab1 yet — let it load in background while we create tab2

                // === Tab 2: github.com ===
                Log("Creating Tab 2 (github.com)...");
                BrowserTab tab2 = null;
                try
                {
                    tab2 = new BrowserTab();
                    Log($"Tab 2 created (Id={tab2.Id})");

                    tab2.Browser.TitleChanged += t => Log($"Tab2 TitleChanged: {t}");
                    tab2.Browser.UrlChanged += u => Log($"Tab2 UrlChanged: {u}");
                    tab2.Browser.LoadingChanged += l => Log($"Tab2 LoadingChanged: {l}");
                    tab2.Browser.NeedsRepaint += () => Log($"Tab2 NeedsRepaint");
                    tab2.Browser.ConsoleMessage += m => Log($"Tab2 Console: {m?.Substring(0, Math.Min(m?.Length ?? 0, 120))}");

                    Log("Tab2: Updating viewport...");
                    tab2.Browser.UpdateViewport(new SkiaSharp.SKSize(1280, 720));
                    Log($"Tab2: HasViewport={tab2.Browser.HasViewport}");

                    Log("Tab2: Starting navigation to https://github.com...");
                    var tab2NavSw = Stopwatch.StartNew();
                    var tab2NavTask = tab2.NavigateAsync("https://github.com");
                    Log($"Tab2: NavigateAsync returned task (elapsed={tab2NavSw.ElapsedMilliseconds}ms)");

                    // Wait for tab1 to settle (30s timeout)
                    Log("Waiting for Tab1 navigation to complete (30s timeout)...");
                    var tab1Timeout = Task.Delay(30_000);
                    var tab1Completed = await Task.WhenAny(tab1NavTask, tab1Timeout);
                    if (tab1Completed == tab1Timeout)
                    {
                        Log("WARNING: Tab1 navigation timed out after 30s!");
                    }
                    else
                    {
                        Log($"Tab1: Navigation completed in {tab1NavSw.ElapsedMilliseconds}ms. Final URL={tab1.Url}");
                    }

                    // Wait for tab2 to settle (30s timeout)
                    Log("Waiting for Tab2 navigation to complete (30s timeout)...");
                    var tab2Timeout = Task.Delay(30_000);
                    var tab2Completed = await Task.WhenAny(tab2NavTask, tab2Timeout);
                    if (tab2Completed == tab2Timeout)
                    {
                        Log("WARNING: Tab2 navigation timed out after 30s!");
                    }
                    else
                    {
                        Log($"Tab2: Navigation completed in {tab2NavSw.ElapsedMilliseconds}ms. Final URL={tab2.Url}");
                    }
                }
                finally
                {
                    if (tab2 != null)
                    {
                        Log("Shutting down Tab2 engine loop...");
                        ShutdownEngineLoop(tab2.Browser);
                        Log("Tab2 shutdown complete.");
                    }
                }
            }
            finally
            {
                if (tab1 != null)
                {
                    Log("Shutting down Tab1 engine loop...");
                    ShutdownEngineLoop(tab1.Browser);
                    Log("Tab1 shutdown complete.");
                }
            }

            // Print full timeline
            Log("=== FULL TIMELINE ===");
            foreach (var (ms, msg) in log)
            {
                _output.WriteLine($"[{ms,6}ms] {msg}");
            }

            Assert.NotNull(tab1);
        }

        /// <summary>
        /// Test that specifically checks if creating a second BrowserHost while the first
        /// is still loading causes a hang or deadlock.
        /// </summary>
        [Fact]
        public async Task ConcurrentBrowserHost_Creation_NoDeadlock()
        {
            var sw = Stopwatch.StartNew();
            _output.WriteLine($"[{sw.ElapsedMilliseconds}ms] Creating BrowserHost 1...");

            var host1 = new FenBrowser.FenEngine.Rendering.BrowserHost();
            _output.WriteLine($"[{sw.ElapsedMilliseconds}ms] BrowserHost 1 created.");

            _output.WriteLine($"[{sw.ElapsedMilliseconds}ms] Creating BrowserHost 2...");
            var host2 = new FenBrowser.FenEngine.Rendering.BrowserHost();
            _output.WriteLine($"[{sw.ElapsedMilliseconds}ms] BrowserHost 2 created.");

            // Start navigating both concurrently
            _output.WriteLine($"[{sw.ElapsedMilliseconds}ms] Starting navigation on Host1 (google.com)...");
            var nav1 = host1.NavigateAsync("https://www.google.com");

            _output.WriteLine($"[{sw.ElapsedMilliseconds}ms] Starting navigation on Host2 (github.com)...");
            var nav2 = host2.NavigateAsync("https://github.com");

            // Wait up to 60s for both
            var timeout = Task.Delay(60_000);
            var allDone = Task.WhenAll(nav1, nav2);

            var completed = await Task.WhenAny(allDone, timeout);
            var elapsed = sw.ElapsedMilliseconds;

            if (completed == timeout)
            {
                _output.WriteLine($"[{elapsed}ms] TIMEOUT: At least one navigation did not complete in 60s.");
                _output.WriteLine($"  Host1 status: {nav1.Status}");
                _output.WriteLine($"  Host2 status: {nav2.Status}");
            }
            else
            {
                _output.WriteLine($"[{elapsed}ms] Both navigations completed.");
                _output.WriteLine($"  Host1 URL: {host1.CurrentUri}");
                _output.WriteLine($"  Host2 URL: {host2.CurrentUri}");
            }

            host1.Dispose();
            host2.Dispose();
        }

        private static void ShutdownEngineLoop(BrowserIntegration integration)
        {
            var runningField = typeof(BrowserIntegration).GetField("_running", BindingFlags.Instance | BindingFlags.NonPublic);
            var wakeEventField = typeof(BrowserIntegration).GetField("_wakeEvent", BindingFlags.Instance | BindingFlags.NonPublic);
            var engineThreadField = typeof(BrowserIntegration).GetField("_engineThread", BindingFlags.Instance | BindingFlags.NonPublic);
            var currentFrameField = typeof(BrowserIntegration).GetField("_currentFrame", BindingFlags.Instance | BindingFlags.NonPublic);
            var currentSeedField = typeof(BrowserIntegration).GetField("_currentFrameSeedImage", BindingFlags.Instance | BindingFlags.NonPublic);

            runningField?.SetValue(integration, false);
            (wakeEventField?.GetValue(integration) as AutoResetEvent)?.Set();
            (engineThreadField?.GetValue(integration) as Thread)?.Join(TimeSpan.FromSeconds(5));
            (currentFrameField?.GetValue(integration) as SkiaSharp.SKPicture)?.Dispose();
            (currentSeedField?.GetValue(integration) as SkiaSharp.SKImage)?.Dispose();
        }
    }
}
