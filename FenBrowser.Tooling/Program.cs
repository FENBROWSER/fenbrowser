using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Globalization;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using FenBrowser.Core;
using FenBrowser.Core.Css;
using FenBrowser.Core.Logging;
using FenBrowser.FenEngine.Testing;
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
                case "jstime":
                    RunJsTime(args);
                    return;
                case "acid2":
                    await RunAcid2Async().ConfigureAwait(false);
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
                    await FenBrowser.FenEngine.Tests.LogicTestRunner.MainTest(args).ConfigureAwait(false);
                    return;
                case "test262":
                    await Test262ToolRunner.RunAsync(args).ConfigureAwait(false);
                    return;
                case "wpt":
                    await WptToolRunner.RunAsync(args).ConfigureAwait(false);
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
                "document.readyState",
                "typeof window.__SCRIPTS_LOADED__",
                "Object.keys(window.__SCRIPTS_LOADED__||{}).join(',')",
                "typeof window.__INITIAL_STATE__",
                "typeof window.webpackChunk_twitter_responsive_web",
                "document.querySelectorAll('*').length",
                "document.getElementById('react-root') ? document.getElementById('react-root').children.length : 'no-react-root'",
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
                catch (Exception ex) { val = "<throw> " + ex.Message; }
                if (val.Length > 200) val = val.Substring(0, 200) + "…";
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
            const int maxRunMs = 35000;
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
            const string acid2Url = "http://acid2.acidtests.org/#top";
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
                        server.SetDriver(new HostBrowserDriver());
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
