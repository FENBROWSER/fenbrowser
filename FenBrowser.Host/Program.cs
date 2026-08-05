using System;
using System.Threading.Tasks;
using FenBrowser.Core;
using FenBrowser.Core.Cache;
using FenBrowser.Core.Logging;
using FenBrowser.Core.Css;
using System.Linq;
using System.Text;
using System.IO;
using System.Diagnostics;
using System.Threading;
using System.IO.Pipes;
using FenBrowser.Host.ProcessIsolation;
using FenBrowser.Host.ProcessIsolation.Gpu;
using FenBrowser.Host.ProcessIsolation.Network;
using FenBrowser.Host.ProcessIsolation.Targets;
using FenBrowser.Host.ProcessIsolation.Utility;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.Http;
using FenBrowser.Core.Network;
using FenBrowser.FenEngine.Rendering;
using FenBrowser.FenEngine.Adapters;
using FenBrowser.FenEngine.Typography;
using SkiaSharp;
using SkiaSharp.HarfBuzz;
using FenBrowser.DependencyInjection;
using FenBrowser.Host.Platform;

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
            UtilityChild
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

            return StartupMode.Browser;
        }

        // Sentinel so the relaunch shim does not recurse if a hosting environment
        // already gave us a fat-enough stack.
        private const string LargeStackEnvVar = "FENBROWSER_LARGE_STACK_HOSTED";
        private const int LargeStackBytes = 16 * 1024 * 1024;
        internal const float BrokeredFrameScrollOverdrawFraction = 0.5f;
        internal const float BrokeredFrameScrollOverdrawMinPixels = 128f;
        internal const float BrokeredFrameScrollOverdrawMaxPixels = 512f;

        internal static float ComputeBrokeredFrameRasterHeight(float viewportHeight)
        {
            if (!float.IsFinite(viewportHeight) || viewportHeight <= 1f)
            {
                return 1f;
            }

            float visibleHeight = Math.Min(viewportHeight, FenBrowser.Host.ProcessIsolation.FrameSharedMemory.MaxHeight);
            float overdrawHeight = Math.Clamp(
                visibleHeight * BrokeredFrameScrollOverdrawFraction,
                BrokeredFrameScrollOverdrawMinPixels,
                BrokeredFrameScrollOverdrawMaxPixels);

            return Math.Min(
                FenBrowser.Host.ProcessIsolation.FrameSharedMemory.MaxHeight,
                visibleHeight + overdrawHeight);
        }

        public static async Task Main(string[] args)
        {
            // The .NET main thread inherits its stack from the PE header
            // SizeOfStackReserve, which is 1 MB by default on x64 Windows.
            // Modern SPA JS frameworks (React/Vue/Angular) recurse through
            // paint/layout/event chains on this thread and blow the 1 MB cap,
            // producing silent-exit StackOverflowExceptions that don't even
            // raise a managed handler. To eliminate that whole class of failure
            // regardless of launcher (VS F5, `dotnet run`, direct .exe, editbin
            // patched or not), re-enter Main on a worker thread with an explicit
            // 16 MB stack. Keep the original main thread blocked on the worker
            // so the process lifetime, console handlers, and CTRL-C semantics
            // continue to be owned by it.
            if (Environment.GetEnvironmentVariable(LargeStackEnvVar) != "1")
            {
                Environment.SetEnvironmentVariable(LargeStackEnvVar, "1");
                int exitCode = 0;
                Exception capturedException = null;
                var worker = new Thread(
                    () =>
                    {
                        try
                        {
                            MainCore(args).GetAwaiter().GetResult();
                        }
                        catch (Exception ex)
                        {
                            capturedException = ex;
                            exitCode = 1;
                        }
                    },
                    LargeStackBytes)
                {
                    Name = "FenBrowser-Main",
                    IsBackground = false
                };
                worker.Start();
                worker.Join();
                if (capturedException != null)
                {
                    // Surface the underlying error the same way an unhandled
                    // exception on the original main thread would have.
                    throw capturedException;
                }
                Environment.ExitCode = exitCode;
                return;
            }

            await MainCore(args).ConfigureAwait(false);
        }

        private static async Task MainCore(string[] args)
        {
            // Enable High-DPI Awareness (Per-Monitor V2)
            if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows))
            {
                try
                {
                    SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
                }
                catch (Exception ex)
                {
                    EngineLog.Write(LogSubsystem.General, LogSeverity.Debug, $"[Startup] DPI awareness setup skipped: {ex.Message}");
                }
            }

            try
            {
                // Force UTF-8 Console Output if possible (may fail if no console attached)
                try
                {
                    Console.OutputEncoding = Encoding.UTF8;
                }
                catch (Exception ex)
                {
                    EngineLog.Write(LogSubsystem.General, LogSeverity.Debug, $"[Startup] Console UTF-8 setup skipped: {ex.Message}");
                }

                // Global Exception Handling
                AppDomain.CurrentDomain.UnhandledException += (sender, e) => {
                    EngineLog.Write(LogSubsystem.General, LogSeverity.Error, $"[CRASH] Unhandled Domain Exception: {e.ExceptionObject}", LogMarker.Invariant);
                    TryExportCrashBundle("Unhandled domain exception", e.ExceptionObject as Exception);
                };

                TaskScheduler.UnobservedTaskException += (sender, e) => {
                    EngineLog.Write(LogSubsystem.General, LogSeverity.Error, $"[CRASH] Unobserved Task Exception: {e.Exception}", LogMarker.EngineBug);
                    TryExportCrashBundle("Unobserved task exception", e.Exception);
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

                // 1. Logging Setup
                EngineLog.InitializeFromSettings();
                RejectToolingArguments(args);

                if (args.Contains("--log-level") && args.Length > Array.IndexOf(args, "--log-level") + 1)
                {
                    var levelStr = args[Array.IndexOf(args, "--log-level") + 1];
                    if (Enum.TryParse<FenBrowser.Core.Logging.LogLevel>(levelStr, true, out var level))
                    {
                        FenBrowser.Core.Logging.LogManager.Initialize(true, FenBrowser.Core.Logging.LogCategory.All, level);
                    }
                }

                string initialUrl = args.Length > 0 && !args[0].StartsWith("--") ? args[0] : "https://www.google.com";
                EngineLog.Write(LogSubsystem.General, LogSeverity.Info, $"[Host] Starting FenBrowser with URL: {initialUrl}");

                // 2. Engine Config
                CssEngineConfig.CurrentEngine = CssEngineType.Custom;

                // Initialize DI Container
                var container = new FenBrowser.DependencyInjection.ServiceContainer();
                container.AddCoreServices();

                // Create platform host
                var platformHost = FenBrowser.Host.Platform.PlatformHostFactory.Create();
                platformHost.EnableHighDpiAwareness();

                // 3. Initialize Window Manager via Platform Host
                var windowOptions = new FenBrowser.Host.Platform.WindowOptions
                {
                    Title = "FenBrowser",
                    Size = new FenBrowser.Host.Platform.Size(1280, 800),
                    State = FenBrowser.Host.Platform.WindowState.Maximized,
                    VSync = true,
                    Border = FenBrowser.Host.Platform.WindowBorder.Hidden
                };

                var platformWindow = platformHost.CreateWindow(windowOptions);
                platformWindow.Initialize(initialUrl);

                // 4. Initialize Chrome Manager (UI)
                // Hook into Window Load event to avoiding init before GL context
                platformWindow.OnLoad += () => {
                    ChromeManager.Instance.Initialize(initialUrl);

                    // DIAGNOSTIC LOGGING
                    var wm = WindowManager.Instance;
                    EngineLog.Write(LogSubsystem.General, LogSeverity.Info, $"[DPI-CHECK] Physical: {wm.Window.FramebufferSize.X}x{wm.Window.FramebufferSize.Y}");
                    EngineLog.Write(LogSubsystem.General, LogSeverity.Info, $"[DPI-CHECK] Logical:  {wm.Window.Size.X}x{wm.Window.Size.Y}");
                    EngineLog.Write(LogSubsystem.General, LogSeverity.Info, $"[DPI-CHECK] Scale:    {wm.DpiScale}");
                };

                // 5. Run Application
                platformHost.Run();
            }
            catch (Exception ex)
            {
                AttachConsole(ATTACH_PARENT_PROCESS); // Ensure crash logs are visible if run from console
                Console.WriteLine($"[Host] Fatal Shutdown: {ex}");
                EngineLog.Write(LogSubsystem.General, LogSeverity.Error, $"[Host] Fatal Shutdown: {ex}", LogMarker.Invariant);
                TryExportCrashBundle("Host fatal shutdown", ex);
                throw;
            }
        }

        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        private static extern bool AttachConsole(int dwProcessId);
        private const int ATTACH_PARENT_PROCESS = -1;

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool SetProcessDpiAwarenessContext(int dpiContext);
        private const int DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = -4;

        private static void TryExportCrashBundle(string summary, Exception? exception)
        {
            try
            {
                var bundlePath = EngineLog.ExportFailureBundle(
                    summary: string.IsNullOrWhiteSpace(summary)
                        ? "FenBrowser crash snapshot"
                        : summary,
                    url: null,
                    testId: null,
                    maxEntries: 4000);

                EngineLog.Write(
                    LogSubsystem.General,
                    LogSeverity.Error,
                    $"[CRASH] Failure bundle exported to {bundlePath}",
                    LogMarker.Fallback,
                    default,
                    exception == null
                        ? null
                        : new Dictionary<string, object> { ["exception"] = exception.ToString() });
            }
            catch (Exception exportError)
            {
                try
                {
                    EngineLog.Write(
                        LogSubsystem.General,
                        LogSeverity.Error,
                        $"[CRASH] Failed to export failure bundle: {exportError.Message}",
                        LogMarker.Fallback);
                }
                catch
                {
                    // no-op
                }
            }
        }

        private static void RejectToolingArguments(string[] args)
        {
            if (args == null || args.Length == 0)
            {
                return;
            }

            bool requestedTooling =
                string.Equals(args[0], "--wpt", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(args[0], "--acid2", StringComparison.OrdinalIgnoreCase) ||
                args.Any(a => a.StartsWith("--port=", StringComparison.OrdinalIgnoreCase)) ||
                args.Any(a => string.Equals(a, "--debug-css", StringComparison.OrdinalIgnoreCase));

            if (!requestedTooling)
            {
                return;
            }

            AttachConsole(ATTACH_PARENT_PROCESS);
            throw new InvalidOperationException(
                "Tooling commands moved out of FenBrowser.Host. Use FenBrowser.Tooling for webdriver, acid2, and debug-css workflows.");
        }

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
            EngineLog.InitializeFromSettings();

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

            EngineLog.Write(LogSubsystem.ProcessIsolation, LogSeverity.Info, $"[RendererChild] Started for tab={tabId}, parentPid={parentPid}, pipe={pipeName}, assignment={assignmentKey}");

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
                EngineLog.Write(LogSubsystem.ProcessIsolation, LogSeverity.Info, $"[RendererChild] Exiting for tab={tabId}");
                return;
            }

            if (!string.Equals(sandboxProfile, "renderer_minimal", StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(capabilitySet, "navigate,input,frame", StringComparison.OrdinalIgnoreCase))
            {
                EngineLog.Write(LogSubsystem.ProcessIsolation, LogSeverity.Warn, $"[RendererChild] Startup policy assertion failed for tab={tabId}. sandboxProfile={sandboxProfile}, capabilities={capabilitySet}.");
                return;
            }

            using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                pipe.Connect(5000);
            }
            catch (Exception ex)
            {
                EngineLog.Write(LogSubsystem.ProcessIsolation, LogSeverity.Warn, $"[RendererChild] Failed to connect IPC pipe '{pipeName}': {ex.Message}");
                return;
            }

            using var reader = new StreamReader(pipe, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: true);
            using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };
            using var browser = new FenBrowser.FenEngine.Rendering.BrowserHost();
            using var logForwarder = new ChildProcessLogForwarder("renderer", tabId);

            bool handshakeComplete = false;
            bool running = true;
            bool hasFrameViewport = false;
            float lastFrameViewportWidth = 1280f;
            float lastFrameViewportHeight = 720f;
            float lastFrameScrollY = 0f;
            int pendingRendererRepaintFrame = 0;

            void SendFrameReady(float viewportWidth, float viewportHeight, float scrollY, string requestedBy, string correlationId)
            {
                viewportWidth = Math.Max(1f, Math.Min(viewportWidth, FenBrowser.Host.ProcessIsolation.FrameSharedMemory.MaxWidth));
                viewportHeight = Math.Max(1f, Math.Min(viewportHeight, FenBrowser.Host.ProcessIsolation.FrameSharedMemory.MaxHeight));
                scrollY = Math.Max(0f, scrollY);
                float rasterHeight = ComputeBrokeredFrameRasterHeight(viewportHeight);

                int iWidth = (int)viewportWidth;
                int iHeight = (int)rasterHeight;
                float actualWidth = viewportWidth;
                float actualHeight = iHeight;
                uint seqNum = 0;
                FenBrowser.FenEngine.Rendering.Core.RenderFrameResult frameResult = null;

                // Lazily create the shared memory writer on first frame publication.
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

                            // Document-space raster viewport (top advances with scroll); the canvas
                            // is translated by -scrollY so the visible band rasterises at (0,0).
                            // The raster surface is taller than the visible viewport so compositor
                            // scroll preview has real pixels for the newly exposed bottom band.
                            var viewport = new SkiaSharp.SKRect(0, scrollY, viewportWidth, scrollY + actualHeight);
                            canvas.Save();
                            if (scrollY > 0f)
                            {
                                canvas.Translate(0, -scrollY);
                            }
                            var childRenderer = new FenBrowser.FenEngine.Rendering.SkiaDomRenderer();
                            var contentHeightHint = Math.Max(
                                browser.Engine?.LastLayout?.ContentHeight ?? 0f,
                                scrollY + actualHeight);
                            childRenderer.ScrollManager.SetScrollBounds(
                                null,
                                viewportWidth,
                                Math.Max(contentHeightHint, viewportHeight),
                                viewportWidth,
                                viewportHeight);
                            childRenderer.ScrollManager.SetScrollPosition(null, 0, scrollY);
                            frameResult = childRenderer.RenderFrame(new FenBrowser.FenEngine.Rendering.Core.RenderFrameRequest
                            {
                                Root = domRoot,
                                Canvas = canvas,
                                Styles = styles != null
                                    ? new System.Collections.Generic.Dictionary<FenBrowser.Core.Dom.V2.Node, FenBrowser.Core.Css.CssComputed>(styles)
                                    : new System.Collections.Generic.Dictionary<FenBrowser.Core.Dom.V2.Node, FenBrowser.Core.Css.CssComputed>(),
                                Viewport = viewport,
                                BaseUrl = browser.CurrentUri?.AbsoluteUri,
                                SeparateLayoutViewport = new SkiaSharp.SKSize(viewportWidth, viewportHeight),
                                InvalidationReason = FenBrowser.FenEngine.Rendering.Core.RenderFrameInvalidationReason.ProcessIsolation,
                                RequestedBy = requestedBy,
                                EmitVerificationReport = false
                            });
                            canvas.Restore();
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
                            EngineLog.Write(LogSubsystem.Paint, LogSeverity.Debug, $"[RendererChild] Frame written to shared memory: {iWidth}x{iHeight} for tab={tabId} requestedBy={requestedBy}");
                        }
                        else
                        {
                            EngineLog.Write(LogSubsystem.Paint, LogSeverity.Debug, $"[RendererChild] No DOM root for tab={tabId}; skipping frame write.");
                        }
                    }
                    catch (Exception renderEx)
                    {
                        EngineLog.Write(LogSubsystem.Paint, LogSeverity.Warn, $"[RendererChild] Frame render failed for tab={tabId}: {renderEx.Message}");
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
                    FrameSequenceNumber = seqNum,
                    RequestedBy = frameResult?.RequestedBy ?? requestedBy,
                    InvalidationReason = frameResult?.InvalidationReason.ToString() ?? FenBrowser.FenEngine.Rendering.Core.RenderFrameInvalidationReason.ProcessIsolation.ToString(),
                    RasterMode = frameResult?.RasterMode.ToString() ?? FenBrowser.FenEngine.Rendering.Core.RenderFrameRasterMode.Full.ToString(),
                    UsedDamageRasterization = frameResult?.UsedDamageRasterization ?? false,
                    DamageAreaRatio = frameResult?.DamageAreaRatio ?? 0f,
                    LayoutUpdated = frameResult?.Telemetry?.LayoutUpdated ?? false,
                    PaintTreeRebuilt = frameResult?.Telemetry?.PaintTreeRebuilt ?? false,
                    WatchdogTriggered = frameResult?.WatchdogTriggered ?? false,
                    WatchdogReason = frameResult?.WatchdogReason ?? string.Empty,
                    TotalDurationMs = frameResult?.Telemetry?.TotalDurationMs ?? 0d,
                    DomNodeCount = frameResult?.Telemetry?.DomNodeCount ?? 0,
                    BoxCount = frameResult?.Telemetry?.BoxCount ?? 0,
                    PaintNodeCount = frameResult?.Telemetry?.PaintNodeCount ?? 0,
                    ScrollY = scrollY,
                    ContentHeight = frameResult?.Layout?.ContentHeight ?? 0f
                };

                SendRendererEnvelope(writer, new RendererIpcEnvelope
                {
                    Type = RendererIpcMessageType.FrameReady.ToString(),
                    TabId = tabId,
                    CorrelationId = correlationId,
                    Payload = RendererIpc.SerializePayload(payload)
                });
            }

            void DrainPendingRendererRepaintFrame()
            {
                if (Interlocked.Exchange(ref pendingRendererRepaintFrame, 0) != 1)
                {
                    return;
                }

                if (!handshakeComplete || !hasFrameViewport)
                {
                    return;
                }

                SendFrameReady(
                    lastFrameViewportWidth,
                    lastFrameViewportHeight,
                    lastFrameScrollY,
                    "RendererChild.RepaintReady",
                    Guid.NewGuid().ToString("N"));
            }

            void SendMetadata(string title = null, SkiaSharp.SKBitmap favicon = null, bool faviconChanged = false)
            {
                if (!handshakeComplete)
                {
                    return;
                }

                byte[] faviconBytes = null;
                if (favicon != null)
                {
                    try
                    {
                        using var image = SkiaSharp.SKImage.FromBitmap(favicon);
                        using var data = image?.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
                        faviconBytes = data?.ToArray();
                    }
                    catch (Exception ex)
                    {
                        EngineLog.Write(LogSubsystem.ProcessIsolation, LogSeverity.Warn, $"[RendererChild] Failed to encode favicon metadata for tab={tabId}: {ex.Message}");
                    }
                }

                if (string.IsNullOrWhiteSpace(title) && !faviconChanged)
                {
                    return;
                }

                SendRendererEnvelope(writer, new RendererIpcEnvelope
                {
                    Type = RendererIpcMessageType.MetadataChanged.ToString(),
                    TabId = tabId,
                    CorrelationId = Guid.NewGuid().ToString("N"),
                    Payload = RendererIpc.SerializePayload(new RendererMetadataChangedPayload
                    {
                        Url = browser.CurrentUri?.AbsoluteUri ?? string.Empty,
                        Title = title,
                        FaviconChanged = faviconChanged,
                        FaviconPngBytes = faviconBytes
                    })
                });
            }

            browser.TitleChanged += (_, title) => SendMetadata(title: title);
            browser.FaviconChanged += (_, favicon) => SendMetadata(favicon: favicon, faviconChanged: true);
            browser.RepaintReady += (_, __) =>
            {
                if (!handshakeComplete || !hasFrameViewport)
                {
                    return;
                }

                Interlocked.Exchange(ref pendingRendererRepaintFrame, 1);
            };

            while (running)
            {
                if (handshakeComplete)
                {
                    logForwarder.FlushRenderer(writer, tabId);
                    DrainPendingRendererRepaintFrame();
                }

                if (!IsParentAlive(parentPid))
                {
                    break;
                }

                var readResult = await RendererChildLoopIo.ReadLineWithTimeoutAsync(
                    reader,
                    TimeSpan.FromMilliseconds(100)).ConfigureAwait(false);
                if (!readResult.Completed)
                {
                    if (handshakeComplete)
                    {
                        DrainPendingRendererRepaintFrame();
                    }

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

                if (!RendererIpc.TryValidateInboundEnvelope(envelope, tabId, out var rendererMessageType, out var rendererRejectionReason))
                {
                    EngineLog.Write(LogSubsystem.ProcessIsolation, LogSeverity.Warn, $"[RendererChild] Rejected inbound envelope: {rendererRejectionReason}.");
                    continue;
                }

                try
                {
                    if (rendererMessageType == RendererIpcMessageType.Hello)
                    {
                        if (handshakeComplete)
                        {
                            SendRendererEnvelope(writer, new RendererIpcEnvelope
                            {
                                Type = RendererIpcMessageType.Error.ToString(),
                                TabId = tabId,
                                CorrelationId = envelope.CorrelationId,
                                Payload = "duplicate_handshake"
                            });
                            continue;
                        }

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

                    if (!string.IsNullOrWhiteSpace(envelope.Token))
                    {
                        SendRendererEnvelope(writer, new RendererIpcEnvelope
                        {
                            Type = RendererIpcMessageType.Error.ToString(),
                            TabId = tabId,
                            CorrelationId = envelope.CorrelationId,
                            Payload = "token_not_allowed"
                        });
                        continue;
                    }

                    if (rendererMessageType == RendererIpcMessageType.Navigate)
                    {
                        var payload = RendererIpc.DeserializePayload<RendererNavigatePayload>(envelope);
                        var url = payload?.Url ?? string.Empty;
                        if (!string.IsNullOrWhiteSpace(url))
                        {
                            if (payload.ViewportWidth > 1f && payload.ViewportHeight > 1f)
                            {
                                browser.UpdateViewportHint(payload.ViewportWidth, payload.ViewportHeight);
                            }

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

                    if (rendererMessageType == RendererIpcMessageType.Input)
                    {
                        var input = RendererIpc.DeserializePayload<RendererInputEvent>(envelope);
                        if (input != null && input.IsMeaningful)
                        {
                            await DispatchRendererInputAsync(browser, input).ConfigureAwait(false);
                        }

                        SendRendererEnvelope(writer, new RendererIpcEnvelope
                        {
                            Type = RendererIpcMessageType.Ack.ToString(),
                            TabId = tabId,
                            CorrelationId = envelope.CorrelationId
                        });
                        continue;
                    }

                    if (rendererMessageType == RendererIpcMessageType.FrameRequest)
                    {
                        var frameRequest = RendererIpc.DeserializePayload<RendererFrameRequestPayload>(envelope);
                        float vpWidth = frameRequest?.ViewportWidth ?? 1280f;
                        float vpHeight = frameRequest?.ViewportHeight ?? 720f;
                        float scrollY = Math.Max(0f, frameRequest?.ScrollY ?? 0f);

                        // Clamp to sane range.
                        vpWidth = Math.Max(1f, Math.Min(vpWidth, FenBrowser.Host.ProcessIsolation.FrameSharedMemory.MaxWidth));
                        vpHeight = Math.Max(1f, Math.Min(vpHeight, FenBrowser.Host.ProcessIsolation.FrameSharedMemory.MaxHeight));
                        lastFrameViewportWidth = vpWidth;
                        lastFrameViewportHeight = vpHeight;
                        lastFrameScrollY = scrollY;
                        hasFrameViewport = true;

                        SendFrameReady(vpWidth, vpHeight, scrollY, "RendererChild.FrameRequest", envelope.CorrelationId);
                        continue;
                    }

                    if (rendererMessageType == RendererIpcMessageType.Shutdown ||
                        rendererMessageType == RendererIpcMessageType.TabClosed)
                    {
                        running = false;
                        continue;
                    }

                    if (rendererMessageType == RendererIpcMessageType.Ping)
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

            if (handshakeComplete)
            {
                logForwarder.FlushRenderer(writer, tabId);
            }

            frameSharedMemory?.Dispose();
            EngineLog.Write(LogSubsystem.ProcessIsolation, LogSeverity.Info, $"[RendererChild] Exiting for tab={tabId}");
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
                EngineLog.Write(LogSubsystem.ProcessIsolation, LogSeverity.Warn, "[NetworkChild] Missing required startup environment.");
                return;
            }

            if (!string.Equals(sandboxProfile, "network_process", StringComparison.OrdinalIgnoreCase))
            {
                EngineLog.Write(LogSubsystem.ProcessIsolation, LogSeverity.Warn, $"[NetworkChild] Startup policy assertion failed. sandboxProfile={sandboxProfile}, capabilities={capabilitySet}.");
                return;
            }

            using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                pipe.Connect(5000);
            }
            catch (Exception ex)
            {
                EngineLog.Write(LogSubsystem.ProcessIsolation, LogSeverity.Warn, $"[NetworkChild] Failed to connect IPC pipe '{pipeName}': {ex.Message}");
                return;
            }

            using var reader = new StreamReader(pipe, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: true);
            using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };
            using var httpClient = HttpClientFactory.CreateClient();
            using var noProxyHandler = HttpClientFactory.CreateHandler();
            using var noProxyClient = HttpClientFactory.CreateClient(noProxyHandler);
            using var logForwarder = new ChildProcessLogForwarder("network", 0);
            var activeRequests = new ConcurrentDictionary<string, CancellationTokenSource>(StringComparer.Ordinal);
            bool handshakeComplete = false;
            bool running = true;

            noProxyHandler.UseProxy = false;

            while (running)
            {
                if (handshakeComplete)
                {
                    logForwarder.FlushNetwork(writer);
                }

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

                if (!NetworkIpc.TryValidateInboundEnvelope(envelope, out var networkMessageType, out var networkRejectionReason))
                {
                    EngineLog.Write(LogSubsystem.ProcessIsolation, LogSeverity.Warn, $"[NetworkChild] Rejected inbound envelope: {networkRejectionReason}.");
                    continue;
                }

                try
                {
                    if (networkMessageType == NetworkIpcMessageType.Hello)
                    {
                        if (handshakeComplete)
                        {
                            SendNetworkEnvelope(writer, new NetworkIpcEnvelope
                            {
                                Type = NetworkIpcMessageType.Error.ToString(),
                                RequestId = envelope.RequestId,
                                Payload = "duplicate_handshake"
                            });
                            continue;
                        }

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

                    if (networkMessageType == NetworkIpcMessageType.FetchRequest)
                    {
                        var payload = NetworkIpc.DeserializePayload<NetworkFetchRequestPayload>(envelope);
                        if (payload == null || string.IsNullOrWhiteSpace(payload.Url))
                        {
                            SendFetchFailure(writer, envelope.RequestId, envelope.CapabilityToken, "invalid_request", "Missing fetch URL.");
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
                                SendFetchFailure(writer, envelope.RequestId, envelope.CapabilityToken, "cancelled", "Request cancelled.");
                            }
                            catch (Exception ex)
                            {
                                SendFetchFailure(writer, envelope.RequestId, envelope.CapabilityToken, "fetch_failed", ex.Message);
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

                    if (networkMessageType == NetworkIpcMessageType.CancelRequest)
                    {
                        if (activeRequests.TryRemove(envelope.RequestId, out var cts))
                        {
                            cts.Cancel();
                            cts.Dispose();
                        }
                        continue;
                    }

                    if (networkMessageType == NetworkIpcMessageType.Ping)
                    {
                        SendNetworkEnvelope(writer, new NetworkIpcEnvelope
                        {
                            Type = NetworkIpcMessageType.Pong.ToString(),
                            RequestId = envelope.RequestId
                        });
                        continue;
                    }

                    if (networkMessageType == NetworkIpcMessageType.Shutdown)
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
                try
                {
                    kvp.Value.Cancel();
                }
                catch (Exception ex)
                {
                    EngineLog.Write(LogSubsystem.Net, LogSeverity.Debug, $"[NetworkChild] Request cancel failed: {ex.Message}");
                }

                try
                {
                    kvp.Value.Dispose();
                }
                catch (Exception ex)
                {
                    EngineLog.Write(LogSubsystem.Net, LogSeverity.Debug, $"[NetworkChild] Request dispose failed: {ex.Message}");
                }
            }

            if (handshakeComplete)
            {
                logForwarder.FlushNetwork(writer);
            }

            EngineLog.Write(LogSubsystem.Net, LogSeverity.Info, "[NetworkChild] Exiting.");
        }

        private static async Task RunTargetChildLoopAsync(TargetProcessKind expectedKind)
        {
            var contract = expectedKind == TargetProcessKind.Gpu
                ? GpuProcessIpc.Contract
                : UtilityProcessIpc.Contract;
            var pipeName = Environment.GetEnvironmentVariable("FEN_TARGET_PIPE_NAME");
            var authToken = Environment.GetEnvironmentVariable("FEN_TARGET_AUTH_TOKEN");
            var parentPidRaw = Environment.GetEnvironmentVariable("FEN_TARGET_PARENT_PID");
            var targetKindRaw = Environment.GetEnvironmentVariable("FEN_TARGET_KIND");
            var sandboxProfile = Environment.GetEnvironmentVariable("FEN_TARGET_SANDBOX_PROFILE");
            var capabilitySet = Environment.GetEnvironmentVariable("FEN_TARGET_CAPABILITIES");
            var parentPid = int.TryParse(parentPidRaw, out var parsedParentPid) ? parsedParentPid : 0;

            if (string.IsNullOrWhiteSpace(pipeName) || string.IsNullOrWhiteSpace(authToken))
            {
                EngineLog.Write(LogSubsystem.ProcessIsolation, LogSeverity.Warn, $"[{expectedKind}Child] Missing required startup environment.");
                return;
            }

            if (!string.Equals(targetKindRaw, expectedKind.ToString().ToLowerInvariant(), StringComparison.OrdinalIgnoreCase))
            {
                EngineLog.Write(LogSubsystem.ProcessIsolation, LogSeverity.Warn, $"[{expectedKind}Child] Startup kind assertion failed. targetKind={targetKindRaw}.");
                return;
            }

            if (!contract.Matches(sandboxProfile, capabilitySet))
            {
                EngineLog.Write(LogSubsystem.ProcessIsolation, LogSeverity.Warn, $"[{expectedKind}Child] Startup policy assertion failed. sandboxProfile={sandboxProfile}, capabilities={capabilitySet}.");
                return;
            }

            using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                pipe.Connect(5000);
            }
            catch (Exception ex)
            {
                EngineLog.Write(LogSubsystem.ProcessIsolation, LogSeverity.Warn, $"[{expectedKind}Child] Failed to connect IPC pipe '{pipeName}': {ex.Message}");
                return;
            }

            using var reader = new StreamReader(pipe, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: true);
            using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };
            using var logForwarder = new ChildProcessLogForwarder(expectedKind.ToString().ToLowerInvariant(), 0);

            var handshakeComplete = false;
            var running = true;
            while (running)
            {
                if (handshakeComplete)
                {
                    logForwarder.FlushTarget(writer);
                }

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

                if (!TargetIpc.TryValidateInboundEnvelope(envelope, out var targetMessageType, out var targetRejectionReason))
                {
                    EngineLog.Write(LogSubsystem.ProcessIsolation, LogSeverity.Warn, $"[{expectedKind}Child] Rejected inbound envelope: {targetRejectionReason}.");
                    continue;
                }

                try
                {
                    if (targetMessageType == TargetIpcMessageType.Hello)
                    {
                        if (handshakeComplete)
                        {
                            SendTargetEnvelope(writer, new TargetIpcEnvelope
                            {
                                Type = TargetIpcMessageType.Error.ToString(),
                                RequestId = envelope.RequestId,
                                Payload = "duplicate_handshake"
                            });
                            continue;
                        }

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
                                SandboxProfile = contract.ProfileName,
                                Capabilities = contract.CapabilitySet,
                                ProcessId = Environment.ProcessId
                            })
                        });
                        continue;
                    }

                    if (!handshakeComplete)
                    {
                        continue;
                    }

                    if (targetMessageType == TargetIpcMessageType.Ping)
                    {
                        SendTargetEnvelope(writer, new TargetIpcEnvelope
                        {
                            Type = TargetIpcMessageType.Pong.ToString(),
                            RequestId = envelope.RequestId
                        });
                        continue;
                    }

                    if (targetMessageType == TargetIpcMessageType.CompositorFrameSubmit)
                    {
                        if (expectedKind != TargetProcessKind.Gpu)
                        {
                            SendTargetEnvelope(writer, new TargetIpcEnvelope
                            {
                                Type = TargetIpcMessageType.Error.ToString(),
                                RequestId = envelope.RequestId,
                                Payload = "unsupported_target_for_compositor_submit"
                            });
                            continue;
                        }

                        var payload = TargetIpc.DeserializePayload<TargetCompositorFramePayload>(envelope);
                        if (payload == null || payload.FrameSequence <= 0)
                        {
                            SendTargetEnvelope(writer, new TargetIpcEnvelope
                            {
                                Type = TargetIpcMessageType.Error.ToString(),
                                RequestId = envelope.RequestId,
                                Payload = "invalid_compositor_payload"
                            });
                            continue;
                        }

                        SendTargetEnvelope(writer, new TargetIpcEnvelope
                        {
                            Type = TargetIpcMessageType.CompositorFrameAck.ToString(),
                            RequestId = envelope.RequestId,
                            Payload = TargetIpc.SerializePayload(new TargetCompositorFrameAckPayload
                            {
                                FrameSequence = payload.FrameSequence
                            })
                        });
                        continue;
                    }

                    // Utility process: Font service operations
                    if (expectedKind == TargetProcessKind.Utility)
                    {
                        await HandleFontMetricsRequest(writer, envelope).ConfigureAwait(false);
                        await HandleFontMeasureWidthRequest(writer, envelope).ConfigureAwait(false);
                        await HandleFontShapeTextRequest(writer, envelope).ConfigureAwait(false);
                        await HandleFontResolveTypefaceRequest(writer, envelope).ConfigureAwait(false);
                        await HandleImageDecodeRequest(writer, envelope).ConfigureAwait(false);
                        await HandleSvgDecodeRequest(writer, envelope).ConfigureAwait(false);
                    }

                    if (targetMessageType == TargetIpcMessageType.Shutdown)
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

            if (handshakeComplete)
            {
                logForwarder.FlushTarget(writer);
            }

            EngineLog.Write(LogSubsystem.ProcessIsolation, LogSeverity.Info, $"[{expectedKind}Child] Exiting.");
        }

        private sealed class ChildProcessLogForwarder : IDisposable
        {
            private readonly ConcurrentQueue<EngineLogEvent> _queue = new();
            private readonly Action<EngineLogEvent> _handler;
            private readonly string _processKind;
            private readonly int _tabId;
            private int _dropped;

            public ChildProcessLogForwarder(string processKind, int tabId)
            {
                _processKind = processKind;
                _tabId = tabId;
                _handler = OnEvent;
                EngineLog.EngineEventWritten += _handler;
            }

            public void Dispose()
            {
                EngineLog.EngineEventWritten -= _handler;
            }

            private void OnEvent(EngineLogEvent evt)
            {
                // Avoid re-forwarding aggregation traffic or self-referential IPC noise.
                if (evt.Header.Subsystem == LogSubsystem.Ipc)
                {
                    return;
                }

                if (_queue.Count >= 4096)
                {
                    Interlocked.Increment(ref _dropped);
                    return;
                }

                _queue.Enqueue(evt);
            }

            public void FlushRenderer(StreamWriter writer, int tabId)
            {
                FlushCore(batch =>
                {
                    SendRendererEnvelope(writer, new RendererIpcEnvelope
                    {
                        Type = RendererIpcMessageType.LogBatch.ToString(),
                        TabId = tabId,
                        CorrelationId = Guid.NewGuid().ToString("N"),
                        Payload = RendererIpc.SerializePayload(batch)
                    });
                });
            }

            public void FlushNetwork(StreamWriter writer)
            {
                FlushCore(batch =>
                {
                    SendNetworkEnvelope(writer, new NetworkIpcEnvelope
                    {
                        Type = NetworkIpcMessageType.LogBatch.ToString(),
                        RequestId = Guid.NewGuid().ToString("N"),
                        Payload = NetworkIpc.SerializePayload(batch)
                    });
                });
            }

            public void FlushTarget(StreamWriter writer)
            {
                FlushCore(batch =>
                {
                    SendTargetEnvelope(writer, new TargetIpcEnvelope
                    {
                        Type = TargetIpcMessageType.LogBatch.ToString(),
                        RequestId = Guid.NewGuid().ToString("N"),
                        Payload = TargetIpc.SerializePayload(batch)
                    });
                });
            }

            private void FlushCore(Action<EngineLogBatchPayload> send)
            {
                if (send == null)
                {
                    return;
                }

                var dropped = Interlocked.Exchange(ref _dropped, 0);
                if (dropped > 0)
                {
                    _queue.Enqueue(BuildDropEvent(dropped));
                }

                while (_queue.TryDequeue(out var first))
                {
                    var events = new List<EngineLogEvent>(64) { first };
                    while (events.Count < 64 && _queue.TryDequeue(out var next))
                    {
                        events.Add(next);
                    }

                    send(new EngineLogBatchPayload
                    {
                        ProcessKind = _processKind,
                        TabId = _tabId,
                        Events = events
                    });
                }
            }

            private EngineLogEvent BuildDropEvent(int dropped)
            {
                var context = new EngineLogContext(
                    TabId: _tabId > 0 ? _tabId.ToString() : null,
                    Source: _processKind);

                var header = new EngineLogHeader(
                    DateTimeOffset.UtcNow,
                    0,
                    Environment.ProcessId,
                    Environment.CurrentManagedThreadId,
                    LogSubsystem.Ipc,
                    LogSeverity.Warn,
                    LogMarker.Fallback,
                    Guid.NewGuid(),
                    null,
                    null,
                    context);

                var payload = new EngineLogPayload
                {
                    MessageTemplate = "Child log forwarder dropped events before flush",
                    Fields = new Dictionary<string, object>
                    {
                        ["droppedCount"] = dropped
                    }
                };

                return new EngineLogEvent(header, payload);
            }
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
                lock (writer)
                {
                    writer.WriteLine(RendererIpc.SerializeEnvelope(envelope));
                    writer.Flush();
                }
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

        // Utility process: Font service handlers
        private static Task HandleFontMetricsRequest(StreamWriter writer, TargetIpcEnvelope envelope)
        {
            if (envelope.Type != TargetIpcMessageType.FontGetMetrics.ToString())
                return Task.CompletedTask;

            try
            {
                var payload = TargetIpc.DeserializePayload<FontGetMetricsPayload>(envelope);
                if (payload == null)
                {
                    SendErrorResponse(writer, envelope.RequestId, TargetIpcMessageType.FontGetMetricsResponse, "Invalid payload");
                    return Task.CompletedTask;
                }

                var fontService = GetOrCreateFontService();
                var metrics = fontService.GetMetrics(payload.FontFamily, payload.FontSize, payload.FontWeight, payload.CssLineHeight);

                var response = new FontGetMetricsResponsePayload
                {
                    Success = true,
                    Ascent = metrics.Ascent,
                    Descent = metrics.Descent,
                    LineGap = metrics.Leading,
                    LineHeight = metrics.LineHeight,
                    XHeight = metrics.XHeight,
                    CapHeight = metrics.EmSize
                };

                SendTargetEnvelope(writer, new TargetIpcEnvelope
                {
                    Type = TargetIpcMessageType.FontGetMetricsResponse.ToString(),
                    RequestId = envelope.RequestId,
                    Payload = TargetIpc.SerializePayload(response)
                });
            }
            catch (Exception ex)
            {
                SendErrorResponse(writer, envelope.RequestId, TargetIpcMessageType.FontGetMetricsResponse, ex.Message);
            }

            return Task.CompletedTask;
        }

        private static Task HandleFontMeasureWidthRequest(StreamWriter writer, TargetIpcEnvelope envelope)
        {
            if (envelope.Type != TargetIpcMessageType.FontMeasureWidth.ToString())
                return Task.CompletedTask;

            try
            {
                var payload = TargetIpc.DeserializePayload<FontMeasureWidthPayload>(envelope);
                if (payload == null)
                {
                    SendErrorResponse(writer, envelope.RequestId, TargetIpcMessageType.FontMeasureWidthResponse, "Invalid payload");
                    return Task.CompletedTask;
                }

                var fontService = GetOrCreateFontService();
                var width = fontService.MeasureTextWidth(payload.Text, payload.FontFamily, payload.FontSize, payload.FontWeight);

                var response = new FontMeasureWidthResponsePayload
                {
                    Success = true,
                    Width = width
                };

                SendTargetEnvelope(writer, new TargetIpcEnvelope
                {
                    Type = TargetIpcMessageType.FontMeasureWidthResponse.ToString(),
                    RequestId = envelope.RequestId,
                    Payload = TargetIpc.SerializePayload(response)
                });
            }
            catch (Exception ex)
            {
                SendErrorResponse(writer, envelope.RequestId, TargetIpcMessageType.FontMeasureWidthResponse, ex.Message);
            }

            return Task.CompletedTask;
        }

        private static Task HandleFontShapeTextRequest(StreamWriter writer, TargetIpcEnvelope envelope)
        {
            if (envelope.Type != TargetIpcMessageType.FontShapeText.ToString())
                return Task.CompletedTask;

            try
            {
                var payload = TargetIpc.DeserializePayload<FontShapeTextPayload>(envelope);
                if (payload == null)
                {
                    SendErrorResponse(writer, envelope.RequestId, TargetIpcMessageType.FontShapeTextResponse, "Invalid payload");
                    return Task.CompletedTask;
                }

                var fontService = GetOrCreateFontService();
                var glyphRun = fontService.ShapeText(payload.Text, payload.FontFamily, payload.FontSize, payload.FontWeight);

                var glyphData = new PositionedGlyphData[glyphRun.Glyphs.Length];
                for (int i = 0; i < glyphRun.Glyphs.Length; i++)
                {
                    glyphData[i] = new PositionedGlyphData
                    {
                        GlyphId = glyphRun.Glyphs[i].GlyphId,
                        X = glyphRun.Glyphs[i].X,
                        Y = glyphRun.Glyphs[i].Y,
                        AdvanceX = glyphRun.Glyphs[i].AdvanceX
                    };
                }

                var metricsData = new NormalizedFontMetricsData
                {
                    Ascent = glyphRun.Metrics.Ascent,
                    Descent = glyphRun.Metrics.Descent,
                    LineGap = glyphRun.Metrics.Leading,
                    LineHeight = glyphRun.Metrics.LineHeight,
                    XHeight = glyphRun.Metrics.XHeight,
                    CapHeight = glyphRun.Metrics.EmSize
                };

                var response = new FontShapeTextResponsePayload
                {
                    Success = true,
                    Glyphs = glyphData,
                    Width = glyphRun.Width,
                    FontSize = glyphRun.FontSize,
                    Metrics = metricsData,
                    SourceText = glyphRun.SourceText
                };

                SendTargetEnvelope(writer, new TargetIpcEnvelope
                {
                    Type = TargetIpcMessageType.FontShapeTextResponse.ToString(),
                    RequestId = envelope.RequestId,
                    Payload = TargetIpc.SerializePayload(response)
                });
            }
            catch (Exception ex)
            {
                SendErrorResponse(writer, envelope.RequestId, TargetIpcMessageType.FontShapeTextResponse, ex.Message);
            }

            return Task.CompletedTask;
        }

        private static Task HandleFontResolveTypefaceRequest(StreamWriter writer, TargetIpcEnvelope envelope)
        {
            if (envelope.Type != TargetIpcMessageType.FontResolveTypeface.ToString())
                return Task.CompletedTask;

            try
            {
                var payload = TargetIpc.DeserializePayload<FontResolveTypefacePayload>(envelope);
                if (payload == null)
                {
                    SendErrorResponse(writer, envelope.RequestId, TargetIpcMessageType.FontResolveTypefaceResponse, "Invalid payload");
                    return Task.CompletedTask;
                }

                var fontService = GetOrCreateFontService();
                var style = (SKFontStyleSlant)payload.FontStyle;
var typeface = fontService.ResolveTypeface(payload.FontFamily, payload.FontWeight, style);

                int fontWeightValue = 400;
                int fontStyleValue = 0;
                int fontWidthValue = 5;

                if (typeface != null)
                {
                    fontWeightValue = (int)typeface.FontWeight;
                    fontStyleValue = (int)typeface.FontStyle.Slant;
                    fontWidthValue = (int)typeface.FontWidth;
                }

                var response = new FontResolveTypefaceResponsePayload
                {
                    Success = true,
                    FamilyName = typeface?.FamilyName ?? string.Empty,
                    FontWeight = fontWeightValue,
                    FontStyle = fontStyleValue,
                    FontWidth = fontWidthValue
                };

                SendTargetEnvelope(writer, new TargetIpcEnvelope
                {
                    Type = TargetIpcMessageType.FontResolveTypefaceResponse.ToString(),
                    RequestId = envelope.RequestId,
                    Payload = TargetIpc.SerializePayload(response)
                });
            }
            catch (Exception ex)
            {
                SendErrorResponse(writer, envelope.RequestId, TargetIpcMessageType.FontResolveTypefaceResponse, ex.Message);
            }

            return Task.CompletedTask;
        }

        private static Task HandleImageDecodeRequest(StreamWriter writer, TargetIpcEnvelope envelope)
        {
            if (envelope.Type != TargetIpcMessageType.ImageDecode.ToString())
                return Task.CompletedTask;

            try
            {
                var payload = TargetIpc.DeserializePayload<ImageDecodePayload>(envelope);
                if (payload == null || payload.Data == null)
                {
                    SendErrorResponse(writer, envelope.RequestId, TargetIpcMessageType.ImageDecodeResponse, "Invalid payload or empty data");
                    return Task.CompletedTask;
                }

                var bitmap = DecodeImage(payload.Data, payload.TargetWidth, payload.TargetHeight, payload.Url);

                byte[] bitmapBytes = null;
                int width = 0, height = 0;
                if (bitmap != null && !bitmap.IsNull)
                {
                    width = bitmap.Width;
                    height = bitmap.Height;
                    using var image = SKImage.FromBitmap(bitmap);
                    using var data = image.Encode(SKEncodedImageFormat.Png, 100);
                    bitmapBytes = data?.ToArray() ?? Array.Empty<byte>();
                    bitmap.Dispose();
                }

                var response = new ImageDecodeResponsePayload
                {
                    Success = bitmapBytes != null && bitmapBytes.Length > 0,
                    ErrorMessage = bitmapBytes == null || bitmapBytes.Length == 0 ? "Decode returned no bitmap" : null,
                    BitmapBytes = bitmapBytes ?? Array.Empty<byte>(),
                    Width = width,
                    Height = height,
                    Format = "png"
                };

                SendTargetEnvelope(writer, new TargetIpcEnvelope
                {
                    Type = TargetIpcMessageType.ImageDecodeResponse.ToString(),
                    RequestId = envelope.RequestId,
                    Payload = TargetIpc.SerializePayload(response)
                });
            }
            catch (Exception ex)
            {
                SendErrorResponse(writer, envelope.RequestId, TargetIpcMessageType.ImageDecodeResponse, ex.Message);
            }

            return Task.CompletedTask;
        }

        private static Task HandleSvgDecodeRequest(StreamWriter writer, TargetIpcEnvelope envelope)
        {
            if (envelope.Type != TargetIpcMessageType.SvgDecode.ToString())
                return Task.CompletedTask;

            try
            {
                var payload = TargetIpc.DeserializePayload<SvgDecodePayload>(envelope);
                if (payload == null || string.IsNullOrWhiteSpace(payload.SvgContent))
                {
                    SendErrorResponse(writer, envelope.RequestId, TargetIpcMessageType.SvgDecodeResponse, "Invalid payload or empty SVG content");
                    return Task.CompletedTask;
                }

                var svgRenderer = new SvgSkiaRenderer();
                var limits = new SvgRenderLimits
                {
                    MaxElementCount = payload.Limits?.MaxElementCount ?? 10000,
                    MaxFilterCount = payload.Limits?.MaxFilterCount ?? 100,
                    MaxRecursionDepth = payload.Limits?.MaxRecursionDepth ?? 100,
                    MaxRenderTimeMs = payload.Limits?.MaxRenderTimeMs ?? 5000,
                    AllowExternalReferences = payload.Limits?.AllowExternalReferences ?? false
                };

                var result = svgRenderer.Render(payload.SvgContent, limits);

                byte[] bitmapBytes = null;
                int width = 0, height = 0;
                if (result.Success && result.Bitmap != null && !result.Bitmap.IsNull)
                {
                    width = result.Bitmap.Width;
                    height = result.Bitmap.Height;
                    using var image = SKImage.FromBitmap(result.Bitmap);
                    using var data = image.Encode(SKEncodedImageFormat.Png, 100);
                    bitmapBytes = data?.ToArray() ?? Array.Empty<byte>();
                    result.Bitmap.Dispose();
                }

                var response = new SvgDecodeResponsePayload
                {
                    Success = bitmapBytes != null && bitmapBytes.Length > 0,
                    ErrorMessage = bitmapBytes == null || bitmapBytes.Length == 0 ? result.ErrorMessage : null,
                    BitmapBytes = bitmapBytes ?? Array.Empty<byte>(),
                    Width = width,
                    Height = height
                };

                SendTargetEnvelope(writer, new TargetIpcEnvelope
                {
                    Type = TargetIpcMessageType.SvgDecodeResponse.ToString(),
                    RequestId = envelope.RequestId,
                    Payload = TargetIpc.SerializePayload(response)
                });
            }
            catch (Exception ex)
            {
                SendErrorResponse(writer, envelope.RequestId, TargetIpcMessageType.SvgDecodeResponse, ex.Message);
            }

            return Task.CompletedTask;
        }

        private static SkiaFontService _fontService;
        private static readonly object _fontServiceLock = new();

        private static SkiaFontService GetOrCreateFontService()
        {
            if (_fontService != null)
                return _fontService;

            lock (_fontServiceLock)
            {
                if (_fontService == null)
                {
                    _fontService = new SkiaFontService();
                }
                return _fontService;
            }
        }

        private static SKBitmap DecodeImage(byte[] data, int? targetWidth, int? targetHeight, string url)
        {
            if (data == null || data.Length == 0)
                return null;

            try
            {
                // Check for SVG
                if (!string.IsNullOrWhiteSpace(url) && 
                    (url.EndsWith(".svg", StringComparison.OrdinalIgnoreCase) || 
                     url.StartsWith("data:image/svg+xml", StringComparison.OrdinalIgnoreCase)))
                {
                    string svgContent = System.Text.Encoding.UTF8.GetString(data);
                    var svgRenderer = new SvgSkiaRenderer();
                    var result = svgRenderer.Render(svgContent);
                    return result.Bitmap;
                }

                // Decode raster image
                var bitmap = SKBitmap.Decode(data);
                if (bitmap == null)
                {
                    // Fallback
                    using var skData = SKData.CreateCopy(data);
                    if (skData != null && !skData.IsEmpty)
                    {
                        using var image = SKImage.FromEncodedData(skData);
                        if (image != null)
                        {
                            bitmap = SKBitmap.FromImage(image);
                        }
                    }
                }

                if (bitmap != null && (targetWidth.HasValue || targetHeight.HasValue))
                {
                    int srcW = bitmap.Width;
                    int srcH = bitmap.Height;
                    int dstW = targetWidth ?? srcW;
                    int dstH = targetHeight ?? srcH;

                    if (dstW != srcW || dstH != srcH)
                    {
                        var info = new SKImageInfo(dstW, dstH, SKColorType.Bgra8888, SKAlphaType.Premul);
                        using var resized = new SKBitmap(info);
                        using var canvas = new SKCanvas(resized);
                        canvas.DrawBitmap(bitmap, new SKRect(0, 0, dstW, dstH));
                        bitmap.Dispose();
                        bitmap = resized;
                    }
                }

                return bitmap;
            }
            catch
            {
                return null;
            }
        }

        private static void SendErrorResponse(StreamWriter writer, string requestId, TargetIpcMessageType responseType, string errorMessage)
        {
            SendTargetEnvelope(writer, new TargetIpcEnvelope
            {
                Type = responseType.ToString(),
                RequestId = requestId,
                Payload = TargetIpc.SerializePayload(new { Success = false, ErrorMessage = errorMessage })
            });
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

        private static void SendFetchFailure(
            StreamWriter writer,
            string requestId,
            string capabilityToken,
            string errorCode,
            string errorMessage)
        {
            SendNetworkEnvelope(writer, new NetworkIpcEnvelope
            {
                Type = NetworkIpcMessageType.FetchFailed.ToString(),
                RequestId = requestId,
                CapabilityToken = capabilityToken,
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

        internal static async Task DispatchRendererInputAsync(BrowserHost browser, RendererInputEvent input)
        {
            ArgumentNullException.ThrowIfNull(browser);
            ArgumentNullException.ThrowIfNull(input);

            switch (input.Type)
            {
                case RendererInputEventType.MouseDown:
                    browser.OnMouseDown(input.X, input.Y, input.Button);
                    break;
                case RendererInputEventType.MouseUp:
                    browser.OnMouseUp(input.X, input.Y, input.Button);
                    if (input.ShouldEmitClick)
                    {
                        await browser.DispatchClickAndActivate(input.X, input.Y, input.Button).ConfigureAwait(false);
                    }
                    break;
                case RendererInputEventType.MouseMove:
                    browser.OnMouseMove(input.X, input.Y);
                    break;
                case RendererInputEventType.DblClick:
                    browser.OnDoubleClick(input.X, input.Y, input.Button);
                    break;
                case RendererInputEventType.ContextMenu:
                    browser.OnContextMenu(input.X, input.Y, input.Button);
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
                    browser.OnMouseWheel(input.X, input.Y, input.DeltaX, input.DeltaY);
                    break;
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


