using FenBrowser.Media.WebAudio;

namespace FenBrowser.Media.Tests;

/// <summary>WA 1.30 ScriptProcessorNode: blocks handed to script and played back two buffers later.</summary>
public class WebAudioScriptProcessorTests
{
    private const float Rate = 48000f;

    private static float[] Render(AudioGraph graph, int channels, int length) =>
        new OfflineAudioRenderer(graph, channels, length).RenderAll()[0];

    [Fact]
    public void AScriptProcessorPlaysScriptsOutputTwoBuffersLater()
    {
        const int bufferSize = 256;
        var graph = new AudioGraph(Rate, 1);
        var constant = new ConstantSourceKernel(graph);
        constant.Offset.Timeline.SetValueAtTime(0.5f, 0);
        var processor = new ScriptProcessorKernel(graph, bufferSize, 1, 1) { WaitForScript = true };
        var playbackTimes = new List<double>();
        processor.BlockReady = (_, request) =>
        {
            // What an audioprocess handler adding 1 to its input does.
            for (int i = 0; i < bufferSize; i++)
                request.Output[0][i] = request.Input[0][i] + 1f;
            playbackTimes.Add(request.PlaybackTime);
            ScriptProcessorKernel.Complete(request);
        };
        graph.Post(g =>
        {
            g.AddNode(constant);
            g.AddNode(processor);
            g.Connect(constant, 0, processor, 0);
            g.Connect(processor, 0, g.Destination, 0);
            constant.Start(0);
        });

        var output = Render(graph, 1, 4 * bufferSize);

        Assert.All(output[..(2 * bufferSize)], v => Assert.Equal(0f, v));
        Assert.All(output[(2 * bufferSize)..], v => Assert.Equal(1.5f, v));
        // Block n is heard from (n + 2) * bufferSize.
        Assert.Equal(2 * bufferSize / (double)Rate, playbackTimes[0], 9);
        Assert.Equal(3 * bufferSize / (double)Rate, playbackTimes[1], 9);
    }
}
