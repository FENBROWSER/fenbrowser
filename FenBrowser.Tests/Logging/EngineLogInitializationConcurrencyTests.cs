using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using FenBrowser.Core.Logging;
using Xunit;

namespace FenBrowser.Tests.Logging;

[Collection(EngineLogTestCollection.Name)]
public sealed class EngineLogInitializationConcurrencyTests
{
    [Fact]
    public async Task ConcurrentFirstAccessPublishesOneStableLoggerInstance()
    {
        const int callerCount = 24;

        try
        {
            for (var round = 0; round < 10; round++)
            {
                ResetLoggerForFirstAccess();
                using var start = new ManualResetEventSlim(false);
                using var ready = new CountdownEvent(callerCount);
                var observed = new ConcurrentBag<IEngineLogger>();

                Task[] callers = Enumerable.Range(0, callerCount)
                    .Select(_ => Task.Run(() =>
                    {
                        ready.Signal();
                        start.Wait();
                        observed.Add(EngineLog.Current);
                    }))
                    .ToArray();

                Assert.True(ready.Wait(TimeSpan.FromSeconds(10)), "Concurrent logger callers did not become ready.");
                start.Set();
                await Task.WhenAll(callers);

                Assert.Single(observed.Distinct(LoggerReferenceComparer.Instance));
            }
        }
        finally
        {
            EngineLog.Configure(new EngineLoggingOptions
            {
                Enabled = false,
                EnableConsoleSink = false,
                EnableDebugSink = false,
                EnableNdjsonSink = false,
                EnableRingBufferSink = false,
                EnableTraceSink = false
            });
            EngineLog.ClearCompatibilityBuffer();
        }
    }

    private static void ResetLoggerForFirstAccess()
    {
        FieldInfo loggerField = typeof(EngineLog).GetField("_logger", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("EngineLog._logger was not found.");

        if (loggerField.GetValue(null) is IDisposable current)
        {
            current.Dispose();
        }

        loggerField.SetValue(null, null);
        EngineLog.ClearCompatibilityBuffer();
    }

    private sealed class LoggerReferenceComparer : IEqualityComparer<IEngineLogger>
    {
        public static readonly LoggerReferenceComparer Instance = new();

        public bool Equals(IEngineLogger? x, IEngineLogger? y) => ReferenceEquals(x, y);

        public int GetHashCode(IEngineLogger obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
    }
}
