using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Threading;
using FenBrowser.Core;
using FenBrowser.FenEngine.Rendering;
using FenBrowser.FenEngine.Security; // Added
using FenBrowser.WebDriver;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using FenBrowser.Core.Logging;
using Avalonia.Input;

namespace FenBrowser.UI
{
    public class ExtensionModel
    {
        public string Name { get; set; }
        public string Icon { get; set; }
    }

    public class TabItemModel : System.ComponentModel.INotifyPropertyChanged
    {
        public event System.ComponentModel.PropertyChangedEventHandler PropertyChanged;

        private bool _isActive;
        private string _title = "New Tab";
        
        public int Id { get; set; }
        
        public string Title
        {
            get => _title;
            set
            {
                if (_title != value)
                {
                    _title = value;
                    PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Title)));
                }
            }
        }

        public bool IsActive
        {
            get => _isActive;
            set
            {
                if (_isActive != value)
                {
                    _isActive = value;
                    PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(IsActive)));
                }
            }
        }
        public IBrowser Browser { get; set; }
        public object LastRenderedContent { get; set; }
        
        private bool _isDevToolsOpen;
        public bool IsDevToolsOpen
        {
            get => _isDevToolsOpen;
            set
            {
                if (_isDevToolsOpen != value)
                {
                    _isDevToolsOpen = value;
                    PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(IsDevToolsOpen)));
                }
            }
        }
        
        public DevToolsView DevToolsInstance { get; set; }
    } // end TabItemModel

    public partial class MainWindow : Window
    {
        private int _nextTabId = 1;

        // private BrowserHost _browser; // Removed single instance
        private IBrowser _activeBrowser;
        private WebDriverServer _webDriver;

        // Note: leave these null if your XAML has x:Name and source-gen will produce them.
        // You can uncomment the FindControl lines in ctor to locate by name at runtime.
        private ItemsControl _extensionsArea;
        private ItemsControl _tabsStrip;
        private Control _browserContainer; // keep generic because your XAML may use Border, ContentControl, Panel, etc.
        private TextBox _addressBox;
        private ProgressBar _loadingBar;
        private Border _devToolsContainer;
        private Canvas _highlightOverlay;
        private Avalonia.Controls.Shapes.Path _securityIconPath;
        private Button _siteInfoButton;
        private ScrollViewer _skiaScrollViewer;
        private ContentControl _specialPageContainer;

        public ObservableCollection<TabItemModel> Tabs { get; } = new ObservableCollection<TabItemModel>();
        public ObservableCollection<ExtensionModel> Extensions { get; } = new ObservableCollection<ExtensionModel>();

        // StyledProperty for binding
        public static readonly StyledProperty<double> CurrentTabWidthProperty =
            AvaloniaProperty.Register<MainWindow, double>(nameof(CurrentTabWidth), defaultValue: 200);

        public double CurrentTabWidth
        {
            get => GetValue(CurrentTabWidthProperty);
            set => SetValue(CurrentTabWidthProperty, value);
        }

        public bool IsPrivate { get; }

        public MainWindow(int? port = null, string initialUrl = null, bool isPrivate = false)
        {
            IsPrivate = isPrivate;
            InitializeComponent();
            DataContext = this; // Set DataContext for bindings

            // Wire up controls manually since source generator isn't binding to private fields
            _extensionsArea = this.FindControl<ItemsControl>("ExtensionsArea");
            _tabsStrip = this.FindControl<ItemsControl>("TabsStrip");
            _browserContainer = this.FindControl<Border>("BrowserContainer");
            _devToolsContainer = this.FindControl<Border>("DevToolsContainer");
            _addressBox = this.FindControl<TextBox>("AddressBox");
            _addressBox = this.FindControl<TextBox>("AddressBox");
            _loadingBar = this.FindControl<ProgressBar>("LoadingBar");
            _highlightOverlay = this.FindControl<Canvas>("HighlightOverlay");
            _securityIconPath = this.FindControl<Avalonia.Controls.Shapes.Path>("SecurityIconPath");
            _securityIconPath = this.FindControl<Avalonia.Controls.Shapes.Path>("SecurityIconPath");
            _siteInfoButton = this.FindControl<Button>("SiteInfoButton");
            _skiaScrollViewer = this.FindControl<ScrollViewer>("SkiaScrollViewer");
            _specialPageContainer = this.FindControl<ContentControl>("SpecialPageContainer");
            
            // Wire up SkiaView interaction
            var skiaView = this.FindControl<SkiaBrowserView>("SkiaView");
            if (skiaView != null)
            {
                skiaView.LinkInternalClicked += (s, url) =>
                {
                    Dispatcher.UIThread.Post(async () =>
                    {
                        if (_activeBrowser != null)
                        {
                             // Determine if absolute or relative?
                             // SkiaDomRenderer resolved it? No, checks href.
                             // ImageLoader handles relative URLs for images.
                             // We should probably help resolve it here if needed, 
                             // OR ensure CheckLink in SkiaBrowserView resolves it.
                             // But let's assume raw href and resolving happens in BrowserApi or BrowserHost.
                             // Usually NavigateAsync(url) handles absolute.
                             // If it is "/wiki/Foo", NavigateAsync might fail if it expects absolute.
                             // Let's resolve against current URI if relative.
                             
                             var current = _activeBrowser.CurrentUri;
                             if (current != null && !url.StartsWith("http") && !url.StartsWith("data:"))
                             {
                                 try
                                 {
                                     var resolved = new Uri(current, url);
                                     url = resolved.AbsoluteUri;
                                 }
                                 catch {}
                             }
                             
                             await _activeBrowser.NavigateAsync(url);
                        }
                    });
                };
            }

            // Register with WebDriverIntegration for real window/tab/screenshot operations
            WebDriverIntegration.RegisterMainWindow(
                this,
                _browserContainer,
                createTab: async () => {
                    var tab = new TabItemModel { Id = _nextTabId++, Title = "New Tab", IsActive = false };
                    Tabs.Add(tab);
                    CreateBrowserForTab(tab);
                    return tab.Id.ToString();
                },
                closeTab: (handle) => {
                    var tab = Tabs.FirstOrDefault(t => t.Id.ToString() == handle);
                    if (tab != null && Tabs.Count > 1) {
                        Tabs.Remove(tab);
                        return true;
                    }
                    return false;
                },
                switchTab: (handle) => {
                    var tab = Tabs.FirstOrDefault(t => t.Id.ToString() == handle);
                    if (tab != null) {
                        // Inline tab switching logic (from OnTabClicked)
                        foreach (var t in Tabs) t.IsActive = t.Id == tab.Id;
                        _activeBrowser = tab.Browser;
                        if (tab.LastRenderedContent is Control c)
                            SetBrowserContainerContent(c);
                        UpdateAddressBar(_activeBrowser);
                        return true;
                    }
                    return false;
                },
                getTabHandles: () => Tabs.Select(t => t.Id.ToString()),
                getCurrentTab: () => Tabs.FirstOrDefault(t => t.IsActive)?.Id.ToString() ?? "1"
            );

            // initial data
            var initialTab = new TabItemModel { Id = _nextTabId++, Title = "New tab", IsActive = true };
            Tabs.Add(initialTab);

            // ensure ExtensionsArea isn't empty during testing
            Extensions.Add(new ExtensionModel { Name = "Adblock", Icon = "AB" });
            Extensions.Add(new ExtensionModel { Name = "Grammarly", Icon = "G" });
            Extensions.Add(new ExtensionModel { Name = "Translate", Icon = "T" });

            // Bind collections into UI if controls are available
            if (_extensionsArea != null) _extensionsArea.ItemsSource = Extensions;
            if (_tabsStrip != null) _tabsStrip.ItemsSource = Tabs;

            // Setup listeners for tab resizing
            Tabs.CollectionChanged += (s, e) => RecalculateTabWidth();

            // Watch Bounds changes via PropertyChanged (avoids the IObserver lambda ambiguity)
            this.PropertyChanged += (s, e) =>
            {
                if (e.Property == BoundsProperty)
                {
                    RecalculateTabWidth();
                }
            };


            InitializeBrowser(port, initialTab, initialUrl);
            

            // Fix: Bind SkiaView LayoutViewport here since SetBrowserContainerContent is bypassed
            BindSkiaViewport();
            
            this.KeyDown += OnWindowKeyDown;
        }

        private void BindSkiaViewport()
        {
            if (_skiaScrollViewer != null && this.FindControl<SkiaBrowserView>("SkiaView") is SkiaBrowserView skiaView)
            {
                void UpdateLayoutViewport()
                {
                    Dispatcher.UIThread.Post(() => {
                        var vp = _skiaScrollViewer.Viewport;
                        // Use Bounds if Viewport is invalid/zero (initial state)
                        if (vp.Width <= 0 || vp.Height <= 0) vp = _skiaScrollViewer.Bounds.Size;
                        
                        if (vp.Width > 0 && vp.Height > 0)
                        {
                            var skSize = new SkiaSharp.SKSize((float)vp.Width, (float)vp.Height);
                            skiaView.LayoutViewport = skSize;
                        }
                    });
                }
                
                // Initial
                UpdateLayoutViewport();
                
                // Subscribe
                _skiaScrollViewer.GetObservable(ScrollViewer.ViewportProperty).Subscribe(new Avalonia.Reactive.AnonymousObserver<Size>(_ => UpdateLayoutViewport()));
                _skiaScrollViewer.SizeChanged += (s, e) => UpdateLayoutViewport();
            }
        }

        private void OnWindowKeyDown(object sender, Avalonia.Input.KeyEventArgs e)
        {
            if (e.Key == Key.F12)
            {
                ToggleDevTools();
            }
        }

        private void InitializeBrowser(int? port, TabItemModel initialTab, string initialUrl)
        {
            // Create browser for the initial tab
            CreateBrowserForTab(initialTab);
            _activeBrowser = initialTab.Browser;
            UpdateAddressBar(_activeBrowser);

            // Initialize WebDriver with the first tab's browser (for now)
            try 
            { 
                _webDriver = new WebDriverServer(_activeBrowser, port ?? 4444); 
                _webDriver.Start();
                try { FenLogger.Debug($"[WebDriver] Started on port {port ?? 4444}", LogCategory.General); } catch { }
            } 
            catch (Exception ex) 
            { 
                try { FenLogger.Debug($"[WebDriver] Failed to start: {ex.Message}", LogCategory.Errors); } catch { }
            }

            if (!string.IsNullOrEmpty(initialUrl))
            {
                _activeBrowser.NavigateAsync(initialUrl);
            }


        }

        private void CreateBrowserForTab(TabItemModel tab)
        {
            var browser = new BrowserHost(IsPrivate);
            browser.EnableJavaScript = BrowserSettings.Instance.EnableJavaScript;
            tab.Browser = browser;

            // Inject WebDriver delegates for real window/screenshot operations
            browser.GetWindowRectDelegate = () => WebDriverIntegration.GetWindowRect();
            browser.SetWindowRectDelegate = (x, y, w, h) => WebDriverIntegration.SetWindowRect(x, y, w, h);
            browser.MaximizeWindowDelegate = () => WebDriverIntegration.MaximizeWindow();
            browser.MinimizeWindowDelegate = () => WebDriverIntegration.MinimizeWindow();
            browser.FullscreenWindowDelegate = () => WebDriverIntegration.FullscreenWindow();
            browser.CreateNewTabDelegate = () => WebDriverIntegration.CreateNewTabAsync();
            browser.CaptureScreenshotDelegate = () => WebDriverIntegration.CaptureScreenshotAsync();

            browser.RepaintReady += (s, element) =>
            {
                try { FenLogger.Debug($"[MainWindow] RepaintReady fired. Element: {element}", LogCategory.General); } catch {}
                tab.LastRenderedContent = element;
                Dispatcher.UIThread.Post(() =>
                {
                    if (tab.IsActive)
                    {
                        // TEMPORARY SKIA VERIFICATION WITH BOX MODEL
                        var root = browser.GetDomRoot();
                        var skiaView = this.FindControl<SkiaBrowserView>("SkiaView");
                        
                        try { FenLogger.Debug($"[MainWindow] UI Thread. Root: {root?.Tag}, SkiaView: {skiaView}", LogCategory.General); } catch {}

                        if (root != null)
                        {
                           if (skiaView != null) 
                           {
                               // Pass ComputedStyles to renderer
                               try { FenLogger.Debug("[MainWindow] Calling SkiaView.Render", LogCategory.General); } catch {}
                               
                               // 0. Attach Context Menu if missing
                               if (skiaView.ContextMenu == null)
                               {
                                   skiaView.ContextMenu = CreateBrowserContextMenu();
                               }
                               
                               // 1. Set BaseUrl for relative image resolution
                               var newBaseUrl = browser.CurrentUri?.AbsoluteUri ?? "";
                               
                               // Debug: Log BaseUrl being set (skip if empty to reduce log spam)
                               if (!string.IsNullOrEmpty(newBaseUrl)) {
                                   try { FenLogger.Debug($"[MainWindow] Setting SkiaView.BaseUrl = '{newBaseUrl}'", LogCategory.General); } catch {}
                               }
                               
                               skiaView.BaseUrl = newBaseUrl;
                               
                               // 2. Render
                               skiaView.Render(root, browser.ComputedStyles);
                               return; // Skip old renderer logic
                           }
                        }

                        if (element is Avalonia.Controls.Control ctrl)
                            SetBrowserContainerContent(ctrl);
                        else
                            SetBrowserContainerContent(new TextBlock { Text = "Unsupported visual element" });
                        
                        UpdateAddressBar(browser);
                    }
                });
            };

            browser.Navigated += (s, uri) =>
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (tab.IsActive) UpdateAddressBar(browser);
                });
            };

            browser.NavigationFailed += (s, msg) =>
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (tab.IsActive)
                    {
                        SetBrowserContainerContent(new TextBlock
                        {
                            Text = $"Navigation Failed: {msg}",
                            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                            Foreground = Avalonia.Media.Brushes.Red
                        });
                        if (_loadingBar != null) _loadingBar.IsVisible = false;
                    }
                });
            };

            browser.LoadingChanged += (s, isLoading) =>
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (tab.IsActive && _loadingBar != null)
                    {
                        _loadingBar.IsVisible = isLoading;
                    }
                });
            };

            browser.TitleChanged += (s, title) =>
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (!string.IsNullOrWhiteSpace(title))
                    {
                    if (!string.IsNullOrWhiteSpace(title))
                    {
                        tab.Title = title + (IsPrivate ? " (Private)" : "");
                    }
                    }
                });
            };

            browser.HighlightRectChanged += (rect) =>
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (tab.IsActive)
                    {
                        UpdateHighlight(rect);
                    }
                });
            };

            browser.PermissionRequested += async (origin, perm) =>
            {
                return await Dispatcher.UIThread.InvokeAsync(async () =>
                {
                    // Simple custom dialog
                    var tcs = new System.Threading.Tasks.TaskCompletionSource<bool>();
                    
                    var dialog = new Window
                    {
                        Width = 400,
                        Height = 180,
                        Title = "Permissions",
                        WindowStartupLocation = WindowStartupLocation.CenterOwner,
                        CanResize = false,
                        SystemDecorations = SystemDecorations.BorderOnly
                    };

                    // Styling
                    dialog.Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#FAFAFA"));
                    var border = new Border 
                    { 
                        BorderBrush = Avalonia.Media.Brushes.Gray, 
                        BorderThickness = new Thickness(1),
                        Padding = new Thickness(20)
                    };

                    var mainPanel = new StackPanel { Spacing = 15 };
                    mainPanel.Children.Add(new TextBlock 
                    { 
                        Text = "Permission Request", 
                        FontWeight = Avalonia.Media.FontWeight.Bold, 
                        FontSize = 16 
                    });
                    
                    mainPanel.Children.Add(new TextBlock 
                    { 
                        Text = $"The website \"{origin}\" would like to access your:\n{perm}",
                        TextWrapping = Avalonia.Media.TextWrapping.Wrap
                    });

                    var buttons = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right, Spacing = 10 };
                    var btnAllow = new Button { Content = "Allow" }; // Default styles apply
                    var btnDeny = new Button { Content = "Block" };

                    btnAllow.Click += (_, __) => { tcs.TrySetResult(true); dialog.Close(); };
                    btnDeny.Click += (_, __) => { tcs.TrySetResult(false); dialog.Close(); };

                    buttons.Children.Add(btnAllow);
                    buttons.Children.Add(btnDeny);
                    mainPanel.Children.Add(buttons);

                    border.Child = mainPanel;
                    dialog.Content = border;

                    dialog.Closed += (_, __) => tcs.TrySetResult(false);

                    await dialog.ShowDialog(this);
                    return await tcs.Task;
                });
            };
        }

        private void UpdateHighlight(Rect? rect)
        {
            if (_highlightOverlay == null) return;
            _highlightOverlay.Children.Clear();

            if (rect.HasValue)
            {
                var r = rect.Value;
                var border = new Border
                {
                    Width = r.Width,
                    Height = r.Height,
                    BorderBrush = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Colors.Blue, 0.5),
                    BorderThickness = new Thickness(2),
                    Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Colors.Blue, 0.2)
                };
                Canvas.SetLeft(border, r.X);
                Canvas.SetTop(border, r.Y);
                _highlightOverlay.Children.Add(border);
            }
        }

        private void UpdateAddressBar(IBrowser browser)
        {
            var uri = browser?.CurrentUri;
            if (_addressBox != null && uri != null)
            {
                _addressBox.Text = uri.AbsoluteUri;
            }

            if (_securityIconPath != null && browser != null)
            {
                var state = browser.SecurityState;
                if (uri == null || uri.Scheme == "about" || uri.Scheme == "fen")
                {
                    // Neutral / No icon for internal pages
                    _securityIconPath.Data = null; 
                    _securityIconPath.IsVisible = false;
                    if (_siteInfoButton != null) _siteInfoButton.IsVisible = false;
                }
                else
                {
                    _securityIconPath.IsVisible = true;
                    if (_siteInfoButton != null) _siteInfoButton.IsVisible = true;
                    switch (state)
                    {
                        case SecurityState.Secure:
                            // Lock icon
                            _securityIconPath.Data = Avalonia.Media.Geometry.Parse("M12 1L3 5v6c0 5.55 3.84 10.74 9 12 5.16-1.26 9-6.45 9-12V5l-9-4zm0 10.99h7c-.53 4.12-3.28 7.79-7 8.94V12H5V6.3l7-3.11v8.8z");
                            _securityIconPath.Fill = Avalonia.Media.Brushes.Gray; // Or Green/Black depending on theme
                            break;
                        case SecurityState.Warning:
                            // Warning triangle
                            _securityIconPath.Data = Avalonia.Media.Geometry.Parse("M1 21h22L12 2 1 21zm12-3h-2v-2h2v2zm0-4h-2v-4h2v4z");
                            _securityIconPath.Fill = Avalonia.Media.Brushes.Red;
                            break;
                        case SecurityState.NotSecure:
                            // Info icon
                            _securityIconPath.Data = Avalonia.Media.Geometry.Parse("M11 7h2v2h-2zm0 4h2v6h-2zm1-9C6.48 2 2 6.48 2 12s4.48 10 10 10 10-4.48 10-10S17.52 2 12 2zm0 18c-4.41 0-8-3.59-8-8s3.59-8 8-8 8 3.59 8 8-3.59 8-8 8z");
                            _securityIconPath.Fill = Avalonia.Media.Brushes.Gray;
                            break;
                        default:
                            _securityIconPath.Data = null;
                            _securityIconPath.IsVisible = false;
                            if (_siteInfoButton != null) _siteInfoButton.IsVisible = false;
                            break;
                    }
                }
            }
        }


        /// <summary>
        /// Put a control inside BrowserContainer regardless of whether it's a ContentControl, Border/Decorator, Panel, etc.
        /// </summary>
        private void SetBrowserContainerContent(Control content)
        {
            if (_browserContainer == null) return;

            // Fix: Detach content from its previous parent to avoid "Visual already has a parent" error
            if (content.Parent is Avalonia.Controls.ContentControl oldCc)
            {
                oldCc.Content = null;
            }
            else if (content.Parent is Avalonia.Controls.Decorator oldDec)
            {
                oldDec.Child = null;
            }
            else if (content.Parent is Avalonia.Controls.Panel oldPanel)
            {
                oldPanel.Children.Remove(content);
            }

            switch (_browserContainer)
            {
                case ContentControl cc:
                    cc.Content = content;
                    break;

                case Decorator decorator:
                    // Border derives from Decorator and exposes Child
                    if (content is SettingsPage)
                    {
                        decorator.Child = content;
                    }
                    else
                    {
                        // Wrap web content in ScrollViewer to enable scrolling
                        var sv = new ScrollViewer
                        {
                            Content = content,
                            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
                            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto
                        };
                        // Ensure content fills the viewport (fixes 100vh, centering, and background issues)
                        if (content is Control contentControl)
                        {
                            // Function to update content minimum size to match viewport
                            void UpdateContentSize()
                            {
                                // Post to UI thread to ensure Viewport is updated after layout
                                Dispatcher.UIThread.Post(() => {
                                    var viewport = sv.Viewport;
                                    // Fallback to Bounds if Viewport is not yet valid
                                    if (viewport.Width == 0 || viewport.Height == 0) viewport = sv.Bounds.Size;

                                    if (viewport.Height > 0) contentControl.MinHeight = viewport.Height;
                                    if (viewport.Width > 0) contentControl.MinWidth = viewport.Width;
                                });
                            }

                            // Initial update
                            if (sv.Viewport.Height <= 0) contentControl.MinHeight = 100; // Ensure at least one render pass happens
                            UpdateContentSize();

                            // Update on ScrollViewer resize (window resize/maximize)
                            sv.SizeChanged += (s, e) => UpdateContentSize();
                            
                            // Update when Viewport changes (e.g. scrollbars appear/disappear)
                            sv.GetObservable(ScrollViewer.ViewportProperty).Subscribe(new Avalonia.Reactive.AnonymousObserver<Size>(_ => UpdateContentSize()));
                        }
                        else if (content is FenBrowser.FenEngine.Rendering.SkiaBrowserView skiaView)
                        {
                            // This path is likely unused if SkiaView is in XAML, but kept for dynamic injection support
                            // Logic moved to BindSkiaViewport() for XAML instance, but good to have here too as backup
                        }
                        decorator.Child = sv;
                    }
                    break;

                case Panel panel:
                    panel.Children.Clear();
                    panel.Children.Add(content);
                    break;

                default:
                    // As a last resort, if BrowserContainer itself is a Control that can host a child via DataContext,
                    // set DataContext so templates can react. This is a non-destructive fallback.
                    _browserContainer.DataContext = content;
                    break;
            }
            
            // Attach Context Menu to the content control if possible
            if (content is Control c)
            {
                c.ContextMenu = CreateBrowserContextMenu();
            }
        }
        
        /// <summary>
        /// Show a special page (Settings, etc.) and hide SkiaView
        /// </summary>
        private void ShowSpecialPage(Control content)
        {
            if (_skiaScrollViewer != null)
                _skiaScrollViewer.IsVisible = false;
            
            if (_specialPageContainer != null)
            {
                // Detach content from previous parent if any
                if (content.Parent is ContentControl oldCc)
                    oldCc.Content = null;
                    
                _specialPageContainer.Content = content;
                _specialPageContainer.IsVisible = true;
            }
        }
        
        /// <summary>
        /// Show SkiaView (web content) and hide special page container
        /// </summary>
        private void ShowWebContent()
        {
            if (_specialPageContainer != null)
            {
                _specialPageContainer.Content = null;
                _specialPageContainer.IsVisible = false;
            }
            
            if (_skiaScrollViewer != null)
                _skiaScrollViewer.IsVisible = true;
        }

        private ContextMenu CreateBrowserContextMenu()
        {
            var menu = new ContextMenu();
            
            // Clipboard operations
            var copy = new MenuItem { Header = "Copy", InputGesture = new KeyGesture(Key.C, KeyModifiers.Control) };
            copy.Click += async (s, e) => 
            {
                try
                {
                    var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
                    if (clipboard != null)
                    {
                        var focused = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement();
                        if (focused is TextBox tb && !string.IsNullOrEmpty(tb.SelectedText))
                        {
                            await clipboard.SetTextAsync(tb.SelectedText);
                        }
                        else if (_activeBrowser?.CurrentUri != null)
                        {
                            await clipboard.SetTextAsync(_activeBrowser.CurrentUri.AbsoluteUri);
                        }
                    }
                }
                catch { }
            };
            menu.Items.Add(copy);
            
            var cut = new MenuItem { Header = "Cut", InputGesture = new KeyGesture(Key.X, KeyModifiers.Control) };
            cut.Click += async (s, e) => 
            {
                try
                {
                    var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
                    var focused = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement();
                    if (clipboard != null && focused is TextBox tb && !string.IsNullOrEmpty(tb.SelectedText))
                    {
                        await clipboard.SetTextAsync(tb.SelectedText);
                        var start = tb.SelectionStart;
                        tb.Text = tb.Text.Remove(start, tb.SelectedText.Length);
                        tb.CaretIndex = start;
                    }
                }
                catch { }
            };
            menu.Items.Add(cut);
            
            var paste = new MenuItem { Header = "Paste", InputGesture = new KeyGesture(Key.V, KeyModifiers.Control) };
            paste.Click += async (s, e) => 
            {
                try
                {
                    var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
                    var focused = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement();
                    if (clipboard != null && focused is TextBox tb)
                    {
                        var text = await clipboard.GetTextAsync();
                        if (!string.IsNullOrEmpty(text))
                        {
                            var start = tb.SelectionStart;
                            var newText = tb.Text ?? "";
                            if (tb.SelectionEnd > tb.SelectionStart)
                            {
                                newText = newText.Remove(start, tb.SelectionEnd - start);
                            }
                            tb.Text = newText.Insert(start, text);
                            tb.CaretIndex = start + text.Length;
                        }
                    }
                }
                catch { }
            };
            menu.Items.Add(paste);
            
            var selectAll = new MenuItem { Header = "Select All", InputGesture = new KeyGesture(Key.A, KeyModifiers.Control) };
            selectAll.Click += (s, e) => 
            {
                try
                {
                    var focused = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement();
                    if (focused is TextBox tb)
                    {
                        tb.SelectAll();
                    }
                }
                catch { }
            };
            menu.Items.Add(selectAll);
            
            menu.Items.Add(new Separator());
            
            var refresh = new MenuItem { Header = "Refresh", InputGesture = new KeyGesture(Key.R, KeyModifiers.Control) };
            refresh.Click += (s, e) => Refresh_Click(s, e);
            menu.Items.Add(refresh);
            
            menu.Items.Add(new Separator());
            
            var viewSource = new MenuItem { Header = "View page source", InputGesture = new KeyGesture(Key.U, KeyModifiers.Control) };
            viewSource.Click += (s, e) => 
            {
                if (_activeBrowser?.CurrentUri != null)
                {
                    var url = "view-source:" + _activeBrowser.CurrentUri.AbsoluteUri;
                    OnNewTabClick(this, null);
                    if (_activeBrowser != null) _activeBrowser.NavigateAsync(url);
                }
            };
            menu.Items.Add(viewSource);
            
            var inspect = new MenuItem { Header = "Inspect" };
            inspect.Click += (s, e) => 
            {
                if (_activeBrowser != null)
                {
                    var activeTab = Tabs.FirstOrDefault(t => t.IsActive);
                    if (activeTab != null && !activeTab.IsDevToolsOpen)
                    {
                        ToggleDevTools();
                    }
                }
            };
            menu.Items.Add(inspect);

            return menu;
            }


        private void RecalculateTabWidth()
        {
            if (Tabs.Count == 0) return;

            double availableWidth = this.Bounds.Width - 250;

            if (availableWidth <= 0) return;

            double newWidth = availableWidth / Tabs.Count;
            newWidth = System.Math.Clamp(newWidth, 60, 200);

            CurrentTabWidth = newWidth;
        }

        // Navigation handlers (wire to your engine)
        private async void Back_Click(object sender, RoutedEventArgs e)
        {
            if (_activeBrowser != null && _activeBrowser.CanGoBack)
            {
                await _activeBrowser.GoBackAsync();
            }
        }

        private async void Forward_Click(object sender, RoutedEventArgs e)
        {
            if (_activeBrowser != null && _activeBrowser.CanGoForward)
            {
                await _activeBrowser.GoForwardAsync();
            }
        }
        private void Refresh_Click(object sender, RoutedEventArgs e)
        {
            if (_activeBrowser != null && _activeBrowser.CurrentUri != null)
            {
                _ = _activeBrowser.NavigateAsync(_activeBrowser.CurrentUri.AbsoluteUri);
            }
        }
        
        private async void Home_Click(object sender, RoutedEventArgs e)
        {
            // Navigate to home page (configurable, default to about:blank or a start page)
            var homePage = BrowserSettings.Instance.HomePage ?? "about:blank";
            if (_activeBrowser != null)
            {
                await _activeBrowser.NavigateAsync(homePage);
            }
        }
        
        private void OnBookmarkClick(object sender, RoutedEventArgs e)
        {
            // Toggle bookmark for current page
            if (_activeBrowser?.CurrentUri == null) return;
            
            var url = _activeBrowser.CurrentUri.AbsoluteUri;
            var title = Tabs.FirstOrDefault(t => t.IsActive)?.Title ?? "Untitled";
            
            // Check if already bookmarked
            var bookmarkIcon = this.FindControl<Avalonia.Controls.Shapes.Path>("BookmarkIconPath");
            bool isBookmarked = BookmarkManager.Instance.IsBookmarked(url);
            
            if (isBookmarked)
            {
                BookmarkManager.Instance.RemoveBookmark(url);
                if (bookmarkIcon != null)
                {
                    bookmarkIcon.Fill = (Avalonia.Media.IBrush)this.FindResource("MutedTextBrush");
                }
            }
            else
            {
                BookmarkManager.Instance.AddBookmark(url, title);
                if (bookmarkIcon != null)
                {
                    bookmarkIcon.Fill = Avalonia.Media.Brushes.Gold;
                }
            }
        }
        
        private double _currentZoom = 1.0;
        
        private void ZoomIn_Click(object sender, RoutedEventArgs e)
        {
            _currentZoom = Math.Min(3.0, _currentZoom + 0.1);
            ApplyZoom();
        }
        
        private void ZoomOut_Click(object sender, RoutedEventArgs e)
        {
            _currentZoom = Math.Max(0.25, _currentZoom - 0.1);
            ApplyZoom();
        }
        
        private void ApplyZoom()
        {
            var percentage = this.FindControl<TextBlock>("ZoomPercentage");
            if (percentage != null)
            {
                percentage.Text = $"{(int)(_currentZoom * 100)}%";
            }
            
            // Apply zoom to SkiaView
            var skiaView = this.FindControl<SkiaBrowserView>("SkiaView");
            if (skiaView != null)
            {
                skiaView.ZoomLevel = (float)_currentZoom;
                skiaView.InvalidateVisual();
            }
        }

        private async void OnGoClick(object sender, RoutedEventArgs e)
        {
            var url = AddressBox?.Text?.Trim();
            if (!string.IsNullOrEmpty(url))
            {
                // Update active tab title immediately
                var activeTab = Tabs.FirstOrDefault(t => t.IsActive);
                if (activeTab != null)
                {
                    activeTab.Title = "Loading...";
                }

                SetBrowserContainerContent(new TextBlock
                {
                    Text = $"Navigating to: {url}...",
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
                });

                if (_activeBrowser != null)
                    await _activeBrowser.NavigateAsync(url);
            }
        }

        private void AddressBox_KeyDown(object sender, Avalonia.Input.KeyEventArgs e)
        {
            if (e.Key == Avalonia.Input.Key.Enter)
            {
                OnGoClick(sender, e);
            }
        }

        private void OnSiteInfoClick(object sender, RoutedEventArgs e)
        {
            var popup = this.FindControl<Popup>("SiteInfoFlyout");
            var content = this.FindControl<SiteInfoPopup>("SiteInfoContent");
            
            if (popup != null && content != null)
            {
                if (popup.IsOpen)
                {
                    popup.IsOpen = false;
                    content.Detach();
                }
                else
                {
                    // Configure with current site info
                    if (_activeBrowser != null && _activeBrowser.CurrentUri != null)
                    {
                        var scheme = _activeBrowser.CurrentUri.Scheme.ToLowerInvariant();
                        if (scheme != "http" && scheme != "https")
                        {
                            // Only show for web pages
                            return;
                        }

                        content.Configure(_activeBrowser.CurrentUri.Host, _activeBrowser.ResourceManager, _activeBrowser.CurrentCertificate);
                        
                        // Handle close request from the popup itself
                        EventHandler closeHandler = null;
                        closeHandler = (s, args) =>
                        {
                            popup.IsOpen = false;
                            content.Detach();
                            content.CloseRequested -= closeHandler;
                        };
                        content.CloseRequested += closeHandler;
                        
                        // Also detach when popup closes via light dismiss
                        EventHandler<EventArgs> popupClosedHandler = null;
                        popupClosedHandler = (s, args) =>
                        {
                            content.Detach();
                            if (closeHandler != null) content.CloseRequested -= closeHandler;
                            popup.Closed -= popupClosedHandler;
                        };
                        popup.Closed += popupClosedHandler;

                        popup.IsOpen = true;
                    }
                }
            }
        }

        private void OnDevToolsClick(object sender, RoutedEventArgs e)
        {
            ToggleDevTools();
        }

        private void ToggleDevTools()
        {
            var activeTab = Tabs.FirstOrDefault(t => t.IsActive);
            if (activeTab != null)
            {
                activeTab.IsDevToolsOpen = !activeTab.IsDevToolsOpen;
                UpdateDevToolsVisibility(activeTab);
            }
        }

        private void UpdateDevToolsVisibility(TabItemModel tab)
        {
            if (_devToolsContainer == null) return;

            if (tab.IsDevToolsOpen)
            {
                if (tab.DevToolsInstance == null)
                {
                    tab.DevToolsInstance = new DevToolsView();
                    tab.DevToolsInstance.CloseRequested += (s, e) => 
                    {
                        tab.IsDevToolsOpen = false;
                        UpdateDevToolsVisibility(tab);
                    };
                }
                
                if (tab.Browser != null)
                {
                    tab.DevToolsInstance.Attach(tab.Browser);
                }

                _devToolsContainer.Child = tab.DevToolsInstance;
                _devToolsContainer.IsVisible = true;
                _devToolsContainer.Height = 300; // Default height
            }
            else
            {
                if (tab.DevToolsInstance != null)
                {
                    tab.DevToolsInstance.Detach();
                }
                _devToolsContainer.Child = null;
                _devToolsContainer.IsVisible = false;
                _devToolsContainer.Height = 0;
            }
        }

        private void OnSettingsClick(object sender, RoutedEventArgs e)
        {
            try
            {
                // Check if settings tab already exists
                var existingSettingsTab = Tabs.FirstOrDefault(t => t.Title == "Settings");
                if (existingSettingsTab != null)
                {
                    // Switch to existing settings tab
                    foreach (var t in Tabs) t.IsActive = t.Id == existingSettingsTab.Id;
                    if (existingSettingsTab.LastRenderedContent is Control c)
                        ShowSpecialPage(c);
                    if (AddressBox != null) AddressBox.Text = "fen://settings";
                    return;
                }

                // Create new settings tab
                foreach (var t in Tabs) t.IsActive = false;
                var settingsTab = new TabItemModel { Id = _nextTabId++, Title = "Settings", IsActive = true };
                Tabs.Add(settingsTab);
                
                var settingsPage = new SettingsPage();
                settingsPage.CloseRequested += (s, args) =>
                {
                    // Close the settings tab when close is requested
                    OnCloseTab(settingsTab);
                };
                
                // Attach the active browser to allow cookie management
                settingsPage.AttachBrowser(_activeBrowser);

                // Store settings page as the tab's content
                settingsTab.LastRenderedContent = settingsPage;
                ShowSpecialPage(settingsPage);

                if (AddressBox != null)
                    AddressBox.Text = "fen://settings";
            }
            catch (Exception ex)
            {
                System.IO.File.AppendAllText("crash_log_new.txt", $"[OnSettingsClick] Error: {ex}\r\n");
            }
        }

        private void OnMenuClick(object sender, RoutedEventArgs e)
        {
            if (sender is not Button button) return;

            var menu = new MenuFlyout();

            var newTab = new MenuItem { Header = "New tab" };
            newTab.Click += (s, args) => { /* TODO: Implement tab support */ };
            menu.Items.Add(newTab);

            var newWindow = new MenuItem { Header = "New window" };
            newWindow.Click += (s, args) => 
            {
                 var win = new MainWindow();
                 win.Show();
            };
            menu.Items.Add(newWindow);

            var newPrivateWindow = new MenuItem { Header = "New private window" };
            newPrivateWindow.Click += (s, args) => 
            {
                 var win = new MainWindow(null, null, true);
                 win.Show();
            };
            menu.Items.Add(newPrivateWindow);

            menu.Items.Add(new Separator());

            var settings = new MenuItem { Header = "Settings" };
            settings.Click += (s, args) => OnSettingsClick(s, args);
            menu.Items.Add(settings);

            var help = new MenuItem { Header = "Help and About" };
            help.Click += (s, args) => { /* TODO: Show about dialog */ };
            menu.Items.Add(help);

            menu.Items.Add(new Separator());

            var close = new MenuItem { Header = "Close FenBrowser" };
            close.Click += (s, args) => Close();
            menu.Items.Add(close);

            menu.ShowAt(button);
        }

        private void OnExtensionClick(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.DataContext is ExtensionModel ext)
            {
                // Handle extension click
            }
        }

        private void OnNewTabClick(object sender, RoutedEventArgs e)
        {
            try
            {
                foreach (var t in Tabs) t.IsActive = false;
                var newTab = new TabItemModel { Id = _nextTabId++, Title = "New Tab", IsActive = true };
                Tabs.Add(newTab);
                CreateBrowserForTab(newTab);
                _activeBrowser = newTab.Browser;
                
                // Clear UI for new tab or show start page
                SetBrowserContainerContent(new TextBlock { Text = "New Tab", HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center });
                if (_addressBox != null) _addressBox.Text = "";
                
                UpdateAddressBar(_activeBrowser);
            }
            catch (Exception ex)
            {
                System.IO.File.AppendAllText("crash_log_new.txt", $"[OnNewTabClick] Error: {ex}\r\n");
            }
        }

        private void OnTabClicked(object sender, RoutedEventArgs e)
        {
            if (sender is ToggleButton tb && tb.DataContext is TabItemModel model)
            {
                foreach (var t in Tabs) t.IsActive = t.Id == model.Id;
                
                if (model.IsActive)
                {
                    // Only update _activeBrowser if the tab has a browser (not for special pages like settings)
                    if (model.Browser != null)
                    {
                        _activeBrowser = model.Browser;
                    }
                    
                    // Handle special pages vs web content
                    if (model.Title == "Settings" || model.LastRenderedContent is SettingsPage)
                    {
                        if (model.LastRenderedContent is Control c)
                            ShowSpecialPage(c);
                        if (AddressBox != null) AddressBox.Text = "fen://settings";
                    }
                    else
                    {
                        ShowWebContent();
                        UpdateAddressBar(_activeBrowser);
                    }
                    
                    UpdateDevToolsVisibility(model);
                }
            }
        }

        private void OnCloseTab(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag != null)
            {
                if (int.TryParse(btn.Tag.ToString(), out int id))
                {
                    var toRemove = Tabs.FirstOrDefault(t => t.Id == id);
                    if (toRemove != null)
                    {
                        var wasActive = toRemove.IsActive;
                        Tabs.Remove(toRemove);

                        if (wasActive && Tabs.Any())
                        {
                            var newActive = Tabs[0];
                            newActive.IsActive = true;
                            _activeBrowser = newActive.Browser;
                            if (newActive.LastRenderedContent is Control c)
                                SetBrowserContainerContent(c);
                            else
                                SetBrowserContainerContent(new TextBlock { Text = "Ready" });
                            UpdateAddressBar(_activeBrowser);
                        }
                        
                        // Dispose the closed browser
                        if (toRemove.Browser is IDisposable d) d.Dispose();
                    }
                }
            }
        }

        // Overload for programmatic tab closing (e.g., settings tab close button)
        private void OnCloseTab(TabItemModel toRemove)
        {
            if (toRemove == null) return;
            
            var wasActive = toRemove.IsActive;
            Tabs.Remove(toRemove);

            if (wasActive && Tabs.Any())
            {
                var newActive = Tabs[0];
                newActive.IsActive = true;
                _activeBrowser = newActive.Browser;
                if (newActive.LastRenderedContent is Control c)
                    SetBrowserContainerContent(c);
                else
                    SetBrowserContainerContent(new TextBlock { Text = "Ready" });
                UpdateAddressBar(_activeBrowser);
            }
            
            // Dispose the closed browser if applicable
            if (toRemove.Browser is IDisposable d) d.Dispose();
        }

        private void OnExtensionsOverflow(object sender, RoutedEventArgs e)
        {
            var menu = new ContextMenu();
            var items = new List<MenuItem>();

            foreach (var ext in Extensions)
            {
                var m = new MenuItem { Header = ext.Name };
                m.Click += (_, __) =>
                {
                    // Handle overflow extension click
                };
                items.Add(m);
            }

            menu.ItemsSource = items;

            if (sender is Button b)
            {
                b.ContextMenu = menu;
                menu.Open(b);
            }
        }

        private void OnMinimizeClick(object sender, RoutedEventArgs e)
        {
            this.WindowState = WindowState.Minimized;
        }

        private void OnMaximizeClick(object sender, RoutedEventArgs e)
        {
            this.WindowState = this.WindowState == WindowState.Maximized
                ? WindowState.Normal
                : WindowState.Maximized;
        }

        private void OnCloseClick(object sender, RoutedEventArgs e)
        {
            this.Close();
        }

        private void OnTitleBarPointerPressed(object sender, Avalonia.Input.PointerPressedEventArgs e)
        {
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            {
                BeginMoveDrag(e);
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);
            _webDriver?.Stop();
            foreach(var t in Tabs)
            {
                if (t.Browser is IDisposable d) d.Dispose();
            }
        }
    } // end MainWindow
}
