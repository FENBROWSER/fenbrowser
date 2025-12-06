using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using FenBrowser.FenEngine.Rendering;
using System.Collections.ObjectModel;
using System.Linq;

namespace FenBrowser.UI
{
    public partial class CookiesPage : UserControl
    {
        private IBrowser _browser;
        public ObservableCollection<WebDriverCookie> Cookies { get; } = new ObservableCollection<WebDriverCookie>();

        public CookiesPage()
        {
            InitializeComponent();
            CookiesList.ItemsSource = Cookies;
            
            // Bind Toggle
            BlockThirdPartyToggle.IsChecked = Core.BrowserSettings.Instance.BlockThirdPartyCookies;
            BlockThirdPartyToggle.IsCheckedChanged += OnBlockThirdPartyChanged;
        }

        private void OnBlockThirdPartyChanged(object sender, RoutedEventArgs e)
        {
            if (BlockThirdPartyToggle.IsChecked.HasValue)
            {
                Core.BrowserSettings.Instance.BlockThirdPartyCookies = BlockThirdPartyToggle.IsChecked.Value;
                Core.BrowserSettings.Instance.Save();
            }
        }

        public void Configure(IBrowser browser)
        {
            _browser = browser;
            LoadCookies();
        }

        private async void LoadCookies()
        {
            if (_browser == null) return;
            var cookies = await _browser.GetAllCookiesAsync();
            Cookies.Clear();
            if (cookies != null)
            {
                foreach (var c in cookies) Cookies.Add(c);
            }
        }

        private void OnRefreshClick(object sender, RoutedEventArgs e)
        {
            LoadCookies();
        }

        private async void OnDeleteCookieClick(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is WebDriverCookie cookie && _browser != null)
            {
                await _browser.DeleteCookieAsync(cookie.Name);
                LoadCookies();
            }
        }

        private void OnClearAllClick(object sender, RoutedEventArgs e)
        {
            _browser?.ClearBrowsingData();
            LoadCookies();
        }
    }
}
