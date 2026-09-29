using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using FenBrowser.FenEngine.Adapters;
using FenBrowser.Host;
using FenBrowser.Host.ProcessIsolation.Targets;
using SkiaSharp;
using Xunit;

namespace FenBrowser.Tests.Svg;

/// <summary>
/// First-party-only coverage for the target-process SVG decode entry points.
/// <para>
/// The first-party engine is the only backend, so a fallback requirement, a
/// resource rejection, or a failed render is terminal. Both entry points must
/// apply the shared fail-closed admission predicate and return no bitmap and no
/// partial bytes for such a result, while clean first-party output still
/// crosses the IPC boundary intact.
/// </para>
/// <para>
/// Both entry points resolve the renderer through
/// <c>SvgRendererFactory.GetConfiguredRenderer()</c>, which reads the
/// process-wide backend selection, and the target-process image path shares the
/// ImageLoader caches. The class therefore runs inside
/// <see cref="SvgRendererBackendStateCollection"/> so a decode can never observe
/// another test's backend selection or image cache.
/// </para>
/// </summary>
[Collection(SvgRendererBackendStateCollection.Name)]
public sealed class TargetProcessSvgDecodeTests
{
    private const string CleanFirstPartySvg =
        "<svg xmlns=\"http://www.w3.org/2000/svg\" width='20' height='20'><rect width='20' height='20' fill='red'/></svg>";

    private const string UnsupportedFeatureSvg =
        "<svg xmlns=\"http://www.w3.org/2000/svg\" width='80' height='30'>" +
        "<text x='2' y='15' writing-mode='vertical-rl'>Fen</text></svg>";

    private static readonly string RejectedResourceSvg =
        "<svg xmlns=\"http://www.w3.org/2000/svg\" width='10' height='10'>" +
        "<image href='" + DataSvg(
            "<svg width='10' height='10'><foreignObject width='10' height='10'/></svg>") +
        "' width='10' height='10'/></svg>";

    [Fact]
    public void FirstPartyRenderer_UnsupportedFeature_IsNotAdmissible()
    {
        using var result = new FenSvgRenderer().Render(UnsupportedFeatureSvg);

        Assert.False(SvgRenderResult.IsAdmissible(result));
        Assert.Contains(
            "fallback",
            SvgRenderResult.DescribeRejection(result),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FirstPartyRenderer_RejectedResource_IsNotAdmissible()
    {
        using var result = new FenSvgRenderer().Render(RejectedResourceSvg);

        Assert.False(SvgRenderResult.IsAdmissible(result));
        Assert.Contains(
            "resource",
            SvgRenderResult.DescribeRejection(result),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FirstPartyRenderer_CleanDocument_IsAdmissible()
    {
        using var result = new FenSvgRenderer().Render(CleanFirstPartySvg);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.True(SvgRenderResult.IsAdmissible(result));
        Assert.Null(SvgRenderResult.DescribeRejection(result));
    }

    [Fact]
    public async Task SvgDecode_CleanFirstParty_ReturnsEncodedBitmap()
    {
        var response = await SendSvgDecodeAsync(CleanFirstPartySvg);

        Assert.True(response.Success, response.ErrorMessage);
        Assert.Null(response.ErrorMessage);
        Assert.NotEmpty(response.BitmapBytes);
        Assert.Equal(20, response.Width);
        Assert.Equal(20, response.Height);
        AssertPng(response.BitmapBytes);
    }

    [Fact]
    public async Task SvgDecode_UnsupportedFeature_ReturnsFailureWithoutBytes()
    {
        var response = await SendSvgDecodeAsync(UnsupportedFeatureSvg);

        AssertNoPartialBitmap(response);
        Assert.Contains("fallback", response.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SvgDecode_RejectedResource_ReturnsFailureWithoutBytes()
    {
        var response = await SendSvgDecodeAsync(RejectedResourceSvg);

        AssertNoPartialBitmap(response);
        Assert.Contains("resource", response.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ImageDecode_CleanFirstParty_ReturnsBitmap()
    {
        using SKBitmap bitmap = Program.DecodeImage(
            Encoding.UTF8.GetBytes(CleanFirstPartySvg),
            null,
            null,
            "https://example.test/assets/clean.svg");

        Assert.NotNull(bitmap);
        Assert.Equal(20, bitmap.Width);
        Assert.Equal(20, bitmap.Height);
    }

    [Fact]
    public void ImageDecode_UnsupportedFeature_ReturnsNoBitmap()
    {
        using SKBitmap bitmap = Program.DecodeImage(
            Encoding.UTF8.GetBytes(UnsupportedFeatureSvg),
            null,
            null,
            "https://example.test/assets/unsupported.svg");

        Assert.Null(bitmap);
    }

    [Fact]
    public void ImageDecode_RejectedResource_ReturnsNoBitmap()
    {
        using SKBitmap bitmap = Program.DecodeImage(
            Encoding.UTF8.GetBytes(RejectedResourceSvg),
            null,
            null,
            "data:image/svg+xml;base64,PHN2Zy8+");

        Assert.Null(bitmap);
    }

    [Fact]
    public void RejectSvgDecodeResult_AdmitsCleanFirstPartyPixels()
    {
        using var result = SuccessfulResult(requiresFallback: false);

        Assert.Null(Program.RejectSvgDecodeResult(result, "test"));
    }

    [Fact]
    public void RejectSvgDecodeResult_RejectsMissingResult()
    {
        string reason = Program.RejectSvgDecodeResult(null, "test");

        AssertBoundedReason(reason);
    }

    [Fact]
    public void RejectSvgDecodeResult_RejectsFailedResult()
    {
        using var result = new SvgRenderResult
        {
            Success = false,
            ErrorMessage = new string('e', 4096)
        };

        AssertBoundedReason(Program.RejectSvgDecodeResult(result, "test"));
    }

    [Fact]
    public void RejectSvgDecodeResult_RejectsFallbackRequirement()
    {
        using var result = SuccessfulResult(requiresFallback: true);

        string reason = Program.RejectSvgDecodeResult(result, "test");

        AssertBoundedReason(reason);
        Assert.Contains("fallback", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RejectSvgDecodeResult_RejectsResourceRejectionFlag()
    {
        using var result = SuccessfulResult(requiresFallback: false);
        result.HadResourceRejection = true;

        string reason = Program.RejectSvgDecodeResult(result, "test");

        AssertBoundedReason(reason);
        Assert.Contains("resource", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RejectSvgDecodeResult_RejectsResourceRejectionCodes()
    {
        using var result = SuccessfulResult(requiresFallback: false);
        result.ResourceRejectionReasonCodes = new[] { "external-reference-blocked" };

        AssertBoundedReason(Program.RejectSvgDecodeResult(result, "test"));
    }

    [Fact]
    public void RejectSvgDecodeResult_RejectsNonFirstPartyBackend()
    {
        using var result = SuccessfulResult(requiresFallback: false);
        result.Backend = (SvgRendererBackend)7;

        string reason = Program.RejectSvgDecodeResult(result, "test");

        Assert.False(SvgRenderResult.IsAdmissible(result));
        AssertBoundedReason(reason);
        Assert.Contains("backend", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RejectSvgDecodeResult_KeepsDiagnosticsBoundedAndSourceFree()
    {
        using var flagged = SuccessfulResult(requiresFallback: true);
        flagged.FallbackReasonCodes = new[] { new string('c', 4096), new string('d', 4096) };
        flagged.Warnings = new[] { new string('w', 4096) };
        using var failed = new SvgRenderResult
        {
            Success = false,
            ErrorMessage = new string('e', 4096)
        };

        string flaggedReason = Program.RejectSvgDecodeResult(flagged, "test");
        string failedReason = Program.RejectSvgDecodeResult(failed, "test");

        AssertBoundedReason(flaggedReason);
        AssertBoundedReason(failedReason);
        Assert.DoesNotContain(new string('c', 4096), flaggedReason, StringComparison.Ordinal);
        Assert.DoesNotContain(new string('d', 4096), flaggedReason, StringComparison.Ordinal);
        Assert.DoesNotContain(new string('w', 4096), flaggedReason, StringComparison.Ordinal);
        Assert.DoesNotContain(new string('e', 4096), failedReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SvgDecode_EmptyContent_TakesErrorPathWithBoundedMetadata()
    {
        var (line, response) = await SendSvgDecodeRawAsync("   ");

        AssertBoundedLine(line);
        Assert.NotNull(response);
        Assert.False(response.Success);
        AssertNoBitmapBytes(response);
        Assert.Equal(0, response.Width);
        Assert.Equal(0, response.Height);
        AssertBoundedReason(response.ErrorMessage);
    }

    [Fact]
    public async Task SvgDecode_MalformedPayload_TakesErrorPathWithBoundedMetadata()
    {
        var request = new TargetIpcEnvelope
        {
            Type = TargetIpcMessageType.SvgDecode.ToString(),
            RequestId = Guid.NewGuid().ToString("N"),
            Payload = "{not-json",
            TimestampUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };

        using var buffer = new MemoryStream();
        await using (var writer = new StreamWriter(buffer, new UTF8Encoding(false), 1024, leaveOpen: true))
        {
            await Program.HandleSvgDecodeRequest(writer, request);
            await writer.FlushAsync();
        }

        string line = FirstLine(Encoding.UTF8.GetString(buffer.ToArray()));
        AssertBoundedLine(line);
        Assert.True(TargetIpc.TryDeserialize(line, out var envelope), line);
        Assert.Equal(TargetIpcMessageType.SvgDecodeResponse.ToString(), envelope.Type);

        var payload = TargetIpc.DeserializePayload<SvgDecodeResponsePayload>(envelope);
        Assert.NotNull(payload);
        Assert.False(payload.Success);
        AssertNoBitmapBytes(payload);
        AssertBoundedReason(payload.ErrorMessage);
    }

    [Fact]
    public void SendErrorResponse_UnboundedExceptionMessage_IsBoundedOnTheWire()
    {
        using var buffer = new MemoryStream();
        using var writer = new StreamWriter(buffer, new UTF8Encoding(false), 1024, leaveOpen: true);

        Program.SendErrorResponse(
            writer,
            Guid.NewGuid().ToString("N"),
            TargetIpcMessageType.SvgDecodeResponse,
            new string('e', 512 * 1024));
        writer.Flush();

        string line = FirstLine(Encoding.UTF8.GetString(buffer.ToArray()));
        AssertBoundedLine(line);
        Assert.True(TargetIpc.TryDeserialize(line, out var envelope), line);
        Assert.Equal(TargetIpcMessageType.SvgDecodeResponse.ToString(), envelope.Type);

        var payload = TargetIpc.DeserializePayload<SvgDecodeResponsePayload>(envelope);
        Assert.NotNull(payload);
        Assert.False(payload.Success);
        AssertBoundedReason(payload.ErrorMessage);
        Assert.DoesNotContain(new string('e', 4096), line, StringComparison.Ordinal);
    }

    [Fact]
    public void SendTargetEnvelope_WithinBudget_RoundTripsThroughTheBoundedPath()
    {
        using var buffer = new MemoryStream();
        using var writer = new StreamWriter(buffer, new UTF8Encoding(false), 1024, leaveOpen: true);

        Program.SendTargetEnvelope(writer, new TargetIpcEnvelope
        {
            Type = TargetIpcMessageType.SvgDecodeResponse.ToString(),
            RequestId = Guid.NewGuid().ToString("N"),
            Payload = TargetIpc.SerializePayload(new SvgDecodeResponsePayload
            {
                Success = false,
                ErrorMessage = "SVG decode rejected",
                BitmapBytes = Array.Empty<byte>()
            })
        });
        writer.Flush();

        string line = FirstLine(Encoding.UTF8.GetString(buffer.ToArray()));
        AssertBoundedLine(line);
        Assert.True(TargetIpc.TryDeserialize(line, out var envelope), line);
        Assert.Equal(TargetIpcMessageType.SvgDecodeResponse.ToString(), envelope.Type);
    }

    [Fact]
    public void SendTargetEnvelope_OversizedPayload_EmitsNoLine()
    {
        using var buffer = new MemoryStream();
        using var writer = new StreamWriter(buffer, new UTF8Encoding(false), 1024, leaveOpen: true);

        Program.SendTargetEnvelope(writer, new TargetIpcEnvelope
        {
            Type = TargetIpcMessageType.SvgDecodeResponse.ToString(),
            RequestId = Guid.NewGuid().ToString("N"),
            Payload = new string('x', 512 * 1024)
        });
        writer.Flush();

        Assert.Equal(string.Empty, Encoding.UTF8.GetString(buffer.ToArray()));
    }

    [Fact]
    public void SendTargetEnvelope_OversizedPayload_DoesNotBlockBoundedErrorResponse()
    {
        using var buffer = new MemoryStream();
        using var writer = new StreamWriter(buffer, new UTF8Encoding(false), 1024, leaveOpen: true);

        Program.SendTargetEnvelope(writer, new TargetIpcEnvelope
        {
            Type = TargetIpcMessageType.SvgDecodeResponse.ToString(),
            RequestId = Guid.NewGuid().ToString("N"),
            Payload = new string('x', 512 * 1024)
        });
        Program.SendErrorResponse(
            writer,
            Guid.NewGuid().ToString("N"),
            TargetIpcMessageType.SvgDecodeResponse,
            new string('e', 512 * 1024));
        writer.Flush();

        string text = Encoding.UTF8.GetString(buffer.ToArray());
        Assert.Single(text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries));
        AssertBoundedLine(FirstLine(text));
    }

    [Fact]
    public async Task SvgDecode_ResponseBitmapOverEnvelopeBudget_FailsClosedWithBoundedMetadata()
    {
        var response = await SendSvgDecodeAsync(
            NoiseImageSvg(176),
            new SvgRenderLimitsData { MaxRasterWidth = 1024, MaxRasterHeight = 1024 });

        AssertNoPartialBitmap(response);
        Assert.Contains("payload budget", response.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NormalizeSvgDecodeLimits_NullLimits_UsesEngineDefaults()
    {
        var limits = Program.NormalizeSvgDecodeLimits(null);

        Assert.False(limits.AllowExternalReferences);
        Assert.Equal(SvgRenderLimits.Default.MaxRasterPixels, limits.MaxRasterPixels);
        Assert.Equal(SvgRenderLimits.Default.MaxElementCount, limits.MaxElementCount);
    }

    [Fact]
    public void NormalizeSvgDecodeLimits_RequesterGrantsExternalReferences_IsDenied()
    {
        var limits = Program.NormalizeSvgDecodeLimits(new SvgRenderLimitsData
        {
            AllowExternalReferences = true
        });

        Assert.False(limits.AllowExternalReferences);
    }

    [Fact]
    public void NormalizeSvgDecodeLimits_NegativeValues_FallBackToDefaults()
    {
        var limits = Program.NormalizeSvgDecodeLimits(new SvgRenderLimitsData
        {
            MaxRasterPixels = -1,
            MaxRasterWidth = -4096,
            MaxRasterHeight = -4096,
            MaxElementCount = -1,
            MaxSourceChars = -1,
            MaxRecursionDepth = -1,
            MaxFilterCount = -1,
            MaxRenderTimeMs = -1,
            MaxDecodedImagePixels = -1,
            MaxDecodedImageBytes = -1,
            MaxCumulativeResourceBytes = -1,
            MaxResourceCount = -1,
            MaxActiveLayers = -1,
            MaxReferenceDepth = -1
        });

        Assert.Equal(SvgRenderLimits.Default.MaxRasterPixels, limits.MaxRasterPixels);
        Assert.Equal(SvgRenderLimits.Default.MaxRasterWidth, limits.MaxRasterWidth);
        Assert.Equal(SvgRenderLimits.Default.MaxRasterHeight, limits.MaxRasterHeight);
        Assert.Equal(SvgRenderLimits.Default.MaxElementCount, limits.MaxElementCount);
        Assert.Equal(SvgRenderLimits.Default.MaxSourceChars, limits.MaxSourceChars);
        Assert.Equal(SvgRenderLimits.Default.MaxRecursionDepth, limits.MaxRecursionDepth);
        Assert.Equal(SvgRenderLimits.Default.MaxFilterCount, limits.MaxFilterCount);
        Assert.Equal(SvgRenderLimits.Default.MaxRenderTimeMs, limits.MaxRenderTimeMs);
        Assert.Equal(SvgRenderLimits.Default.MaxDecodedImagePixels, limits.MaxDecodedImagePixels);
        Assert.Equal(SvgRenderLimits.Default.MaxDecodedImageBytes, limits.MaxDecodedImageBytes);
        Assert.Equal(SvgRenderLimits.Default.MaxCumulativeResourceBytes, limits.MaxCumulativeResourceBytes);
        Assert.Equal(SvgRenderLimits.Default.MaxResourceCount, limits.MaxResourceCount);
        Assert.Equal(SvgRenderLimits.Default.MaxActiveLayers, limits.MaxActiveLayers);
        Assert.Equal(SvgRenderLimits.Default.MaxReferenceDepth, limits.MaxReferenceDepth);
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    [InlineData(long.MinValue)]
    public void NormalizeSvgDecodeLimits_NonPositiveRasterPixels_FallBackToDefault(long requested)
    {
        var limits = Program.NormalizeSvgDecodeLimits(new SvgRenderLimitsData
        {
            MaxRasterPixels = requested
        });

        Assert.Equal(SvgRenderLimits.Default.MaxRasterPixels, limits.MaxRasterPixels);
    }

    [Theory]
    [InlineData(long.MaxValue)]
    [InlineData(1L << 40)]
    public void NormalizeSvgDecodeLimits_ExtremeRasterPixels_AreClamped(long requested)
    {
        var limits = Program.NormalizeSvgDecodeLimits(new SvgRenderLimitsData
        {
            MaxRasterPixels = requested
        });

        Assert.InRange(limits.MaxRasterPixels, 1, 64L * 1024 * 1024);
        Assert.True(limits.MaxRasterPixels < requested);
    }

    [Fact]
    public void NormalizeSvgDecodeLimits_ExtremeBudgets_AreClampedToProcessCaps()
    {
        var limits = Program.NormalizeSvgDecodeLimits(new SvgRenderLimitsData
        {
            MaxElementCount = int.MaxValue,
            MaxSourceChars = int.MaxValue,
            MaxRasterWidth = int.MaxValue,
            MaxRasterHeight = int.MaxValue,
            MaxDecodedImagePixels = long.MaxValue,
            MaxDecodedImageBytes = int.MaxValue,
            MaxCumulativeResourceBytes = int.MaxValue,
            MaxResourceCount = int.MaxValue,
            MaxActiveLayers = int.MaxValue,
            MaxReferenceDepth = int.MaxValue,
            MaxRecursionDepth = int.MaxValue,
            MaxFilterCount = int.MaxValue,
            MaxRenderTimeMs = int.MaxValue
        });

        Assert.InRange(limits.MaxElementCount, 1, 250_000);
        Assert.InRange(limits.MaxSourceChars, 1, 32 * 1024 * 1024);
        Assert.InRange(limits.MaxRasterWidth, 1, 32_768);
        Assert.InRange(limits.MaxRasterHeight, 1, 32_768);
        Assert.InRange(limits.MaxDecodedImagePixels, 1L, 64L * 1024 * 1024);
        Assert.InRange(limits.MaxDecodedImageBytes, 1, 32 * 1024 * 1024);
        Assert.InRange(limits.MaxCumulativeResourceBytes, 1, 64 * 1024 * 1024);
        Assert.InRange(limits.MaxResourceCount, 1, 512);
        Assert.InRange(limits.MaxActiveLayers, 1, 16);
        Assert.InRange(limits.MaxReferenceDepth, 1, 64);
        Assert.InRange(limits.MaxRecursionDepth, 1, 512);
        Assert.InRange(limits.MaxFilterCount, 1, 1_000);
        Assert.InRange(limits.MaxRenderTimeMs, 1, 30_000);
    }

    [Fact]
    public void NormalizeSvgDecodeLimits_TighterRequesterLimits_ArePreserved()
    {
        var limits = Program.NormalizeSvgDecodeLimits(new SvgRenderLimitsData
        {
            MaxRasterPixels = 4_096,
            MaxElementCount = 64
        });

        Assert.Equal(4_096, limits.MaxRasterPixels);
        Assert.Equal(64, limits.MaxElementCount);
    }

    [Fact]
    public async Task SvgDecode_AllowExternalReferencesOverIpc_CannotPerformAmbientIo()
    {
        var response = await SendSvgDecodeAsync(
            "<svg xmlns='http://www.w3.org/2000/svg' width='10' height='10'>" +
            "<image href='https://example.test/assets/red.png' width='10' height='10'/></svg>",
            new SvgRenderLimitsData { AllowExternalReferences = true });

        AssertNoPartialBitmap(response);
        Assert.Contains("resource", response.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        AssertBoundedReason(response.ErrorMessage);
    }

    [Fact]
    public async Task SvgDecode_ExternalRelativeReferenceOverIpc_CannotPerformAmbientIo()
    {
        var response = await SendSvgDecodeAsync(
            "<svg xmlns='http://www.w3.org/2000/svg' width='10' height='10'>" +
            "<image href='red.png' width='10' height='10'/></svg>",
            new SvgRenderLimitsData { AllowExternalReferences = true });

        AssertNoPartialBitmap(response);
        Assert.Contains("resource", response.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SvgDecode_NegativeRasterPixelBudget_IsNormalizedThenEnforced()
    {
        var (line, response) = await SendSvgDecodeRawAsync(
            CleanFirstPartySvg,
            new SvgRenderLimitsData { MaxRasterPixels = -1 });

        AssertBoundedLine(line);
        Assert.True(response.Success, response.ErrorMessage);
        AssertPng(response.BitmapBytes);
        Assert.Equal(20, response.Width);
        Assert.Equal(20, response.Height);
    }

    [Fact]
    public async Task SvgDecode_TinyRasterPixelBudget_IsEnforcedAfterNormalization()
    {
        var response = await SendSvgDecodeAsync(
            CleanFirstPartySvg,
            new SvgRenderLimitsData { MaxRasterPixels = 1 });

        AssertNoPartialBitmap(response);
        Assert.Contains("raster", response.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SvgDecode_ExtremeRasterPixelBudget_IsClampedToProcessCap()
    {
        var response = await SendSvgDecodeAsync(
            "<svg xmlns='http://www.w3.org/2000/svg' width='40000' height='40000'>" +
            "<rect width='40000' height='40000' fill='red'/></svg>",
            new SvgRenderLimitsData
            {
                MaxRasterPixels = long.MaxValue,
                MaxRasterWidth = 40000,
                MaxRasterHeight = 40000
            });

        AssertNoPartialBitmap(response);
        Assert.Contains("raster", response.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void OversizedResponseReason_IsBounded()
    {
        AssertBoundedReason(Program.OversizedResponseReason("SVG decode"));
        Assert.True(Program.MaxTargetResponseBitmapBytes < TargetIpc.MaxPayloadChars);
        Assert.True(Program.FitsTargetResponseBudget(new byte[Program.MaxTargetResponseBitmapBytes]));
        Assert.False(Program.FitsTargetResponseBudget(new byte[Program.MaxTargetResponseBitmapBytes + 1]));
    }

    private static SvgRenderResult SuccessfulResult(bool requiresFallback)
    {
        return new SvgRenderResult
        {
            Success = true,
            Backend = SvgRendererBackend.FirstParty,
            Bitmap = new SKBitmap(2, 2),
            RequiresFallback = requiresFallback
        };
    }

    private static void AssertNoPartialBitmap(SvgDecodeResponsePayload response)
    {
        Assert.False(response.Success);
        AssertNoBitmapBytes(response);
        Assert.Equal(0, response.Width);
        Assert.Equal(0, response.Height);
        AssertBoundedReason(response.ErrorMessage);
    }

    private static void AssertNoBitmapBytes(SvgDecodeResponsePayload response)
    {
        Assert.True(
            response.BitmapBytes == null || response.BitmapBytes.Length == 0,
            "Rejected decode responses must not carry partial bitmap bytes.");
    }

    private static void AssertBoundedReason(string reason)
    {
        Assert.False(string.IsNullOrWhiteSpace(reason));
        Assert.InRange(reason.Length, 1, TargetIpc.MaxResponseMetadataChars);
    }

    private static void AssertBoundedLine(string line)
    {
        Assert.False(string.IsNullOrEmpty(line));
        Assert.InRange(line.Length, 1, TargetIpc.MaxEnvelopeChars);
        Assert.DoesNotContain("\n", line, StringComparison.Ordinal);
    }

    private static void AssertPng(byte[] bytes)
    {
        Assert.True(bytes.Length > 8, "Encoded response was too small to be a PNG.");
        Assert.Equal(0x89, bytes[0]);
        Assert.Equal((byte)'P', bytes[1]);
        Assert.Equal((byte)'N', bytes[2]);
        Assert.Equal((byte)'G', bytes[3]);
    }

    private static string DataSvg(string svg) =>
        "data:image/svg+xml;base64," + Convert.ToBase64String(Encoding.UTF8.GetBytes(svg));

    private static string NoiseImageSvg(int size)
    {
        var pixels = new byte[size * size * 4];
        var random = new Random(1234);
        random.NextBytes(pixels);
        for (int i = 3; i < pixels.Length; i += 4)
        {
            pixels[i] = 255;
        }

        using var bitmap = new SKBitmap(new SKImageInfo(size, size, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        System.Runtime.InteropServices.Marshal.Copy(pixels, 0, bitmap.GetPixels(), pixels.Length);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        byte[] png = data.ToArray();
        Assert.InRange(png.Length, Program.MaxTargetResponseBitmapBytes + 1, 180 * 1024);
        Assert.InRange(png.Length * 4 / 3, 1, 256 * 1024);

        return "<svg xmlns=\"http://www.w3.org/2000/svg\" " +
            $"width='{size}' height='{size}'>" +
            $"<image href='data:image/png;base64,{Convert.ToBase64String(png)}' " +
            $"width='{size}' height='{size}'/></svg>";
    }

    private static string FirstLine(string text) =>
        text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries)[0];

    private static async Task<SvgDecodeResponsePayload> SendSvgDecodeAsync(string svgContent) =>
        (await SendSvgDecodeRawAsync(svgContent)).Response;

    private static async Task<SvgDecodeResponsePayload> SendSvgDecodeAsync(
        string svgContent,
        SvgRenderLimitsData limits) =>
        (await SendSvgDecodeRawAsync(svgContent, limits)).Response;

    private static async Task<(string Line, SvgDecodeResponsePayload Response)> SendSvgDecodeRawAsync(
        string svgContent,
        SvgRenderLimitsData limits = null)
    {
        var request = new TargetIpcEnvelope
        {
            Type = TargetIpcMessageType.SvgDecode.ToString(),
            RequestId = Guid.NewGuid().ToString("N"),
            Payload = TargetIpc.SerializePayload(new SvgDecodePayload
            {
                SvgContent = svgContent,
                Limits = limits
            }),
            TimestampUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };

        using var buffer = new MemoryStream();
        await using (var writer = new StreamWriter(buffer, new UTF8Encoding(false), 1024, leaveOpen: true))
        {
            await Program.HandleSvgDecodeRequest(writer, request);
            await writer.FlushAsync();
        }

        string line = FirstLine(Encoding.UTF8.GetString(buffer.ToArray()));

        Assert.True(TargetIpc.TryDeserialize(line, out var response), line);
        Assert.Equal(TargetIpcMessageType.SvgDecodeResponse.ToString(), response.Type);
        Assert.Equal(request.RequestId, response.RequestId);

        var payload = TargetIpc.DeserializePayload<SvgDecodeResponsePayload>(response);
        Assert.NotNull(payload);
        return (line, payload);
    }
}
