using System;
using Silk.NET.Input;
using Silk.NET.Maths;
using Silk.NET.Windowing;

namespace FenBrowser.Host.Platform;

/// <summary>
/// Abstract clipboard interface for cross-platform clipboard operations.
/// </summary>
public interface IClipboard
{
    /// <summary>
    /// Gets text from the clipboard.
    /// </summary>
    string GetText();

    /// <summary>
    /// Sets text to the clipboard.
    /// </summary>
    bool SetText(string text);

    /// <summary>
    /// Gets whether the clipboard contains text.
    /// </summary>
    bool HasText { get; }
}

/// <summary>
/// Abstract window interface for cross-platform window management.
/// </summary>
public interface IWindow
{
    /// <summary>
    /// Gets the window title.
    /// </summary>
    string Title { get; set; }

    /// <summary>
    /// Gets/sets the window size in logical pixels.
    /// </summary>
    Size Size { get; set; }

    /// <summary>
    /// Gets the framebuffer size in physical pixels.
    /// </summary>
    Size FramebufferSize { get; }

    /// <summary>
    /// Gets the DPI scale factor.
    /// </summary>
    float DpiScale { get; }

    /// <summary>
    /// Sets the window icon.
    /// </summary>
    void SetIcon(params object[] icons);

    /// <summary>
    /// Shows the window.
    /// </summary>
    void Show();

    /// <summary>
    /// Hides the window.
    /// </summary>
    void Hide();

    /// <summary>
    /// Closes the window.
    /// </summary>
    void Close();

    /// <summary>
    /// Sets the window position.
    /// </summary>
    void SetPosition(int x, int y);

    /// <summary>
    /// Sets the window state (normal, minimized, maximized).
    /// </summary>
    void SetWindowState(WindowState state);

    /// <summary>
    /// Gets the window state.
    /// </summary>
    WindowState State { get; }

    /// <summary>
    /// Gets the underlying Silk.NET window for platform-specific operations.
    /// </summary>
    Silk.NET.Windowing.IWindow SilkWindow { get; }

    /// <summary>
    /// Event fired when the window loads.
    /// </summary>
    event Action OnLoad;

    /// <summary>
    /// Event fired when the window renders.
    /// </summary>
    event Action<double> OnRender;

    /// <summary>
    /// Event fired when the window resizes.
    /// </summary>
    event Action<Size> OnResize;

    /// <summary>
    /// Event fired when the window closes.
    /// </summary>
    event Action OnClose;

    /// <summary>
    /// Initializes the window (platform-specific initialization).
    /// </summary>
    void Initialize(string initialUrl);
}

/// <summary>
/// Window state enumeration.
/// </summary>
public enum WindowState
{
    Normal,
    Minimized,
    Maximized,
    Fullscreen
}

/// <summary>
/// Size structure for cross-platform use.
/// </summary>
public readonly record struct Size(int X, int Y)
{
    public static Size Empty => new Size(0, 0);
    
    public int Width => X;
    public int Height => Y;
}

/// <summary>
/// Platform abstraction for host operations.
/// </summary>
public interface IPlatformHost
{
    /// <summary>
    /// Gets the clipboard service.
    /// </summary>
    IClipboard Clipboard { get; }

    /// <summary>
    /// Creates a new window.
    /// </summary>
    IWindow CreateWindow(WindowOptions options);

    /// <summary>
    /// Runs the main message loop.
    /// </summary>
    void Run();

    /// <summary>
    /// Quits the main message loop.
    /// </summary>
    void Quit();

    /// <summary>
    /// Schedules an action on the main thread.
    /// </summary>
    void InvokeOnMainThread(Action action);

    /// <summary>
    /// Gets the current platform.
    /// </summary>
    PlatformType Platform { get; }

    /// <summary>
    /// Enables high-DPI awareness.
    /// </summary>
    void EnableHighDpiAwareness();
}

/// <summary>
/// Platform type enumeration.
/// </summary>
public enum PlatformType
{
    Windows,
    Linux,
    MacOS,
    Unknown
}

/// <summary>
/// Window creation options.
/// </summary>
public sealed class WindowOptions
{
    public string Title { get; set; } = "FenBrowser";
    public Size Size { get; set; } = new Size(1280, 800);
    public WindowState State { get; set; } = WindowState.Normal;
    public bool VSync { get; set; } = true;
    public bool TransparentFramebuffer { get; set; } = false;
    public WindowBorder Border { get; set; } = WindowBorder.Resizable;
    public object? Api { get; set; }
}

/// <summary>
/// Window border style.
/// </summary>
public enum WindowBorder
{
    Fixed,
    Resizable,
    Hidden
}