using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using FenBrowser.Core;
using FenBrowser.Core.Logging;
using Silk.NET.Input;
using Silk.NET.Maths;
using Silk.NET.OpenGLES;
using Silk.NET.Windowing;
using SkiaSharp;
using FenBrowser.Host.Input;
using FenBrowser.Host.Platform;

namespace FenBrowser.Host.Platform.Windows;

/// <summary>
/// Windows implementation of IWindow using Silk.NET.
/// </summary>
internal sealed class WindowsWindow : IWindow
{
    private readonly Silk.NET.Windowing.IWindow _window;
    private readonly GRContext _grContext;
    private readonly SKSurface _surface;
    private readonly GRBackendRenderTarget _renderTarget;

    private int _physicalWidth;
    private int _physicalHeight;
    private int _logicalWidth;
    private int _logicalHeight;
    private float _dpiScale = 1.0f;

    public WindowsWindow(Silk.NET.Windowing.IWindow window, GRContext grContext, SKSurface surface, GRBackendRenderTarget renderTarget)
    {
        _window = window;
        _grContext = grContext;
        _surface = surface;
        _renderTarget = renderTarget;
        SyncDimensions();

        // Wire up Silk.NET events to our own events
        _window.Load += () => OnLoad?.Invoke();
        _window.Render += delta => OnRender?.Invoke(delta);
        _window.Resize += size => OnResize?.Invoke(new Size(size.X, size.Y));
        _window.Closing += () => OnClose?.Invoke();
    }

    public string Title
    {
        get => _window.Title;
        set => _window.Title = value;
    }

    public Size Size
    {
        get => new Size(_logicalWidth, _logicalHeight);
        set
        {
            _logicalWidth = value.X;
            _logicalHeight = value.Y;
            _window.Size = new Vector2D<int>(_logicalWidth, _logicalHeight);
        }
    }

    public Size FramebufferSize
    {
        get => new Size(_physicalWidth, _physicalHeight);
        set
        {
            _physicalWidth = value.X;
            _physicalHeight = value.Y;
        }
    }

    public float DpiScale => _dpiScale;

    public Silk.NET.Windowing.IWindow SilkWindow => _window;

    public void SetIcon(params object[] icons)
    {
        // Silk.NET's SetWindowIcon takes ReadOnlySpan<RawImage>
        // Convert the icons if possible, otherwise skip
    }

    public void Show()
    {
        // Silk.NET IWindow doesn't have Show/Hide - visibility is controlled by IsVisible
        _window.IsVisible = true;
    }

    public void Hide()
    {
        _window.IsVisible = false;
    }

    public void Close() => _window.Close();

    public void SetPosition(int x, int y)
    {
        _window.Position = new Vector2D<int>(x, y);
    }

    public void SetWindowState(WindowState state)
    {
        _window.WindowState = state switch
        {
            FenBrowser.Host.Platform.WindowState.Normal => Silk.NET.Windowing.WindowState.Normal,
            FenBrowser.Host.Platform.WindowState.Minimized => Silk.NET.Windowing.WindowState.Minimized,
            FenBrowser.Host.Platform.WindowState.Maximized => Silk.NET.Windowing.WindowState.Maximized,
            FenBrowser.Host.Platform.WindowState.Fullscreen => Silk.NET.Windowing.WindowState.Fullscreen,
            _ => Silk.NET.Windowing.WindowState.Normal
        };
    }

    public WindowState State => _window.WindowState switch
    {
        Silk.NET.Windowing.WindowState.Normal => FenBrowser.Host.Platform.WindowState.Normal,
        Silk.NET.Windowing.WindowState.Minimized => FenBrowser.Host.Platform.WindowState.Minimized,
        Silk.NET.Windowing.WindowState.Maximized => FenBrowser.Host.Platform.WindowState.Maximized,
        Silk.NET.Windowing.WindowState.Fullscreen => FenBrowser.Host.Platform.WindowState.Fullscreen,
        _ => FenBrowser.Host.Platform.WindowState.Normal
    };

    public event Action OnLoad;
    public event Action<double> OnRender;
    public event Action<Size> OnResize;
    public event Action OnClose;

    public void Initialize(string initialUrl)
    {
        // The window is already initialized during creation
    }

    internal void RenderFrame(double deltaTime)
    {
        if (_surface == null) return;
        _surface.Canvas.Flush();
        _grContext.Flush();
    }

    internal void ResizeFrame(Vector2D<int> size)
    {
        SyncDimensions();
    }

    internal void DisposeNative()
    {
        _surface?.Dispose();
        _renderTarget?.Dispose();
        _grContext?.Dispose();
    }

    private void SyncDimensions()
    {
        _physicalWidth = _window.FramebufferSize.X;
        _physicalHeight = _window.FramebufferSize.Y;
        _logicalWidth = _window.Size.X;
        _logicalHeight = _window.Size.Y;

        if (_logicalWidth == 0) _logicalWidth = 1;

        _dpiScale = (float)_physicalWidth / _logicalWidth;
    }
}

/// <summary>
/// Windows implementation of IPlatformHost using Silk.NET.
/// </summary>
internal sealed class WindowsPlatformHost : IPlatformHost
{
    private readonly ConcurrentQueue<Action> _mainThreadQueue = new();
    private readonly int _mainThreadId;
    private WindowsWindow? _window;
    private GRContext? _grContext;
    private SKSurface? _surface;
    private GRBackendRenderTarget? _renderTarget;

    public WindowsPlatformHost()
    {
        _mainThreadId = Environment.CurrentManagedThreadId;
    }

    public IClipboard Clipboard { get; } = new WindowsClipboard();

    public IWindow CreateWindow(WindowOptions options)
    {
        var silkOptions = new Silk.NET.Windowing.WindowOptions
        {
            Size = new Vector2D<int>(options.Size.X, options.Size.Y),
            VSync = options.VSync,
            WindowState = MapWindowState(options.State),
            WindowBorder = MapWindowBorder(options.Border),
            TransparentFramebuffer = options.TransparentFramebuffer,
            Title = options.Title,
            API = options.Api != null
                ? (GraphicsAPI)options.Api
                : new GraphicsAPI(ContextAPI.OpenGLES, ContextProfile.Core, ContextFlags.Default, new APIVersion(3, 0))
        };

        var window = Silk.NET.Windowing.Window.Create(silkOptions);

        // Initialize graphics context
        var gl = window.CreateOpenGLES();
        var glInterface = GRGlInterface.Create();

        if (glInterface == null)
            throw new InvalidOperationException("GPU initialization failed: GRGlInterface.Create() returned null.");

        var grContext = GRContext.CreateGl(glInterface);
        if (grContext == null)
            throw new InvalidOperationException("GPU initialization failed: GRContext.CreateGl returned null.");

        _grContext = grContext;

        var wrappedWindow = new WindowsWindow(window, _grContext, _surface!, _renderTarget!);

        // Wire up render loop to process main thread queue
        window.Render += delta => {
            ProcessMainThreadQueue();
            wrappedWindow.RenderFrame(delta);
        };
        window.Resize += size => wrappedWindow.ResizeFrame(size);
        window.Closing += () => wrappedWindow.DisposeNative();

        _window = wrappedWindow;
        return wrappedWindow;
    }

    public void Run()
    {
        _window?.SilkWindow.Run();
    }

    public void Quit()
    {
        _window?.SilkWindow.Close();
    }

    public void InvokeOnMainThread(Action action)
    {
        if (Environment.CurrentManagedThreadId == _mainThreadId)
        {
            action();
            return;
        }

        var tcs = new TaskCompletionSource<bool>();
        _mainThreadQueue.Enqueue(() =>
        {
            try
            {
                action();
                tcs.SetResult(true);
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        });

        tcs.Task.Wait();
    }

    public PlatformType Platform => PlatformType.Windows;

    public void EnableHighDpiAwareness()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            try
            {
                SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
            }
            catch (Exception ex)
            {
                EngineLog.Write(LogSubsystem.General, LogSeverity.Debug, $"[WindowsPlatformHost] DPI awareness setup skipped: {ex.Message}");
            }
        }
    }

    private void ProcessMainThreadQueue()
    {
        int max = 50;
        while (max-- > 0 && _mainThreadQueue.TryDequeue(out var action))
        {
            action();
        }
    }

    private static Silk.NET.Windowing.WindowState MapWindowState(FenBrowser.Host.Platform.WindowState state)
    {
        return state switch
        {
            FenBrowser.Host.Platform.WindowState.Normal => Silk.NET.Windowing.WindowState.Normal,
            FenBrowser.Host.Platform.WindowState.Minimized => Silk.NET.Windowing.WindowState.Minimized,
            FenBrowser.Host.Platform.WindowState.Maximized => Silk.NET.Windowing.WindowState.Maximized,
            FenBrowser.Host.Platform.WindowState.Fullscreen => Silk.NET.Windowing.WindowState.Fullscreen,
            _ => Silk.NET.Windowing.WindowState.Normal
        };
    }

    private static Silk.NET.Windowing.WindowBorder MapWindowBorder(FenBrowser.Host.Platform.WindowBorder border)
    {
        return border switch
        {
            FenBrowser.Host.Platform.WindowBorder.Fixed => Silk.NET.Windowing.WindowBorder.Fixed,
            FenBrowser.Host.Platform.WindowBorder.Resizable => Silk.NET.Windowing.WindowBorder.Resizable,
            FenBrowser.Host.Platform.WindowBorder.Hidden => Silk.NET.Windowing.WindowBorder.Hidden,
            _ => Silk.NET.Windowing.WindowBorder.Resizable
        };
    }

    public void Dispose()
    {
        _surface?.Dispose();
        _renderTarget?.Dispose();
        _grContext?.Dispose();
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetProcessDpiAwarenessContext(int dpiContext);
    private const int DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = -4;
}