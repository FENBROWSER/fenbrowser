using FenBrowser.Js.Test262;
using Xunit;

namespace FenBrowser.Js.Tests;

public sealed class Test262ExpectationsTests
{
    [Fact]
    public void Load_ThrowsWhenFileHasNoValidEntries()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "fenjs-test262-expectations-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var filePath = Path.Combine(tempRoot, "expectations.json");

        try
        {
            File.WriteAllText(filePath, """
            {
              "metadata": {
                "owner": "js"
              },
              "expectations": []
            }
            """);

            var ex = Assert.Throws<InvalidOperationException>(() => Test262Expectations.Load(filePath));
            Assert.Contains("No valid expectation entries found in file", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Load_ThrowsWhenDirectoryHasNoValidEntries()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "fenjs-test262-expectations-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var filePath = Path.Combine(tempRoot, "empty.json");

        try
        {
            File.WriteAllText(filePath, """
            {
              "metadata": {
                "owner": "js"
              },
              "expectations": [
                {
                  "path": "",
                  "status": ""
                }
              ]
            }
            """);

            var ex = Assert.Throws<InvalidOperationException>(() => Test262Expectations.Load(tempRoot));
            Assert.Contains("No valid expectation entries found in directory", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Load_ReturnsEntriesForValidFile()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "fenjs-test262-expectations-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var filePath = Path.Combine(tempRoot, "valid.json");

        try
        {
            File.WriteAllText(filePath, """
            {
              "metadata": {
                "owner": "js",
                "area": "runtime"
              },
              "expectations": [
                {
                  "path": "test/language/foo.js",
                  "status": "RuntimeError",
                  "reason": "known",
                  "expiresAtMilestone": "M2"
                }
              ]
            }
            """);

            var loaded = Test262Expectations.Load(filePath);
            Assert.Single(loaded.Entries);
            Assert.Equal("RuntimeError", loaded.Entries[0].Status);
            Assert.Equal("js", loaded.Entries[0].Owner);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }
}
