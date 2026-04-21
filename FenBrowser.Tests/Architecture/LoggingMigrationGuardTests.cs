using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace FenBrowser.Tests.Architecture;

public class LoggingMigrationGuardTests
{
    private static readonly string[] MigrationScopedProjects =
    [
        "FenBrowser.Host",
        "FenBrowser.FenEngine",
        "FenBrowser.DevTools",
        "FenBrowser.WebDriver"
    ];

    [Fact]
    public void MigrationScopedProjects_DoNotUseLegacyFenLogger()
    {
        var repoRoot = GetRepoRoot();
        var offenders = new List<string>();

        foreach (var project in MigrationScopedProjects)
        {
            var projectRoot = Path.Combine(repoRoot, project);
            foreach (var file in Directory.EnumerateFiles(projectRoot, "*.cs", SearchOption.AllDirectories))
            {
                if (file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) ||
                    file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var source = File.ReadAllText(file);
                if (source.Contains("FenLogger.", StringComparison.Ordinal))
                {
                    offenders.Add(Path.GetRelativePath(repoRoot, file));
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            "Legacy FenLogger usage found:\n" + string.Join("\n", offenders.OrderBy(x => x)));
    }

    [Theory]
    [InlineData("FenBrowser.Host/Program.cs")]
    [InlineData("FenBrowser.Host/Widgets/SettingsPageWidget.cs")]
    public void HostBootstrapPaths_DoNotUseLegacyLogManagerInitialization(string relativePath)
    {
        var fullPath = Path.Combine(GetRepoRoot(), relativePath);
        var source = File.ReadAllText(fullPath);

        Assert.DoesNotContain("LogManager.InitializeFromSettings(", source);
    }

    private static string GetRepoRoot()
    {
        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    }
}
