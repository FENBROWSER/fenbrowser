using FenBrowser.Media.Diagnostics;

namespace FenBrowser.Media.Tests;

public class MediaLimitsTests
{
    [Theory]
    [InlineData(0, 10)]
    [InlineData(10, -1)]
    [InlineData(8193, 10)]
    [InlineData(10, 8193)]
    [InlineData(8192, 8192)] // each side fits, pixel count does not
    public void CheckVideoDimensions_RejectsOutOfRange(int width, int height)
    {
        Assert.Throws<MediaLimitExceededException>(() => MediaLimits.Default.CheckVideoDimensions(width, height));
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(1920, 1080)]
    [InlineData(7680, 4320)]
    public void CheckVideoDimensions_AcceptsSupportedSizes(int width, int height)
    {
        MediaLimits.Default.CheckVideoDimensions(width, height);
    }

    [Fact]
    public void CheckPacketSize_UsesPerKindCeiling()
    {
        var limits = MediaLimits.Default;
        limits.CheckPacketSize(MediaTrackKind.Video, 2 * 1024 * 1024);
        var ex = Assert.Throws<MediaLimitExceededException>(
            () => limits.CheckPacketSize(MediaTrackKind.Audio, 2 * 1024 * 1024));
        Assert.Equal(nameof(MediaLimits.MaxAudioPacketBytes), ex.Limit);
        Assert.Throws<MediaLimitExceededException>(() => limits.CheckPacketSize(MediaTrackKind.Video, -1));
    }

    [Fact]
    public void CheckAudioFormat_RejectsHostileValues()
    {
        MediaLimits.Default.CheckAudioFormat(48_000, 2);
        Assert.Throws<MediaLimitExceededException>(() => MediaLimits.Default.CheckAudioFormat(0, 2));
        Assert.Throws<MediaLimitExceededException>(() => MediaLimits.Default.CheckAudioFormat(48_000, 255));
    }

    [Fact]
    public void Limits_CanBeTightenedPerPlayer()
    {
        var strict = MediaLimits.Default with { MaxVideoWidth = 640 };
        Assert.Throws<MediaLimitExceededException>(() => strict.CheckVideoDimensions(1280, 720));
    }

    [Fact]
    public void Report_EmitsLimitExceededEventWithFields()
    {
        var sink = new RecordingMediaLogSink();
        var player = PlayerId.Next();
        var ex = new MediaLimitExceededException(nameof(MediaLimits.MaxTracks), 99, 64);

        ex.Report(sink, player);

        var e = Assert.Single(sink.Events);
        Assert.Equal(MediaEventKind.LimitExceeded, e.Kind);
        Assert.Equal(player, e.Player);
        Assert.Contains(new KeyValuePair<string, string>("limit", "MaxTracks"), e.Fields!);
        Assert.Contains(new KeyValuePair<string, string>("value", "99"), e.Fields!);
        Assert.Contains(new KeyValuePair<string, string>("max", "64"), e.Fields!);
    }

    [Fact]
    public void NullSink_SkipsEventConstruction()
    {
        NullMediaLogSink.Instance.Emit(PlayerId.None, MediaEventKind.Error, MediaLogLevel.Error, "ignored", ("k", "v"));
        Assert.False(NullMediaLogSink.Instance.IsEnabled(MediaLogLevel.Error));
    }

    [Fact]
    public void PlayerIds_AreUniqueAndReadable()
    {
        var a = PlayerId.Next();
        var b = PlayerId.Next();
        Assert.NotEqual(a, b);
        Assert.StartsWith("player-", a.ToString(), StringComparison.Ordinal);
    }
}
