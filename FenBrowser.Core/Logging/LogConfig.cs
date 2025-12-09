using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace FenBrowser.Core.Logging
{
    /// <summary>
    /// Runtime-configurable logging with per-category enable/disable and persistence.
    /// Provides quick toggle methods for common debugging scenarios.
    /// Follows FenBrowser motto: modularity, security, privacy, reliability.
    /// </summary>
    public static class LogConfig
    {
        private static LogCategory _enabledCategories = LogCategory.Errors | LogCategory.General;
        private static LogLevel _minimumLevel = LogLevel.Info;
        private static bool _masterEnabled = false;
        private static readonly object _lock = new object();

        #region Master Control

        /// <summary>
        /// Master switch - quickly enable/disable ALL logging with one call.
        /// </summary>
        public static bool MasterEnabled
        {
            get => _masterEnabled;
            set
            {
                lock (_lock)
                {
                    _masterEnabled = value;
                    ApplyToLogManager();
                }
            }
        }

        /// <summary>
        /// Enable all logging (all categories, debug level).
        /// </summary>
        public static void EnableAll()
        {
            lock (_lock)
            {
                _masterEnabled = true;
                _enabledCategories = LogCategory.All;
                _minimumLevel = LogLevel.Debug;
                ApplyToLogManager();
            }
        }

        /// <summary>
        /// Disable all logging completely.
        /// </summary>
        public static void DisableAll()
        {
            lock (_lock)
            {
                _masterEnabled = false;
                ApplyToLogManager();
            }
        }

        #endregion

        #region Category Control

        /// <summary>
        /// Get currently enabled log categories.
        /// </summary>
        public static LogCategory EnabledCategories => _enabledCategories;

        /// <summary>
        /// Get current minimum log level.
        /// </summary>
        public static LogLevel MinimumLevel => _minimumLevel;

        /// <summary>
        /// Enable a specific log category.
        /// </summary>
        public static void EnableCategory(LogCategory category)
        {
            lock (_lock)
            {
                _enabledCategories |= category;
                ApplyToLogManager();
            }
        }

        /// <summary>
        /// Disable a specific log category.
        /// </summary>
        public static void DisableCategory(LogCategory category)
        {
            lock (_lock)
            {
                _enabledCategories &= ~category;
                ApplyToLogManager();
            }
        }

        /// <summary>
        /// Check if a category is enabled.
        /// </summary>
        public static bool IsCategoryEnabled(LogCategory category)
        {
            return _masterEnabled && _enabledCategories.HasFlag(category);
        }

        /// <summary>
        /// Set the minimum log level.
        /// </summary>
        public static void SetMinimumLevel(LogLevel level)
        {
            lock (_lock)
            {
                _minimumLevel = level;
                ApplyToLogManager();
            }
        }

        /// <summary>
        /// Set specific categories to enable (replaces current settings).
        /// </summary>
        public static void SetCategories(LogCategory categories)
        {
            lock (_lock)
            {
                _enabledCategories = categories;
                ApplyToLogManager();
            }
        }

        #endregion

        #region Quick Presets

        /// <summary>
        /// Enable only error logging (minimal output).
        /// </summary>
        public static void PresetErrorsOnly()
        {
            lock (_lock)
            {
                _masterEnabled = true;
                _enabledCategories = LogCategory.Errors;
                _minimumLevel = LogLevel.Error;
                ApplyToLogManager();
            }
        }

        /// <summary>
        /// Enable rendering-related logging (Layout, CSS, Rendering, DOM).
        /// </summary>
        public static void PresetRendering()
        {
            lock (_lock)
            {
                _masterEnabled = true;
                _enabledCategories = LogCategory.Errors | LogCategory.Rendering | 
                    LogCategory.Layout | LogCategory.CSS | LogCategory.DOM;
                _minimumLevel = LogLevel.Debug;
                ApplyToLogManager();
            }
        }

        /// <summary>
        /// Enable JavaScript-related logging.
        /// </summary>
        public static void PresetJavaScript()
        {
            lock (_lock)
            {
                _masterEnabled = true;
                _enabledCategories = LogCategory.Errors | LogCategory.JavaScript | LogCategory.JsExecution;
                _minimumLevel = LogLevel.Debug;
                ApplyToLogManager();
            }
        }

        /// <summary>
        /// Enable network-related logging.
        /// </summary>
        public static void PresetNetwork()
        {
            lock (_lock)
            {
                _masterEnabled = true;
                _enabledCategories = LogCategory.Errors | LogCategory.Network | LogCategory.Navigation;
                _minimumLevel = LogLevel.Debug;
                ApplyToLogManager();
            }
        }

        /// <summary>
        /// Enable feature gap tracking (HTML/CSS/JS unsupported features).
        /// </summary>
        public static void PresetFeatureGaps()
        {
            lock (_lock)
            {
                _masterEnabled = true;
                _enabledCategories = LogCategory.Errors | LogCategory.HtmlParsing | 
                    LogCategory.CssParsing | LogCategory.JsExecution | LogCategory.FeatureGaps;
                _minimumLevel = LogLevel.Warn;
                ApplyToLogManager();
            }
        }

        #endregion

        #region Persistence

        /// <summary>
        /// Save current configuration to JSON file.
        /// </summary>
        public static void SaveToFile(string filePath)
        {
            try
            {
                var config = new LogConfigData
                {
                    MasterEnabled = _masterEnabled,
                    EnabledCategories = (int)_enabledCategories,
                    MinimumLevel = (int)_minimumLevel
                };

                string json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(filePath, json);
            }
            catch (Exception ex)
            {
                LogManager.LogError(LogCategory.General, $"Failed to save log config: {ex.Message}");
            }
        }

        /// <summary>
        /// Load configuration from JSON file.
        /// </summary>
        public static void LoadFromFile(string filePath)
        {
            try
            {
                if (!File.Exists(filePath)) return;

                string json = File.ReadAllText(filePath);
                var config = JsonSerializer.Deserialize<LogConfigData>(json);

                if (config != null)
                {
                    lock (_lock)
                    {
                        _masterEnabled = config.MasterEnabled;
                        _enabledCategories = (LogCategory)config.EnabledCategories;
                        _minimumLevel = (LogLevel)config.MinimumLevel;
                        ApplyToLogManager();
                    }
                }
            }
            catch (Exception ex)
            {
                LogManager.LogError(LogCategory.General, $"Failed to load log config: {ex.Message}");
            }
        }

        private class LogConfigData
        {
            public bool MasterEnabled { get; set; }
            public int EnabledCategories { get; set; }
            public int MinimumLevel { get; set; }
        }

        #endregion

        #region Internal

        private static void ApplyToLogManager()
        {
            LogManager.Initialize(_masterEnabled, _enabledCategories, _minimumLevel);
        }

        /// <summary>
        /// Get a human-readable summary of current logging configuration.
        /// </summary>
        public static string GetConfigSummary()
        {
            var enabledList = new List<string>();
            foreach (LogCategory cat in Enum.GetValues(typeof(LogCategory)))
            {
                if (cat != LogCategory.None && cat != LogCategory.All && _enabledCategories.HasFlag(cat))
                {
                    enabledList.Add(cat.ToString());
                }
            }

            return $"Master: {(_masterEnabled ? "ON" : "OFF")}, Level: {_minimumLevel}, " +
                   $"Categories: {string.Join(", ", enabledList)}";
        }

        #endregion
    }
}
