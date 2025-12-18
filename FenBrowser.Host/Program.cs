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
using FenBrowser.Host.Tabs;
using FenBrowser.Host.Context;
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
    private static TabBarWidget _tabBar;
    private static ToolbarWidget _toolbar;
    private static StatusBarWidget _statusBar;
    private static ContextMenuWidget _contextMenu;
    private static Widget _focusedWidget;
    
    // Content area (below toolbar, above status bar)
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
        // Initialize TabBar
        _tabBar = new TabBarWidget();
        _tabBar.NewTabRequested += () => TabManager.Instance.CreateTab("https://example.com");
        _tabBar.TabActivated += tab => TabManager.Instance.SwitchToTab(TabManager.Instance.Tabs.ToList().IndexOf(tab));
        _tabBar.TabCloseRequested += tab =>
        {
            int index = TabManager.Instance.Tabs.ToList().IndexOf(tab);
            TabManager.Instance.CloseTab(index);
        };
        
        // Initialize Toolbar
        _toolbar = new ToolbarWidget();
        _toolbar.SetUrl(_currentUrl);
        _toolbar.NavigateRequested += OnNavigate;
        _toolbar.BackClicked += async () => 
        {
            var tab = TabManager.Instance.ActiveTab;
            if (tab != null) await tab.Browser.GoBackAsync();
        };
        _toolbar.ForwardClicked += async () =>
        {
            var tab = TabManager.Instance.ActiveTab;
            if (tab != null) await tab.Browser.GoForwardAsync();
        };
        _toolbar.RefreshClicked += async () =>
        {
            var tab = TabManager.Instance.ActiveTab;
            if (tab != null) await tab.Browser.RefreshAsync();
        };
        _toolbar.HomeClicked += () => OnNavigate("https://example.com");
        _toolbar.AddressBar.FocusRequested += w => FocusManager.Instance.RequestFocus(w);
        
        // Initialize StatusBar
        _statusBar = new StatusBarWidget();
        
        // Wire TabManager events
        TabManager.Instance.ActiveTabChanged += tab =>
        {
            if (tab != null)
            {
                _toolbar.SetUrl(tab.Url);
                _toolbar.SetCanGoBack(tab.Browser.CanGoBack);
                _toolbar.SetCanGoForward(tab.Browser.CanGoForward);
                _window.Title = $"FenBrowser - {tab.Title}";
            }
        };
        
        // Register keyboard shortcuts
        RegisterKeyboardShortcuts();
        
        // Initial layout
        LayoutWidgets();
        
        // Create first tab with initial URL
        TabManager.Instance.CreateTab(_currentUrl);
    }
    
    private static void RegisterKeyboardShortcuts()
    {
        var kbd = KeyboardDispatcher.Instance;
        
        // Tab shortcuts
        kbd.RegisterCtrl(Key.T, () => TabManager.Instance.CreateTab("https://example.com"));
        kbd.RegisterCtrl(Key.W, () => TabManager.Instance.CloseActiveTab());
        kbd.RegisterGlobal(Key.Tab, true, false, false, () => TabManager.Instance.NextTab()); // Ctrl+Tab
        kbd.RegisterGlobal(Key.Tab, true, true, false, () => TabManager.Instance.PreviousTab()); // Ctrl+Shift+Tab
        kbd.RegisterGlobal(Key.T, true, true, false, () => TabManager.Instance.ReopenClosedTab()); // Ctrl+Shift+T
        
        // Navigation shortcuts
        kbd.RegisterCtrl(Key.L, () => FocusManager.Instance.RequestFocus(_toolbar.AddressBar));
        kbd.RegisterCtrl(Key.R, async () =>
        {
            var tab = TabManager.Instance.ActiveTab;
            if (tab != null) await tab.Browser.RefreshAsync();
        });
        kbd.Register(Key.F5, async () =>
        {
            var tab = TabManager.Instance.ActiveTab;
            if (tab != null) await tab.Browser.RefreshAsync();
        });
        
        // History navigation
        kbd.RegisterGlobal(Key.Left, false, false, true, async () =>
        {
            var tab = TabManager.Instance.ActiveTab;
            if (tab != null) await tab.Browser.GoBackAsync();
        }); // Alt+Left
        kbd.RegisterGlobal(Key.Right, false, false, true, async () =>
        {
            var tab = TabManager.Instance.ActiveTab;
            if (tab != null) await tab.Browser.GoForwardAsync();
        }); // Alt+Right
        
        // Focus navigation
        kbd.Register(Key.Escape, () =>
        {
            if (_contextMenu?.IsOpen == true)
            {
                _contextMenu.Hide();
            }
            else
            {
                FocusManager.Instance.ClearFocus();
            }
        });
    }
    
    private static void LayoutWidgets()
    {
        float tabBarHeight = 32;
        float toolbarHeight = 40;
        float statusBarHeight = StatusBarWidget.PreferredHeight;
        
        // TabBar at top
        _tabBar?.Layout(new SKRect(0, 0, _width, tabBarHeight));
        
        // Toolbar below tab bar
        _toolbar?.Layout(new SKRect(0, tabBarHeight, _width, tabBarHeight + toolbarHeight));
        
        // StatusBar at bottom
        _statusBar?.Layout(new SKRect(0, _height - statusBarHeight, _width, _height));
        
        // Content area between toolbar and status bar
        _contentArea = new SKRect(0, tabBarHeight + toolbarHeight, _width, _height - statusBarHeight);
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
        _toolbar?.SetUrl(url);
        _window.Title = "Loading...";
        
        var activeTab = TabManager.Instance.ActiveTab;
        if (activeTab != null)
        {
            _ = activeTab.NavigateAsync(url);
        }
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
        
        // Draw tab bar
        _tabBar?.Paint(canvas);
        
        // Draw toolbar
        _toolbar?.PaintAll(canvas);
        
        // Update toolbar state from active tab
        var activeTab = TabManager.Instance.ActiveTab;
        if (activeTab != null)
        {
            _toolbar?.SetCanGoBack(activeTab.Browser.CanGoBack);
            _toolbar?.SetCanGoForward(activeTab.Browser.CanGoForward);
        }
        
        // Draw browser content area
        canvas.Save();
        canvas.ClipRect(_contentArea);
        
        // Translate to content area origin
        canvas.Translate(_contentArea.Left, _contentArea.Top);
        
        var contentViewport = new SKRect(0, 0, _contentArea.Width, _contentArea.Height);
        TabManager.Instance.RenderActiveTab(canvas, contentViewport);
        
        canvas.Restore();
        
        // Draw status bar
        _statusBar?.Paint(canvas);
        
        // Draw context menu on top if open
        if (_contextMenu?.IsOpen == true)
        {
            _contextMenu.Paint(canvas);
        }
        
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
        // Check context menu first
        if (_contextMenu?.IsOpen == true)
        {
            _contextMenu.OnKeyDown(key);
            // If still open, don't bubble
            if (_contextMenu?.IsOpen == true) return;
        }
        
        // Get modifier states
        bool ctrl = keyboard.IsKeyPressed(Key.ControlLeft) || keyboard.IsKeyPressed(Key.ControlRight);
        bool shift = keyboard.IsKeyPressed(Key.ShiftLeft) || keyboard.IsKeyPressed(Key.ShiftRight);
        bool alt = keyboard.IsKeyPressed(Key.AltLeft) || keyboard.IsKeyPressed(Key.AltRight);
        
        // Dispatch to KeyboardDispatcher (Global -> Focused Widget -> Active Tab)
        if (KeyboardDispatcher.Instance.Dispatch(key, ctrl, shift, alt))
        {
            return;
        }
        
        // Fallback: Escape closes window if nothing else handled it and no focus
        if (key == Key.Escape && _focusedWidget == null && _contextMenu?.IsOpen != true)
        {
           // Optional: confirm before closing? For now just keep open or minimize? 
           // Better not to close whole app on Esc unless explicitly requested.
        }
    }
    
    private static void OnKeyChar(IKeyboard keyboard, char character)
    {
        // Use KeyboardDispatcher
        KeyboardDispatcher.Instance.DispatchChar(character);
    }
    
    private static void OnMouseDown(IMouse mouse, MouseButton button)
    {
        float x = mouse.Position.X;
        float y = mouse.Position.Y;
        
        // Close context menu on any click
        if (_contextMenu?.IsOpen == true && !_contextMenu.Bounds.Contains(x, y))
        {
            _contextMenu.Hide();
            return;
        }
        
        // Context menu click
        if (_contextMenu?.IsOpen == true)
        {
            _contextMenu.OnMouseDown(x, y, button);
            return;
        }
        
        // Right-click context menu
        if (button == MouseButton.Right && _contentArea.Contains(x, y))
        {
            ShowContextMenu(x, y);
            return;
        }
        
        // Hit test tab bar
        if (_tabBar != null && _tabBar.Bounds.Contains(x, y))
        {
            _tabBar.OnMouseDown(x, y, button);
            return;
        }
        
        // Hit test toolbar
        if (_toolbar != null)
        {
            var hit = _toolbar.HitTestDeep(x, y);
            if (hit != null)
            {
                hit.OnMouseDown(x, y, button);
                return;
            }
        }
        
        // Content area click
        if (_contentArea.Contains(x, y))
        {
            var activeTab = TabManager.Instance.ActiveTab;
            activeTab?.Browser.HandleClick(x, y, _contentArea.Left, _contentArea.Top);
        }
        else
        {
            // Click outside - clear focus
            FocusManager.Instance.ClearFocus();
        }
    }
    
    private static void ShowContextMenu(float x, float y)
    {
        var activeTab = TabManager.Instance.ActiveTab;
        if (activeTab == null) return;
        
        var result = activeTab.Browser.PerformHitTest(x, y, _contentArea.Left, _contentArea.Top);
        
        var items = ContextMenuBuilder.Build(
            result,
            hasSelection: false, // TODO: wire selection state
            canPaste: true,
            onNavigate: url => OnNavigate(url),
            onCopy: () => { /* TODO */ },
            onPaste: () => { /* TODO */ },
            onSelectAll: () => { /* TODO */ },
            onReload: async () => await activeTab.Browser.RefreshAsync(),
            onBack: async () => await activeTab.Browser.GoBackAsync(),
            onForward: async () => await activeTab.Browser.GoForwardAsync(),
            onOpenInNewTab: url => TabManager.Instance.CreateTab(url),
            onCopyLink: url => { /* TODO: copy to clipboard */ }
        );
        
        _contextMenu = new ContextMenuWidget(items);
        _contextMenu.Show(x, y, _width, _height);
    }
    
    private static void OnMouseUp(IMouse mouse, MouseButton button)
    {
        float x = mouse.Position.X;
        float y = mouse.Position.Y;
        
        // Forward to all toolbar children (they track their own pressed state)
        if (_toolbar != null)
        {
            foreach (var child in _toolbar.Children)
            {
                child.OnMouseUp(x, y, button);
            }
        }
    }
    
    private static void OnMouseMove(IMouse mouse, System.Numerics.Vector2 position)
    {
        // Forward to tab bar for hover effects
        _tabBar?.OnMouseMove(position.X, position.Y);
        
        // Forward to toolbar children for hover effects
        if (_toolbar != null)
        {
            foreach (var child in _toolbar.Children)
            {
                child.OnMouseMove(position.X, position.Y);
            }
        }
        
        // Forward to context menu if open
        if (_contextMenu?.IsOpen == true)
        {
            _contextMenu.OnMouseMove(position.X, position.Y);
        }
        
        // Hit test content area for cursor and status bar updates
        if (_contentArea.Contains(position.X, position.Y))
        {
            var activeTab = TabManager.Instance.ActiveTab;
            if (activeTab != null)
            {
                var result = activeTab.Browser.HandleMouseMove(
                    position.X, 
                    position.Y, 
                    _contentArea.Left, 
                    _contentArea.Top
                );
                
                // Update cursor based on hit test
                CursorManager.UpdateFromHitTest(mouse, result);
                
                // Update status bar with hover URL
                _statusBar?.UpdateFromHitTest(result);
            }
        }
        else
        {
            // Reset cursor when not over content
            CursorManager.ResetCursor(mouse);
            _statusBar?.ClearHoverUrl();
        }
    }
    
    private static void OnScroll(IMouse mouse, ScrollWheel wheel)
    {
        float x = mouse.Position.X;
        float y = mouse.Position.Y;
        
        // Tab bar scroll for tab overflow
        if (_tabBar != null && _tabBar.Bounds.Contains(x, y))
        {
            _tabBar.HandleScroll(wheel.Y);
            return;
        }
        
        // Only scroll if mouse is in content area
        if (_contentArea.Contains(x, y))
        {
            var activeTab = TabManager.Instance.ActiveTab;
            activeTab?.Browser.Scroll(wheel.Y);
        }
    }
}

