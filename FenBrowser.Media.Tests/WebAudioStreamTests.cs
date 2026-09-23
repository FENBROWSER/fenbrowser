using FenBrowser.Media.Audio;
using FenBrowser.Media.WebAudio;

namespace FenBrowser.Media.Tests;

/// <summary>
/// The WA5 kernels: MediaStreamAudioDestinationNode and MediaStreamAudioSourceNode over an
/// <see cref="AudioTrackPipe"/> (WA 1.20, 1.21) and the media element tap (WA 1.20).
/// </summary>
public class WebAudioStreamTests
{
    private const float Rate = 48000f;

    private static float[] Render(AudioGraph graph, int channels, int length) =>
        new OfflineAudioRenderer(graph, channels, length).RenderAll()[0];

    private static AudioBus Block(float value)
    {
        var bus = new AudioBus(WebAudioLimits.MaxChannels, 128);
        bus.Reset(1);
        bus.Channel(0).Fill(value);
        bus.MarkNotSilent();
        return bus;
    }

    [Fact]
    public void APipeReadsSilenceForFramesNotYetWrittenOrAlreadyOverwritten()
    {
        var pipe = new AudioTrackPipe(Rate, 1, capacityFrames: 256);
        for (int i = 1; i <= 3; i++)
            pipe.Write(Block(i));

        var read = new[] { new float[512] };
        pipe.Read(0, 512, read);

        // Frames 0-127 were overwritten by the third block; 384 onwards were never written.
        Assert.All(read[0][..128], v => Assert.Equal(0f, v));
        Assert.All(read[0][128..256], v => Assert.Equal(2f, v));
        Assert.All(read[0][256..384], v => Assert.Equal(3f, v));
        Assert.All(read[0][384..], v => Assert.Equal(0f, v));
    }

    [Fact]
    public void AStreamSourceAtTheSameRateHearsItsDestinationWithoutGaps()
    {
        var graph = new AudioGraph(Rate, 1);
        var constant = new ConstantSourceKernel(graph);
        constant.Offset.Timeline.SetValueAtTime(0.25f, 0);
        var destination = new StreamDestinationKernel(graph, new AudioTrackPipe(Rate, 1));
        var source = new StreamSourceKernel(graph);
        graph.Post(g =>
        {
            g.AddNode(constant);
            g.AddNode(destination);
            g.AddNode(source);
            g.Connect(constant, 0, destination, 0);
            g.Connect(source, 0, g.Destination, 0);
            source.SetPipe(destination.Pipe);
            constant.Start(0);
        });

        var output = Render(graph, 1, 2048);

        // The first quantum or two pass before the source has anything to read; from then on
        // every frame arrives, none dropped for interpolation lookahead.
        int first = Array.FindIndex(output, v => v != 0f);
        Assert.InRange(first, 0, 256);
        Assert.All(output[first..], v => Assert.Equal(0.25f, v));
    }

    [Fact]
    public void AMutedStreamSourceIsSilentButKeepsReading()
    {
        var graph = new AudioGraph(Rate, 1);
        var pipe = new AudioTrackPipe(Rate, 1);
        var source = new StreamSourceKernel(graph) { Muted = true };
        graph.Post(g =>
        {
            g.AddNode(source);
            g.Connect(source, 0, g.Destination, 0);
            source.SetPipe(pipe);
        });

        var renderer = new OfflineAudioRenderer(graph, 1, 1024);
        for (int q = 0; q < 8; q++)
        {
            pipe.Write(Block(1f));
            renderer.RenderQuantum();
        }

        Assert.All(renderer.Result[0], v => Assert.Equal(0f, v));
    }

    [Fact]
    public async Task AnElementTapIsPulledByTheGraphSoItsAudioArrivesWhole()
    {
        var pipe = new AudioTrackPipe(Rate, 2);
        var output = new PipeAudioOutput(pipe);
        var ramp = new Ramp();
        await output.OpenAsync(new AudioStreamFormat((int)Rate, 2), ramp, CancellationToken.None);
        output.Start();
        try
        {
            var graph = new AudioGraph(Rate, 2);
            var source = new StreamSourceKernel(graph);
            graph.Post(g =>
            {
                g.AddNode(source);
                g.Connect(source, 0, g.Destination, 0);
                source.SetPipe(pipe);
            });

            var left = new OfflineAudioRenderer(graph, 2, 128 * 64).RenderAll()[0];

            // Every quantum carries player frames in order: the ramp never repeats or skips.
            int first = Array.FindIndex(left, v => v != 0f);
            Assert.Equal(0, first);
            for (int i = 1; i < left.Length; i++)
                Assert.Equal(left[i - 1] + 1f, left[i]);
            Assert.True(output.FramesPlayed >= left.Length);
        }
        finally
        {
            await output.DisposeAsync();
        }
    }

    // A player that renders 1, 2, 3, ... on every channel.
    private sealed class Ramp : IAudioRenderCallback
    {
        private float _next = 1f;

        public int Render(Span<float> destination, int channels)
        {
            int frames = destination.Length / channels;
            for (int i = 0; i < frames; i++)
            {
                for (int c = 0; c < channels; c++)
                    destination[i * channels + c] = _next;
                _next++;
            }

            return frames;
        }
    }
}
