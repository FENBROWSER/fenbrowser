using System.Collections.Generic;
using System.IO;
using FenBrowser.Core;
using Xunit;

namespace FenBrowser.Tests.Core
{
    public class BrowserSettingsTests
    {
        [Fact]
        public void Normalize_FixesInvalidUserFacingSettings_AndValidatePasses()
        {
            var settings = new BrowserSettings
            {
                HomePage = "example.com",
                SearchEngineUrl = "not-a-url",
                DefaultZoom = 50,
                FontSize = "Huge",
                Logging = new LogSettings
                {
                    MaxLogFileSizeMB = 0,
                    MaxArchivedFiles = 0,
                    MemoryBufferSize = 0,
                    LogPath = ""
                },
                Bookmarks = new List<Bookmark>
                {
                    new() { Title = "Broken", Url = "notaurl" },
                    new() { Title = "", Url = "https://fenbrowser.dev/docs" }
                },
                StartupUrls = new List<string>
                {
                    "https://example.org",
                    "notaurl",
                    "https://example.org"
                }
            };

            settings.Normalize();
            settings.ValidateOrThrow();

            Assert.Equal("https://example.com/", settings.HomePage);
            Assert.Equal("https://www.google.com/search?q=", settings.SearchEngineUrl);
            Assert.Equal(5.0, settings.DefaultZoom);
            Assert.Equal("Medium", settings.FontSize);
            Assert.Single(settings.Bookmarks);
            Assert.Equal("https://fenbrowser.dev/docs", settings.Bookmarks[0].Title);
            Assert.Single(settings.StartupUrls);
            Assert.Equal("https://example.org/", settings.StartupUrls[0]);
            Assert.Equal(10, settings.Logging.MaxLogFileSizeMB);
            Assert.Equal(10, settings.Logging.MaxArchivedFiles);
            Assert.Equal(1000, settings.Logging.MemoryBufferSize);
            Assert.False(string.IsNullOrWhiteSpace(settings.Logging.LogPath));
        }

        [Fact]
        public void Normalize_MigratesLegacyLogPath_ToCurrentWorkspaceLogs()
        {
            string originalDirectory = Directory.GetCurrentDirectory();
            string tempDirectory = Path.Combine(Path.GetTempPath(), "fenbrowser-settings-tests", Path.GetRandomFileName());
            Directory.CreateDirectory(tempDirectory);

            try
            {
                Directory.SetCurrentDirectory(tempDirectory);

                var settings = new BrowserSettings
                {
                    Logging = new LogSettings
                    {
                        LogPath = Path.Combine(AppContext.BaseDirectory, "logs")
                    }
                };

                settings.Normalize();

                Assert.True(settings.Logging.EnableLogging);
                Assert.Equal(Path.Combine(tempDirectory, "logs"), settings.Logging.LogPath);
            }
            finally
            {
                Directory.SetCurrentDirectory(originalDirectory);

                if (Directory.Exists(tempDirectory))
                {
                    Directory.Delete(tempDirectory, recursive: true);
                }
            }
        }
    }
}
