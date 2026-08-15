using System;
using System.Collections.Concurrent;
using System.Threading;
using FenBrowser.Core.Logging;
using Silk.NET.OpenGLES;
using Silk.NET.Windowing;
using SkiaSharp;

namespace FenBrowser.Host;

/// <summary>
/// OS-level popup window created by window.open().
/// Each popup runs its own Silk.NET game loop on a dedicated thread
/// and renders HTML content using the FenEngine rendering pipeline.
/// </summary>
public sealed class PopupWindow : IDisposable
{
    private readonly IWindow _window;
    private readonly Thread _windowThread;
    private readonly string _name;
    private readonly object _contentLock = new();
    private readonly Action<PopupWindow> _closedCallback;
    private int _width;
    private int _height;

    private GL _gl;
    private GRContext _grContext;
    private GRBackendRenderTarget _renderTarget;
    private SKSurface _surface;

    private string _htmlContent;
    private bool _contentReady;
    private SKPicture _renderedFrame;
    private bool _renderFailed;
    private FenBrowser.FenEngine.Rendering.CustomHtmlEngine _engine;
    private int _contentVersion;
    private int _renderedContentVersion = -1;

    private int _closeRequested;
    private int _cleanupCompleted;
    private int _isClosed;
    private bool _glReady;

    public string Name => _name;
    public bool IsClosed => Volatile.Read(ref _isClosed) != 0;

    internal PopupWindow(
        string name,
        int width,
        int height,
        Action<PopupWindow> closedCallback = null)
    {
        _name = name;
        _width = width;
        _height = height;
        _closedCallback = closedCallback;

        var options = WindowOptions.Default;
        options.Size = new Silk.NET.Maths.Vector2D<int>(width, height);
        options.Title = name ?? "FenBrowser Popup";
        options.VSync = false;
        options.WindowBorder = WindowBorder.Resizable;
        options.API = new GraphicsAPI(
            ContextAPI.OpenGLES,
            ContextProfile.Core,
            ContextFlags.Default,
            new APIVersion(3, 0));

        _window = Silk.NET.Windowing.Window.Create(options);
        _window.Load += OnLoad;
        _window.Render += OnRender;
        _window.Resize += OnResize;
        _window.Closing += OnClose;

        _windowThread = new Thread(WindowThreadMain)
        {
            Name = $"Popup-{name}",
            IsBackground = true
        };
        _windowThread.Start();
    }

    private void WindowThreadMain()
    {
        try
        {
            _window.Run();
        }
        catch (Exception ex)
        {
            if (Volatile.Read(ref _closeRequested) == 0)
            {
                EngineLogBridge.Warn($"[PopupWindow] {_name}: {ex.Message}", LogCategory.General);
            }
        }
        finally
        {
            Volatile.Write(ref _isClosed, 1);
            CleanupWindowThreadResources();
            try
            {
                _closedCallback?.Invoke(this);
            }
            catch (Exception ex)
            {
                EngineLogBridge.Warn(
                    $"[PopupWindow] {_name}: close callback failed: {ex.Message}",
                    LogCategory.General);
            }
        }
    }

    public void SetContent(string html)
    {
        if (Volatile.Read(ref _closeRequested) != 0)
        {
            return;
        }

        FenBrowser.FenEngine.Rendering.CustomHtmlEngine engine;
        try
        {
            engine = new FenBrowser.FenEngine.Rendering.CustomHtmlEngine();
            engine.LoadHtml(
                html,
                new Uri("fen://popup/" + (_name ?? "unnamed")),
                _ => System.Threading.Tasks.Task.FromResult<string>(null),
                _ => System.Threading.Tasks.Task.FromResult<System.IO.Stream>(null),
                _ => { },
                viewportWidth: Volatile.Read(ref _width));
        }
        catch (Exception ex)
        {
            lock (_contentLock)
            {
                _htmlContent = html;
                _contentReady = true;
                _renderFailed = true;
                _engine = null;
                unchecked { _contentVersion++; }
            }

            EngineLogBridge.Warn($"[PopupWindow] {_name}: LoadHtml failed: {ex.Message}", LogCategory.Rendering);
            return;
        }

        lock (_contentLock)
        {
            if (_closeRequested != 0)
            {
                return;
            }

            _htmlContent = html;
            _contentReady = true;
            _renderFailed = false;
            _engine = engine;
            unchecked { _contentVersion++; }
        }
    }

    public void Focus()
    {
        // The OS brings new windows to the foreground automatically.
    }

    private void OnLoad()
    {
        try
        {
            _gl = _window.CreateOpenGLES();
            var glInterface = GRGlInterface.Create();
            if (glInterface == null)
            {
                EngineLogBridge.Warn($"[PopupWindow] {_name}: GRGlInterface.Create returned null", LogCategory.Rendering);
                return;
            }

            try
            {
                _grContext = GRContext.CreateGl(glInterface);
            }
            finally
            {
                glInterface.Dispose();
            }

            if (_grContext == null)
            {
                EngineLogBridge.Warn($"[PopupWindow] {_name}: GRContext.CreateGl returned null", LogCategory.Rendering);
                return;
            }

            _glReady = true;
            CreateRenderTarget();
            EngineLogBridge.Debug($"[PopupWindow] {_name}: GL+Skia ready", LogCategory.Rendering);
        }
        catch (Exception ex)
        {
            EngineLogBridge.Warn($"[PopupWindow] {_name}: GL init failed: {ex.Message}", LogCategory.Rendering);
        }
    }

    private void CreateRenderTarget()
    {
        _surface?.Dispose();
        _surface = null;
        _renderTarget?.Dispose();
        _renderTarget = null;

        if (_gl == null || _grContext == null || _width <= 0 || _height <= 0)
        {
            return;
        }

        var fbSize = _window.FramebufferSize;
        int fw = fbSize.X;
        int fh = fbSize.Y;
        if (fw <= 0 || fh <= 0)
        {
            return;
        }

        _gl.GetInteger(GLEnum.FramebufferBinding, out int framebuffer);
        _gl.GetInteger(GLEnum.Stencil, out int stencil);
        _gl.GetInteger(GLEnum.Samples, out int samples);

        var fbInfo = new GRGlFramebufferInfo((uint)framebuffer, SKColorType.Rgba8888.ToGlSizedFormat());
        _renderTarget = new GRBackendRenderTarget(fw, fh, samples, stencil, fbInfo);
        _surface = SKSurface.Create(_grContext, _renderTarget, GRSurfaceOrigin.BottomLeft, SKColorType.Rgba8888);
    }

    private void OnRender(double deltaTime)
    {
        if (IsClosed || Volatile.Read(ref _cleanupCompleted) != 0 || !_glReady)
        {
            return;
        }

        if (_surface == null)
        {
            CreateRenderTarget();
        }
        if (_surface == null)
        {
            return;
        }

        try
        {
            var canvas = _surface.Canvas;
            canvas.Clear(SKColors.White);

            FenBrowser.FenEngine.Rendering.CustomHtmlEngine engine;
            bool contentReady;
            bool renderFailed;
            string htmlContent;
            int contentVersion;
            lock (_contentLock)
            {
                engine = _engine;
                contentReady = _contentReady;
                renderFailed = _renderFailed;
                htmlContent = _htmlContent;
                contentVersion = _contentVersion;
            }

            // SKPicture belongs to the popup graphics/render thread. Content may be
            // replaced from another thread, so invalidate by version here instead of
            // disposing the previous picture in SetContent(). Previously a non-null
            // _renderedFrame made every later document.write()/SetContent update show
            // the first rendered document forever.
            if (_renderedFrame != null && _renderedContentVersion != contentVersion)
            {
                _renderedFrame.Dispose();
                _renderedFrame = null;
                _renderedContentVersion = -1;
            }

            if (engine != null && _renderedFrame == null && contentReady && !renderFailed)
            {
                var snapshot = engine.GetRenderSnapshot();
                if (snapshot.Root != null && snapshot.Styles != null && snapshot.Styles.Count > 0)
                {
                    try
                    {
                        var renderer = new FenBrowser.FenEngine.Rendering.SkiaDomRenderer();
                        renderer.EnsureLayout(snapshot.Root, snapshot.Styles, _width, _height);

                        using var recorder = new SKPictureRecorder();
                        var recCanvas = recorder.BeginRecording(new SKRect(0, 0, _width, _height));
                        renderer.Render(
                            snapshot.Root,
                            recCanvas,
                            snapshot.Styles,
                            new SKRect(0, 0, _width, _height));
                        _renderedFrame = recorder.EndRecording();
                        _renderedContentVersion = contentVersion;
                    }
                    catch (Exception ex)
                    {
                        lock (_contentLock)
                        {
                            if (ReferenceEquals(_engine, engine) && _contentVersion == contentVersion)
                            {
                                _renderFailed = true;
                            }
                        }

                        renderFailed = true;
                        EngineLogBridge.Warn(
                            $"[PopupWindow] {_name}: paint failed: {ex.Message}",
                            LogCategory.Rendering);
                    }
                }
            }

            if (_renderedFrame != null)
            {
                canvas.DrawPicture(_renderedFrame);
            }
            else if (renderFailed)
            {
                DrawFallback(canvas, _width, _height, htmlContent);
            }
            else if (contentReady)
            {
                DrawLoading(canvas, _width, _height);
            }

            canvas.Flush();
            _grContext.Flush();
        }
        catch (Exception ex)
        {
            EngineLogBridge.Debug(
                $"[PopupWindow] {_name}: dropped frame: {ex.GetType().Name}: {ex.Message}",
                LogCategory.Rendering);
        }
    }

    private static void DrawFallback(SKCanvas canvas, int w, int h, string html)
    {
        using var bg = new SKPaint { Color = SKColors.White, IsAntialias = true };
        canvas.DrawRect(0, 0, w, h, bg);

        using var textFont = new SKFont(SKTypeface.FromFamilyName("Segoe UI"), 14);
        using var textPaint = new SKPaint
        {
            Color = new SKColor(17, 24, 39),
            IsAntialias = true
        };

        var plainText = System.Text.RegularExpressions.Regex.Replace(html ?? "", "<[^>]+>", " ");
        plainText = System.Text.RegularExpressions.Regex.Replace(plainText, @"\s+", " ").Trim();
        if (plainText.Length > 500)
        {
            plainText = plainText.Substring(0, 500) + "...";
        }

        float y = 30;
        foreach (var line in plainText.Split('\n'))
        {
            if (y > h - 20)
            {
                break;
            }
            canvas.DrawText(line.Trim(), 20, y, SKTextAlign.Left, textFont, textPaint);
            y += 20;
        }
    }

    private static void DrawLoading(SKCanvas canvas, int w, int h)
    {
        using var bg = new SKPaint { Color = SKColors.White, IsAntialias = true };
        canvas.DrawRect(0, 0, w, h, bg);
        using var textFont = new SKFont(SKTypeface.FromFamilyName("Segoe UI"), 16);
        using var textPaint = new SKPaint
        {
            Color = new SKColor(107, 114, 128),
            IsAntialias = true
        };
        const string msg = "Loading...";
        float tw = textFont.MeasureText(msg);
        canvas.DrawText(msg, (w - tw) / 2f, h / 2f, SKTextAlign.Left, textFont, textPaint);
    }

    private void OnResize(Silk.NET.Maths.Vector2D<int> size)
    {
        if (size.X <= 0 || size.Y <= 0 || IsClosed)
        {
            return;
        }

        _width = size.X;
        _height = size.Y;
        _renderedFrame?.Dispose();
        _renderedFrame = null;
        _renderedContentVersion = -1;
        CreateRenderTarget();
    }

    private void OnClose()
    {
        Volatile.Write(ref _isClosed, 1);
        Interlocked.Exchange(ref _closeRequested, 1);
    }

    public void Close()
    {
        if (Interlocked.Exchange(ref _closeRequested, 1) != 0)
        {
            return;
        }

        Volatile.Write(ref _isClosed, 1);
        try
        {
            _window.Close();
        }
        catch (Exception ex)
        {
            EngineLogBridge.Debug(
                $"[PopupWindow] {_name}: close request failed: {ex.GetType().Name}: {ex.Message}",
                LogCategory.General);
        }
    }

    public void Dispose()
    {
        Close();

        if (Thread.CurrentThread != _windowThread && _windowThread.IsAlive)
        {
            _windowThread.Join(TimeSpan.FromSeconds(2));
        }
    }

    private void CleanupWindowThreadResources()
    {
        if (Interlocked.Exchange(ref _cleanupCompleted, 1) != 0)
        {
            return;
        }

        _glReady = false;

        _renderedFrame?.Dispose();
        _renderedFrame = null;
        _renderedContentVersion = -1;
        _surface?.Dispose();
        _surface = null;
        _renderTarget?.Dispose();
        _renderTarget = null;
        _grContext?.Dispose();
        _grContext = null;
        _gl?.Dispose();
        _gl = null;

        try
        {
            _window.Dispose();
        }
        catch (Exception ex)
        {
            EngineLogBridge.Debug(
                $"[PopupWindow] {_name}: window dispose failed: {ex.GetType().Name}: {ex.Message}",
                LogCategory.General);
        }
    }
}

/// <summary>
/// Tracks active popup windows for lifecycle management.
/// </summary>
public static class PopupWindowManager
{
    private static readonly ConcurrentDictionary<PopupWindow, byte> _popups = new();

    public static PopupWindow Create(string name, int width, int height)
    {
        width = Math.Clamp(width, 200, 1920);
        height = Math.Clamp(height, 150, 1080);
        var popup = new PopupWindow(name, width, height, OnPopupClosed);
        _popups.TryAdd(popup, 0);

        if (popup.IsClosed)
        {
            _popups.TryRemove(popup, out _);
        }

        return popup;
    }

    public static void CloseAll()
    {
        foreach (var popup in _popups.Keys)
        {
            try
            {
                popup.Close();
            }
            catch (Exception ex)
            {
                EngineLogBridge.Debug(
                    $"[PopupWindowManager] close failed: {ex.GetType().Name}: {ex.Message}",
                    LogCategory.General);
            }
        }
    }

    private static void OnPopupClosed(PopupWindow popup)
    {
        _popups.TryRemove(popup, out _);
    }
}
