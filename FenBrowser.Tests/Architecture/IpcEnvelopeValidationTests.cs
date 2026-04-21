using System;
using FenBrowser.Host.ProcessIsolation;
using FenBrowser.Host.ProcessIsolation.Network;
using FenBrowser.Host.ProcessIsolation.Targets;
using Xunit;

namespace FenBrowser.Tests.Architecture;

public class IpcEnvelopeValidationTests
{
    [Fact]
    public void RendererIpc_ValidEnvelope_IsAccepted()
    {
        var envelope = new RendererIpcEnvelope
        {
            Type = RendererIpcMessageType.Ready.ToString(),
            TabId = 7,
            CorrelationId = Guid.NewGuid().ToString("N"),
            TimestampUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };

        var ok = RendererIpc.TryValidateInboundEnvelope(
            envelope,
            expectedTabId: 7,
            out var messageType,
            out var rejectionReason);

        Assert.True(ok);
        Assert.Equal(RendererIpcMessageType.Ready, messageType);
        Assert.True(string.IsNullOrEmpty(rejectionReason));
    }

    [Fact]
    public void RendererIpc_WrongTab_IsRejected()
    {
        var envelope = new RendererIpcEnvelope
        {
            Type = RendererIpcMessageType.FrameReady.ToString(),
            TabId = 4,
            CorrelationId = Guid.NewGuid().ToString("N"),
            TimestampUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };

        var ok = RendererIpc.TryValidateInboundEnvelope(
            envelope,
            expectedTabId: 5,
            out _,
            out var rejectionReason);

        Assert.False(ok);
        Assert.Equal("tab-mismatch", rejectionReason);
    }

    [Fact]
    public void RendererIpc_InvalidCorrelation_IsRejected()
    {
        var envelope = new RendererIpcEnvelope
        {
            Type = RendererIpcMessageType.Ready.ToString(),
            TabId = 2,
            CorrelationId = "not-a-guid",
            TimestampUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };

        var ok = RendererIpc.TryValidateInboundEnvelope(
            envelope,
            expectedTabId: 2,
            out _,
            out var rejectionReason);

        Assert.False(ok);
        Assert.Equal("correlation-invalid", rejectionReason);
    }

    [Fact]
    public void NetworkIpc_InvalidRequestId_IsRejected()
    {
        var envelope = new NetworkIpcEnvelope
        {
            Type = NetworkIpcMessageType.FetchRequest.ToString(),
            RequestId = "not-a-guid",
            CapabilityToken = "token",
            TimestampUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };

        var ok = NetworkIpc.TryValidateInboundEnvelope(envelope, out _, out var rejectionReason);

        Assert.False(ok);
        Assert.Equal("requestid-invalid", rejectionReason);
    }

    [Fact]
    public void NetworkIpc_ValidEnvelope_IsAccepted()
    {
        var envelope = new NetworkIpcEnvelope
        {
            Type = NetworkIpcMessageType.Ping.ToString(),
            RequestId = Guid.NewGuid().ToString("N"),
            TimestampUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };

        var ok = NetworkIpc.TryValidateInboundEnvelope(envelope, out var messageType, out var rejectionReason);

        Assert.True(ok);
        Assert.Equal(NetworkIpcMessageType.Ping, messageType);
        Assert.True(string.IsNullOrEmpty(rejectionReason));
    }

    [Fact]
    public void TargetIpc_TooLargePayload_IsRejected()
    {
        var envelope = new TargetIpcEnvelope
        {
            Type = TargetIpcMessageType.Ready.ToString(),
            RequestId = Guid.NewGuid().ToString("N"),
            Payload = new string('x', 100_000),
            TimestampUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };

        var ok = TargetIpc.TryValidateInboundEnvelope(envelope, out _, out var rejectionReason);

        Assert.False(ok);
        Assert.Equal("payload-too-large", rejectionReason);
    }
}
