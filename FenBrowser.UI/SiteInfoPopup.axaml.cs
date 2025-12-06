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

        public void Configure(string host, ResourceManager resourceManager, CertificateInfo certInfo)
        {
            var header = this.FindControl<TextBlock>("HeaderHost");
            if (header != null) header.Text = $"About {host}";

            _resourceManager = resourceManager;
            if (_resourceManager != null)
            {
                UpdateBlockedCount(_resourceManager.BlockedRequestCount);
                _resourceManager.BlockedCountChanged += OnBlockedCountChanged;
            }

            // Bind Certificate Info
            var txtIssuer = this.FindControl<TextBlock>("CertIssuer");
            var txtSubject = this.FindControl<TextBlock>("CertSubject");
            var txtDates = this.FindControl<TextBlock>("CertDates");
            var txtThumb = this.FindControl<TextBlock>("CertThumb");
            var statusText = this.FindControl<TextBlock>("SecurityStatusText");

            if (certInfo != null && certInfo.IsValid)
            {
                if (txtIssuer != null) txtIssuer.Text = $"Issuer: {certInfo.Issuer}";
                if (txtSubject != null) txtSubject.Text = $"Subject: {certInfo.Subject}";
                if (txtDates != null) txtDates.Text = $"Valid: {certInfo.NotBefore.ToShortDateString()} - {certInfo.NotAfter.ToShortDateString()}";
                if (txtThumb != null) txtThumb.Text = $"Thumbprint: {certInfo.Thumbprint}";
                if (statusText != null) statusText.Text = "Connection is secure";
                if (statusText != null) statusText.Foreground = Avalonia.Media.Brushes.Green;
            }
            else
            {
                if (txtIssuer != null) txtIssuer.Text = "No certificate information available";
                if (txtSubject != null) txtSubject.Text = "";
                if (txtDates != null) txtDates.Text = "";
                if (txtThumb != null) txtThumb.Text = "";
                if (statusText != null) statusText.Text = "Connection is NOT secure";
                if (statusText != null) statusText.Foreground = Avalonia.Media.Brushes.Gray;
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
