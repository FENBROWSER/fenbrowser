using System;
using System.IO;

namespace FenBrowser.Core.Logging;

/// <summary>
/// Reader/copy helpers for log artifacts that may still be held open by an
/// active <see cref="BufferedFileLogSink"/>. The sink keeps its write handle
/// open across <c>EngineLog.Flush</c> by design, so consumers must open
/// artifacts with a share mode that tolerates the writer; the default
/// <see cref="FileShare.Read"/> used by <see cref="File.Copy"/> and
/// <see cref="File.ReadAllLines(string)"/> fails on Windows while the sink is
/// attached.
/// </summary>
public static class LogArtifacts
{
    /// <summary>
    /// Share mode that coexists with the sink's write handle and with
    /// concurrent readers, including rotation-time deletes.
    /// </summary>
    private const FileShare WriterTolerantShare =
        FileShare.ReadWrite | FileShare.Delete;

    /// <summary>
    /// Reads all lines of a log artifact while tolerating an active writer.
    /// </summary>
    public static string[] ReadAllLines(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            WriterTolerantShare,
            bufferSize: 4096);
        using var reader = new StreamReader(stream);
        return ReadAllLinesCore(reader);
    }

    /// <summary>
    /// Copies a log artifact while tolerating an active writer on the source.
    /// </summary>
    public static void Copy(string sourcePath, string destinationPath, bool overwrite = true)
    {
        var dir = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrWhiteSpace(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        using var source = new FileStream(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            WriterTolerantShare,
            bufferSize: 65536);
        using var destination = new FileStream(
            destinationPath,
            overwrite ? FileMode.Create : FileMode.CreateNew,
            FileAccess.Write,
            FileShare.Read,
            bufferSize: 65536);
        source.CopyTo(destination, 65536);
    }

    private static string[] ReadAllLinesCore(StreamReader reader)
    {
        // StreamReader.ReadLine does not report whether the final line was
        // newline-terminated, so count manually to keep trailing-newline
        // semantics identical to File.ReadAllLines.
        System.Collections.Generic.List<string> lines = new();
        while (reader.Peek() >= 0)
        {
            var line = reader.ReadLine();
            if (line == null)
            {
                break;
            }

            lines.Add(line);
        }

        return lines.ToArray();
    }
}
