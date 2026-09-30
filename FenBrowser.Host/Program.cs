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
            UtilityChild,
            MediaChild
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

            if (args.Any(a => string.Equals(a, "--media-child", StringComparison.OrdinalIgnoreCase)) ||
                string.Equals(getEnvironmentVariable("FEN_MEDIA_CHILD"), "1", StringComparison.Ordinal))
            {
                return StartupMode.MediaChild;
            }

            return StartupMode.Browser;
        }

        /// <summary>
        /// Resolves the startup window geometry from the command line.
        /// The browser opens maximized so a real session uses the whole screen;
        /// `--windowed` opens at a fixed size instead, which is what screenshot
        /// comparisons against another browser need, and `--window-size WxH`
        /// picks that size (implying windowed).
        /// </summary>
        // The URL is positional, but it used to be read only at args[0], so
        // "--windowed https://example.com" silently opened the home page instead.
        // Take the first argument that is neither a flag nor a flag's value.
        internal static string ResolveInitialUrl(string[] args)
        {
            if (args == null)
            {
                return null;
            }

            for (int i = 0; i < args.Length; i++)
            {
                var arg = args[i];
                if (string.IsNullOrWhiteSpace(arg))
                {
                    continue;
                }

                if (arg.StartsWith("--", StringComparison.Ordinal))
                {
                    // Skip the value of a flag that takes one, unless it was
                    // given as --flag=value.
                    if (FlagTakesValue(arg) && !arg.Contains('=') && i + 1 < args.Length)
                    {
                        i++;
                    }
                    continue;
                }

                return arg;
            }

            return null;
        }

        private static bool FlagTakesValue(string flag)
        {
            return string.Equals(flag, "--window-size", StringComparison.OrdinalIgnoreCase)
                || string.Equals(flag, "--log-level", StringComparison.OrdinalIgnoreCase);
        }

        internal static (Platform.WindowState State, Platform.Size Size) ResolveWindowLayout(string[] args)
        {
            var size = new Platform.Size(1280, 800);
            var state = Platform.WindowState.Maximized;

            if (args == null || args.Length == 0)
            {
                return (state, size);
            }

            if (args.Any(a => string.Equals(a, "--windowed", StringComparison.OrdinalIgnoreCase)))
            {
                state = Platform.WindowState.Normal;
            }

            int sizeIndex = Array.FindIndex(
                args,
                a => string.Equals(a, "--window-size", StringComparison.OrdinalIgnoreCase));
            if (sizeIndex >= 0 && sizeIndex + 1 < args.Length)
            {
                var parts = args[sizeIndex + 1].Split('x', 'X');
                if (parts.Length == 2 &&
                    int.TryParse(parts[0], out var width) &&
                    int.TryParse(parts[1], out var height) &&
                    width > 0 && height > 0)
                {
                    size = new Platform.Size(width, height);
                    // An explicit size is meaningless while maximized.
                    state = Platform.WindowState.Normal;
                }
            }

            return (state, size);
        }

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

        /// <summary>
        /// The document band a brokered frame rasterizes around the viewport at
        /// <paramref name="scrollY"/>: <see cref="ComputeBrokeredFrameRasterHeight"/>'s band
        /// below it and as much again above, so the host's scroll preview has real pixels
        /// whichever way the user scrolls before the next frame lands. Scrolling up used
        /// to expose white at once, since the frame began at the viewport's top edge.
        /// </summary>
        /// <returns>The band's top in document space and its height.</returns>
        internal static (float Top, float Height) ComputeBrokeredFrameRasterBand(float viewportHeight, float scrollY)
        {
            float belowHeight = ComputeBrokeredFrameRasterHeight(viewportHeight);
            float overdraw = Math.Max(0f, belowHeight - Math.Min(viewportHeight, FenBrowser.Host.ProcessIsolation.FrameSharedMemory.MaxHeight));
            float above = Math.Min(Math.Max(0f, scrollY), overdraw);
            above = Math.Min(above, Math.Max(0f, FenBrowser.Host.ProcessIsolation.FrameSharedMemory.MaxHeight - belowHeight));
            return (Math.Max(0f, scrollY) - above, above + belowHeight);
        }

        public static async Task Main(string[] args)
        {
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

                if (startupMode == StartupMode.MediaChild)
                {
                    await RunTargetChildLoopAsync(TargetProcessKind.Media).ConfigureAwait(false);
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

                string initialUrl = ResolveInitialUrl(args) ?? "https://www.google.com";
                EngineLog.Write(LogSubsystem.General, LogSeverity.Info, $"[Host] Starting FenBrowser with URL: {initialUrl}");

                // 2. Engine Config
                CssEngineConfig.CurrentEngine = CssEngineType.Custom;
                _ = SvgRendererWarmup.Start();

                // Initialize DI Container
                var container = new FenBrowser.DependencyInjection.ServiceContainer();
                container.AddCoreServices();

                // Create platform host
                var platformHost = FenBrowser.Host.Platform.PlatformHostFactory.Create();
                platformHost.EnableHighDpiAwareness();

                // 3. Initialize Window Manager via Platform Host
                var (windowState, windowSize) = ResolveWindowLayout(args);
                EngineLog.Write(LogSubsystem.General, LogSeverity.Info,
                    $"[Host] Window layout: {windowState} {windowSize.Width}x{windowSize.Height}");
                var windowOptions = new FenBrowser.Host.Platform.WindowOptions
                {
                    Title = "FenBrowser",
                    Size = windowSize,
                    State = windowState,
                    VSync = true,
                    Border = FenBrowser.Host.Platform.WindowBorder.Hidden
                };

                var platformWindow = platformHost.CreateWindow(windowOptions);
                platformWindow.Initialize(initialUrl);

                // The app shell (GL, Skia surface, input, render loop) binds to
                // the platform window; WindowManager exposes it to the UI layer.
                var windowManager = WindowManager.Instance;
                windowManager.Initialize(platformWindow, initialUrl);

                // 4. Initialize Chrome Manager (UI)
                // Hook into Window Load event to avoiding init before GL context
                windowManager.OnLoad += () => {
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
                if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows))
                {
                    AttachConsole(ATTACH_PARENT_PROCESS); // Ensure crash logs are visible if run from console
                }
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
            // Pages paint here under process isolation; warm the SVG path while the
            // child connects so the first inline icon does not render cold.
            _ = SvgRendererWarmup.Start();

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
            SkiaSharp.SKBitmap frameBitmap = null;
            SkiaSharp.SKCanvas frameCanvas = null;

            // Frames come from this process, so this is the only place that can say
            // where a multi-second freeze went. The host's own loop reports nothing
            // in brokered mode because it does not render. Track the gap between
            // published frames and split each frame into the two things that can
            // block it: taking the DOM snapshot, which contends with whoever holds
            // the render state lock, and the raster itself.
            long lastFramePublishTimestamp = 0;

            EngineLog.Write(LogSubsystem.ProcessIsolation, LogSeverity.Info, $"[RendererChild] Started for tab={tabId}, parentPid={parentPid}, pipe={pipeName}, assignment={assignmentKey}");

            if (string.IsNullOrWhiteSpace(pipeName) || string.IsNullOrWhiteSpace(authToken))
            {
                EngineLog.Write(
                    LogSubsystem.ProcessIsolation,
                    LogSeverity.Warn,
                    $"[RendererChild] Missing authenticated IPC startup data for tab={tabId}; exiting.");
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

            // The renderer holds no sockets: its sandbox profile grants none, and on
            // Linux the network namespace is unshared outright. Every fetch the
            // engine makes below goes to the broker over this pipe, which forwards
            // it to the sandboxed network process. Install the transport before the
            // BrowserHost exists so no client is ever created against the direct
            // socket handler.
            using var networkClient = new RendererNetworkClient(tabId, envelope => SendRendererEnvelope(writer, envelope));
            FenBrowser.Core.Network.HttpClientFactory.ConfigureRequestTransport(networkClient.SendAsync);

            // Container parsing and codec decoding run in the media process (design
            // §2.2, ADR-0004); the renderer keeps the element, the renderer and the
            // device. Until BLOCK-PROC-002 gives the renderer a real sandbox it can
            // launch that child itself; the child dies with the renderer's job.
            using var mediaProcess = FenBrowser.Host.ProcessIsolation.Media.MediaProcessIpc.IsMediaProcessEnabled()
                ? new FenBrowser.Host.ProcessIsolation.Media.MediaProcessClient()
                : null;
            FenBrowser.FenEngine.Media.MediaEngineServices.DecodeSources = mediaProcess;
            FenBrowser.FenEngine.Media.MediaEngineServices.RemoteDecoders = mediaProcess?.CreateRemoteDecoders(FenBrowser.FenEngine.Media.MediaEngineServices.Decoders);

            using var browser = new FenBrowser.FenEngine.Rendering.BrowserHost();
            using var logForwarder = new ChildProcessLogForwarder("renderer", tabId);
            var childRenderer = new FenBrowser.FenEngine.Rendering.SkiaDomRenderer();
            ConfigureRendererChildBrowser(browser, childRenderer);

            bool handshakeComplete = false;
            bool running = true;
            // Last cursor/href published to the broker, so a move that does not
            // change either sends nothing. A pointer move fires per frame.
            string lastPublishedCursor = null;
            string lastPublishedHref = null;
            bool hasFrameViewport = false;
            // The navigation in flight, if any. A navigation is not awaited by
            // the message loop: the broker asks for a frame roughly every 33ms
            // while a page loads, and those requests are what put the first
            // paint on screen. Awaiting here left them unread in the pipe until
            // the load finished, so the window stayed blank for the whole load
            // - 5.5s on google.com against 0.9s for the same engine in-process.
            // Navigations still run one at a time by chaining onto this task.
            Task pendingNavigation = Task.CompletedTask;
            float lastFrameViewportWidth = 1280f;
            float lastFrameViewportHeight = 720f;
            float lastFrameScrollY = 0f;
            int pendingRendererRepaintFrame = 0;
            // Animation ticks seen when the last frame went out, so a stall can report
            // how many ticks landed while no frame was published.
            long ticksAtLastFramePublish = 0;
            var frameProductionLock = new object();
            // Between a tick and a published frame sit two more steps that can drop the
            // frame: the engine deciding a tick changed nothing (no OnAnimationFrame),
            // and the child loop not reaching its drain. Count both so a stall says
            // which step lost it rather than only that it was lost.
            long animationFrameEvents = 0;
            long animationFrameEventsAtLastPublish = 0;
            long drainCalls = 0;
            long drainCallsAtLastPublish = 0;
            // Set when a CSS animation/transition tick produced changed values.
            // Without this pump the engine updates animation overlays internally
            // but nothing schedules a new frame, so animations freeze on screen.
            int pendingAnimationRepaintFrame = 0;
            var pendingAnimationWorkLock = new object();
            var pendingAnimationUpdateKind = FenBrowser.FenEngine.Rendering.AnimationUpdateKind.None;
            var pendingAnimationCompositeElements = new System.Collections.Generic.HashSet<FenBrowser.Core.Dom.V2.Element>();
            var pendingAnimationPaintElements = new System.Collections.Generic.HashSet<FenBrowser.Core.Dom.V2.Element>();
            long pendingAnimationGeneration = 0;
            // Set when the DOM signalled a repaint (RepaintReady): those frames may
            // carry style changes (class flips that toggle animations), so the
            // renderer must run its new-animation registration scan.
            int pendingRepaintNeedsStyleScan = 0;
            FenBrowser.Core.Dom.V2.Element lastBlinkCaretElement = null;
            int lastBlinkCaretOffset = -1;
            int lastBlinkCaretPhase = -1;

            // CSS animation/transition ticks must drive frame production in the child:
            // the engine updates animation overlays internally, but without this pump
            // nothing schedules the repaint that shows the animated value, so pages
            // freeze mid-animation (spinner not spinning, pop not playing).
            childRenderer.AnimationEngine.OnAnimationFrame += animation =>
            {
                if (!handshakeComplete || !hasFrameViewport)
                {
                    return;
                }

                lock (pendingAnimationWorkLock)
                {
                    pendingAnimationUpdateKind |= animation.UpdateKind;
                    if (animation.Element != null &&
                        (animation.UpdateKind & FenBrowser.FenEngine.Rendering.AnimationUpdateKind.Composite) != 0)
                    {
                        pendingAnimationCompositeElements.Add(animation.Element);
                    }

                    if (animation.Element != null &&
                        (animation.UpdateKind & (FenBrowser.FenEngine.Rendering.AnimationUpdateKind.Paint |
                                                 FenBrowser.FenEngine.Rendering.AnimationUpdateKind.Layout)) != 0)
                    {
                        pendingAnimationPaintElements.Add(animation.Element);
                    }

                    pendingAnimationGeneration = Math.Max(pendingAnimationGeneration, animation.Generation);
                }

                Interlocked.Increment(ref animationFrameEvents);
                Interlocked.Exchange(ref pendingAnimationRepaintFrame, 1);
            };

            void SendFrameReady(float viewportWidth, float viewportHeight, float scrollY, string requestedBy, string correlationId,
                FenBrowser.FenEngine.Rendering.Core.RenderFrameInvalidationReason invalidationReason =
                    FenBrowser.FenEngine.Rendering.Core.RenderFrameInvalidationReason.ProcessIsolation,
                FenBrowser.FenEngine.Rendering.AnimationUpdateKind animationUpdateKind =
                    FenBrowser.FenEngine.Rendering.AnimationUpdateKind.None,
                System.Collections.Generic.IReadOnlyCollection<FenBrowser.Core.Dom.V2.Element> compositeDirtyElements = null,
                System.Collections.Generic.IReadOnlyCollection<FenBrowser.Core.Dom.V2.Element> paintDirtyElements = null,
                long animationGeneration = 0)
            {
                // The frame pump and the IPC loop both produce frames, and a frame owns
                // the child's single bitmap, canvas and shared-memory slot. One producer
                // at a time; the loser waits rather than rasterising into the same pixels.
                lock (frameProductionLock)
                {
                    SendFrameReadyCore(viewportWidth, viewportHeight, scrollY, requestedBy, correlationId,
                        invalidationReason, animationUpdateKind, compositeDirtyElements, paintDirtyElements,
                        animationGeneration);
                }
            }

            void SendFrameReadyCore(float viewportWidth, float viewportHeight, float scrollY, string requestedBy, string correlationId,
                FenBrowser.FenEngine.Rendering.Core.RenderFrameInvalidationReason invalidationReason,
                FenBrowser.FenEngine.Rendering.AnimationUpdateKind animationUpdateKind,
                System.Collections.Generic.IReadOnlyCollection<FenBrowser.Core.Dom.V2.Element> compositeDirtyElements,
                System.Collections.Generic.IReadOnlyCollection<FenBrowser.Core.Dom.V2.Element> paintDirtyElements,
                long animationGeneration)
            {
                viewportWidth = Math.Max(1f, Math.Min(viewportWidth, FenBrowser.Host.ProcessIsolation.FrameSharedMemory.MaxWidth));
                viewportHeight = Math.Max(1f, Math.Min(viewportHeight, FenBrowser.Host.ProcessIsolation.FrameSharedMemory.MaxHeight));
                scrollY = Math.Max(0f, scrollY);
                var (rasterTop, rasterHeight) = ComputeBrokeredFrameRasterBand(viewportHeight, scrollY);
                float surfaceTopOffset = scrollY - rasterTop;

                int iWidth = (int)viewportWidth;
                int iHeight = (int)rasterHeight;
                float actualWidth = viewportWidth;
                float actualHeight = iHeight;
                uint seqNum = 0;
                FenBrowser.FenEngine.Rendering.Core.RenderFrameResult frameResult = null;

                // Lazily create the shared memory writer on first frame publication.
                if (frameSharedMemory == null)
                {
                    frameSharedMemory = FenBrowser.Host.ProcessIsolation.FrameSharedMemory.CreateForWriter(tabId, parentPid, capabilityToken: authToken);
                }

                if (frameSharedMemory != null)
                {
                    try
                    {
                        var frameStartTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
                        var gapMs = lastFramePublishTimestamp == 0
                            ? 0d
                            : System.Diagnostics.Stopwatch.GetElapsedTime(lastFramePublishTimestamp, frameStartTimestamp).TotalMilliseconds;

                        var snapshotStartTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
                        var domRoot = browser.GetDomRoot();
                        var styles = browser.ComputedStyles;
                        var snapshotMs = System.Diagnostics.Stopwatch.GetElapsedTime(snapshotStartTimestamp).TotalMilliseconds;

                        if (gapMs > 500d || snapshotMs > 100d)
                        {
                            // Report the animation timer alongside the gap. A stall whose
                            // tick count moved is a frame-delivery problem; one whose count
                            // stood still is the timer being starved or blocked, and a
                            // non-zero inFlight says a tick is stuck inside the engine.
                            var tickHealth = childRenderer.AnimationEngine.SnapshotTickHealth();
                            long ticksInGap = tickHealth.Started - ticksAtLastFramePublish;
                            long eventsInGap = Interlocked.Read(ref animationFrameEvents) - animationFrameEventsAtLastPublish;
                            long drainsInGap = Interlocked.Read(ref drainCalls) - drainCallsAtLastPublish;
                            EngineLog.Write(
                                LogSubsystem.ProcessIsolation,
                                LogSeverity.Warn,
                                $"[FrameStall] renderer child: {gapMs:F0}ms since the last published frame, " +
                                $"snapshot took {snapshotMs:F0}ms, " +
                                $"animTicks={ticksInGap} started={tickHealth.Started} " +
                                $"completed={tickHealth.Completed} inFlight={tickHealth.InFlight} " +
                                $"peakInFlight={tickHealth.InFlightPeak} longestTickMs={tickHealth.LongestMs} " +
                                $"animFrameEvents={eventsInGap} drains={drainsInGap} " +
                                $"running={childRenderer.AnimationEngine.IsRunning} " +
                                childRenderer.AnimationEngine.DescribeTickOutcomes());
                        }

                        if (domRoot != null)
                        {
                            var imageInfo = new SkiaSharp.SKImageInfo(iWidth, iHeight, SkiaSharp.SKColorType.Bgra8888, SkiaSharp.SKAlphaType.Premul);
                            if (frameBitmap == null || frameBitmap.Width != iWidth || frameBitmap.Height != iHeight)
                            {
                                frameCanvas?.Dispose();
                                frameBitmap?.Dispose();
                                frameBitmap = new SkiaSharp.SKBitmap(imageInfo);
                                frameCanvas = new SkiaSharp.SKCanvas(frameBitmap);
                            }

                            var bitmap = frameBitmap;
                            var canvas = frameCanvas;
                            canvas.Clear(SkiaSharp.SKColors.White);

                            // Document-space raster band: the visible viewport plus overdraw above
                            // and below, so the host's scroll preview has real pixels for whatever
                            // a scroll exposes before the next frame. The canvas is translated by
                            // -rasterTop so the band's top row lands at (0,0).
                            var viewport = new SkiaSharp.SKRect(0, rasterTop, viewportWidth, rasterTop + actualHeight);
                            canvas.Save();
                            if (rasterTop > 0f)
                            {
                                canvas.Translate(0, -rasterTop);
                            }
                            var contentHeightHint = Math.Max(
                                browser.Engine?.LastLayout?.ContentHeight ?? 0f,
                                rasterTop + actualHeight);
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
                                Styles = styles ?? new System.Collections.Generic.Dictionary<FenBrowser.Core.Dom.V2.Node, FenBrowser.Core.Css.CssComputed>(),
                                Viewport = viewport,
                                BaseUrl = browser.CurrentUri?.AbsoluteUri,
                                SeparateLayoutViewport = new SkiaSharp.SKSize(viewportWidth, viewportHeight),
                                HasBaseFrame = false,
                                InvalidationReason = invalidationReason,
                                RequestedBy = requestedBy,
                                AnimationUpdateKind = animationUpdateKind,
                                CompositeDirtyElements = compositeDirtyElements,
                                PaintDirtyElements = paintDirtyElements,
                                AnimationGeneration = animationGeneration,
                                EmitVerificationReport = false
                            });
                            canvas.Restore();
                            canvas.Flush();

                            int byteCount = iWidth * iHeight * 4;
                            frameSharedMemory.WriteFrame(
                                iWidth,
                                iHeight,
                                bitmap.GetPixels(),
                                byteCount);
                            frameSharedMemory.SignalReady();
                            seqNum = 1; // Approximate; actual seq tracked inside WriteFrame.

                            var frameMs = System.Diagnostics.Stopwatch.GetElapsedTime(frameStartTimestamp).TotalMilliseconds;
                            lastFramePublishTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
                            ticksAtLastFramePublish = childRenderer.AnimationEngine.SnapshotTickHealth().Started;
                            animationFrameEventsAtLastPublish = Interlocked.Read(ref animationFrameEvents);
                            drainCallsAtLastPublish = Interlocked.Read(ref drainCalls);
                            if (frameMs > 250d)
                            {
                                EngineLog.Write(
                                    LogSubsystem.ProcessIsolation,
                                    LogSeverity.Warn,
                                    $"[FrameStall] renderer child: producing one frame took {frameMs:F0}ms " +
                                    $"(snapshot {snapshotMs:F0}ms) requestedBy={requestedBy}");
                            }

                            EngineLog.Write(LogSubsystem.Paint, LogSeverity.Debug, $"[RendererChild] Frame written to shared memory: {iWidth}x{iHeight} for tab={tabId} requestedBy={requestedBy}");
                        }
                        else
                        {
                            EngineLog.Write(LogSubsystem.Paint, LogSeverity.Debug, $"[RendererChild] No DOM root for tab={tabId}; skipping frame write.");
                        }
                    }
                    catch (Exception renderEx)
                    {
                        EngineLog.Write(LogSubsystem.Paint, LogSeverity.Warn, $"[RendererChild] Frame render failed for tab={tabId}: {renderEx}");
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
                    SurfaceTopOffset = surfaceTopOffset,
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
                Interlocked.Increment(ref drainCalls);
                // Check readiness BEFORE consuming the pending flags. Taking them
                // first and then bailing out threw the request away: an image that
                // finished decoding before the first frame viewport arrived lost its
                // repaint entirely, and the decoded bitmap was not drawn until some
                // unrelated event rebuilt the paint tree - which is why Hacker News's
                // upvote arrows only appeared once the pointer moved.
                if (!handshakeComplete || !hasFrameViewport)
                {
                    return;
                }

                bool animationDriven = Interlocked.Exchange(ref pendingAnimationRepaintFrame, 0) == 1;
                bool domRepaint = Interlocked.Exchange(ref pendingRendererRepaintFrame, 0) == 1;
                // A DOM-change frame may toggle animation applicability (class flips
                // that show/hide animated elements), so it must carry the Style reason
                // and re-run the renderer's new-animation registration scan.
                bool needsStyleScan = domRepaint && Interlocked.Exchange(ref pendingRepaintNeedsStyleScan, 0) == 1;
                if (!animationDriven && !domRepaint)
                {
                    return;
                }

                var animationUpdateKind = FenBrowser.FenEngine.Rendering.AnimationUpdateKind.None;
                FenBrowser.Core.Dom.V2.Element[] compositeDirtyElements = null;
                FenBrowser.Core.Dom.V2.Element[] paintDirtyElements = null;
                long animationGeneration = 0;
                if (animationDriven)
                {
                    lock (pendingAnimationWorkLock)
                    {
                        animationUpdateKind = pendingAnimationUpdateKind;
                        compositeDirtyElements = pendingAnimationCompositeElements.Count > 0
                            ? pendingAnimationCompositeElements.ToArray()
                            : Array.Empty<FenBrowser.Core.Dom.V2.Element>();
                        paintDirtyElements = pendingAnimationPaintElements.Count > 0
                            ? pendingAnimationPaintElements.ToArray()
                            : Array.Empty<FenBrowser.Core.Dom.V2.Element>();
                        animationGeneration = pendingAnimationGeneration;

                        pendingAnimationUpdateKind = FenBrowser.FenEngine.Rendering.AnimationUpdateKind.None;
                        pendingAnimationCompositeElements.Clear();
                        pendingAnimationPaintElements.Clear();
                    }
                }

                var invalidationReason = needsStyleScan
                    ? FenBrowser.FenEngine.Rendering.Core.RenderFrameInvalidationReason.Style
                    : domRepaint
                        ? FenBrowser.FenEngine.Rendering.Core.RenderFrameInvalidationReason.ProcessIsolation
                        : FenBrowser.FenEngine.Rendering.Core.RenderFrameInvalidationReason.None;
                if (animationDriven)
                {
                    invalidationReason |= FenBrowser.FenEngine.Rendering.Core.RenderFrameInvalidationReason.Animation;
                    if ((animationUpdateKind & FenBrowser.FenEngine.Rendering.AnimationUpdateKind.Layout) != 0)
                    {
                        invalidationReason |= FenBrowser.FenEngine.Rendering.Core.RenderFrameInvalidationReason.Layout |
                                              FenBrowser.FenEngine.Rendering.Core.RenderFrameInvalidationReason.Paint;
                    }
                    else if ((animationUpdateKind & FenBrowser.FenEngine.Rendering.AnimationUpdateKind.Paint) != 0)
                    {
                        invalidationReason |= FenBrowser.FenEngine.Rendering.Core.RenderFrameInvalidationReason.Paint;
                    }
                }

                SendFrameReady(
                    lastFrameViewportWidth,
                    lastFrameViewportHeight,
                    lastFrameScrollY,
                    domRepaint && animationDriven
                        ? "RendererChild.RepaintReady+AnimationTick"
                        : domRepaint ? "RendererChild.RepaintReady" : "RendererChild.AnimationTick",
                    Guid.NewGuid().ToString("N"),
                    invalidationReason,
                    animationUpdateKind,
                    compositeDirtyElements,
                    paintDirtyElements,
                    animationGeneration);
            }

            void CheckTextCaretBlink()
            {
                if (!handshakeComplete || !hasFrameViewport)
                {
                    return;
                }

                var state = FenBrowser.FenEngine.Rendering.ElementStateManager.Instance.GetTextCaretState();
                var caretElement = state.Element;
                if (caretElement == null)
                {
                    lastBlinkCaretElement = null;
                    lastBlinkCaretOffset = -1;
                    lastBlinkCaretPhase = -1;
                    return;
                }

                var caretOffset = state.Offset;
                var elapsed = Math.Max(0, (DateTime.UtcNow - state.LastChangeUtc).TotalMilliseconds);
                var phase = (int)((long)elapsed % FenBrowser.FenEngine.Rendering.SkiaDomRenderer.TextCaretBlinkPeriodMs
                                  / FenBrowser.FenEngine.Rendering.SkiaDomRenderer.TextCaretVisibleMs);

                if (ReferenceEquals(caretElement, lastBlinkCaretElement) &&
                    caretOffset == lastBlinkCaretOffset &&
                    phase == lastBlinkCaretPhase)
                {
                    return;
                }

                lastBlinkCaretElement = caretElement;
                lastBlinkCaretOffset = caretOffset;
                lastBlinkCaretPhase = phase;

                SendFrameReady(
                    lastFrameViewportWidth,
                    lastFrameViewportHeight,
                    lastFrameScrollY,
                    "RendererChild.TextCaretBlink",
                    Guid.NewGuid().ToString("N"));
            }

            void SendMetadata(
                string title = null,
                SkiaSharp.SKBitmap favicon = null,
                bool faviconChanged = false,
                bool urlChanged = false)
            {
                // Allow favicon/title metadata to be sent even before handshake completes
                // for better perceived loading performance. Only block URL changes pre-handshake.
                bool isCriticalMetadata = faviconChanged || !string.IsNullOrWhiteSpace(title);
                if (!handshakeComplete && !isCriticalMetadata)
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

                if (!ShouldPublishRendererMetadata(title, faviconChanged, urlChanged))
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

            browser.Navigated += (_, _) => SendMetadata(urlChanged: true);
            browser.TitleChanged += (_, title) => SendMetadata(title: title);
            browser.FaviconChanged += (_, favicon) => SendMetadata(favicon: favicon, faviconChanged: true);

            // Forward page-visible navigation lifecycle transitions to the broker.
            // Transitions are attributed to their navigation via the binder: the
            // correlation of the Navigate envelope being processed binds to the
            // engine navigation id it starts, so a navigation initiated inside
            // the child (script, input) forwards an empty correlation instead of
            // inheriting the previous WebDriver one.
            var correlationBinder = new RendererNavigationCorrelationBinder();
            browser.NavigationLifecycleChanged += (_, transition) =>
            {
                var transitionCorrelation = correlationBinder.Observe(transition.NavigationId, transition.Phase);
                if (!handshakeComplete || !ShouldForwardRendererLifecyclePhase(transition.Phase))
                {
                    return;
                }

                SendRendererEnvelope(writer, new RendererIpcEnvelope
                {
                    Type = RendererIpcMessageType.NavigationLifecycle.ToString(),
                    TabId = tabId,
                    CorrelationId = Guid.NewGuid().ToString("N"),
                    Payload = RendererIpc.SerializePayload(new RendererNavigationLifecyclePayload
                    {
                        NavigationCorrelationId = transitionCorrelation ?? string.Empty,
                        NavigationId = transition.NavigationId,
                        Phase = transition.Phase.ToString(),
                        EffectiveUrl = transition.EffectiveUrl,
                        Detail = transition.Detail
                    })
                });
            };
            browser.RepaintReady += (_, __) =>
            {
                // Remember the request even if the viewport is not known yet - the
                // first frame viewport can arrive after an image has already decoded,
                // and dropping the signal here left that image unpainted.
                Interlocked.Exchange(ref pendingRendererRepaintFrame, 1);
            };

            // Frames must not be produced on the thread that handles IPC messages. An
            // Input message awaits the JS worker, and that wait is capped at 2s, so a
            // page running a long script starved frame production for seconds at a
            // time: measured 172 animation frame events delivered against a single
            // drain in 2003ms, which is the reCAPTCHA spinner jumping instead of
            // spinning. This pump drains on its own thread, so a blocked dispatch
            // costs input latency and nothing else.
            using var framePumpCancellation = new CancellationTokenSource();
            var framePump = Task.Run(async () =>
            {
                using var pump = new PeriodicTimer(TimeSpan.FromMilliseconds(8));
                try
                {
                    while (await pump.WaitForNextTickAsync(framePumpCancellation.Token).ConfigureAwait(false))
                    {
                        try
                        {
                            DrainPendingRendererRepaintFrame();
                            CheckTextCaretBlink();
                        }
                        catch (Exception pumpEx)
                        {
                            EngineLog.Write(
                                LogSubsystem.Paint,
                                LogSeverity.Warn,
                                $"[RendererChild] Frame pump iteration failed for tab={tabId}: {pumpEx.Message}");
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                }
            });

            // Input latency accounting: where a click's time goes between the
            // broker stamping the envelope and this loop finishing with it.
            var inputStats = new RendererChildInputStats(tabId);

            // Frame production belongs to the pump above, never to this loop. When
            // this loop also drained frames, each drain rasterised a full 3840x2160
            // surface (~150ms) before the next single line was read: measured 400-880ms
            // of every second spent at the loop top, ~5 input envelopes read per second
            // against ~90 sent, and pointer events reaching the page 18-23s after the
            // click once the broker's bounded queue had filled.
            while (running)
            {
                var loopTopStart = Stopwatch.GetTimestamp();
                if (handshakeComplete)
                {
                    logForwarder.FlushRenderer(writer, tabId);
                }
                inputStats.RecordLoopTop(Stopwatch.GetElapsedTime(loopTopStart));

                if (!IsParentAlive(parentPid))
                {
                    break;
                }

                var readResult = await RendererChildLoopIo.ReadLineWithTimeoutAsync(
                    reader,
                    childRenderer.AnimationEngine.IsRunning
                        ? TimeSpan.FromMilliseconds(8)
                        : TimeSpan.FromMilliseconds(100)).ConfigureAwait(false);
                inputStats.MaybeFlush();
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

                    if (rendererMessageType == RendererIpcMessageType.NetworkFetchBodyPipe)
                    {
                        networkClient.OnBodyPipe(envelope);
                        continue;
                    }

                    if (rendererMessageType == RendererIpcMessageType.NetworkFetchResponseHead)
                    {
                        networkClient.OnResponseHead(envelope);
                        continue;
                    }

                    if (rendererMessageType == RendererIpcMessageType.NetworkFetchFailed)
                    {
                        networkClient.OnFailed(envelope);
                        continue;
                    }

                    if (rendererMessageType == RendererIpcMessageType.Navigate)
                    {
                        var payload = RendererIpc.DeserializePayload<RendererNavigatePayload>(envelope);
                        var url = payload?.Url ?? string.Empty;
                        var navigationCorrelationId = payload?.NavigationCorrelationId;
                        var navigateIsUserInput = payload?.IsUserInput ?? false;
                        var navigateCorrelation = envelope.CorrelationId;
                        if (payload != null && payload.ViewportWidth > 1f && payload.ViewportHeight > 1f)
                        {
                            browser.UpdateViewportHint(payload.ViewportWidth, payload.ViewportHeight);
                        }

                        pendingNavigation = RunChildNavigationAsync(
                            pendingNavigation,
                            browser,
                            correlationBinder,
                            writer,
                            tabId,
                            url,
                            navigateIsUserInput,
                            navigationCorrelationId,
                            navigateCorrelation);
                        continue;
                    }

                    if (rendererMessageType == RendererIpcMessageType.Input)
                    {
                        var input = RendererIpc.DeserializePayload<RendererInputEvent>(envelope);
                        if (input != null && input.IsMeaningful)
                        {
                            var receiveLagMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - envelope.TimestampUnixMs;
                            var dispatchStart = Stopwatch.GetTimestamp();
                            await DispatchRendererInputAsync(browser, input).ConfigureAwait(false);
                            inputStats.RecordInput(input, receiveLagMs, Stopwatch.GetElapsedTime(dispatchStart));

                            // Only this process can answer "what is under the
                            // pointer" - the broker has no document in brokered
                            // mode, so its own hit test comes back empty and the
                            // cursor stayed an arrow over every link. Resolve it
                            // here and tell the broker when it changes.
                            if (input.Type == RendererInputEventType.MouseMove)
                            {
                                PublishRendererCursor(writer, tabId, childRenderer, input.X, input.Y,
                                    ref lastPublishedCursor, ref lastPublishedHref);
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

                        // The broker asks for a frame on every paint (rate-limited to
                        // 33ms). Producing it here put a full raster on this thread
                        // between two input reads, which is where the remaining
                        // 100-370ms of click latency lived after frame draining moved
                        // to the pump. Hand the request to the pump instead; it runs
                        // every 8ms so the frame is still prompt.
                        Interlocked.Exchange(ref pendingRendererRepaintFrame, 1);
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

            // The pump renders into frameBitmap/frameCanvas, so it has to be stopped
            // and joined before those are disposed.
            framePumpCancellation.Cancel();
            try
            {
                await framePump.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }

            frameSharedMemory?.Dispose();
            frameCanvas?.Dispose();
            frameBitmap?.Dispose();
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
                                await using var bodyPipe = await NetworkBodyPipe.ConnectClientAsync(
                                    payload.BodyPipeName,
                                    payload.BodyPipeToken,
                                    linkedCts.Token).ConfigureAwait(false);
                                using var requestBody = payload.HasBody
                                    ? bodyPipe.OpenReadStream(long.MaxValue)
                                    : null;
                                using var request = NetworkFetchMessages.BuildRequest(payload, requestBody);
                                using var response = await SendNetworkRequestAsync(httpClient, noProxyClient, request, linkedCts.Token).ConfigureAwait(false);

                                SendNetworkEnvelope(writer, new NetworkIpcEnvelope
                                {
                                    Type = NetworkIpcMessageType.FetchResponseHead.ToString(),
                                    RequestId = envelope.RequestId,
                                    CapabilityToken = envelope.CapabilityToken,
                                    Payload = NetworkIpc.SerializePayload(
                                        NetworkFetchMessages.BuildResponseHead(response, envelope.RequestId, payload.Mode, payload.Url))
                                });

                                using var bodyStream = await response.Content.ReadAsStreamAsync(linkedCts.Token).ConfigureAwait(false);
                                await bodyPipe.SendStreamAsync(bodyStream, linkedCts.Token).ConfigureAwait(false);
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
            var contract = expectedKind switch
            {
                TargetProcessKind.Gpu => GpuProcessIpc.Contract,
                TargetProcessKind.Media => FenBrowser.Host.ProcessIsolation.Media.MediaProcessIpc.Contract,
                _ => UtilityProcessIpc.Contract
            };
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
            using var mediaService = expectedKind == TargetProcessKind.Media
                ? new FenBrowser.Host.ProcessIsolation.Media.MediaChildService(
                    FenBrowser.FenEngine.Media.MediaEngineServices.Demuxers,
                    FenBrowser.FenEngine.Media.MediaEngineServices.Decoders,
                    FenBrowser.FenEngine.Media.MediaEngineServices.Log)
                : null;

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

                    if (mediaService != null &&
                        await mediaService.HandleAsync(envelope, targetMessageType, response => SendTargetEnvelope(writer, response), CancellationToken.None).ConfigureAwait(false))
                    {
                        continue;
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

            // A sandboxed child on Linux runs in its own PID namespace (bwrap
            // --unshare-all), where the broker's pid does not exist at all, so
            // "not found" says nothing about whether the broker is alive - every
            // child exited on its first loop iteration here. On Unix the pipe is
            // the liveness signal: the read loop ends on EOF when the broker goes
            // away, and bwrap's --die-with-parent kills the child if the broker
            // dies without closing anything.
            if (!OperatingSystem.IsWindows())
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

        // Runs one navigation without holding up the child's message loop, and
        // after whatever navigation preceded it so two never overlap. The Ack
        // still reports real completion; the broker uses it only for latency,
        // so reporting it late costs nothing and reporting it early would lie.
        private static async Task RunChildNavigationAsync(
            Task previous,
            FenBrowser.FenEngine.Rendering.BrowserHost browser,
            RendererNavigationCorrelationBinder correlationBinder,
            StreamWriter writer,
            int tabId,
            string url,
            bool isUserInput,
            string navigationCorrelationId,
            string ackCorrelationId)
        {
            try
            {
                await previous.ConfigureAwait(false);
            }
            catch
            {
                // A failed navigation must not stop the one after it.
            }

            try
            {
                correlationBinder.BeginNavigation(navigationCorrelationId);
                if (!string.IsNullOrWhiteSpace(url))
                {
                    if (isUserInput)
                        await browser.NavigateUserInputAsync(url).ConfigureAwait(false);
                    else
                        await browser.NavigateAsync(url).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                EngineLog.Write(
                    LogSubsystem.ProcessIsolation,
                    LogSeverity.Warn,
                    $"[RendererChild] Navigation to '{url}' failed: {ex.Message}");
            }
            finally
            {
                // The envelope's navigation is done: a later engine navigation
                // started inside the child (script, input) binds no correlation.
                correlationBinder.EndNavigation();
                SendRendererEnvelope(writer, new RendererIpcEnvelope
                {
                    Type = RendererIpcMessageType.Ack.ToString(),
                    TabId = tabId,
                    CorrelationId = ackCorrelationId
                });
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
                lock (writer)
                {
                    writer.WriteLine(NetworkIpc.Serialize(envelope));
                    writer.Flush();
                }
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

                using var bitmap = DecodeImage(payload.Data, payload.TargetWidth, payload.TargetHeight, payload.Url);

                byte[] bitmapBytes = null;
                int width = 0, height = 0;
                string budgetReason = null;
                if (bitmap != null && !bitmap.IsNull)
                {
                    using var image = SKImage.FromBitmap(bitmap);
                    using var data = image.Encode(SKEncodedImageFormat.Png, 100);
                    var encoded = data?.ToArray() ?? Array.Empty<byte>();
                    if (FitsTargetResponseBudget(encoded))
                    {
                        width = bitmap.Width;
                        height = bitmap.Height;
                        bitmapBytes = encoded;
                    }
                    else
                    {
                        budgetReason = OversizedResponseReason("Image decode");
                    }
                    bitmap.Dispose();
                }

                var response = new ImageDecodeResponsePayload
                {
                    Success = bitmapBytes != null && bitmapBytes.Length > 0,
                    ErrorMessage = bitmapBytes == null || bitmapBytes.Length == 0
                        ? budgetReason ?? "Decode returned no bitmap"
                        : null,
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

        internal static Task HandleSvgDecodeRequest(StreamWriter writer, TargetIpcEnvelope envelope)
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

                var svgRenderer = SvgRendererFactory.GetConfiguredRenderer();
                var limits = NormalizeSvgDecodeLimits(payload.Limits);
                // Target-process decoding always serves an image context: script is inert.
                limits.TreatScriptsAsInert = true;
                limits.LayOutTextAreasOnOneLine = true;

                using var result = svgRenderer.Render(new SvgRenderRequest(payload.SvgContent, limits)
                {
                    DiagnosticSource = "target-process"
                });

                byte[] bitmapBytes = null;
                int width = 0, height = 0;
                string failureReason = RejectSvgDecodeResult(result, "svg-decode");

                if (failureReason == null && result.Bitmap != null && !result.Bitmap.IsNull)
                {
                    using var image = SKImage.FromBitmap(result.Bitmap);
                    using var data = image.Encode(SKEncodedImageFormat.Png, 100);
                    var encoded = data?.ToArray() ?? Array.Empty<byte>();
                    if (FitsTargetResponseBudget(encoded))
                    {
                        width = result.Bitmap.Width;
                        height = result.Bitmap.Height;
                        bitmapBytes = encoded;
                    }
                    else
                    {
                        failureReason = OversizedResponseReason("SVG decode");
                    }
                }

                bool decodeOk = bitmapBytes != null && bitmapBytes.Length > 0;

                var response = new SvgDecodeResponsePayload
                {
                    Success = decodeOk,
                    ErrorMessage = decodeOk ? null : failureReason ?? "SVG decode produced no bitmap",
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

        internal static SvgRenderLimits NormalizeSvgDecodeLimits(SvgRenderLimitsData limits)
        {
            if (limits == null)
            {
                return SvgRenderLimits.Normalize(SvgRenderLimits.Default);
            }

            return SvgRenderLimits.Normalize(new SvgRenderLimits
            {
                MaxElementCount = limits.MaxElementCount,
                MaxFilterCount = limits.MaxFilterCount,
                MaxRecursionDepth = limits.MaxRecursionDepth,
                MaxRenderTimeMs = limits.MaxRenderTimeMs,
                MaxSourceChars = limits.MaxSourceChars,
                MaxRasterWidth = limits.MaxRasterWidth,
                MaxRasterHeight = limits.MaxRasterHeight,
                MaxRasterPixels = limits.MaxRasterPixels,
                MaxDecodedImagePixels = limits.MaxDecodedImagePixels,
                MaxDecodedImageBytes = limits.MaxDecodedImageBytes,
                MaxCumulativeResourceBytes = limits.MaxCumulativeResourceBytes,
                MaxResourceCount = limits.MaxResourceCount,
                MaxActiveLayers = limits.MaxActiveLayers,
                MaxReferenceDepth = limits.MaxReferenceDepth,
                AllowExternalReferences = false
            });
        }

        internal const int MaxTargetResponseBitmapBytes = ((TargetIpc.MaxPayloadChars - 1024) / 4) * 3;

        internal static bool FitsTargetResponseBudget(byte[] bitmapBytes) =>
            bitmapBytes == null || bitmapBytes.Length <= MaxTargetResponseBitmapBytes;

        internal static string OversizedResponseReason(string entryPoint) =>
            TargetIpc.BoundMetadata(
                $"{entryPoint} response exceeds the target IPC payload budget " +
                $"({MaxTargetResponseBitmapBytes} bytes)",
                TargetIpc.MaxResponseMetadataChars);

        /// <summary>
        /// Fail-closed admission gate for target-process SVG decoding. Returns null
        /// when the rendered pixels may be returned to the requester, otherwise the
        /// bounded rejection reason. Rejected results must never yield a bitmap:
        /// partially rendered or resource-stripped pixels are not authoritative.
        /// The first-party engine is the only admissible backend.
        /// </summary>
        internal static string RejectSvgDecodeResult(SvgRenderResult result, string entryPoint)
        {
            bool admissible = SvgRenderResult.IsAdmissible(result);
            if (admissible && result.Backend == SvgRendererBackend.FirstParty)
            {
                return null;
            }

            var reason = admissible
                ? "SVG render produced pixels from an unsupported backend"
                : SvgRenderResult.DescribeRejection(result) ?? "SVG render rejected";
            EngineLog.Write(
                LogSubsystem.ProcessIsolation,
                LogSeverity.Warn,
                $"[Target] SVG decode rejected ({entryPoint}): {reason}");

            return TargetIpc.BoundMetadata(reason, TargetIpc.MaxResponseMetadataChars);
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

        internal static SKBitmap DecodeImage(byte[] data, int? targetWidth, int? targetHeight, string url)
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
                    var svgRenderer = SvgRendererFactory.GetConfiguredRenderer();
                    using var result = svgRenderer.Render(svgContent);
                    if (RejectSvgDecodeResult(result, "image-decode") != null)
                    {
                        return null;
                    }
                    return result.DetachBitmap();
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
                        var resized = new SKBitmap(info);
                        try
                        {
                            using var canvas = new SKCanvas(resized);
                            canvas.DrawBitmap(bitmap, new SKRect(0, 0, dstW, dstH), SKSamplingOptions.Default);
                        }
                        catch
                        {
                            resized.Dispose();
                            throw;
                        }

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

        internal static void SendErrorResponse(StreamWriter writer, string requestId, TargetIpcMessageType responseType, string errorMessage)
        {
            SendTargetEnvelope(writer, new TargetIpcEnvelope
            {
                Type = responseType.ToString(),
                RequestId = requestId,
                Payload = TargetIpc.SerializePayload(new
                {
                    Success = false,
                    ErrorMessage = TargetIpc.BoundMetadata(errorMessage, TargetIpc.MaxResponseMetadataChars)
                })
            });
        }

        internal static void SendTargetEnvelope(StreamWriter writer, TargetIpcEnvelope envelope)
        {
            if (writer == null || envelope == null)
            {
                return;
            }

            if (!TargetIpc.TrySerializeEnvelope(envelope, out var line, out var rejectionReason))
            {
                EngineLog.Write(
                    LogSubsystem.ProcessIsolation,
                    LogSeverity.Warn,
                    $"[TargetChild] Rejected outbound IPC envelope: {rejectionReason}.");
                return;
            }

            try
            {
                lock (writer)
                {
                    writer.WriteLine(line);
                    writer.Flush();
                }
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
            catch (HttpRequestException ex) when (IsLoopbackProxyRefusal(ex) && request.Content == null)
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

        // Resolve what the pointer is over and forward it to the broker, but only
        // when it differs from what was sent last: a mouse move arrives per
        // frame, and an unchanged cursor is not worth an envelope.
        private static void PublishRendererCursor(
            StreamWriter writer,
            int tabId,
            FenBrowser.FenEngine.Rendering.SkiaDomRenderer renderer,
            float x,
            float y,
            ref string lastCursor,
            ref string lastHref)
        {
            if (writer == null || renderer == null)
            {
                return;
            }

            string cursor;
            string href;
            string tagName;
            try
            {
                if (renderer.HitTest(x, y, out var hit))
                {
                    cursor = hit.Cursor.ToString();
                    href = hit.Href ?? string.Empty;
                    tagName = hit.TagName ?? string.Empty;
                }
                else
                {
                    cursor = FenBrowser.FenEngine.Interaction.CursorType.Default.ToString();
                    href = string.Empty;
                    tagName = string.Empty;
                }
            }
            catch (Exception ex)
            {
                // A hit test racing a layout pass must never take the child down.
                EngineLog.Write(
                    LogSubsystem.ProcessIsolation,
                    LogSeverity.Debug,
                    $"[RendererChild] Cursor hit test failed for tab={tabId}: {ex.Message}");
                return;
            }

            if (string.Equals(cursor, lastCursor, StringComparison.Ordinal) &&
                string.Equals(href, lastHref, StringComparison.Ordinal))
            {
                return;
            }

            lastCursor = cursor;
            lastHref = href;

            SendRendererEnvelope(writer, new RendererIpcEnvelope
            {
                Type = RendererIpcMessageType.CursorChanged.ToString(),
                TabId = tabId,
                CorrelationId = Guid.NewGuid().ToString("N"),
                Payload = RendererIpc.SerializePayload(new RendererCursorChangedPayload
                {
                    Cursor = cursor,
                    Href = href,
                    TagName = tagName
                })
            });
        }

        /// <summary>
        /// Per-second summary of the renderer child's input handling: how late each
        /// envelope arrives relative to the broker's stamp, how long the child spends
        /// dispatching it, and how long the loop top (frame drain, log flush) holds
        /// the reader. Pointer clicks are also logged individually.
        /// </summary>
        private sealed class RendererChildInputStats
        {
            private readonly int _tabId;
            private long _windowStart = Stopwatch.GetTimestamp();
            private int _moves;
            private int _others;
            private double _maxLagMs;
            private double _sumDispatchMs;
            private double _maxDispatchMs;
            private string _maxDispatchType = string.Empty;
            private double _sumLoopTopMs;
            private double _maxLoopTopMs;

            public RendererChildInputStats(int tabId)
            {
                _tabId = tabId;
            }

            public void RecordLoopTop(TimeSpan elapsed)
            {
                var ms = elapsed.TotalMilliseconds;
                _sumLoopTopMs += ms;
                if (ms > _maxLoopTopMs)
                {
                    _maxLoopTopMs = ms;
                }
            }

            public void RecordInput(RendererInputEvent input, long receiveLagMs, TimeSpan dispatch)
            {
                var dispatchMs = dispatch.TotalMilliseconds;
                if (input.Type == RendererInputEventType.MouseMove)
                {
                    _moves++;
                }
                else
                {
                    _others++;
                }

                if (receiveLagMs > _maxLagMs)
                {
                    _maxLagMs = receiveLagMs;
                }

                _sumDispatchMs += dispatchMs;
                if (dispatchMs > _maxDispatchMs)
                {
                    _maxDispatchMs = dispatchMs;
                    _maxDispatchType = input.Type.ToString();
                }

                if (input.Type is RendererInputEventType.MouseDown or RendererInputEventType.MouseUp)
                {
                    EngineLog.Write(
                        LogSubsystem.Event,
                        LogSeverity.Info,
                        $"[InputLatency] {input.Type} document=({input.X:F1},{input.Y:F1}) receiveLagMs={receiveLagMs} dispatchMs={dispatchMs:F1}");
                }
            }

            public void MaybeFlush()
            {
                if (Stopwatch.GetElapsedTime(_windowStart).TotalMilliseconds < 1000)
                {
                    return;
                }

                if (_moves + _others > 0 || _maxLoopTopMs > 20)
                {
                    EngineLog.Write(
                        LogSubsystem.Event,
                        LogSeverity.Info,
                        $"[InputLatency] tab={_tabId} window=1s moves={_moves} others={_others} maxReceiveLagMs={_maxLagMs:F0} " +
                        $"dispatchTotalMs={_sumDispatchMs:F0} maxDispatchMs={_maxDispatchMs:F1}({_maxDispatchType}) " +
                        $"loopTopTotalMs={_sumLoopTopMs:F0} maxLoopTopMs={_maxLoopTopMs:F1}");
                }

                _windowStart = Stopwatch.GetTimestamp();
                _moves = 0;
                _others = 0;
                _maxLagMs = 0;
                _sumDispatchMs = 0;
                _maxDispatchMs = 0;
                _maxDispatchType = string.Empty;
                _sumLoopTopMs = 0;
                _maxLoopTopMs = 0;
            }
        }

        private static readonly bool DumpFramesOnClick =
            !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("FEN_DUMP_FRAMES_ON_CLICK"));

        /// <summary>
        /// FEN_DUMP_FRAMES_ON_CLICK=1: after every click, write each iframe's live
        /// document to logs/frame-dumps. The engine-source snapshot only covers the
        /// top document, and reCAPTCHA keeps all of its state in child frames, so
        /// without this there is no way to tell "the click changed nothing" from
        /// "the change was never painted".
        /// </summary>
        private static void DumpFrameDocumentsAfterClick(BrowserHost browser)
        {
            if (!DumpFramesOnClick)
            {
                return;
            }

            try
            {
                var root = browser.GetDomRoot();
                if (root == null)
                {
                    return;
                }

                var dir = Path.Combine(FenBrowser.Core.Logging.DiagnosticPaths.GetLogsDirectory(), "frame-dumps");
                Directory.CreateDirectory(dir);
                var stamp = DateTime.Now.ToString("HHmmss_fff");
                var index = 0;
                foreach (var node in FenBrowser.Core.Dom.V2.DomExtensions.DescendantsAndSelf(root))
                {
                    if (node is not FenBrowser.Core.Dom.V2.Element frame ||
                        !string.Equals(frame.LocalName, "iframe", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var children = frame.ChildNodes;
                    for (int i = 0; children != null && i < children.Length; i++)
                    {
                        if (children[i] is not FenBrowser.Core.Dom.V2.Document frameDocument)
                        {
                            continue;
                        }

                        var html = frameDocument.DocumentElement?.OuterHTML;
                        if (string.IsNullOrEmpty(html))
                        {
                            continue;
                        }

                        var path = Path.Combine(dir, $"{stamp}_frame{index++}.html");
                        File.WriteAllText(path, "<!-- src=" + frame.GetAttribute("src") + " -->" + Environment.NewLine + html);
                    }
                }

                EngineLog.Write(LogSubsystem.Event, LogSeverity.Info, $"[FrameDump] wrote {index} frame document(s) to {dir} ({stamp})");
            }
            catch (Exception ex)
            {
                EngineLog.Write(LogSubsystem.Event, LogSeverity.Warn, $"[FrameDump] failed: {ex.Message}");
            }
        }

        internal static async Task DispatchRendererInputAsync(BrowserHost browser, RendererInputEvent input)
        {
            ArgumentNullException.ThrowIfNull(browser);
            ArgumentNullException.ThrowIfNull(input);

            if (input.Type is RendererInputEventType.MouseDown or RendererInputEventType.MouseUp)
            {
                EngineLog.Write(
                    LogSubsystem.Event,
                    LogSeverity.Info,
                    $"[InputPipeline] renderer received {input.Type} document=({input.X:F1},{input.Y:F1}) emitClick={input.EmitClick}");
            }

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
                    DumpFrameDocumentsAfterClick(browser);
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

        internal static void ConfigureRendererChildBrowser(BrowserHost browser, SkiaDomRenderer renderer)
        {
            ArgumentNullException.ThrowIfNull(browser);
            ArgumentNullException.ThrowIfNull(renderer);

            // The renderer child must use one renderer instance for both frame
            // production and input hit testing. Otherwise BrowserHost falls back
            // to CustomHtmlEngine's private cached renderer, whose layout snapshot
            // can predate dynamically attached iframe documents.
            browser.SetActiveRenderer(renderer);
            browser.Engine.SetExternalRenderer(renderer);
        }

        internal static bool ShouldPublishRendererMetadata(string title, bool faviconChanged, bool urlChanged) =>
            !string.IsNullOrWhiteSpace(title) || faviconChanged || urlChanged;

        /// <summary>
        /// Only terminal or page-visible lifecycle transitions cross the renderer
        /// IPC boundary: the WebDriver page-load strategy waits for Interactive
        /// (DOMContentLoaded) or Complete (load) and must observe Failed or
        /// Cancelled promptly; intermediate fetch phases stay local diagnostics.
        /// </summary>
        internal static bool ShouldForwardRendererLifecyclePhase(
            FenBrowser.Core.Engine.NavigationLifecyclePhase phase) =>
            phase == FenBrowser.Core.Engine.NavigationLifecyclePhase.Interactive ||
            phase == FenBrowser.Core.Engine.NavigationLifecyclePhase.Complete ||
            phase == FenBrowser.Core.Engine.NavigationLifecyclePhase.Failed ||
            phase == FenBrowser.Core.Engine.NavigationLifecyclePhase.Cancelled;

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
