using System;
using Silk.NET.Maths;
using Silk.NET.Windowing;
using Silk.NET.OpenGL;
using Silk.NET.Input;
using SkiaSharp;
using FenBrowser.Core;
using FenBrowser.Core.Logging;
using FenBrowser.Host.Widgets;
using FenBrowser.Host.Input;
using FenBrowser.FenEngine.Interaction;

namespace FenBrowser.Host;

/// <summary>
/// FenBrowser.Host - Custom rendering host using Silk.NET + SkiaSharp
/// This replaces the Avalonia-based UI with a lightweight native window.
/// </summary>
public class Program
{
    private static IWindow _window;
    private static GL _gl;
    private static GRContext _grContext;
    private static SKSurface _surface;
    private static GRBackendRenderTarget _renderTarget;
    
    private static int _width = 1280;
    private static int _height = 800;
    private static string _currentUrl = "https://example.com";
    
    // UI Widgets
    private static ToolbarWidget _toolbar;
    private static Widget _focusedWidget;
    
    // Browser integration
    private static BrowserIntegration _browser;
    
    // Content area (below toolbar)
    private static SKRect _contentArea;
    
    // Input reference for cursor management
    private static IMouse _mouse;
    
    public static void Main(string[] args)
    {
        // Parse command line for initial URL
        if (args.Length > 0)
        {
            _currentUrl = args[0];
        }
        
        FenLogger.Info($"[Host] Starting FenBrowser.Host with URL: {_currentUrl}", LogCategory.General);
        
        // Create window options
        var options = WindowOptions.Default;
        options.Size = new Vector2D<int>(_width, _height);
        options.Title = "FenBrowser";
        options.API = new GraphicsAPI(ContextAPI.OpenGL, ContextProfile.Core, ContextFlags.ForwardCompatible, new APIVersion(3, 3));
        options.VSync = true;
        
        // Create and run window
        _window = Window.Create(options);
        _window.Load += OnLoad;
        _window.Render += OnRender;
        _window.Resize += OnResize;
        _window.Closing += OnClosing;
        
        try
        {
            _window.Run();
        }
        catch (Exception ex)
        {
            FenLogger.Error($"[Host] Fatal error: {ex}", LogCategory.General);
            throw;
        }
    }
    
    private static void OnLoad()
    {
        FenLogger.Info("[Host] Window loaded, initializing OpenGL and SkiaSharp...", LogCategory.General);
        
        // Get OpenGL context
        _gl = _window.CreateOpenGL();
        
        // Initialize input
        var input = _window.CreateInput();
        foreach (var keyboard in input.Keyboards)
        {
            keyboard.KeyDown += OnKeyDown;
            keyboard.KeyChar += OnKeyChar;
        }
        foreach (var mouse in input.Mice)
        {
            mouse.MouseDown += OnMouseDown;
            mouse.MouseUp += OnMouseUp;
            mouse.MouseMove += OnMouseMove;
            mouse.Scroll += OnScroll;
            _mouse = mouse; // Store for cursor management
        }
        
        // Initialize SkiaSharp with OpenGL
        InitializeSkia();
        
        // Initialize UI widgets
        InitializeWidgets();
        
        FenLogger.Info("[Host] Initialization complete!", LogCategory.General);
    }
    
    private static void InitializeWidgets()
    {
        _toolbar = new ToolbarWidget();
        _toolbar.SetUrl(_currentUrl);
        
        // Initialize browser integration
        _browser = new BrowserIntegration();
        _browser.UrlChanged += url => _toolbar.SetUrl(url);
        _browser.LoadingChanged += loading => _window.Title = loading ? "Loading..." : $"FenBrowser - {_browser.CurrentUrl}";
        _browser.NeedsRepaint += () => { }; // Will trigger natural render cycle
        
        // Wire navigation events
        _toolbar.NavigateRequested += OnNavigate;
        _toolbar.BackClicked += async () => await _browser.GoBackAsync();
        _toolbar.ForwardClicked += async () => await _browser.GoForwardAsync();
        _toolbar.RefreshClicked += async () => await _browser.RefreshAsync();
        _toolbar.HomeClicked += () => OnNavigate("https://example.com");
        
        // Wire focus handling
        _toolbar.AddressBar.FocusRequested += (w) => SetFocus(w);
        
        // Initial layout
        LayoutWidgets();
        
        // Navigate to initial URL
        _ = _browser.NavigateAsync(_currentUrl);
    }
    
    private static void LayoutWidgets()
    {
        var fullBounds = new SKRect(0, 0, _width, _height);
        _toolbar.Layout(fullBounds);
        
        // Content area is below toolbar
        _contentArea = new SKRect(0, _toolbar.Bounds.Bottom, _width, _height);
    }
    
    private static void SetFocus(Widget widget)
    {
        if (_focusedWidget != null)
        {
            _focusedWidget.IsFocused = false;
        }
        _focusedWidget = widget;
        if (_focusedWidget != null)
        {
            _focusedWidget.IsFocused = true;
        }
    }
    
    private static void OnNavigate(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        
        _currentUrl = url;
        _toolbar.SetUrl(url);
        _window.Title = "Loading...";
        
        _ = _browser.NavigateAsync(url);
    }
    
    private static void InitializeSkia()
    {
        // Create GRContext for GPU rendering
        var glInterface = GRGlInterface.Create();
        if (glInterface == null)
        {
            FenLogger.Error("[Host] Failed to create GRGlInterface", LogCategory.General);
            return;
        }
        
        _grContext = GRContext.CreateGl(glInterface);
        if (_grContext == null)
        {
            FenLogger.Error("[Host] Failed to create GRContext", LogCategory.General);
            return;
        }
        
        CreateRenderTarget();
    }
    
    private static void CreateRenderTarget()
    {
        // Get framebuffer info
        _gl.GetInteger(GLEnum.FramebufferBinding, out int framebuffer);
        _gl.GetInteger(GLEnum.Stencil, out int stencil);
        _gl.GetInteger(GLEnum.Samples, out int samples);
        
        var fbInfo = new GRGlFramebufferInfo((uint)framebuffer, SKColorType.Rgba8888.ToGlSizedFormat());
        
        // Create render target
        _renderTarget = new GRBackendRenderTarget(_width, _height, samples, stencil, fbInfo);
        
        // Create surface
        _surface = SKSurface.Create(_grContext, _renderTarget, GRSurfaceOrigin.BottomLeft, SKColorType.Rgba8888);
        
        if (_surface == null)
        {
            FenLogger.Error("[Host] Failed to create SKSurface", LogCategory.General);
        }
    }
    
    private static void OnRender(double deltaTime)
    {
        if (_surface == null) return;
        
        var canvas = _surface.Canvas;
        
        // Clear with background color
        canvas.Clear(new SKColor(250, 250, 250));
        
        // Draw toolbar
        _toolbar.PaintAll(canvas);
        
        // Update toolbar state
        _toolbar.SetCanGoBack(_browser?.CanGoBack ?? false);
        _toolbar.SetCanGoForward(_browser?.CanGoForward ?? false);
        
        // Draw browser content area
        canvas.Save();
        canvas.ClipRect(_contentArea);
        
        // Translate to content area origin
        canvas.Translate(_contentArea.Left, _contentArea.Top);
        
        var contentViewport = new SKRect(0, 0, _contentArea.Width, _contentArea.Height);
        _browser?.Render(canvas, contentViewport);
        
        canvas.Restore();
        
        // Flush and swap
        canvas.Flush();
        _grContext.Flush();
    }
    
    private static void OnResize(Vector2D<int> size)
    {
        _width = size.X;
        _height = size.Y;
        
        FenLogger.Debug($"[Host] Window resized to {_width}x{_height}", LogCategory.General);
        
        // Recreate render target
        _surface?.Dispose();
        _renderTarget?.Dispose();
        
        _gl.Viewport(0, 0, (uint)_width, (uint)_height);
        CreateRenderTarget();
        
        // Re-layout widgets
        LayoutWidgets();
    }
    
    private static void OnClosing()
    {
        FenLogger.Info("[Host] Shutting down...", LogCategory.General);
        
        _surface?.Dispose();
        _renderTarget?.Dispose();
        _grContext?.Dispose();
        _gl?.Dispose();
    }
    
    // Input handlers
    private static void OnKeyDown(IKeyboard keyboard, Key key, int scancode)
    {
        // ESC to close
        if (key == Key.Escape)
        {
            _window.Close();
            return;
        }
        
        // Forward to focused widget
        _focusedWidget?.OnKeyDown(key);
    }
    
    private static void OnKeyChar(IKeyboard keyboard, char character)
    {
        // Forward to focused widget
        _focusedWidget?.OnTextInput(character);
    }
    
    private static void OnMouseDown(IMouse mouse, MouseButton button)
    {
        float x = mouse.Position.X;
        float y = mouse.Position.Y;
        
        // Hit test toolbar first
        var hit = _toolbar.HitTestDeep(x, y);
        if (hit != null)
        {
            hit.OnMouseDown(x, y, button);
        }
        else if (_contentArea.Contains(x, y))
        {
            // Click in content area - use hit testing for link navigation
            _browser?.HandleClick(x, y, _contentArea.Left, _contentArea.Top);
        }
        else
        {
            // Click outside - clear focus
            SetFocus(null);
        }
    }
    
    private static void OnMouseUp(IMouse mouse, MouseButton button)
    {
        float x = mouse.Position.X;
        float y = mouse.Position.Y;
        
        // Forward to all toolbar children (they track their own pressed state)
        foreach (var child in _toolbar.Children)
        {
            child.OnMouseUp(x, y, button);
        }
    }
    
    private static void OnMouseMove(IMouse mouse, System.Numerics.Vector2 position)
    {
        // Forward to toolbar children for hover effects
        foreach (var child in _toolbar.Children)
        {
            child.OnMouseMove(position.X, position.Y);
        }
        
        // Hit test content area for cursor updates
        if (_contentArea.Contains(position.X, position.Y) && _browser != null)
        {
            var result = _browser.HandleMouseMove(
                position.X, 
                position.Y, 
                _contentArea.Left, 
                _contentArea.Top
            );
            
            // Update cursor based on hit test
            CursorManager.UpdateFromHitTest(mouse, result);
        }
        else
        {
            // Reset cursor when not over content
            CursorManager.ResetCursor(mouse);
        }
    }
    
    private static void OnScroll(IMouse mouse, ScrollWheel wheel)
    {
        float x = mouse.Position.X;
        float y = mouse.Position.Y;
        
        // Only scroll if mouse is in content area
        if (_contentArea.Contains(x, y))
        {
            _browser?.Scroll(wheel.Y);
        }
    }
}
