using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using FenBrowser.Host.ProcessIsolation;
using FenBrowser.Host.Tabs;
using Xunit;

namespace FenBrowser.Tests.ProcessIsolation;

[Collection(BrokeredRendererProcessCollection.Name)]
public sealed class BrokeredRendererProcessAcceptanceTests
{
    [Fact(Skip = "BLOCKED_NEEDS_HUMAN_DECISION: select installer/runtime ACL provisioning for the RendererMinimal AppContainer before enabling this real-process acceptance.")]
    public async Task BrokeredRenderer_AuthenticatesNavigatesAndPublishesSharedMemoryFrame()
    {
        using var environment = new EnvironmentScope(new Dictionary<string, string?>
        {
            ["FEN_AUTO_START_TARGET_PROCESSES"] = "0",
            ["FEN_RENDERER_PROCESS_POOL"] = "0",
            ["FEN_RENDERER_MAX_RESTARTS"] = "0",
            ["FEN_RENDERER_READY_TIMEOUT_MS"] = "3000",
            ["FEN_RENDERER_ALLOW_UNSANDBOXED"] = null
        });

        var metadataReady = new TaskCompletionSource<RendererMetadataChangedPayload>(TaskCreationOptions.RunContinuationsAsynchronously);
        var frameReady = new TaskCompletionSource<RendererFrameReadyPayload>(TaskCreationOptions.RunContinuationsAsynchronously);
        var startupFailure = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var coordinator = new BrokeredProcessIsolationCoordinator();
        var tab = new BrowserTab();

        coordinator.MetadataChanged += (tabId, payload) =>
        {
            if (tabId == tab.Id && string.Equals(payload?.Title, "brokered-ready", StringComparison.Ordinal))
            {
                metadataReady.TrySetResult(payload);
            }
        };
        coordinator.FrameReceived += (tabId, payload) =>
        {
            if (tabId == tab.Id && payload?.PixelData is { Length: > 0 })
            {
                frameReady.TrySetResult(payload);
            }
        };
        coordinator.RendererCrashed += (tabId, reason) =>
        {
            if (tabId == tab.Id)
            {
                startupFailure.TrySetResult(reason);
            }
        };

        try
        {
            coordinator.Initialize();
            coordinator.OnTabCreated(tab);
            await AwaitSessionReadyAsync(
                coordinator,
                tab.Id,
                startupFailure.Task,
                TimeSpan.FromSeconds(8));

            const string fixture = "data:text/html,%3Ctitle%3Ebrokered-ready%3C%2Ftitle%3E%3Cmain%20style%3D%27background%3A%230b7%3Bwidth%3A160px%3Bheight%3A80px%27%3Ebrokered%3C%2Fmain%3E";
            coordinator.OnNavigationRequested(tab, fixture, isUserInput: false);

            var metadata = await AwaitOrFailAsync(
                metadataReady.Task,
                startupFailure.Task,
                TimeSpan.FromSeconds(8),
                () => DescribeSession(coordinator, tab.Id));
            Assert.Equal("brokered-ready", metadata.Title);
            Assert.StartsWith("data:text/html,", metadata.Url, StringComparison.OrdinalIgnoreCase);

            coordinator.OnFrameRequested(tab, 320, 200);
            var frame = await AwaitOrFailAsync(
                frameReady.Task,
                startupFailure.Task,
                TimeSpan.FromSeconds(8),
                () => DescribeSession(coordinator, tab.Id));

            Assert.Equal(320, frame.SurfaceWidth);
            Assert.True(frame.SurfaceHeight >= 200);
            Assert.NotNull(frame.PixelData);
            Assert.Equal((int)(frame.SurfaceWidth * frame.SurfaceHeight * 4), frame.PixelData!.Length);
            Assert.True(frame.FrameSequenceNumber > 0);
        }
        finally
        {
            coordinator.OnTabClosed(tab);
            coordinator.Shutdown();
        }
    }

    private static async Task AwaitSessionReadyAsync(
        BrokeredProcessIsolationCoordinator coordinator,
        int tabId,
        Task<string> failure,
        TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (failure.IsCompleted)
            {
                throw new Xunit.Sdk.XunitException(
                    $"Renderer child failed before authenticated startup completed: {await failure}; {DescribeSession(coordinator, tabId)}");
            }

            if (coordinator.TryGetSessionSnapshot(tabId, out var snapshot) &&
                snapshot.ProcessId > 0 &&
                !snapshot.HasExited &&
                string.IsNullOrWhiteSpace(snapshot.LastStartupFailure))
            {
                return;
            }

            await Task.WhenAny(Task.Delay(50), failure);
        }

        throw new Xunit.Sdk.XunitException(
            $"Renderer child authenticated startup timed out after {timeout.TotalSeconds:F0} seconds; {DescribeSession(coordinator, tabId)}");
    }

    private static async Task<T> AwaitOrFailAsync<T>(
        Task<T> success,
        Task<string> failure,
        TimeSpan timeout,
        Func<string> describeSession)
    {
        var timeoutTask = Task.Delay(timeout);
        var completed = await Task.WhenAny(success, failure, timeoutTask);
        if (completed == success)
        {
            return await success;
        }

        if (completed == failure)
        {
            var sessionDescription = describeSession();
            throw new Xunit.Sdk.XunitException(
                $"Renderer child failed before acceptance completed: {await failure}; {sessionDescription}");
        }

        throw new Xunit.Sdk.XunitException($"Renderer child acceptance timed out after {timeout.TotalSeconds:F0} seconds.");
    }

    private static string DescribeSession(BrokeredProcessIsolationCoordinator coordinator, int tabId)
    {
        return coordinator.TryGetSessionSnapshot(tabId, out var snapshot)
            ? $"pid={snapshot.ProcessId}, exited={snapshot.HasExited}, exitCode={snapshot.ExitCode?.ToString() ?? "n/a"}, sandbox={snapshot.SandboxProfile}, startup={snapshot.LastStartupFailure}"
            : "session-snapshot=missing";
    }

    private sealed class EnvironmentScope : IDisposable
    {
        private readonly Dictionary<string, string?> _previous = new(StringComparer.Ordinal);

        public EnvironmentScope(IReadOnlyDictionary<string, string?> values)
        {
            foreach (var pair in values)
            {
                _previous[pair.Key] = Environment.GetEnvironmentVariable(pair.Key);
                Environment.SetEnvironmentVariable(pair.Key, pair.Value);
            }
        }

        public void Dispose()
        {
            foreach (var pair in _previous)
            {
                Environment.SetEnvironmentVariable(pair.Key, pair.Value);
            }
        }
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class BrokeredRendererProcessCollection
{
    public const string Name = "Brokered renderer process";
}
