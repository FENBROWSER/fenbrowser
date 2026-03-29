using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using FenBrowser.Core;
using FenBrowser.Core.Css;
using FenBrowser.Core.Logging;
using FenBrowser.FenEngine.Testing;
using FenBrowser.Tooling.Host;
using FenBrowser.Host;
using FenBrowser.Host.Tabs;
using FenBrowser.Host.WebDriver;

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
                case "test262":
                    await RunTest262Async(args).ConfigureAwait(false);
                    return;
                case "test262-suite":
                    await RunTest262SuiteAsync(args).ConfigureAwait(false);
                    return;
                case "test262-range":
                    await RunTest262RangeAsync(args).ConfigureAwait(false);
                    return;
                case "wpt":
                    await RunWptAsync(args).ConfigureAwait(false);
                    return;
                case "acid2":
                    await RunAcid2Async().ConfigureAwait(false);
                    return;
                case "webdriver":
                    await RunWebDriverAsync(args).ConfigureAwait(false);
                    return;
                case "debug-css":
                    RunCssDebug();
                    return;
                case "test":
                    await FenBrowser.FenEngine.Tests.LogicTestRunner.MainTest(args).ConfigureAwait(false);
                    return;
                default:
                    PrintUsage();
                    return;
            }
        }

        private static async Task RunVerifyAsync(string[] args)
        {
            if (args.Length < 2)
            {
                throw new ArgumentException("verify requires <html_path>.");
            }

            await VerificationRunner.GenerateSnapshot(args[1], "verification_output.png").ConfigureAwait(false);
        }

        private static async Task RunTest262Async(string[] args)
        {
            if (args.Length < 2)
            {
                throw new ArgumentException("test262 requires <test_file_or_category>.");
            }

            var input = args[1];
            var rootPath = args.Length > 2 ? args[2] : Path.Combine(Directory.GetCurrentDirectory(), "test262");
            var runner = new Test262Runner(rootPath);

            if (File.Exists(input))
            {
                var result = await runner.RunSingleTestAsync(input).ConfigureAwait(false);
                Console.WriteLine($"Result: {(result.Passed ? "PASS" : "FAIL")}");
                Console.WriteLine($"Duration: {result.Duration.TotalMilliseconds}ms");
                if (!result.Passed)
                {
                    Console.WriteLine($"Expected: {result.Expected}");
                    Console.WriteLine($"Actual: {result.Actual}");
                    if (!string.IsNullOrWhiteSpace(result.Error))
                    {
                        Console.WriteLine($"Error: {result.Error}");
                    }
                }
                return;
            }

            var results = await runner.RunCategoryAsync(input, (_, count) =>
            {
                if (count % 100 == 0)
                {
                    Console.Write($"\rProcessed: {count}");
                }
            }).ConfigureAwait(false);

            Console.WriteLine($"\rProcessed: {results.Count}");
            Console.WriteLine(runner.GenerateSummary());
        }

        private static async Task RunTest262SuiteAsync(string[] args)
        {
            var category = args.Length > 1 ? args[1] : string.Empty;
            var rootPath = args.Length > 2 ? args[2] : Path.Combine(Directory.GetCurrentDirectory(), "test262");
            var runner = new Test262Runner(rootPath);
            var results = await runner.RunCategoryAsync(category, (_, count) =>
            {
                if (count % 100 == 0)
                {
                    Console.Write(".");
                }
            }).ConfigureAwait(false);

            var passed = results.Count(r => r.Passed);
            var failed = results.Count - passed;
            Console.WriteLine();
            Console.WriteLine($"Total: {results.Count}, Passed: {passed}, Failed: {failed}");
        }

        private static async Task RunTest262RangeAsync(string[] args)
        {
            if (args.Length < 3)
            {
                throw new ArgumentException("test262-range requires <skip> <take> [category] [root].");
            }

            var skip = int.Parse(args[1]);
            var take = int.Parse(args[2]);
            var category = args.Length > 3 ? args[3] : string.Empty;
            var rootPath = args.Length > 4 ? args[4] : Path.Combine(Directory.GetCurrentDirectory(), "test262");
            var runner = new Test262Runner(rootPath);
            var results = await runner.RunSliceAsync(category, skip, take, (_, count) =>
            {
                if (count % 10 == 0)
                {
                    Console.Write(".");
                }
            }).ConfigureAwait(false);

            var passed = results.Count(r => r.Passed);
            var failed = results.Count - passed;
            Console.WriteLine();
            Console.WriteLine($"Total: {results.Count}, Passed: {passed}, Failed: {failed}");
        }

        private static async Task RunWptAsync(string[] args)
        {
            if (args.Length < 2)
            {
                throw new ArgumentException("wpt requires <test_file_or_category> [rootPath].");
            }

            var input = args[1];
            var rootPath = args.Length > 2 ? args[2] : Path.Combine(Directory.GetCurrentDirectory(), "wpt");

            BrowserIntegrationRuntime.BrowserHostOptionsFactory = () => ToolingBrowserHostOptions.CreateForWpt(rootPath);

            CssEngineConfig.CurrentEngine = CssEngineType.Custom;
            var windowManager = WindowManager.Instance;
            windowManager.Initialize("about:blank", isHeadless: true);

            WireToolingConsoleCapture();

            windowManager.OnLoad += () =>
            {
                Task.Run(async () =>
                {
                    try
                    {
                        ChromeManager.Instance.Initialize("about:blank");

                        var runner = new WPTTestRunner(rootPath, async url =>
                        {
                            await WindowManager.Instance.RunOnMainThread(async () =>
                            {
                                if (TabManager.Instance.ActiveTab == null)
                                {
                                    TabManager.Instance.CreateTab(url);
                                    return;
                                }

                                await TabManager.Instance.ActiveTab.NavigateAsync(url).ConfigureAwait(false);
                            }).ConfigureAwait(false);
                        });

                        if (File.Exists(input))
                        {
                            var result = await runner.RunSingleTestAsync(input).ConfigureAwait(false);
                            Console.WriteLine($"Result: {(result.Success ? "PASS" : "FAIL")}");
                            Console.WriteLine($"Stats: {result.PassCount} passed, {result.FailCount} failed");
                            if (!string.IsNullOrWhiteSpace(result.Error))
                            {
                                Console.WriteLine(result.Error);
                            }
                        }
                        else
                        {
                            var results = await runner.RunCategoryAsync(input, (_, count) =>
                            {
                                if (count % 10 == 0)
                                {
                                    Console.Write($"\rProcessed: {count}");
                                }
                            }).ConfigureAwait(false);

                            Console.WriteLine($"\rProcessed: {results.Count}");
                            Console.WriteLine(runner.GenerateSummary());
                        }
                    }
                    finally
                    {
                        BrowserIntegrationRuntime.Reset();
                        Environment.Exit(0);
                    }
                });
            };

            windowManager.Run();
        }

        private static async Task RunAcid2Async()
        {
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
                        var result = await runner.RunAcid2Async(async url =>
                        {
                            var screenshotTask = await WindowManager.Instance.RunOnMainThread(async () =>
                            {
                                if (TabManager.Instance.ActiveTab == null)
                                {
                                    TabManager.Instance.CreateTab(url);
                                }
                                else
                                {
                                    await TabManager.Instance.ActiveTab.NavigateAsync(url).ConfigureAwait(false);
                                }

                                await Task.Delay(2000).ConfigureAwait(false);
                                return WindowManager.Instance.CaptureScreenshot();
                            }).ConfigureAwait(false);

                            return await screenshotTask.ConfigureAwait(false);
                        }).ConfigureAwait(false);

                        Console.WriteLine($"Result: {(result.Passed ? "PASS" : "FAIL")}");
                        Console.WriteLine(result.Message);
                    }
                    finally
                    {
                        Environment.Exit(0);
                    }
                });
            };

            windowManager.Run();
        }

        private static async Task RunWebDriverAsync(string[] args)
        {
            var port = args
                .FirstOrDefault(a => a.StartsWith("--port=", StringComparison.OrdinalIgnoreCase));
            var headless = args.Any(a => string.Equals(a, "--headless", StringComparison.OrdinalIgnoreCase));
            var driverPort = port != null && int.TryParse(port.Split('=')[1], out var parsedPort)
                ? parsedPort
                : 4444;

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
            Console.WriteLine("  test262 <test_file_or_category> [root]");
            Console.WriteLine("  test262-suite [category] [root]");
            Console.WriteLine("  test262-range <skip> <take> [category] [root]");
            Console.WriteLine("  wpt <test_file_or_category> [root]");
            Console.WriteLine("  acid2");
            Console.WriteLine("  webdriver [--port=4444] [--headless]");
            Console.WriteLine("  debug-css");
            Console.WriteLine("  test");
        }
    }
}
