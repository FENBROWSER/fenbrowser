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

        var observedRequest = await childTask;
        Assert.Equal(request.RequestUri!.AbsoluteUri, observedRequest.Url);
        Assert.Equal("POST", observedRequest.Method);
        Assert.Equal("parity", observedRequest.Headers!["X-Fen-Request"]);
        Assert.Equal("request-body", Encoding.UTF8.GetString(Convert.FromBase64String(observedRequest.BodyBase64!)));
        Assert.Equal("https://fixture.test", observedRequest.InitiatorOrigin);

        Assert.Equal(201, (int)response.StatusCode);
        Assert.Equal("Created", response.ReasonPhrase);
        Assert.Equal("response-body", await response.Content.ReadAsStringAsync());
        Assert.Equal("fixture", response.Headers.GetValues("X-Fen-Response").Single());
        Assert.Equal("text/plain", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task CrossOriginResponse_IsRejectedByCapabilityLock()
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
        var exception = await Assert.ThrowsAsync<HttpRequestException>(() =>
            coordinator.SendAsync(
                request,
                initiatorOrigin: "https://fixture.test",
                cancellation.Token));

        Assert.Contains("capability token validation", exception.Message, StringComparison.Ordinal);
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
        using var coordinator = new NetworkProcessCoordinator(maxBodyBytes: 8);

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

        Assert.Contains("maximum allowed size", exception.Message, StringComparison.Ordinal);
        await childTask;
    }

    [Fact]
    public async Task MalformedResponseBodyBase64_IsRejected()
    {
        var pipeName = $"fen_network_test_{Guid.NewGuid():N}";
        var authToken = Guid.NewGuid().ToString("N");
        using var session = new NetworkProcessSession(pipeName, authToken);
        using var coordinator = new NetworkProcessCoordinator();

        session.Start(childProcess: null);
        var childTask = RunDeterministicChildAsync(
            pipeName,
            authToken,
            malformedBodyBase64: "not-valid-base64!");
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

        Assert.Contains("malformed", exception.Message, StringComparison.OrdinalIgnoreCase);
        await childTask;
    }

    private static async Task<NetworkFetchRequestPayload> RunDeterministicChildAsync(
        string pipeName,
        string expectedAuthToken,
        string? responseUrl = null,
        string? responseCapabilityToken = null,
        string? payloadRequestId = null,
        string? failureErrorCode = null,
        IReadOnlyList<string>? responseBodyChunks = null,
        string? malformedBodyBase64 = null)
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
            return payload;
        }

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
                ContentLength = "response-body".Length,
                Headers = new Dictionary<string, string>
                {
                    ["X-Fen-Response"] = "fixture",
                    ["Content-Type"] = "text/plain; charset=utf-8"
                }
            })
        });
        var bodyChunks = responseBodyChunks ?? ["response-body"];
        for (var chunkIndex = 0; chunkIndex < bodyChunks.Count; chunkIndex++)
        {
            var bodyChunk = bodyChunks[chunkIndex];
            await WriteEnvelopeAsync(writer, new NetworkIpcEnvelope
            {
                Type = NetworkIpcMessageType.FetchResponseBody.ToString(),
                RequestId = fetch.RequestId,
                CapabilityToken = responseCapabilityToken ?? fetch.CapabilityToken,
                Payload = NetworkIpc.SerializePayload(new NetworkFetchResponseBodyPayload
                {
                    RequestId = payloadRequestId ?? fetch.RequestId,
                    IsComplete = chunkIndex == bodyChunks.Count - 1,
                    ChunkIndex = chunkIndex,
                    BodyChunkBase64 = malformedBodyBase64 ?? Convert.ToBase64String(Encoding.UTF8.GetBytes(bodyChunk)),
                    BytesTotal = bodyChunks.Sum(static chunk => Encoding.UTF8.GetByteCount(chunk))
                })
            });
        }

        return payload;
    }

    private static NetworkIpcEnvelope ReadEnvelope(string? line)
    {
        Assert.True(NetworkIpc.TryDeserialize(line ?? string.Empty, out var envelope));
        return Assert.IsType<NetworkIpcEnvelope>(envelope);
    }

    private static Task WriteEnvelopeAsync(StreamWriter writer, NetworkIpcEnvelope envelope) =>
        writer.WriteLineAsync(NetworkIpc.Serialize(envelope));
}
