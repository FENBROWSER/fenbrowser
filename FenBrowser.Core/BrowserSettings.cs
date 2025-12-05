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

    public class LogSettings
    {
        public bool EnableLogging { get; set; } = false;
        public int EnabledCategories { get; set; } = (int)FenBrowser.Core.Logging.LogCategory.Errors;
        public int MinimumLevel { get; set; } = (int)FenBrowser.Core.Logging.LogLevel.Info;
        public bool LogToFile { get; set; } = true;
        public bool LogToDebug { get; set; } = true;
        public int MaxLogFileSizeMB { get; set; } = 10;
        public int MemoryBufferSize { get; set; } = 1000;
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

        public UserAgentType SelectedUserAgent { get; set; } = UserAgentType.FenBrowser;
        public ThemePreference Theme { get; set; } = ThemePreference.System;

        public LogSettings Logging { get; set; } = new LogSettings();
        public bool EnableJavaScript { get; set; } = true;
        public bool EnableTrackingPrevention { get; set; } = true;
        public static string GetUserAgentString(UserAgentType type, bool useMobile = false)
        {
            switch (type)
            {
                case UserAgentType.Chrome:
                    return useMobile
                        ? "Mozilla/5.0 (Linux; Android 10; K) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Mobile Safari/537.36"
                        : "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";

                case UserAgentType.Firefox:
                    return useMobile
                        ? "Mozilla/5.0 (Android 14; Mobile; rv:146.0) Gecko/146.0 Firefox/146.0 FenBrowser/1.0"
                        : "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:146.0) Gecko/20100101 Firefox/146.0 FenBrowser/1.0";

                case UserAgentType.FenBrowser:
                    return useMobile
                        ? "Mozilla/5.0 (Android 14; Mobile; rv:133.0) Gecko/133.0 FenBrowser/1.0"
                        : "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:133.0) Gecko/20100101 FenBrowser/1.0";

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
                System.Diagnostics.Debug.WriteLine($"[Settings] Failed to save: {ex.Message}");
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
                System.Diagnostics.Debug.WriteLine($"[Settings] Failed to load: {ex.Message}");
            }

            return new BrowserSettings();
        }
    }
}
