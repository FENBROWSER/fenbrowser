using System;
using Silk.NET.Maths;
using Silk.NET.Windowing;
using Silk.NET.OpenGL;
using Silk.NET.Input;
using SkiaSharp;
using FenBrowser.Core.Dom;
using FenBrowser.DevTools.Core;
using FenBrowser.DevTools.Core.Protocol;
using FenBrowser.Core;
using FenBrowser.Core.Logging;
using FenBrowser.Host.Widgets;
using FenBrowser.Host.Input;
using FenBrowser.Host.Tabs;
using FenBrowser.Host.Context;
using FenBrowser.Host.Theme;
using FenBrowser.FenEngine.Interaction;
using FenBrowser.FenEngine.Rendering;
using System.Linq;

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
    
    private static int _physicalWidth;
    private static int _physicalHeight;
    private static int _logicalWidth = 1280;
    private static int _logicalHeight = 800;
    private static float _dpiScale = 1.0f;
    private static string _currentUrl = "https://example.com";
    
    // Compositor and Root UI
    private static Compositor _compositor;
    private static RootWidget _root;
    
    // UI Widgets
    private static TabBarWidget _tabBar;
    private static ToolbarWidget _toolbar;
    private static StatusBarWidget _statusBar;
    private static ContextMenuWidget _contextMenu;
    private static Widget _focusedWidget => InputManager.Instance.FocusedWidget;
    private static Widget _hoveredWidget;
    // private static SettingsOverlayWidget _settingsOverlay; // REMOVED
    
    // Track active tab for event unsubscription
    private static BrowserTab _currentActiveTab;
    
    // Input reference for cursor management
    private static IMouse _mouse;
    
    // Window Render/Drag state
    private static bool _isDragging = false;
    private static System.Numerics.Vector2 _lastMousePos;
    
    // DevTools
    private static FenBrowser.DevTools.Core.DevToolsController _devTools;
    private static DevToolsHostAdapter _devToolsHost;
    private static FenBrowser.DevTools.Core.DevToolsServer _devToolsServer;
    private static FenBrowser.DevTools.Instrumentation.DomInstrumenter _domInstrumenter;

    
    public static void Main(string[] args)
    {
        // Parse command line for initial URL
        if (args.Length > 0)
        {
            _currentUrl = args[0];
        }
        
        // Initialize logging to absolute path to ensure visibility
        FenBrowser.Core.FenLogger.Initialize(@"C:\Users\udayk\Videos\FENBROWSER\host_debug.txt");
        
        // Initialize StructuredLogger for module-specific diagnostics (CSS, Layout, Network)
        StructuredLogger.Initialize(@"C:\Users\udayk\Videos\FENBROWSER\logs");
        
        FenLogger.Info($"[Host] Starting FenBrowser.Host with URL: {_currentUrl}", LogCategory.General);
        
        // Create window options
        var options = WindowOptions.Default;
        options.Size = new Vector2D<int>(_logicalWidth, _logicalHeight);
        options.VSync = true;
        options.WindowState = WindowState.Maximized;
        options.WindowBorder = WindowBorder.Hidden; // Borderless for custom chrome
        options.TransparentFramebuffer = false;
        
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
            // Initialize physical units
            _physicalWidth = _window.FramebufferSize.X;
            _physicalHeight = _window.FramebufferSize.Y;
            _logicalWidth = _window.Size.X;
            _logicalHeight = _window.Size.Y;
            _dpiScale = (float)_physicalWidth / _logicalWidth;
            
            _mouse = mouse; // Store for cursor management
        }
        
        SyncCompositorScale();
        
        // Initialize SkiaSharp with OpenGL
        InitializeSkia();

        FenLogger.Info($"[Host] Theme Initialized. Background: {ThemeManager.Current.Background}, Surface: {ThemeManager.Current.Surface}, Text: {ThemeManager.Current.Text}", LogCategory.General);
        
        // Initialize UI widgets
        InitializeWidgets();
        
        FenLogger.Info("[Host] Initialization complete!", LogCategory.General);
    }
    
    private static void SyncCompositorScale()
    {
        if (_compositor != null) _compositor.DpiScale = _dpiScale;
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
        
        // Wire Window Controls
        _tabBar.MinimizeRequested += () => _window.WindowState = WindowState.Minimized;
        _tabBar.MaximizeRequested += () => 
        {
            if (_window.WindowState == WindowState.Maximized)
                _window.WindowState = WindowState.Normal;
            else
                _window.WindowState = WindowState.Maximized;
        };
        _tabBar.CloseRequested += () => _window.Close();
        
        // Initialize Toolbar
        _toolbar = new ToolbarWidget();
        _toolbar.SetUrl(_currentUrl);
        _toolbar.NavigateRequested += OnNavigate;
        _toolbar.BackClicked += async () => 
        {
            var tab = TabManager.Instance.ActiveTab;
            if (tab != null) await tab.Browser.GoBackAsync();
        };
        _toolbar.RefreshClicked += async () =>
        {
            var tab = TabManager.Instance.ActiveTab;
            if (tab != null) await tab.Browser.RefreshAsync();
        };
        _toolbar.HomeClicked += () => OnNavigate("https://example.com");
        _toolbar.AddressBar.FocusRequested += w => InputManager.Instance.RequestFocus(w);
        _toolbar.SettingsClicked += () => 
        {
             var existingTab = TabManager.Instance.Tabs.FirstOrDefault(t => t.Url.Equals("fen://settings", StringComparison.OrdinalIgnoreCase));
             if (existingTab != null)
             {
                 int index = TabManager.Instance.Tabs.ToList().IndexOf(existingTab);
                 TabManager.Instance.SwitchToTab(index);
             }
             else
             {
                 TabManager.Instance.CreateTab("fen://settings");
             }
        };
        
        // Initialize StatusBar
        _statusBar = new StatusBarWidget();
        
        // _settingsOverlay = new SettingsOverlayWidget(); // REMOVED
        
        // Create the Root UI Hierarchy
        _root = new RootWidget(_tabBar, _toolbar, _statusBar);
        _root.SetContent(new WebContentWidget());
        // _root.SetOverlay(_settingsOverlay); // REMOVED
        
        // Initialize the Compositor (The Heartbeat)
        _compositor = new Compositor(_root);
        
        // Wire TabManager events
        TabManager.Instance.ActiveTabChanged += OnActiveTabChanged;
        
        // Initialize DevTools
        InitializeDevTools();
        
        // Create first tab with initial URL
        TabManager.Instance.CreateTab(_currentUrl);
        
        // Register keyboard shortcuts
        RegisterKeyboardShortcuts();
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
        kbd.RegisterCtrl(Key.L, () => InputManager.Instance.RequestFocus(_toolbar.AddressBar));
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
        
        kbd.RegisterGlobal(Key.T, false, false, true, () =>
        {
            ThemeManager.ToggleTheme();
            _root.Invalidate();
        }); // Alt+T
        
        // Focus navigation
        kbd.Register(Key.Escape, () =>
        {
            if (_contextMenu?.IsOpen == true)
            {
                _contextMenu.Hide();
            }
            else
            {
                InputManager.Instance.ClearFocus();
            }
        });
    }
    
    private static void InitializeDevTools()
    {
        // Create DevTools Protocol Server
        _devToolsServer = new FenBrowser.DevTools.Core.DevToolsServer();
        _domInstrumenter = new FenBrowser.DevTools.Instrumentation.DomInstrumenter(_devToolsServer);
        
        // Log protocol messages to console for Phase D1 verification
        _devToolsServer.OnJsonOutput(json => 
        {
            FenLogger.Debug($"[DevTools-JSON] {json}", LogCategory.General);
        });

        // Create DevTools UI controller
        _devTools = new FenBrowser.DevTools.Core.DevToolsController();
        
        // Register legacy panels (they will be refactored to use protocol later)
        _devTools.RegisterPanel(new FenBrowser.DevTools.Panels.ElementsPanel());
        _devTools.RegisterPanel(new FenBrowser.DevTools.Panels.ConsolePanel());
        _devTools.RegisterPanel(new FenBrowser.DevTools.Panels.NetworkPanel());
        
        // Create host adapter when we have an active tab
        TabManager.Instance.ActiveTabChanged += (tab) =>
        {
            if (tab != null)
            {
                // Reset server state on tab change
                _devToolsServer.Reset();
                
                // Initialize DOM domain with current tab's document
                _devToolsServer.InitializeDom(
                    () => tab.Browser.Document,
                    nodeId => {
                        if (nodeId.HasValue)
                        {
                            var node = _devToolsServer.Registry.GetNode(nodeId.Value);
                            tab.Browser.HighlightElement(node as Element);
                        }
                        else
                        {
                            tab.Browser.HighlightElement(null);
                        }
                    }
                );
                
                _devToolsServer.InitializeCss(node => {
                    if (node == null) return null;
                    return tab.Browser.ComputedStyles.TryGetValue(node, out var s) ? s : null;
                }, node => {
                    if (node is Element el && tab.Browser.CssSources != null)
                    {
                        return CssLoader.GetMatchedRules(el, tab.Browser.CssSources);
                    }
                    return new System.Collections.Generic.List<CssLoader.MatchedRule>();
                });

                _devToolsHost = new DevToolsHostAdapter(tab.Browser, _devToolsServer);
                _devTools.Attach(_devToolsHost);

                // Phase D1 Verification: Request document when navigation completes
                tab.Browser.UrlChanged += async (url) => {
                    await Task.Delay(2000); // Wait for initial render to settle
                    var response = await _devToolsServer.ProcessRequestAsync("{\"id\": 100, \"method\": \"DOM.getDocument\", \"params\": {}}");
                    FenLogger.Info($"[DevTools-D1] Document snapshot for {url}: {response}", LogCategory.General);
                };

                // Test Phase D1: Request document 5 seconds after load to verify JSON protocol
                Task.Run(async () => {
                    await Task.Delay(5000);
                    var response = await _devToolsServer.ProcessRequestAsync("{\"id\": 1, \"method\": \"DOM.getDocument\", \"params\": {}}");
                    FenLogger.Info($"[DevTools-Test] getDocument response: {response}", LogCategory.General);
                });
            }
        };
        
        // Handle invalidation
        _devTools.Invalidated += () => _root?.Invalidate();
        
        FenLogger.Info("[Host] DevTools initialized (Protocol Server + UI)", LogCategory.General);
    }
    
    private static void LayoutWidgets()
    {
        // Layout is now handled by RootWidget and Compositor
        _root?.InvalidateLayout();
    }
    
    private static void SetFocus(Widget widget)
    {
        InputManager.Instance.RequestFocus(widget);
    }
    
    private static void OnActiveTabChanged(BrowserTab tab)
    {
        // Unsubscribe from previous
        if (_currentActiveTab != null)
        {
            _currentActiveTab.Browser.UrlChanged -= OnBrowserUrlChanged;
            _currentActiveTab.Browser.ContextMenuRequested -= OnContextMenuRequested;
        }
        
        _currentActiveTab = tab;
        
        if (tab != null)
        {
            // Subscribe to new
            tab.Browser.UrlChanged += OnBrowserUrlChanged;
            tab.Browser.ContextMenuRequested += OnContextMenuRequested;
            
            // Initial update
            _toolbar.SetUrl(tab.Url);
            _toolbar.SetCanGoBack(tab.Browser.CanGoBack);
            _toolbar.SetCanGoForward(tab.Browser.CanGoForward);
            _window.Title = $"FenBrowser - {tab.Title}";
        }
        _root.Invalidate();
    }

    private static void OnContextMenuRequested(BrowserIntegration.ContextMenuRequest request)
    {
        // Ensure UI thread
        ShowContextMenu(request.X, request.Y, request.Hit);
    }
    
    // ... (UrlChanged, OnNavigate, Setup) ...

    private static void ShowContextMenu(float x, float y, FenBrowser.FenEngine.Interaction.HitTestResult hit)
    {
         var activeTab = TabManager.Instance.ActiveTab;
         if (activeTab == null) return;
         
         string currentUrl = activeTab.Url;
         
         // Build menu items
         var items = ContextMenuBuilder.Build(
             hit,
             currentUrl,
             hasSelection: false, // TODO
             canPaste: true,
             onNavigate: url => OnNavigate(url),
             onCopy: () => { /* TODO */ },
             onPaste: () => { /* TODO */ },
             onSelectAll: () => { /* TODO */ },
             onReload: async () => await activeTab.Browser.RefreshAsync(),
             onBack: async () => await activeTab.Browser.GoBackAsync(),
             onForward: async () => await activeTab.Browser.GoForwardAsync(),
             onOpenInNewTab: url => TabManager.Instance.CreateTab(url),
             onCopyLink: url => { 
                // Todo: Clipboard
                FenLogger.Info($"[Clipboard] Copy Link: {url}", LogCategory.General);
             },
             onViewPageSource: viewSourceUrl => {
                // Open view-source: in a new tab
                FenLogger.Info($"[ViewSource] Requested URL: '{viewSourceUrl}' (currentUrl was: '{currentUrl}')", LogCategory.General);
                if (!string.IsNullOrEmpty(viewSourceUrl) && viewSourceUrl != "view-source:")
                {
                    TabManager.Instance.CreateTab(viewSourceUrl);
                }
                else
                {
                    FenLogger.Warn("[ViewSource] Cannot view source - no URL available", LogCategory.General);
                }
             },
             onInspectElement: hitResult => {
                // Open DevTools and select the inspected element
                try
                {
                    // Close context menu
                    _root.SetPopup(null);
                    
                    // Show DevTools
                    if (_devTools != null)
                    {
                        _devTools.Show();
                        
                        // Select element in Elements panel
                        if (hitResult.NativeElement is FenBrowser.Core.Dom.Element element)
                        {
                            _devTools.SelectElement(element);
                        }
                        
                        _root.Invalidate();
                        FenLogger.Info($"[DevTools] Opened for <{hitResult.TagName}>", LogCategory.General);
                    }
                }
                catch (Exception ex)
                {
                    FenLogger.Error($"[DevTools] Failed to open: {ex.Message}", LogCategory.General);
                }
             }

         );
         
         _contextMenu = new ContextMenuWidget(items);
         _contextMenu.CloseRequested += () => _root.SetPopup(null); // Clean up on close
         
         // Attach to Root
         _root.SetPopup(_contextMenu);
         
         // Show
         _contextMenu.Show(x, y, _window.Size.X / _dpiScale, _window.Size.Y / _dpiScale);
    }

    
    private static void OnBrowserUrlChanged(string url)
    {
        // Must run on UI thread (we are on UI thread usually, unless BrowserIntegration fires on bg)
        // BrowserIntegration fires UrlChanged on Engine thread? 
        // Logic: _browser.Navigated event is used in BrowserIntegration.
        // We should ensure thread safety or Invalidate logic.
        // For simple host, just setting property is fine, Invalidate triggers render.
        _toolbar.SetUrl(url);
        _root.Invalidate();
    }
    
    private static void OnNavigate(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        
        // Reset DevTools state for new page
        _devToolsServer?.Reset();

        _currentUrl = url;
        // _toolbar?.SetUrl(url); // Don't set immediately, let BrowserIntegration normalize it? 
        // Actually showing user typing is good. But BrowserIntegration will update us with "https://"
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
        _renderTarget = new GRBackendRenderTarget(_physicalWidth, _physicalHeight, samples, stencil, fbInfo);
        
        // Create surface
        _surface = SKSurface.Create(_grContext, _renderTarget, GRSurfaceOrigin.BottomLeft, SKColorType.Rgba8888);
        
        if (_surface == null)
        {
            FenLogger.Error("[Host] Failed to create SKSurface", LogCategory.General);
        }
    }
    
    private static void OnRender(double deltaTime)
    {
        if (_surface == null || _compositor == null) return;
        
        // The Compositor is the single authority over rendering
        // It takes logical size and its internal DpiScale
        // FenLogger.Debug($"[Host] Rendering frame...", LogCategory.General); // Minimize spam
        _compositor.Composite(_surface.Canvas, new SKSize(_logicalWidth, _logicalHeight));
        
        // Draw Tooltips (Overlay)
        DrawTooltip(_surface.Canvas);
        
        // Draw DevTools (docked at bottom)
        if (_devTools != null && _devTools.IsVisible)
        {
            float devToolsHeight = _devTools.Height;
            var devToolsBounds = new SKRect(
                0, 
                _logicalHeight - devToolsHeight,
                _logicalWidth,
                _logicalHeight
            );
            _devTools.Paint(_surface.Canvas, devToolsBounds);
        }
        
        // Flush and swap
        _surface.Canvas.Flush();
        _grContext.Flush();
    }
    
    private static void OnResize(Vector2D<int> size)
    {
        _logicalWidth = size.X;
        _logicalHeight = size.Y;
        
        // Calculate physical pixels and DPI
        _physicalWidth = _window.FramebufferSize.X;
        _physicalHeight = _window.FramebufferSize.Y;
        _dpiScale = (float)_physicalWidth / _logicalWidth;
        
        if (_compositor != null) _compositor.DpiScale = _dpiScale;
        
        FenLogger.Info($"[Host] Resized: Logical={_logicalWidth}x{_logicalHeight}, Physical={_physicalWidth}x{_physicalHeight}, DPI={_dpiScale:F2}", LogCategory.General);
        
        // Recreate render target
        _surface?.Dispose();
        _renderTarget?.Dispose();
        
        _gl.Viewport(0, 0, (uint)_physicalWidth, (uint)_physicalHeight);
        CreateRenderTarget();
        
        // Re-layout widgets (Top-Down)
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
        
        
        // Update global state for widgets to check
        KeyboardDispatcher.Instance.IsCtrlPressed = ctrl;
        
        // Check DevTools first (as it's an overlay)
        if (_devTools != null && _devTools.IsVisible)
        {
            // Map common keys to ASCII/standard codes for DevTools panels
            int devToolsKeyCode = (int)key;
            if (key == Key.Backspace) devToolsKeyCode = 8;
            else if (key == Key.Enter) devToolsKeyCode = 13;
            else if (key == Key.Escape) devToolsKeyCode = 27;
            
            if (_devTools.OnKeyDown(devToolsKeyCode, ctrl, shift, alt)) return;
        }

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
        // Forward to DevTools
        if (_devTools != null && _devTools.IsVisible)
        {
            _devTools.OnTextInput(character);
        }
        
        // Use KeyboardDispatcher
        KeyboardDispatcher.Instance.DispatchChar(character);
    }
    
    private static void OnMouseDown(IMouse mouse, MouseButton button)
    {
        // Convert to logic units
        float x = mouse.Position.X / _dpiScale;
        float y = mouse.Position.Y / _dpiScale;
        
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
        
        // Check DevTools
        if (_devTools != null && _devTools.IsVisible)
        {
            float devToolsHeight = _devTools.Height;
            var dtBounds = new SKRect(0, _logicalHeight - devToolsHeight, _logicalWidth, _logicalHeight);
            if (dtBounds.Contains(x, y))
            {
                if (_devTools.OnMouseDown(x, y, button == MouseButton.Right)) return;
            }
        }
        
        // Hit test the entire root UI
        var hit = _root?.HitTestDeep(x, y);
        FenLogger.Info($"[Input] MouseDown at ({x:F1}, {y:F1}). Hit: {hit?.GetType().Name ?? "null"}", LogCategory.General);

        if (hit != null)
        {
            if (button == MouseButton.Left)
            {
                InputManager.Instance.RequestFocus(hit);
                
                // Start dragging if we clicked the empty space of TabBar
                if (hit == _tabBar)
                {
                    _isDragging = true;
                    _lastMousePos = new System.Numerics.Vector2(x, y); // Track relative to window
                    // However, for moving the window, we usually want screen coords or delta.
                    // But here we are in 'OnMouseDown', x/y is window-relative.
                    // Silk.NET doesn't give us screen coords directly in mouse event easily unless looking at global.
                    // We'll use delta logic in OnMouseMove.
                    // Wait, if we move the window, the mouse position relative to window *should* stay same if we move window with mouse.
                    // So we just need to track that we started.
                    // Actually, for window movement, we need system pointer location or use a helper.
                    // Since Silk.NET handles the loop, if we update Window.Position, the mouse relative pos might float.
                    // Standard approach: Get global mouse pos. Silk.NET Viewport vs Screen.
                    // Let's use `_window.PointToScreen(new Vector2D<int>((int)x, (int)y))`? Not standard API.
                    
                    // Simpler: Just track we are dragging.
                }
            }
            hit.OnMouseDown(x, y, button);
        }
        else
        {
            InputManager.Instance.ClearFocus();
        }
    }
    
    private static void ShowContextMenu(float x, float y)
    {
        var activeTab = TabManager.Instance.ActiveTab;
        if (activeTab == null) return;
        
        // Find the WebContentWidget to get its top-left for coordinate translation
        var contentWidget = _root.Children.OfType<WebContentWidget>().FirstOrDefault();
        float localX = x; // Already scaled
        float localY = y;
        float originX = 0;
        float originY = 0;
        
        if (contentWidget != null)
        {
            originX = contentWidget.Bounds.Left;
            originY = contentWidget.Bounds.Top;
        }
        
        var result = activeTab.Browser.PerformHitTest(x, y, originX, originY);
        
        var items = ContextMenuBuilder.Build(
            result,
            activeTab.Url,
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
            onCopyLink: url => { /* TODO: copy to clipboard */ },
            onViewPageSource: viewSourceUrl => TabManager.Instance.CreateTab(viewSourceUrl),
            onInspectElement: hitResult => {
                var bbox = hitResult.BoundingBox ?? default;
                FenLogger.Info($"[Inspect] Tag={hitResult.TagName} ID={hitResult.ElementId} Bounds=({bbox.Left:F0},{bbox.Top:F0},{bbox.Width:F0}x{bbox.Height:F0})", LogCategory.General);
            }
        );

        
        _contextMenu = new ContextMenuWidget(items);
        _contextMenu.Show(x, y, _window.Size.X, _window.Size.Y);
    }
    
    private static void OnMouseUp(IMouse mouse, MouseButton button)
    {
        float x = mouse.Position.X / _dpiScale;
        float y = mouse.Position.Y / _dpiScale;
        
        var captured = InputManager.Instance.CapturedWidget;
        if (captured != null)
        {
            captured.OnMouseUp(x, y, button);
            InputManager.Instance.ReleaseCapture();
            return;
        }
        
        // Check DevTools
        if (_devTools != null && _devTools.IsVisible)
        {
            float devToolsHeight = _devTools.Height;
            var dtBounds = new SKRect(0, _logicalHeight - devToolsHeight, _logicalWidth, _logicalHeight);
            if (dtBounds.Contains(x, y))
            {
                _devTools.OnMouseUp(x, y);
                return;
            }
        }
        
        if (button == MouseButton.Left)
        {
            _isDragging = false;
        }
        
        _root?.HitTestDeep(x, y)?.OnMouseUp(x, y, button);
    }
    
    private static void OnMouseMove(IMouse mouse, System.Numerics.Vector2 position)
    {
        var x = position.X / _dpiScale;
        var y = position.Y / _dpiScale;
        
        // Forward to DevTools
        if (_devTools != null && _devTools.IsVisible)
        {
            _devTools.OnMouseMove(x, y);
        }

        // Window Dragging logic
        if (_isDragging)
        {
             // We need to move the window.
             // Delta is implied.
             // But Wait, OnMouseMove gives new position X, Y relative to window top-left.
             // If we move the window, the relative X, Y shouldn't change ideally (mouse moves with window).
             // But if mouse moved physically, X, Y changes.
             // Delta = currentPos - startPos.
             // NewWindowPos = OldWindowPos + Delta.
             // But we need to use Screen Coordinates for Window.Position.
             // And X,Y are client coordinates.
             
             // Simple naive drag:
             // 1. Calculate delta in pixels.
             // 2. Add to Window.Position.
             // 3. CAUTION: If we move window, the frame of reference moves. 
             // If we add delta to window pos, the window moves under the mouse.
             // The mouse is effectively stationary on screen, so relative X/Y changes inversely?
             // No, if we move window to match mouse movement, relative X/Y stays 0 delta.
             
             // Correct logic for custom drag:
             // OnMouseDown: capture local point (anchor).
             // OnMouseMove: 
             //   delta = currentLocal - anchorLocal.
             //   Window.Position += delta.
             //   (Because if mouse moved RIGHT (positive delta), we want window to move RIGHT so that anchor stays under mouse.)
             
             var deltaX = (int)(x - _lastMousePos.X);
             var deltaY = (int)(y - _lastMousePos.Y);
             
             if (deltaX != 0 || deltaY != 0)
             {
                 _window.Position += new Vector2D<int>(deltaX, deltaY);
                 // Note: _lastMousePos is NOT updated because we want to keep anchoring to the original click point relative to the window!
                 // If we updated _lastMousePos, we would drift.
                 // Actually, wait. If we move the window, the mouse position relative to window *should* revert to original if we moved it exactly.
                 // But due to lag, it might not.
                 // Ideally, we don't update _lastMousePos.
             }
        }
        
        var captured = InputManager.Instance.CapturedWidget;
        if (captured != null)
        {
            captured.OnMouseMove(x, y);
            return;
        }
        
        var hit = _root?.HitTestDeep(x, y);
        
        // Handle Hover Leave
        if (_hoveredWidget != hit)
        {
            if (_hoveredWidget != null)
            {
                // Simulate mouse leave by sending coordinates outside bounds
                _hoveredWidget.OnMouseMove(-9999, -9999);
            }
            _hoveredWidget = hit;
        }

        if (hit != null)
        {
            hit.OnMouseMove(x, y);
            
            // If it's the web content, we handle cursor and status bar
            if (hit is WebContentWidget web)
            {
                var activeTab = TabManager.Instance.ActiveTab;
                if (activeTab != null)
                {
                    var result = activeTab.Browser.HandleMouseMove(x, y, web.Bounds.Left, web.Bounds.Top);
                    CursorManager.UpdateFromHitTest(mouse, result);
                    _statusBar?.UpdateFromHitTest(result);
                }
            }
            else
            {
                CursorManager.ResetCursor(mouse);
                _statusBar?.ClearHoverUrl();
            }
        }
        
        // Dispatch to context menu if open (Z-order usually handled but this is legacy glue)
        if (_contextMenu?.IsOpen == true)
        {
            _contextMenu.OnMouseMove(x, y);
        }
    }
    
    private static void OnScroll(IMouse mouse, ScrollWheel wheel)
    {
        float x = mouse.Position.X / _dpiScale;
        float y = mouse.Position.Y / _dpiScale;
        
        // Check DevTools
        if (_devTools != null && _devTools.IsVisible)
        {
            float devToolsHeight = _devTools.Height;
            var dtBounds = new SKRect(0, _logicalHeight - devToolsHeight, _logicalWidth, _logicalHeight);
            if (dtBounds.Contains(x, y))
            {
                _devTools.OnMouseWheel(x, y, wheel.X, wheel.Y);
                return;
            }
        }

        var hit = _root?.HitTestDeep(x, y);
        hit?.OnMouseWheel(x, y, wheel.X, wheel.Y);
    }

    private static void DrawTooltip(SKCanvas canvas)
    {
        if (_hoveredWidget == null || string.IsNullOrEmpty(_hoveredWidget.HelpText)) return;
        
        // Don't show tooltip if we are dragging or clicking
        if (_isDragging || _mouse.IsButtonPressed(MouseButton.Left)) return;
        
        var theme = ThemeManager.Current;
        var text = _hoveredWidget.HelpText;
        
        using var paint = new SKPaint
        {
            Color = theme.Text,
            IsAntialias = true,
            TextSize = 12,
            Typeface = SKTypeface.FromFamilyName("Segoe UI", SKFontStyle.Normal)
        };
        
        var textBounds = new SKRect();
        paint.MeasureText(text, ref textBounds);
        
        float padding = 6;
        float width = textBounds.Width + padding * 2;
        float height = textBounds.Height + padding * 2;
        
        // Position near mouse
        float mouseX = _mouse.Position.X / _dpiScale;
        float mouseY = _mouse.Position.Y / _dpiScale;
        float x = mouseX + 10;
        float y = mouseY + 20;
        
        // Bounds check
        if (x + width > _logicalWidth) x = _logicalWidth - width - 5;
        if (y + height > _logicalHeight) y = _logicalHeight - height - 5;
        
        var rect = new SKRect(x, y, x + width, y + height);
        
        // Shadow/Background
        using var bgPaint = new SKPaint { Color = theme.Surface, IsAntialias = false };
        canvas.DrawRect(rect, bgPaint);
        
        using var borderPaint = new SKPaint { Color = theme.Border, IsAntialias = false, Style = SKPaintStyle.Stroke };
        canvas.DrawRect(rect, borderPaint);
        
        canvas.DrawText(text, x + padding, y + padding + textBounds.Height, paint);
    }
}

