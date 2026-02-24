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

                // Process-isolation renderer child mode.
                if (args.Any(a => string.Equals(a, "--renderer-child", StringComparison.OrdinalIgnoreCase)) ||
                    string.Equals(Environment.GetEnvironmentVariable("FEN_RENDERER_CHILD"), "1", StringComparison.Ordinal))
                {
                    RunRendererChildLoop(args);
                    return;
                }

                // 0. CLI Tooling Interception
                if (args.Length >= 2 && args[0] == "--test262")
                {
                    AttachConsole(ATTACH_PARENT_PROCESS);
                    
                    string input = args[1]; // File path OR category
                    string rootPath = args.Length > 2 ? args[2] : Path.Combine(Directory.GetCurrentDirectory(), "test262");
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
                else if (args.Length >= 1 && args[0] == "--acid2")
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

        private static void RunRendererChildLoop(string[] args)
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
                    Thread.Sleep(500);
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

                var readTask = reader.ReadLineAsync();
                if (!readTask.Wait(500))
                {
                    continue;
                }

                var line = readTask.Result;
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
                                browser.NavigateUserInputAsync(url).GetAwaiter().GetResult();
                            else
                                browser.NavigateAsync(url).GetAwaiter().GetResult();
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
                                        browser.HandleKeyPress(MapRendererKey(input.Key)).GetAwaiter().GetResult();
                                    }
                                    break;
                                case RendererInputEventType.TextInput:
                                    if (!string.IsNullOrWhiteSpace(input.Text))
                                    {
                                        browser.HandleKeyPress(input.Text).GetAwaiter().GetResult();
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
                        var payload = new RendererFrameReadyPayload
                        {
                            Url = browser.CurrentUri?.AbsoluteUri ?? "about:blank",
                            FrameTimestampUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                            SurfaceWidth = frameRequest?.ViewportWidth ?? 0f,
                            SurfaceHeight = frameRequest?.ViewportHeight ?? 0f,
                            DirtyRegionCount = 1,
                            HasDamage = true
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

            FenLogger.Info($"[RendererChild] Exiting for tab={tabId}", LogCategory.General);
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

