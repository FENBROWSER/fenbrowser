using System;
using FenBrowser.Core.Logging;
using Xunit;

namespace FenBrowser.Tests.Logging;

/// <summary>
/// Phase 13 regression: hot-path logging must be rate-limited so that a repeated
/// identical event emits at most once per window instead of once per occurrence.
/// </summary>
[Collection(EngineLogTestCollection.Name)]
public class Phase13RateLimitedLoggingTests
{
    [Fact]
    public void WriteRateLimited_EmitsOncePerWindowForSameKey()
    {
        EngineLog.Configure(new EngineLoggingOptions
        {
            Enabled = true,
            GlobalMinimumSeverity = LogSeverity.Debug,
            EnableConsoleSink = false,
            EnableDebugSink = false,
            EnableNdjsonSink = false,
            EnableRingBufferSink = true,
            EnableTraceSink = false,
            RingBufferCapacity = 1000
        });
        EngineLog.ClearCompatibilityBuffer();

        var key = "phase13.test." + Guid.NewGuid().ToString("N");
        for (int i = 0; i < 500; i++)
        {
            EngineLog.WriteRateLimited(
                key,
                TimeSpan.FromSeconds(30),
                LogSubsystem.Paint,
                LogSeverity.Debug,
                "hot-path event");
        }

        var count = 0;
        foreach (var entry in EngineLog.GetCompatibilityRecentEntries())
        {
            if (entry.Message == "hot-path event")
            {
                count++;
            }
        }

        Assert.Equal(1, count);
    }
}
