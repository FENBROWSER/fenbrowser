using FenBrowser.Media.Audio;
using FenBrowser.Media.Buffers;

namespace FenBrowser.Media.Tests;

public class TimeStretcherTests
{
    private static List<float> Stretch(double rate, int sampleRate, int channels, int frames, float frequency, out List<AudioBlock> blocks)
    {
        var stretcher = new TimeStretcher(sampleRate, channels) { Rate = rate };
        blocks = [];
        int per = 1024;
        for (int start = 0; start < frames; start += per)
        {
            int count = Math.Min(per, frames - start);
            var block = AudioBlock.Allocate(MediaLimits.Default, sampleRate, channels, count, MediaTime.FromTimescale(start, sampleRate));
            for (int i = 0; i < count; i++)
            {
                float v = 0.5f * MathF.Sin(2 * MathF.PI * frequency * (start + i) / sampleRate);
                for (int c = 0; c < channels; c++)
                    block.Samples[i * channels + c] = v;
            }

            stretcher.Push(block);
            while (stretcher.Pull(MediaLimits.Default) is { } outBlock)
                blocks.Add(outBlock);
        }

        while (stretcher.Pull(MediaLimits.Default, flush: true) is { } tail)
            blocks.Add(tail);

        var samples = new List<float>();
        foreach (var b in blocks)
            for (int i = 0; i < b.FrameCount; i++)
                samples.Add(b.Samples[i * channels]);
        return samples;
    }

    private static int ZeroCrossings(List<float> samples, int from, int to)
    {
        int n = 0;
        for (int i = from + 1; i < to; i++)
            if ((samples[i - 1] < 0) != (samples[i] < 0))
                n++;
        return n;
    }

    [Theory]
    [InlineData(2.0)]
    [InlineData(0.5)]
    [InlineData(1.25)]
    public void KeepsPitchWhileChangingLength(double rate)
    {
        const int sampleRate = 48000;
        const int seconds = 2;
        var samples = Stretch(rate, sampleRate, 2, sampleRate * seconds, 440f, out var blocks);

        // Length follows the rate; the last partial sequence (under 82 ms) plays unstretched.
        double expected = sampleRate * seconds / rate;
        int sequence = (int)(0.082 * sampleRate);
        Assert.InRange(samples.Count, expected - sequence, expected + sequence);

        // Pitch does not: 440 Hz gives 880 crossings per second of output regardless of the rate.
        int middleFrom = samples.Count / 4;
        int middleTo = samples.Count * 3 / 4;
        double crossingsPerSecond = ZeroCrossings(samples, middleFrom, middleTo) / ((middleTo - middleFrom) / (double)sampleRate);
        Assert.InRange(crossingsPerSecond, 860, 900);

        // Amplitude survives the cross-fades.
        float peak = samples.Skip(middleFrom).Take(middleTo - middleFrom).Max(Math.Abs);
        Assert.InRange(peak, 0.45f, 0.55f);

        // Output timestamps are media time and advance by output frames × rate.
        for (int i = 1; i < blocks.Count; i++)
        {
            double advance = (blocks[i].Timestamp - blocks[i - 1].Timestamp).TotalSeconds;
            Assert.InRange(advance, blocks[i - 1].FrameCount * rate / sampleRate * 0.99, blocks[i - 1].FrameCount * rate / sampleRate * 1.01 + 1e-6);
        }

        foreach (var b in blocks)
            b.Dispose();
    }

    [Fact]
    public void Flush_DropsInputAndRestartsTiming()
    {
        var stretcher = new TimeStretcher(48000, 1) { Rate = 2 };
        var block = AudioBlock.Allocate(MediaLimits.Default, 48000, 1, 4000, MediaTime.FromSeconds(3));
        stretcher.Push(block);
        Assert.Equal(4000, stretcher.BufferedFrames);
        stretcher.Flush();
        Assert.Equal(0, stretcher.BufferedFrames);

        var later = AudioBlock.Allocate(MediaLimits.Default, 48000, 1, 8000, MediaTime.FromSeconds(9));
        stretcher.Push(later);
        var output = stretcher.Pull(MediaLimits.Default);
        Assert.NotNull(output);
        Assert.Equal(MediaTime.FromSeconds(9), output.Timestamp);
        output.Dispose();
    }
}
