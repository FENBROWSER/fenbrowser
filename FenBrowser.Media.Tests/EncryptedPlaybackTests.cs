using System.Text;
using FenBrowser.Media.Audio;
using FenBrowser.Media.Codecs.Ffmpeg;
using FenBrowser.Media.Codecs.MediaFoundation;
using FenBrowser.Media.Diagnostics;
using FenBrowser.Media.Element;
using FenBrowser.Media.Eme;
using FenBrowser.Media.Pipeline;

namespace FenBrowser.Media.Tests;

/// <summary>
/// Encrypted playback end to end: the decode source stalls on a packet whose key has not
/// arrived (EME §7.2, which is what makes the element fire <c>waitingforkey</c>), and
/// decodes that same packet once the Clear Key CDM has been given a licence for it.
/// </summary>
public class EncryptedPlaybackTests
{
    private const string KeyIdBase64 = "rRP56ivmmLh19QSo48zqZA";

    private const string License =
        "{\"keys\":[{\"kty\":\"oct\",\"kid\":\"" + KeyIdBase64 + "\",\"k\":\"vn34o2Z6ao_VZNDtgTOalQ\"}]}";

    private static string? EncryptedVideo()
    {
        var root = Environment.GetEnvironmentVariable("FEN_WPT_ROOT") ?? @"D:\wpt";
        string path = Path.Combine(root, "encrypted-media", "content", "video_512x288_h264-360k_enc_dashinit.mp4");
        return File.Exists(path) ? path : null;
    }

    private static LocalMediaDecodeSource Open(string path)
    {
        var demuxers = new DemuxerRegistry();
        var decoders = new DecoderRegistry();
        MediaFormats.RegisterBuiltIn(demuxers, decoders);
        FfmpegDecoders.TryRegister(decoders, NullMediaLogSink.Instance);
        MediaFoundationDecoders.TryRegister(decoders, NullMediaLogSink.Instance);
        return new LocalMediaDecodeSource(
            new MemoryByteSource(File.ReadAllBytes(path)),
            "video/mp4",
            demuxers,
            decoders,
            MediaPipelineContext.ForTests());
    }

    /// <summary>A CDM that has already been handed the licence for the WPT content.</summary>
    private static ClearKeyCdm CdmWithTheKey()
    {
        var cdm = new ClearKeyCdm();
        var session = cdm.CreateSession(MediaKeySessionType.Temporary);
        Assert.NotNull(session);
        Assert.True(session.GenerateRequest(
            EmeInitDataType.KeyIds,
            Encoding.UTF8.GetBytes("{\"kids\":[\"" + KeyIdBase64 + "\"]}")).Succeeded);
        Assert.True(session.Update(Encoding.ASCII.GetBytes(License)).Succeeded);
        return cdm;
    }

    [Fact]
    public async Task WithoutAKeyTheSourceStallsInsteadOfDecoding()
    {
        string? path = EncryptedVideo();
        if (path is null)
            return;

        await using var source = Open(path);
        await source.OpenAsync(CancellationToken.None);

        // Reading gets as far as the first protected packet and stops there. It is not the
        // end of the stream, and nothing was decoded from ciphertext.
        var decoded = await source.ReadAsync(CancellationToken.None);
        decoded?.Dispose();
        Assert.True(source.WaitingForKey);
    }

    [Fact]
    public async Task TheStalledPacketDecodesOnceTheLicenceArrives()
    {
        string? path = EncryptedVideo();
        if (path is null)
            return;

        await using var source = Open(path);
        var info = await source.OpenAsync(CancellationToken.None);
        Assert.NotNull(info.VideoTrack);

        (await source.ReadAsync(CancellationToken.None))?.Dispose();
        Assert.True(source.WaitingForKey);

        await source.SetMediaKeysAsync(CdmWithTheKey(), CancellationToken.None);

        // The packet that was held back is read again, decrypted and decoded; nothing in
        // the stream was skipped over while it waited.
        int pictures = 0;
        for (int i = 0; i < 12 && pictures == 0; i++)
        {
            using var item = await source.ReadAsync(CancellationToken.None);
            if (item?.Video is not null)
                pictures++;
        }

        Assert.False(source.WaitingForKey);
        Assert.True(pictures > 0, "no picture came out of the encrypted stream after the licence arrived");
    }

    [Fact]
    public async Task AKeyForSomeoneElsesContentDoesNotUnblockIt()
    {
        string? path = EncryptedVideo();
        if (path is null)
            return;

        var cdm = new ClearKeyCdm();
        var session = cdm.CreateSession(MediaKeySessionType.Temporary);
        Assert.NotNull(session);
        session.GenerateRequest(EmeInitDataType.KeyIds, Encoding.UTF8.GetBytes("{\"kids\":[\"MDEyMzQ1Njc4OTAxMjM0NQ\"]}"));
        Assert.True(session.Update(Encoding.ASCII.GetBytes(
            "{\"keys\":[{\"kty\":\"oct\",\"kid\":\"MDEyMzQ1Njc4OTAxMjM0NQ\",\"k\":\"MDEyMzQ1Njc4OTAxMjM0NQ\"}]}")).Succeeded);

        await using var source = Open(path);
        await source.OpenAsync(CancellationToken.None);
        await source.SetMediaKeysAsync(cdm, CancellationToken.None);

        (await source.ReadAsync(CancellationToken.None))?.Dispose();
        Assert.True(source.WaitingForKey);
    }

    /// <summary>
    /// A client that counts what the element would turn into <c>waitingforkey</c> events.
    /// </summary>
    private sealed class KeyClient : IMediaResourceClient
    {
        private int _waiting;

        public int WaitingForKeyCount => Volatile.Read(ref _waiting);
        public MediaReadyState ReadyState { get; private set; }
        public string? Failure { get; private set; }

        public void Failed(MediaResourceFailure failure, string message) => Failure = failure + ": " + message;
        public void MetadataAvailable(MediaResourceMetadata metadata) { }
        public void ReadyStateChanged(MediaReadyState state) => ReadyState = state;
        public void DurationChanged(MediaTime duration) { }
        public void VideoSizeChanged(int width, int height) { }
        public void PositionChanged(MediaTime position, bool monotonic) { }
        public void ReachedEnd() { }
        public void SeekCompleted(MediaTime position) { }
        public void BufferedChanged(MediaTimeRanges buffered) { }
        public void SeekableChanged(MediaTimeRanges seekable) { }
        public void Progress() { }
        public void Suspended() { }
        public void Resumed() { }
        public void Stalled() { }
        public void FetchedEntirely() { }
        public void WaitingForKey() => Interlocked.Increment(ref _waiting);

        public async Task WaitForAsync(Func<bool> condition, int timeoutMs = 10000)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (!condition())
            {
                if (DateTime.UtcNow > deadline)
                    throw new TimeoutException("Condition not met. Failure: " + Failure + ", waiting: " + WaitingForKeyCount);
                await Task.Delay(10);
            }
        }
    }

    /// <summary>
    /// EME §7.2: the element says it is waiting for a key once per stall, not once per read
    /// attempt, and the licence that arrives afterwards wakes the player itself - the CDM is
    /// updated from the page's thread long after the keys were handed to the pipeline, so
    /// nothing else would tell the media task to try that packet again.
    /// </summary>
    [Fact]
    public async Task TheElementWaitsForAKeyOnceAndThePlayerResumesWhenTheLicenceArrives()
    {
        string? path = EncryptedVideo();
        if (path is null)
            return;

        var demuxers = new DemuxerRegistry();
        var decoders = new DecoderRegistry();
        MediaFormats.RegisterBuiltIn(demuxers, decoders);
        FfmpegDecoders.TryRegister(decoders, NullMediaLogSink.Instance);
        MediaFoundationDecoders.TryRegister(decoders, NullMediaLogSink.Instance);

        var client = new KeyClient();
        var services = new MediaPlayerServices(demuxers, decoders, new NullAudioOutputFactory(realtime: false), TimeProvider.System);
        var player = new MediaPlayer(
            new MemoryByteSource(File.ReadAllBytes(path)), "video/mp4", client, action => action(), services,
            MediaPipelineContext.ForTests());

        // A CDM that has been given no licence at all: every protected packet stalls.
        var cdm = new ClearKeyCdm();
        var session = cdm.CreateSession(MediaKeySessionType.Temporary);
        Assert.NotNull(session);
        Assert.True(session.GenerateRequest(
            EmeInitDataType.KeyIds,
            Encoding.UTF8.GetBytes("{\"kids\":[\"" + KeyIdBase64 + "\"]}")).Succeeded);

        player.SetMediaKeys(cdm);
        player.Start();
        try
        {
            await client.WaitForAsync(() => client.WaitingForKeyCount > 0);

            // The player keeps trying that packet every tick; the element hears about it once.
            await Task.Delay(400);
            Assert.Equal(1, client.WaitingForKeyCount);
            Assert.True(client.ReadyState < MediaReadyState.HaveCurrentData);

            // A licence for some other key wakes the player, but it is still the same stall
            // (EME §7.4 "playback blocked waiting for key"): no second event.
            var other = cdm.CreateSession(MediaKeySessionType.Temporary);
            Assert.NotNull(other);
            Assert.True(other.GenerateRequest(
                EmeInitDataType.KeyIds,
                Encoding.UTF8.GetBytes("{\"kids\":[\"AAAAAAAAAAAAAAAAAAAAAA\"]}")).Succeeded);
            Assert.True(other.Update(Encoding.ASCII.GetBytes(
                "{\"keys\":[{\"kty\":\"oct\",\"kid\":\"AAAAAAAAAAAAAAAAAAAAAA\",\"k\":\"AAAAAAAAAAAAAAAAAAAAAA\"}]}")).Succeeded);
            await Task.Delay(400);
            Assert.Equal(1, client.WaitingForKeyCount);

            // The page's update() call comes in on its own thread; the CDM tells the player.
            Assert.True(session.Update(Encoding.ASCII.GetBytes(License)).Succeeded);

            await client.WaitForAsync(() => client.ReadyState >= MediaReadyState.HaveCurrentData);
            Assert.Null(client.Failure);
        }
        finally
        {
            player.Dispose();
        }
    }
}
