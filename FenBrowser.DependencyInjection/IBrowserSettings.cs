using FenBrowser.Core;

namespace FenBrowser.DependencyInjection;

/// <summary>
/// Abstraction for browser settings access.
/// </summary>
public interface IBrowserSettings
{
    UserAgentType SelectedUserAgent { get; set; }
    ThemePreference Theme { get; set; }
    bool ShowFavoritesBar { get; set; }
    bool ShowFavoritesButton { get; set; }
    List<Bookmark> Bookmarks { get; set; }
    LogSettings Logging { get; set; }
    ResilienceSettings Resilience { get; set; }
    bool EnableJavaScript { get; set; }
    bool EnableTrackingPrevention { get; set; }
    string HomePage { get; set; }
    string SearchEngine { get; set; }
    string SearchEngineUrl { get; set; }
    double DefaultZoom { get; set; }
    bool RestoreTabsOnStartup { get; set; }
    StartupBehavior StartupAction { get; set; }
    List<string> StartupUrls { get; set; }
    bool ShowHomeButton { get; set; }
    string DownloadPath { get; set; }
    bool AskDownloadLocation { get; set; }
    bool OpenDownloadFolderOnStart { get; set; }
    bool SendDoNotTrack { get; set; }
    bool ClearCookiesOnExit { get; set; }
    bool BlockThirdPartyCookies { get; set; }
    bool UseSecureDNS { get; set; }
    string SecureDnsEndpoint { get; set; }
    bool SafeBrowsing { get; set; }
    bool ImproveBrowser { get; set; }
    bool BlockPopups { get; set; }
    bool AllowFileSchemeNavigation { get; set; }
    bool AllowAutomationFileNavigation { get; set; }
    string FontSize { get; set; }
    bool ShowDeveloperTools { get; set; }
    bool HardwareAcceleration { get; set; }
    bool EnableSleepingTabs { get; set; }
    bool RunInBackground { get; set; }

    void Normalize();
    void ValidateOrThrow();
    void Save();
}