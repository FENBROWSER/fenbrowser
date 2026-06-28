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
    private int _width, _height;

    // GL + Skia
    private GL _gl;
    private GRContext _grContext;
    private GRBackendRenderTarget _renderTarget;
    private SKSurface _surface;

    // HTML content from document.write()/close()
    private string _htmlContent;
    private bool _contentReady;
    private SKPicture _renderedFrame;
    private bool _renderFailed;
    private FenBrowser.FenEngine.Rendering.CustomHtmlEngine _engine;

    private bool _disposed;
    private bool _glReady;

    public string Name => _name;
    public bool IsClosed { get; private set; }

    internal PopupWindow(string name, int width, int height)
    {
        _name = name;
        _width = width;
        _height = height;

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

        _windowThread = new Thread(() =>
        {
            try { _window.Run(); }
            catch (Exception ex)
            {
                if (!_disposed)
                    EngineLogBridge.Warn($"[PopupWindow] {_name}: {ex.Message}", LogCategory.General);
            }
        })
        {
            Name = $"Popup-{name}",
            IsBackground = true
        };
        _windowThread.Start();
    }

    public void SetContent(string html)
    {
        _htmlContent = html;
        _contentReady = true;

        // Kick off async HTML rendering.  The popup game loop will poll
        // GetRenderSnapshot() each frame and draw the result once ready.
        try
        {
            var engine = new FenBrowser.FenEngine.Rendering.CustomHtmlEngine();
            engine.LoadHtml(
                html,
                new Uri("fen://popup/" + (_name ?? "unnamed")),
                _ => System.Threading.Tasks.Task.FromResult<string>(null),
                _ => System.Threading.Tasks.Task.FromResult<System.IO.Stream>(null),
                _ => { },
                viewportWidth: _width);

            // Store engine reference for polling
            _engine = engine;
        }
        catch (Exception ex)
        {
            EngineLogBridge.Warn($"[PopupWindow] {_name}: LoadHtml failed: {ex.Message}", LogCategory.Rendering);
            _renderFailed = true;
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
            _grContext = GRContext.CreateGl(glInterface);
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
        _renderTarget?.Dispose();

        if (_gl == null || _grContext == null || _width <= 0 || _height <= 0)
            return;

        var fbSize = _window.FramebufferSize;
        int fw = fbSize.X, fh = fbSize.Y;
        if (fw <= 0 || fh <= 0) return;

        _gl.GetInteger(GLEnum.FramebufferBinding, out int framebuffer);
        _gl.GetInteger(GLEnum.Stencil, out int stencil);
        _gl.GetInteger(GLEnum.Samples, out int samples);

        var fbInfo = new GRGlFramebufferInfo((uint)framebuffer, SKColorType.Rgba8888.ToGlSizedFormat());
        _renderTarget = new GRBackendRenderTarget(fw, fh, samples, stencil, fbInfo);
        _surface = SKSurface.Create(_grContext, _renderTarget, GRSurfaceOrigin.BottomLeft, SKColorType.Rgba8888);
    }

    private void OnRender(double deltaTime)
    {
        if (_disposed || IsClosed || !_glReady) return;
        if (_surface == null) CreateRenderTarget();
        if (_surface == null) return;

        try
        {
            var canvas = _surface.Canvas;
            canvas.Clear(SKColors.White);

            // Poll the engine for a completed render
            if (_engine != null && _renderedFrame == null && _contentReady && !_renderFailed)
            {
                var snapshot = _engine.GetRenderSnapshot();
                if (snapshot.Root != null && snapshot.Styles != null && snapshot.Styles.Count > 0)
                {
                    try
                    {
                        var renderer = new FenBrowser.FenEngine.Rendering.SkiaDomRenderer();
                        renderer.EnsureLayout(
                            snapshot.Root,
                            snapshot.Styles,
                            _width, _height);

                        using var recorder = new SKPictureRecorder();
                        var recCanvas = recorder.BeginRecording(
                            new SKRect(0, 0, _width, _height));
                        renderer.Render(
                            snapshot.Root,
                            recCanvas,
                            snapshot.Styles,
                            new SKRect(0, 0, _width, _height));
                        _renderedFrame = recorder.EndRecording();
                    }
                    catch (Exception ex)
                    {
                        EngineLogBridge.Warn(
                            $"[PopupWindow] {_name}: paint failed: {ex.Message}",
                            LogCategory.Rendering);
                        _renderFailed = true;
                    }
                }
            }

            if (_renderedFrame != null)
            {
                canvas.DrawPicture(_renderedFrame);
            }
            else if (_renderFailed)
            {
                DrawFallback(canvas, _width, _height, _htmlContent);
            }
            else if (_contentReady)
            {
                DrawLoading(canvas, _width, _height);
            }

            canvas.Flush();
            _grContext.Flush();
        }
        catch { /* frame dropped */ }
    }

    private static void DrawFallback(SKCanvas canvas, int w, int h, string html)
    {
        // Parse and render basic text from the HTML as a fallback
        using var bg = new SKPaint { Color = SKColors.White, IsAntialias = true };
        canvas.DrawRect(0, 0, w, h, bg);

        using var text = new SKPaint
        {
            Color = new SKColor(17, 24, 39),
            TextSize = 14,
            IsAntialias = true,
            Typeface = SKTypeface.FromFamilyName("Segoe UI")
        };

        // Simple text extraction from HTML for fallback display
        var plainText = System.Text.RegularExpressions.Regex.Replace(
            html ?? "", "<[^>]+>", " ");
        plainText = System.Text.RegularExpressions.Regex.Replace(
            plainText, @"\s+", " ").Trim();
        if (plainText.Length > 500) plainText = plainText.Substring(0, 500) + "...";

        float y = 30;
        foreach (var line in plainText.Split('\n'))
        {
            if (y > h - 20) break;
            canvas.DrawText(line.Trim(), 20, y, text);
            y += 20;
        }
    }

    private static void DrawLoading(SKCanvas canvas, int w, int h)
    {
        using var bg = new SKPaint { Color = SKColors.White, IsAntialias = true };
        canvas.DrawRect(0, 0, w, h, bg);
        using var text = new SKPaint
        {
            Color = new SKColor(107, 114, 128),
            TextSize = 16,
            IsAntialias = true,
            Typeface = SKTypeface.FromFamilyName("Segoe UI")
        };
        var msg = "Loading...";
        float tw = text.MeasureText(msg);
        canvas.DrawText(msg, (w - tw) / 2f, h / 2f, text);
    }

    private void OnResize(Silk.NET.Maths.Vector2D<int> size)
    {
        _width = size.X;
        _height = size.Y;
        _renderedFrame?.Dispose();
        _renderedFrame = null;
        CreateRenderTarget();
    }

    private void OnClose()
    {
        IsClosed = true;
        Dispose();
    }

    public void Close()
    {
        if (_disposed || IsClosed) return;
        IsClosed = true;
        try { _window?.Close(); } catch { }
        Dispose();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _renderedFrame?.Dispose();
        _surface?.Dispose();
        _renderTarget?.Dispose();
        _grContext?.Dispose();
        _gl?.Dispose();
    }
}

/// <summary>
/// Tracks active popup windows for lifecycle management.
/// </summary>
public static class PopupWindowManager
{
    private static readonly ConcurrentDictionary<PopupWindow, bool> _popups = new();

    public static PopupWindow Create(string name, int width, int height)
    {
        width = Math.Clamp(width, 200, 1920);
        height = Math.Clamp(height, 150, 1080);
        var popup = new PopupWindow(name, width, height);
        _popups[popup] = true;
        return popup;
    }

    public static void CloseAll()
    {
        foreach (var kv in _popups)
        {
            try { kv.Key.Close(); } catch { }
        }
        _popups.Clear();
    }
}
