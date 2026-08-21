using System.IO;
using System.Text;
using FenBrowser.Core.Logging;
using Xunit;

namespace FenBrowser.Tests.Logging;

/// <summary>
/// Contract tests for <see cref="LogArtifacts"/>: log artifact readers must
/// tolerate the engine file sink's still-open write handle. The sink keeps its
/// handle open across <c>EngineLog.Flush</c>, and Windows enforces share-mode
/// compatibility, so plain <see cref="File.Copy"/>/<see cref="File.ReadAllLines(string)"/>
/// fail while logging is active.
/// </summary>
public sealed class LogArtifactsTests
{
    [Fact]
    public void ReadAllLines_ToleratesActiveWriterHandle()
    {
        var path = Path.Combine(Path.GetTempPath(), $"fenbrowser-logartifacts-read-{Path.GetRandomFileName()}.jsonl");
        try
        {
            // Simulate the sink: an open write handle kept across flushes.
            using var writerHandle = new FileStream(
                path,
                FileMode.Create,
                FileAccess.Write,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 1024);
            var payload = Encoding.UTF8.GetBytes("{\"a\":1}\n{\"a\":2}\n");
            writerHandle.Write(payload);
            writerHandle.Flush();

            var lines = LogArtifacts.ReadAllLines(path);

            Assert.Equal(2, lines.Length);
            Assert.Equal("{\"a\":1}", lines[0]);
            Assert.Equal("{\"a\":2}", lines[1]);
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    [Fact]
    public void Copy_ToleratesActiveWriterHandle()
    {
        var sourcePath = Path.Combine(Path.GetTempPath(), $"fenbrowser-logartifacts-src-{Path.GetRandomFileName()}.jsonl");
        var destinationPath = Path.Combine(Path.GetTempPath(), $"fenbrowser-logartifacts-dst-{Path.GetRandomFileName()}.jsonl");
        try
        {
            using var writerHandle = new FileStream(
                sourcePath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 1024);
            var payload = Encoding.UTF8.GetBytes("line-one\nline-two\n");
            writerHandle.Write(payload);
            writerHandle.Flush();

            LogArtifacts.Copy(sourcePath, destinationPath);

            Assert.Equal(payload, File.ReadAllBytes(destinationPath));
        }
        finally
        {
            try { File.Delete(sourcePath); } catch { }
            try { File.Delete(destinationPath); } catch { }
        }
    }

    [Fact]
    public void ReadAllLines_EmptyFile_ReturnsNoLines()
    {
        var path = Path.Combine(Path.GetTempPath(), $"fenbrowser-logartifacts-empty-{Path.GetRandomFileName()}.jsonl");
        try
        {
            File.WriteAllBytes(path, Array.Empty<byte>());

            Assert.Empty(LogArtifacts.ReadAllLines(path));
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }
}
