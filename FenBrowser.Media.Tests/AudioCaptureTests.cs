using FenBrowser.Media.Audio;
using FenBrowser.Media.Buffers;
using FenBrowser.Media.Clock;
using FenBrowser.Media.WebAudio;

namespace FenBrowser.Media.Tests;

/// <summary>
/// captureStream() audio: the renderer copies what it renders before the element's volume
/// and muting (Media Capture from DOM Elements §3), and the capture writer carries that
/// copy into a Web Audio track pipe at the pipe's rate.
/// </summary>
public class AudioCaptureTests
{
    private sealed class Position(int sampleRate) : IAudioPlaybackPosition
    {
        public int SampleRate => sampleRate;
        public long FramesPlayed { get; set; }
    }

    private sealed class Recorder : IAudioCapture
    {
        public List<float> Samples { get; } = [];

        public void Write(ReadOnlySpan<float> interleaved, int channels, int sampleRate) => Samples.AddRange(interleaved.ToArray());
    }

    private static AudioRenderer RendererWithHalfScaleTone(int frames)
    {
        var renderer = new AudioRenderer(new AudioStreamFormat(48000, 1), new AudioMasterClock(new Position(48000)));
        var block = AudioBlock.Allocate(MediaLimits.Default, 48000, 1, frames, MediaTime.Zero);
        block.Samples.Fill(0.5f);
        renderer.Enqueue(block);
        return renderer;
    }

    [Theory]
    [InlineData(0.2, false)]
    [InlineData(1.0, true)]
    [InlineData(0.0, false)]
    public void TheCaptureHearsTheElementWhateverItsVolumeOrMuting(double volume, bool muted)
    {
        var renderer = RendererWithHalfScaleTone(256);
        var recorder = new Recorder();
        renderer.Volume = volume;
        renderer.Muted = muted;
        renderer.Capture = recorder;

        var device = new float[256];
        Assert.Equal(256, renderer.Render(device, 1));

        Assert.All(recorder.Samples, s => Assert.Equal(0.5f, s));
        float heard = muted ? 0f : (float)(0.5 * volume);
        Assert.All(device, s => Assert.Equal(heard, s, 5));
    }

    [Fact]
    public void WithoutACaptureTheDeviceStillGetsTheVolume()
    {
        var renderer = RendererWithHalfScaleTone(128);
        renderer.Volume = 0.5;

        var device = new float[128];
        renderer.Render(device, 1);

        Assert.All(device, s => Assert.Equal(0.25f, s, 5));
    }

    [Fact]
    public void TheWriterConvertsTheDeviceRateToThePipes()
    {
        var pipe = new AudioTrackPipe(48000, 2);
        var writer = new AudioCaptureWriter(48000, 2);
        writer.SetPipes([pipe]);

        // One second of mono at 44.1 kHz, in device-sized pieces.
        var chunk = new float[441];
        chunk.AsSpan().Fill(0.25f);
        for (int i = 0; i < 100; i++)
            writer.Write(chunk, 1, 44100);

        // Whole blocks only, and very nearly a second's worth at the pipe's rate.
        Assert.Equal(0, pipe.Written % 128);
        Assert.InRange(pipe.Written, 48000 - 256, 48000);

        var read = new[] { new float[128], new float[128] };
        pipe.Read(pipe.Written - 128, 128, read);
        Assert.All(read[0], s => Assert.Equal(0.25f, s, 5));
        Assert.All(read[1], s => Assert.Equal(0.25f, s, 5));
    }

    [Fact]
    public void NothingIsWrittenBeforeTheWriterHasPipes()
    {
        var writer = new AudioCaptureWriter(48000, 2);
        writer.Write(new float[512], 2, 48000);

        var pipe = new AudioTrackPipe(48000, 2);
        writer.SetPipes([pipe]);
        writer.Write(new float[512], 2, 48000);

        Assert.Equal(256, pipe.Written);
    }
}
