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
                  "reason": "known runtime limitation",
                  "expiresAtMilestone": "M2"
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
                  "reason": "known runtime limitation",
                  "expiresAtMilestone": "M2"
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
                  "status": "RuntimeError",
                  "expiresAtMilestone": "M2"
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
                  "reason": "tbd",
                  "expiresAtMilestone": "M2"
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

    [Fact]
    public void Load_ThrowsWhenMilestoneIsMissing()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "fenjs-test262-expectations-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var filePath = Path.Combine(tempRoot, "missing-milestone.json");

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
                  "reason": "known runtime limitation"
                }
              ]
            }
            """);

            var ex = Assert.Throws<InvalidDataException>(() => Test262Expectations.Load(filePath));
            Assert.Contains("invalid 'expiresAtMilestone'", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Load_ThrowsWhenMilestoneFormatIsInvalid()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "fenjs-test262-expectations-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var filePath = Path.Combine(tempRoot, "invalid-milestone.json");

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
                  "reason": "known runtime limitation",
                  "expiresAtMilestone": "next"
                }
              ]
            }
            """);

            var ex = Assert.Throws<InvalidDataException>(() => Test262Expectations.Load(filePath));
            Assert.Contains("invalid 'expiresAtMilestone'", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Load_AllowsDecimalMilestoneFormat()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "fenjs-test262-expectations-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var filePath = Path.Combine(tempRoot, "decimal-milestone.json");

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
                  "reason": "known runtime limitation",
                  "expiresAtMilestone": "M2.1"
                }
              ]
            }
            """);

            var loaded = Test262Expectations.Load(filePath);
            Assert.Single(loaded.Entries);
            Assert.Equal("M2.1", loaded.Entries[0].ExpiresAtMilestone);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Load_RejectsLowercaseMilestonePrefix()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "fenjs-test262-expectations-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var filePath = Path.Combine(tempRoot, "lowercase-milestone.json");

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
                  "reason": "known runtime limitation",
                  "expiresAtMilestone": "m2"
                }
              ]
            }
            """);

            var ex = Assert.Throws<InvalidDataException>(() => Test262Expectations.Load(filePath));
            Assert.Contains("invalid 'expiresAtMilestone'", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Load_Directory_ThrowsWhenOwnerMetadataConflicts()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "fenjs-test262-expectations-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        try
        {
            File.WriteAllText(Path.Combine(tempRoot, "a.json"), """
            {
              "metadata": {
                "owner": "team-a",
                "area": "runtime",
                "test262Commit": "abc"
              },
              "expectations": [
                {
                  "path": "test/language/a.js",
                  "status": "RuntimeError",
                  "reason": "known",
                  "expiresAtMilestone": "M2"
                }
              ]
            }
            """);

            File.WriteAllText(Path.Combine(tempRoot, "b.json"), """
            {
              "metadata": {
                "owner": "team-b",
                "area": "runtime",
                "test262Commit": "abc"
              },
              "expectations": [
                {
                  "path": "test/language/b.js",
                  "status": "RuntimeError",
                  "reason": "known",
                  "expiresAtMilestone": "M2"
                }
              ]
            }
            """);

            var ex = Assert.Throws<InvalidDataException>(() => Test262Expectations.Load(tempRoot));
            Assert.Contains("Conflicting expectation metadata 'owner'", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Load_Directory_ThrowsWhenAreaMetadataConflicts()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "fenjs-test262-expectations-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        try
        {
            File.WriteAllText(Path.Combine(tempRoot, "a.json"), """
            {
              "metadata": {
                "owner": "team-a",
                "area": "runtime",
                "test262Commit": "abc"
              },
              "expectations": [
                {
                  "path": "test/language/a.js",
                  "status": "RuntimeError",
                  "reason": "known",
                  "expiresAtMilestone": "M2"
                }
              ]
            }
            """);

            File.WriteAllText(Path.Combine(tempRoot, "b.json"), """
            {
              "metadata": {
                "owner": "team-a",
                "area": "parser",
                "test262Commit": "abc"
              },
              "expectations": [
                {
                  "path": "test/language/b.js",
                  "status": "RuntimeError",
                  "reason": "known",
                  "expiresAtMilestone": "M2"
                }
              ]
            }
            """);

            var ex = Assert.Throws<InvalidDataException>(() => Test262Expectations.Load(tempRoot));
            Assert.Contains("Conflicting expectation metadata 'area'", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Load_Directory_ThrowsWhenCommitMetadataConflicts()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "fenjs-test262-expectations-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        try
        {
            File.WriteAllText(Path.Combine(tempRoot, "a.json"), """
            {
              "metadata": {
                "owner": "team-a",
                "area": "runtime",
                "test262Commit": "abc"
              },
              "expectations": [
                {
                  "path": "test/language/a.js",
                  "status": "RuntimeError",
                  "reason": "known",
                  "expiresAtMilestone": "M2"
                }
              ]
            }
            """);

            File.WriteAllText(Path.Combine(tempRoot, "b.json"), """
            {
              "metadata": {
                "owner": "team-a",
                "area": "runtime",
                "test262Commit": "def"
              },
              "expectations": [
                {
                  "path": "test/language/b.js",
                  "status": "RuntimeError",
                  "reason": "known",
                  "expiresAtMilestone": "M2"
                }
              ]
            }
            """);

            var ex = Assert.Throws<InvalidDataException>(() => Test262Expectations.Load(tempRoot));
            Assert.Contains("Conflicting expectation metadata 'test262Commit'", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Load_Directory_MergesWhenMetadataIsConsistent()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "fenjs-test262-expectations-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        try
        {
            File.WriteAllText(Path.Combine(tempRoot, "a.json"), """
            {
              "metadata": {
                "owner": "team-a",
                "area": "runtime",
                "test262Commit": "abc"
              },
              "expectations": [
                {
                  "path": "test/language/a.js",
                  "status": "RuntimeError",
                  "reason": "known",
                  "expiresAtMilestone": "M2"
                }
              ]
            }
            """);

            File.WriteAllText(Path.Combine(tempRoot, "b.json"), """
            {
              "metadata": {
                "owner": "team-a",
                "area": "runtime",
                "test262Commit": "abc"
              },
              "expectations": [
                {
                  "path": "test/language/b.js",
                  "status": "ParserError",
                  "reason": "known",
                  "expiresAtMilestone": "M2.1"
                }
              ]
            }
            """);

            var loaded = Test262Expectations.Load(tempRoot);
            Assert.Equal("team-a", loaded.MetadataOwner);
            Assert.Equal("runtime", loaded.MetadataArea);
            Assert.Equal("abc", loaded.MetadataCommit);
            Assert.Equal(2, loaded.Entries.Count);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Load_ThrowsWhenDirectoryContainsNoJsonFiles()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "fenjs-test262-expectations-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        try
        {
            File.WriteAllText(Path.Combine(tempRoot, "note.txt"), "not json");

            var ex = Assert.Throws<InvalidOperationException>(() => Test262Expectations.Load(tempRoot));
            Assert.Contains("No valid expectation entries found in directory", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }
}
