using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using FenBrowser.Core;
using System;

namespace FenBrowser.UI
{
    public partial class SiteInfoPopup : UserControl
    {
        public event EventHandler CloseRequested;
        private ResourceManager _resourceManager;

        public SiteInfoPopup()
        {
            InitializeComponent();
            
            // Initial state
            var toggle = this.FindControl<ToggleSwitch>("TrackingToggle");
            if (toggle != null)
            {
                toggle.IsChecked = BrowserSettings.Instance.EnableTrackingPrevention;
            }
        }

        public void Configure(string host, ResourceManager resourceManager)
        {
            var header = this.FindControl<TextBlock>("HeaderHost");
            if (header != null) header.Text = $"About {host}";

            _resourceManager = resourceManager;
            if (_resourceManager != null)
            {
                UpdateBlockedCount(_resourceManager.BlockedRequestCount);
                _resourceManager.BlockedCountChanged += OnBlockedCountChanged;
            }
        }

        public void Detach()
        {
            if (_resourceManager != null)
            {
                _resourceManager.BlockedCountChanged -= OnBlockedCountChanged;
                _resourceManager = null;
            }
        }

        private void OnBlockedCountChanged(object sender, int count)
        {
            Dispatcher.UIThread.Post(() => UpdateBlockedCount(count));
        }

        private void UpdateBlockedCount(int count)
        {
            var txt = this.FindControl<TextBlock>("BlockedCountText");
            if (txt != null)
            {
                txt.Text = $"{count} blocked";
            }
        }

        private void OnCloseClick(object sender, RoutedEventArgs e)
        {
            CloseRequested?.Invoke(this, EventArgs.Empty);
        }

        private void OnTrackingToggled(object sender, RoutedEventArgs e)
        {
            if (sender is ToggleSwitch ts)
            {
                BrowserSettings.Instance.EnableTrackingPrevention = ts.IsChecked ?? true;
                BrowserSettings.Instance.Save();
                
                // Ideally we should reload the page to apply changes, 
                // but for now we just save the setting.
            }
        }
    }
}
