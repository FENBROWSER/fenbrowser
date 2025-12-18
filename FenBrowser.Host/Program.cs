using System;
using Silk.NET.Maths;
using Silk.NET.Windowing;
using Silk.NET.OpenGL;
using Silk.NET.Input;
using SkiaSharp;
using FenBrowser.Core;
using FenBrowser.Core.Logging;

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
    private static string _initialUrl = "https://example.com";
    
    public static void Main(string[] args)
    {
        // Parse command line for initial URL
        if (args.Length > 0)
        {
            _initialUrl = args[0];
        }
        
        FenLogger.Info($"[Host] Starting FenBrowser.Host with URL: {_initialUrl}", LogCategory.General);
        
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
        }
        
        // Initialize SkiaSharp with OpenGL
        InitializeSkia();
        
        FenLogger.Info("[Host] Initialization complete!", LogCategory.General);
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
        canvas.Clear(SKColors.White);
        
        // Draw test content (placeholder for FenEngine integration)
        using var paint = new SKPaint
        {
            Color = SKColors.DarkBlue,
            IsAntialias = true,
            TextSize = 24
        };
        
        canvas.DrawText($"FenBrowser.Host - {_initialUrl}", 20, 40, paint);
        canvas.DrawText($"Window: {_width}x{_height}", 20, 70, paint);
        canvas.DrawText("Phase 2: Silk.NET + SkiaSharp", 20, 100, paint);
        
        // Draw a colored rectangle as visual test
        using var rectPaint = new SKPaint
        {
            Color = new SKColor(66, 133, 244), // Google blue
            IsAntialias = true
        };
        canvas.DrawRoundRect(new SKRoundRect(new SKRect(20, 120, 300, 200), 8), rectPaint);
        
        using var whitePaint = new SKPaint
        {
            Color = SKColors.White,
            IsAntialias = true,
            TextSize = 18
        };
        canvas.DrawText("OpenGL + SkiaSharp Working!", 40, 165, whitePaint);
        
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
        FenLogger.Debug($"[Host] KeyDown: {key}", LogCategory.General);
        
        if (key == Key.Escape)
        {
            _window.Close();
        }
    }
    
    private static void OnKeyChar(IKeyboard keyboard, char character)
    {
        // Handle text input for address bar, etc.
    }
    
    private static void OnMouseDown(IMouse mouse, MouseButton button)
    {
        FenLogger.Debug($"[Host] MouseDown: {button} at ({mouse.Position.X}, {mouse.Position.Y})", LogCategory.General);
    }
    
    private static void OnMouseUp(IMouse mouse, MouseButton button)
    {
        // Handle click release
    }
    
    private static void OnMouseMove(IMouse mouse, System.Numerics.Vector2 position)
    {
        // Handle hover effects
    }
    
    private static void OnScroll(IMouse mouse, ScrollWheel wheel)
    {
        FenLogger.Debug($"[Host] Scroll: {wheel.Y}", LogCategory.General);
    }
}
