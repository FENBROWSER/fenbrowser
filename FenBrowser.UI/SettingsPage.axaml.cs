using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Input;
using FenBrowser.Core;
using FenBrowser.Core.Logging;
using FenBrowser.FenEngine.Rendering;
using Avalonia.Media;
using System;

namespace FenBrowser.UI
{
    public partial class SettingsPage : UserControl
    {
        public event EventHandler CloseRequested;

        public SettingsPage()
        {
            InitializeComponent();
            LoadSettings();
        }

        public void AttachBrowser(IBrowser browser)
        {
            if (CookiesView != null) CookiesView.Configure(browser);
        }

        private void OnNavGeneralClick(object sender, PointerPressedEventArgs e)
        {
            GeneralPanel.IsVisible = true;
            PrivacyPanel.IsVisible = false;
            NavGeneral.Background = Application.Current.FindResource("HoverColor") as IBrush;
            NavPrivacy.Background = Brushes.Transparent;
        }

        private void OnNavPrivacyClick(object sender, PointerPressedEventArgs e)
        {
            GeneralPanel.IsVisible = false;
            PrivacyPanel.IsVisible = true;
            NavPrivacy.Background = Application.Current.FindResource("HoverColor") as IBrush;
            NavGeneral.Background = Brushes.Transparent;
        }

        private void LoadSettings()
        {
            var settings = BrowserSettings.Instance;

            // Set the appropriate radio button
            switch (settings.SelectedUserAgent)
            {
                case UserAgentType.Chrome:
                    ChromeRadio.IsChecked = true;
                    break;
                case UserAgentType.Firefox:
                    FirefoxRadio.IsChecked = true;
                    break;
                case UserAgentType.FenBrowser:
                    FenBrowserRadio.IsChecked = true;
                    break;
            }

            // Load JavaScript setting
            EnableJavaScriptSwitch.IsChecked = settings.EnableJavaScript;

            // Load logging settings
            EnableLoggingSwitch.IsChecked = settings.Logging.EnableLogging;
            var categories = (LogCategory)settings.Logging.EnabledCategories;
            LogNavigationCheck.IsChecked = categories.HasFlag(LogCategory.Navigation);
            LogRenderingCheck.IsChecked = categories.HasFlag(LogCategory.Rendering);
            LogCSSCheck.IsChecked = categories.HasFlag(LogCategory.CSS);
            LogJavaScriptCheck.IsChecked = categories.HasFlag(LogCategory.JavaScript);
            LogNetworkCheck.IsChecked = categories.HasFlag(LogCategory.Network);
            LogImagesCheck.IsChecked = categories.HasFlag(LogCategory.Images);
            LogLayoutCheck.IsChecked = categories.HasFlag(LogCategory.Layout);
            LogEventsCheck.IsChecked = categories.HasFlag(LogCategory.Events);
            LogStorageCheck.IsChecked = categories.HasFlag(LogCategory.Storage);
            LogPerformanceCheck.IsChecked = categories.HasFlag(LogCategory.Performance);
            LogErrorsCheck.IsChecked = categories.HasFlag(LogCategory.Errors);

            // Load Theme settings
            switch (settings.Theme)
            {
                case ThemePreference.Light:
                    ThemeLightRadio.IsChecked = true;
                    break;
                case ThemePreference.Dark:
                    ThemeDarkRadio.IsChecked = true;
                    break;
                case ThemePreference.System:
                default:
                    ThemeSystemRadio.IsChecked = true;
                    break;
            }
        }

        private async void OnSaveClick(object sender, RoutedEventArgs e)
        {
            var settings = BrowserSettings.Instance;

            // Read selected radio button
            if (ChromeRadio.IsChecked == true)
                settings.SelectedUserAgent = UserAgentType.Chrome;
            else if (FirefoxRadio.IsChecked == true)
                settings.SelectedUserAgent = UserAgentType.Firefox;
            else if (FenBrowserRadio.IsChecked == true)
                settings.SelectedUserAgent = UserAgentType.FenBrowser;

            else if (FenBrowserRadio.IsChecked == true)
                settings.SelectedUserAgent = UserAgentType.FenBrowser;

            // Save Theme setting
            if (ThemeLightRadio.IsChecked == true)
                ThemeManager.SetTheme(ThemePreference.Light);
            else if (ThemeDarkRadio.IsChecked == true)
                ThemeManager.SetTheme(ThemePreference.Dark);
            else
                ThemeManager.SetTheme(ThemePreference.System);

            // Save JavaScript setting
            settings.EnableJavaScript = EnableJavaScriptSwitch.IsChecked == true;

            // Save logging settings
            settings.Logging.EnableLogging = EnableLoggingSwitch.IsChecked == true;
            LogCategory categories = LogCategory.None;
            if (LogNavigationCheck.IsChecked == true) categories |= LogCategory.Navigation;
            if (LogRenderingCheck.IsChecked == true) categories |= LogCategory.Rendering;
            if (LogCSSCheck.IsChecked == true) categories |= LogCategory.CSS;
            if (LogJavaScriptCheck.IsChecked == true) categories |= LogCategory.JavaScript;
            if (LogNetworkCheck.IsChecked == true) categories |= LogCategory.Network;
            if (LogImagesCheck.IsChecked == true) categories |= LogCategory.Images;
            if (LogLayoutCheck.IsChecked == true) categories |= LogCategory.Layout;
            if (LogEventsCheck.IsChecked == true) categories |= LogCategory.Events;
            if (LogStorageCheck.IsChecked == true) categories |= LogCategory.Storage;
            if (LogPerformanceCheck.IsChecked == true) categories |= LogCategory.Performance;
            if (LogErrorsCheck.IsChecked == true) categories |= LogCategory.Errors;
            settings.Logging.EnabledCategories = (int)categories;

            // Initialize LogManager with new settings
            LogManager.Initialize(settings.Logging.EnableLogging, categories, (FenBrowser.Core.Logging.LogLevel)settings.Logging.MinimumLevel);

            // Save to file
            settings.Save();

            // Show notification
            NotificationPopup.IsVisible = true;

            // Wait for user to see it
            await System.Threading.Tasks.Task.Delay(1500);

            // Hide notification
            NotificationPopup.IsVisible = false;
        }

        private void OnCloseClick(object sender, RoutedEventArgs e)
        {
            CloseRequested?.Invoke(this, EventArgs.Empty);
        }

        private void OnViewLogsClick(object sender, RoutedEventArgs e)
        {
            // TODO: Implement log viewer in inline mode
        }

        private void OnClearLogsClick(object sender, RoutedEventArgs e)
        {
            LogManager.ClearLogs(deleteFile: true);
        }
    }
}
