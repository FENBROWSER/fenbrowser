using System;
using System.Diagnostics;
using System.IO;
using FenBrowser.Core.Security.Sandbox;
using Xunit;

namespace FenBrowser.Tests.Core;

public class CodeCleanupChecklistContractTests
{
    [Fact]
    public void ReducedHtmlFixture_Exists_ForLayoutPaintEventCoverage()
    {
        var root = FindRepositoryRoot();
        var fixturePath = Path.Combine(root, "FenBrowser.Tests", "Fixtures", "Reduced", "layout_paint_event_minimal.html");

        Assert.True(File.Exists(fixturePath), $"Missing reduced HTML fixture: {fixturePath}");

        var html = File.ReadAllText(fixturePath);
        Assert.Contains("id=\"visible\"", html, StringComparison.Ordinal);
        Assert.Contains("addEventListener(\"click\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public void NullSandbox_SpawnProcess_ThrowsNotSupportedException()
    {
        using var sandbox = new NullSandbox(OsSandboxProfile.RendererMinimal, suppressWarning: true);
        var startInfo = new ProcessStartInfo("cmd.exe", "/c echo noop");

        Assert.Throws<NotSupportedException>(() => sandbox.SpawnProcess(startInfo));
    }

    private static string FindRepositoryRoot()
    {
        var current = AppContext.BaseDirectory;
        for (var i = 0; i < 10; i++)
        {
            if (File.Exists(Path.Combine(current, "FenBrowser.sln")))
            {
                return current;
            }

            var parent = Directory.GetParent(current);
            if (parent == null)
            {
                break;
            }

            current = parent.FullName;
        }

        throw new InvalidOperationException("Could not locate repository root from test base directory.");
    }
}
