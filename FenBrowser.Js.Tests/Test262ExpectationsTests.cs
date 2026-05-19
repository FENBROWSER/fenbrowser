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

            var ex = Assert.Throws<InvalidDataException>(() => Test262Expectations.Load(tempRoot));
            Assert.Contains("missing required 'path'", ex.Message, StringComparison.Ordinal);
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

    [Fact]
    public void Load_ThrowsWhenStatusIsInvalid()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "fenjs-test262-expectations-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var filePath = Path.Combine(tempRoot, "invalid-status.json");

        try
        {
            File.WriteAllText(filePath, """
            {
              "expectations": [
                {
                  "path": "test/language/foo.js",
                  "status": "TypoStatus"
                }
              ]
            }
            """);

            var ex = Assert.Throws<InvalidDataException>(() => Test262Expectations.Load(filePath));
            Assert.Contains("Invalid expectation status", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Load_ThrowsWhenPathIsMissing()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "fenjs-test262-expectations-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var filePath = Path.Combine(tempRoot, "missing-path.json");

        try
        {
            File.WriteAllText(filePath, """
            {
              "expectations": [
                {
                  "status": "RuntimeError"
                }
              ]
            }
            """);

            var ex = Assert.Throws<InvalidDataException>(() => Test262Expectations.Load(filePath));
            Assert.Contains("missing required 'path'", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Load_ThrowsWhenStatusIsMissing()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "fenjs-test262-expectations-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var filePath = Path.Combine(tempRoot, "missing-status.json");

        try
        {
            File.WriteAllText(filePath, """
            {
              "expectations": [
                {
                  "path": "test/language/foo.js"
                }
              ]
            }
            """);

            var ex = Assert.Throws<InvalidDataException>(() => Test262Expectations.Load(filePath));
            Assert.Contains("missing required 'status'", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Load_ThrowsWhenOwnerIsMissing()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "fenjs-test262-expectations-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var filePath = Path.Combine(tempRoot, "missing-owner.json");

        try
        {
            File.WriteAllText(filePath, """
            {
              "metadata": {
                "area": "runtime"
              },
              "expectations": [
                {
                  "path": "test/language/foo.js",
                  "status": "RuntimeError",
                  "reason": "known runtime limitation"
                }
              ]
            }
            """);

            var ex = Assert.Throws<InvalidDataException>(() => Test262Expectations.Load(filePath));
            Assert.Contains("missing required 'owner'", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Load_ThrowsWhenAreaIsMissing()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "fenjs-test262-expectations-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var filePath = Path.Combine(tempRoot, "missing-area.json");

        try
        {
            File.WriteAllText(filePath, """
            {
              "metadata": {
                "owner": "js"
              },
              "expectations": [
                {
                  "path": "test/language/foo.js",
                  "status": "RuntimeError",
                  "reason": "known runtime limitation"
                }
              ]
            }
            """);

            var ex = Assert.Throws<InvalidDataException>(() => Test262Expectations.Load(filePath));
            Assert.Contains("missing required 'area'", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Load_ThrowsWhenReasonIsMissing()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "fenjs-test262-expectations-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var filePath = Path.Combine(tempRoot, "missing-reason.json");

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
                  "status": "RuntimeError"
                }
              ]
            }
            """);

            var ex = Assert.Throws<InvalidDataException>(() => Test262Expectations.Load(filePath));
            Assert.Contains("missing required 'reason'", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Load_ThrowsWhenReasonIsPlaceholder()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "fenjs-test262-expectations-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var filePath = Path.Combine(tempRoot, "placeholder-reason.json");

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
                  "reason": "tbd"
                }
              ]
            }
            """);

            var ex = Assert.Throws<InvalidDataException>(() => Test262Expectations.Load(filePath));
            Assert.Contains("missing required 'reason'", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Entry_Matches_WildcardAndCaseInsensitivePath()
    {
        var entry = new Test262ExpectationEntry(
            PathPattern: "test/language/*/foo.js",
            Status: "RuntimeError",
            Reason: string.Empty,
            ExpiresAtMilestone: "M3",
            Owner: "js",
            Area: "runtime");

        Assert.True(entry.Matches("test/language/expressions/Foo.js", "runtimeerror"));
        Assert.False(entry.Matches("test/language/expressions/bar.js", "runtimeerror"));
        Assert.False(entry.Matches("test/language/expressions/Foo.js", "ParserError"));
    }
}
