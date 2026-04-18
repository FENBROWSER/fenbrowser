using System;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace FenBrowser.Core.Logging
{
    /// <summary>
    /// Best-effort file writes for high-frequency diagnostics/logging paths.
    /// Retries transient share/lock failures and never throws to callers.
    /// </summary>
    internal static class ResilientFileWriter
    {
        private const int MaxAttempts = 4;
        private static readonly object WarningLock = new object();
        private static DateTime _lastWarningUtc = DateTime.MinValue;

        public static bool AppendAllText(string path, string content)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            return ExecuteWithRetry(path, () =>
            {
                EnsureDirectory(path);
                using var stream = new FileStream(
                    path,
                    FileMode.Append,
                    FileAccess.Write,
                    FileShare.ReadWrite | FileShare.Delete);
                using var writer = new StreamWriter(stream);
                writer.Write(content ?? string.Empty);
            });
        }

        public static bool WriteAllText(string path, string content)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            return ExecuteWithRetry(path, () =>
            {
                EnsureDirectory(path);
                using var stream = new FileStream(
                    path,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.ReadWrite | FileShare.Delete);
                using var writer = new StreamWriter(stream);
                writer.Write(content ?? string.Empty);
            });
        }

        public static bool TryMoveFile(string sourcePath, string destinationPath)
        {
            if (string.IsNullOrWhiteSpace(sourcePath) || string.IsNullOrWhiteSpace(destinationPath))
            {
                return false;
            }

            return ExecuteWithRetry(sourcePath, () =>
            {
                EnsureDirectory(destinationPath);
                File.Move(sourcePath, destinationPath, overwrite: false);
            });
        }

        private static bool ExecuteWithRetry(string path, Action action)
        {
            for (var attempt = 1; attempt <= MaxAttempts; attempt++)
            {
                try
                {
                    action();
                    return true;
                }
                catch (Exception ex) when (IsTransient(ex))
                {
                    if (attempt == MaxAttempts)
                    {
                        EmitThrottledWarning(path, ex);
                        return false;
                    }

                    Thread.Sleep(attempt * 3);
                }
                catch (Exception ex)
                {
                    EmitThrottledWarning(path, ex);
                    return false;
                }
            }

            return false;
        }

        private static bool IsTransient(Exception ex)
        {
            return ex is IOException || ex is UnauthorizedAccessException;
        }

        private static void EnsureDirectory(string filePath)
        {
            var directory = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrWhiteSpace(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }
        }

        private static void EmitThrottledWarning(string path, Exception ex)
        {
            lock (WarningLock)
            {
                var now = DateTime.UtcNow;
                if ((now - _lastWarningUtc).TotalSeconds < 2)
                {
                    return;
                }

                _lastWarningUtc = now;
            }

            Debug.WriteLine($"[ResilientFileWriter] write skipped for '{path}': {ex.GetType().Name}: {ex.Message}");
        }
    }
}
