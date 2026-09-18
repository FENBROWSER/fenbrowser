using System.Diagnostics;
using System.Text;
using FenBrowser.Core;
using FenBrowser.Core.Network;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Media;
using FenBrowser.FenEngine.Scripting;
using FenBrowser.Host.ProcessIsolation.Fuzz;
using FenBrowser.Host.ProcessIsolation.Media;
using FenBrowser.Media;
using FenBrowser.Media.Buffers;
using FenBrowser.Media.Pipeline;
using Xunit;

namespace FenBrowser.Tests.ProcessIsolation;

/// <summary>
/// The media process (MEDIA_ENGINE_DESIGN §2.2, ADR-0004): demuxing and decoding in a
/// separate sandboxed child, the same samples as in-process, and a crash that fails only
/// the players on it.
/// </summary>
[Collection("Media Engine State")]
public sealed class MediaProcessTests
{
    [Fact]
    public async Task TheMediaProcessDecodesTheSameSamplesAsThisProcess()
    {
        var bytes = File.ReadAllBytes(Path.Combine(FindTestAssets(), "media", "sine_opus.ogg"));
        using var client = new MediaProcessClient();

        var local = new LocalMediaDecodeSourceFactory(MediaEngineServices.Demuxers, MediaEngineServices.Decoders)
            .Create(new MemoryByteSource(bytes), "audio/ogg", MediaPipelineContext.ForTests());
        var remote = client.Create(new MemoryByteSource(bytes), "audio/ogg", MediaPipelineContext.ForTests());

        var expected = await local.OpenAsync(CancellationToken.None);
        var actual = await remote.OpenAsync(CancellationToken.None);
        Assert.Equal(1, client.Generation);
        Assert.Equal(expected.Duration, actual.Duration);
        Assert.Equal(expected.IsSeekable, actual.IsSeekable);
        Assert.Equal(expected.AudioTrack!.Config.Codec, actual.AudioTrack!.Config.Codec);
        Assert.Equal(expected.AudioTrack.Config.SampleRate, actual.AudioTrack.Config.SampleRate);
        Assert.Equal(expected.Tracks.Count, actual.Tracks.Count);
        Assert.Null(actual.VideoTrack);

        long frames = 0;
        while (true)
        {
            var a = await local.ReadAsync(CancellationToken.None);
            var b = await remote.ReadAsync(CancellationToken.None);
            if (a is null || b is null)
            {
                Assert.Null(a);
                Assert.Null(b);
                break;
            }

            var blockA = a.Value.Audio!;
            var blockB = b.Value.Audio!;
            Assert.Equal(blockA.Timestamp, blockB.Timestamp);
            Assert.Equal(blockA.FrameCount, blockB.FrameCount);
            Assert.Equal(blockA.Channels, blockB.Channels);
            Assert.True(blockA.Samples.SequenceEqual(blockB.Samples), $"samples differ at {blockA.Timestamp}");
            frames += blockA.FrameCount;
            a.Value.Dispose();
            b.Value.Dispose();
        }

        Assert.InRange(frames, 47_000, 49_000);

        // A seek lands both on the same page.
        await local.SeekAsync(MediaTime.FromSeconds(0.5), CancellationToken.None);
        await remote.SeekAsync(MediaTime.FromSeconds(0.5), CancellationToken.None);
        var afterLocal = await local.ReadAsync(CancellationToken.None);
        var afterRemote = await remote.ReadAsync(CancellationToken.None);
        Assert.NotNull(afterLocal);
        Assert.NotNull(afterRemote);
        Assert.Equal(afterLocal.Value.Timestamp, afterRemote.Value.Timestamp);
        afterLocal.Value.Dispose();
        afterRemote.Value.Dispose();

        await local.DisposeAsync();
        await remote.DisposeAsync();
    }

    [Fact]
    public async Task TheMediaProcessDecodesTheSamePicturesAsThisProcess()
    {
        var bytes = File.ReadAllBytes(Path.Combine(FindTestAssets(), "media", "pattern_vp8_vorbis.webm"));
        using var client = new MediaProcessClient();

        var local = new LocalMediaDecodeSourceFactory(MediaEngineServices.Demuxers, MediaEngineServices.Decoders)
            .Create(new MemoryByteSource(bytes), "video/webm", MediaPipelineContext.ForTests());
        var remote = client.Create(new MemoryByteSource(bytes), "video/webm", MediaPipelineContext.ForTests());

        var expected = await local.OpenAsync(CancellationToken.None);
        var actual = await remote.OpenAsync(CancellationToken.None);
        Assert.NotNull(actual.VideoTrack);
        Assert.NotNull(actual.AudioTrack);
        Assert.Equal(expected.VideoTrack!.Config.Codec, actual.VideoTrack.Config.Codec);
        Assert.Equal(64, actual.VideoTrack.Config.Width);
        Assert.Equal(48, actual.VideoTrack.Config.Height);

        int pictures = 0;
        int blocks = 0;
        while (true)
        {
            var a = await local.ReadAsync(CancellationToken.None);
            var b = await remote.ReadAsync(CancellationToken.None);
            if (a is null || b is null)
            {
                Assert.Null(a);
                Assert.Null(b);
                break;
            }

            Assert.Equal(a.Value.Timestamp, b.Value.Timestamp);
            if (a.Value.Video is { } frameA)
            {
                var frameB = Assert.IsType<VideoFrame>(b.Value.Video);
                Assert.Equal(frameA.Format, frameB.Format);
                Assert.Equal(frameA.Width, frameB.Width);
                Assert.Equal(frameA.Height, frameB.Height);
                Assert.Equal(frameA.Duration, frameB.Duration);
                for (int plane = 0; plane < frameA.PlaneCount; plane++)
                {
                    Assert.True(frameA.GetPlane(plane).SequenceEqual(frameB.GetPlane(plane)), $"plane {plane} differs at {frameA.Timestamp}");
                }

                pictures++;
            }
            else
            {
                Assert.NotNull(b.Value.Audio);
                blocks++;
            }

            a.Value.Dispose();
            b.Value.Dispose();
        }

        Assert.Equal(10, pictures);
        Assert.True(blocks > 40, $"only {blocks} audio blocks");
        await local.DisposeAsync();
        await remote.DisposeAsync();
    }

    [Fact]
    public async Task AnUnsupportedResourceIsRefusedByTheMediaProcessWithoutKillingIt()
    {
        using var client = new MediaProcessClient();
        var remote = client.Create(new MemoryByteSource(Encoding.ASCII.GetBytes("<html>not media</html>")), "text/html", MediaPipelineContext.ForTests());
        await Assert.ThrowsAsync<MediaUnsupportedException>(async () => await remote.OpenAsync(CancellationToken.None));
        await remote.DisposeAsync();

        // The child is still there for the next resource.
        var bytes = File.ReadAllBytes(Path.Combine(FindTestAssets(), "media", "sine_pcm16.wav"));
        var next = client.Create(new MemoryByteSource(bytes), "audio/wav", MediaPipelineContext.ForTests());
        var info = await next.OpenAsync(CancellationToken.None);
        Assert.Equal(MediaCodec.Pcm, info.AudioTrack!.Config.Codec);
        Assert.Equal(1, client.Generation);
        await next.DisposeAsync();
    }

    [Fact]
    public async Task AMediaProcessCrashFailsThePlayerWithADecodeErrorAndTheTabSurvives()
    {
        var previousFetcher = MediaFetchResource.FetchDetailedAsync;
        var previousMode = MediaAutoplayPolicy.Default.Mode;
        var previousSources = MediaEngineServices.DecodeSources;
        MediaAutoplayPolicy.Default.Mode = AutoplayPolicyMode.Allowed;
        using var client = new MediaProcessClient();
        MediaEngineServices.DecodeSources = client;

        // Ten seconds of tone: long enough that the child is still decoding while it plays.
        var longWav = SineWav(seconds: 10);
        var shortWav = File.ReadAllBytes(Path.Combine(FindTestAssets(), "media", "sine_pcm16.wav"));
        MediaFetchResource.FetchDetailedAsync = (request, _) => Task.FromResult(new BinaryFetchResult
        {
            Body = request.Url.EndsWith("long", StringComparison.Ordinal) ? longWav : shortWav,
            StatusCode = 200,
            FinalUri = new Uri(request.Url),
            ContentType = "audio/wav",
        });
        try
        {
            var engine = await CreateEngineAsync("<html><body></body></html>");
            engine.Evaluate("""
                globalThis.__events = [];
                var a = document.createElement('audio');
                ['playing', 'ended', 'error'].forEach(function (t) {
                    a.addEventListener(t, function () { globalThis.__events.push(t + (t === 'error' ? ':' + a.error.code : '')); });
                });
                a.src = 'long';
                a.play();
                """);

            Assert.Equal("playing", await WaitForAsync(engine, "globalThis.__events.join(',')"));
            Assert.Equal(1, client.Generation);
            Assert.NotNull(client.ChildProcessId);

            client.KillChildForTesting();

            // MEDIA_ERR_DECODE on the element whose player lived in the child (§4.8.11.5
            // "If the media data is corrupted" path through the resource's failure)...
            var events = await WaitForAsync(engine, "globalThis.__events.length > 1 ? globalThis.__events.join(',') : ''");
            Assert.Equal("playing,error:3", events);
            Assert.Equal("true", engine.Evaluate("String(a.networkState === a.NETWORK_IDLE)")?.ToString());

            // ...and nothing else: the document still runs script, and the next player
            // gets a fresh media process.
            Assert.Equal("42", engine.Evaluate("String(6 * 7)")?.ToString());
            engine.Evaluate("""
                globalThis.__next = [];
                var b = document.createElement('audio');
                ['ended', 'error'].forEach(function (t) {
                    b.addEventListener(t, function () { globalThis.__next.push(t); });
                });
                b.src = 'short';
                b.play();
                """);
            Assert.Equal("ended", await WaitForAsync(engine, "globalThis.__next.join(',')"));
            Assert.Equal(2, client.Generation);
        }
        finally
        {
            MediaEngineServices.DecodeSources = previousSources;
            MediaFetchResource.FetchDetailedAsync = previousFetcher;
            MediaAutoplayPolicy.Default.Mode = previousMode;
        }
    }

    /// <summary>A canonical 16-bit mono 48 kHz RIFF WAVE with a 440 Hz tone.</summary>
    private static byte[] SineWav(int seconds)
    {
        const int rate = 48000;
        int frames = rate * seconds;
        var data = new byte[frames * 2];
        for (int i = 0; i < frames; i++)
        {
            short sample = (short)(Math.Sin(2 * Math.PI * 440 * i / rate) * 8000);
            BitConverter.TryWriteBytes(data.AsSpan(i * 2), sample);
        }

        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
        writer.Write("RIFF"u8);
        writer.Write(36 + data.Length);
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(rate);
        writer.Write(rate * 2);
        writer.Write((short)2);
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(data.Length);
        writer.Write(data);
        writer.Flush();
        return stream.ToArray();
    }

    /// <summary>
    /// DEFINITION_OF_DONE: a new IPC message type gets 10,000 mutated iterations through the
    /// fuzz harness. The media child must answer every mutated open, read, seek and close with
    /// a protocol error and keep no session, because no mutated open can name a real region.
    /// </summary>
    [Fact]
    public void TheMediaIpcSurvivesTenThousandMutatedEnvelopes()
    {
        var endpoint = new MediaIpcFuzzEndpoint();
        var mutator = new StructuredMutator(seed: 20260918);
        var seeds = IpcFuzzHarness.MediaIpcSeeds().ToArray();
        var failures = new List<string>();

        for (var i = 0; i < 10_000; i++)
        {
            var mutated = mutator.MutateJson(seeds[i % seeds.Length]);
            if (!endpoint.Fuzz(mutated))
            {
                failures.Add(Convert.ToBase64String(mutated));
                if (failures.Count >= 5) break;
            }
        }

        Assert.True(failures.Count == 0, "media IPC fuzz failures: " + string.Join(" | ", failures));
    }

    private static string FindTestAssets()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "test_assets");
            if (Directory.Exists(Path.Combine(candidate, "media")))
            {
                return candidate;
            }
        }

        throw new DirectoryNotFoundException("test_assets/media not found above " + AppContext.BaseDirectory);
    }

    private static async Task<string> WaitForAsync(FenJsBrowserScriptEngine engine, string expression)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.ElapsedMilliseconds < 10000)
        {
            var value = engine.Evaluate(expression)?.ToString();
            if (!string.IsNullOrEmpty(value))
            {
                return value;
            }

            await Task.Delay(25);
        }

        return engine.Evaluate(expression)?.ToString() ?? string.Empty;
    }

    private static async Task<FenJsBrowserScriptEngine> CreateEngineAsync(string html)
    {
        var baseUri = new Uri("https://example.test/");
        var document = new HtmlParser(html, baseUri).Parse();
        var engine = new FenJsBrowserScriptEngine(new JsHostAdapter(
            navigate: _ => { },
            post: (_, _) => { },
            status: _ => { },
            log: _ => { }))
        {
            Sandbox = SandboxPolicy.AllowAll
        };
        await engine.SetDomAsync(document.DocumentElement, baseUri);
        return engine;
    }
}
