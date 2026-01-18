using System;
using System.Threading.Tasks;
using FenBrowser.Core;
using FenBrowser.Core.Logging;
using FenBrowser.Core.Css;
using System.Linq;
using System.Text;
using FenBrowser.FenEngine.Testing;
using System.IO;

namespace FenBrowser.Host
{
    /// <summary>
    /// FenBrowser.Host Entry Point.
    /// Bootstraps the application by initializing WindowManager and ChromeManager.
    /// </summary>
    public class Program
    {
        public static void Main(string[] args)
        {
            try
            {
                // Global Exception Handling
                AppDomain.CurrentDomain.UnhandledException += (sender, e) => {
                    FenLogger.Error($"[CRASH] Unhandled Domain Exception: {e.ExceptionObject}", LogCategory.General);
                };

                TaskScheduler.UnobservedTaskException += (sender, e) => {
                    FenLogger.Error($"[CRASH] Unobserved Task Exception: {e.Exception}", LogCategory.General);
                    e.SetObserved();
                };

                // 0. CLI Tooling Interception
                if (args.Length >= 2 && args[0] == "--test262")
                {
                    AttachConsole(ATTACH_PARENT_PROCESS);
                    
                    string input = args[1]; // File path OR category
                    string rootPath = args.Length > 2 ? args[2] : @"C:\Users\udayk\test262";
                    var runner = new Test262Runner(rootPath);
                    
                    if (File.Exists(input))
                    {
                        // Run Single File
                        Console.WriteLine($"Running Test262 File: {Path.GetFileName(input)}");
                        var result = runner.RunSingleTestAsync(input).GetAwaiter().GetResult();
                        
                        Console.WriteLine($"Result: {(result.Passed ? "PASS" : "FAIL")}");
                        if (!result.Passed)
                        {
                            Console.WriteLine($"  Expected: {result.Expected}");
                            Console.WriteLine($"  Actual:   {result.Actual}");
                            if (!string.IsNullOrEmpty(result.Error))
                                Console.WriteLine($"  Error:    {result.Error}");
                        }
                        Console.WriteLine($"Duration: {result.Duration.TotalMilliseconds}ms");
                    }
                    else
                    {
                        // Assume Category (e.g., "language" or "language/expressions")
                        Console.WriteLine($"Running Test262 Category: {input}");
                        Console.WriteLine("Starting execution... (this may take a while)");
                        
                        var results = runner.RunCategoryAsync(input, (filename, count) => {
                            if (count % 100 == 0)
                            {
                                // Pad output to overwrite previous lines completely if needed
                                string msg = $"\rProcessed: {count} - {filename}";
                                if (msg.Length < 70) msg = msg.PadRight(70);
                                Console.Write(msg);
                            }
                        }).GetAwaiter().GetResult();
                        
                        Console.WriteLine($"\rProcessed: {results.Count} tests. Done.");
                        
                        // Generate Report
                        var summary = runner.GenerateSummary();
                        Console.WriteLine(summary);
                        
                        // Save to file
                        var reportPath = Path.Combine(Directory.GetCurrentDirectory(), "test262_report.txt");
                        var sb = new StringBuilder();
                        sb.AppendLine($"Test262 Report - {DateTime.Now}");
                        sb.AppendLine($"Category: {input}");
                        sb.AppendLine(summary);
                        
                        if (results.Any(r => !r.Passed))
                        {
                            sb.AppendLine("=== All Failures ===");
                            foreach(var fail in results.Where(r => !r.Passed))
                            {
                                sb.AppendLine($"[FAIL] {Path.GetRelativePath(rootPath, fail.TestFile)}");
                                sb.AppendLine($"       Expected: {fail.Expected}");
                                sb.AppendLine($"       Actual:   {fail.Actual}");
                                if(!string.IsNullOrEmpty(fail.Error))
                                    sb.AppendLine($"       Error:    {fail.Error}");
                                sb.AppendLine();
                            }
                        }
                        
                        File.WriteAllText(reportPath, sb.ToString());
                        Console.WriteLine($"Full report saved to: {reportPath}");
                    }

                    return; // Exit after running test
                }
                else if (args.Length >= 2 && args[0] == "--wpt")
                {
                    AttachConsole(ATTACH_PARENT_PROCESS);
                    string input = args[1]; // File path OR category
                    string rootPath = args.Length > 2 ? args[2] : @"C:\Users\udayk\wpt"; // Default WPT path
                    
                    Console.WriteLine($"[WPT] Initializing Headless Engine for: {input}");

                    // Initialize Headless
                    FenBrowser.Core.Logging.LogManager.InitializeFromSettings();
                    CssEngineConfig.CurrentEngine = CssEngineType.Custom;
                    var wptWm = WindowManager.Instance;
                    wptWm.Initialize("about:blank", isHeadless: true);

                    // Hook into OnLoad to run tests once engine is ready
                    wptWm.OnLoad += () => {
                         Task.Run(async () => {
                            try
                            {
                                // Initialize ChromeManager (UI) - even if hidden
                                ChromeManager.Instance.Initialize("about:blank");
                                
                                var runner = new WPTTestRunner(rootPath, async (url) => {
                                    // Make sure we run on main thread where OpenGL context exists
                                    await FenBrowser.Host.Program.RunOnMainThread(async () => {
                                        if (FenBrowser.Host.Tabs.TabManager.Instance.ActiveTab != null)
                                        {
                                            await FenBrowser.Host.Tabs.TabManager.Instance.ActiveTab.NavigateAsync(url);
                                        }
                                        else
                                        {
                                            // Ensure a tab exists
                                            FenBrowser.Host.Tabs.TabManager.Instance.CreateTab(url);
                                        }
                                    });
                                });
                                
                                if (File.Exists(input))
                                {
                                    Console.WriteLine($"Running WPT File: {Path.GetFileName(input)}");
                                    var result = await runner.RunSingleTestAsync(input);
                                    
                                    Console.WriteLine($"Result: {(result.Success ? "PASS" : "FAIL")}");
                                    Console.WriteLine($"Stats:  {result.PassCount} passed, {result.FailCount} failed");
                                    if (!result.Success && !string.IsNullOrEmpty(result.Error))
                                    {
                                        Console.WriteLine($"Error: {result.Error}");
                                    }
                                }
                                else
                                {
                                    Console.WriteLine($"Running WPT Category: {input}");
                                    Console.WriteLine("Starting execution...");
                                    
                                    var results = await runner.RunCategoryAsync(input, (filename, count) => {
                                        if (count % 10 == 0)
                                        {
                                            string msg = $"\rProcessed: {count} - {filename}";
                                            if (msg.Length < 70) msg = msg.PadRight(70);
                                            Console.Write(msg);
                                        }
                                    });
                                    
                                    Console.WriteLine($"\rProcessed: {results.Count} tests. Done.");
                                    
                                    var summary = runner.GenerateSummary();
                                    Console.WriteLine(summary);
                                    
                                    var reportPath = Path.Combine(Directory.GetCurrentDirectory(), "wpt_report.txt");
                                    File.WriteAllText(reportPath, summary);
                                    Console.WriteLine($"Report saved to: {reportPath}");
                                }
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"[WPT] Fatal Error: {ex}");
                            }
                            finally
                            {
                                Console.WriteLine("[WPT] Work complete. Exiting...");
                                Environment.Exit(0);
                            }
                         });
                    };

                    // Run the loop (invisible)
                    wptWm.Run();
                    return;
                }

                // 1. Logging Setup
                FenBrowser.Core.Logging.LogManager.InitializeFromSettings();
                if (args.Contains("--log-level") && args.Length > Array.IndexOf(args, "--log-level") + 1)
                {
                    var levelStr = args[Array.IndexOf(args, "--log-level") + 1];
                    if (Enum.TryParse<FenBrowser.Core.Logging.LogLevel>(levelStr, true, out var level))
                    {
                        FenBrowser.Core.Logging.LogManager.Initialize(true, FenBrowser.Core.Logging.LogCategory.All, level);
                    }
                }

                string initialUrl = args.Length > 0 && !args[0].StartsWith("--") ? args[0] : "https://www.google.com";
                FenLogger.Info($"[Host] Starting FenBrowser with URL: {initialUrl}", LogCategory.General);

                // 2. Engine Config
                CssEngineConfig.CurrentEngine = CssEngineType.Custom;

                // 3. Initialize Window Manager
                var windowManager = WindowManager.Instance;
                windowManager.Initialize(initialUrl);

                // 4. Initialize Chrome Manager (UI)
                // Hook into Window Load event to avoiding init before GL context
                windowManager.OnLoad += () => {
                    ChromeManager.Instance.Initialize(initialUrl);
                };

                // 5. Run Application
                windowManager.Run();
            }
            catch (Exception ex)
            {
                AttachConsole(ATTACH_PARENT_PROCESS); // Ensure crash logs are visible if run from console
                Console.WriteLine($"[Host] Fatal Shutdown: {ex}");
                FenLogger.Error($"[Host] Fatal Shutdown: {ex}", LogCategory.General);
                throw;
            }
        }

        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        private static extern bool AttachConsole(int dwProcessId);
        private const int ATTACH_PARENT_PROCESS = -1;

        // Bridge for legacy static calls from DevTools or other components
        public static Task<T> RunOnMainThread<T>(Func<T> func) => WindowManager.Instance.RunOnMainThread(func);
        public static Task RunOnMainThread(Action action) => WindowManager.Instance.RunOnMainThread(action);
        
        /// <summary>
        /// Copy text to system clipboard. (10/10)
        /// </summary>
        public static void CopyToClipboard(string text)
        {
            WindowManager.Instance.CopyToClipboard(text);
        }
    }
}

