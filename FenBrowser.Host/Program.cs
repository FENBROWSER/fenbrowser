using System;
using System.Threading.Tasks;
using FenBrowser.Core;
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

                // 3. Initialize Window Manager
                var windowManager = WindowManager.Instance;
                windowManager.Initialize(initialUrl);

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
                windowManager.Run();
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

            while (running)
            {
                if (handshakeComplete)
                {
                    logForwarder.FlushRenderer(writer, tabId);
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
                            switch (input.Type)
                            {
                                case RendererInputEventType.MouseDown:
                                    browser.OnMouseDown(input.X, input.Y, input.Button);
                                    break;
                                case RendererInputEventType.MouseUp:
                                    browser.OnMouseUp(input.X, input.Y, input.Button);
                                    if (input.ShouldEmitClick)
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

                    if (rendererMessageType == RendererIpcMessageType.FrameRequest)
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
                        FenBrowser.FenEngine.Rendering.Core.RenderFrameResult frameResult = null;

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
                                    frameResult = childRenderer.RenderFrame(new FenBrowser.FenEngine.Rendering.Core.RenderFrameRequest
                                    {
                                        Root = domRoot,
                                        Canvas = canvas,
                                        Styles = styles != null
                                            ? new System.Collections.Generic.Dictionary<FenBrowser.Core.Dom.V2.Node, FenBrowser.Core.Css.CssComputed>(styles)
                                            : new System.Collections.Generic.Dictionary<FenBrowser.Core.Dom.V2.Node, FenBrowser.Core.Css.CssComputed>(),
                                        Viewport = viewport,
                                        BaseUrl = browser.CurrentUri?.AbsoluteUri,
                                        InvalidationReason = FenBrowser.FenEngine.Rendering.Core.RenderFrameInvalidationReason.ProcessIsolation,
                                        RequestedBy = "RendererChild.FrameRequest",
                                        EmitVerificationReport = false
                                    });
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
                                    EngineLog.Write(LogSubsystem.Paint, LogSeverity.Debug, $"[RendererChild] Frame written to shared memory: {iWidth}x{iHeight} for tab={tabId}");
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
                            RequestedBy = frameResult?.RequestedBy ?? "RendererChild.FrameRequest",
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
                            PaintNodeCount = frameResult?.Telemetry?.PaintNodeCount ?? 0
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
                try { kvp.Value.Cancel(); } catch { }
                try { kvp.Value.Dispose(); } catch { }
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


