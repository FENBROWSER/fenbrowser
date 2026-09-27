using FenBrowser.Media.Audio;
using FenBrowser.Media.Pipeline;
using FenBrowser.Media.WebAudio;

namespace FenBrowser.Media.Tests;

/// <summary>
/// WA4: decodeAudioData's decoder and resampler (WA 1.1.3, design WA-D7) and the device
/// renderer behind an AudioContext (WA-D6).
/// </summary>
public class WebAudioOutputTests
{
    private static (DemuxerRegistry, DecoderRegistry) Registries()
    {
        var demuxers = new DemuxerRegistry();
        var decoders = new DecoderRegistry();
        MediaFormats.RegisterBuiltIn(demuxers, decoders);
        return (demuxers, decoders);
    }

    [Fact]
    public async Task AWavFileDecodesToItsSamplesAtItsOwnRate()
    {
        var (demuxers, decoders) = Registries();
        var decoded = await AudioFileDecoder.DecodeAsync(
            MediaFixtures.Read("sine_pcm16.wav"), 48000, demuxers, decoders, MediaPipelineContext.ForTests(), CancellationToken.None);

        Assert.Single(decoded.Channels);
        Assert.Equal(48000, decoded.Length);
        Assert.Equal(48000f, decoded.SampleRate);
        Assert.Contains(decoded.Channels[0], v => Math.Abs(v) > 0.1f);
    }

    [Fact]
    public async Task DecodingToAnotherRateResamplesTheLength()
    {
        var (demuxers, decoders) = Registries();
        var decoded = await AudioFileDecoder.DecodeAsync(
            MediaFixtures.Read("sine_pcm16.wav"), 24000, demuxers, decoders, MediaPipelineContext.ForTests(), CancellationToken.None);
        Assert.Equal(24000, decoded.Length);
    }

    [Fact]
    public async Task BytesThatAreNotAudioFailToDecode()
    {
        var (demuxers, decoders) = Registries();
        await Assert.ThrowsAsync<AudioFileDecoder.DecodeFailedException>(() => AudioFileDecoder.DecodeAsync(
            new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, 48000, demuxers, decoders, MediaPipelineContext.ForTests(), CancellationToken.None));
    }

    [Fact]
    public void ResamplingASineKeepsItASine()
    {
        var input = Enumerable.Range(0, 4410).Select(i => (float)Math.Sin(2 * Math.PI * 441 * i / 44100.0)).ToArray();
        var output = Resampler.Resample(input, 44100, 48000);
        Assert.Equal(4800, output.Length);
        for (int n = 200; n < 4600; n++)
            Assert.True(Math.Abs(Math.Sin(2 * Math.PI * 441 * n / 48000.0) - output[n]) < 1e-3, $"frame {n}");
    }

    [Fact]
    public void TheDeviceRendererConvertsTheContextRateToTheDevices()
    {
        var graph = new AudioGraph(44100, 2);
        var osc = new OscillatorKernel(graph);
        osc.Frequency.Timeline.SetValueAtTime(441, 0);
        graph.Post(g =>
        {
            g.AddNode(osc);
            g.Connect(osc, 0, g.Destination, 0);
            osc.Start(0);
        });

        var renderer = new WebAudioDeviceRenderer(graph, new AudioStreamFormat(48000, 2)) { Running = true };
        var interleaved = new float[4800 * 2];
        renderer.Render(interleaved, 2);

        // The resampler's filter reaches 16 input frames ahead, so the output lags by none of
        // them: frame n at 48 kHz is the sine at time n / 48000.
        for (int n = 100; n < 4700; n++)
        {
            double expected = Math.Sin(2 * Math.PI * 441 * n / 48000.0);
            Assert.True(Math.Abs(expected - interleaved[2 * n]) < 1e-3, $"left frame {n}");
            Assert.True(Math.Abs(expected - interleaved[2 * n + 1]) < 1e-3, $"right frame {n}");
        }
    }

    [Fact]
    public void ASuspendedRendererGivesSilenceAndDoesNotAdvanceTheGraph()
    {
        var graph = new AudioGraph(48000, 2);
        var renderer = new WebAudioDeviceRenderer(graph, new AudioStreamFormat(48000, 2));
        var buffer = new float[256];
        Array.Fill(buffer, 1f);
        renderer.Render(buffer, 2);
        Assert.All(buffer, v => Assert.Equal(0f, v));
        Assert.Equal(0, graph.CurrentFrame);
    }
}
