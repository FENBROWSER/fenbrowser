using System.IO.Pipes;
using System.Text;
using FenBrowser.Host.ProcessIsolation.Network;

namespace FenBrowser.Tests.ProcessIsolation;

public sealed class NetworkProcessCoordinatorTests
{
    [Fact]
    public async Task ChildResponse_RetainsRequestIdentityAndHttpSemantics()
    {
        var pipeName = $"fen_network_test_{Guid.NewGuid():N}";
        var authToken = Guid.NewGuid().ToString("N");
        using var session = new NetworkProcessSession(pipeName, authToken);
        using var coordinator = new NetworkProcessCoordinator();

        session.Start(childProcess: null);
        var childTask = RunDeterministicChildAsync(pipeName, authToken);
        Assert.True(await session.WaitForReadyAsync(TimeSpan.FromSeconds(5)));
        coordinator.AttachSession(session);

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "https://fixture.test/network/parity?value=1");
        request.Headers.TryAddWithoutValidation("X-Fen-Request", "parity");
        request.Content = new StringContent("request-body", Encoding.UTF8, "text/plain");

        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        using var response = await coordinator.SendAsync(
            request,
            initiatorOrigin: "https://fixture.test",
            cancellation.Token);

        // Read the body before awaiting the fake child: its return closes the control
        // pipe, which the session treats as the network process going away.
        var body = await response.Content.ReadAsStringAsync(cancellation.Token);

        var observedRequest = await childTask;
        Assert.Equal(request.RequestUri!.AbsoluteUri, observedRequest.Payload.Url);
        Assert.Equal("POST", observedRequest.Payload.Method);
        Assert.Equal("parity", observedRequest.Payload.Headers!["X-Fen-Request"]);
        Assert.Equal("request-body", observedRequest.Body);
        Assert.Equal("https://fixture.test", observedRequest.Payload.InitiatorOrigin);

        Assert.Equal(201, (int)response.StatusCode);
        Assert.Equal("Created", response.ReasonPhrase);
        Assert.Equal("response-body", body);
        Assert.Equal("fixture", response.Headers.GetValues("X-Fen-Response").Single());
        Assert.Equal("text/plain", response.Content.Headers.ContentType?.MediaType);
    }

    // A renderer's fetch goes renderer -> broker (RendererNetworkClient/RendererNetworkRelay)
    // -> network child (NetworkProcessCoordinator). A form POST has to survive both hops as
    // the server would see it from Chrome: the body, its Content-Length (it went out
    // chunked, which servers commonly refuse for a login form) and headers such as
    // User-Agent, whose parts were re-joined with commas.
    [Fact]
    public async Task RendererFetchWithBody_ReachesTheNetworkChildThroughTheBroker()
    {
        var pipeName = $"fen_network_test_{Guid.NewGuid():N}";
        var authToken = Guid.NewGuid().ToString("N");
        using var session = new NetworkProcessSession(pipeName, authToken);
        using var coordinator = new NetworkProcessCoordinator();
        session.Start(childProcess: null);
        var childTask = RunDeterministicChildAsync(pipeName, authToken);
        Assert.True(await session.WaitForReadyAsync(TimeSpan.FromSeconds(5)));
        coordinator.AttachSession(session);

        var coordinatorProperty = typeof(FenBrowser.Host.ProcessIsolation.ProcessIsolationRuntime)
            .GetProperty(nameof(FenBrowser.Host.ProcessIsolation.ProcessIsolationRuntime.NetworkCoordinator))!;
        var previousCoordinator = coordinatorProperty.GetValue(null);
        coordinatorProperty.SetValue(null, coordinator);

        RendererNetworkClient? client = null;
        using var relay = new RendererNetworkRelay(tabId: 7, envelope =>
        {
            if (envelope.Type == nameof(FenBrowser.Host.ProcessIsolation.RendererIpcMessageType.NetworkFetchBodyPipe)) client!.OnBodyPipe(envelope);
            else if (envelope.Type == nameof(FenBrowser.Host.ProcessIsolation.RendererIpcMessageType.NetworkFetchResponseHead)) client!.OnResponseHead(envelope);
            else if (envelope.Type == nameof(FenBrowser.Host.ProcessIsolation.RendererIpcMessageType.NetworkFetchFailed)) client!.OnFailed(envelope);
        });
        client = new RendererNetworkClient(tabId: 7, envelope =>
        {
            if (envelope.Type == nameof(FenBrowser.Host.ProcessIsolation.RendererIpcMessageType.NetworkFetch)) relay.HandleFetch(envelope);
            else if (envelope.Type == nameof(FenBrowser.Host.ProcessIsolation.RendererIpcMessageType.NetworkFetchCancel)) relay.HandleCancel(envelope);
        });

        try
        {
            const string userAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/146.0.0.0 Safari/537.36";
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://fixture.test/session");
            request.Headers.TryAddWithoutValidation("User-Agent", userAgent);
            request.Headers.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");
            request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["login"] = "alice",
                ["password"] = "secret"
            });

            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var response = await client.SendAsync(request, cancellation.Token);
            await response.Content.ReadAsStringAsync(cancellation.Token);

            var observed = await childTask.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("POST", observed.Payload.Method);
            Assert.Equal("login=alice&password=secret", observed.Body);
            Assert.Equal("27", observed.Payload.Headers!["Content-Length"]);
            Assert.Equal(userAgent, observed.Payload.Headers["User-Agent"]);
            Assert.Equal("en-US,en;q=0.9", observed.Payload.Headers["Accept-Language"]);
        }
        finally
        {
            client.Dispose();
            coordinatorProperty.SetValue(null, previousCoordinator);
        }
    }

    // The per-request capability token authenticates a response (5dba7747); its URL
    // may be on another origin, as it is after a cross-origin redirect, and origin
    // policy is left to CORS rather than the IPC layer.
    [Fact]
    public async Task CrossOriginResponseUrl_IsAcceptedWithValidCapability()
    {
        var pipeName = $"fen_network_test_{Guid.NewGuid():N}";
        var authToken = Guid.NewGuid().ToString("N");
        using var session = new NetworkProcessSession(pipeName, authToken);
        using var coordinator = new NetworkProcessCoordinator();

        session.Start(childProcess: null);
        var childTask = RunDeterministicChildAsync(
            pipeName,
            authToken,
            responseUrl: "https://other.test/network/parity");
        Assert.True(await session.WaitForReadyAsync(TimeSpan.FromSeconds(5)));
        coordinator.AttachSession(session);

        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            "https://fixture.test/network/parity");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        using var response = await coordinator.SendAsync(
            request,
            initiatorOrigin: "https://fixture.test",
            cancellation.Token);

        Assert.Equal(201, (int)response.StatusCode);
        await childTask;
    }

    [Fact]
    public async Task InvalidResponseCapabilityToken_IsRejected()
    {
        var pipeName = $"fen_network_test_{Guid.NewGuid():N}";
        var authToken = Guid.NewGuid().ToString("N");
        using var session = new NetworkProcessSession(pipeName, authToken);
        using var coordinator = new NetworkProcessCoordinator();

        session.Start(childProcess: null);
        var childTask = RunDeterministicChildAsync(
            pipeName,
            authToken,
            responseCapabilityToken: "invalid-capability-token");
        Assert.True(await session.WaitForReadyAsync(TimeSpan.FromSeconds(5)));
        coordinator.AttachSession(session);

        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            "https://fixture.test/network/parity");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var exception = await Assert.ThrowsAsync<HttpRequestException>(() =>
            coordinator.SendAsync(
                request,
                initiatorOrigin: "https://fixture.test",
                cancellation.Token));

        Assert.Contains("invalid-capability-token", exception.Message, StringComparison.Ordinal);
        await childTask;
    }

    [Fact]
    public async Task MismatchedPayloadRequestId_IsRejected()
    {
        var pipeName = $"fen_network_test_{Guid.NewGuid():N}";
        var authToken = Guid.NewGuid().ToString("N");
        using var session = new NetworkProcessSession(pipeName, authToken);
        using var coordinator = new NetworkProcessCoordinator();

        session.Start(childProcess: null);
        var childTask = RunDeterministicChildAsync(
            pipeName,
            authToken,
            payloadRequestId: Guid.NewGuid().ToString("N"));
        Assert.True(await session.WaitForReadyAsync(TimeSpan.FromSeconds(5)));
        coordinator.AttachSession(session);

        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            "https://fixture.test/network/parity");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var exception = await Assert.ThrowsAsync<HttpRequestException>(() =>
            coordinator.SendAsync(
                request,
                initiatorOrigin: "https://fixture.test",
                cancellation.Token));

        Assert.Contains("response-requestid-mismatch", exception.Message, StringComparison.Ordinal);
        await childTask;
    }

    [Fact]
    public async Task MatchingCapabilityFetchFailure_IsPropagated()
    {
        var pipeName = $"fen_network_test_{Guid.NewGuid():N}";
        var authToken = Guid.NewGuid().ToString("N");
        using var session = new NetworkProcessSession(pipeName, authToken);
        using var coordinator = new NetworkProcessCoordinator();

        session.Start(childProcess: null);
        var childTask = RunDeterministicChildAsync(
            pipeName,
            authToken,
            failureErrorCode: "fixture-failure");
        Assert.True(await session.WaitForReadyAsync(TimeSpan.FromSeconds(5)));
        coordinator.AttachSession(session);

        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            "https://fixture.test/network/parity");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var exception = await Assert.ThrowsAsync<HttpRequestException>(() =>
            coordinator.SendAsync(
                request,
                initiatorOrigin: "https://fixture.test",
                cancellation.Token));

        Assert.Contains("[fixture-failure]", exception.Message, StringComparison.Ordinal);
        await childTask;
    }

    [Fact]
    public async Task AggregateResponseBodyOverLimit_IsRejected()
    {
        var pipeName = $"fen_network_test_{Guid.NewGuid():N}";
        var authToken = Guid.NewGuid().ToString("N");
        using var session = new NetworkProcessSession(pipeName, authToken);
        using var coordinator = new NetworkProcessCoordinator(defaultResponseBodyLimit: 8);

        session.Start(childProcess: null);
        var childTask = RunDeterministicChildAsync(
            pipeName,
            authToken,
            responseBodyChunks: ["12345", "67890"]);
        Assert.True(await session.WaitForReadyAsync(TimeSpan.FromSeconds(5)));
        coordinator.AttachSession(session);

        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            "https://fixture.test/network/parity");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var exception = await Assert.ThrowsAsync<HttpRequestException>(() =>
            coordinator.SendAsync(
                request,
                initiatorOrigin: "https://fixture.test",
                cancellation.Token));

        Assert.Contains("destination limit", exception.Message, StringComparison.Ordinal);
        await childTask;
    }

    [Fact]
    public async Task ChunkedResponseBody_PreservesBinaryWireOrder()
    {
        var pipeName = $"fen_network_test_{Guid.NewGuid():N}";
        var authToken = Guid.NewGuid().ToString("N");
        using var session = new NetworkProcessSession(pipeName, authToken);
        using var coordinator = new NetworkProcessCoordinator();

        session.Start(childProcess: null);
        var childTask = RunDeterministicChildAsync(
            pipeName,
            authToken,
            responseBodyChunks: ["first", "second", "third"]);
        Assert.True(await session.WaitForReadyAsync(TimeSpan.FromSeconds(5)));
        coordinator.AttachSession(session);

        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            "https://fixture.test/network/parity");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        using var response = await coordinator.SendAsync(
            request,
            initiatorOrigin: "https://fixture.test",
            cancellation.Token);

        Assert.Equal("firstsecondthird", await response.Content.ReadAsStringAsync(cancellation.Token));
        await childTask;
    }

    [Fact]
    public async Task ChildDisconnectDuringFetch_FailsAsNetworkError()
    {
        var pipeName = $"fen_network_test_{Guid.NewGuid():N}";
        var authToken = Guid.NewGuid().ToString("N");
        using var session = new NetworkProcessSession(pipeName, authToken);
        using var coordinator = new NetworkProcessCoordinator();

        session.Start(childProcess: null);
        var childTask = RunDeterministicChildAsync(
            pipeName,
            authToken,
            disconnectAfterFetch: true);
        Assert.True(await session.WaitForReadyAsync(TimeSpan.FromSeconds(5)));
        coordinator.AttachSession(session);

        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            "https://fixture.test/network/parity");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var exception = await Assert.ThrowsAsync<HttpRequestException>(() =>
            coordinator.SendAsync(
                request,
                initiatorOrigin: "https://fixture.test",
                cancellation.Token));

        Assert.Contains("disconnected", exception.Message, StringComparison.OrdinalIgnoreCase);
        await childTask;
    }

    [Fact]
    public async Task CallerCancellation_SendsCancelForWireRequestId()
    {
        var pipeName = $"fen_network_test_{Guid.NewGuid():N}";
        var authToken = Guid.NewGuid().ToString("N");
        using var session = new NetworkProcessSession(pipeName, authToken);
        using var coordinator = new NetworkProcessCoordinator();
        var fetchReceived = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        session.Start(childProcess: null);
        var childTask = RunDeterministicChildAsync(
            pipeName,
            authToken,
            fetchReceived: fetchReceived,
            waitForCancellation: true);
        Assert.True(await session.WaitForReadyAsync(TimeSpan.FromSeconds(5)));
        coordinator.AttachSession(session);

        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            "https://fixture.test/network/parity");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var sendTask = coordinator.SendAsync(
            request,
            initiatorOrigin: "https://fixture.test",
            cancellation.Token);

        Assert.True(await fetchReceived.Task.WaitAsync(TimeSpan.FromSeconds(2)));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sendTask);
        await childTask;
    }

    [Fact]
    public async Task RequestWithoutContent_ProducesEmptyBodyFrameSequence()
    {
        var pipeName = $"fen_network_test_{Guid.NewGuid():N}";
        var authToken = Guid.NewGuid().ToString("N");
        using var session = new NetworkProcessSession(pipeName, authToken);
        using var coordinator = new NetworkProcessCoordinator();

        session.Start(childProcess: null);
        var childTask = RunDeterministicChildAsync(
            pipeName,
            authToken);
        Assert.True(await session.WaitForReadyAsync(TimeSpan.FromSeconds(5)));
        coordinator.AttachSession(session);

        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            "https://fixture.test/network/parity");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        using var response = await coordinator.SendAsync(
            request,
            initiatorOrigin: "https://fixture.test",
            cancellation.Token);

        var observed = await childTask;
        Assert.False(observed.Payload.HasBody);
        Assert.Equal(string.Empty, observed.Body);
    }

    private static async Task<ObservedNetworkRequest> RunDeterministicChildAsync(
        string pipeName,
        string expectedAuthToken,
        string? responseUrl = null,
        string? responseCapabilityToken = null,
        string? payloadRequestId = null,
        string? failureErrorCode = null,
        IReadOnlyList<string>? responseBodyChunks = null,
        bool disconnectAfterFetch = false,
        TaskCompletionSource<bool>? fetchReceived = null,
        bool waitForCancellation = false)
    {
        using var pipe = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);
        await pipe.ConnectAsync(5_000);
        using var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, leaveOpen: true);
        using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true)
        {
            AutoFlush = true
        };

        var hello = ReadEnvelope(await reader.ReadLineAsync());
        Assert.Equal(NetworkIpcMessageType.Hello.ToString(), hello.Type);
        Assert.Equal(expectedAuthToken, hello.CapabilityToken);

        await WriteEnvelopeAsync(writer, new NetworkIpcEnvelope
        {
            Type = NetworkIpcMessageType.Ready.ToString()
        });

        var fetch = ReadEnvelope(await reader.ReadLineAsync());
        Assert.Equal(NetworkIpcMessageType.FetchRequest.ToString(), fetch.Type);
        Assert.False(string.IsNullOrWhiteSpace(fetch.RequestId));
        Assert.False(string.IsNullOrWhiteSpace(fetch.CapabilityToken));
        var payload = Assert.IsType<NetworkFetchRequestPayload>(
            NetworkIpc.DeserializePayload<NetworkFetchRequestPayload>(fetch));
        await using var bodyPipe = await NetworkBodyPipe.ConnectClientAsync(
            payload.BodyPipeName,
            payload.BodyPipeToken,
            CancellationToken.None);
        await using var requestBody = bodyPipe.OpenReadStream(1024 * 1024);
        using var requestBytes = new MemoryStream();
        await requestBody.CopyToAsync(requestBytes);
        var observed = new ObservedNetworkRequest(
            payload,
            Encoding.UTF8.GetString(requestBytes.ToArray()));
        fetchReceived?.TrySetResult(true);

        if (waitForCancellation)
        {
            var cancelLine = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(2));
            var cancel = ReadEnvelope(cancelLine);
            Assert.Equal(NetworkIpcMessageType.CancelRequest.ToString(), cancel.Type);
            Assert.Equal(fetch.RequestId, cancel.RequestId);
            return observed;
        }

        if (disconnectAfterFetch)
        {
            return observed;
        }

        if (!string.IsNullOrWhiteSpace(failureErrorCode))
        {
            await WriteEnvelopeAsync(writer, new NetworkIpcEnvelope
            {
                Type = NetworkIpcMessageType.FetchFailed.ToString(),
                RequestId = fetch.RequestId,
                CapabilityToken = fetch.CapabilityToken,
                Payload = NetworkIpc.SerializePayload(new NetworkFetchFailedPayload
                {
                    RequestId = fetch.RequestId,
                    ErrorCode = failureErrorCode,
                    ErrorMessage = "deterministic child failure"
                })
            });
            return observed;
        }

        var bodyChunks = responseBodyChunks ?? ["response-body"];
        var responseBytes = Encoding.UTF8.GetBytes(string.Concat(bodyChunks));
        await WriteEnvelopeAsync(writer, new NetworkIpcEnvelope
        {
            Type = NetworkIpcMessageType.FetchResponseHead.ToString(),
            RequestId = fetch.RequestId,
            CapabilityToken = responseCapabilityToken ?? fetch.CapabilityToken,
            Payload = NetworkIpc.SerializePayload(new NetworkFetchResponseHeadPayload
            {
                RequestId = payloadRequestId ?? fetch.RequestId,
                StatusCode = 201,
                StatusText = "Created",
                Url = responseUrl ?? payload.Url,
                ResponseType = "basic",
                ContentLength = responseBytes.Length,
                Headers = new Dictionary<string, string>
                {
                    ["X-Fen-Response"] = "fixture",
                    ["Content-Type"] = "text/plain; charset=utf-8"
                }
            })
        });

        if (responseUrl != null || responseCapabilityToken != null || payloadRequestId != null)
        {
            return observed;
        }

        try
        {
            using var responseStream = new MemoryStream(responseBytes, writable: false);
            await bodyPipe.SendStreamAsync(responseStream, CancellationToken.None);
        }
        catch (IOException)
        {
        }

        return observed;
    }

    private static NetworkIpcEnvelope ReadEnvelope(string? line)
    {
        Assert.True(NetworkIpc.TryDeserialize(line ?? string.Empty, out var envelope));
        return Assert.IsType<NetworkIpcEnvelope>(envelope);
    }

    private static Task WriteEnvelopeAsync(StreamWriter writer, NetworkIpcEnvelope envelope) =>
        writer.WriteLineAsync(NetworkIpc.Serialize(envelope));

    private sealed record ObservedNetworkRequest(NetworkFetchRequestPayload Payload, string Body);
}
