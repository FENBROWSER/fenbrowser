using System;
using System.Reflection;
using FenBrowser.Host.Widgets;
using Xunit;

namespace FenBrowser.Tests.Architecture;

public class ClipboardHelperTests
{
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
}
