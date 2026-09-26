using System;
using Xunit;
using FenBrowser.FenEngine.Adapters;
using FenBrowser.Host.ProcessIsolation.Targets;

namespace FenBrowser.Tests.Architecture
{
    /// <summary>
    /// Compliance tests for Rule 3: SVG Sandboxing.
    /// Verifies SvgRenderLimits are enforced.
    /// </summary>
    public class SvgSandboxingTests
    {
        [Fact]
        public void SvgRenderLimits_Default_HasSafeValues()
        {
            // Arrange
            var limits = SvgRenderLimits.Default;
            
            // Assert
            Assert.Equal(32, limits.MaxRecursionDepth);
            Assert.Equal(16, limits.MaxFilterCount);
            Assert.Equal(250, limits.MaxRenderTimeMs);
            Assert.False(limits.AllowExternalReferences);
        }
        
        [Fact]
        public void SvgRenderLimits_Strict_IsMoreRestrictive()
        {
            // Arrange
            var limits = SvgRenderLimits.Strict;
            
            // Assert
            Assert.True(limits.MaxRecursionDepth <= 16);
            Assert.True(limits.MaxFilterCount <= 5);
            Assert.True(limits.MaxRenderTimeMs <= 50);
            Assert.False(limits.AllowExternalReferences);
        }
        
        [Fact]
        public void TargetIpc_SvgDecodeResponseMetadata_IsBoundedBeforeSerialization()
        {
            // Arrange - renderer diagnostics that reached the transport unbounded.
            var payload = new SvgDecodeResponsePayload
            {
                Success = false,
                ErrorMessage = new string('e', 400_000),
                BitmapBytes = Array.Empty<byte>()
            };

            // Assert - metadata is bounded at the payload itself.
            Assert.InRange(payload.ErrorMessage.Length, 1, TargetIpc.MaxResponseMetadataChars);

            var envelope = new TargetIpcEnvelope
            {
                Type = TargetIpcMessageType.SvgDecodeResponse.ToString(),
                RequestId = Guid.NewGuid().ToString("N"),
                Payload = TargetIpc.SerializePayload(payload),
                TimestampUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            };

            Assert.InRange(envelope.Payload.Length, 1, 100_000);

            var accepted = TargetIpc.TryValidateInboundEnvelope(envelope, out var messageType, out var reason);
            Assert.True(accepted, reason);
            Assert.Equal(TargetIpcMessageType.SvgDecodeResponse, messageType);

            var decoded = TargetIpc.DeserializePayload<SvgDecodeResponsePayload>(envelope);
            Assert.NotNull(decoded);
            Assert.InRange(decoded.ErrorMessage.Length, 1, TargetIpc.MaxResponseMetadataChars);
        }

        [Fact]
        public void TargetIpc_ImageDecodeResponseMetadata_IsBounded()
        {
            var payload = new ImageDecodeResponsePayload
            {
                Success = true,
                ErrorMessage = new string('e', 200_000),
                Format = new string('p', 4_000),
                BitmapBytes = new byte[] { 1, 2, 3 }
            };

            Assert.InRange(payload.ErrorMessage.Length, 1, TargetIpc.MaxResponseMetadataChars);
            Assert.InRange(payload.Format.Length, 1, TargetIpc.MaxFormatMetadataChars);
            Assert.InRange(TargetIpc.SerializePayload(payload).Length, 1, 100_000);
        }

        [Fact]
        public void TargetIpc_OversizedOutboundPayload_IsRejectedBeforeSerialization()
        {
            var envelope = new TargetIpcEnvelope
            {
                Type = TargetIpcMessageType.SvgDecodeResponse.ToString(),
                RequestId = Guid.NewGuid().ToString("N"),
                Payload = new string('x', 100_000),
                TimestampUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            };

            var ok = TargetIpc.TrySerializeEnvelope(envelope, out var line, out var rejectionReason);

            Assert.False(ok);
            Assert.Null(line);
            Assert.Equal("payload-too-large", rejectionReason);
        }

        [Fact]
        public void TargetIpc_BoundedOutboundEnvelope_RoundTrips()
        {
            var envelope = new TargetIpcEnvelope
            {
                Type = TargetIpcMessageType.SvgDecodeResponse.ToString(),
                RequestId = Guid.NewGuid().ToString("N"),
                Payload = TargetIpc.SerializePayload(new SvgDecodeResponsePayload
                {
                    Success = false,
                    ErrorMessage = "SVG source length (9000000) exceeds limit (8000000)",
                    BitmapBytes = Array.Empty<byte>()
                }),
                TimestampUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            };

            var ok = TargetIpc.TrySerializeEnvelope(envelope, out var line, out var rejectionReason);

            Assert.True(ok, rejectionReason);
            Assert.NotNull(line);
            Assert.True(TargetIpc.TryDeserialize(line, out var decoded));
            Assert.Equal(TargetIpcMessageType.SvgDecodeResponse.ToString(), decoded.Type);
            var payload = TargetIpc.DeserializePayload<SvgDecodeResponsePayload>(decoded);
            Assert.Equal("SVG source length (9000000) exceeds limit (8000000)", payload.ErrorMessage);
        }
    }
}
