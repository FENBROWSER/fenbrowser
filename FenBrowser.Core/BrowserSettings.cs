using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;

namespace FenBrowser.Core
{
    public enum UserAgentType
    {
        FenBrowser,
        Firefox,
        Chrome,
        Edge
    }

    public enum ThemePreference
    {
        System,
        Light,
        Dark
    }

    public enum StartupBehavior
    {
        OpenNewTab,
        RestoreLastSession,
        OpenSpecificPage
    }

    public class Bookmark
    {
        public string Title { get; set; }
        public string Url { get; set; }
    }

    public class LogSettings
    {
        public bool EnableLogging { get; set; } = true;
        public int EnabledCategories { get; set; } = -1;
        public int MinimumLevel { get; set; } = (int)FenBrowser.Core.Logging.LogLevel.Info;
        public bool LogToFile { get; set; } = true;
        public bool LogToDebug { get; set; } = true;
        public int MaxLogFileSizeMB { get; set; } = 10;
        public int MaxArchivedFiles { get; set; } = 10;
        public int MemoryBufferSize { get; set; } = 1000;
        public bool MirrorStructuredLogs { get; set; } = true;

        /// <summary>
        /// Path for log files. Defaults to "logs" folder in the current execution directory.
        /// </summary>
        public string LogPath { get; set; } = GetDefaultLogPath();

        internal void Normalize()
        {
            if (MaxLogFileSizeMB < 1)
                MaxLogFileSizeMB = 10;

            if (MaxArchivedFiles < 1)
                MaxArchivedFiles = 10;

            if (MemoryBufferSize < 1)
                MemoryBufferSize = 1000;

            var legacyBaseDirectoryPath = Path.Combine(AppContext.BaseDirectory, "logs");
            if (string.IsNullOrWhiteSpace(LogPath) ||
                string.Equals(Path.GetFullPath(LogPath), Path.GetFullPath(legacyBaseDirectoryPath), StringComparison.OrdinalIgnoreCase))
            {
                LogPath = GetDefaultLogPath();
            }
        }

        private static string GetDefaultLogPath()
        {
            try
            {
                var cwd = Directory.GetCurrentDirectory();
                if (!string.IsNullOrWhiteSpace(cwd))
                {
                    return Path.Combine(cwd, "logs");
                }
            }
            catch
            {
                // Fall back to AppContext below.
            }

            return Path.Combine(AppContext.BaseDirectory, "logs");
        }
    }

    public class BrowserSettings
    {
        private static BrowserSettings _instance;
        private static readonly object _lock = new();
        private static readonly string _settingsPath;

        static BrowserSettings()
        {
            var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var fenBrowserPath = Path.Combine(appDataPath, "FenBrowser");
            Directory.CreateDirectory(fenBrowserPath);
            _settingsPath = Path.Combine(fenBrowserPath, "settings.json");
        }

        public static BrowserSettings Instance
        {
            get
            {
                if (_instance == null)
                {
                    lock (_lock)
                    {
                        if (_instance == null)
                        {
                            _instance = Load();
                        }
                    }
                }

                return _instance;
            }
        }

        public UserAgentType SelectedUserAgent { get; set; } = UserAgentType.Edge;
        public ThemePreference Theme { get; set; } = ThemePreference.System;

        private bool _showFavoritesBar = true;

        public bool ShowFavoritesBar
        {
            get => _showFavoritesBar;
            set => _showFavoritesBar = value;
        }

        public bool ShowFavoritesButton { get; set; } = true;
        public List<Bookmark> Bookmarks { get; set; } = new();

        public LogSettings Logging { get; set; } = new();
        public bool EnableJavaScript { get; set; } = true;
        public bool EnableTrackingPrevention { get; set; } = true;

        /// <summary>
        /// Home page URL for the Home button.
        /// </summary>
        public string HomePage { get; set; } = "https://example.com/";

        // General Settings
        public string SearchEngine { get; set; } = "Google";
        public string SearchEngineUrl { get; set; } = "https://www.google.com/search?q=";
        public double DefaultZoom { get; set; } = 1.0;
        public bool RestoreTabsOnStartup { get; set; } = false;
        public StartupBehavior StartupAction { get; set; } = StartupBehavior.OpenNewTab;
        public List<string> StartupUrls { get; set; } = new();

        // UI Settings
        public bool ShowHomeButton { get; set; } = true;

        // Downloads Settings
        public string DownloadPath { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        public bool AskDownloadLocation { get; set; } = false;
        public bool OpenDownloadFolderOnStart { get; set; } = false;

        // Privacy Settings
        public bool SendDoNotTrack { get; set; } = true;
        public bool ClearCookiesOnExit { get; set; } = false;
        public bool BlockThirdPartyCookies { get; set; } = false;
        public bool UseSecureDNS { get; set; } = false;
        public string SecureDnsEndpoint { get; set; } = "https://cloudflare-dns.com/dns-query";
        public bool SafeBrowsing { get; set; } = true;
        public bool ImproveBrowser { get; set; } = false;
        public bool BlockPopups { get; set; } = true;
        public bool AllowFileSchemeNavigation { get; set; } = true;
        public bool AllowAutomationFileNavigation { get; set; } = false;

        // Appearance Settings
        public string FontSize { get; set; } = "Medium";

        // Advanced Settings
        public bool ShowDeveloperTools { get; set; } = true;

        // System Settings
        public bool HardwareAcceleration { get; set; } = true;
        public bool EnableSleepingTabs { get; set; } = true;
        public bool RunInBackground { get; set; } = false;

        public static string GetUserAgentString(UserAgentType type, bool useMobile = false)
        {
            return GetBrowserSurface(type, null, useMobile).UserAgent;
        }

        public static string GetClientHints(UserAgentType type, bool useMobile = false)
        {
            return GetBrowserSurface(type, null, useMobile).UserAgentData.ToSecChUaHeader();
        }

        public static string GetSecChUaPlatform(UserAgentType? type = null, bool useMobile = false)
        {
            return QuoteClientHintValue(GetBrowserSurface(type ?? Instance.SelectedUserAgent, null, useMobile).UserAgentData.Platform);
        }

        public static string GetSecChUaPlatformVersion(UserAgentType? type = null, bool useMobile = false)
        {
            return QuoteClientHintValue(GetBrowserSurface(type ?? Instance.SelectedUserAgent, null, useMobile).UserAgentData.PlatformVersion);
        }

        public static string GetSecChUaFullVersion(UserAgentType? type = null, bool useMobile = false)
        {
            var surface = GetBrowserSurface(type ?? Instance.SelectedUserAgent, null, useMobile);
            return QuoteClientHintValue(GetPrimaryBrowserFullVersion(surface.UserAgentData));
        }

        public static string GetSecChUaFullVersionList(UserAgentType? type = null, bool useMobile = false)
        {
            var surface = GetBrowserSurface(type ?? Instance.SelectedUserAgent, null, useMobile);
            return surface.UserAgentData.ToSecChUaHeader(surface.UserAgentData.FullVersionList);
        }

        public static string GetSecChUaArch(UserAgentType? type = null, bool useMobile = false)
        {
            return QuoteClientHintValue(GetBrowserSurface(type ?? Instance.SelectedUserAgent, null, useMobile).UserAgentData.Architecture);
        }

        public static string GetSecChUaBitness(UserAgentType? type = null, bool useMobile = false)
        {
            return QuoteClientHintValue(GetBrowserSurface(type ?? Instance.SelectedUserAgent, null, useMobile).UserAgentData.Bitness);
        }

        public static string GetSecChUaModel(UserAgentType? type = null, bool useMobile = false)
        {
            return QuoteClientHintValue(GetBrowserSurface(type ?? Instance.SelectedUserAgent, null, useMobile).UserAgentData.Model);
        }

        public static BrowserSurfaceProfile GetBrowserSurface(
            UserAgentType type,
            BrowserViewportMetrics metrics = null,
            bool useMobile = false)
        {
            metrics = NormalizeViewportMetrics(metrics);
            return BrowserSurfaceProfileFactory.Create(
                type,
                metrics,
                useMobile,
                CultureInfo.CurrentCulture,
                Instance.SendDoNotTrack);
        }

        private static BrowserViewportMetrics NormalizeViewportMetrics(BrowserViewportMetrics metrics)
        {
            metrics ??= BrowserViewportMetrics.Create(1280, 720);

            return new BrowserViewportMetrics
            {
                WindowWidth = metrics.WindowWidth,
                WindowHeight = metrics.WindowHeight,
                OuterWidth = metrics.OuterWidth,
                OuterHeight = metrics.OuterHeight,
                ScreenWidth = metrics.ScreenWidth,
                ScreenHeight = metrics.ScreenHeight,
                AvailableScreenWidth = metrics.AvailableScreenWidth,
                AvailableScreenHeight = metrics.AvailableScreenHeight,
                DevicePixelRatio = metrics.DevicePixelRatio,
                ScreenX = metrics.ScreenX,
                ScreenY = metrics.ScreenY,
                Hover = metrics.Hover,
                FinePointer = metrics.FinePointer,
                ReducedMotion = metrics.ReducedMotion,
                PreferredColorScheme = ResolvePreferredColorScheme()
            };
        }

        internal static string ResolvePreferredColorScheme()
        {
            return Instance.Theme switch
            {
                ThemePreference.Dark => "dark",
                ThemePreference.Light => "light",
                _ => "light"
            };
        }

        public static void ApplyBrowserRequestHeaders(
            HttpRequestMessage request,
            UserAgentType? type = null,
            bool useMobile = false)
        {
            if (request == null)
            {
                return;
            }

            var selectedUserAgent = type ?? Instance.SelectedUserAgent;
            var surface = GetBrowserSurface(selectedUserAgent, null, useMobile);
            if (!string.IsNullOrWhiteSpace(surface.UserAgent))
            {
                TryAddRequestHeader(request, "User-Agent", surface.UserAgent);
            }

            var hints = surface.UserAgentData.ToSecChUaHeader();
            if (string.IsNullOrWhiteSpace(hints))
            {
                return;
            }

            TryAddRequestHeader(request, "Sec-CH-UA", hints);
            TryAddRequestHeader(request, "Sec-CH-UA-Mobile", useMobile ? "?1" : "?0");
            TryAddRequestHeader(request, "Sec-CH-UA-Platform", QuoteClientHintValue(surface.UserAgentData.Platform));
            TryAddRequestHeader(request, "Sec-CH-UA-Platform-Version", QuoteClientHintValue(surface.UserAgentData.PlatformVersion));
            TryAddRequestHeader(request, "Sec-CH-UA-Full-Version", QuoteClientHintValue(GetPrimaryBrowserFullVersion(surface.UserAgentData)));
            TryAddRequestHeader(request, "Sec-CH-UA-Full-Version-List", surface.UserAgentData.ToSecChUaHeader(surface.UserAgentData.FullVersionList));
            TryAddRequestHeader(request, "Sec-CH-UA-Arch", QuoteClientHintValue(surface.UserAgentData.Architecture));
            TryAddRequestHeader(request, "Sec-CH-UA-Bitness", QuoteClientHintValue(surface.UserAgentData.Bitness));
            TryAddRequestHeader(request, "Sec-CH-UA-Model", QuoteClientHintValue(surface.UserAgentData.Model));
        }

        private static string GetPrimaryBrowserFullVersion(BrowserUserAgentDataProfile userAgentData)
        {
            if (userAgentData?.FullVersionList == null || userAgentData.FullVersionList.Count == 0)
            {
                return string.Empty;
            }

            var preferredBrand = userAgentData.FullVersionList.FirstOrDefault(brand =>
                !IsGreaseBrand(brand?.Brand) &&
                !string.Equals(brand?.Brand, "Chromium", StringComparison.OrdinalIgnoreCase));
            if (preferredBrand != null)
            {
                return preferredBrand.Version ?? string.Empty;
            }

            var nonGreaseBrand = userAgentData.FullVersionList.FirstOrDefault(brand => !IsGreaseBrand(brand?.Brand));
            return nonGreaseBrand?.Version ?? string.Empty;
        }

        private static bool IsGreaseBrand(string brand)
        {
            return string.Equals(brand, " Not;A Brand", StringComparison.Ordinal);
        }

        private static string QuoteClientHintValue(string value)
        {
            return $"\"{value ?? string.Empty}\"";
        }

        private static void TryAddRequestHeader(HttpRequestMessage request, string name, string value)
        {
            if (request.Headers.Contains(name) || string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            request.Headers.TryAddWithoutValidation(name, value);
        }

        public void Normalize()
        {
            Logging ??= new LogSettings();
            Logging.Normalize();

            if (!Enum.IsDefined(typeof(UserAgentType), SelectedUserAgent))
                SelectedUserAgent = UserAgentType.Edge;

            if (!Enum.IsDefined(typeof(ThemePreference), Theme))
                Theme = ThemePreference.System;

            if (!Enum.IsDefined(typeof(StartupBehavior), StartupAction))
                StartupAction = StartupBehavior.OpenNewTab;

            DefaultZoom = Math.Clamp(DefaultZoom, 0.25, 5.0);
            FontSize = NormalizeFontSize(FontSize);
            HomePage = NormalizeAbsoluteUrl(HomePage, "https://example.com/");
            SearchEngineUrl = NormalizeSearchEngineUrl(SearchEngineUrl);

            if (string.IsNullOrWhiteSpace(SearchEngine))
                SearchEngine = "Google";

            if (string.IsNullOrWhiteSpace(DownloadPath))
            {
                DownloadPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            }

            Bookmarks = NormalizeBookmarks(Bookmarks);
            StartupUrls = NormalizeStartupUrls(StartupUrls);
        }

        public void ValidateOrThrow()
        {
            if (!Enum.IsDefined(typeof(UserAgentType), SelectedUserAgent))
                throw new InvalidOperationException($"Unsupported user agent selection: {SelectedUserAgent}");

            if (!Enum.IsDefined(typeof(ThemePreference), Theme))
                throw new InvalidOperationException($"Unsupported theme preference: {Theme}");

            if (!Enum.IsDefined(typeof(StartupBehavior), StartupAction))
                throw new InvalidOperationException($"Unsupported startup behavior: {StartupAction}");

            if (DefaultZoom < 0.25 || DefaultZoom > 5.0)
                throw new InvalidOperationException($"DefaultZoom must stay within [0.25, 5.0]. Actual: {DefaultZoom}");

            if (!IsAllowedFontSize(FontSize))
                throw new InvalidOperationException($"Unsupported FontSize preset: {FontSize}");

            if (Logging == null)
                throw new InvalidOperationException("Logging settings must be configured.");

            if (!TryNormalizeAbsoluteUrl(HomePage, out _, allowBlank: false))
                throw new InvalidOperationException($"HomePage must be an absolute http/https URL. Actual: {HomePage}");

            if (!TryNormalizeSearchEngineUrl(SearchEngineUrl, out _))
                throw new InvalidOperationException($"SearchEngineUrl must be an absolute http/https URL that can accept a query. Actual: {SearchEngineUrl}");

            if (Bookmarks != null && Bookmarks.Any(bookmark => !TryNormalizeAbsoluteUrl(bookmark?.Url, out _, allowBlank: false)))
                throw new InvalidOperationException("Bookmarks must contain only absolute http/https URLs.");

            if (StartupUrls != null && StartupUrls.Any(url => !TryNormalizeAbsoluteUrl(url, out _, allowBlank: false)))
                throw new InvalidOperationException("StartupUrls must contain only absolute http/https URLs.");
        }

        public void Save()
        {
            try
            {
                Normalize();
                ValidateOrThrow();
                var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(_settingsPath, json);
            }
            catch (Exception ex)
            {
                FenLogger.Error($"[Settings] Failed to save: {ex.Message}", FenBrowser.Core.Logging.LogCategory.General);
            }
        }

        private static BrowserSettings Load()
        {
            try
            {
                if (File.Exists(_settingsPath))
                {
                    var json = File.ReadAllText(_settingsPath);
                    var settings = JsonSerializer.Deserialize<BrowserSettings>(json) ?? new BrowserSettings();
                    settings.Normalize();
                    settings.ValidateOrThrow();
                    return settings;
                }
            }
            catch (Exception ex)
            {
                FenLogger.Error($"[Settings] Failed to load: {ex.Message}", FenBrowser.Core.Logging.LogCategory.General);
            }

            var fallback = new BrowserSettings();
            fallback.Normalize();
            return fallback;
        }

        private static List<Bookmark> NormalizeBookmarks(List<Bookmark> bookmarks)
        {
            if (bookmarks == null)
                return new List<Bookmark>();

            var normalized = new List<Bookmark>();
            foreach (var bookmark in bookmarks)
            {
                if (bookmark == null || !TryNormalizeAbsoluteUrl(bookmark.Url, out var normalizedUrl, allowBlank: false))
                    continue;

                normalized.Add(new Bookmark
                {
                    Title = string.IsNullOrWhiteSpace(bookmark.Title) ? normalizedUrl : bookmark.Title.Trim(),
                    Url = normalizedUrl
                });
            }

            return normalized;
        }

        private static List<string> NormalizeStartupUrls(List<string> startupUrls)
        {
            if (startupUrls == null)
                return new List<string>();

            return startupUrls
                .Where(url => TryNormalizeAbsoluteUrl(url, out _, allowBlank: false))
                .Select(url => NormalizeAbsoluteUrl(url, "https://example.com/"))
                .Distinct(StringComparer.Ordinal)
                .ToList();
        }

        private static bool IsAllowedFontSize(string fontSize)
        {
            return string.Equals(fontSize, "Small", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(fontSize, "Medium", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(fontSize, "Large", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(fontSize, "Extra Large", StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizeFontSize(string fontSize)
        {
            if (!IsAllowedFontSize(fontSize))
                return "Medium";

            return fontSize.Trim();
        }

        private static string NormalizeSearchEngineUrl(string value)
        {
            return TryNormalizeSearchEngineUrl(value, out var normalized)
                ? normalized
                : "https://www.google.com/search?q=";
        }

        private static bool TryNormalizeSearchEngineUrl(string value, out string normalized)
        {
            normalized = string.Empty;
            if (!TryNormalizeAbsoluteUrl(value, out var absoluteUrl, allowBlank: false))
                return false;

            if (!absoluteUrl.Contains("?", StringComparison.Ordinal))
                return false;

            normalized = absoluteUrl;
            return true;
        }

        private static string NormalizeAbsoluteUrl(string value, string fallback)
        {
            return TryNormalizeAbsoluteUrl(value, out var normalized, allowBlank: false)
                ? normalized
                : fallback;
        }

        private static bool TryNormalizeAbsoluteUrl(string value, out string normalized, bool allowBlank)
        {
            normalized = string.Empty;
            if (string.IsNullOrWhiteSpace(value))
                return allowBlank;

            var candidate = value.Trim();
            if (Uri.TryCreate(candidate, UriKind.Absolute, out var uri) &&
                (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            {
                normalized = uri.AbsoluteUri;
                return true;
            }

            return false;
        }
    }
}
