using System;
using System.IO;
using System.Text.Json;

namespace FenBrowser.Core
{
    public enum UserAgentType
    {
        FenBrowser,
        Firefox,
        Chrome
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
        public bool EnableLogging { get; set; } = false;
        public int EnabledCategories { get; set; } = -1; // All categories enabled by default for debugging
        public int MinimumLevel { get; set; } = (int)FenBrowser.Core.Logging.LogLevel.Info;
        public bool LogToFile { get; set; } = true;
        public bool LogToDebug { get; set; } = true;
        public int MaxLogFileSizeMB { get; set; } = 10;
        public int MemoryBufferSize { get; set; } = 1000;
        
        /// <summary>
        /// Path for log files. Defaults to "logs" folder in the current execution directory.
        /// </summary>
        public string LogPath { get; set; } = System.IO.Path.Combine(
            AppContext.BaseDirectory, "logs");
    }

    public class BrowserSettings
    {
        private static BrowserSettings _instance;
        private static readonly object _lock = new object();
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

        public UserAgentType SelectedUserAgent { get; set; } = UserAgentType.Chrome;
        public ThemePreference Theme { get; set; } = ThemePreference.System;

        public bool ShowFavoritesBar 
        { 
            get => _showFavoritesBar; 
            set => _showFavoritesBar = value;
        }
        private bool _showFavoritesBar = true;
        public bool ShowFavoritesButton { get; set; } = true;
        public System.Collections.Generic.List<Bookmark> Bookmarks { get; set; } = new System.Collections.Generic.List<Bookmark>();
        
        public LogSettings Logging { get; set; } = new LogSettings();
        public bool EnableJavaScript { get; set; } = true;
        public bool EnableTrackingPrevention { get; set; } = true;
        
        /// <summary>
        /// Home page URL for the Home button
        /// </summary>
        public string HomePage { get; set; } = "example.com";
        
        // General Settings
        public string SearchEngine { get; set; } = "Google";
        public string SearchEngineUrl { get; set; } = "https://www.google.com/search?q=";
        public double DefaultZoom { get; set; } = 1.0;
        public bool RestoreTabsOnStartup { get; set; } = false;
        public StartupBehavior StartupAction { get; set; } = StartupBehavior.OpenNewTab;
        public System.Collections.Generic.List<string> StartupUrls { get; set; } = new System.Collections.Generic.List<string>();
        
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
        public bool SafeBrowsing { get; set; } = true;
        public bool ImproveBrowser { get; set; } = false;
        public bool BlockPopups { get; set; } = true;
        
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
            switch (type)
            {
                case UserAgentType.Chrome:
                    return useMobile
                        ? "Mozilla/5.0 (Linux; Android 10; K) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/121.0.6167.85 Mobile Safari/537.36"
                        : "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/121.0.6167.85 Safari/537.36";

                case UserAgentType.Firefox:
                    return useMobile
                        ? "Mozilla/5.0 (Android 14; Mobile; rv:133.0) Gecko/133.0 Firefox/133.0"
                        : "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:133.0) Gecko/20100101 Firefox/133.0";

                case UserAgentType.FenBrowser:
                    // Mimic Chrome to avoid blocking, but keep identifier
                    return useMobile
                        ? "Mozilla/5.0 (Linux; Android 10; K) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/145.0.7600.0 Mobile Safari/537.36 FenBrowser/1.0"
                        : "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/145.0.7600.0 Safari/537.36 FenBrowser/1.0";

                default:
                    return GetUserAgentString(UserAgentType.Chrome, useMobile);
            }
        }

        public void Save()
        {
            try
            {
                var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(_settingsPath, json);
            }
            catch (Exception ex)
            {
                FenBrowser.Core.FenLogger.Error($"[Settings] Failed to save: {ex.Message}", FenBrowser.Core.Logging.LogCategory.General);
            }
        }

        private static BrowserSettings Load()
        {
            try
            {
                if (File.Exists(_settingsPath))
                {
                    var json = File.ReadAllText(_settingsPath);
                    return JsonSerializer.Deserialize<BrowserSettings>(json) ?? new BrowserSettings();
                }
            }
            catch (Exception ex)
            {
                FenBrowser.Core.FenLogger.Error($"[Settings] Failed to load: {ex.Message}", FenBrowser.Core.Logging.LogCategory.General);
            }

            var settings = new BrowserSettings();
            
            // Add some default bookmarks
            // Bookmarks are empty by default for privacy
            // settings.Bookmarks.Add(new Bookmark { Title = "FenBrowser", Url = "https://fenbrowser.com" });
            
            return settings;
        }
    }
}
