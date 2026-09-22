using System.Text;
using FenBrowser.Media.Codecs.Ffmpeg;
using FenBrowser.Media.Codecs.MediaFoundation;
using FenBrowser.Media.Diagnostics;
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
}
