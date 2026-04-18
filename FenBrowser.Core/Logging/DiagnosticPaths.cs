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

            var cwd = SafeGetCurrentDirectory();
            var workspaceFromCwd = FindWorkspaceRoot(cwd);
            if (!string.IsNullOrWhiteSpace(workspaceFromCwd))
            {
                return workspaceFromCwd;
            }

            var appBase = AppContext.BaseDirectory;
            var workspaceFromBase = FindWorkspaceRoot(appBase);
            if (!string.IsNullOrWhiteSpace(workspaceFromBase))
            {
                return workspaceFromBase;
            }

            if (!string.IsNullOrWhiteSpace(cwd))
            {
                return cwd;
            }

            return appBase;
        }

        public static string GetLogsDirectory()
        {
            var envRoot = Environment.GetEnvironmentVariable(DiagnosticsRootEnv);
            if (!string.IsNullOrWhiteSpace(envRoot))
            {
                var workspaceLogsDir = Path.Combine(GetWorkspaceRoot(), "logs");
                TryEnsureDirectory(workspaceLogsDir);
                return workspaceLogsDir;
            }

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
            => Path.Combine(GetLogsDirectory(), fileName);

        public static string GetLogArtifactPath(string fileName)
            => Path.Combine(GetLogsDirectory(), fileName);

        public static void AppendRootText(string fileName, string text)
        {
            ResilientFileWriter.AppendAllText(GetRootArtifactPath(fileName), text);
        }

        public static void AppendLogText(string fileName, string text)
        {
            ResilientFileWriter.AppendAllText(GetLogArtifactPath(fileName), text);
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

        private static string SafeGetCurrentDirectory()
        {
            try
            {
                return Directory.GetCurrentDirectory();
            }
            catch
            {
                return null;
            }
        }

        private static string FindWorkspaceRoot(string startDirectory)
        {
            if (string.IsNullOrWhiteSpace(startDirectory))
            {
                return null;
            }

            try
            {
                var current = new DirectoryInfo(startDirectory);
                while (current != null)
                {
                    if (File.Exists(Path.Combine(current.FullName, "FenBrowser.sln")) ||
                        Directory.Exists(Path.Combine(current.FullName, ".git")))
                    {
                        return current.FullName;
                    }

                    current = current.Parent;
                }
            }
            catch
            {
                // Ignore and fall through to caller fallback.
            }

            return null;
        }
    }
}
