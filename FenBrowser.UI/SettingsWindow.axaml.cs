using Avalonia.Controls;
using Avalonia.Interactivity;
using FenBrowser.Core;
using FenBrowser.Core.Logging;

namespace FenBrowser.UI
{
    public partial class SettingsWindow : Window
    {
        private UserAgentType _originalSelection;

        public SettingsWindow()
        {
            InitializeComponent();
            LoadSettings();
        }

        private void LoadSettings()
        {
            var settings = BrowserSettings.Instance;
            _originalSelection = settings.SelectedUserAgent;

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
        }

        private void OnSaveClick(object sender, RoutedEventArgs e)
        {
            var settings = BrowserSettings.Instance;

            // Read selected radio button
            if (ChromeRadio.IsChecked == true)
                settings.SelectedUserAgent = UserAgentType.Chrome;
            else if (FirefoxRadio.IsChecked == true)
                settings.SelectedUserAgent = UserAgentType.Firefox;
            else if (FenBrowserRadio.IsChecked == true)
                settings.SelectedUserAgent = UserAgentType.FenBrowser;

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

            // Check if User-Agent changed
            bool changed = settings.SelectedUserAgent != _originalSelection;

            // Close window and notify parent
            if (changed)
            {
                ShowMessage("User-Agent settings saved successfully!", "Settings Saved");
           }

            Close();
        }

        private void OnCancelClick(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private async void OnViewLogsClick(object sender, RoutedEventArgs e)
        {
            var logViewerWindow = new LogViewerWindow();
            await logViewerWindow.ShowDialog(this);
        }

        private void OnClearLogsClick(object sender, RoutedEventArgs e)
        {
            var result = ShowConfirmation("Are you sure you want to clear all logs?", "Clear Logs");
            if (result == DialogResult.Yes)
            {
                LogManager.ClearLogs(deleteFile: true);
                ShowMessage("Logs cleared successfully.", "Clear Logs");
            }
        }

        private DialogResult ShowConfirmation(string message, string title)
        {
            var result = DialogResult.No;
            var dialog = new Window
            {
                Title = title,
                Width = 350,
                Height = 150,
                WindowStartupLocation = WindowStartupLocation.CenterOwner
            };

            var stack = new StackPanel { Margin = new Avalonia.Thickness(20), Spacing = 15 };
            stack.Children.Add(new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap });

            var buttonPanel = new StackPanel { 
                Orientation = Avalonia.Layout.Orientation.Horizontal, 
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right, 
                Spacing = 8 
            };

            var yesButton = new Button { Content = "Yes", MinWidth = 70 };
            yesButton.Click += (s, e) => { result = DialogResult.Yes; dialog.Close(); };
            buttonPanel.Children.Add(yesButton);

            var noButton = new Button { Content = "No", MinWidth = 70 };
            noButton.Click += (s, e) => { result = DialogResult.No; dialog.Close(); };
            buttonPanel.Children.Add(noButton);

            stack.Children.Add(buttonPanel);
            dialog.Content = stack;
            dialog.ShowDialog(this);
            return result;
        }

        private void ShowMessage(string message, string title)
        {
            var dialog = new Window
            {
                Title = title,
                Width = 350,
                Height = 150,
                WindowStartupLocation = WindowStartupLocation.CenterOwner
            };

            var stack = new StackPanel { Margin = new Avalonia.Thickness(20), Spacing = 15 };
            stack.Children.Add(new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap });

            var okButton = new Button { Content = "OK", MinWidth = 70, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right };
            okButton.Click += (s, e) => dialog.Close();
            stack.Children.Add(okButton);

            dialog.Content = stack;
            dialog.ShowDialog(this);
        }

        private enum DialogResult { Yes, No }
    }
}
