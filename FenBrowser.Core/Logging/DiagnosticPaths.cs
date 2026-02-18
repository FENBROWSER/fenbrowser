using System;
using System.IO;
using FenBrowser.Core;

namespace FenBrowser.Core.Logging
{
    /// <summary>
    /// Centralized diagnostics artifact paths for debug dumps and screenshots.
    /// This removes hardcoded machine-specific absolute paths.
    /// </summary>
    public static class DiagnosticPaths
    {
        private const string DiagnosticsRootEnv = "FEN_DIAGNOSTICS_DIR";

        public static string GetWorkspaceRoot()
        {
            var envRoot = Environment.GetEnvironmentVariable(DiagnosticsRootEnv);
            if (!string.IsNullOrWhiteSpace(envRoot))
            {
                TryEnsureDirectory(envRoot);
                return envRoot;
            }

            var cwd = Directory.GetCurrentDirectory();
            if (!string.IsNullOrWhiteSpace(cwd))
            {
                return cwd;
            }

            return AppContext.BaseDirectory;
        }

        public static string GetLogsDirectory()
        {
            try
            {
                var configured = BrowserSettings.Instance?.Logging?.LogPath;
                if (!string.IsNullOrWhiteSpace(configured))
                {
                    TryEnsureDirectory(configured);
                    return configured;
                }
            }
            catch
            {
                // Fall back to workspace-root logs.
            }

            var logsDir = Path.Combine(GetWorkspaceRoot(), "logs");
            TryEnsureDirectory(logsDir);
            return logsDir;
        }

        public static string GetRootArtifactPath(string fileName)
            => Path.Combine(GetWorkspaceRoot(), fileName);

        public static string GetLogArtifactPath(string fileName)
            => Path.Combine(GetLogsDirectory(), fileName);

        public static void AppendRootText(string fileName, string text)
        {
            try { File.AppendAllText(GetRootArtifactPath(fileName), text); } catch { }
        }

        public static void AppendLogText(string fileName, string text)
        {
            try { File.AppendAllText(GetLogArtifactPath(fileName), text); } catch { }
        }

        private static void TryEnsureDirectory(string path)
        {
            try
            {
                if (!Directory.Exists(path))
                {
                    Directory.CreateDirectory(path);
                }
            }
            catch
            {
                // Ignore diagnostics path setup failures.
            }
        }
    }
}
