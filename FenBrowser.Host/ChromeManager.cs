using System.Linq;
using System.Threading.Tasks;
using FenBrowser.Core;
using FenBrowser.Core.Logging;
using FenBrowser.Host.Widgets;
using FenBrowser.Host.Input;
using InputManager = FenBrowser.Host.Input.InputManager;
using FenBrowser.Host.Tabs;
using FenBrowser.Host.Context;
using FenBrowser.Host.Theme;
using FenBrowser.FenEngine.Interaction;
using FenBrowser.DevTools.Core;
using Silk.NET.Input;
using SkiaSharp;
using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Css;
using FenBrowser.FenEngine.Rendering;
using FenBrowser.WebDriver;
using FenBrowser.WebDriver.Commands;
using FenBrowser.Host.WebDriver;

namespace FenBrowser.Host
{
    /// <summary>
    /// Manages the Browser Chrome (UI Widgets, Layout, Context Menu, DevTools).
    /// </summary>
    public class ChromeManager
    {
        private static ChromeManager _instance;
        public static ChromeManager Instance => _instance ??= new ChromeManager();

        private Compositor _compositor;
        private RootWidget _root;
        
        private TabBarWidget _tabBar;
        private ToolbarWidget _toolbar;
        private StatusBarWidget _statusBar;
        private ContextMenuWidget _contextMenu;
        private Widget _focusedWidget => InputManager.Instance.FocusedWidget;
        private Widget _hoveredWidget;
        
        // Window Drag State
        private bool _isDragging = false;
        private System.Numerics.Vector2 _lastMousePos;

        // DevTools
        private DevToolsController _devTools;
        private DevToolsHostAdapter _devToolsHost;
        private DevToolsServer _devToolsServer;
        private RemoteDebugServer _remoteDebugServer;
        private FenBrowser.DevTools.Instrumentation.DomInstrumenter _domInstrumenter;
        
        // WebDriver
        private WebDriverServer _webDriverServer;
        private FenBrowserDriver _webDriverAdapter;

        // Track Active Tab
        private BrowserTab _currentActiveTab;
        private IMouse _mouse; // For cursor

        private ChromeManager() { }


        public void Initialize(string initialUrl)
        {
            FenLogger.Info("[ChromeManager] Initializing Widgets...", LogCategory.General);

            InitializeWidgets(initialUrl);
            InitializeDevTools();
            InitializeWebDriver();
            
            // Create Root and Compositor
            _root = new RootWidget(_tabBar, _toolbar, _statusBar, new DevToolsWidget(_devTools));
            _root.SetContent(new WebContentWidget());
            
            // Wire Bookmarks
            _root.BookmarksBar.BookmarkClicked += OnNavigate;
            
            _compositor = new Compositor(_root);
            _compositor.DpiScale = WindowManager.Instance.DpiScale;

            // Wire TabManager
            TabManager.Instance.ActiveTabChanged += OnActiveTabChanged;
            
            // Create Initial Tab
            TabManager.Instance.CreateTab(initialUrl);
            
            RegisterKeyboardShortcuts();
            
            // Wire WindowManager Events
            WireWindowEvents();
        }

        private void WireWindowEvents()
        {
            var wm = WindowManager.Instance;
            wm.OnResize += size => {
                _compositor.DpiScale = wm.DpiScale;
                LayoutWidgets();
            };
            
            wm.OnRender += dt => Render(wm.Canvas);
            
            // Input Wiring
            wm.OnKeyDown += OnKeyDown;
            wm.OnKeyChar += OnKeyChar;
            wm.OnMouseDown += OnMouseDown;
            wm.OnMouseUp += OnMouseUp;
            wm.OnMouseMove += OnMouseMove;
            wm.OnScroll += OnScroll;
            wm.OnClose += Shutdown;
        }

        private void InitializeWidgets(string initialUrl)
        {
            _tabBar = new TabBarWidget();
            _tabBar.NewTabRequested += () => TabManager.Instance.CreateTab("fen://newtab");
            _tabBar.TabActivated += tab => TabManager.Instance.SwitchToTab(TabManager.Instance.Tabs.ToList().IndexOf(tab));
            _tabBar.TabCloseRequested += tab => TabManager.Instance.CloseTab(TabManager.Instance.Tabs.ToList().IndexOf(tab));
            
            // Window Controls
            _tabBar.MinimizeRequested += () => WindowManager.Instance.Window.WindowState = Silk.NET.Windowing.WindowState.Minimized;
            _tabBar.MaximizeRequested += () => {
                var w = WindowManager.Instance.Window;
                w.WindowState = w.WindowState == Silk.NET.Windowing.WindowState.Maximized 
                    ? Silk.NET.Windowing.WindowState.Normal 
                    : Silk.NET.Windowing.WindowState.Maximized;
            };
            _tabBar.CloseRequested += () => WindowManager.Instance.Window.Close();

            // Toolbar
            _toolbar = new ToolbarWidget();
            _toolbar.SetUrl(initialUrl);
            _toolbar.NavigateRequested += OnNavigate;
            _toolbar.BackClicked += async () => { if (TabManager.Instance.ActiveTab != null) await TabManager.Instance.ActiveTab.Browser.GoBackAsync(); };
            _toolbar.RefreshClicked += async () => { if (TabManager.Instance.ActiveTab != null) await TabManager.Instance.ActiveTab.Browser.RefreshAsync(); };
            _toolbar.HomeClicked += () => OnNavigate(BrowserSettings.Instance.HomePage);
            _toolbar.AddressBar.FocusRequested += w => InputManager.Instance.RequestFocus(w);
            _toolbar.AddressBar.BookmarkToggled += OnBookmarkToggled;
            _toolbar.SettingsClicked += () => ActivateOrOpen("fen://settings");
            _toolbar.FavoritesClicked += () => ActivateOrOpen("fen://settings"); // TODO: specialized favorites

            _statusBar = new StatusBarWidget();
        }

        private void ActivateOrOpen(string url)
        {
             var existing = TabManager.Instance.Tabs.FirstOrDefault(t => t.Url.Equals(url, StringComparison.OrdinalIgnoreCase));
             if (existing != null)
                 TabManager.Instance.SwitchToTab(TabManager.Instance.Tabs.ToList().IndexOf(existing));
             else
                 TabManager.Instance.CreateTab(url);
        }

        private void InitializeDevTools()
        {
            _devToolsServer = new DevToolsServer();
            _remoteDebugServer = new RemoteDebugServer(_devToolsServer, 9222);
            _remoteDebugServer.Start();
            _domInstrumenter = new FenBrowser.DevTools.Instrumentation.DomInstrumenter(_devToolsServer);
            
            _devToolsServer.OnJsonOutput(json => FenLogger.Debug($"[DevTools-JSON] {json}", LogCategory.General));

            _devTools = new DevToolsController();
            _devTools.RegisterPanel(new FenBrowser.DevTools.Panels.ElementsPanel());
            _devTools.RegisterPanel(new FenBrowser.DevTools.Panels.ConsolePanel());
            _devTools.RegisterPanel(new FenBrowser.DevTools.Panels.NetworkPanel());
            
            TabManager.Instance.ActiveTabChanged += SetupDevToolsForTab;
            _devTools.Invalidated += () => _root?.Invalidate();
            
            FenLogger.Info("[ChromeManager] DevTools Initialized", LogCategory.General);
        }

        private void InitializeWebDriver()
        {
            try
            {
                _webDriverServer = new WebDriverServer(4444);
                _webDriverServer.OnLog += msg => FenLogger.Info(msg, LogCategory.General);
                _webDriverServer.Start();
                FenLogger.Info("[ChromeManager] WebDriver Server started on port 4444", LogCategory.General);
            }
            catch (Exception ex)
            {
                FenLogger.Error($"[ChromeManager] Failed to start WebDriver Server: {ex.Message}", LogCategory.General);
            }
        }
        
        private void SetupDevToolsForTab(BrowserTab tab)
        {
            if (tab == null) return;
            
            _devToolsServer.Reset();
            
            // DOM Access
            _devToolsServer.InitializeDom(
                () => WindowManager.Instance.RunOnMainThread(() => tab.Browser.Document).Result,
                nodeId => WindowManager.Instance.RunOnMainThread(() => {
                    var node = _devToolsServer.Registry.GetNode(nodeId.Value);
                    tab.Browser.HighlightElement(node as Element);
                }).Wait()
            );
            
            // CSS Access
            _devToolsServer.InitializeCss(
                node => WindowManager.Instance.RunOnMainThread(() => tab.Browser.ComputedStyles.TryGetValue(node, out var s) ? s : null).Result, 
                node => WindowManager.Instance.RunOnMainThread(() => {
                    try
                    {
                        return node is Element el && tab.Browser.CssSources != null 
                            ? CssLoader.GetMatchedRules(el, tab.Browser.CssSources) 
                            : new System.Collections.Generic.List<CssLoader.MatchedRule>();
                    }
                    catch
                    {
                        return new System.Collections.Generic.List<CssLoader.MatchedRule>();
                    }
                }).Result,
                (node, prop, val) => WindowManager.Instance.RunOnMainThread(() => {
                    if (node is Element el) {
                        // Simple style patching
                         var existing = el.GetAttribute("style") ?? "";
                         var styles = existing.Split(';', StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).Where(s => !string.IsNullOrEmpty(s)).ToDictionary(s => s.Split(':')[0].Trim().ToLower(), s => s.Split(':')[1].Trim());
                         
                         if (string.IsNullOrWhiteSpace(val)) styles.Remove(prop.ToLower());
                         else styles[prop.ToLower()] = val;
                         
                         el.SetAttribute("style", string.Join("; ", styles.Select(kv => $"{kv.Key}: {kv.Value}")));
                         tab.Browser.InvalidateComputedStyle(el);
                    }
                }).Wait(),
                () => WindowManager.Instance.RunOnMainThread(() => tab.Browser.RequestRepaint()).Wait()
            );

            // Update WebDriver focus
            if (_webDriverServer != null)
            {
                _webDriverAdapter = new FenBrowserDriver(tab.Browser);
                _webDriverServer.GetType().GetField("_handler", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                    ?.GetValue(_webDriverServer)?.GetType().GetProperty("Browser")?.SetValue(
                        _webDriverServer.GetType().GetField("_handler", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.GetValue(_webDriverServer),
                        _webDriverAdapter);
            }

            _devToolsHost = new DevToolsHostAdapter(tab.Browser, _devToolsServer);
            _devToolsHost.CursorChanged += cursor => CursorManager.UpdateCursorFromDevTools(_mouse, cursor);
            
            // Wire up capture events for proper drag handling
            var devToolsWidget = _root.FindWidget<DevToolsWidget>();
            if (devToolsWidget != null)
            {
                _devToolsHost.CaptureRequested += () => InputManager.Instance.SetCapture(devToolsWidget);
                _devToolsHost.CaptureReleased += () => InputManager.Instance.ReleaseCapture();
            }
            
            _devTools.Attach(_devToolsHost);
        }

        private void RegisterKeyboardShortcuts()
        {
            var kbd = KeyboardDispatcher.Instance;
            
            kbd.RegisterCtrl(Key.T, () => TabManager.Instance.CreateTab("fen://newtab"));
            kbd.RegisterCtrl(Key.W, () => TabManager.Instance.CloseActiveTab());
            kbd.RegisterGlobal(Key.Tab, true, false, false, () => TabManager.Instance.NextTab()); 
            kbd.RegisterCtrl(Key.L, () => InputManager.Instance.RequestFocus(_toolbar.AddressBar));
            kbd.RegisterCtrl(Key.R, async () => { if(TabManager.Instance.ActiveTab != null) await TabManager.Instance.ActiveTab.Browser.RefreshAsync(); });
            kbd.Register(Key.F5, async () => { if(TabManager.Instance.ActiveTab != null) await TabManager.Instance.ActiveTab.Browser.RefreshAsync(); });
            kbd.Register(Key.Escape, () => {
                if (_contextMenu?.IsOpen == true) _contextMenu.Hide();
                else InputManager.Instance.ClearFocus();
            });
        }

        private void LayoutWidgets() => _root?.InvalidateLayout();

        private void OnNavigate(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return;
            WindowManager.Instance.Window.Title = "Loading...";
            TabManager.Instance.ActiveTab?.NavigateAsync(url);
        }

        private void OnActiveTabChanged(BrowserTab tab)
        {
            if (_currentActiveTab != null)
            {
                _currentActiveTab.Browser.UrlChanged -= OnBrowserUrlChanged;
                _currentActiveTab.Browser.ContextMenuRequested -= OnContextMenuRequested;
            }
            
            _currentActiveTab = tab;
            
            if (tab != null)
            {
                tab.Browser.UrlChanged += OnBrowserUrlChanged;
                tab.Browser.ContextMenuRequested += OnContextMenuRequested;
                _toolbar.SetUrl(tab.Url);
                UpdateBookmarkStar(tab.Url);
                WindowManager.Instance.Window.Title = $"FenBrowser - {tab.Title}";
            }
            _root.Invalidate();
        }

        private void OnBrowserUrlChanged(string url)
        {
            _toolbar.SetUrl(url);
            UpdateBookmarkStar(url);
            _root.Invalidate();
        }
        
        private void UpdateBookmarkStar(string url)
        {
             _toolbar.AddressBar.IsBookmarked = BrowserSettings.Instance.Bookmarks.Any(b => b.Url.Equals(url, StringComparison.OrdinalIgnoreCase));
        }

        private void OnBookmarkToggled()
        {
            var tab = TabManager.Instance.ActiveTab;
            if (tab == null) return;
            var url = tab.Url;
            var existing = BrowserSettings.Instance.Bookmarks.FirstOrDefault(b => b.Url.Equals(url, StringComparison.OrdinalIgnoreCase));
            if (existing != null) BrowserSettings.Instance.Bookmarks.Remove(existing);
            else BrowserSettings.Instance.Bookmarks.Add(new Bookmark { Title = tab.Title ?? url, Url = url });
            
            BrowserSettings.Instance.Save();
            UpdateBookmarkStar(url);
            _root?.BookmarksBar.RefreshBookmarks();
            _root?.Invalidate();
        }

        private void OnContextMenuRequested(BrowserIntegration.ContextMenuRequest request)
        {
            ShowContextMenu(request.X, request.Y, request.Hit);
        }
        
        private void ShowContextMenu(float x, float y, FenBrowser.FenEngine.Interaction.HitTestResult hit)
        {
            var activeTab = TabManager.Instance.ActiveTab;
            if (activeTab == null) return;
            
             var items = ContextMenuBuilder.Build(
                 hit,
                 activeTab.Url,
                 hasSelection: false,
                 canPaste: true,
                 onNavigate: OnNavigate,
                 onCopy: () => {},
                 onPaste: () => {},
                 onSelectAll: () => {},
                 onReload: async () => await activeTab.Browser.RefreshAsync(),
                 onBack: async () => await activeTab.Browser.GoBackAsync(),
                 onForward: async () => await activeTab.Browser.GoForwardAsync(),
                 onOpenInNewTab: url => TabManager.Instance.CreateTab(url),
                 onCopyLink: url => {}, // Clipboard
                 onViewPageSource: url => TabManager.Instance.CreateTab(url),
                 onInspectElement: h => {
                      _root.SetPopup(null);
                      _devTools?.Show();
                      if (h.NativeElement is Element el) _devTools?.SelectElement(el);
                      _root?.Invalidate();
                 }
             );
             
             _contextMenu = new ContextMenuWidget(items);
             _contextMenu.CloseRequested += () => _root.SetPopup(null);
             _root.SetPopup(_contextMenu);
             
             // Convert to Logical coords if needed? Show() takes logical coords.
             // Wait, Show takes width/height of window for bounds check.
             var wm = WindowManager.Instance;
             _contextMenu.Show(x, y, wm.LogicalWidth, wm.LogicalHeight);
        }

        private void Render(SKCanvas canvas)
        {
            if (_compositor == null) return;
            _compositor.Composite(canvas, new SKSize(WindowManager.Instance.LogicalWidth, WindowManager.Instance.LogicalHeight));
            DrawTooltip(canvas);
        }
        
        // --- Input Propagation ---

        private void OnKeyDown(IKeyboard k, Key key, int code)
        {
            bool ctrl = k.IsKeyPressed(Key.ControlLeft) || k.IsKeyPressed(Key.ControlRight);
            bool shift = k.IsKeyPressed(Key.ShiftLeft) || k.IsKeyPressed(Key.ShiftRight);
            bool alt = k.IsKeyPressed(Key.AltLeft) || k.IsKeyPressed(Key.AltRight);

            if (_contextMenu?.IsOpen == true)
            {
                _contextMenu.OnKeyDown(key, ctrl, shift, alt);
                if (_contextMenu.IsOpen) return;
            }

            KeyboardDispatcher.Instance.IsCtrlPressed = ctrl;

            if (_devTools != null && _devTools.IsVisible)
            {
                 // Legacy DevTools Key Mapping
                 int dtKey = (int)key;
                 if (key == Key.Backspace) dtKey = 8;
                 else if (key == Key.Enter) dtKey = 13;
                 else if (key == Key.Escape) dtKey = 27;
                 
                if (_devTools.OnKeyDown(dtKey, ctrl, shift, alt)) return;
            }

            if (KeyboardDispatcher.Instance.Dispatch(key, ctrl, shift, alt)) return;
        }

        private void OnKeyChar(IKeyboard k, char c)
        {
            if (_devTools != null && _devTools.IsVisible) _devTools.OnTextInput(c);
            bool ctrl = k.IsKeyPressed(Key.ControlLeft) || k.IsKeyPressed(Key.ControlRight);
            KeyboardDispatcher.Instance.DispatchChar(c, ctrl);
        }

        private void OnMouseDown(IMouse m, MouseButton b)
        {
            _mouse = m; // Cache for other logic
            float dpi = WindowManager.Instance.DpiScale;
            float x = m.Position.X / dpi;
            float y = m.Position.Y / dpi;

            if (_contextMenu?.IsOpen == true && !_contextMenu.Bounds.Contains(x,y)) { _contextMenu.Hide(); return; }
            if (_contextMenu?.IsOpen == true) { _contextMenu.OnMouseDown(x,y,b); return; }

            var hit = _root?.HitTestDeep(x, y);
            
            if (hit != null)
            {
                if (b == MouseButton.Left)
                {
                    InputManager.Instance.RequestFocus(hit);
                    if (hit == _tabBar)
                    {
                        _isDragging = true;
                        _lastMousePos = new System.Numerics.Vector2(x,y); // Logic relative tracking
                    }
                }
                hit.OnMouseDown(x, y, b);
            }
            else InputManager.Instance.ClearFocus();
        }

        private void OnMouseUp(IMouse m, MouseButton b)
        {
             float dpi = WindowManager.Instance.DpiScale;
             float x = m.Position.X / dpi;
             float y = m.Position.Y / dpi;
             
             var cap = InputManager.Instance.CapturedWidget;
             if (cap != null)
             {
                 cap.OnMouseUp(x,y,b);
                 InputManager.Instance.ReleaseCapture();
                 return;
             }
             
             if (b == MouseButton.Left) _isDragging = false;
             
             _root?.HitTestDeep(x,y)?.OnMouseUp(x,y,b);
        }

        private void OnMouseMove(IMouse m, System.Numerics.Vector2 pos)
        {
            _mouse = m;
            float dpi = WindowManager.Instance.DpiScale;
            float x = pos.X / dpi;
            float y = pos.Y / dpi;
            
            if (_isDragging) {
                 // Implement Window Drag
                 // Delta logic:
                 var deltaX = (int)(x - _lastMousePos.X);
                 var deltaY = (int)(y - _lastMousePos.Y);
                 if (deltaX != 0 || deltaY != 0)
                    WindowManager.Instance.Window.Position += new Silk.NET.Maths.Vector2D<int>(deltaX, deltaY);
            }

             var cap = InputManager.Instance.CapturedWidget;
             if (cap != null) { cap.OnMouseMove(x,y); return; }

             var hit = _root?.HitTestDeep(x,y);
             
             if (_hoveredWidget != hit) {
                 _hoveredWidget?.OnMouseMove(-9999, -9999); // Leave
                 _hoveredWidget = hit;
             }
             
             if (hit != null) {
                 hit.OnMouseMove(x,y);
                 
                 if (hit is WebContentWidget web && TabManager.Instance.ActiveTab != null) {
                      var result = TabManager.Instance.ActiveTab.Browser.HandleMouseMove(x,y, web.Bounds.Left, web.Bounds.Top);
                      CursorManager.UpdateFromHitTest(m, result);
                      _statusBar?.UpdateFromHitTest(result);
                 } else {
                     CursorManager.ResetCursor(m);
                     _statusBar?.ClearHoverUrl();
                 }
             }
             
             if (_contextMenu?.IsOpen == true) _contextMenu.OnMouseMove(x,y);
        }
        
        private void OnScroll(IMouse m, ScrollWheel w)
        {
            float dpi = WindowManager.Instance.DpiScale;
            float x = m.Position.X / dpi;
            float y = m.Position.Y / dpi;
            
            if (_devTools != null && _devTools.IsVisible) {
                // Bounds check devtools (simplified)
                 if (y > WindowManager.Instance.LogicalHeight - _devTools.Height) {
                     _devTools.OnMouseWheel(x,y, w.X, w.Y);
                     return;
                 }
            }
            
            _root?.HitTestDeep(x,y)?.OnMouseWheel(x,y, w.X, w.Y);
        }
        
        private void DrawTooltip(SKCanvas canvas)
        {
            if (_hoveredWidget == null || string.IsNullOrEmpty(_hoveredWidget.HelpText) || _isDragging || (_mouse != null && _mouse.IsButtonPressed(MouseButton.Left))) return;
            
            var text = _hoveredWidget.HelpText;
            var theme = ThemeManager.Current;
            using var paint = new SKPaint { Color = theme.Text, IsAntialias = true, TextSize = 12 };
            var bounds = new SKRect();
            paint.MeasureText(text, ref bounds);
            
            float mouseX = _mouse.Position.X / WindowManager.Instance.DpiScale;
            float mouseY = _mouse.Position.Y / WindowManager.Instance.DpiScale;
            float dX = mouseX + 10;
            float dY = mouseY + 20;

            var rect = new SKRect(dX, dY, dX + bounds.Width + 12, dY + bounds.Height + 12);
            using var bg = new SKPaint { Color = theme.Surface };
            using var border = new SKPaint { Color = theme.Border, Style = SKPaintStyle.Stroke };
            
            canvas.DrawRect(rect, bg);
            canvas.DrawRect(rect, border);
            canvas.DrawText(text, dX + 6, dY + 6 + bounds.Height, paint);
        }

        private void Shutdown()
        {
             // DevToolsServer is not IDisposable
             _remoteDebugServer?.Dispose();
        }
    }
}
