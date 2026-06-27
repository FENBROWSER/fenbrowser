using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Globalization;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using FenBrowser.Core;
using FenBrowser.Core.Css;
using FenBrowser.Core.Engine;
using FenBrowser.Core.Logging;
using FenBrowser.FenEngine.Scripting;
using FenBrowser.Tooling.Host;
using FenBrowser.Host;
using FenBrowser.Host.Tabs;
using FenBrowser.Host.WebDriver;
using FenBrowser.FenEngine.Rendering.Performance;
using SkiaSharp;

namespace FenBrowser.Tooling
{
    internal static class Program
    {
        public static async Task Main(string[] args)
        {
            if (args.Length == 0)
            {
                PrintUsage();
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
                case "multitab":
                    // await MultiTabHeadlessRunner.RunAsync(args.Skip(1).ToArray()).ConfigureAwait(false);
                    Console.WriteLine("[tooling] Multitab removed with legacy engine.");
                    return;
                default:
                    PrintUsage();
                    return;
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
            var bundleDir = WriteDebugSiteBundle(report);

            PrintDebugSiteReport(report);
            Console.WriteLine();
            Console.WriteLine($"[debug-site] Bundle: {bundleDir}");
        }

        private static async Task<DebugSiteReport> CollectDebugSiteDiagnosticsAsync(string url, int settleMs)
        {
            CssEngineConfig.CurrentEngine = CssEngineType.Custom;
            ConfigureDebugSiteFileLogging();

            var consoleMessages = new List<string>();
            var navFailures = new List<string>();
            var networkCapture = new DebugSiteNetworkCapture();

            using var host = new FenBrowser.FenEngine.Rendering.BrowserHost();
            host.ConsoleMessage += msg => { lock (consoleMessages) consoleMessages.Add(msg); };
            host.NavigationFailed += (_, msg) => { lock (navFailures) navFailures.Add(msg); };
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
            sw.Stop();

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

            var root = host.GetDomRoot();
            int nodeCount = CountDomNodes(root);
            string rawHtml = SafeCall(() => host.GetRawHtml()) ?? string.Empty;
            string text = SafeCall(() => host.GetTextContent()) ?? string.Empty;
            var styles = SafeCall(() => host.ComputedStyles);
            var screenshot = CaptureDebugSiteScreenshot(root, styles, host.CurrentUri?.AbsoluteUri ?? url);
            var lifecycle = BuildLifecycleSummary(host.NavigationLifecycleState, probeResults);
            var scriptLoading = host.Engine?.ScriptEngine?.GetScriptLoadingSnapshot() ?? new BrowserScriptLoadingSnapshot();
            var eventLoop = host.Engine?.ScriptEngine?.GetEventLoopSnapshot() ?? new BrowserEventLoopSnapshot();

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
                NetworkRequests = networkCapture.Snapshot(),
                Probes = probeResults,
                RenderedTextSample = text.Length > 800 ? text.Substring(0, 800) : text,
                ScreenshotCaptured = screenshot.Captured,
                ScreenshotPath = screenshot.Path,
                ScreenshotWidth = screenshot.Width,
                ScreenshotHeight = screenshot.Height,
                ScreenshotError = screenshot.Error
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

        private static DebugSiteScreenshotResult CaptureDebugSiteScreenshot(
            FenBrowser.Core.Dom.V2.Node root,
            Dictionary<FenBrowser.Core.Dom.V2.Node, CssComputed> styles,
            string baseUrl)
        {
            const int width = 1280;
            const int height = 800;
            var screenshotPath = DiagnosticPaths.GetRootArtifactPath("debug_screenshot.png");

            if (root == null)
            {
                return new DebugSiteScreenshotResult(false, screenshotPath, width, height, "DOM root was not available.");
            }

            if (styles == null || styles.Count == 0)
            {
                return new DebugSiteScreenshotResult(false, screenshotPath, width, height, "Computed styles were not available.");
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

                using var image = SKImage.FromBitmap(bitmap);
                using var data = image.Encode(SKEncodedImageFormat.Png, 100);
                if (data == null)
                {
                    return new DebugSiteScreenshotResult(false, screenshotPath, width, height, "PNG encoding returned null.");
                }

                using (var stream = File.Open(screenshotPath, FileMode.Create, FileAccess.Write, FileShare.Read))
                {
                    data.SaveTo(stream);
                }

                return new DebugSiteScreenshotResult(true, screenshotPath, width, height, null);
            }
            catch (Exception ex)
            {
                return new DebugSiteScreenshotResult(false, screenshotPath, width, height, ex.GetType().Name + ": " + ex.Message);
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
            Console.WriteLine($"Screenshot captured    : {report.ScreenshotCaptured}");
            Console.WriteLine($"Navigation phase       : {report.Lifecycle?.Phase ?? "(unknown)"}");
            Console.WriteLine($"Navigation detail      : {report.Lifecycle?.Detail ?? "(none)"}");
            Console.WriteLine($"Scripts discovered     : {report.ScriptLoading?.TotalScripts ?? 0}");
            Console.WriteLine($"Scripts executed       : {report.ScriptLoading?.ExecutionCompleted ?? 0}");
            Console.WriteLine($"Scripts failed         : {report.ScriptLoading?.ExecutionFailed ?? 0}");
            Console.WriteLine($"DOMContentLoaded fired : {report.EventLoop?.DomContentLoadedFired ?? false}");
            Console.WriteLine($"Load fired             : {report.EventLoop?.LoadFired ?? false}");
            Console.WriteLine($"Microtask checkpoints  : {report.EventLoop?.MicrotaskCheckpoints ?? 0}");
            Console.WriteLine($"Network requests       : {report.NetworkRequests.Count}");
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
            File.WriteAllText(
                Path.Combine(bundleDir, "summary.json"),
                JsonSerializer.Serialize(report, jsonOptions),
                new UTF8Encoding(false));
            File.WriteAllText(
                Path.Combine(bundleDir, "summary.md"),
                BuildDebugSiteSummary(report, runId),
                new UTF8Encoding(false));
            File.WriteAllLines(Path.Combine(bundleDir, "console.log"), report.ConsoleMessages, new UTF8Encoding(false));
            File.WriteAllLines(Path.Combine(bundleDir, "navigation_failures.log"), report.NavigationFailures, new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(bundleDir, "rendered_text.txt"), report.RenderedTextSample ?? string.Empty, new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(bundleDir, "probes.json"), JsonSerializer.Serialize(report.Probes, jsonOptions), new UTF8Encoding(false));
            File.WriteAllText(
                Path.Combine(bundleDir, "exceptions.json"),
                JsonSerializer.Serialize(ExtractExceptionRecords(report.ConsoleMessages, report.NavigateException), jsonOptions),
                new UTF8Encoding(false));
            File.WriteAllText(
                Path.Combine(bundleDir, "missing_apis.json"),
                JsonSerializer.Serialize(ExtractMissingApiRecords(report.ConsoleMessages), jsonOptions),
                new UTF8Encoding(false));
            File.WriteAllText(
                Path.Combine(bundleDir, "network.json"),
                JsonSerializer.Serialize(BuildNetworkSummary(report), jsonOptions),
                new UTF8Encoding(false));
            File.WriteAllText(
                Path.Combine(bundleDir, "lifecycle.json"),
                JsonSerializer.Serialize(report.Lifecycle ?? BuildLifecycleSummary(null, report.Probes), jsonOptions),
                new UTF8Encoding(false));
            File.WriteAllText(
                Path.Combine(bundleDir, "script_loading.json"),
                JsonSerializer.Serialize(report.ScriptLoading ?? new BrowserScriptLoadingSnapshot(), jsonOptions),
                new UTF8Encoding(false));
            File.WriteAllText(
                Path.Combine(bundleDir, "event_loop.json"),
                JsonSerializer.Serialize(report.EventLoop ?? new BrowserEventLoopSnapshot(), jsonOptions),
                new UTF8Encoding(false));

            TryCopyLogArtifact("debug_screenshot.png", Path.Combine(bundleDir, "screenshot.png"));
            TryCopyLogArtifact("dom_dump.txt", Path.Combine(bundleDir, "dom_dump.txt"));
            TryCopyLatestLogArtifact("raw_source_*.html", Path.Combine(bundleDir, "raw_source.html"));
            TryCopyLatestLogArtifact("rendered_text_*.txt", Path.Combine(bundleDir, "rendered_text_artifact.txt"));
            TryCopyLatestLogArtifact("fenbrowser_trace_*.jsonl", Path.Combine(bundleDir, "trace.jsonl"));
            TryCopyLatestLogArtifact("fenbrowser_*_trace.jsonl", Path.Combine(bundleDir, "trace.jsonl"));
            TryCopyLatestStructuredLogArtifact(Path.Combine(bundleDir, "logs.ndjson"));

            File.WriteAllText(
                Path.Combine(bundleDir, "artifact_manifest.json"),
                JsonSerializer.Serialize(BuildArtifactManifest(bundleDir), jsonOptions),
                new UTF8Encoding(false));

            return bundleDir;
        }

        private static string BuildDebugSiteSummary(DebugSiteReport report, string runId)
        {
            var firstConsoleError = report.ConsoleMessages.FirstOrDefault(IsLikelyErrorMessage) ?? "(none captured)";
            var firstNavFailure = report.NavigationFailures.FirstOrDefault() ?? "(none captured)";
            var firstMissingApi = ExtractMissingApiRecords(report.ConsoleMessages).FirstOrDefault()?.Api ?? "(none captured)";
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
            sb.AppendLine($"Screenshot captured: {report.ScreenshotCaptured}");
            if (!report.ScreenshotCaptured && !string.IsNullOrWhiteSpace(report.ScreenshotError))
            {
                sb.AppendLine($"Screenshot error: {report.ScreenshotError}");
            }
            sb.AppendLine($"Navigation lifecycle phase: {report.Lifecycle?.Phase ?? "(unknown)"}");
            sb.AppendLine($"Navigation lifecycle detail: {report.Lifecycle?.Detail ?? "(none)"}");
            sb.AppendLine($"Document readyState probe: {report.Lifecycle?.DocumentReadyState ?? "(not captured)"}");
            sb.AppendLine($"Scripts discovered: {report.ScriptLoading?.TotalScripts ?? 0}");
            sb.AppendLine($"Scripts eligible: {report.ScriptLoading?.EligibleScripts ?? 0}");
            sb.AppendLine($"Scripts executed: {report.ScriptLoading?.ExecutionCompleted ?? 0}");
            sb.AppendLine($"Scripts failed: {report.ScriptLoading?.ExecutionFailed ?? 0}");
            sb.AppendLine($"Script fetch failures: {report.ScriptLoading?.FetchFailed ?? 0}");
            sb.AppendLine($"Event-loop status: {report.EventLoop?.Status ?? "not-run"}");
            sb.AppendLine($"DOMContentLoaded fired: {report.EventLoop?.DomContentLoadedFired ?? false}");
            sb.AppendLine($"Load fired: {report.EventLoop?.LoadFired ?? false}");
            sb.AppendLine($"Microtask checkpoints: {report.EventLoop?.MicrotaskCheckpoints ?? 0}");
            sb.AppendLine($"Timers scheduled: {report.EventLoop?.TimersScheduled ?? 0}");
            sb.AppendLine($"Animation frames executed: {report.EventLoop?.AnimationFramesExecuted ?? 0}");
            sb.AppendLine($"Network requests: {report.NetworkRequests.Count}");
            sb.AppendLine($"Failed network requests: {failedNetworkRequests}");
            sb.AppendLine($"Navigation failures: {report.NavigationFailures.Count}");
            sb.AppendLine($"Console messages: {report.ConsoleMessages.Count}");
            sb.AppendLine();
            sb.AppendLine("## First Blocker Signals");
            sb.AppendLine();
            sb.AppendLine($"First fatal console error: {firstConsoleError}");
            sb.AppendLine($"First fatal network/navigation error: {firstNavFailure}");
            sb.AppendLine($"First missing API: {firstMissingApi}");
            sb.AppendLine($"Navigation lifecycle terminal: {report.Lifecycle?.IsTerminalPhase ?? false}");
            sb.AppendLine($"Script loading status: {report.ScriptLoading?.Status ?? "not-run"}");
            sb.AppendLine("First layout blocker: (not automatically classified yet)");
            sb.AppendLine($"DOMContentLoaded fired: {report.EventLoop?.DomContentLoadedFired ?? false}");
            sb.AppendLine($"Load fired: {report.EventLoop?.LoadFired ?? false}");
            sb.AppendLine();
            sb.AppendLine("## Artifact Contract");
            sb.AppendLine();
            sb.AppendLine("- `summary.json`: structured run summary.");
            sb.AppendLine("- `trace.jsonl`: copied from latest engine trace when present.");
            sb.AppendLine("- `logs.ndjson`: copied from latest structured engine log when present.");
            sb.AppendLine("- `screenshot.png`: copied from `logs/debug_screenshot.png` when present.");
            sb.AppendLine("- `lifecycle.json`: final navigation lifecycle snapshot and document readyState probe.");
            sb.AppendLine("- `script_loading.json`: script discovery, fetch, execution, failure, and async-pending counts.");
            sb.AppendLine("- `event_loop.json`: DOMContentLoaded/load, microtask, timer, and requestAnimationFrame counters.");
            sb.AppendLine("- `dom_dump.txt`: copied from `logs/dom_dump.txt` when present.");
            sb.AppendLine("- `artifact_manifest.json`: present/missing artifact status.");
            return sb.ToString();
        }

        private static DebugSiteLifecycleSummary BuildLifecycleSummary(
            NavigationLifecycleSnapshot snapshot,
            IReadOnlyDictionary<string, string> probes)
        {
            string readyState = null;
            probes?.TryGetValue("typeof document !== 'undefined' ? document.readyState : 'no-document'", out readyState);

            var phase = snapshot?.Phase ?? NavigationLifecyclePhase.Idle;
            var isTerminal = phase == NavigationLifecyclePhase.Complete ||
                             phase == NavigationLifecyclePhase.Failed ||
                             phase == NavigationLifecyclePhase.Cancelled;

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
                IsFailureTerminalPhase = phase == NavigationLifecyclePhase.Failed || phase == NavigationLifecyclePhase.Cancelled
            };
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

        private static List<object> BuildArtifactManifest(string bundleDir)
        {
            string[] expected =
            {
                "summary.md",
                "summary.json",
                "trace.jsonl",
                "logs.ndjson",
                "console.log",
                "network.json",
                "lifecycle.json",
                "script_loading.json",
                "event_loop.json",
                "exceptions.json",
                "missing_apis.json",
                "probes.json",
                "dom_dump.txt",
                "raw_source.html",
                "rendered_text.txt",
                "screenshot.png"
            };

            return expected
                .Select(name =>
                {
                    var path = Path.Combine(bundleDir, name);
                    var info = File.Exists(path) ? new FileInfo(path) : null;
                    return (object)new
                    {
                        name,
                        exists = info != null,
                        sizeBytes = info?.Length ?? 0,
                        lastWriteUtc = info?.LastWriteTimeUtc.ToString("O", CultureInfo.InvariantCulture)
                    };
                })
                .ToList();
        }

        private static List<ExceptionRecord> ExtractExceptionRecords(IEnumerable<string> consoleMessages, string navigateException)
        {
            var records = new List<ExceptionRecord>();
            if (!string.IsNullOrWhiteSpace(navigateException))
            {
                records.Add(new ExceptionRecord("NavigateAsync", "Exception", navigateException));
            }

            foreach (var message in consoleMessages ?? Enumerable.Empty<string>())
            {
                if (IsLikelyErrorMessage(message))
                {
                    records.Add(new ExceptionRecord("Console", "Error", message));
                }
            }

            return records;
        }

        private static List<MissingApiRecord> ExtractMissingApiRecords(IEnumerable<string> consoleMessages)
        {
            var records = new List<MissingApiRecord>();
            foreach (var message in consoleMessages ?? Enumerable.Empty<string>())
            {
                var api = TryExtractMissingApiName(message);
                if (!string.IsNullOrWhiteSpace(api) &&
                    records.All(existing => !string.Equals(existing.Api, api, StringComparison.OrdinalIgnoreCase)))
                {
                    records.Add(new MissingApiRecord(api, message));
                }
            }

            return records;
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
            public List<DebugSiteNetworkRecord> NetworkRequests { get; init; } = new();
            public Dictionary<string, string> Probes { get; init; } = new(StringComparer.Ordinal);
            public string RenderedTextSample { get; init; }
            public bool ScreenshotCaptured { get; init; }
            public string ScreenshotPath { get; init; }
            public int ScreenshotWidth { get; init; }
            public int ScreenshotHeight { get; init; }
            public string ScreenshotError { get; init; }
        }

        private sealed record ExceptionRecord(string Source, string Type, string Message);

        private sealed record MissingApiRecord(string Api, string Evidence);

        private sealed record DebugSiteScreenshotResult(
            bool Captured,
            string Path,
            int Width,
            int Height,
            string Error);

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
                var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (headers == null)
                {
                    return result;
                }

                foreach (var header in headers)
                {
                    result[header.Key] = string.Join(", ", header.Value);
                }

                return result;
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

            // WPT serves many fixtures on https://web-platform.test:* with local certs.
            // Automation mode should not fail navigation on certificate trust checks.
            NetworkConfiguration.Instance.IgnoreCertificateErrors = true;

            CssEngineConfig.CurrentEngine = CssEngineType.Custom;
            var windowManager = WindowManager.Instance;
            windowManager.Initialize("about:blank", isHeadless: headless);

            windowManager.OnLoad += () =>
            {
                Task.Run(() =>
                {
                    try
                    {
                        ChromeManager.Instance.Initialize("about:blank");
                        var server = new FenBrowser.WebDriver.WebDriverServer(driverPort);
                        // server.SetDriver(new HostBrowserDriver()); // Legacy removed
                        server.Start();
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine(ex);
                        Environment.Exit(1);
                    }
                });
            };

            windowManager.Run();
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
            Console.WriteLine("  acid2");
            Console.WriteLine("  acid2-compare");
            Console.WriteLine("  acid2-layout-html [output_html]");
            Console.WriteLine("  webdriver [--port=4444] [--headless]");
            Console.WriteLine("  render-perf");
            Console.WriteLine("  capability-ledger [output_json] [--require-live-evidence] [--logs-dir <path>]");
            Console.WriteLine("  debug-css");
            Console.WriteLine("  test");
            Console.WriteLine("  test262 --root <path> [--workers N] [--timeout-ms N] [--max N] [--filter <substring>] [--output <json_path>] [--event-log <jsonl_path>]");
            Console.WriteLine("  wpt [--root <wpt_path>] [--binary <host_exe>] [--webdriver-binary <launcher>] [--processes N] [--timeout-seconds N] [--venv <path>] [--skip-venv-setup] [--output-dir <dir>] [--tests <paths>]");
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
