using FenBrowser.Media.Buffers;
using FenBrowser.Media.Containers.Mp4;
using FenBrowser.Media.Eme;
using FenBrowser.Media.Mse;
using FenBrowser.Media.Pipeline;

namespace FenBrowser.Media.Tests;

/// <summary>
/// ISO/IEC 23001-7 Common Encryption as it is carried in an MP4, checked against the
/// <c>encrypted-media</c> WPT content: the same media is there both encrypted and in the
/// clear, so decrypting one has to produce the other byte for byte. That is a cross-check
/// against a muxer this engine had no part in.
/// </summary>
public class Mp4EncryptionTests
{
    /// <summary>The key the WPT content is encrypted with (content-metadata.js, "mp4-basic").</summary>
    private static readonly byte[] s_key =
        [0xbe, 0x7d, 0xf8, 0xa3, 0x66, 0x7a, 0x6a, 0x8f, 0xd5, 0x64, 0xd0, 0xed, 0x81, 0x33, 0x9a, 0x95];

    private static readonly byte[] s_keyId =
        [0xad, 0x13, 0xf9, 0xea, 0x2b, 0xe6, 0x98, 0xb8, 0x75, 0xf5, 0x04, 0xa8, 0xe3, 0xcc, 0xea, 0x64];

    /// <summary>The second key of the WPT "mp4-av-multikey" content, which its audio uses.</summary>
    private static readonly byte[] s_audioKeyId =
        [0x55, 0x8e, 0xe5, 0x41, 0xb9, 0x0a, 0xb2, 0xf3, 0x95, 0x0d, 0x00, 0xad, 0xe3, 0x76, 0x0d, 0x45];

    private static readonly byte[] s_audioKey =
        [0x91, 0x03, 0x92, 0x63, 0x01, 0x6d, 0xa6, 0x35, 0x77, 0x0d, 0x57, 0xdb, 0x92, 0xf9, 0x8b, 0xd0];

    /// <summary>The Clear Key licence for this content: both keys, looked up by key ID.</summary>
    private static byte[] KeyFor(KeyId keyId) =>
        keyId == new KeyId(s_audioKeyId) ? s_audioKey : s_key;

    private static string? ContentRoot()
    {
        var root = Environment.GetEnvironmentVariable("FEN_WPT_ROOT") ?? @"D:\wpt";
        string content = Path.Combine(root, "encrypted-media", "content");
        return Directory.Exists(content) ? content : null;
    }

    private static async Task<List<EncodedPacket>> ReadAll(string path)
    {
        var context = MediaPipelineContext.ForTests();
        await using var demuxer = Mp4DemuxerFactory.Instance.Create(new MemoryByteSource(File.ReadAllBytes(path)), context);
        await demuxer.InitializeAsync(CancellationToken.None);

        var packets = new List<EncodedPacket>();
        while (await demuxer.ReadPacketAsync(CancellationToken.None) is { } packet)
            packets.Add(packet);
        return packets;
    }

    private static async Task<DemuxerInfo> Open(string path, List<EncodedPacket> packets)
    {
        var context = MediaPipelineContext.ForTests();
        await using var demuxer = Mp4DemuxerFactory.Instance.Create(new MemoryByteSource(File.ReadAllBytes(path)), context);
        var info = await demuxer.InitializeAsync(CancellationToken.None);
        while (await demuxer.ReadPacketAsync(CancellationToken.None) is { } packet)
            packets.Add(packet);
        return info;
    }

    [Fact]
    public async Task AnEncryptedTrackStillNamesItsCodec()
    {
        string? content = ContentRoot();
        if (content is null)
            return;

        var packets = new List<EncodedPacket>();
        var info = await Open(Path.Combine(content, "video_512x288_h264-360k_enc_dashinit.mp4"), packets);
        try
        {
            // An 'encv' entry must answer with the codec its 'sinf' names, or the element
            // would report a track it cannot play.
            var video = Assert.Single(info.Tracks);
            Assert.Equal(MediaCodec.H264, video.Config.Codec);
            Assert.Equal(512, video.Config.Width);
            Assert.Equal(288, video.Config.Height);
        }
        finally
        {
            packets.ForEach(p => p.Dispose());
        }
    }

    [Fact]
    public async Task TheFilesPsshBoxesBecomeCencInitializationData()
    {
        string? content = ContentRoot();
        if (content is null)
            return;

        var packets = new List<EncodedPacket>();
        var info = await Open(Path.Combine(content, "video_512x288_h264-360k_enc_dashinit.mp4"), packets);
        try
        {
            var (initDataType, initData) = Assert.Single(info.InitializationData);
            Assert.Equal("cenc", initDataType);
            Assert.True(EmeInitData.TryParsePsshBoxes(initData, out var boxes));
            Assert.NotEmpty(boxes);
        }
        finally
        {
            packets.ForEach(p => p.Dispose());
        }
    }

    [Fact]
    public async Task EveryEncryptedSampleCarriesItsKeyIdAndIv()
    {
        string? content = ContentRoot();
        if (content is null)
            return;

        var packets = await ReadAll(Path.Combine(content, "video_512x288_h264-360k_enc_dashinit.mp4"));
        try
        {
            Assert.NotEmpty(packets);
            var encrypted = packets.Where(p => p.Encryption is not null).ToList();
            Assert.NotEmpty(encrypted);

            foreach (var packet in encrypted)
            {
                var info = packet.Encryption!;
                Assert.Equal(s_keyId, info.KeyId.ToArray());
                Assert.Equal(CencScheme.Cenc, info.Scheme);
                Assert.Contains(info.Iv.Length, (int[])[8, 16]);
                // H.264 in CENC is subsampled: the NAL headers stay in the clear.
                Assert.NotEmpty(info.Subsamples);
            }
        }
        finally
        {
            packets.ForEach(p => p.Dispose());
        }
    }

    [Fact]
    public async Task TheEncryptedStreamDecryptsToTheClearOne()
    {
        string? content = ContentRoot();
        if (content is null)
            return;

        var encrypted = await ReadAll(Path.Combine(content, "video_512x288_h264-360k_enc_dashinit.mp4"));
        var clear = await ReadAll(Path.Combine(content, "video_512x288_h264-360k_clear_dashinit.mp4"));
        try
        {
            Assert.NotEmpty(encrypted);
            Assert.Equal(clear.Count, encrypted.Count);

            using var decryptor = new CencDecryptor();
            int decrypted = 0;
            for (int i = 0; i < encrypted.Count; i++)
            {
                Assert.Equal(clear[i].Pts, encrypted[i].Pts);
                Assert.Equal(clear[i].Length, encrypted[i].Length);

                if (encrypted[i].Encryption is { } info)
                {
                    Assert.True(decryptor.TryDecrypt(encrypted[i].Memory.Span, info, s_key));
                    decrypted++;
                }

                Assert.Equal(clear[i].Span.ToArray(), encrypted[i].Span.ToArray());
            }

            Assert.True(decrypted > 0, "the encrypted stream had no encrypted samples");
        }
        finally
        {
            encrypted.ForEach(p => p.Dispose());
            clear.ForEach(p => p.Dispose());
        }
    }

    [Fact]
    public async Task ASeigSampleGroupGivesEachSampleRunItsOwnKey()
    {
        string? content = ContentRoot();
        if (content is null)
            return;

        // The WPT "mp4-multikey" video is encrypted with two keys, and a 'seig' sample
        // group says which of them each run of samples needs.
        byte[] firstKeyId = [0x13, 0xa7, 0x53, 0x06, 0xd1, 0x18, 0x91, 0x7b, 0x47, 0xa6, 0xc1, 0x83, 0x64, 0x42, 0x51, 0x6f];
        byte[] secondKeyId = [0xee, 0x73, 0x56, 0x4e, 0xc8, 0xa8, 0x90, 0xf0, 0x78, 0xef, 0x68, 0x71, 0xfa, 0x4b, 0xe1, 0x8b];

        var packets = await ReadAll(Path.Combine(content, "video_512x288_h264-360k_enc_2keys_2sess.mp4"));
        try
        {
            var keyIds = packets
                .Where(p => p.Encryption is not null)
                .Select(p => p.Encryption!.KeyId)
                .Distinct()
                .ToList();

            Assert.Equal(2, keyIds.Count);
            Assert.Contains(new KeyId(firstKeyId), keyIds);
            Assert.Contains(new KeyId(secondKeyId), keyIds);
        }
        finally
        {
            packets.ForEach(p => p.Dispose());
        }
    }

    [Fact]
    public async Task TheEncryptedAudioStreamDecryptsToo()
    {
        string? content = ContentRoot();
        if (content is null)
            return;

        var encrypted = await ReadAll(Path.Combine(content, "audio_aac-lc_128k_enc_dashinit.mp4"));
        var clear = await ReadAll(Path.Combine(content, "audio_aac-lc_128k_dashinit.mp4"));
        try
        {
            Assert.NotEmpty(encrypted);
            Assert.Equal(clear.Count, encrypted.Count);
            Assert.True(
                encrypted.Any(p => p.Encryption is not null),
                $"no audio sample was described as encrypted (of {encrypted.Count})");

            using var decryptor = new CencDecryptor();
            int decrypted = 0;
            for (int i = 0; i < encrypted.Count; i++)
            {
                if (encrypted[i].Encryption is { } info)
                {
                    Assert.True(decryptor.TryDecrypt(encrypted[i].Memory.Span, info, KeyFor(info.KeyId)));
                    decrypted++;
                }

                Assert.Equal(clear[i].Span.ToArray(), encrypted[i].Span.ToArray());
            }

            Assert.True(decrypted > 0, "the encrypted stream had no encrypted samples");
        }
        finally
        {
            encrypted.ForEach(p => p.Dispose());
            clear.ForEach(p => p.Dispose());
        }
    }

    /// <summary>
    /// The same file through the MSE byte stream parser, which hands the demuxer the
    /// initialization segment followed by one media segment at a time: every sample that
    /// carries its encryption in the progressive read has to carry it here too, or a
    /// MediaSource plays protected content as noise instead of waiting for a key.
    /// </summary>
    [Fact]
    public void EverySampleAppendedThroughMseCarriesItsEncryption()
    {
        string? content = ContentRoot();
        if (content is null)
            return;

        var parser = new Mp4SegmentParser(MediaPipelineContext.ForTests());
        var packets = new List<EncodedPacket>();
        try
        {
            parser.Append(
                File.ReadAllBytes(Path.Combine(content, "video_512x288_h264-360k_enc_dashinit.mp4")),
                _ => { },
                packets.Add);

            Assert.NotEmpty(packets);
            Assert.All(packets, packet => Assert.NotNull(packet.Encryption));
        }
        finally
        {
            packets.ForEach(p => p.Dispose());
        }
    }
}
