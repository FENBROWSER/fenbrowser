using System;
using System.Reflection;
using System.IO;
using FenBrowser.Host.Widgets;
using Xunit;

namespace FenBrowser.Tests.Architecture;

public class ClipboardHelperTests
{
    [Fact]
    public void WebContentKeyboardCallbacks_DoNotReadOrMutatePageSelectionDirectly()
    {
        var sourcePath = Path.Combine(
            FindRepositoryRoot(),
            "FenBrowser.Host",
            "Widgets",
            "WebContentWidget.cs");
        var source = File.ReadAllText(sourcePath);

        Assert.DoesNotContain("Browser.GetSelectedText(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Browser.DeleteSelection(", source, StringComparison.Ordinal);
        Assert.Contains("HandleClipboardCommand(\"Copy\")", source, StringComparison.Ordinal);
        Assert.Contains("HandleClipboardCommand(\"Cut\")", source, StringComparison.Ordinal);
    }

    [Fact]
    public void TryOpenClipboardWithRetry_RetriesUntilOpenSucceeds()
    {
        var attempts = 0;
        var delayCalls = 0;

        var result = InvokeTryOpenClipboardWithRetry(
            () => ++attempts >= 3,
            5,
            _ => delayCalls++);

        Assert.True(result);
        Assert.Equal(3, attempts);
        Assert.Equal(2, delayCalls);
    }

    [Fact]
    public void TryOpenClipboardWithRetry_StopsAfterMaxAttempts()
    {
        var attempts = 0;
        var delayCalls = 0;

        var result = InvokeTryOpenClipboardWithRetry(
            () =>
            {
                attempts++;
                return false;
            },
            4,
            _ => delayCalls++);

        Assert.False(result);
        Assert.Equal(4, attempts);
        Assert.Equal(3, delayCalls);
    }

    [Fact]
    public void TryOpenClipboardWithRetry_DoesNotDelayAfterImmediateSuccess()
    {
        var delayCalls = 0;

        var result = InvokeTryOpenClipboardWithRetry(
            () => true,
            4,
            _ => delayCalls++);

        Assert.True(result);
        Assert.Equal(0, delayCalls);
    }

    private static bool InvokeTryOpenClipboardWithRetry(Func<bool> openClipboard, int maxAttempts, Action<int> retryDelay)
    {
        var method = typeof(ClipboardHelper).GetMethod(
            "TryOpenClipboardWithRetry",
            BindingFlags.Static | BindingFlags.NonPublic);

        Assert.NotNull(method);

        var result = method!.Invoke(null, new object[] { openClipboard, maxAttempts, retryDelay });
        Assert.IsType<bool>(result);
        return (bool)result!;
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "FenBrowser.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return directory!.FullName;
    }
}
