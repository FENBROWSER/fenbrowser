using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Globalization;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using FenBrowser.Core;
using FenBrowser.Core.Css;
using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Engine;
using FenBrowser.Core.Logging;
using FenBrowser.Core.Performance;
using FenBrowser.FenEngine.Rendering;
using FenBrowser.FenEngine.Rendering.Core;
using FenBrowser.FenEngine.Rendering.Paint;
using FenBrowser.FenEngine.Scripting;
using FenBrowser.Tooling.Host;
using FenBrowser.Host;
using FenBrowser.Host.Tabs;
using FenBrowser.Host.WebDriver;
using FenBrowser.FenEngine.Rendering.Performance;
using FenBrowser.Js.Performance;
using SkiaSharp;

namespace FenBrowser.Tooling
{
    internal static class Program
    {
        internal const int DebugSiteViewportWidth = 1280;
        internal const int DebugSiteViewportHeight = 800;
        internal const int WebDriverMainStackBytes = 16 * 1024 * 1024;
        internal const string WebDriverLargeStackEnvironmentVariable =
            "FEN_TOOLING_WEBDRIVER_LARGE_STACK";

        public static async Task Main(string[] args)
        {
            if (args.Length == 0)
            {
                PrintUsage();
                return;
            }

            if (RequiresWebDriverLargeStack(
                    args,
                    Environment.GetEnvironmentVariable(WebDriverLargeStackEnvironmentVariable)))
            {
                RunWebDriverMainOnLargeStack(args);
                return;
            }

            LogManager.InitializeFromSettings();

            var command = args[0].Trim().ToLowerInvariant();
            switch (command)
            {
                case "verify":
                    await RunVerifyAsync(args).ConfigureAwait(false);
                    return;
                case "diagnose":
                    await RunDiagnoseAsync(args).ConfigureAwait(false);
                    return;
                case "debug-site":
                    await RunDebugSiteAsync(args).ConfigureAwait(false);
                    return;
                case "debug-site-interact":
                    await RunDebugSiteInteractionAsync(args).ConfigureAwait(false);
                    return;
                case "jstime":
                    RunJsTime(args);
                    return;
                case "acid2":
                case "acid-ref":
                case "acid-cmp":
                    Console.WriteLine("[tooling] Acid test runners removed with legacy engine.");
                    return;
                case "acid2-compare":
                    await RunAcid2CompareAsync().ConfigureAwait(false);
                    return;
                case "acid2-layout-html":
                    await RunAcid2LayoutHtmlAsync(args).ConfigureAwait(false);
                    return;
                case "webdriver":
                    await RunWebDriverAsync(args).ConfigureAwait(false);
                    return;
                case "render-perf":
                    await RunRenderPerfAsync().ConfigureAwait(false);
                    return;
                case "js-perf":
                    await RunFenJsPerfAsync(args).ConfigureAwait(false);
                    return;
                case "dom-perf":
                    await RunDomPerfAsync().ConfigureAwait(false);
                    return;
                case "capability-ledger":
                    RunCapabilityLedger(args);
                    return;
                case "debug-css":
                    RunCssDebug();
                    return;
                case "test":
                    // await FenBrowser.FenEngine.Tests.LogicTestRunner.MainTest(args).ConfigureAwait(false);
                    Console.WriteLine("[tooling] Legacy test command removed with legacy engine.");
                    return;
                case "test262":
                    // await Test262ToolRunner.RunAsync(args).ConfigureAwait(false);
                    Console.WriteLine("[tooling] Use FenBrowser.Js.Test262 runner directly.");
                    return;
                case "wpt":
                    await WptToolRunner.RunAsync(args).ConfigureAwait(false);
                    return;
                case "webidl-inventory":
                    WebIdlInventoryRunner.Run(args);
                    return;
                case "multitab":
                    // await MultiTabHeadlessRunner.RunAsync(args.Skip(1).ToArray()).ConfigureAwait(false);
                    Console.WriteLine("[tooling] Multitab removed with legacy engine.");
                    return;
                default:
                    PrintUsage();
                    return;
            }
        }

        internal static bool RequiresWebDriverLargeStack(string[] args, string? marker)
            => args.Length > 0 &&
               string.Equals(args[0], "webdriver", StringComparison.OrdinalIgnoreCase) &&
               !string.Equals(marker, "1", StringComparison.Ordinal);

        private static void RunWebDriverMainOnLargeStack(string[] args)
        {
            Environment.SetEnvironmentVariable(WebDriverLargeStackEnvironmentVariable, "1");
            Exception? failure = null;
            var worker = new Thread(
                () =>
                {
                    try
                    {
                        Main(args).GetAwaiter().GetResult();
                    }
                    catch (Exception ex)
                    {
                        failure = ex;
                    }
                },
                WebDriverMainStackBytes)
            {
                IsBackground = false,
                Name = "FenBrowser-WebDriver-Main"
            };

            worker.Start();
            worker.Join();
            if (failure != null)
            {
                ExceptionDispatchInfo.Capture(failure).Throw();
            }
        }

        // Isolates the FenJS front-end so we can tell whether a heavy bundle hangs
        // in parse vs compile vs execute. Runs on a 16 MB stack thread because the
        // browser does too (deep minified nesting overflows the default 1 MB stack).
        private static void RunJsTime(string[] args)
        {
            if (args.Length < 2)
            {
                Console.WriteLine("usage: jstime <js_file> [phase=parse|compile|all]");
                return;
            }

            var path = args[1];
            var phase = args.Length >= 3 ? args[2].Trim().ToLowerInvariant() : "all";
            var text = File.ReadAllText(path);
            Console.WriteLine($"[jstime] file={path} bytes={text.Length} phase={phase}");

            Exception? failure = null;
            var worker = new System.Threading.Thread(() =>
            {
                try
                {
                    var source = new FenBrowser.Js.Source.SourceText(text, path);

                    var swParse = System.Diagnostics.Stopwatch.StartNew();
                    var program = FenBrowser.Js.Parser.JsParser.ParseScript(source);
                    swParse.Stop();
                    Console.WriteLine($"[jstime] PARSE ok in {swParse.ElapsedMilliseconds} ms (statements={program.Body.Count})");

                    if (phase == "parse")
                    {
                        return;
                    }

                    var swCompile = System.Diagnostics.Stopwatch.StartNew();
                    var compiler = new FenBrowser.Js.Bytecode.BytecodeCompiler();
                    var fn = compiler.CompileProgram(program);
                    swCompile.Stop();
                    Console.WriteLine($"[jstime] COMPILE ok in {swCompile.ElapsedMilliseconds} ms (instructions={fn.Instructions.Count}, maxDepth={compiler.MaxObservedCompileDepth})");

                    if (phase == "exec" || phase == "run")
                    {
                        var interp = new FenBrowser.Js.Interpreter.BytecodeInterpreter();
                        var swExec = System.Diagnostics.Stopwatch.StartNew();
                        var result = interp.Execute(fn);
                        swExec.Stop();
                        Console.WriteLine($"[jstime] EXEC ok in {swExec.ElapsedMilliseconds} ms (result={result.Tag})");
                    }
                }
                catch (FenBrowser.Js.Interpreter.JsThrownException jte)
                {
                    Console.WriteLine($"[jstime] THREW: {jte.Message}");
                    failure = null;
                }
                catch (Exception ex)
                {
                    failure = ex;
                }
            }, 256 * 1024 * 1024);

            worker.IsBackground = true;
            worker.Start();
            if (!worker.Join(TimeSpan.FromSeconds(120)))
            {
                Console.WriteLine("[jstime] TIMED OUT after 120s (front-end did not finish) — capturing stack…");
                Console.WriteLine("[jstime] HANG CONFIRMED in front-end (parse/compile).");
                Environment.Exit(2);
            }

            if (failure is not null)
            {
                Console.WriteLine($"[jstime] FAILED: {failure.GetType().Name}: {failure.Message}");
                Console.WriteLine(failure.StackTrace);
                Environment.Exit(1);
            }

            Console.WriteLine("[jstime] DONE");
        }

        private static async Task RunVerifyAsync(string[] args)
        {
            if (args.Length < 2)
            {
                throw new ArgumentException("verify requires <html_path>.");
            }

            await VerificationRunner.GenerateSnapshot(args[1], "verification_output.png").ConfigureAwait(false);
        }


        private static async Task RunDiagnoseAsync(string[] args)
        {
            if (args.Length < 2)
            {
                throw new ArgumentException("diagnose requires <url> [settle_ms]");
            }

            var url = args[1];
            var settleMs = args.Length > 2 && int.TryParse(args[2], out var parsed) ? parsed : 20000;

            CssEngineConfig.CurrentEngine = CssEngineType.Custom;

            var consoleMessages = new List<string>();
            var navFailures = new List<string>();

            using var host = new FenBrowser.FenEngine.Rendering.BrowserHost();
            host.ConsoleMessage += msg => { lock (consoleMessages) consoleMessages.Add(msg); };
            host.NavigationFailed += (_, msg) => { lock (navFailures) navFailures.Add(msg); };

            Console.WriteLine($"[diagnose] Navigating to {url} (settle {settleMs}ms)...");
            var sw = System.Diagnostics.Stopwatch.StartNew();

            bool navOk;
            try
            {
                navOk = await host.NavigateAsync(url).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[diagnose] NavigateAsync threw: {ex.GetType().Name}: {ex.Message}");
                Console.WriteLine(ex.StackTrace);
                return;
            }

            // Settle: let subresources, scripts, and async work run. Poll DOM size until stable.
            int lastCount = -1, stableTicks = 0;
            var deadline = DateTime.UtcNow.AddMilliseconds(settleMs);
            while (DateTime.UtcNow < deadline)
            {
                await Task.Delay(250).ConfigureAwait(false);
                int count = CountDomNodes(host.GetDomRoot());
                if (count == lastCount) { if (++stableTicks >= 8) break; }
                else { stableTicks = 0; lastCount = count; }
            }
            sw.Stop();

            // Probe the live page global state (runs on the same script interpreter).
            string[] probes =
            {
                "typeof window",
                "typeof document",
                "typeof AwsWafIntegration",
                "typeof window.__SCRIPTS_LOADED__",
                "Object.keys(window.__SCRIPTS_LOADED__||{}).join(',')",
                "typeof window.__INITIAL_STATE__",
                "typeof window.webpackChunk_twitter_responsive_web",
                "typeof document !== 'undefined' ? document.readyState : 'no-document'",
                "typeof document !== 'undefined' && document.querySelectorAll ? document.querySelectorAll('*').length : 'no-document'",
                "navigator.userAgent",
                "window.location.href",
                "document.querySelector('meta[name=\"viewport\"]') && document.querySelector('meta[name=\"viewport\"]').getAttribute('content') || 'no-viewport-meta'",
            };
            Console.WriteLine();
            Console.WriteLine("---- page global probes ----");
            foreach (var p in probes)
            {
                string val;
                try
                {
                    var r = await host.ExecuteScriptAsync(p).ConfigureAwait(false);
                    val = r?.ToString() ?? "null";
                }
                catch (Exception ex) { val = "<throw> " + ex.GetType().Name + ": " + ex.Message + (ex.InnerException != null ? " | inner: " + ex.InnerException.GetType().Name + ": " + ex.InnerException.Message : ""); }
                if (val.Length > 300) val = val.Substring(0, 300) + "…";
                Console.WriteLine($"   {p}  =>  {val}");
            }

            var root = host.GetDomRoot();
            int nodeCount = CountDomNodes(root);
            string rawHtml = SafeCall(() => host.GetRawHtml()) ?? string.Empty;
            string text = SafeCall(() => host.GetTextContent()) ?? string.Empty;
            var styles = SafeCall(() => host.ComputedStyles);

            Console.WriteLine();
            Console.WriteLine("================ DIAGNOSE REPORT ================");
            Console.WriteLine($"NavigateAsync returned : {navOk}");
            Console.WriteLine($"Final URL              : {host.CurrentUri}");
            Console.WriteLine($"Elapsed                : {sw.ElapsedMilliseconds} ms");
            Console.WriteLine($"DOM nodes              : {nodeCount}");
            Console.WriteLine($"Raw HTML length        : {rawHtml.Length}");
            Console.WriteLine($"Rendered text length   : {text.Length}");
            Console.WriteLine($"Computed styles count  : {styles?.Count ?? 0}");
            Console.WriteLine($"Nav failures           : {navFailures.Count}");
            foreach (var f in navFailures.Take(20)) Console.WriteLine($"   ! {f}");
            Console.WriteLine($"Console messages       : {consoleMessages.Count}");
            foreach (var m in consoleMessages.Take(60))
            {
                var line = m.Replace("\n", " ").Replace("\r", " ");
                if (line.Length > 300) line = line.Substring(0, 300) + "…";
                Console.WriteLine($"   > {line}");
            }
            Console.WriteLine();
            Console.WriteLine("---- rendered text (first 800 chars) ----");
            Console.WriteLine(text.Length > 800 ? text.Substring(0, 800) : text);
            Console.WriteLine("=================================================");
        }

        private static async Task RunDebugSiteAsync(string[] args)
        {
            if (args.Length < 2)
            {
                throw new ArgumentException("debug-site requires <url> [settle_ms]");
            }

            var url = args[1];
            var settleMs = args.Length > 2 && int.TryParse(args[2], out var parsed) ? parsed : 20000;
            var report = await CollectDebugSiteDiagnosticsAsync(url, settleMs).ConfigureAwait(false);
            report.LoggerDrainTimeoutMs = 2000;
            report.LoggerDrainSucceeded = EngineLog.Flush(TimeSpan.FromMilliseconds(report.LoggerDrainTimeoutMs));
            report.MissingApiSnapshot = MissingApiTracker.GetSnapshot(siteUrl: null);
            var bundleDir = WriteDebugSiteBundle(report);

            PrintDebugSiteReport(report);
            Console.WriteLine();
            Console.WriteLine($"[debug-site] Bundle: {bundleDir}");
        }

        internal static FenBrowser.FenEngine.Rendering.BrowserHost CreateDebugSiteBrowserHost()
        {
            var host = new FenBrowser.FenEngine.Rendering.BrowserHost();
            host.UpdateViewportHint(DebugSiteViewportWidth, DebugSiteViewportHeight);
            return host;
        }

        private static async Task RunDebugSiteInteractionAsync(string[] args)
        {
            if (args.Length < 5)
            {
                throw new ArgumentException(
                    "debug-site-interact requires <url> <target_selector> <text> <submit_selector> [settle_ms] [interaction_settle_ms]");
            }

            var url = args[1];
            var settleMs = args.Length > 5 && int.TryParse(args[5], out var parsedSettle) ? parsedSettle : 20000;
            var interactionSettleMs = args.Length > 6 && int.TryParse(args[6], out var parsedInteractionSettle)
                ? parsedInteractionSettle
                : 5000;
            var request = new DebugSiteInteractionRequest(args[2], args[3], args[4], interactionSettleMs);
            var report = await CollectDebugSiteDiagnosticsAsync(url, settleMs, request).ConfigureAwait(false);
            report.LoggerDrainTimeoutMs = 2000;
            report.LoggerDrainSucceeded = EngineLog.Flush(TimeSpan.FromMilliseconds(report.LoggerDrainTimeoutMs));
            report.MissingApiSnapshot = MissingApiTracker.GetSnapshot(siteUrl: null);
            var bundleDir = WriteDebugSiteBundle(report);

            PrintDebugSiteReport(report);
            Console.WriteLine();
            Console.WriteLine($"[debug-site-interact] Bundle: {bundleDir}");
        }

        private static async Task<DebugSiteReport> CollectDebugSiteDiagnosticsAsync(
            string url,
            int settleMs,
            DebugSiteInteractionRequest interactionRequest = null)
        {
            CssEngineConfig.CurrentEngine = CssEngineType.Custom;
            EngineCapabilities.Reset();
            MissingApiTracker.ResetForRun();
            ConfigureDebugSiteFileLogging();

            var consoleMessages = new List<string>();
            var navFailures = new List<string>();
            var lifecycleTransitions = new List<DebugSiteLifecycleTransition>();
            var networkCapture = new DebugSiteNetworkCapture();

            using var host = CreateDebugSiteBrowserHost();
            host.ConsoleMessage += msg => { lock (consoleMessages) consoleMessages.Add(msg); };
            host.NavigationFailed += (_, msg) => { lock (navFailures) navFailures.Add(msg); };
            host.NavigationLifecycleChanged += (_, transition) =>
            {
                lock (lifecycleTransitions)
                {
                    lifecycleTransitions.Add(DebugSiteLifecycleTransition.From(transition));
                }
            };
            networkCapture.Attach(host.ResourceManager);

            Console.WriteLine($"[debug-site] Navigating to {url} (settle {settleMs}ms)...");
            var sw = System.Diagnostics.Stopwatch.StartNew();

            bool navOk;
            string navigateException = null;
            try
            {
                navOk = await host.NavigateAsync(url).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                navOk = false;
                navigateException = ex.ToString();
            }

            int lastCount = -1, stableTicks = 0;
            var deadline = DateTime.UtcNow.AddMilliseconds(settleMs);
            while (DateTime.UtcNow < deadline)
            {
                await Task.Delay(250).ConfigureAwait(false);
                int count = CountDomNodes(host.GetDomRoot());
                if (count == lastCount) { if (++stableTicks >= 8) break; }
                else { stableTicks = 0; lastCount = count; }
            }

            // ── Timer / redirect drain ──────────────────────────────────
            // Pages like Google CAPTCHA schedule setTimeout callbacks that
            // may trigger a redirect or build the visible UI after the
            // initial DOM-stability loop exits.  Navigation resets replace
            // the JS engine, making PendingHostTimers unreliable across
            // redirects.  Instead we watch for DOM-size changes (a redirect
            // replaces the document) and keep waiting until the deadline or
            // the size is truly stable.
            var drainDeadline = DateTime.UtcNow.AddMilliseconds(settleMs);
            int drainStable = 0;
            while (DateTime.UtcNow < drainDeadline)
            {
                await Task.Delay(500).ConfigureAwait(false);
                int domNow = CountDomNodes(host.GetDomRoot());
                if (domNow == lastCount)
                {
                    if (++drainStable >= 6) break; // 3 s of stability
                }
                else
                {
                    drainStable = 0;
                    lastCount = domNow;
                }
            }

            // DOM stability does not imply render stability: a newly attached iframe can
            // already be present while its own stylesheet cascade is still completing.
            // Flush the browser's normal pending render work before capturing evidence.
            await host.FlushPendingLayoutAsync().ConfigureAwait(false);

            DebugSiteInteractionResult interaction = null;
            if (interactionRequest != null)
            {
                var beforeRoot = host.GetDomRoot();
                var beforeStyles = SafeCall(() => host.ComputedStyles);
                var beforeScreenshot = CaptureDebugSiteScreenshot(
                    beforeRoot,
                    beforeStyles,
                    host.CurrentUri?.AbsoluteUri ?? url);
                if (beforeScreenshot.Captured)
                {
                    TryCopyFile(
                        beforeScreenshot.Path,
                        DiagnosticPaths.GetRootArtifactPath("debug_interaction_before.png"));
                }
                else
                {
                    TryDeleteFile(DiagnosticPaths.GetRootArtifactPath("debug_interaction_before.png"));
                }

                interaction = await DebugSiteInteractionRunner.RunAsync(
                        host,
                        interactionRequest,
                        () => networkCapture.Snapshot().Count)
                    .ConfigureAwait(false);
                interaction = interaction with
                {
                    BeforeScreenshotCaptured = beforeScreenshot.Captured,
                    BeforeScreenshotError = beforeScreenshot.Error ?? string.Empty
                };
            }
            sw.Stop();

            // ── Capture screenshot early ─────────────────────────────────
            // Capture the root, styles, and screenshot now — before the
            // probe scripts run.  Probe ExecuteScriptAsync calls can trigger
            // navigation side-effects that desynchronise the DOM and the
            // cached ComputedStyles dictionary.
            var root = host.GetDomRoot();
            int nodeCount = CountDomNodes(root);
            string rawHtml = SafeCall(() => host.GetRawHtml()) ?? string.Empty;
            string text = SafeCall(() => host.GetTextContent()) ?? string.Empty;
            var styles = SafeCall(() => host.ComputedStyles);
            var screenshot = CaptureDebugSiteScreenshot(root, styles, host.CurrentUri?.AbsoluteUri ?? url);
            if (interaction != null)
            {
                interaction = interaction with
                {
                    AfterScreenshotCaptured = screenshot.Captured,
                    AfterScreenshotError = screenshot.Error ?? string.Empty
                };
            }

            string[] probes =
            {
                "typeof window",
                "typeof document",
                "typeof AwsWafIntegration",
                "typeof window.__SCRIPTS_LOADED__",
                "Object.keys(window.__SCRIPTS_LOADED__||{}).join(',')",
                "typeof window.__INITIAL_STATE__",
                "typeof window.webpackChunk_twitter_responsive_web",
                "typeof document !== 'undefined' ? document.readyState : 'no-document'",
                "typeof document !== 'undefined' && document.querySelectorAll ? document.querySelectorAll('*').length : 'no-document'",
                "navigator.userAgent",
                "window.location.href",
                "document.querySelector('meta[name=\"viewport\"]') && document.querySelector('meta[name=\"viewport\"]').getAttribute('content') || 'no-viewport-meta'",
            };

            var probeResults = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var probe in probes)
            {
                string value;
                try
                {
                    var result = await host.ExecuteScriptAsync(probe).ConfigureAwait(false);
                    value = result?.ToString() ?? "null";
                }
                catch (Exception ex)
                {
                    value = "<throw> " + ex.GetType().Name + ": " + ex.Message +
                            (ex.InnerException != null ? " | inner: " + ex.InnerException.GetType().Name + ": " + ex.InnerException.Message : "");
                }

                if (value.Length > 300)
                {
                    value = value.Substring(0, 300) + "...";
                }

                probeResults[probe] = value;
            }

            var styleLayout = BuildStyleLayoutSummary(
                root,
                styles,
                screenshot.RenderContext,
                screenshot.Telemetry,
                screenshot.Captured,
                screenshot.Error);
            var scriptLoading = host.Engine?.ScriptEngine?.GetScriptLoadingSnapshot() ?? new BrowserScriptLoadingSnapshot();
            var eventLoop = host.Engine?.ScriptEngine?.GetEventLoopSnapshot() ?? new BrowserEventLoopSnapshot();
            var missingApis = ExtractMissingApiRecords(consoleMessages, EngineCapabilities.GetUnsupportedJsSnapshot());
            List<DebugSiteLifecycleTransition> lifecycleTimeline;
            lock (lifecycleTransitions)
            {
                lifecycleTimeline = lifecycleTransitions
                    .Select(static transition => transition.Clone())
                    .ToList();
            }
            var lifecycle = BuildLifecycleSummary(host.NavigationLifecycleState, probeResults, lifecycleTimeline, eventLoop);

            return new DebugSiteReport
            {
                Url = url,
                SettleMs = settleMs,
                NavigateReturned = navOk,
                NavigateException = navigateException,
                FinalUrl = host.CurrentUri?.AbsoluteUri ?? string.Empty,
                ElapsedMs = sw.ElapsedMilliseconds,
                DomNodeCount = nodeCount,
                RawHtmlLength = rawHtml.Length,
                RenderedTextLength = text.Length,
                ComputedStylesCount = styles?.Count ?? 0,
                NavigationFailures = navFailures.ToList(),
                ConsoleMessages = consoleMessages.ToList(),
                Lifecycle = lifecycle,
                ScriptLoading = scriptLoading,
                EventLoop = eventLoop,
                StyleLayout = styleLayout,
                MissingApis = missingApis,
                StyleDump = BuildStyleDump(root, styles),
                LayoutDump = BuildLayoutDump(root, screenshot.RenderContext),
                PaintDump = BuildPaintDump(screenshot.RenderContext),
                DisplayListDump = BuildDisplayListDump(screenshot.RenderContext),
                NetworkRequests = networkCapture.Snapshot(),
                Probes = probeResults,
                RenderedTextSample = text.Length > 800 ? text.Substring(0, 800) : text,
                ScreenshotCaptured = screenshot.Captured,
                ScreenshotPath = screenshot.Path,
                ScreenshotWidth = screenshot.Width,
                ScreenshotHeight = screenshot.Height,
                ScreenshotError = screenshot.Error,
                Interaction = interaction
            };
        }

        private static void ConfigureDebugSiteFileLogging()
        {
            var logsDir = DiagnosticPaths.GetLogsDirectory();
            Directory.CreateDirectory(logsDir);
            var ndjsonPath = Path.Combine(logsDir, $"fenbrowser_{DateTime.Now:yyyyMMdd_HHmmss}.jsonl");

            EngineLog.Configure(new EngineLoggingOptions
            {
                Enabled = true,
                GlobalMinimumSeverity = LogSeverity.Debug,
                EnableConsoleSink = false,
                EnableDebugSink = false,
                EnableNdjsonSink = true,
                EnableRingBufferSink = true,
                EnableTraceSink = true,
                RingBufferCapacity = Math.Max(1000, BrowserSettings.Instance?.Logging?.MemoryBufferSize ?? 5000),
                NdjsonFilePath = ndjsonPath,
                TraceFilePath = ndjsonPath.Replace(".jsonl", "_trace.jsonl", StringComparison.OrdinalIgnoreCase)
            });
        }

        internal static DebugSiteScreenshotResult CaptureDebugSiteScreenshot(
            FenBrowser.Core.Dom.V2.Node root,
            Dictionary<FenBrowser.Core.Dom.V2.Node, CssComputed> styles,
            string baseUrl)
        {
            const int width = DebugSiteViewportWidth;
            const int height = DebugSiteViewportHeight;
            var screenshotPath = DiagnosticPaths.GetRootArtifactPath("debug_site_screenshot.png");

            if (root == null)
            {
                return new DebugSiteScreenshotResult(false, screenshotPath, width, height, "DOM root was not available.", null, null);
            }

            if (styles == null || styles.Count == 0)
            {
                return new DebugSiteScreenshotResult(false, screenshotPath, width, height, "Computed styles were not available.", null, null);
            }

            try
            {
                if (File.Exists(screenshotPath))
                {
                    File.Delete(screenshotPath);
                }

                using var bitmap = new SKBitmap(width, height);
                using var canvas = new SKCanvas(bitmap);
                canvas.Clear(SKColors.White);

                var renderer = new FenBrowser.FenEngine.Rendering.SkiaDomRenderer();
                renderer.Render(
                    root,
                    canvas,
                    styles,
                    new SKRect(0, 0, width, height),
                    baseUrl,
                    emitVerificationReport: false);
                canvas.Flush();
                var renderContext = renderer.CreateRenderContext();
                var telemetry = renderer.LastFrameTelemetry;

                using var image = SKImage.FromBitmap(bitmap);
                using var data = image.Encode(SKEncodedImageFormat.Png, 100);
                if (data == null)
                {
                    return new DebugSiteScreenshotResult(false, screenshotPath, width, height, "PNG encoding returned null.", renderContext, telemetry);
                }

                using (var stream = File.Open(screenshotPath, FileMode.Create, FileAccess.Write, FileShare.Read))
                {
                    data.SaveTo(stream);
                }

                return new DebugSiteScreenshotResult(true, screenshotPath, width, height, null, renderContext, telemetry);
            }
            catch (Exception ex)
            {
                return new DebugSiteScreenshotResult(false, screenshotPath, width, height, ex.GetType().Name + ": " + ex.Message, null, null);
            }
        }

        private static void PrintDebugSiteReport(DebugSiteReport report)
        {
            Console.WriteLine();
            Console.WriteLine("---- page global probes ----");
            foreach (var kvp in report.Probes)
            {
                Console.WriteLine($"   {kvp.Key}  =>  {kvp.Value}");
            }

            Console.WriteLine();
            Console.WriteLine("================ DEBUG-SITE REPORT ================");
            Console.WriteLine($"NavigateAsync returned : {report.NavigateReturned}");
            Console.WriteLine($"Final URL              : {report.FinalUrl}");
            Console.WriteLine($"Elapsed                : {report.ElapsedMs} ms");
            Console.WriteLine($"DOM nodes              : {report.DomNodeCount}");
            Console.WriteLine($"Raw HTML length        : {report.RawHtmlLength}");
            Console.WriteLine($"Rendered text length   : {report.RenderedTextLength}");
            Console.WriteLine($"Computed styles count  : {report.ComputedStylesCount}");
            Console.WriteLine($"Layout boxes           : {report.StyleLayout?.BoxCount ?? 0}");
            Console.WriteLine($"Paint nodes            : {report.StyleLayout?.PaintNodeCount ?? 0}");
            Console.WriteLine($"Screenshot captured    : {report.ScreenshotCaptured}");
            Console.WriteLine($"Navigation phase       : {report.Lifecycle?.Phase ?? "(unknown)"}");
            Console.WriteLine($"Navigation transition detail: {report.Lifecycle?.Detail ?? "(none)"}");
            Console.WriteLine($"Lifecycle transitions  : {report.Lifecycle?.TransitionCount ?? 0}");
            Console.WriteLine($"Scripts discovered     : {report.ScriptLoading?.TotalScripts ?? 0}");
            Console.WriteLine($"Scripts executed       : {report.ScriptLoading?.ExecutionCompleted ?? 0}");
            Console.WriteLine($"Scripts failed         : {report.ScriptLoading?.ExecutionFailed ?? 0}");
            Console.WriteLine($"DOMContentLoaded fired : {report.EventLoop?.DomContentLoadedFired ?? false}");
            Console.WriteLine($"Load fired             : {report.EventLoop?.LoadFired ?? false}");
            Console.WriteLine($"Microtask checkpoints  : {report.EventLoop?.MicrotaskCheckpoints ?? 0}");
            Console.WriteLine($"Network requests       : {report.NetworkRequests.Count}");
            Console.WriteLine($"Interaction status     : {report.Interaction?.Status ?? "not-run"}");
            if (report.Interaction != null && !string.IsNullOrWhiteSpace(report.Interaction.Error))
            {
                Console.WriteLine($"Interaction error      : {report.Interaction.Error}");
            }
            if (!report.ScreenshotCaptured && !string.IsNullOrWhiteSpace(report.ScreenshotError))
            {
                Console.WriteLine($"Screenshot error       : {report.ScreenshotError}");
            }
            if (!string.IsNullOrWhiteSpace(report.NavigateException))
            {
                var firstLine = report.NavigateException
                    .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                    .FirstOrDefault();
                Console.WriteLine($"Navigate exception     : {firstLine}");
            }
            Console.WriteLine($"Nav failures           : {report.NavigationFailures.Count}");
            foreach (var failure in report.NavigationFailures.Take(20)) Console.WriteLine($"   ! {failure}");
            Console.WriteLine($"Console messages       : {report.ConsoleMessages.Count}");
            foreach (var message in report.ConsoleMessages.Take(60))
            {
                var line = message.Replace("\n", " ").Replace("\r", " ");
                if (line.Length > 300) line = line.Substring(0, 300) + "...";
                Console.WriteLine($"   > {line}");
            }
            Console.WriteLine();
            Console.WriteLine("---- rendered text (first 800 chars) ----");
            Console.WriteLine(report.RenderedTextSample);
            Console.WriteLine("===================================================");
        }

        private static string WriteDebugSiteBundle(DebugSiteReport report)
        {
            var runId = DateTime.UtcNow.ToString("yyyyMMddTHHmmssZ", CultureInfo.InvariantCulture);
            var siteId = MakeSafeSiteId(report.FinalUrl, report.Url);
            var bundleDir = Path.Combine(DiagnosticPaths.GetLogsDirectory(), "real-site", siteId, runId);
            Directory.CreateDirectory(bundleDir);

            var jsonOptions = new JsonSerializerOptions { WriteIndented = true };
            var exceptionSummary = BuildExceptionSummary(report);
            var firstBlocker = BuildFirstBlocker(report);
            var artifactExportFailures = new List<DebugSiteArtifactExportFailure>();
            var utf8 = new UTF8Encoding(false);
            void WriteText(string name, Func<string> contentFactory) =>
                TryWriteDebugSiteArtifact(
                    bundleDir,
                    name,
                    path => File.WriteAllText(path, contentFactory(), utf8),
                    artifactExportFailures);
            void WriteLines(string name, Func<IEnumerable<string>> linesFactory) =>
                TryWriteDebugSiteArtifact(
                    bundleDir,
                    name,
                    path => File.WriteAllLines(path, linesFactory(), utf8),
                    artifactExportFailures);

            WriteText("summary.json", () => JsonSerializer.Serialize(report, jsonOptions));
            WriteText("summary.md", () => BuildDebugSiteSummary(report, runId, exceptionSummary));
            WriteLines("console.log", () => report.ConsoleMessages);
            WriteLines("navigation_failures.log", () => report.NavigationFailures);
            WriteText("rendered_text.txt", () => report.RenderedTextSample ?? string.Empty);
            WriteText("probes.json", () => JsonSerializer.Serialize(report.Probes, jsonOptions));
            WriteText("exceptions.json", () => JsonSerializer.Serialize(exceptionSummary, jsonOptions));
            WriteText(
                "missing_apis.json",
                () => JsonSerializer.Serialize(
                    BuildMissingApiSnapshot(report),
                    new JsonSerializerOptions
                    {
                        WriteIndented = true,
                        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                    }));
            WriteText("network.json", () => JsonSerializer.Serialize(BuildNetworkSummary(report), jsonOptions));
            WriteText(
                "lifecycle.json",
                () => JsonSerializer.Serialize(
                    report.Lifecycle ?? BuildLifecycleSummary(null, report.Probes, null, report.EventLoop),
                    jsonOptions));
            WriteText(
                "lifecycle_timeline.json",
                () => JsonSerializer.Serialize(
                    report.Lifecycle?.Transitions ?? new List<DebugSiteLifecycleTransition>(),
                    jsonOptions));
            WriteText(
                "script_loading.json",
                () => JsonSerializer.Serialize(
                    report.ScriptLoading ?? new BrowserScriptLoadingSnapshot(),
                    jsonOptions));
            WriteText(
                "event_loop.json",
                () => JsonSerializer.Serialize(
                    report.EventLoop ?? new BrowserEventLoopSnapshot(),
                    jsonOptions));
            WriteText("first_blocker.json", () => JsonSerializer.Serialize(firstBlocker, jsonOptions));
            WriteText("ipc.json", () => JsonSerializer.Serialize(BuildInactiveIpcArtifact(), jsonOptions));
            WriteText(
                "sandbox_denials.json",
                () => JsonSerializer.Serialize(BuildInactiveSandboxDenialsArtifact(), jsonOptions));
            WriteText(
                "performance.json",
                () => JsonSerializer.Serialize(BuildPerformanceArtifact(report), jsonOptions));
            if (report.Interaction != null)
            {
                WriteText(
                    "interaction.json",
                    () => JsonSerializer.Serialize(report.Interaction, jsonOptions));
            }
            WriteText(
                "style_layout.json",
                () => JsonSerializer.Serialize(
                    report.StyleLayout ?? new DebugSiteStyleLayoutSummary(),
                    jsonOptions));
            WriteText("style_dump.txt", () => report.StyleDump ?? string.Empty);
            WriteText("layout_dump.txt", () => report.LayoutDump ?? string.Empty);
            WriteText("paint_dump.txt", () => report.PaintDump ?? string.Empty);
            WriteText("display_list.txt", () => report.DisplayListDump ?? string.Empty);

            TryCopyLogArtifact("debug_site_screenshot.png", Path.Combine(bundleDir, "screenshot.png"));
            if (report.Interaction != null)
            {
                if (report.Interaction.BeforeScreenshotCaptured)
                {
                    TryCopyLogArtifact("debug_interaction_before.png", Path.Combine(bundleDir, "interaction_before.png"));
                }
                if (report.Interaction.AfterScreenshotCaptured)
                {
                    TryCopyLogArtifact("debug_site_screenshot.png", Path.Combine(bundleDir, "interaction_after.png"));
                }
            }
            TryCopyLogArtifact("dom_dump.txt", Path.Combine(bundleDir, "dom_dump.txt"));
            TryCopyLatestLogArtifact("raw_source_*.html", Path.Combine(bundleDir, "raw_source.html"));
            TryCopyLatestLogArtifact("rendered_text_*.txt", Path.Combine(bundleDir, "rendered_text_artifact.txt"));
            TryCopyLatestLogArtifact("fenbrowser_trace_*.jsonl", Path.Combine(bundleDir, "trace.jsonl"));
            TryCopyLatestLogArtifact("fenbrowser_*_trace.jsonl", Path.Combine(bundleDir, "trace.jsonl"));
            TryCopyLatestStructuredLogArtifact(Path.Combine(bundleDir, "logs.ndjson"));

            var artifactManifest = BuildArtifactManifest(
                bundleDir,
                report.Interaction != null,
                artifactExportFailures);
            var missingRequiredArtifacts = artifactManifest
                .Where(static artifact => !artifact.exists)
                .Select(static artifact => artifact.name)
                .ToArray();
            if (missingRequiredArtifacts.Length > 0)
            {
                firstBlocker = BuildFirstBlocker(report, missingRequiredArtifacts);
                WriteText(
                    "first_blocker.json",
                    () => JsonSerializer.Serialize(firstBlocker, jsonOptions));
                artifactManifest = BuildArtifactManifest(
                    bundleDir,
                    report.Interaction != null,
                    artifactExportFailures);
            }

            WriteText(
                "artifact_manifest.json",
                () => JsonSerializer.Serialize(artifactManifest, jsonOptions));

            return bundleDir;
        }

        private static string BuildDebugSiteSummary(
            DebugSiteReport report,
            string runId,
            DebugSiteExceptionSummary exceptionSummary)
        {
            var firstConsoleError = report.ConsoleMessages.FirstOrDefault(IsLikelyErrorMessage) ?? "(none captured)";
            var firstNavFailure = report.NavigationFailures.FirstOrDefault() ?? "(none captured)";
            var firstMissingApi = report.MissingApiSnapshot?.Records.FirstOrDefault()?.ApiName
                ?? report.MissingApis?.FirstOrDefault()?.Api
                ?? "(none captured)";
            var firstLayoutBlocker = report.StyleLayout?.FirstLayoutBlocker ?? "(not captured)";
            var firstPaintBlocker = report.StyleLayout?.FirstPaintBlocker ?? "(not captured)";
            var failedNetworkRequests = report.NetworkRequests.Count(request => request.Failed || (request.StatusCode.HasValue && request.StatusCode.Value >= 400));

            var sb = new StringBuilder();
            sb.AppendLine("# FenBrowser Real-Site Debug Run");
            sb.AppendLine();
            sb.AppendLine($"Run ID: {runId}");
            sb.AppendLine($"URL: {report.Url}");
            sb.AppendLine($"Final URL: {report.FinalUrl}");
            sb.AppendLine($"NavigateAsync returned: {report.NavigateReturned}");
            sb.AppendLine($"Elapsed: {report.ElapsedMs} ms");
            sb.AppendLine($"DOM nodes: {report.DomNodeCount}");
            sb.AppendLine($"Raw HTML length: {report.RawHtmlLength}");
            sb.AppendLine($"Rendered text length: {report.RenderedTextLength}");
            sb.AppendLine($"Computed styles: {report.ComputedStylesCount}");
            sb.AppendLine($"Style/layout status: {report.StyleLayout?.Status ?? "not-captured"}");
            sb.AppendLine($"Styled DOM nodes: {report.StyleLayout?.StyledNodeCount ?? 0}");
            sb.AppendLine($"Unstyled elements: {report.StyleLayout?.UnstyledElementCount ?? 0}");
            sb.AppendLine($"Layout boxes: {report.StyleLayout?.BoxCount ?? 0}");
            sb.AppendLine($"Zero-area boxes: {report.StyleLayout?.ZeroAreaBoxCount ?? 0}");
            sb.AppendLine($"Paint roots: {report.StyleLayout?.PaintRootCount ?? 0}");
            sb.AppendLine($"Paint nodes: {report.StyleLayout?.PaintNodeCount ?? 0}");
            sb.AppendLine($"Raster mode: {report.StyleLayout?.RasterMode ?? "none"}");
            sb.AppendLine($"Screenshot captured: {report.ScreenshotCaptured}");
            if (!report.ScreenshotCaptured && !string.IsNullOrWhiteSpace(report.ScreenshotError))
            {
                sb.AppendLine($"Screenshot error: {report.ScreenshotError}");
            }
            sb.AppendLine($"Navigation lifecycle phase: {report.Lifecycle?.Phase ?? "(unknown)"}");
            sb.AppendLine($"Navigation transition detail: {report.Lifecycle?.Detail ?? "(none)"}");
            sb.AppendLine($"Navigation lifecycle transitions: {report.Lifecycle?.TransitionCount ?? 0}");
            sb.AppendLine($"Navigation lifecycle phases: {FormatLifecyclePhasePath(report.Lifecycle?.TransitionPhases)}");
            sb.AppendLine($"Document readyState probe: {report.Lifecycle?.DocumentReadyState ?? "(not captured)"}");
            sb.AppendLine($"Scripts discovered: {report.ScriptLoading?.TotalScripts ?? 0}");
            sb.AppendLine($"Scripts eligible: {report.ScriptLoading?.EligibleScripts ?? 0}");
            sb.AppendLine($"Scripts executed: {report.ScriptLoading?.ExecutionCompleted ?? 0}");
            sb.AppendLine($"Scripts failed: {report.ScriptLoading?.ExecutionFailed ?? 0}");
            sb.AppendLine($"Script fetch failures: {report.ScriptLoading?.FetchFailed ?? 0}");
            sb.AppendLine($"Event-loop status: {report.EventLoop?.Status ?? "not-run"}");
            sb.AppendLine($"DOMContentLoaded fired: {report.EventLoop?.DomContentLoadedFired ?? false}");
            sb.AppendLine($"DOMContentLoaded UTC: {report.EventLoop?.DomContentLoadedUtc ?? string.Empty}");
            sb.AppendLine($"Load fired: {report.EventLoop?.LoadFired ?? false}");
            sb.AppendLine($"Load UTC: {report.EventLoop?.LoadUtc ?? string.Empty}");
            sb.AppendLine($"Microtask checkpoints: {report.EventLoop?.MicrotaskCheckpoints ?? 0}");
            sb.AppendLine($"Timers scheduled: {report.EventLoop?.TimersScheduled ?? 0}");
            sb.AppendLine($"Animation frames executed: {report.EventLoop?.AnimationFramesExecuted ?? 0}");
            sb.AppendLine($"Callback failures: {report.EventLoop?.CallbackFailures ?? 0}");
            sb.AppendLine($"Callback failure records retained: {report.EventLoop?.CallbackFailureRecords?.Count ?? 0}");
            sb.AppendLine($"Exceptions total: {exceptionSummary?.TotalCount ?? 0}");
            sb.AppendLine($"Other exceptions: {exceptionSummary?.OtherExceptionCount ?? 0}");
            sb.AppendLine($"Logger drain before export: {(report.LoggerDrainSucceeded ? "succeeded" : "timed-out")}");
            sb.AppendLine($"Logger drain timeout: {report.LoggerDrainTimeoutMs} ms");
            sb.AppendLine($"Network requests: {report.NetworkRequests.Count}");
            sb.AppendLine($"Failed network requests: {failedNetworkRequests}");
            sb.AppendLine($"Navigation failures: {report.NavigationFailures.Count}");
            sb.AppendLine($"Console messages: {report.ConsoleMessages.Count}");
            sb.AppendLine($"Interaction status: {report.Interaction?.Status ?? "not-run"}");
            if (report.Interaction != null)
            {
                sb.AppendLine($"Interaction target found: {report.Interaction.InputTargetFound}");
                sb.AppendLine($"Interaction focus acquired: {report.Interaction.FocusAcquired}");
                sb.AppendLine($"Interaction text accepted: {report.Interaction.TextAccepted}");
                sb.AppendLine($"Interaction submit attempted: {report.Interaction.SubmitAttempted}");
                sb.AppendLine($"Interaction outcome observed: {report.Interaction.SubmissionOutcomeObserved}");
                sb.AppendLine($"Interaction outcome settled: {report.Interaction.SubmissionOutcomeSettled}");
                sb.AppendLine($"Interaction event records: {report.Interaction.EventRecords.Count}");
                if (!string.IsNullOrWhiteSpace(report.Interaction.Error))
                {
                    sb.AppendLine($"Interaction error: {report.Interaction.Error}");
                }
            }
            sb.AppendLine();
            sb.AppendLine("## First Blocker Signals");
            sb.AppendLine();
            sb.AppendLine($"First fatal console error: {firstConsoleError}");
            sb.AppendLine($"First fatal network/navigation error: {firstNavFailure}");
            sb.AppendLine($"First missing API: {firstMissingApi}");
            sb.AppendLine($"Navigation lifecycle terminal: {report.Lifecycle?.IsTerminalPhase ?? false}");
            sb.AppendLine($"Script loading status: {report.ScriptLoading?.Status ?? "not-run"}");
            sb.AppendLine($"First layout blocker: {firstLayoutBlocker}");
            sb.AppendLine($"First paint blocker: {firstPaintBlocker}");
            sb.AppendLine($"DOMContentLoaded fired: {report.EventLoop?.DomContentLoadedFired ?? false}");
            sb.AppendLine($"Load fired: {report.EventLoop?.LoadFired ?? false}");
            sb.AppendLine();
            sb.AppendLine("## Artifact Contract");
            sb.AppendLine();
            sb.AppendLine("- `summary.json`: structured run summary.");
            sb.AppendLine("- `trace.jsonl`: copied from latest engine trace when present.");
            sb.AppendLine("- `logs.ndjson`: copied from latest structured engine log when present.");
            sb.AppendLine("- `screenshot.png`: copied from Tooling-owned `logs/debug_site_screenshot.png` when present.");
            sb.AppendLine("- `lifecycle.json`: final navigation lifecycle snapshot and document readyState probe.");
            sb.AppendLine("- `lifecycle_timeline.json`: ordered navigation lifecycle transitions captured from `BrowserHost.NavigationLifecycleChanged`.");
            sb.AppendLine("- `script_loading.json`: script discovery, fetch, execution, failure, and async-pending counts.");
            sb.AppendLine("- `event_loop.json`: DOMContentLoaded/load, microtask, timer, and requestAnimationFrame counters.");
            sb.AppendLine("- `interaction.json`: click, focus, text, submit, request/navigation, and bounded event evidence when interaction was requested.");
            sb.AppendLine("- `interaction_before.png` / `interaction_after.png`: correlated screenshots when interaction was requested.");
            sb.AppendLine("- `first_blocker.json`: deterministic milestone dependency and first-causal-blocker classification.");
            sb.AppendLine("- `ipc.json`: typed IPC state; inactive with zero events for the current in-process debug-site host.");
            sb.AppendLine("- `sandbox_denials.json`: typed sandbox state; inactive with zero denials when no sandbox is configured.");
            sb.AppendLine("- `performance.json`: single diagnostic sample from already-captured navigation and render-stage telemetry; not a benchmark.");
            sb.AppendLine("- `style_layout.json`: style/layout/paint counters, timing, status, and first blocker classification.");
            sb.AppendLine("- `style_dump.txt`: DOM preorder computed-style snapshot.");
            sb.AppendLine("- `layout_dump.txt`: layout Box tree snapshot with geometry.");
            sb.AppendLine("- `paint_dump.txt`: Paint Tree snapshot with node bounds and paint-specific fields.");
            sb.AppendLine("- `display_list.txt`: flattened Paint Tree order used as the current display-list proxy.");
            sb.AppendLine("- `dom_dump.txt`: copied from `logs/dom_dump.txt` when present.");
            sb.AppendLine("- `artifact_manifest.json`: present/missing artifact status.");
            return sb.ToString();
        }

        private static DebugSiteLifecycleSummary BuildLifecycleSummary(
            NavigationLifecycleSnapshot snapshot,
            IReadOnlyDictionary<string, string> probes,
            IReadOnlyList<DebugSiteLifecycleTransition> transitions = null,
            BrowserEventLoopSnapshot eventLoop = null)
        {
            string readyState = null;
            probes?.TryGetValue("typeof document !== 'undefined' ? document.readyState : 'no-document'", out readyState);

            var phase = snapshot?.Phase ?? NavigationLifecyclePhase.Idle;
            var isTerminal = phase == NavigationLifecyclePhase.Complete ||
                             phase == NavigationLifecyclePhase.Failed ||
                             phase == NavigationLifecyclePhase.Cancelled;
            var normalizedTransitions = NormalizeLifecycleTransitions(transitions);

            return new DebugSiteLifecycleSummary
            {
                NavigationId = snapshot?.NavigationId ?? 0,
                Phase = phase.ToString(),
                RequestedUrl = snapshot?.RequestedUrl ?? string.Empty,
                EffectiveUrl = snapshot?.EffectiveUrl ?? string.Empty,
                ResponseStatus = snapshot?.ResponseStatus ?? string.Empty,
                Detail = snapshot?.Detail ?? string.Empty,
                IsUserInput = snapshot?.IsUserInput ?? false,
                IsRedirect = snapshot?.IsRedirect ?? false,
                RedirectCount = snapshot?.RedirectCount ?? 0,
                CommitSource = snapshot?.CommitSource ?? string.Empty,
                LastTransitionUtc = snapshot?.LastTransitionUtc.ToString("O", CultureInfo.InvariantCulture) ?? string.Empty,
                DocumentReadyState = readyState ?? string.Empty,
                IsTerminalPhase = isTerminal,
                IsSuccessfulTerminalPhase = phase == NavigationLifecyclePhase.Complete,
                IsFailureTerminalPhase = phase == NavigationLifecyclePhase.Failed || phase == NavigationLifecyclePhase.Cancelled,
                TransitionCount = normalizedTransitions.Count,
                TransitionPhases = normalizedTransitions.Select(static transition => transition.Phase).ToList(),
                FirstTransitionUtc = normalizedTransitions.FirstOrDefault()?.TimestampUtc ?? string.Empty,
                TerminalTransitionUtc = normalizedTransitions.LastOrDefault()?.TimestampUtc ?? string.Empty,
                DomContentLoadedFired = eventLoop?.DomContentLoadedFired ?? false,
                DomContentLoadedUtc = eventLoop?.DomContentLoadedUtc ?? string.Empty,
                LoadFired = eventLoop?.LoadFired ?? false,
                LoadUtc = eventLoop?.LoadUtc ?? string.Empty,
                Transitions = normalizedTransitions
            };
        }

        private static List<DebugSiteLifecycleTransition> NormalizeLifecycleTransitions(
            IReadOnlyList<DebugSiteLifecycleTransition> transitions)
        {
            var normalized = new List<DebugSiteLifecycleTransition>();
            if (transitions == null || transitions.Count == 0)
            {
                return normalized;
            }

            DateTimeOffset? firstTimestamp = null;
            foreach (var transition in transitions)
            {
                if (transition == null)
                {
                    continue;
                }

                var clone = transition.Clone();
                clone.Sequence = normalized.Count + 1;
                if (TryParseIsoTimestamp(clone.TimestampUtc, out var timestamp))
                {
                    firstTimestamp ??= timestamp;
                    clone.MillisecondsSinceFirstTransition = Math.Max(
                        0,
                        (timestamp - firstTimestamp.Value).TotalMilliseconds);
                }

                normalized.Add(clone);
            }

            return normalized;
        }

        private static bool TryParseIsoTimestamp(string timestamp, out DateTimeOffset value)
        {
            return DateTimeOffset.TryParse(
                timestamp,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out value);
        }

        private static string FormatLifecyclePhasePath(IReadOnlyList<string> phases)
        {
            return phases == null || phases.Count == 0
                ? "(none captured)"
                : string.Join(" -> ", phases);
        }

        private static DebugSiteStyleLayoutSummary BuildStyleLayoutSummary(
            Node root,
            IReadOnlyDictionary<Node, CssComputed> styles,
            RenderContext renderContext,
            RenderFrameTelemetry telemetry,
            bool screenshotCaptured,
            string screenshotError)
        {
            var effectiveStyles = styles ?? renderContext?.Styles;
            var domNodes = CollectDomTraversal(root);
            var elementCount = domNodes.Count(item => item.Node is Element);
            var styledNodeCount = effectiveStyles?.Count ?? 0;
            var styledElementCount = effectiveStyles?.Keys.Count(node => node is Element) ?? 0;
            var unstyledElementCount = effectiveStyles == null
                ? elementCount
                : domNodes.Count(item => item.Node is Element && !effectiveStyles.ContainsKey(item.Node));
            var boxCount = renderContext?.Boxes?.Count ?? 0;
            var zeroAreaBoxCount = renderContext?.Boxes?.Values.Count(IsZeroAreaBox) ?? 0;
            var paintRootCount = renderContext?.PaintTreeRoots?.Count ?? 0;
            var paintNodeCount = CountPaintNodes(renderContext?.PaintTreeRoots);
            var layoutBlocker = ClassifyFirstLayoutBlocker(root, effectiveStyles, renderContext, boxCount, zeroAreaBoxCount);
            var paintBlocker = ClassifyFirstPaintBlocker(layoutBlocker, renderContext, paintNodeCount, screenshotCaptured, screenshotError);
            var status = ClassifyStyleLayoutStatus(root, effectiveStyles, renderContext, boxCount, paintNodeCount, screenshotCaptured);

            return new DebugSiteStyleLayoutSummary
            {
                Status = status,
                DomRootPresent = root != null,
                DomNodeCount = domNodes.Count,
                ElementCount = elementCount,
                ComputedStyleCount = styledNodeCount,
                StyledNodeCount = styledNodeCount,
                StyledElementCount = styledElementCount,
                UnstyledElementCount = unstyledElementCount,
                RenderContextAvailable = renderContext != null,
                BoxCount = boxCount,
                ZeroAreaBoxCount = zeroAreaBoxCount,
                PaintRootCount = paintRootCount,
                PaintNodeCount = paintNodeCount,
                DisplayListNodeCount = paintNodeCount,
                DisplayListSource = "paint-tree-flattened",
                ViewportWidth = renderContext?.ViewportWidth ?? 0,
                ViewportHeight = renderContext?.ViewportHeight ?? 0,
                LayoutRan = boxCount > 0,
                PaintRan = paintNodeCount > 0,
                RasterRan = screenshotCaptured,
                ScreenshotCaptured = screenshotCaptured,
                RasterMode = telemetry?.RasterMode.ToString() ?? "none",
                LayoutDurationMs = telemetry?.LayoutDurationMs ?? 0,
                PaintDurationMs = telemetry?.PaintDurationMs ?? 0,
                RasterDurationMs = telemetry?.RasterDurationMs ?? 0,
                TotalRenderDurationMs = telemetry?.TotalDurationMs ?? 0,
                WatchdogTriggered = telemetry?.WatchdogTriggered ?? false,
                WatchdogReason = telemetry?.WatchdogReason ?? string.Empty,
                FirstLayoutBlocker = layoutBlocker,
                FirstPaintBlocker = paintBlocker
            };
        }

        private static string BuildStyleDump(Node root, IReadOnlyDictionary<Node, CssComputed> styles)
        {
            var sb = new StringBuilder();
            sb.AppendLine("# FenBrowser Computed Style Dump");
            sb.AppendLine();

            if (root == null)
            {
                sb.AppendLine("DOM root: unavailable");
                return sb.ToString();
            }

            if (styles == null || styles.Count == 0)
            {
                sb.AppendLine("Computed styles: unavailable");
                return sb.ToString();
            }

            foreach (var item in CollectDomTraversal(root))
            {
                var indent = new string(' ', item.Depth * 2);
                sb.Append(indent);
                sb.Append(item.Path);
                sb.Append(" ");
                sb.Append(FormatNodeLabel(item.Node));

                if (styles.TryGetValue(item.Node, out var style) && style != null)
                {
                    sb.Append(" ");
                    sb.Append(FormatStyleProperties(style));
                }
                else
                {
                    sb.Append(" style=(none)");
                }

                sb.AppendLine();
            }

            return sb.ToString();
        }

        private static string BuildLayoutDump(Node root, RenderContext renderContext)
        {
            if (root == null)
            {
                return "layout dump unavailable: DOM root was not available." + Environment.NewLine;
            }

            if (renderContext == null)
            {
                return "layout dump unavailable: render context was not available after diagnostic render." + Environment.NewLine;
            }

            if (renderContext.Boxes == null || renderContext.Boxes.Count == 0)
            {
                return "layout dump unavailable: diagnostic render produced no layout boxes." + Environment.NewLine;
            }

            if (root is not Element rootElement)
            {
                return "layout dump unavailable: DOM root was not an Element." + Environment.NewLine;
            }

            try
            {
                return LayoutTreeDumper.DumpTree(
                    rootElement,
                    renderContext.Boxes,
                    renderContext.Styles,
                    LayoutTreeDumper.OutputFormat.IndentedText);
            }
            catch (Exception ex)
            {
                return $"layout dump failed: {ex.GetType().Name}: {ex.Message}{Environment.NewLine}";
            }
        }

        private static string BuildPaintDump(RenderContext renderContext)
        {
            var sb = new StringBuilder();
            sb.AppendLine("# FenBrowser Paint Tree Dump");
            sb.AppendLine();

            var roots = renderContext?.PaintTreeRoots;
            if (roots == null || roots.Count == 0)
            {
                sb.AppendLine("paint tree: unavailable");
                return sb.ToString();
            }

            foreach (var root in roots)
            {
                AppendPaintNodeDump(sb, root, 0);
            }

            return sb.ToString();
        }

        private static string BuildDisplayListDump(RenderContext renderContext)
        {
            var sb = new StringBuilder();
            sb.AppendLine("# FenBrowser Display List Dump");
            sb.AppendLine("source: flattened immutable Paint Tree order");
            sb.AppendLine();

            var roots = renderContext?.PaintTreeRoots;
            if (roots == null || roots.Count == 0)
            {
                sb.AppendLine("display list: unavailable");
                return sb.ToString();
            }

            var sequence = 0;
            foreach (var root in roots)
            {
                AppendDisplayListNode(sb, root, ref sequence);
            }

            return sb.ToString();
        }

        private static void AppendPaintNodeDump(StringBuilder sb, PaintNodeBase node, int depth)
        {
            if (node == null)
            {
                return;
            }

            var indent = new string(' ', depth * 2);
            sb.Append(indent);
            sb.Append(node.GetType().Name);
            sb.Append(" bounds=");
            sb.Append(FormatRect(node.Bounds));
            sb.Append(" opacity=");
            sb.Append(node.Opacity.ToString("0.###", CultureInfo.InvariantCulture));
            sb.Append(" source=");
            sb.Append(FormatNodeLabel(node.SourceNode));
            if (node.ClipRect.HasValue)
            {
                sb.Append(" clip=");
                sb.Append(FormatRect(node.ClipRect.Value));
            }
            AppendPaintNodeSpecificFields(sb, node);
            sb.AppendLine();

            var children = node.Children;
            if (children == null)
            {
                return;
            }

            foreach (var child in children)
            {
                AppendPaintNodeDump(sb, child, depth + 1);
            }
        }

        private static void AppendDisplayListNode(StringBuilder sb, PaintNodeBase node, ref int sequence)
        {
            if (node == null)
            {
                return;
            }

            sequence++;
            sb.Append(sequence.ToString("D5", CultureInfo.InvariantCulture));
            sb.Append(" ");
            sb.Append(node.GetType().Name);
            sb.Append(" bounds=");
            sb.Append(FormatRect(node.Bounds));
            sb.Append(" source=");
            sb.Append(FormatNodeLabel(node.SourceNode));
            AppendPaintNodeSpecificFields(sb, node);
            sb.AppendLine();

            var children = node.Children;
            if (children == null)
            {
                return;
            }

            foreach (var child in children)
            {
                AppendDisplayListNode(sb, child, ref sequence);
            }
        }

        private static void AppendPaintNodeSpecificFields(StringBuilder sb, PaintNodeBase node)
        {
            switch (node)
            {
                case BackgroundPaintNode background:
                    sb.Append(" color=");
                    sb.Append(FormatColor(background.Color));
                    sb.Append(" gradient=");
                    sb.Append(background.Gradient != null);
                    break;
                case BorderPaintNode border:
                    sb.Append(" widths=");
                    sb.Append(FormatFloatArray(border.Widths));
                    sb.Append(" styles=");
                    sb.Append(border.Styles == null ? "(none)" : string.Join(",", border.Styles));
                    break;
                case TextPaintNode text:
                    sb.Append(" text=\"");
                    sb.Append(TrimText(text.FallbackText, 80));
                    sb.Append("\" glyphs=");
                    sb.Append(text.Glyphs?.Count ?? 0);
                    sb.Append(" fontSize=");
                    sb.Append(text.FontSize.ToString("0.###", CultureInfo.InvariantCulture));
                    break;
                case ImagePaintNode image:
                    sb.Append(" objectFit=");
                    sb.Append(image.ObjectFit ?? string.Empty);
                    sb.Append(" background=");
                    sb.Append(image.IsBackgroundImage);
                    sb.Append(" bitmap=");
                    sb.Append(image.Bitmap == null ? "(none)" : $"{image.Bitmap.Width}x{image.Bitmap.Height}");
                    break;
                case StackingContextPaintNode stacking:
                    sb.Append(" zIndex=");
                    sb.Append(stacking.ZIndex);
                    if (!string.IsNullOrWhiteSpace(stacking.Filter))
                    {
                        sb.Append(" filter=");
                        sb.Append(stacking.Filter);
                    }
                    break;
                case ClipPaintNode clip:
                    sb.Append(" clipPath=");
                    sb.Append(clip.ClipPath != null);
                    break;
                case ScrollPaintNode scroll:
                    sb.Append(" scroll=");
                    sb.Append(scroll.ScrollX.ToString("0.###", CultureInfo.InvariantCulture));
                    sb.Append(",");
                    sb.Append(scroll.ScrollY.ToString("0.###", CultureInfo.InvariantCulture));
                    break;
                case StickyPaintNode sticky:
                    sb.Append(" stickyOffset=");
                    sb.Append(FormatPoint(sticky.StickyOffset));
                    break;
            }
        }

        private static string ClassifyStyleLayoutStatus(
            Node root,
            IReadOnlyDictionary<Node, CssComputed> styles,
            RenderContext renderContext,
            int boxCount,
            int paintNodeCount,
            bool screenshotCaptured)
        {
            if (root == null) return "dom-missing";
            if (styles == null || styles.Count == 0) return "style-missing";
            if (renderContext == null) return "render-context-missing";
            if (boxCount == 0) return "layout-missing";
            if (paintNodeCount == 0) return "paint-missing";
            if (!screenshotCaptured) return "raster-missing";
            return "captured";
        }

        private static string ClassifyFirstLayoutBlocker(
            Node root,
            IReadOnlyDictionary<Node, CssComputed> styles,
            RenderContext renderContext,
            int boxCount,
            int zeroAreaBoxCount)
        {
            if (root == null) return "DOM root was not available.";
            if (styles == null || styles.Count == 0) return "Computed styles were not available.";
            if (renderContext == null) return "Render context was not available after diagnostic render.";
            if (boxCount == 0) return "Diagnostic render produced no layout boxes.";
            if (zeroAreaBoxCount == boxCount) return "All layout boxes were zero-area.";
            return "(none captured)";
        }

        private static string ClassifyFirstPaintBlocker(
            string firstLayoutBlocker,
            RenderContext renderContext,
            int paintNodeCount,
            bool screenshotCaptured,
            string screenshotError)
        {
            if (!string.IsNullOrWhiteSpace(firstLayoutBlocker) &&
                !string.Equals(firstLayoutBlocker, "(none captured)", StringComparison.Ordinal))
            {
                return "Paint blocked by layout: " + firstLayoutBlocker;
            }

            if (renderContext?.PaintTreeRoots == null || paintNodeCount == 0)
            {
                return "Diagnostic render produced no paint tree nodes.";
            }

            if (!screenshotCaptured)
            {
                return string.IsNullOrWhiteSpace(screenshotError)
                    ? "Diagnostic raster did not produce a screenshot."
                    : screenshotError;
            }

            return "(none captured)";
        }

        private static List<(Node Node, int Depth, string Path)> CollectDomTraversal(Node root)
        {
            var result = new List<(Node Node, int Depth, string Path)>();
            if (root == null)
            {
                return result;
            }

            var stack = new Stack<(Node Node, int Depth, string Path)>();
            stack.Push((root, 0, "/" + FormatNodePathSegment(root, 1)));
            while (stack.Count > 0)
            {
                var item = stack.Pop();
                result.Add(item);

                var children = new List<Node>();
                for (var child = item.Node.FirstChild; child != null; child = child.NextSibling)
                {
                    children.Add(child);
                }

                for (var i = children.Count - 1; i >= 0; i--)
                {
                    var child = children[i];
                    stack.Push((child, item.Depth + 1, item.Path + "/" + FormatNodePathSegment(child, i + 1)));
                }
            }

            return result;
        }

        private static int CountPaintNodes(IReadOnlyList<PaintNodeBase> roots)
        {
            if (roots == null || roots.Count == 0)
            {
                return 0;
            }

            var count = 0;
            var stack = new Stack<PaintNodeBase>();
            for (var i = roots.Count - 1; i >= 0; i--)
            {
                if (roots[i] != null)
                {
                    stack.Push(roots[i]);
                }
            }

            while (stack.Count > 0)
            {
                var node = stack.Pop();
                count++;
                var children = node.Children;
                if (children == null)
                {
                    continue;
                }

                for (var i = children.Count - 1; i >= 0; i--)
                {
                    if (children[i] != null)
                    {
                        stack.Push(children[i]);
                    }
                }
            }

            return count;
        }

        private static bool IsZeroAreaBox(FenBrowser.FenEngine.Layout.BoxModel box)
        {
            if (box == null)
            {
                return true;
            }

            var rect = box.BorderBox;
            return rect.Width <= 0 || rect.Height <= 0;
        }

        private static string FormatStyleProperties(CssComputed style)
        {
            if (style == null)
            {
                return "style=(null)";
            }

            return string.Join(" ", new[]
            {
                "display=" + FormatCssValue(style.Display),
                "position=" + FormatCssValue(style.Position),
                "visibility=" + FormatCssValue(style.Visibility),
                "overflow=" + FormatCssValue(style.Overflow),
                "width=" + FormatCssNumber(style.Width, style.WidthPercent, style.WidthExpression),
                "height=" + FormatCssNumber(style.Height, style.HeightPercent, style.HeightExpression),
                "margin=" + (style.Margin.ToString() ?? string.Empty),
                "padding=" + (style.Padding.ToString() ?? string.Empty),
                "color=" + FormatColor(style.ForegroundColor),
                "background=" + FormatColor(style.BackgroundColor),
                "fontSize=" + FormatCssValue(style.FontSize?.ToString()),
                "lineHeight=" + FormatCssValue(style.LineHeight?.ToString())
            });
        }

        private static string FormatCssNumber(double? absolute, double? percent, string expression)
        {
            if (!string.IsNullOrWhiteSpace(expression))
            {
                return expression;
            }

            if (absolute.HasValue)
            {
                return absolute.Value.ToString("0.###", CultureInfo.InvariantCulture) + "px";
            }

            if (percent.HasValue)
            {
                return percent.Value.ToString("0.###", CultureInfo.InvariantCulture) + "%";
            }

            return "(auto)";
        }

        private static string FormatCssValue(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? "(unset)" : value.Trim();
        }

        private static string FormatColor(SKColor? color)
        {
            if (!color.HasValue)
            {
                return "(none)";
            }

            var value = color.Value;
            return value.Alpha == 255
                ? $"#{value.Red:X2}{value.Green:X2}{value.Blue:X2}"
                : $"#{value.Alpha:X2}{value.Red:X2}{value.Green:X2}{value.Blue:X2}";
        }

        private static string FormatFloatArray(IReadOnlyList<float> values)
        {
            if (values == null || values.Count == 0)
            {
                return "(none)";
            }

            return string.Join(",", values.Select(value => value.ToString("0.###", CultureInfo.InvariantCulture)));
        }

        private static string FormatRect(SKRect rect)
        {
            return string.Create(
                CultureInfo.InvariantCulture,
                $"[{rect.Left:0.###},{rect.Top:0.###},{rect.Right:0.###},{rect.Bottom:0.###} {rect.Width:0.###}x{rect.Height:0.###}]");
        }

        private static string FormatPoint(SKPoint point)
        {
            return string.Create(CultureInfo.InvariantCulture, $"[{point.X:0.###},{point.Y:0.###}]");
        }

        private static string FormatNodePathSegment(Node node, int index)
        {
            if (node is Element element)
            {
                var tag = string.IsNullOrWhiteSpace(element.LocalName)
                    ? element.TagName
                    : element.LocalName;
                return $"{(tag ?? "element").ToLowerInvariant()}[{index}]";
            }

            return $"{(node?.NodeName ?? "node").ToLowerInvariant()}[{index}]";
        }

        private static string FormatNodeLabel(Node node)
        {
            if (node == null)
            {
                return "(null)";
            }

            if (node is Element element)
            {
                var tag = string.IsNullOrWhiteSpace(element.LocalName)
                    ? element.TagName
                    : element.LocalName;
                var sb = new StringBuilder((tag ?? "element").ToLowerInvariant());
                var id = element.GetAttribute("id");
                if (!string.IsNullOrWhiteSpace(id))
                {
                    sb.Append("#");
                    sb.Append(id.Trim());
                }

                var classes = element.GetAttribute("class");
                if (!string.IsNullOrWhiteSpace(classes))
                {
                    sb.Append(".");
                    sb.Append(Regex.Replace(classes.Trim(), @"\s+", "."));
                }

                return sb.ToString();
            }

            if (node.NodeType == NodeType.Text)
            {
                return "#text \"" + TrimText(node.TextContent, 60) + "\"";
            }

            return node.NodeName ?? node.GetType().Name;
        }

        private static string TrimText(string text, int maxLength)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return string.Empty;
            }

            var singleLine = Regex.Replace(text.Trim(), @"\s+", " ");
            return singleLine.Length <= maxLength
                ? singleLine
                : singleLine.Substring(0, maxLength) + "...";
        }

        private static object BuildNetworkSummary(DebugSiteReport report)
        {
            var requests = report.NetworkRequests ?? new List<DebugSiteNetworkRecord>();
            var failedCount = requests.Count(request =>
                request.Failed ||
                (request.StatusCode.HasValue && request.StatusCode.Value >= 400));

            return new
            {
                status = "captured",
                requestCount = requests.Count,
                failedRequestCount = failedCount,
                navigationFailureCount = report.NavigationFailures?.Count ?? 0,
                navigationFailures = report.NavigationFailures ?? new List<string>(),
                requests
            };
        }

        private static object BuildInactiveIpcArtifact()
        {
            return new
            {
                schemaVersion = 1,
                status = "NOT_STARTED",
                state = "inactive",
                configuration = "not-configured",
                processMode = "in-process",
                eventState = "no-events",
                eventCount = 0,
                reason = "The current debug-site host runs in process; brokered IPC capture is not configured.",
                events = Array.Empty<object>()
            };
        }

        private static object BuildInactiveSandboxDenialsArtifact()
        {
            return new
            {
                schemaVersion = 1,
                status = "NOT_STARTED",
                state = "inactive",
                configuration = "not-configured",
                processMode = "in-process",
                denialState = "no-denials",
                denialCount = 0,
                reason = "The current debug-site host has no renderer sandbox configured.",
                denials = Array.Empty<object>()
            };
        }

        private static object BuildPerformanceArtifact(DebugSiteReport report)
        {
            var styleLayout = report.StyleLayout ?? new DebugSiteStyleLayoutSummary();
            var eventLoop = report.EventLoop ?? new BrowserEventLoopSnapshot();
            return new
            {
                schemaVersion = 1,
                status = "IMPLEMENTED",
                state = "partial",
                processMode = "in-process",
                diagnosticMode = true,
                sampleCount = 1,
                reason = "Single debug-site diagnostic sample; this artifact is not a repeatable performance benchmark.",
                navigation = new
                {
                    elapsedMs = report.ElapsedMs,
                    completed = report.NavigateReturned,
                    lifecyclePhase = report.Lifecycle?.Phase ?? string.Empty
                },
                rendering = new
                {
                    layoutRan = styleLayout.LayoutRan,
                    paintRan = styleLayout.PaintRan,
                    rasterRan = styleLayout.RasterRan,
                    layoutMs = styleLayout.LayoutDurationMs,
                    paintMs = styleLayout.PaintDurationMs,
                    rasterMs = styleLayout.RasterDurationMs,
                    totalMs = styleLayout.TotalRenderDurationMs,
                    watchdogTriggered = styleLayout.WatchdogTriggered
                },
                eventLoop = new
                {
                    callbackFailureCount = eventLoop.CallbackFailures,
                    timerCallbackCount = eventLoop.TimersExecuted,
                    microtaskCheckpointCount = eventLoop.MicrotaskCheckpoints
                },
                unavailableMetrics = new[]
                {
                    "start-to-response",
                    "start-to-commit",
                    "js-compile",
                    "js-execute",
                    "managed-allocations",
                    "gc-pause",
                    "fenjs-live-objects",
                    "input-latency",
                    "frame-interval-distribution"
                }
            };
        }

        private static List<DebugSiteArtifactManifestEntry> BuildArtifactManifest(
            string bundleDir,
            bool includeInteraction,
            IReadOnlyList<DebugSiteArtifactExportFailure> exportFailures = null)
        {
            var expected = new List<string>
            {
                "summary.md",
                "summary.json",
                "trace.jsonl",
                "logs.ndjson",
                "console.log",
                "network.json",
                "lifecycle.json",
                "lifecycle_timeline.json",
                "script_loading.json",
                "event_loop.json",
                "first_blocker.json",
                "ipc.json",
                "sandbox_denials.json",
                "performance.json",
                "style_layout.json",
                "exceptions.json",
                "missing_apis.json",
                "probes.json",
                "style_dump.txt",
                "layout_dump.txt",
                "paint_dump.txt",
                "display_list.txt",
                "dom_dump.txt",
                "raw_source.html",
                "rendered_text.txt",
                "screenshot.png"
            };

            if (includeInteraction)
            {
                expected.Add("interaction.json");
                expected.Add("interaction_before.png");
                expected.Add("interaction_after.png");
            }

            return expected
                .Select(name =>
                {
                    var path = Path.Combine(bundleDir, name);
                    var info = File.Exists(path) ? new FileInfo(path) : null;
                    var exportFailure = exportFailures?.LastOrDefault(failure =>
                        string.Equals(failure.ArtifactName, name, StringComparison.Ordinal));
                    return new DebugSiteArtifactManifestEntry(
                        name,
                        info != null,
                        info?.Length ?? 0,
                        info?.LastWriteTimeUtc.ToString("O", CultureInfo.InvariantCulture),
                        exportFailure == null
                            ? null
                            : exportFailure.ErrorType + ": " + exportFailure.Message);
                })
                .ToList();
        }

        private static void TryWriteDebugSiteArtifact(
            string bundleDir,
            string artifactName,
            Action<string> write,
            List<DebugSiteArtifactExportFailure> exportFailures)
        {
            try
            {
                write(Path.Combine(bundleDir, artifactName));
                exportFailures.RemoveAll(failure =>
                    string.Equals(failure.ArtifactName, artifactName, StringComparison.Ordinal));
            }
            catch (Exception ex)
            {
                exportFailures.RemoveAll(failure =>
                    string.Equals(failure.ArtifactName, artifactName, StringComparison.Ordinal));
                exportFailures.Add(new DebugSiteArtifactExportFailure(
                    artifactName,
                    ex.GetType().Name,
                    (ex.Message ?? string.Empty).Length <= 512
                        ? ex.Message ?? string.Empty
                        : ex.Message.Substring(0, 512) + "..."));
                try
                {
                    Console.Error.WriteLine(
                        $"[debug-site] Failed to export {artifactName}: {ex.GetType().Name}: {ex.Message}");
                }
                catch
                {
                    // Export diagnostics must not replace the original page result.
                }
            }
        }

        private static List<DebugSiteExceptionRecord> ExtractExceptionRecords(IEnumerable<string> consoleMessages, string navigateException)
        {
            var records = new List<DebugSiteExceptionRecord>();
            if (!string.IsNullOrWhiteSpace(navigateException))
            {
                records.Add(new DebugSiteExceptionRecord("NavigateAsync", "Exception", navigateException));
            }

            foreach (var message in consoleMessages ?? Enumerable.Empty<string>())
            {
                if (IsLikelyErrorMessage(message))
                {
                    records.Add(new DebugSiteExceptionRecord("Console", "Error", message));
                }
            }

            return records;
        }

        private static DebugSiteExceptionSummary BuildExceptionSummary(DebugSiteReport report)
        {
            var otherExceptions = ExtractExceptionRecords(report?.ConsoleMessages, report?.NavigateException);
            return DebugSiteExceptionSummaryBuilder.Build(report?.EventLoop, otherExceptions);
        }

        private static FirstBlockerResult BuildFirstBlocker(
            DebugSiteReport report,
            IReadOnlyList<string> missingRequiredArtifacts = null)
        {
            var lifecycle = report?.Lifecycle ?? new DebugSiteLifecycleSummary();
            var eventLoop = report?.EventLoop ?? new BrowserEventLoopSnapshot();
            var scripts = report?.ScriptLoading ?? new BrowserScriptLoadingSnapshot();
            var style = report?.StyleLayout ?? new DebugSiteStyleLayoutSummary();
            var phases = new HashSet<string>(lifecycle.TransitionPhases ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
            var evidence = new List<FirstBlockerEvidence>();
            var contradictions = new List<string>();

            if (!string.IsNullOrWhiteSpace(report?.NavigateException) || lifecycle.IsFailureTerminalPhase)
            {
                evidence.Add(new FirstBlockerEvidence(
                    "navigation-terminal-failure",
                    "Navigation",
                    "Core/Network",
                    "ResponseReceived",
                    1,
                    lifecycle.TerminalTransitionUtc ?? string.Empty,
                    true,
                    report?.NavigateException ?? lifecycle.Detail ?? "navigation failed"));
            }

            var firstScriptFailure = scripts.Scripts?.FirstOrDefault(static script =>
                !string.IsNullOrWhiteSpace(script.Failure) || string.Equals(script.Status, "failed", StringComparison.OrdinalIgnoreCase));
            if (scripts.ExecutionFailed > 0 && !eventLoop.DomContentLoadedFired)
            {
                evidence.Add(new FirstBlockerEvidence(
                    firstScriptFailure?.ScriptId ?? "script-execution-failure",
                    "ECMAScriptSemantics",
                    "FenEngine/Scripting",
                    "RequiredScriptsExecuted",
                    firstScriptFailure?.Ordinal ?? 8,
                    firstScriptFailure?.CompletedUtc ?? string.Empty,
                    true,
                    firstScriptFailure?.Failure ?? "script execution failed before DOMContentLoaded"));
            }

            if (style.DomRootPresent && style.LayoutRan && style.BoxCount == 0)
            {
                evidence.Add(new FirstBlockerEvidence(
                    "layout-root-no-boxes", "Layout", "FenEngine/Layout", "MainUiLaidOut", 12, "", true,
                    "The document root exists but layout produced no boxes."));
            }

            if (lifecycle.IsSuccessfulTerminalPhase &&
                !string.Equals(lifecycle.DocumentReadyState, eventLoop.DocumentReadyState, StringComparison.OrdinalIgnoreCase))
            {
                contradictions.Add(
                    $"Lifecycle readyState '{lifecycle.DocumentReadyState}' disagrees with event-loop readyState '{eventLoop.DocumentReadyState}'.");
            }
            if (lifecycle.IsSuccessfulTerminalPhase && (!eventLoop.DomContentLoadedFired || !eventLoop.LoadFired))
            {
                contradictions.Add("Successful terminal lifecycle disagrees with DOMContentLoaded/load event-loop state.");
            }

            var nonFatal = (eventLoop.CallbackFailureRecords ?? new List<BrowserCallbackFailureRecord>())
                .Where(static failure => !failure.BlockedProgress)
                .OrderBy(static failure => failure.Sequence)
                .Select(static failure => $"callback:{failure.CallbackId} {failure.ExceptionType}: {failure.ExceptionMessage}")
                .ToList();

            if (report?.Interaction?.Attempted == true &&
                !string.Equals(report.Interaction.Status, "passed", StringComparison.Ordinal))
            {
                var (milestone, sequence) = !report.Interaction.InputTargetFound
                    ? ("InputTargetFound", 15L)
                    : !report.Interaction.FocusAcquired
                        ? ("FocusAcquired", 16L)
                        : !report.Interaction.TextAccepted
                            ? ("TextAccepted", 17L)
                            : !report.Interaction.SubmitTargetFound || !report.Interaction.SubmitAttempted
                                ? ("SubmitDefaultActionCompleted", 18L)
                                : ("ResultRequestNavigationCompleted", 19L);
                evidence.Add(new FirstBlockerEvidence(
                    "interaction-acceptance-failure",
                    "InputEventDefaultAction",
                    "Host/Input",
                    milestone,
                    sequence,
                    report.Interaction.CompletedUtc,
                    true,
                    string.IsNullOrWhiteSpace(report.Interaction.Error)
                        ? "The requested browser interaction did not complete."
                        : report.Interaction.Error));
            }

            var interactive = phases.Contains("Interactive") || phases.Contains("Complete");
            var successful = lifecycle.IsSuccessfulTerminalPhase;
            var input = new FirstBlockerInput(
                NavigationRequested: phases.Contains("Requested") || !string.IsNullOrWhiteSpace(report?.Url),
                ResponseReceived: phases.Contains("ResponseReceived"),
                DocumentCreated: phases.Contains("Committing") || interactive,
                ParsingStarted: phases.Contains("Committing") || interactive,
                RequiredStylesDiscovered: interactive,
                RequiredScriptsDiscovered: string.IsNullOrWhiteSpace(scripts.InfrastructureError),
                RequiredResourcesFetched: successful,
                RequiredScriptsExecuted: scripts.ExecutionFailed == 0 || eventLoop.DomContentLoadedFired,
                ParsingCompleted: interactive,
                DomContentLoadedFired: eventLoop.DomContentLoadedFired,
                LoadFired: eventLoop.LoadFired,
                MainUiLaidOut: style.LayoutRan && style.BoxCount > 0,
                MainUiPainted: style.PaintRan && style.PaintNodeCount > 0,
                FrameSubmitted: style.RasterRan && style.ScreenshotCaptured)
            {
                InputTargetFound = report?.Interaction?.Attempted == true ? report.Interaction.InputTargetFound : null,
                FocusAcquired = report?.Interaction?.Attempted == true ? report.Interaction.FocusAcquired : null,
                TextAccepted = report?.Interaction?.Attempted == true ? report.Interaction.TextAccepted : null,
                SubmitCompleted = report?.Interaction?.Attempted == true
                    ? report.Interaction.SubmitAttempted && report.Interaction.SubmissionOutcomeObserved && report.Interaction.SubmissionOutcomeSettled
                    : null,
                ResultNavigationCompleted = report?.Interaction?.Attempted == true
                    ? report.Interaction.SubmissionOutcomeObserved && report.Interaction.SubmissionOutcomeSettled
                    : null,
                Evidence = evidence,
                NonFatalFailures = nonFatal,
                ContradictoryArtifactWarnings = contradictions,
                MissingRequiredArtifacts = missingRequiredArtifacts ?? Array.Empty<string>()
            };

            return FirstBlockerClassifier.Classify(input);
        }

        private static List<MissingApiRecord> ExtractMissingApiRecords(
            IEnumerable<string> consoleMessages,
            IEnumerable<FeatureInfo> unsupportedJsFeatures = null)
        {
            var records = new List<MissingApiRecord>();
            foreach (var feature in unsupportedJsFeatures ?? Enumerable.Empty<FeatureInfo>())
            {
                if (string.IsNullOrWhiteSpace(feature?.Name))
                {
                    continue;
                }

                var (objectName, propertyName) = SplitApiName(feature.Name);
                AddMissingApiRecord(
                    records,
                    feature.Name,
                    objectName,
                    propertyName,
                    "EngineCapabilities",
                    feature.Reason,
                    Math.Max(1, feature.EncounterCount));
            }

            foreach (var message in consoleMessages ?? Enumerable.Empty<string>())
            {
                var api = TryExtractMissingApiName(message);
                if (!string.IsNullOrWhiteSpace(api))
                {
                    var (objectName, propertyName) = SplitApiName(api);
                    AddMissingApiRecord(records, api, objectName, propertyName, "Console", message, 1);
                }
            }

            return records;
        }

        private static BrowserMissingApiSnapshot BuildMissingApiSnapshot(DebugSiteReport report)
        {
            if (report?.MissingApiSnapshot != null)
            {
                return report.MissingApiSnapshot;
            }

            var compact = report?.MissingApis ?? ExtractMissingApiRecords(report?.ConsoleMessages);
            var retained = compact
                .Take(MissingApiTracker.SnapshotRecordLimit)
                .Select(record => new BrowserMissingApiRecordSnapshot
                {
                    ApiName = record.Api,
                    ObjectOrPrototype = record.ObjectName,
                    PropertyName = record.PropertyName,
                    Reason = record.Evidence,
                    Classification = record.Classification,
                    OperationKind = record.OperationKind,
                    ClassificationReason = record.ClassificationReason,
                    StandardPriorityEligible = record.StandardPriorityEligible,
                    ReceiverType = record.ObjectName,
                    EncounterCount = record.EncounterCount
                })
                .ToList();
            return new BrowserMissingApiSnapshot
            {
                GeneratedAtUtc = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                SiteKey = MakeSafeSiteId(report?.FinalUrl, report?.Url),
                SiteUrl = report?.FinalUrl ?? report?.Url ?? string.Empty,
                TotalRecordCount = compact.Count,
                RetainedRecordCount = retained.Count,
                Truncated = compact.Count > retained.Count,
                Records = retained
            };
        }

        private static void AddMissingApiRecord(
            List<MissingApiRecord> records,
            string api,
            string objectName,
            string propertyName,
            string source,
            string evidence,
            int encounterCount)
        {
            if (string.IsNullOrWhiteSpace(api) ||
                records.Any(existing => string.Equals(existing.Api, api, StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            var classification = MissingApiClassifier.Classify(new MissingApiClassificationInput(
                objectName,
                propertyName,
                MissingApiOperationKind.Read));
            records.Add(new MissingApiRecord(
                api,
                objectName ?? string.Empty,
                propertyName ?? string.Empty,
                source ?? string.Empty,
                evidence ?? string.Empty,
                Math.Max(1, encounterCount),
                MissingApiClassifier.ToToken(classification.Classification),
                MissingApiClassifier.ToToken(classification.OperationKind),
                classification.Reason,
                classification.StandardPriorityEligible));
        }

        private static (string ObjectName, string PropertyName) SplitApiName(string api)
        {
            if (string.IsNullOrWhiteSpace(api))
            {
                return (string.Empty, string.Empty);
            }

            var separator = api.LastIndexOf('.');
            if (separator <= 0 || separator >= api.Length - 1)
            {
                return (string.Empty, api);
            }

            return (api.Substring(0, separator), api.Substring(separator + 1));
        }

        private static bool IsLikelyErrorMessage(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
            {
                return false;
            }

            return message.IndexOf("error", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   message.IndexOf("exception", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   message.IndexOf("TypeError", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   message.IndexOf("ReferenceError", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   message.IndexOf("RangeError", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string TryExtractMissingApiName(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
            {
                return null;
            }

            var referenceMatch = Regex.Match(message, @"ReferenceError:\s*(?<api>[A-Za-z_$][\w$\.]*)\s+is\s+not\s+defined", RegexOptions.IgnoreCase);
            if (referenceMatch.Success)
            {
                return referenceMatch.Groups["api"].Value;
            }

            var typeMatch = Regex.Match(message, @"(?<api>[A-Za-z_$][\w$\.]*)\s+is\s+not\s+a\s+function", RegexOptions.IgnoreCase);
            if (typeMatch.Success)
            {
                return typeMatch.Groups["api"].Value;
            }

            return null;
        }

        private static string MakeSafeSiteId(string finalUrl, string originalUrl)
        {
            var candidate = !string.IsNullOrWhiteSpace(finalUrl) ? finalUrl : originalUrl;
            if (Uri.TryCreate(candidate, UriKind.Absolute, out var uri) && !string.IsNullOrWhiteSpace(uri.Host))
            {
                candidate = uri.Host;
            }

            if (string.IsNullOrWhiteSpace(candidate))
            {
                candidate = "unknown";
            }

            return Regex.Replace(candidate.ToLowerInvariant(), @"[^a-z0-9._-]+", "_").Trim('_');
        }

        private static void TryCopyFile(string sourcePath, string destinationPath)
        {
            try
            {
                if (File.Exists(sourcePath))
                {
                    File.Copy(sourcePath, destinationPath, overwrite: true);
                }
            }
            catch
            {
                // Best-effort diagnostic copy.
            }
        }

        private static void TryDeleteFile(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
                // Best-effort diagnostic cleanup.
            }
        }

        private static void TryCopyLogArtifact(string sourceName, string destinationPath)
        {
            try
            {
                var sourcePath = Path.Combine(DiagnosticPaths.GetLogsDirectory(), sourceName);
                if (File.Exists(sourcePath))
                {
                    File.Copy(sourcePath, destinationPath, overwrite: true);
                }
            }
            catch
            {
                // Best-effort diagnostic copy.
            }
        }

        private static void TryCopyLatestLogArtifact(string searchPattern, string destinationPath)
        {
            try
            {
                var logsDir = DiagnosticPaths.GetLogsDirectory();
                if (!Directory.Exists(logsDir))
                {
                    return;
                }

                var latest = new DirectoryInfo(logsDir)
                    .EnumerateFiles(searchPattern, SearchOption.TopDirectoryOnly)
                    .OrderByDescending(file => file.LastWriteTimeUtc)
                    .FirstOrDefault();

                if (latest != null)
                {
                    File.Copy(latest.FullName, destinationPath, overwrite: true);
                }
            }
            catch
            {
                // Best-effort diagnostic copy.
            }
        }

        private static void TryCopyLatestStructuredLogArtifact(string destinationPath)
        {
            try
            {
                var logsDir = DiagnosticPaths.GetLogsDirectory();
                if (!Directory.Exists(logsDir))
                {
                    return;
                }

                var latest = new DirectoryInfo(logsDir)
                    .EnumerateFiles("fenbrowser_*.jsonl", SearchOption.TopDirectoryOnly)
                    .Where(file =>
                        !file.Name.Contains("_trace", StringComparison.OrdinalIgnoreCase) &&
                        !file.Name.StartsWith("fenbrowser_trace_", StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(file => file.LastWriteTimeUtc)
                    .FirstOrDefault();

                if (latest != null)
                {
                    File.Copy(latest.FullName, destinationPath, overwrite: true);
                }
            }
            catch
            {
                // Best-effort diagnostic copy.
            }
        }

        private sealed class DebugSiteReport
        {
            public string Url { get; init; }
            public int SettleMs { get; init; }
            public bool NavigateReturned { get; init; }
            public string NavigateException { get; init; }
            public string FinalUrl { get; init; }
            public long ElapsedMs { get; init; }
            public int DomNodeCount { get; init; }
            public int RawHtmlLength { get; init; }
            public int RenderedTextLength { get; init; }
            public int ComputedStylesCount { get; init; }
            public List<string> NavigationFailures { get; init; } = new();
            public List<string> ConsoleMessages { get; init; } = new();
            public DebugSiteLifecycleSummary Lifecycle { get; init; } = new();
            public BrowserScriptLoadingSnapshot ScriptLoading { get; init; } = new();
            public BrowserEventLoopSnapshot EventLoop { get; init; } = new();
            public DebugSiteStyleLayoutSummary StyleLayout { get; init; } = new();
            public List<MissingApiRecord> MissingApis { get; init; } = new();
            public BrowserMissingApiSnapshot MissingApiSnapshot { get; set; }
            public List<DebugSiteNetworkRecord> NetworkRequests { get; init; } = new();
            public Dictionary<string, string> Probes { get; init; } = new(StringComparer.Ordinal);
            public string RenderedTextSample { get; init; }
            [JsonIgnore]
            public string StyleDump { get; init; }
            [JsonIgnore]
            public string LayoutDump { get; init; }
            [JsonIgnore]
            public string PaintDump { get; init; }
            [JsonIgnore]
            public string DisplayListDump { get; init; }
            public bool ScreenshotCaptured { get; init; }
            public string ScreenshotPath { get; init; }
            public int ScreenshotWidth { get; init; }
            public int ScreenshotHeight { get; init; }
            public string ScreenshotError { get; init; }
            public DebugSiteInteractionResult Interaction { get; init; }
            public bool LoggerDrainSucceeded { get; set; }
            public int LoggerDrainTimeoutMs { get; set; }
        }

        private sealed record MissingApiRecord(
            string Api,
            string ObjectName,
            string PropertyName,
            string Source,
            string Evidence,
            int EncounterCount,
            string Classification,
            string OperationKind,
            string ClassificationReason,
            bool StandardPriorityEligible);

        private sealed record DebugSiteArtifactManifestEntry(
            string name,
            bool exists,
            long sizeBytes,
            string lastWriteUtc,
            string exportError);

        private sealed record DebugSiteArtifactExportFailure(
            string ArtifactName,
            string ErrorType,
            string Message);

        internal sealed record DebugSiteScreenshotResult(
            bool Captured,
            string Path,
            int Width,
            int Height,
            string Error,
            RenderContext RenderContext,
            RenderFrameTelemetry Telemetry);

        private sealed class DebugSiteStyleLayoutSummary
        {
            public string Status { get; init; } = "not-captured";
            public bool DomRootPresent { get; init; }
            public int DomNodeCount { get; init; }
            public int ElementCount { get; init; }
            public int ComputedStyleCount { get; init; }
            public int StyledNodeCount { get; init; }
            public int StyledElementCount { get; init; }
            public int UnstyledElementCount { get; init; }
            public bool RenderContextAvailable { get; init; }
            public int BoxCount { get; init; }
            public int ZeroAreaBoxCount { get; init; }
            public int PaintRootCount { get; init; }
            public int PaintNodeCount { get; init; }
            public int DisplayListNodeCount { get; init; }
            public string DisplayListSource { get; init; } = string.Empty;
            public float ViewportWidth { get; init; }
            public float ViewportHeight { get; init; }
            public bool LayoutRan { get; init; }
            public bool PaintRan { get; init; }
            public bool RasterRan { get; init; }
            public bool ScreenshotCaptured { get; init; }
            public string RasterMode { get; init; } = "none";
            public double LayoutDurationMs { get; init; }
            public double PaintDurationMs { get; init; }
            public double RasterDurationMs { get; init; }
            public double TotalRenderDurationMs { get; init; }
            public bool WatchdogTriggered { get; init; }
            public string WatchdogReason { get; init; } = string.Empty;
            public string FirstLayoutBlocker { get; init; } = "(not captured)";
            public string FirstPaintBlocker { get; init; } = "(not captured)";
        }

        private sealed class DebugSiteLifecycleSummary
        {
            public long NavigationId { get; init; }
            public string Phase { get; init; }
            public string RequestedUrl { get; init; }
            public string EffectiveUrl { get; init; }
            public string ResponseStatus { get; init; }
            public string Detail { get; init; }
            public bool IsUserInput { get; init; }
            public bool IsRedirect { get; init; }
            public int RedirectCount { get; init; }
            public string CommitSource { get; init; }
            public string LastTransitionUtc { get; init; }
            public string DocumentReadyState { get; init; }
            public bool IsTerminalPhase { get; init; }
            public bool IsSuccessfulTerminalPhase { get; init; }
            public bool IsFailureTerminalPhase { get; init; }
            public int TransitionCount { get; init; }
            public List<string> TransitionPhases { get; init; } = new();
            public string FirstTransitionUtc { get; init; }
            public string TerminalTransitionUtc { get; init; }
            public bool DomContentLoadedFired { get; init; }
            public string DomContentLoadedUtc { get; init; }
            public bool LoadFired { get; init; }
            public string LoadUtc { get; init; }
            public List<DebugSiteLifecycleTransition> Transitions { get; init; } = new();
        }

        private sealed class DebugSiteLifecycleTransition
        {
            public int Sequence { get; set; }
            public long NavigationId { get; set; }
            public string PreviousPhase { get; set; } = string.Empty;
            public string Phase { get; set; } = string.Empty;
            public string RequestedUrl { get; set; } = string.Empty;
            public string EffectiveUrl { get; set; } = string.Empty;
            public string ResponseStatus { get; set; } = string.Empty;
            public string Detail { get; set; } = string.Empty;
            public bool IsUserInput { get; set; }
            public bool IsRedirect { get; set; }
            public int RedirectCount { get; set; }
            public string CommitSource { get; set; } = string.Empty;
            public string TimestampUtc { get; set; } = string.Empty;
            public double MillisecondsSinceFirstTransition { get; set; }

            public static DebugSiteLifecycleTransition From(NavigationLifecycleTransition transition)
            {
                if (transition == null)
                {
                    return new DebugSiteLifecycleTransition();
                }

                return new DebugSiteLifecycleTransition
                {
                    NavigationId = transition.NavigationId,
                    PreviousPhase = transition.PreviousPhase.ToString(),
                    Phase = transition.Phase.ToString(),
                    RequestedUrl = transition.RequestedUrl ?? string.Empty,
                    EffectiveUrl = transition.EffectiveUrl ?? string.Empty,
                    ResponseStatus = transition.ResponseStatus ?? string.Empty,
                    Detail = transition.Detail ?? string.Empty,
                    IsUserInput = transition.IsUserInput,
                    IsRedirect = transition.IsRedirect,
                    RedirectCount = transition.RedirectCount,
                    CommitSource = transition.CommitSource ?? string.Empty,
                    TimestampUtc = transition.TimestampUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)
                };
            }

            public DebugSiteLifecycleTransition Clone()
            {
                return new DebugSiteLifecycleTransition
                {
                    Sequence = Sequence,
                    NavigationId = NavigationId,
                    PreviousPhase = PreviousPhase,
                    Phase = Phase,
                    RequestedUrl = RequestedUrl,
                    EffectiveUrl = EffectiveUrl,
                    ResponseStatus = ResponseStatus,
                    Detail = Detail,
                    IsUserInput = IsUserInput,
                    IsRedirect = IsRedirect,
                    RedirectCount = RedirectCount,
                    CommitSource = CommitSource,
                    TimestampUtc = TimestampUtc,
                    MillisecondsSinceFirstTransition = MillisecondsSinceFirstTransition
                };
            }
        }

        private sealed class DebugSiteNetworkCapture
        {
            private readonly object _lock = new();
            private readonly Dictionary<string, DebugSiteNetworkRecord> _byId = new(StringComparer.Ordinal);
            private readonly List<DebugSiteNetworkRecord> _records = new();

            public void Attach(ResourceManager resourceManager)
            {
                if (resourceManager == null)
                {
                    return;
                }

                resourceManager.NetworkRequestStarting += OnRequestStarting;
                resourceManager.NetworkRequestCompleted += OnRequestCompleted;
                resourceManager.NetworkRequestFailed += OnRequestFailed;
            }

            public List<DebugSiteNetworkRecord> Snapshot()
            {
                lock (_lock)
                {
                    return _records
                        .Select(static record => record.Clone())
                        .OrderBy(static record => record.Sequence)
                        .ToList();
                }
            }

            private void OnRequestStarting(string id, HttpRequestMessage request)
            {
                if (string.IsNullOrWhiteSpace(id) || request == null)
                {
                    return;
                }

                lock (_lock)
                {
                    var record = new DebugSiteNetworkRecord
                    {
                        Sequence = _records.Count + 1,
                        RequestId = id,
                        Method = request.Method?.Method ?? string.Empty,
                        Url = request.RequestUri?.AbsoluteUri ?? string.Empty,
                        StartedUtc = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                        RequestHeaders = CaptureHeaders(request.Headers)
                    };
                    if (request.Headers.UserAgent != null)
                    {
                        record.RequestHeaders["User-Agent"] = request.Headers.UserAgent.ToString();
                    }

                    if (request.Content?.Headers != null)
                    {
                        foreach (var header in CaptureHeaders(request.Content.Headers))
                        {
                            record.RequestHeaders[header.Key] = header.Value;
                        }
                    }

                    _byId[id] = record;
                    _records.Add(record);
                }
            }

            private void OnRequestCompleted(string id, HttpResponseMessage response)
            {
                if (string.IsNullOrWhiteSpace(id) || response == null)
                {
                    return;
                }

                lock (_lock)
                {
                    var record = GetOrCreateRecord(id, response.RequestMessage);
                    record.CompletedUtc = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
                    record.DurationMs = CalculateDurationMs(record.StartedUtc, record.CompletedUtc);
                    record.StatusCode = (int)response.StatusCode;
                    record.ReasonPhrase = response.ReasonPhrase ?? string.Empty;
                    record.Success = response.IsSuccessStatusCode;
                    record.ResponseHeaders = CaptureHeaders(response.Headers);
                    if (response.Content?.Headers != null)
                    {
                        foreach (var header in CaptureHeaders(response.Content.Headers))
                        {
                            record.ResponseHeaders[header.Key] = header.Value;
                        }

                        record.ContentLength = response.Content.Headers.ContentLength;
                        record.MimeType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
                    }
                }
            }

            private void OnRequestFailed(string id, Exception exception)
            {
                if (string.IsNullOrWhiteSpace(id))
                {
                    return;
                }

                lock (_lock)
                {
                    var record = GetOrCreateRecord(id, null);
                    record.CompletedUtc = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
                    record.DurationMs = CalculateDurationMs(record.StartedUtc, record.CompletedUtc);
                    record.Failed = true;
                    record.Success = false;
                    record.ErrorType = exception?.GetType().Name ?? string.Empty;
                    record.ErrorMessage = exception?.Message ?? string.Empty;
                }
            }

            private DebugSiteNetworkRecord GetOrCreateRecord(string id, HttpRequestMessage request)
            {
                if (_byId.TryGetValue(id, out var record))
                {
                    return record;
                }

                record = new DebugSiteNetworkRecord
                {
                    Sequence = _records.Count + 1,
                    RequestId = id,
                    Method = request?.Method?.Method ?? string.Empty,
                    Url = request?.RequestUri?.AbsoluteUri ?? string.Empty,
                    StartedUtc = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                    RequestHeaders = request != null
                        ? CaptureHeaders(request.Headers)
                        : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                };
                if (request?.Headers.UserAgent != null)
                {
                    record.RequestHeaders["User-Agent"] = request.Headers.UserAgent.ToString();
                }
                _byId[id] = record;
                _records.Add(record);
                return record;
            }

            private static Dictionary<string, string> CaptureHeaders(HttpHeaders headers)
            {
                return CaptureDebugSiteHeaders(headers);
            }

            private static long? CalculateDurationMs(string startedUtc, string completedUtc)
            {
                if (DateTimeOffset.TryParse(startedUtc, out var started) &&
                    DateTimeOffset.TryParse(completedUtc, out var completed))
                {
                    return Math.Max(0, (long)(completed - started).TotalMilliseconds);
                }

                return null;
            }
        }

        internal static Dictionary<string, string> CaptureDebugSiteHeaders(HttpHeaders headers)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (headers == null)
            {
                return result;
            }

            foreach (var header in headers)
            {
                result[header.Key] = IsSensitiveDiagnosticHeader(header.Key)
                    ? "[redacted]"
                    : string.Join(", ", header.Value);
            }

            return result;
        }

        private static bool IsSensitiveDiagnosticHeader(string name) =>
            string.Equals(name, "Cookie", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(name, "Set-Cookie", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(name, "Authorization", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(name, "Proxy-Authorization", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(name, "X-Api-Key", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(name, "Api-Key", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(name, "X-Auth-Token", StringComparison.OrdinalIgnoreCase);

        private sealed class DebugSiteNetworkRecord
        {
            public int Sequence { get; set; }
            public string RequestId { get; set; }
            public string Method { get; set; }
            public string Url { get; set; }
            public string StartedUtc { get; set; }
            public string CompletedUtc { get; set; }
            public long? DurationMs { get; set; }
            public Dictionary<string, string> RequestHeaders { get; set; } = new(StringComparer.OrdinalIgnoreCase);
            public int? StatusCode { get; set; }
            public string ReasonPhrase { get; set; }
            public bool Success { get; set; }
            public bool Failed { get; set; }
            public string MimeType { get; set; }
            public long? ContentLength { get; set; }
            public Dictionary<string, string> ResponseHeaders { get; set; } = new(StringComparer.OrdinalIgnoreCase);
            public string ErrorType { get; set; }
            public string ErrorMessage { get; set; }

            public DebugSiteNetworkRecord Clone()
            {
                return new DebugSiteNetworkRecord
                {
                    Sequence = Sequence,
                    RequestId = RequestId,
                    Method = Method,
                    Url = Url,
                    StartedUtc = StartedUtc,
                    CompletedUtc = CompletedUtc,
                    DurationMs = DurationMs,
                    RequestHeaders = new Dictionary<string, string>(RequestHeaders ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase),
                    StatusCode = StatusCode,
                    ReasonPhrase = ReasonPhrase,
                    Success = Success,
                    Failed = Failed,
                    MimeType = MimeType,
                    ContentLength = ContentLength,
                    ResponseHeaders = new Dictionary<string, string>(ResponseHeaders ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase),
                    ErrorType = ErrorType,
                    ErrorMessage = ErrorMessage
                };
            }
        }

        private static T SafeCall<T>(Func<T> fn)
        {
            try { return fn(); } catch { return default; }
        }

        private static int CountDomNodes(FenBrowser.Core.Dom.V2.Node root)
        {
            if (root == null) return 0;
            int count = 0;
            var stack = new Stack<FenBrowser.Core.Dom.V2.Node>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                var node = stack.Pop();
                count++;
                for (var child = node.FirstChild; child != null; child = child.NextSibling)
                {
                    stack.Push(child);
                }
            }
            return count;
        }

        private static async Task RunAcid2Async()
        {
            Console.WriteLine("[tooling] Acid2 runner removed with legacy engine.");
            await Task.CompletedTask;
            return;
            const int maxRunMs = 35000; // unreachable, kept for compilation
            CssEngineConfig.CurrentEngine = CssEngineType.Custom;
            var windowManager = WindowManager.Instance;
            windowManager.Initialize("about:blank", isHeadless: true);

            windowManager.OnLoad += () =>
            {
                Task.Run(async () =>
                {
                    try
                    {
                        ChromeManager.Instance.Initialize("about:blank");
                        var runner = new AcidTestRunner();
                        var runTask = runner.RunAcid2Async(async url =>
                        {
                            var screenshotTask = await WindowManager.Instance.RunOnMainThread(async () =>
                            {
                                if (TabManager.Instance.ActiveTab == null)
                                {
                                    TabManager.Instance.CreateTab(url);
                                }
                                else
                                {
                                    _ = TabManager.Instance.ActiveTab.NavigateAsync(url);
                                }

                                await Task.Delay(2200).ConfigureAwait(false);
                                return WindowManager.Instance.CaptureScreenshot();
                            }).ConfigureAwait(false);

                            return await screenshotTask.ConfigureAwait(false);
                        });

                        var completed = await Task.WhenAny(runTask, Task.Delay(maxRunMs)).ConfigureAwait(false);
                        if (completed != runTask)
                        {
                            Console.WriteLine($"Result: FAIL");
                            Console.WriteLine($"Acid2 run timed out after {maxRunMs}ms.");
                            return;
                        }

                        var result = await runTask.ConfigureAwait(false);

                        Console.WriteLine($"Result: {(result.Passed ? "PASS" : "FAIL")}");
                        Console.WriteLine(result.Message);
                    }
                    finally
                    {
                        try { Console.Out.Flush(); } catch { }
                        Environment.Exit(0);
                    }
                });
            };

            windowManager.Run();
        }

        private static async Task RunAcid2CompareAsync()
        {
            Console.WriteLine("[tooling] Acid compare runner removed with legacy engine.");
            await Task.CompletedTask;
            return;
            const string acid2Url = "http://acid2.acidtests.org/#top"; // unreachable, kept for compilation
            const string acid2ReferenceUrl = "http://acid2.acidtests.org/reference.html";
            const int maxCaptureMs = 45000;

            CssEngineConfig.CurrentEngine = CssEngineType.Custom;
            var windowManager = WindowManager.Instance;
            windowManager.Initialize("about:blank", isHeadless: true);

            windowManager.OnLoad += () =>
            {
                Task.Run(async () =>
                {
                    int exitCode = 0;
                    try
                    {
                        ChromeManager.Instance.Initialize("about:blank");

                        var outputDir = Path.Combine(Directory.GetCurrentDirectory(), "acid-baselines");
                        Directory.CreateDirectory(outputDir);

                        using var actual = await CaptureWithBudgetAsync(acid2Url, maxCaptureMs).ConfigureAwait(false);
                        using var referenceCapture = await CaptureWithBudgetAsync(acid2ReferenceUrl, maxCaptureMs).ConfigureAwait(false);

                        var actualPath = Path.Combine(outputDir, "acid2_actual_current.png");
                        var referencePath = Path.Combine(outputDir, "acid2_reference_live_current.png");
                        SaveBitmap(actual, actualPath);
                        SaveBitmap(referenceCapture, referencePath);

                        var runner = new AcidTestRunner(outputDir);
                        var compare = await runner.CompareWithReferenceAsync(
                            "acid2_live_vs_reference",
                            actual,
                            referencePath,
                            threshold: 0.99).ConfigureAwait(false);

                        Console.WriteLine($"Result: {(compare.Passed ? "PASS" : "FAIL")}");
                        Console.WriteLine(compare.Message);
                        Console.WriteLine($"Score: {compare.Score}/100");
                        Console.WriteLine($"Actual: {actualPath}");
                        Console.WriteLine($"Reference: {referencePath}");
                        Console.WriteLine($"Diff: {compare.DiffImagePath ?? "(none)"}");
                    }
                    catch (Exception ex)
                    {
                        exitCode = 1;
                        Console.Error.WriteLine($"acid2-compare failed: {ex}");
                    }
                    finally
                    {
                        try { Console.Out.Flush(); } catch { }
                        try { Console.Error.Flush(); } catch { }
                        Environment.Exit(exitCode);
                    }
                });
            };

            windowManager.Run();

            static async Task<SKBitmap> CaptureWithBudgetAsync(string url, int maxCaptureMs)
            {
                var captureTask = CaptureWindowScreenshotAsync(url, settleMs: 9000);
                var completed = await Task.WhenAny(captureTask, Task.Delay(maxCaptureMs)).ConfigureAwait(false);
                if (completed != captureTask)
                {
                    throw new TimeoutException($"Capture timed out after {maxCaptureMs}ms for {url}");
                }

                return await captureTask.ConfigureAwait(false);
            }
        }

        private static async Task RunAcid2LayoutHtmlAsync(string[] args)
        {
            const string acid2Url = "http://acid2.acidtests.org/#top";
            const string acid2ReferenceUrl = "http://acid2.acidtests.org/reference.html";
            const int maxCaptureMs = 45000;

            var outputDir = Path.Combine(Directory.GetCurrentDirectory(), "acid-baselines");
            Directory.CreateDirectory(outputDir);
            var outputPath = args.Length > 1
                ? Path.GetFullPath(args[1])
                : Path.Combine(outputDir, "acid2_layout_snapshot.html");

            CssEngineConfig.CurrentEngine = CssEngineType.Custom;
            var windowManager = WindowManager.Instance;
            windowManager.Initialize("about:blank", isHeadless: true);

            windowManager.OnLoad += () =>
            {
                Task.Run(async () =>
                {
                    int exitCode = 0;
                    try
                    {
                        ChromeManager.Instance.Initialize("about:blank");
                        using var actual = await CaptureWithBudgetAsync(acid2Url, maxCaptureMs).ConfigureAwait(false);
                        using var referenceCapture = await CaptureWithBudgetAsync(acid2ReferenceUrl, maxCaptureMs).ConfigureAwait(false);
                        var actualPath = Path.Combine(outputDir, "acid2_actual_current.png");
                        var referencePath = Path.Combine(outputDir, "acid2_reference_live_current.png");
                        SaveBitmap(actual, actualPath);
                        SaveBitmap(referenceCapture, referencePath);

                        var runner = new AcidTestRunner(outputDir);
                        var compare = await runner.CompareWithReferenceAsync(
                            "acid2_live_vs_reference",
                            actual,
                            referencePath,
                            threshold: 0.99).ConfigureAwait(false);

                        var layoutDumpPath = Path.Combine(Directory.GetCurrentDirectory(), "layout_engine_debug.txt");
                        if (!File.Exists(layoutDumpPath))
                        {
                            throw new FileNotFoundException($"Layout dump not found: {layoutDumpPath}");
                        }

                        var html = BuildLayoutSnapshotHtml(layoutDumpPath, actualPath, referencePath, compare.DiffImagePath);
                        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
                        File.WriteAllText(outputPath, html, new UTF8Encoding(false));

                        Console.WriteLine($"Result: {(compare.Passed ? "PASS" : "FAIL")}");
                        Console.WriteLine(compare.Message);
                        Console.WriteLine($"Score: {compare.Score}/100");
                        Console.WriteLine($"Actual: {actualPath}");
                        Console.WriteLine($"Reference: {referencePath}");
                        Console.WriteLine($"Diff: {compare.DiffImagePath ?? "(none)"}");
                        Console.WriteLine($"Layout HTML: {outputPath}");
                    }
                    catch (Exception ex)
                    {
                        exitCode = 1;
                        Console.Error.WriteLine($"acid2-layout-html failed: {ex}");
                    }
                    finally
                    {
                        try { Console.Out.Flush(); } catch { }
                        try { Console.Error.Flush(); } catch { }
                        Environment.Exit(exitCode);
                    }
                });
            };

            windowManager.Run();

            static async Task<SKBitmap> CaptureWithBudgetAsync(string url, int maxCaptureMs)
            {
                var captureTask = CaptureWindowScreenshotAsync(url, settleMs: 9000);
                var completed = await Task.WhenAny(captureTask, Task.Delay(maxCaptureMs)).ConfigureAwait(false);
                if (completed != captureTask)
                {
                    throw new TimeoutException($"Capture timed out after {maxCaptureMs}ms for {url}");
                }

                return await captureTask.ConfigureAwait(false);
            }
        }

        private static async Task RunWebDriverAsync(string[] args)
        {
            var headless = args.Any(a => string.Equals(a, "--headless", StringComparison.OrdinalIgnoreCase));
            var driverPort = 4444;

            for (var i = 0; i < args.Length; i++)
            {
                var arg = args[i];
                if (arg.StartsWith("--port=", StringComparison.OrdinalIgnoreCase))
                {
                    if (int.TryParse(arg.Split('=')[1], out var inlinePort))
                    {
                        driverPort = inlinePort;
                    }

                    continue;
                }

                if (string.Equals(arg, "--port", StringComparison.OrdinalIgnoreCase) &&
                    i + 1 < args.Length &&
                    int.TryParse(args[i + 1], out var spacedPort))
                {
                    driverPort = spacedPort;
                    i++;
                }
            }

            using var lifecycle = WebDriverLifecycleDiagnostics.Start(driverPort);

            // WPT serves many fixtures on https://web-platform.test:* with local certs.
            // Automation mode should not fail navigation on certificate trust checks.
            NetworkConfiguration.Instance.IgnoreCertificateErrors = true;

            CssEngineConfig.CurrentEngine = CssEngineType.Custom;
            var windowManager = WindowManager.Instance;
            windowManager.Initialize("about:blank", isHeadless: headless);
            lifecycle.Record("window_manager_initialized", new { headless });

            windowManager.OnLoad += () =>
            {
                lifecycle.Record("window_manager_loaded");
                Task.Run(() =>
                {
                    try
                    {
                        ChromeManager.Instance.Initialize("about:blank");
                        var server = new FenBrowser.WebDriver.WebDriverServer(driverPort);
                        server.OnLog += message =>
                            lifecycle.Record("webdriver_command", new { message });
                        server.SetDriver(new HostBrowserDriver());
                        server.Start();
                        lifecycle.Record("webdriver_server_started");
                    }
                    catch (Exception ex)
                    {
                        lifecycle.Record("webdriver_server_start_failed", new
                        {
                            type = ex.GetType().FullName,
                            ex.Message,
                            ex.StackTrace
                        });
                        Console.Error.WriteLine(ex);
                        Environment.Exit(1);
                    }
                });
            };

            windowManager.Run();
            lifecycle.Record("window_manager_stopped");
            await Task.CompletedTask.ConfigureAwait(false);
        }

        private static void RunCssDebug()
        {
            var sb = new StringBuilder();
            sb.AppendLine("[DEBUG-CSS] Starting CSS Parser Test...");

            try
            {
                string css = "body { background: white; } .gb_Cd.gb_Va.gb_od:not(.gb_Md) { color: blue; } :is(.a, .b, .c) > div { display: none; } div:not(:where(.x, .y)) { opacity: 0.5; }";
                var tokenizer = new FenBrowser.FenEngine.Rendering.Css.CssTokenizer(css);
                var parser = new FenBrowser.FenEngine.Rendering.Css.CssSyntaxParser(tokenizer);
                var sheet = parser.ParseStylesheet();
                sb.AppendLine($"Parsed Rules Count: {sheet.Rules.Count}");
            }
            catch (Exception ex)
            {
                sb.AppendLine(ex.ToString());
            }

            File.WriteAllText("css_debug.txt", sb.ToString());
        }

        private static void RunCapabilityLedger(string[] args)
        {
            var result = CapabilityLedgerCommand.Run(args);
            Console.WriteLine($"Capability ledger latest: {result.LatestPath}");
            Console.WriteLine($"Capability ledger snapshot: {result.SnapshotPath}");
            Console.WriteLine($"Capability count: {result.CapabilityCount}");
            Console.WriteLine($"failureGatePassed={result.FailureGatePassed}");
            Console.WriteLine($"liveArtifactEvidenceId={result.LiveArtifactEvidenceId}");
        }

        private static async Task RunRenderPerfAsync()
        {
            var runner = new RenderPerformanceBenchmarkRunner();
            var report = await runner.RunDefaultSuiteAsync().ConfigureAwait(false);
            var artifactPath = await runner.WriteReportAsync(report).ConfigureAwait(false);
            Console.WriteLine(RenderPerformanceBenchmarkRunner.FormatSummary(report));
            Console.WriteLine($"artifact={artifactPath}");
            Console.WriteLine($"failureGatePassed={report.FailureGatePassed}");
        }

        private static async Task RunFenJsPerfAsync(string[] args)
        {
            var runner = new FenJsPerformanceBenchmarkRunner();
            var scenarios = FenJsPerformanceBenchmarkRunner.BuildDefaultSuite();
            if (args.Length > 1)
            {
                scenarios = scenarios
                    .Where(scenario => string.Equals(scenario.Name, args[1], StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                if (scenarios.Count == 0)
                {
                    throw new ArgumentException($"Unknown FenJS performance scenario '{args[1]}'.");
                }
            }

            var report = runner.RunSuite(scenarios);
            var artifactPath = await runner.WriteReportAsync(report).ConfigureAwait(false);
            Console.WriteLine(FenJsPerformanceBenchmarkRunner.FormatSummary(report));
            Console.WriteLine($"artifact={artifactPath}");
        }

        private static async Task RunDomPerfAsync()
        {
            var runner = new DomPerformanceBenchmarkRunner();
            var report = runner.RunDefaultSuite();
            var artifactPath = await runner.WriteReportAsync(report).ConfigureAwait(false);
            Console.WriteLine(DomPerformanceBenchmarkRunner.FormatSummary(report));
            Console.WriteLine($"artifact={artifactPath}");
        }

        private static void WireToolingConsoleCapture()
        {
            void AttachTab(BrowserTab tab)
            {
                if (tab == null)
                {
                    return;
                }

                tab.Browser.ConsoleMessage += message =>
                {
                    FenBrowser.FenEngine.WebAPIs.TestConsoleCapture.AddEntry("log", message);
                };
            }

            TabManager.Instance.ActiveTabChanged += AttachTab;
            AttachTab(TabManager.Instance.ActiveTab);
        }

        private static void PrintUsage()
        {
            Console.WriteLine("FenBrowser.Tooling commands:");
            Console.WriteLine("  verify <html_path>");
            Console.WriteLine("  diagnose <url> [settle_ms]");
            Console.WriteLine("  debug-site <url> [settle_ms]");
            Console.WriteLine("  debug-site-interact <url> <target_selector> <text> <submit_selector> [settle_ms] [interaction_settle_ms]");
            Console.WriteLine("  acid2");
            Console.WriteLine("  acid2-compare");
            Console.WriteLine("  acid2-layout-html [output_html]");
            Console.WriteLine("  webdriver [--port=4444] [--headless]");
            Console.WriteLine("  render-perf");
            Console.WriteLine("  js-perf [scenario]");
            Console.WriteLine("  dom-perf");
            Console.WriteLine("  capability-ledger [output_json] [--require-live-evidence] [--logs-dir <path>]");
            Console.WriteLine("  debug-css");
            Console.WriteLine("  test");
            Console.WriteLine("  test262 --root <path> [--workers N] [--timeout-ms N] [--max N] [--filter <substring>] [--output <json_path>] [--event-log <jsonl_path>]");
            Console.WriteLine("  wpt [--root <wpt_path>] [--suite normal|workers|webdriver|all] [--metadata <path>] [--test-types <csv>] [--include-file <path>] [--exclude-file <path>] [--total-chunks N --this-chunk N --chunk-type id_hash] [--plan-shards N --history <results>] [--processes N] [--max-restarts N] [--timeout-seconds N] [--stall-timeout-seconds N] [--output-dir <dir>] [--tests <paths>]");
            Console.WriteLine("  webidl-inventory [--root <repo_path>] [--idl <idl_dir>] [--output-dir <dir>] [--wpt-root <wpt_path>] [--selected-wpt <paths>]");
        }

        private static async Task<SKBitmap> CaptureWindowScreenshotAsync(string url, int settleMs = 3500)
        {
            var tab = await WindowManager.Instance.RunOnMainThread(() =>
            {
                if (TabManager.Instance.ActiveTab == null)
                {
                    return TabManager.Instance.CreateTab(url);
                }

                var active = TabManager.Instance.ActiveTab;
                _ = active.NavigateAsync(url);
                return active;
            }).ConfigureAwait(false);

            var readyDeadline = DateTime.UtcNow.AddMilliseconds(Math.Max(1200, settleMs));
            string targetCompareUrl = NormalizeUrlForCompare(url);
            while (DateTime.UtcNow < readyDeadline)
            {
                var ready = await WindowManager.Instance.RunOnMainThread(() =>
                {
                    var currentCompareUrl = NormalizeUrlForCompare(tab.Url);
                    var urlReached = string.Equals(currentCompareUrl, targetCompareUrl, StringComparison.OrdinalIgnoreCase);
                    var hasDom = tab.Browser.Document != null;
                    var hasStyles = tab.Browser.ComputedStyles != null && tab.Browser.ComputedStyles.Count > 0;
                    var isLoading = tab.IsLoading;
                    return urlReached && hasDom && hasStyles && !isLoading;
                }).ConfigureAwait(false);

                if (ready)
                {
                    break;
                }

                await Task.Delay(75).ConfigureAwait(false);
            }

            await Task.Delay(Math.Min(800, Math.Max(200, settleMs / 4))).ConfigureAwait(false);

            return await WindowManager.Instance.RunOnMainThread(() => WindowManager.Instance.CaptureScreenshot())
                .ConfigureAwait(false);

            static string NormalizeUrlForCompare(string raw)
            {
                if (string.IsNullOrWhiteSpace(raw))
                {
                    return string.Empty;
                }

                if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri))
                {
                    return raw.Trim();
                }

                var builder = new UriBuilder(uri) { Fragment = string.Empty };
                return builder.Uri.AbsoluteUri.TrimEnd('/');
            }
        }

        private sealed class LayoutSnapshotEntry
        {
            public int Depth { get; init; }
            public string Name { get; init; } = string.Empty;
            public float X { get; init; }
            public float Y { get; init; }
            public float Width { get; init; }
            public float Height { get; init; }
            public string Meta { get; init; } = string.Empty;
        }

        private static string BuildLayoutSnapshotHtml(string layoutDumpPath, string actualPath, string referencePath, string diffPath)
        {
            var lines = File.ReadAllLines(layoutDumpPath);
            var entries = ParseLayoutEntries(lines);
            if (entries.Count == 0)
            {
                throw new InvalidOperationException($"No layout entries parsed from {layoutDumpPath}.");
            }

            float maxX = entries.Max(e => e.X + Math.Max(e.Width, 1f));
            float maxY = entries.Max(e => e.Y + Math.Max(e.Height, 1f));

            string[] colors =
            {
                "#ef4444", "#3b82f6", "#10b981", "#f59e0b", "#8b5cf6",
                "#ec4899", "#06b6d4", "#84cc16", "#f97316", "#14b8a6"
            };

            var sb = new StringBuilder();
            sb.AppendLine("<!doctype html>");
            sb.AppendLine("<html><head><meta charset=\"utf-8\">");
            sb.AppendLine("<title>FenBrowser Acid2 Layout Snapshot</title>");
            sb.AppendLine("<style>");
            sb.AppendLine("body{margin:0;padding:20px;font-family:Segoe UI,Arial,sans-serif;background:#0b1020;color:#e5e7eb;}");
            sb.AppendLine(".meta{margin-bottom:14px;font-size:13px;line-height:1.45;}");
            sb.AppendLine(".meta code{background:#111827;color:#cbd5e1;padding:2px 5px;border-radius:4px;}");
            sb.AppendLine(".row{margin:4px 0;}");
            sb.AppendLine(".stage{position:relative;background:#f3f4f6;border:1px solid #334155;overflow:hidden;}");
            sb.AppendLine(".box{position:absolute;box-sizing:border-box;border:1px solid;overflow:visible;}");
            sb.AppendLine(".box.text{background:rgba(59,130,246,.12);}");
            sb.AppendLine(".label{position:absolute;left:0;top:0;transform:translate(0,-100%);font-size:10px;line-height:1;padding:2px 4px;white-space:nowrap;background:rgba(15,23,42,.92);color:#e5e7eb;border:1px solid rgba(148,163,184,.4);}");
            sb.AppendLine("</style></head><body>");
            sb.AppendLine("<h1>FenBrowser Acid2 Layout Snapshot</h1>");
            sb.AppendLine("<div class=\"meta\">");
            sb.AppendLine($"<div class=\"row\">Layout dump: <code>{HtmlEscape(layoutDumpPath)}</code></div>");
            sb.AppendLine($"<div class=\"row\">Actual screenshot: <code>{HtmlEscape(actualPath)}</code></div>");
            sb.AppendLine($"<div class=\"row\">Reference screenshot: <code>{HtmlEscape(referencePath)}</code></div>");
            sb.AppendLine($"<div class=\"row\">Diff screenshot: <code>{HtmlEscape(string.IsNullOrWhiteSpace(diffPath) ? "(none)" : diffPath)}</code></div>");
            sb.AppendLine("</div>");
            sb.AppendLine($"<div class=\"stage\" style=\"width:{Math.Ceiling(maxX + 40)}px;height:{Math.Ceiling(maxY + 40)}px;\">");

            foreach (var entry in entries.OrderBy(e => e.Depth))
            {
                var color = colors[entry.Depth % colors.Length];
                var width = Math.Max(0.5f, entry.Width);
                var height = Math.Max(0.5f, entry.Height);
                var isText = entry.Name.IndexOf("Text", StringComparison.OrdinalIgnoreCase) >= 0 ? " text" : string.Empty;
                var title = HtmlEscape($"{entry.Name} {entry.Meta}".Trim());

                sb.AppendLine(
                    $"<div class=\"box{isText}\" title=\"{title}\" style=\"left:{FormatPx(entry.X)}px;top:{FormatPx(entry.Y)}px;width:{FormatPx(width)}px;height:{FormatPx(height)}px;border-color:{color};\">");
                sb.AppendLine($"<div class=\"label\">{HtmlEscape(entry.Name)} {HtmlEscape(entry.Meta)}</div>");
                sb.AppendLine("</div>");
            }

            sb.AppendLine("</div></body></html>");
            return sb.ToString();
        }

        private static List<LayoutSnapshotEntry> ParseLayoutEntries(IEnumerable<string> lines)
        {
            var entries = new List<LayoutSnapshotEntry>();
            var regex = new Regex(
                @"^(?<indent>\s*)(?<name>[A-Za-z][A-Za-z0-9_\-]*)\s+\[(?<x>-?\d+(?:\.\d+)?),\s*(?<y>-?\d+(?:\.\d+)?)\s+(?<w>-?\d+(?:\.\d+)?)x(?<h>-?\d+(?:\.\d+)?)\](?<meta>.*)$",
                RegexOptions.Compiled);

            foreach (var line in lines)
            {
                var match = regex.Match(line);
                if (!match.Success)
                {
                    continue;
                }

                int depth = match.Groups["indent"].Value.Length / 2;
                var entry = new LayoutSnapshotEntry
                {
                    Depth = depth,
                    Name = match.Groups["name"].Value,
                    X = ParseInvariantFloat(match.Groups["x"].Value),
                    Y = ParseInvariantFloat(match.Groups["y"].Value),
                    Width = ParseInvariantFloat(match.Groups["w"].Value),
                    Height = ParseInvariantFloat(match.Groups["h"].Value),
                    Meta = match.Groups["meta"].Value.Trim()
                };

                entries.Add(entry);
            }

            return entries;
        }

        private static float ParseInvariantFloat(string value)
        {
            return float.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture);
        }

        private static string FormatPx(float value)
        {
            return value.ToString("0.###", CultureInfo.InvariantCulture);
        }

        private static string HtmlEscape(string value)
        {
            return System.Net.WebUtility.HtmlEncode(value ?? string.Empty);
        }

        private static void SaveBitmap(SKBitmap bitmap, string path)
        {
            using var image = SKImage.FromBitmap(bitmap);
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            using var stream = File.Open(path, FileMode.Create, FileAccess.Write, FileShare.Read);
            data.SaveTo(stream);
        }
    }
}
