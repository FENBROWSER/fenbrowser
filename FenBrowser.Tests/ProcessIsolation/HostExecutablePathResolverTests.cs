using System;
using System.IO;
using FenBrowser.Host.ProcessIsolation;
using Xunit;

namespace FenBrowser.Tests.ProcessIsolation;

public sealed class HostExecutablePathResolverTests
{
    [Fact]
    public void Resolve_UsesFenBrowserAppHostWhenEmbeddedByTestRunner()
    {
        var resolved = HostExecutablePathResolver.Resolve();

        Assert.False(string.IsNullOrWhiteSpace(resolved));
        Assert.True(File.Exists(resolved));
        Assert.Equal(
            OperatingSystem.IsWindows() ? "FenBrowser.Host.exe" : "FenBrowser.Host",
            Path.GetFileName(resolved));
    }
}
