using System;
using System.Threading.Tasks;
using FenBrowser.Core;
using FenBrowser.Core.Logging;
using FenBrowser.Core.Css;
using System.Linq;
using System.Text;
using FenBrowser.FenEngine.Testing;
using System.IO;
using System.Diagnostics;
using System.Threading;
using System.IO.Pipes;
using FenBrowser.Host.ProcessIsolation;
using FenBrowser.Host.ProcessIsolation.Network;
using FenBrowser.Host.ProcessIsolation.Targets;
using System.Collections.Concurrent;
using System.Net.Http;
using FenBrowser.Core.Network;

namespace FenBrowser.Host
{
    /// <summary>
    /// FenBrowser.Host Entry Point.
    /// Bootstraps the application by initializing WindowManager and ChromeManager.
    /// </summary>
    public class Program
    {
        public enum StartupMode
        {
            Browser,
            RendererChild,
            NetworkChild,
            GpuChild,
            UtilityChild,
            Test262,
            Wpt,
            Acid2,
            WebDriver
        }

        public static StartupMode ResolveStartupMode(string[] args, Func<string, string> getEnvironmentVariable = null)
        {
            getEnvironmentVariable ??= Environment.GetEnvironmentVariable;

            if (args.Any(a => string.Equals(a, "--renderer-child", StringComparison.OrdinalIgnoreCase)) ||
                string.Equals(getEnvironmentVariable("FEN_RENDERER_CHILD"), "1", StringComparison.Ordinal))
            {
                return StartupMode.RendererChild;
            }

            if (args.Any(a => string.Equals(a, "--network-child", StringComparison.OrdinalIgnoreCase)) ||
                string.Equals(getEnvironmentVariable("FEN_NETWORK_CHILD"), "1", StringComparison.Ordinal))
            {
                return StartupMode.NetworkChild;
            }

            if (args.Any(a => string.Equals(a, "--gpu-child", StringComparison.OrdinalIgnoreCase)) ||
                string.Equals(getEnvironmentVariable("FEN_GPU_CHILD"), "1", StringComparison.Ordinal))
            {
                return StartupMode.GpuChild;
            }

            if (args.Any(a => string.Equals(a, "--utility-child", StringComparison.OrdinalIgnoreCase)) ||
                string.Equals(getEnvironmentVariable("FEN_UTILITY_CHILD"), "1", StringComparison.Ordinal))
            {
                return StartupMode.UtilityChild;
            }

            if (args.Length >= 2 && args[0] == "--test262")
            {
                return StartupMode.Test262;
            }

            if (args.Length >= 2 && args[0] == "--wpt")
            {
                return StartupMode.Wpt;
            }

            if (args.Length >= 1 && args[0] == "--acid2")
            {
                return StartupMode.Acid2;
            }

            if (args.Any(a => a.StartsWith("--port=", StringComparison.Ordinal)))
            {
                return StartupMode.WebDriver;
            }

            return StartupMode.Browser;
        }

        public static async Task Main(string[] args)
        {
            // Enable High-DPI Awareness (Per-Monitor V2)
            if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows))
            {
                try { SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2); } catch { }
            }

            try
            {
                // Force UTF-8 Console Output if possible (may fail if no console attached)
                try { Console.OutputEncoding = Encoding.UTF8; } catch { }

                // Global Exception Handling
                AppDomain.CurrentDomain.UnhandledException += (sender, e) => {
                    FenLogger.Error($"[CRASH] Unhandled Domain Exception: {e.ExceptionObject}", LogCategory.General);
                };

                TaskScheduler.UnobservedTaskException += (sender, e) => {
                    FenLogger.Error($"[CRASH] Unobserved Task Exception: {e.Exception}", LogCategory.General);
                    e.SetObserved();
                };

                var startupMode = ResolveStartupMode(args);

                // Process-isolation renderer child mode.
                if (startupMode == StartupMode.RendererChild)
                {
                    await RunRendererChildLoopAsync(args).ConfigureAwait(false);
                    return;
                }

                if (startupMode == StartupMode.NetworkChild)
                {
                    await RunNetworkChildLoopAsync().ConfigureAwait(false);
                    return;
                }

                if (startupMode == StartupMode.GpuChild)
                {
                    await RunTargetChildLoopAsync(TargetProcessKind.Gpu).ConfigureAwait(false);
                    return;
                }

                if (startupMode == StartupMode.UtilityChild)
                {
                    await RunTargetChildLoopAsync(TargetProcessKind.Utility).ConfigureAwait(false);
                    return;
                }

                // 0. CLI Tooling Interception
                if (startupMode == StartupMode.Test262)
                {
                    AttachConsole(ATTACH_PARENT_PROCESS);
                    
                    string input = args[1]; // File path OR category
                    string rootPath = args.Length > 2 ? args[2] : Path.Combine(Directory.GetCurrentDirectory(), "test262");
                    var runner = new Test262Runner(rootPath);
                    
                    if (File.Exists(input))
                    {
                        // Run Single File
                        Console.WriteLine($"Running Test262 File: {Path.GetFileName(input)}");
                        var result = await runner.RunSingleTestAsync(input).ConfigureAwait(false);
                        
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
                        
                        var results = await runner.RunCategoryAsync(input, (filename, count) => {
                            if (count % 100 == 0)
                            {
                                // Pad output to overwrite previous lines completely if needed
                                string msg = $"\rProcessed: {count} - {filename}";
                                if (msg.Length < 70) msg = msg.PadRight(70);
                                Console.Write(msg);
                            }
                        }).ConfigureAwait(false);
                        
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
                else if (startupMode == StartupMode.Wpt)
                {
                    AttachConsole(ATTACH_PARENT_PROCESS);
                    string input = args[1]; // File path OR category
                    string rootPath = args.Length > 2 ? args[2] : Path.Combine(Directory.GetCurrentDirectory(), "wpt"); // Default WPT path
                    
                    Console.WriteLine($"[WPT] Initializing Headless Engine for: {input}");

                    // Set WPT Root Path for BrowserHost remapping
                    FenBrowser.FenEngine.Rendering.BrowserHost.WPTRootPath = rootPath;

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

                    wptWm.Run();
                    return;
                }
                else if (startupMode == StartupMode.Acid2)
                {
                    AttachConsole(ATTACH_PARENT_PROCESS);
                    Console.WriteLine("[Acid2] Initializing Headless Engine for Acid2 Test...");

                    // Initialize Headless
                    FenBrowser.Core.Logging.LogManager.InitializeFromSettings();
                    CssEngineConfig.CurrentEngine = CssEngineType.Custom;
                    var wm = WindowManager.Instance;
                    wm.Initialize("about:blank", isHeadless: true);

                    wm.OnLoad += () => {
                        Task.Run(async () => {
                            try
                            {
                                // Initialize ChromeManager (UI)
                                ChromeManager.Instance.Initialize("about:blank");
                                
                                var runner = new AcidTestRunner();
                                var result = await runner.RunAcid2Async(async (url) => {
                                    var innerTask = await FenBrowser.Host.Program.RunOnMainThread(async () => {
                                        try {
                                            Console.WriteLine("[Acid2] Lambda: Checking ActiveTab...");
                                            if (FenBrowser.Host.Tabs.TabManager.Instance.ActiveTab == null) {
                                                Console.WriteLine("[Acid2] Lambda: Creating Tab...");
                                                FenBrowser.Host.Tabs.TabManager.Instance.CreateTab(url);
                                            }
                                            else {
                                                Console.WriteLine("[Acid2] Lambda: Navigating...");
                                                await FenBrowser.Host.Tabs.TabManager.Instance.ActiveTab.NavigateAsync(url);
                                            }
                                            
                                            Console.WriteLine("[Acid2] Lambda: Waiting for render...");
                                            await Task.Delay(2000); 
                                            
                                            Console.WriteLine("[Acid2] Lambda: Capturing screenshot...");
                                            var shot = WindowManager.Instance.CaptureScreenshot();
                                            Console.WriteLine($"[Acid2] Lambda: Screenshot captured (Null? {shot == null})");
                                            return shot;
                                        } catch (Exception lambdaEx) {
                                            Console.WriteLine($"[Acid2] Lambda Error: {lambdaEx}");
                                            throw;
                                        }
                                    });
                                    return await innerTask;
                                });

                                Console.WriteLine($"Result: {(result.Passed ? "PASS" : "FAIL")}");
                                Console.WriteLine($"Message: {result.Message}");
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"[Acid2] Fatal Error: {ex}");
                            }
                            finally
                            {
                                Console.WriteLine("[Acid2] Work complete. Exiting...");
                                Environment.Exit(0);
                            }
                        });
                    };

                    wm.Run();
                    return;
                }
                // WebDriver Mode (e.g., --port=4444)
                var portArg = args.FirstOrDefault(a => a.StartsWith("--port="));
                if (portArg != null)
                {
                    AttachConsole(ATTACH_PARENT_PROCESS);
                    if (int.TryParse(portArg.Split('=')[1], out int port))
                    {
                         Console.WriteLine($"[WebDriver] Initializing on port {port}...");
                         
                         FenBrowser.Core.Logging.LogManager.InitializeFromSettings();
                         CssEngineConfig.CurrentEngine = CssEngineType.Custom;
                         
                         var wm = WindowManager.Instance;
                         bool headless = args.Contains("--headless");
                         wm.Initialize("about:blank", isHeadless: headless);
                         
                         var server = new FenBrowser.WebDriver.WebDriverServer(port);
                         
                         wm.OnLoad += () => {
                             Task.Run(() => {
                                 try
                                 {
                                     ChromeManager.Instance.Initialize("about:blank");
                                     var driver = new FenBrowser.Host.WebDriver.HostBrowserDriver();
                                     server.SetDriver(driver);
                                     server.Start();
                                 }
                                 catch (Exception ex)
                                 {
                                     Console.WriteLine($"[WebDriver] Startup Error: {ex}");
                                     Environment.Exit(1);
                                 }
                             });
                         };
                         
                         wm.Run();
                         server.Dispose();
                         return;
                    }
                }


                // 1. Logging Setup
                FenBrowser.Core.Logging.LogManager.InitializeFromSettings();
                if (args.Contains("--debug-css"))
                {
                    var sb = new StringBuilder();
                    sb.AppendLine("[DEBUG-CSS] Starting CSS Parser Test...");
                    
                    try 
                    {
                        string css = "body { background: white; } " +
                                     ".gb_Cd.gb_Va.gb_od:not(.gb_Md) { color: blue; } " +
                                     ":is(.a, .b, .c) > div { display: none; } " +
                                     "div:not(:where(.x, .y)) { opacity: 0.5; }";
                        sb.AppendLine($"Testing CSS: {css}");

                        var tokenizer = new FenBrowser.FenEngine.Rendering.Css.CssTokenizer(css);
                        var parser = new FenBrowser.FenEngine.Rendering.Css.CssSyntaxParser(tokenizer);
                        var sheet = parser.ParseStylesheet();

                        Console.WriteLine($"Parsed Rules Count: {sheet.Rules.Count}");
                        
                        // Create dummy DOM
                        var root = new FenBrowser.Core.Dom.V2.Element("BODY");
                        var container = new FenBrowser.Core.Dom.V2.Element("DIV");
                        container.SetAttribute("class", "a b c");
                        root.AppendChild(container);
                        
                        var child = new FenBrowser.Core.Dom.V2.Element("DIV");
                        container.AppendChild(child);
                        
                        var target = new FenBrowser.Core.Dom.V2.Element("DIV");
                        target.SetAttribute("class", "gb_Cd gb_Va gb_od");
                        root.AppendChild(target);

                        foreach(var rule in sheet.Rules)
                        {
                            if (rule is FenBrowser.FenEngine.Rendering.Css.CssStyleRule style)
                            {
                                sb.AppendLine($"Rule Selector: {style.Selector?.Raw}");
                                
                                bool matchRoot = FenBrowser.FenEngine.Rendering.Css.SelectorMatcher.Matches(root, style.Selector);
                                bool matchContainer = FenBrowser.FenEngine.Rendering.Css.SelectorMatcher.Matches(container, style.Selector);
                                bool matchChild = FenBrowser.FenEngine.Rendering.Css.SelectorMatcher.Matches(child, style.Selector);
                                bool matchTarget = FenBrowser.FenEngine.Rendering.Css.SelectorMatcher.Matches(target, style.Selector);
                                
                                sb.AppendLine($"  Matches ROOT: {matchRoot}");
                                sb.AppendLine($"  Matches CONTAINER: {matchContainer}");
                                sb.AppendLine($"  Matches CHILD: {matchChild}");
                                sb.AppendLine($"  Matches TARGET: {matchTarget}");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        sb.AppendLine($"CRASH: {ex}");
                    }
                    
                    File.WriteAllText("css_debug.txt", sb.ToString());
                    return;
                }

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

                    // DIAGNOSTIC LOGGING
                    var wm = WindowManager.Instance;
                    FenLogger.Info($"[DPI-CHECK] Physical: {wm.Window.FramebufferSize.X}x{wm.Window.FramebufferSize.Y}", LogCategory.General);
                    FenLogger.Info($"[DPI-CHECK] Logical:  {wm.Window.Size.X}x{wm.Window.Size.Y}", LogCategory.General);
                    FenLogger.Info($"[DPI-CHECK] Scale:    {wm.DpiScale}", LogCategory.General);
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

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool SetProcessDpiAwarenessContext(int dpiContext);
        private const int DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = -4;

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

        private static async Task RunRendererChildLoopAsync(string[] args)
        {
            LogManager.InitializeFromSettings();

            int tabId = 0;
            var tabArg = args.FirstOrDefault(a => a.StartsWith("--tab-id=", StringComparison.OrdinalIgnoreCase));
            if (tabArg != null)
            {
                _ = int.TryParse(tabArg.Split('=')[1], out tabId);
            }
            else
            {
                _ = int.TryParse(Environment.GetEnvironmentVariable("FEN_RENDERER_TAB_ID"), out tabId);
            }

            int parentPid = 0;
            _ = int.TryParse(Environment.GetEnvironmentVariable("FEN_RENDERER_PARENT_PID"), out parentPid);
            var pipeName = Environment.GetEnvironmentVariable("FEN_RENDERER_PIPE_NAME");
            var authToken = Environment.GetEnvironmentVariable("FEN_RENDERER_AUTH_TOKEN");
            var sandboxProfile = Environment.GetEnvironmentVariable("FEN_RENDERER_SANDBOX_PROFILE");
            var capabilitySet = Environment.GetEnvironmentVariable("FEN_RENDERER_CAPABILITIES");
            var assignmentKey = Environment.GetEnvironmentVariable("FEN_RENDERER_ASSIGNMENT_KEY");

            // Shared memory writer for frame delivery. Created lazily on first FrameRequest.
            FenBrowser.Host.ProcessIsolation.FrameSharedMemory frameSharedMemory = null;

            FenLogger.Info($"[RendererChild] Started for tab={tabId}, parentPid={parentPid}, pipe={pipeName}, assignment={assignmentKey}", LogCategory.General);

            if (string.IsNullOrWhiteSpace(pipeName) || string.IsNullOrWhiteSpace(authToken))
            {
                // Compatibility fallback if IPC is not configured.
                while (true)
                {
                    if (!IsParentAlive(parentPid))
                    {
                        break;
                    }
                    await Task.Delay(500).ConfigureAwait(false);
                }
                FenLogger.Info($"[RendererChild] Exiting for tab={tabId}", LogCategory.General);
                return;
            }

            if (!string.Equals(sandboxProfile, "renderer_minimal", StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(capabilitySet, "navigate,input,frame", StringComparison.OrdinalIgnoreCase))
            {
                FenLogger.Warn(
                    $"[RendererChild] Startup policy assertion failed for tab={tabId}. sandboxProfile={sandboxProfile}, capabilities={capabilitySet}.",
                    LogCategory.General);
                return;
            }

            using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                pipe.Connect(5000);
            }
            catch (Exception ex)
            {
                FenLogger.Warn($"[RendererChild] Failed to connect IPC pipe '{pipeName}': {ex.Message}", LogCategory.General);
                return;
            }

            using var reader = new StreamReader(pipe, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: true);
            using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };
            using var browser = new FenBrowser.FenEngine.Rendering.BrowserHost();

            bool handshakeComplete = false;
            bool running = true;

            while (running)
            {
                if (!IsParentAlive(parentPid))
                {
                    break;
                }

                var readResult = await RendererChildLoopIo.ReadLineWithTimeoutAsync(
                    reader,
                    TimeSpan.FromMilliseconds(500)).ConfigureAwait(false);
                if (!readResult.Completed)
                {
                    continue;
                }

                var line = readResult.Line;
                if (line == null)
                {
                    break;
                }

                if (!RendererIpc.TryDeserializeEnvelope(line, out var envelope))
                {
                    continue;
                }

                try
                {
                    if (string.Equals(envelope.Type, RendererIpcMessageType.Hello.ToString(), StringComparison.OrdinalIgnoreCase))
                    {
                        if (!string.Equals(envelope.Token, authToken, StringComparison.Ordinal))
                        {
                            SendRendererEnvelope(writer, new RendererIpcEnvelope
                            {
                                Type = RendererIpcMessageType.Error.ToString(),
                                TabId = tabId,
                                CorrelationId = envelope.CorrelationId,
                                Payload = "authentication_failed"
                            });
                            break;
                        }

                        handshakeComplete = true;
                        SendRendererEnvelope(writer, new RendererIpcEnvelope
                        {
                            Type = RendererIpcMessageType.Ready.ToString(),
                            TabId = tabId,
                            CorrelationId = envelope.CorrelationId
                        });
                        continue;
                    }

                    if (!handshakeComplete)
                    {
                        continue;
                    }

                    if (string.Equals(envelope.Type, RendererIpcMessageType.Navigate.ToString(), StringComparison.OrdinalIgnoreCase))
                    {
                        var payload = RendererIpc.DeserializePayload<RendererNavigatePayload>(envelope);
                        var url = payload?.Url ?? string.Empty;
                        if (!string.IsNullOrWhiteSpace(url))
                        {
                            if (payload.IsUserInput)
                                await browser.NavigateUserInputAsync(url).ConfigureAwait(false);
                            else
                                await browser.NavigateAsync(url).ConfigureAwait(false);
                        }

                        SendRendererEnvelope(writer, new RendererIpcEnvelope
                        {
                            Type = RendererIpcMessageType.Ack.ToString(),
                            TabId = tabId,
                            CorrelationId = envelope.CorrelationId
                        });
                        continue;
                    }

                    if (string.Equals(envelope.Type, RendererIpcMessageType.Input.ToString(), StringComparison.OrdinalIgnoreCase))
                    {
                        var input = RendererIpc.DeserializePayload<RendererInputEvent>(envelope);
                        if (input != null)
                        {
                            switch (input.Type)
                            {
                                case RendererInputEventType.MouseDown:
                                    browser.OnMouseDown(input.X, input.Y, input.Button);
                                    break;
                                case RendererInputEventType.MouseUp:
                                    browser.OnMouseUp(input.X, input.Y, input.Button);
                                    if (input.EmitClick && input.Button == 0)
                                    {
                                        browser.OnClick(input.X, input.Y, input.Button);
                                    }
                                    break;
                                case RendererInputEventType.MouseMove:
                                    browser.OnMouseMove(input.X, input.Y);
                                    break;
                                case RendererInputEventType.KeyDown:
                                    if (!string.IsNullOrWhiteSpace(input.Key))
                                    {
                                        await browser.HandleKeyPress(MapRendererKey(input.Key)).ConfigureAwait(false);
                                    }
                                    break;
                                case RendererInputEventType.TextInput:
                                    if (!string.IsNullOrWhiteSpace(input.Text))
                                    {
                                        await browser.HandleKeyPress(input.Text).ConfigureAwait(false);
                                    }
                                    break;
                                case RendererInputEventType.MouseWheel:
                                    // BrowserHost currently has no direct wheel-input API; host applies scroll locally.
                                    break;
                            }
                        }

                        SendRendererEnvelope(writer, new RendererIpcEnvelope
                        {
                            Type = RendererIpcMessageType.Ack.ToString(),
                            TabId = tabId,
                            CorrelationId = envelope.CorrelationId
                        });
                        continue;
                    }

                    if (string.Equals(envelope.Type, RendererIpcMessageType.FrameRequest.ToString(), StringComparison.OrdinalIgnoreCase))
                    {
                        var frameRequest = RendererIpc.DeserializePayload<RendererFrameRequestPayload>(envelope);
                        float vpWidth = frameRequest?.ViewportWidth ?? 1280f;
                        float vpHeight = frameRequest?.ViewportHeight ?? 720f;

                        // Clamp to sane range.
                        vpWidth = Math.Max(1f, Math.Min(vpWidth, FenBrowser.Host.ProcessIsolation.FrameSharedMemory.MaxWidth));
                        vpHeight = Math.Max(1f, Math.Min(vpHeight, FenBrowser.Host.ProcessIsolation.FrameSharedMemory.MaxHeight));
                        int iWidth = (int)vpWidth;
                        int iHeight = (int)vpHeight;

                        float actualWidth = vpWidth;
                        float actualHeight = vpHeight;
                        uint seqNum = 0;

                        // Lazily create the shared memory writer on first FrameRequest.
                        if (frameSharedMemory == null)
                        {
                            frameSharedMemory = FenBrowser.Host.ProcessIsolation.FrameSharedMemory.CreateForWriter(tabId, parentPid);
                        }

                        if (frameSharedMemory != null)
                        {
                            try
                            {
                                var domRoot = browser.GetDomRoot();
                                var styles = browser.ComputedStyles;

                                if (domRoot != null)
                                {
                                    var imageInfo = new SkiaSharp.SKImageInfo(iWidth, iHeight, SkiaSharp.SKColorType.Bgra8888, SkiaSharp.SKAlphaType.Premul);
                                    using var bitmap = new SkiaSharp.SKBitmap(imageInfo);
                                    using var canvas = new SkiaSharp.SKCanvas(bitmap);
                                    canvas.Clear(SkiaSharp.SKColors.White);

                                    var viewport = new SkiaSharp.SKRect(0, 0, vpWidth, vpHeight);
                                    var childRenderer = new FenBrowser.FenEngine.Rendering.SkiaDomRenderer();
                                    childRenderer.Render(
                                        domRoot,
                                        canvas,
                                        styles != null ? new System.Collections.Generic.Dictionary<FenBrowser.Core.Dom.V2.Node, FenBrowser.Core.Css.CssComputed>(styles) : new System.Collections.Generic.Dictionary<FenBrowser.Core.Dom.V2.Node, FenBrowser.Core.Css.CssComputed>(),
                                        viewport,
                                        browser.CurrentUri?.AbsoluteUri);
                                    canvas.Flush();

                                    // GetPixelSpan() is a ref struct; copy to byte[] to avoid
                                    // "ref struct in async method" language restriction.
                                    int byteCount = iWidth * iHeight * 4;
                                    var pixelBytes = new byte[byteCount];
                                    System.Runtime.InteropServices.Marshal.Copy(
                                        bitmap.GetPixels(), pixelBytes, 0, byteCount);
                                    frameSharedMemory.WriteFrame(iWidth, iHeight, pixelBytes);
                                    frameSharedMemory.SignalReady();
                                    seqNum = 1; // Approximate; actual seq tracked inside WriteFrame.
                                    FenLogger.Debug($"[RendererChild] Frame written to shared memory: {iWidth}x{iHeight} for tab={tabId}", LogCategory.Rendering);
                                }
                                else
                                {
                                    FenLogger.Debug($"[RendererChild] No DOM root for tab={tabId}; skipping frame write.", LogCategory.Rendering);
                                }
                            }
                            catch (Exception renderEx)
                            {
                                FenLogger.Warn($"[RendererChild] Frame render failed for tab={tabId}: {renderEx.Message}", LogCategory.Rendering);
                            }
                        }

                        var payload = new RendererFrameReadyPayload
                        {
                            Url = browser.CurrentUri?.AbsoluteUri ?? "about:blank",
                            FrameTimestampUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                            SurfaceWidth = actualWidth,
                            SurfaceHeight = actualHeight,
                            DirtyRegionCount = 1,
                            HasDamage = true,
                            FrameSequenceNumber = seqNum
                        };

                        SendRendererEnvelope(writer, new RendererIpcEnvelope
                        {
                            Type = RendererIpcMessageType.FrameReady.ToString(),
                            TabId = tabId,
                            CorrelationId = envelope.CorrelationId,
                            Payload = RendererIpc.SerializePayload(payload)
                        });
                        continue;
                    }

                    if (string.Equals(envelope.Type, RendererIpcMessageType.Shutdown.ToString(), StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(envelope.Type, RendererIpcMessageType.TabClosed.ToString(), StringComparison.OrdinalIgnoreCase))
                    {
                        running = false;
                        continue;
                    }

                    if (string.Equals(envelope.Type, RendererIpcMessageType.Ping.ToString(), StringComparison.OrdinalIgnoreCase))
                    {
                        SendRendererEnvelope(writer, new RendererIpcEnvelope
                        {
                            Type = RendererIpcMessageType.Pong.ToString(),
                            TabId = tabId,
                            CorrelationId = envelope.CorrelationId
                        });
                    }
                }
                catch (Exception ex)
                {
                    SendRendererEnvelope(writer, new RendererIpcEnvelope
                    {
                        Type = RendererIpcMessageType.Error.ToString(),
                        TabId = tabId,
                        CorrelationId = envelope.CorrelationId,
                        Payload = ex.Message
                    });
                }
            }

            frameSharedMemory?.Dispose();
            FenLogger.Info($"[RendererChild] Exiting for tab={tabId}", LogCategory.General);
        }

        private static async Task RunNetworkChildLoopAsync()
        {
            var pipeName = Environment.GetEnvironmentVariable("FEN_NETWORK_PIPE_NAME");
            var authToken = Environment.GetEnvironmentVariable("FEN_NETWORK_AUTH_TOKEN");
            var parentPidRaw = Environment.GetEnvironmentVariable("FEN_NETWORK_PARENT_PID");
            var sandboxProfile = Environment.GetEnvironmentVariable("FEN_NETWORK_SANDBOX_PROFILE");
            var capabilitySet = Environment.GetEnvironmentVariable("FEN_NETWORK_CAPABILITIES");
            var parentPid = int.TryParse(parentPidRaw, out var parsedParentPid) ? parsedParentPid : 0;

            if (string.IsNullOrWhiteSpace(pipeName) || string.IsNullOrWhiteSpace(authToken))
            {
                FenLogger.Warn("[NetworkChild] Missing required startup environment.", LogCategory.General);
                return;
            }

            if (!string.Equals(sandboxProfile, "network_process", StringComparison.OrdinalIgnoreCase))
            {
                FenLogger.Warn(
                    $"[NetworkChild] Startup policy assertion failed. sandboxProfile={sandboxProfile}, capabilities={capabilitySet}.",
                    LogCategory.General);
                return;
            }

            using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                pipe.Connect(5000);
            }
            catch (Exception ex)
            {
                FenLogger.Warn($"[NetworkChild] Failed to connect IPC pipe '{pipeName}': {ex.Message}", LogCategory.General);
                return;
            }

            using var reader = new StreamReader(pipe, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: true);
            using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };
            using var httpClient = HttpClientFactory.CreateClient();
            using var noProxyHandler = HttpClientFactory.CreateHandler();
            using var noProxyClient = HttpClientFactory.CreateClient(noProxyHandler);
            var activeRequests = new ConcurrentDictionary<string, CancellationTokenSource>(StringComparer.Ordinal);
            bool handshakeComplete = false;
            bool running = true;

            noProxyHandler.UseProxy = false;

            while (running)
            {
                if (!IsParentAlive(parentPid))
                {
                    break;
                }

                var readResult = await RendererChildLoopIo.ReadLineWithTimeoutAsync(
                    reader,
                    TimeSpan.FromMilliseconds(500)).ConfigureAwait(false);
                if (!readResult.Completed)
                {
                    continue;
                }

                var line = readResult.Line;
                if (line == null)
                {
                    break;
                }

                if (!NetworkIpc.TryDeserialize(line, out var envelope))
                {
                    continue;
                }

                try
                {
                    if (string.Equals(envelope.Type, NetworkIpcMessageType.Hello.ToString(), StringComparison.OrdinalIgnoreCase))
                    {
                        if (!string.Equals(envelope.CapabilityToken, authToken, StringComparison.Ordinal))
                        {
                            SendNetworkEnvelope(writer, new NetworkIpcEnvelope
                            {
                                Type = NetworkIpcMessageType.Error.ToString(),
                                RequestId = envelope.RequestId,
                                Payload = "authentication_failed"
                            });
                            break;
                        }

                        handshakeComplete = true;
                        SendNetworkEnvelope(writer, new NetworkIpcEnvelope
                        {
                            Type = NetworkIpcMessageType.Ready.ToString(),
                            RequestId = envelope.RequestId
                        });
                        continue;
                    }

                    if (!handshakeComplete)
                    {
                        continue;
                    }

                    if (string.Equals(envelope.Type, NetworkIpcMessageType.FetchRequest.ToString(), StringComparison.OrdinalIgnoreCase))
                    {
                        var payload = NetworkIpc.DeserializePayload<NetworkFetchRequestPayload>(envelope);
                        if (payload == null || string.IsNullOrWhiteSpace(payload.Url))
                        {
                            SendFetchFailure(writer, envelope.RequestId, "invalid_request", "Missing fetch URL.");
                            continue;
                        }

                        var linkedCts = new CancellationTokenSource();
                        activeRequests[envelope.RequestId] = linkedCts;
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                using var request = BuildNetworkChildRequest(payload);
                                using var response = await SendNetworkRequestAsync(httpClient, noProxyClient, request, linkedCts.Token).ConfigureAwait(false);

                                var headers = new System.Collections.Generic.Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                                foreach (var header in response.Headers)
                                {
                                    headers[header.Key] = string.Join(", ", header.Value);
                                }

                                foreach (var header in response.Content.Headers)
                                {
                                    headers[header.Key] = string.Join(", ", header.Value);
                                }

                                SendNetworkEnvelope(writer, new NetworkIpcEnvelope
                                {
                                    Type = NetworkIpcMessageType.FetchResponseHead.ToString(),
                                    RequestId = envelope.RequestId,
                                    CapabilityToken = envelope.CapabilityToken,
                                    Payload = NetworkIpc.SerializePayload(new NetworkFetchResponseHeadPayload
                                    {
                                        RequestId = envelope.RequestId,
                                        StatusCode = (int)response.StatusCode,
                                        StatusText = response.ReasonPhrase ?? string.Empty,
                                        Headers = headers,
                                        Url = response.RequestMessage?.RequestUri?.AbsoluteUri ?? payload.Url,
                                        ResponseType = "basic",
                                        Cors = string.Equals(payload.Mode, "cors", StringComparison.OrdinalIgnoreCase),
                                        Opaque = string.Equals(payload.Mode, "no-cors", StringComparison.OrdinalIgnoreCase),
                                        ContentLength = response.Content.Headers.ContentLength ?? -1
                                    })
                                });

                                using var bodyStream = await response.Content.ReadAsStreamAsync(linkedCts.Token).ConfigureAwait(false);
                                var buffer = new byte[16 * 1024];
                                int chunkIndex = 0;
                                long bytesTotal = 0;
                                while (true)
                                {
                                    var read = await bodyStream.ReadAsync(buffer, 0, buffer.Length, linkedCts.Token).ConfigureAwait(false);
                                    if (read <= 0)
                                    {
                                        break;
                                    }

                                    bytesTotal += read;
                                    var chunk = new byte[read];
                                    Buffer.BlockCopy(buffer, 0, chunk, 0, read);
                                    SendNetworkEnvelope(writer, new NetworkIpcEnvelope
                                    {
                                        Type = NetworkIpcMessageType.FetchResponseBody.ToString(),
                                        RequestId = envelope.RequestId,
                                        CapabilityToken = envelope.CapabilityToken,
                                        Payload = NetworkIpc.SerializePayload(new NetworkFetchResponseBodyPayload
                                        {
                                            RequestId = envelope.RequestId,
                                            IsComplete = false,
                                            ChunkIndex = chunkIndex++,
                                            BodyChunkBase64 = Convert.ToBase64String(chunk),
                                            BytesTotal = bytesTotal
                                        })
                                    });
                                }

                                SendNetworkEnvelope(writer, new NetworkIpcEnvelope
                                {
                                    Type = NetworkIpcMessageType.FetchResponseBody.ToString(),
                                    RequestId = envelope.RequestId,
                                    CapabilityToken = envelope.CapabilityToken,
                                    Payload = NetworkIpc.SerializePayload(new NetworkFetchResponseBodyPayload
                                    {
                                        RequestId = envelope.RequestId,
                                        IsComplete = true,
                                        ChunkIndex = chunkIndex,
                                        BodyChunkBase64 = string.Empty,
                                        BytesTotal = bytesTotal
                                    })
                                });
                            }
                            catch (OperationCanceledException)
                            {
                                SendFetchFailure(writer, envelope.RequestId, "cancelled", "Request cancelled.");
                            }
                            catch (Exception ex)
                            {
                                SendFetchFailure(writer, envelope.RequestId, "fetch_failed", ex.Message);
                            }
                            finally
                            {
                                if (activeRequests.TryRemove(envelope.RequestId, out var cts))
                                {
                                    cts.Dispose();
                                }
                            }
                        });

                        continue;
                    }

                    if (string.Equals(envelope.Type, NetworkIpcMessageType.CancelRequest.ToString(), StringComparison.OrdinalIgnoreCase))
                    {
                        if (activeRequests.TryRemove(envelope.RequestId, out var cts))
                        {
                            cts.Cancel();
                            cts.Dispose();
                        }
                        continue;
                    }

                    if (string.Equals(envelope.Type, NetworkIpcMessageType.Ping.ToString(), StringComparison.OrdinalIgnoreCase))
                    {
                        SendNetworkEnvelope(writer, new NetworkIpcEnvelope
                        {
                            Type = NetworkIpcMessageType.Pong.ToString(),
                            RequestId = envelope.RequestId
                        });
                        continue;
                    }

                    if (string.Equals(envelope.Type, NetworkIpcMessageType.Shutdown.ToString(), StringComparison.OrdinalIgnoreCase))
                    {
                        running = false;
                        continue;
                    }
                }
                catch (Exception ex)
                {
                    SendNetworkEnvelope(writer, new NetworkIpcEnvelope
                    {
                        Type = NetworkIpcMessageType.Error.ToString(),
                        RequestId = envelope.RequestId,
                        Payload = ex.Message
                    });
                }
            }

            foreach (var kvp in activeRequests)
            {
                try { kvp.Value.Cancel(); } catch { }
                try { kvp.Value.Dispose(); } catch { }
            }

            FenLogger.Info("[NetworkChild] Exiting.", LogCategory.Network);
        }

        private static async Task RunTargetChildLoopAsync(TargetProcessKind expectedKind)
        {
            var pipeName = Environment.GetEnvironmentVariable("FEN_TARGET_PIPE_NAME");
            var authToken = Environment.GetEnvironmentVariable("FEN_TARGET_AUTH_TOKEN");
            var parentPidRaw = Environment.GetEnvironmentVariable("FEN_TARGET_PARENT_PID");
            var targetKindRaw = Environment.GetEnvironmentVariable("FEN_TARGET_KIND");
            var sandboxProfile = Environment.GetEnvironmentVariable("FEN_TARGET_SANDBOX_PROFILE");
            var capabilitySet = Environment.GetEnvironmentVariable("FEN_TARGET_CAPABILITIES");
            var parentPid = int.TryParse(parentPidRaw, out var parsedParentPid) ? parsedParentPid : 0;

            if (string.IsNullOrWhiteSpace(pipeName) || string.IsNullOrWhiteSpace(authToken))
            {
                FenLogger.Warn($"[{expectedKind}Child] Missing required startup environment.", LogCategory.General);
                return;
            }

            if (!string.Equals(targetKindRaw, expectedKind.ToString().ToLowerInvariant(), StringComparison.OrdinalIgnoreCase))
            {
                FenLogger.Warn($"[{expectedKind}Child] Startup kind assertion failed. targetKind={targetKindRaw}.", LogCategory.General);
                return;
            }

            var expectedSandboxProfile = expectedKind == TargetProcessKind.Gpu ? "gpu_process" : "utility_process";
            if (!string.Equals(sandboxProfile, expectedSandboxProfile, StringComparison.OrdinalIgnoreCase))
            {
                FenLogger.Warn(
                    $"[{expectedKind}Child] Startup policy assertion failed. sandboxProfile={sandboxProfile}, capabilities={capabilitySet}.",
                    LogCategory.General);
                return;
            }

            using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                pipe.Connect(5000);
            }
            catch (Exception ex)
            {
                FenLogger.Warn($"[{expectedKind}Child] Failed to connect IPC pipe '{pipeName}': {ex.Message}", LogCategory.General);
                return;
            }

            using var reader = new StreamReader(pipe, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: true);
            using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };

            var handshakeComplete = false;
            var running = true;
            while (running)
            {
                if (!IsParentAlive(parentPid))
                {
                    break;
                }

                var readResult = await RendererChildLoopIo.ReadLineWithTimeoutAsync(
                    reader,
                    TimeSpan.FromMilliseconds(500)).ConfigureAwait(false);
                if (!readResult.Completed)
                {
                    continue;
                }

                var line = readResult.Line;
                if (line == null)
                {
                    break;
                }

                if (!TargetIpc.TryDeserialize(line, out var envelope))
                {
                    continue;
                }

                try
                {
                    if (string.Equals(envelope.Type, TargetIpcMessageType.Hello.ToString(), StringComparison.OrdinalIgnoreCase))
                    {
                        if (!string.Equals(envelope.CapabilityToken, authToken, StringComparison.Ordinal))
                        {
                            SendTargetEnvelope(writer, new TargetIpcEnvelope
                            {
                                Type = TargetIpcMessageType.Error.ToString(),
                                RequestId = envelope.RequestId,
                                Payload = "authentication_failed"
                            });
                            break;
                        }

                        handshakeComplete = true;
                        SendTargetEnvelope(writer, new TargetIpcEnvelope
                        {
                            Type = TargetIpcMessageType.Ready.ToString(),
                            RequestId = envelope.RequestId,
                            Payload = TargetIpc.SerializePayload(new TargetReadyPayload
                            {
                                ProcessKind = expectedKind.ToString().ToLowerInvariant(),
                                SandboxProfile = sandboxProfile ?? string.Empty,
                                Capabilities = capabilitySet ?? string.Empty,
                                ProcessId = Environment.ProcessId
                            })
                        });
                        continue;
                    }

                    if (!handshakeComplete)
                    {
                        continue;
                    }

                    if (string.Equals(envelope.Type, TargetIpcMessageType.Ping.ToString(), StringComparison.OrdinalIgnoreCase))
                    {
                        SendTargetEnvelope(writer, new TargetIpcEnvelope
                        {
                            Type = TargetIpcMessageType.Pong.ToString(),
                            RequestId = envelope.RequestId
                        });
                        continue;
                    }

                    if (string.Equals(envelope.Type, TargetIpcMessageType.Shutdown.ToString(), StringComparison.OrdinalIgnoreCase))
                    {
                        running = false;
                        continue;
                    }
                }
                catch (Exception ex)
                {
                    SendTargetEnvelope(writer, new TargetIpcEnvelope
                    {
                        Type = TargetIpcMessageType.Error.ToString(),
                        RequestId = envelope.RequestId,
                        Payload = ex.Message
                    });
                }
            }

            FenLogger.Info($"[{expectedKind}Child] Exiting.", LogCategory.General);
        }

        private static bool IsParentAlive(int parentPid)
        {
            if (parentPid <= 0)
            {
                return true;
            }

            try
            {
                var parent = Process.GetProcessById(parentPid);
                return !parent.HasExited;
            }
            catch
            {
                return false;
            }
        }

        private static void SendRendererEnvelope(StreamWriter writer, RendererIpcEnvelope envelope)
        {
            if (writer == null || envelope == null)
            {
                return;
            }

            try
            {
                writer.WriteLine(RendererIpc.SerializeEnvelope(envelope));
                writer.Flush();
            }
            catch
            {
            }
        }

        private static void SendNetworkEnvelope(StreamWriter writer, NetworkIpcEnvelope envelope)
        {
            if (writer == null || envelope == null)
            {
                return;
            }

            try
            {
                writer.WriteLine(NetworkIpc.Serialize(envelope));
                writer.Flush();
            }
            catch
            {
            }
        }

        private static void SendTargetEnvelope(StreamWriter writer, TargetIpcEnvelope envelope)
        {
            if (writer == null || envelope == null)
            {
                return;
            }

            try
            {
                writer.WriteLine(TargetIpc.Serialize(envelope));
                writer.Flush();
            }
            catch
            {
            }
        }

        private static void SendFetchFailure(StreamWriter writer, string requestId, string errorCode, string errorMessage)
        {
            SendNetworkEnvelope(writer, new NetworkIpcEnvelope
            {
                Type = NetworkIpcMessageType.FetchFailed.ToString(),
                RequestId = requestId,
                Payload = NetworkIpc.SerializePayload(new NetworkFetchFailedPayload
                {
                    RequestId = requestId,
                    ErrorCode = errorCode,
                    ErrorMessage = errorMessage ?? string.Empty
                })
            });
        }

        private static HttpRequestMessage BuildNetworkChildRequest(NetworkFetchRequestPayload payload)
        {
            var request = new HttpRequestMessage(
                new HttpMethod(string.IsNullOrWhiteSpace(payload.Method) ? "GET" : payload.Method),
                payload.Url);

            if (payload.Headers != null)
            {
                foreach (var header in payload.Headers)
                {
                    if (!request.Headers.TryAddWithoutValidation(header.Key, header.Value))
                    {
                        request.Content ??= new ByteArrayContent(Array.Empty<byte>());
                        request.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
                    }
                }
            }

            if (!string.IsNullOrWhiteSpace(payload.BodyBase64))
            {
                var bodyBytes = Convert.FromBase64String(payload.BodyBase64);
                request.Content = new ByteArrayContent(bodyBytes);
            }

            return request;
        }

        private static async Task<HttpResponseMessage> SendNetworkRequestAsync(
            HttpClient httpClient,
            HttpClient noProxyClient,
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            try
            {
                return await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException ex) when (IsLoopbackProxyRefusal(ex))
            {
                using var retryRequest = await CloneHttpRequestMessageAsync(request).ConfigureAwait(false);
                return await noProxyClient.SendAsync(retryRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            }
        }

        private static bool IsLoopbackProxyRefusal(HttpRequestException ex)
        {
            var msg = ex?.ToString() ?? string.Empty;
            if (msg.Length == 0) return false;
            return (msg.IndexOf("127.0.0.1:9", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    msg.IndexOf("localhost:9", StringComparison.OrdinalIgnoreCase) >= 0) &&
                   msg.IndexOf("refused", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static async Task<HttpRequestMessage> CloneHttpRequestMessageAsync(HttpRequestMessage source)
        {
            var clone = new HttpRequestMessage(source.Method, source.RequestUri)
            {
                Version = source.Version,
                VersionPolicy = source.VersionPolicy
            };

            foreach (var header in source.Headers)
            {
                clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            if (source.Content != null)
            {
                var bytes = await source.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                var content = new ByteArrayContent(bytes);
                foreach (var header in source.Content.Headers)
                {
                    content.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }
                clone.Content = content;
            }

            return clone;
        }

        private static string MapRendererKey(string key)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                return key;
            }

            return key switch
            {
                "Backspace" => "Backspace",
                "Enter" => "Enter",
                "Delete" => "Delete",
                "Left" => "ArrowLeft",
                "Right" => "ArrowRight",
                "Home" => "Home",
                "End" => "End",
                _ => key
            };
        }
    }
}

