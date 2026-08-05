using FenBrowser.Core;
using FenBrowser.Core.Logging;

namespace FenBrowser.DependencyInjection;

/// <summary>
/// Adapter that wraps the existing BrowserSettings singleton to implement IBrowserSettings.
/// </summary>
public sealed class BrowserSettingsAdapter : IBrowserSettings
{
    private readonly BrowserSettings _settings;

    public BrowserSettingsAdapter(BrowserSettings settings)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    public UserAgentType SelectedUserAgent { get => _settings.SelectedUserAgent; set => _settings.SelectedUserAgent = value; }
    public ThemePreference Theme { get => _settings.Theme; set => _settings.Theme = value; }
    public bool ShowFavoritesBar { get => _settings.ShowFavoritesBar; set => _settings.ShowFavoritesBar = value; }
    public bool ShowFavoritesButton { get => _settings.ShowFavoritesButton; set => _settings.ShowFavoritesButton = value; }
    public List<Bookmark> Bookmarks { get => _settings.Bookmarks; set => _settings.Bookmarks = value; }
    public LogSettings Logging { get => _settings.Logging; set => _settings.Logging = value; }
    public ResilienceSettings Resilience { get => _settings.Resilience; set => _settings.Resilience = value; }
    public bool EnableJavaScript { get => _settings.EnableJavaScript; set => _settings.EnableJavaScript = value; }
    public bool EnableTrackingPrevention { get => _settings.EnableTrackingPrevention; set => _settings.EnableTrackingPrevention = value; }
    public string HomePage { get => _settings.HomePage; set => _settings.HomePage = value; }
    public string SearchEngine { get => _settings.SearchEngine; set => _settings.SearchEngine = value; }
    public string SearchEngineUrl { get => _settings.SearchEngineUrl; set => _settings.SearchEngineUrl = value; }
    public double DefaultZoom { get => _settings.DefaultZoom; set => _settings.DefaultZoom = value; }
    public bool RestoreTabsOnStartup { get => _settings.RestoreTabsOnStartup; set => _settings.RestoreTabsOnStartup = value; }
    public StartupBehavior StartupAction { get => _settings.StartupAction; set => _settings.StartupAction = value; }
    public List<string> StartupUrls { get => _settings.StartupUrls; set => _settings.StartupUrls = value; }
    public bool ShowHomeButton { get => _settings.ShowHomeButton; set => _settings.ShowHomeButton = value; }
    public string DownloadPath { get => _settings.DownloadPath; set => _settings.DownloadPath = value; }
    public bool AskDownloadLocation { get => _settings.AskDownloadLocation; set => _settings.AskDownloadLocation = value; }
    public bool OpenDownloadFolderOnStart { get => _settings.OpenDownloadFolderOnStart; set => _settings.OpenDownloadFolderOnStart = value; }
    public bool SendDoNotTrack { get => _settings.SendDoNotTrack; set => _settings.SendDoNotTrack = value; }
    public bool ClearCookiesOnExit { get => _settings.ClearCookiesOnExit; set => _settings.ClearCookiesOnExit = value; }
    public bool BlockThirdPartyCookies { get => _settings.BlockThirdPartyCookies; set => _settings.BlockThirdPartyCookies = value; }
    public bool UseSecureDNS { get => _settings.UseSecureDNS; set => _settings.UseSecureDNS = value; }
    public string SecureDnsEndpoint { get => _settings.SecureDnsEndpoint; set => _settings.SecureDnsEndpoint = value; }
    public bool SafeBrowsing { get => _settings.SafeBrowsing; set => _settings.SafeBrowsing = value; }
    public bool ImproveBrowser { get => _settings.ImproveBrowser; set => _settings.ImproveBrowser = value; }
    public bool BlockPopups { get => _settings.BlockPopups; set => _settings.BlockPopups = value; }
    public bool AllowFileSchemeNavigation { get => _settings.AllowFileSchemeNavigation; set => _settings.AllowFileSchemeNavigation = value; }
    public bool AllowAutomationFileNavigation { get => _settings.AllowAutomationFileNavigation; set => _settings.AllowAutomationFileNavigation = value; }
    public string FontSize { get => _settings.FontSize; set => _settings.FontSize = value; }
    public bool ShowDeveloperTools { get => _settings.ShowDeveloperTools; set => _settings.ShowDeveloperTools = value; }
    public bool HardwareAcceleration { get => _settings.HardwareAcceleration; set => _settings.HardwareAcceleration = value; }
    public bool EnableSleepingTabs { get => _settings.EnableSleepingTabs; set => _settings.EnableSleepingTabs = value; }
    public bool RunInBackground { get => _settings.RunInBackground; set => _settings.RunInBackground = value; }

    public void Normalize() => _settings.Normalize();
    public void ValidateOrThrow() => _settings.ValidateOrThrow();
    public void Save() => _settings.Save();
}