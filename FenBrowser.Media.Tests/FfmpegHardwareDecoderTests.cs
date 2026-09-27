using FenBrowser.Media.Buffers;
using FenBrowser.Media.Codecs.Ffmpeg;
using FenBrowser.Media.Containers.Matroska;
using FenBrowser.Media.Diagnostics;
using FenBrowser.Media.Pipeline;
using FenBrowser.Media.Types;

namespace FenBrowser.Media.Tests;

/// <summary>
/// libavcodec decoding on the platform's video hardware (MEDIA_ENGINE_DESIGN section 8,
/// M7): Direct3D 11 Video here, VA-API on Linux, VideoToolbox on macOS. The three legs
/// differ only in the <c>AVHWDeviceType</c> handed to <c>av_hwdevice_ctx_create</c>, so
/// what runs on this machine is the whole mechanism: the device is attached to the
/// decoder, pictures come back out of GPU memory through
/// <c>av_hwframe_transfer_data</c>, and they are the pictures the software decoder gives.
/// </summary>
public class FfmpegHardwareDecoderTests
{
    private sealed class Collect : IDecodeOutput<VideoFrame>
    {
        public List<VideoFrame> Frames { get; } = [];
        public void Emit(VideoFrame item) => Frames.Add(item);
    }

    [Fact]
    public void ThePlatformDeviceIsTheOneThisBuildRunsOn()
    {
        var expected = OperatingSystem.IsWindows() ? FfmpegHardwareDevice.D3D11Va
            : OperatingSystem.IsMacOS() ? FfmpegHardwareDevice.VideoToolbox
            : OperatingSystem.IsLinux() ? FfmpegHardwareDevice.VaApi
            : FfmpegHardwareDevice.None;
        Assert.Equal(expected, FfmpegHardwareVideoDecoderFactory.PlatformDevice);
    }

    /// <summary>The hardware decoder is offered ahead of the software one for the same codec.</summary>
    [Fact]
    public async Task TheHardwareDecoderIsChosenFirst()
    {
        var context = MediaPipelineContext.ForTests();
        var (track, demuxer) = await OpenAsync("pattern_vp9_720p.webm", context);
        await using var _ = demuxer;
        var candidates = Registry().GetVideoCandidates(track.Config);
        Assert.True(candidates[0].IsHardwareAccelerated, $"the first candidate was {candidates[0].Name}");
        Assert.Contains(candidates, c => !c.IsHardwareAccelerated);
    }

    /// <summary>
    /// A 720p stream is big enough for a GPU to take (drivers refuse the tiny fixtures), so
    /// on a machine with a video device every picture comes off the GPU and matches the
    /// software decode exactly. Without a device the same decoder decodes in software, and
    /// the pictures are still the same.
    /// </summary>
    [Fact]
    public async Task HardwarePicturesAreTheSameAsTheSoftwareOnes()
    {
        var context = MediaPipelineContext.ForTests();
        _ = Registry(); // installs the native library resolver
        long before = FfmpegHardwareCounters.TransferredPictures;

        var hardware = new List<VideoFrame>();
        var software = new List<VideoFrame>();
        try
        {
            bool usedHardware = await DecodeAsync("pattern_vp9_720p.webm", context, FfmpegHardwareVideoDecoderFactory.PlatformDevice, hardware);
            _ = await DecodeAsync("pattern_vp9_720p.webm", context, FfmpegHardwareDevice.None, software);

            Assert.Equal(10, software.Count);
            Assert.Equal(software.Count, hardware.Count);
            for (int i = 0; i < software.Count; i++)
            {
                Assert.Equal(software[i].Timestamp, hardware[i].Timestamp);
                Assert.Equal(software[i].Width, hardware[i].Width);
                Assert.Equal(software[i].Height, hardware[i].Height);
                // The hardware decoder hands back NV12 and the software one I420: the same
                // samples in a different plane layout, so they are compared as pictures.
                int stride = software[i].Width * 4;
                var expected = new byte[stride * software[i].Height];
                var actual = new byte[expected.Length];
                Video.PixelConverter.ToBgra(software[i], expected, stride);
                Video.PixelConverter.ToBgra(hardware[i], actual, stride);
                Assert.True(expected.AsSpan().SequenceEqual(actual), $"picture {i} differs between the hardware and software decode");
            }

            if (usedHardware)
            {
                Assert.Equal(before + hardware.Count, FfmpegHardwareCounters.TransferredPictures);
            }
        }
        finally
        {
            foreach (var frame in hardware)
                frame.Dispose();
            foreach (var frame in software)
                frame.Dispose();
        }
    }

    /// <summary>The kill switch keeps every libavcodec decoder in software.</summary>
    [Fact]
    public void TheKillSwitchTurnsTheHardwarePathOff()
    {
        string? previous = Environment.GetEnvironmentVariable("FEN_MEDIA_HW_DECODE");
        Environment.SetEnvironmentVariable("FEN_MEDIA_HW_DECODE", "off");
        try
        {
            Assert.Equal(FfmpegHardwareDevice.None, FfmpegHardwareVideoDecoderFactory.PlatformDevice);
            var factory = new FfmpegHardwareVideoDecoderFactory(MediaCodec.Vp9, "vp9");
            var config = new CodecConfig(MediaTrackKind.Video, MediaCodec.Vp9, "vp09.00.10.08", Width: 1280, Height: 720);
            Assert.Equal(DecoderSupport.Unsupported, factory.Supports(config));
        }
        finally
        {
            Environment.SetEnvironmentVariable("FEN_MEDIA_HW_DECODE", previous);
        }
    }

    private static DecoderRegistry Registry()
    {
        var decoders = new DecoderRegistry();
        MediaFormats.RegisterBuiltIn(new DemuxerRegistry(), decoders);
        Assert.True(FfmpegDecoders.TryRegister(decoders, NullMediaLogSink.Instance), "libavcodec must be available on the development machine");
        return decoders;
    }

    private static async Task<(MediaTrackInfo Track, IDemuxer Demuxer)> OpenAsync(string fixture, MediaPipelineContext context)
    {
        var demuxer = MatroskaDemuxerFactory.Instance.Create(new MemoryByteSource(MediaFixtures.Read(fixture)), context);
        var info = await demuxer.InitializeAsync(CancellationToken.None);
        return (info.Tracks.Single(t => t.Kind == MediaTrackKind.Video), demuxer);
    }

    /// <summary>Decodes the fixture with one device type; returns whether the device attached.</summary>
    private static async Task<bool> DecodeAsync(string fixture, MediaPipelineContext context, FfmpegHardwareDevice device, List<VideoFrame> into)
    {
        var (track, demuxer) = await OpenAsync(fixture, context);
        await using var owned = demuxer;
        await using var decoder = new FfmpegVideoDecoder("vp9", context, device);
        await decoder.ConfigureAsync(track.Config, CancellationToken.None);

        var output = new Collect();
        while (await demuxer.ReadPacketAsync(CancellationToken.None) is { } packet)
        {
            using (packet)
            {
                if (packet.TrackId == track.Id)
                    await decoder.DecodeAsync(packet, output, CancellationToken.None);
            }
        }

        await decoder.DrainAsync(output, CancellationToken.None);
        into.AddRange(output.Frames);
        return decoder.UsesHardware;
    }
}
