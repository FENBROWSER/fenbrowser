using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Threading;
using FenBrowser.FenEngine.Rendering;
using FenBrowser.WebDriver;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

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
        public int Id { get; set; }
        public string Title { get; set; }

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
    } // end TabItemModel

    public partial class MainWindow : Window
    {
        private int _nextTabId = 1;
        private BrowserHost _browser;
        private WebDriverServer _webDriver;

        // Note: leave these null if your XAML has x:Name and source-gen will produce them.
        // You can uncomment the FindControl lines in ctor to locate by name at runtime.
        private ItemsControl _extensionsArea;
        private ItemsControl _tabsStrip;
        private Control _browserContainer; // keep generic because your XAML may use Border, ContentControl, Panel, etc.
        private TextBox _addressBox;
        private ProgressBar _loadingBar;

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

        public MainWindow()
        {
            InitializeComponent();
            DataContext = this; // Set DataContext for bindings

            // Wire up controls manually since source generator isn't binding to private fields
            _extensionsArea = this.FindControl<ItemsControl>("ExtensionsArea");
            _tabsStrip = this.FindControl<ItemsControl>("TabsStrip");
            _browserContainer = this.FindControl<Border>("BrowserContainer");
            _addressBox = this.FindControl<TextBox>("AddressBox");
            _loadingBar = this.FindControl<ProgressBar>("LoadingBar");

            // initial data
            Tabs.Add(new TabItemModel { Id = _nextTabId++, Title = "New tab", IsActive = true });

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

            InitializeBrowser();
        }

        private void InitializeBrowser()
        {
            _browser = new BrowserHost();
            try { _webDriver = new WebDriverServer(_browser); _webDriver.Start(); } catch { }

            _browser.RepaintReady += (s, element) =>
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (_browserContainer != null)
                    {
                        // element is typed as 'object' by the engine; try casting to Avalonia Control
                        if (element is Avalonia.Controls.Control ctrl)
                        {
                            SetBrowserContainerContent(ctrl);
                        }
                        else
                        {
                            SetBrowserContainerContent(new TextBlock { Text = "Unsupported visual element" });
                        }
                        // Fix: Get the current URI from the browser
                        var uri = _browser?.CurrentUri;
                        if (_addressBox != null && uri != null)
                        {
                            _addressBox.Text = uri.AbsoluteUri;

                            // Update active tab title
                            var activeTab = Tabs.FirstOrDefault(t => t.IsActive);
                            if (activeTab != null)
                            {
                                // Try to get document title, fallback to hostname
                                var title = uri.Host;
                                if (string.IsNullOrEmpty(title))
                                    title = "New tab";
                                activeTab.Title = title;
                            }
                        }
                    }
                });
            };

            _browser.NavigationFailed += (s, msg) =>
            {
                Dispatcher.UIThread.Post(() =>
                {
                    SetBrowserContainerContent(new TextBlock
                    {
                        Text = $"Navigation Failed: {msg}",
                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                        Foreground = Avalonia.Media.Brushes.Red
                    });

                    if (_loadingBar != null) _loadingBar.IsVisible = false;
                });
            };

            _browser.LoadingChanged += (s, isLoading) =>
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (_loadingBar != null)
                    {
                        _loadingBar.IsVisible = isLoading;
                    }
                });
            };

            _browser.TitleChanged += (s, title) =>
            {
                Dispatcher.UIThread.Post(() =>
                {
                    var activeTab = Tabs.FirstOrDefault(t => t.IsActive);
                    if (activeTab != null && !string.IsNullOrWhiteSpace(title))
                    {
                        activeTab.Title = title;
                    }
                });
            };
        }


        /// <summary>
        /// Put a control inside BrowserContainer regardless of whether it's a ContentControl, Border/Decorator, Panel, etc.
        /// </summary>
        private void SetBrowserContainerContent(Control content)
        {
            if (_browserContainer == null) return;

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
                        decorator.Child = new ScrollViewer
                        {
                            Content = content,
                            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
                            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto
                        };
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
            if (_browser != null && _browser.CanGoBack)
            {
                await _browser.GoBackAsync();
            }
        }

        private async void Forward_Click(object sender, RoutedEventArgs e)
        {
            if (_browser != null && _browser.CanGoForward)
            {
                await _browser.GoForwardAsync();
            }
        }
        private void Refresh_Click(object sender, RoutedEventArgs e)
        {
            if (_browser != null && _browser.CurrentUri != null)
            {
                _ = _browser.NavigateAsync(_browser.CurrentUri.AbsoluteUri);
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

                await _browser.NavigateAsync(url);
            }
        }

        private void AddressBox_KeyDown(object sender, Avalonia.Input.KeyEventArgs e)
        {
            if (e.Key == Avalonia.Input.Key.Enter)
            {
                OnGoClick(sender, e);
            }
        }

        private void OnSettingsClick(object sender, RoutedEventArgs e)
        {
            var settingsPage = new SettingsPage();
            settingsPage.CloseRequested += (s, args) =>
            {
                // Restore browser content when settings is closed
                // If you want last rendered content retained, adjust logic accordingly.
                if (_browserContainer != null)
                    SetBrowserContainerContent(new TextBlock { Text = "Ready" });
            };

            SetBrowserContainerContent(settingsPage);

            if (AddressBox != null)
                AddressBox.Text = "fen://settings";
        }

        private void OnMenuClick(object sender, RoutedEventArgs e)
        {
            if (sender is not Button button) return;

            var menu = new MenuFlyout();

            var newTab = new MenuItem { Header = "New tab" };
            newTab.Click += (s, args) => { /* TODO: Implement tab support */ };
            menu.Items.Add(newTab);

            var newWindow = new MenuItem { Header = "New window" };
            newWindow.Click += (s, args) => { /* TODO: Implement new window */ };
            menu.Items.Add(newWindow);

            var newPrivate = new MenuItem { Header = "New InPrivate window" };
            newPrivate.Click += (s, args) => { /* TODO: Implement private browsing */ };
            menu.Items.Add(newPrivate);

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
            foreach (var t in Tabs) t.IsActive = false;
            Tabs.Add(new TabItemModel { Id = _nextTabId++, Title = "New Tab", IsActive = true });
        }

        private void OnTabClicked(object sender, RoutedEventArgs e)
        {
            if (sender is ToggleButton tb && tb.DataContext is TabItemModel model)
            {
                foreach (var t in Tabs) t.IsActive = t.Id == model.Id;
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
                            Tabs[0].IsActive = true;
                        }
                    }
                }
            }
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
            _browser?.Dispose();
        }
    } // end MainWindow
}
