using System.Globalization;
using FenBrowser.Media.Diagnostics;

namespace FenBrowser.Media;

/// <summary>
/// Hard ceilings applied to hostile media before any allocation
/// (docs/MEDIA_ENGINE_DESIGN.md §4). Parsers check these and throw
/// <see cref="MediaLimitExceededException"/>; they never clamp silently.
/// </summary>
public sealed record MediaLimits
{
    public static readonly MediaLimits Default = new();

    public int MaxVideoWidth { get; init; } = 8192;
    public int MaxVideoHeight { get; init; } = 8192;

    /// <summary>8K UHD (7680 × 4320).</summary>
    public long MaxVideoPixels { get; init; } = 7680L * 4320L;

    public int MaxTracks { get; init; } = 64;
    public int MaxSampleTableEntries { get; init; } = 16_000_000;
    public int MaxVideoPacketBytes { get; init; } = 64 * 1024 * 1024;
    public int MaxAudioPacketBytes { get; init; } = 1024 * 1024;
    public int MaxContainerNestingDepth { get; init; } = 32;
    public int MaxQueuedVideoFrames { get; init; } = 8;
    public MediaTime MaxQueuedAudio { get; init; } = MediaTime.FromSeconds(2);
    public int MaxAudioChannels { get; init; } = 32;
    public int MaxAudioSampleRate { get; init; } = 384_000;
    public long MseVideoBufferQuotaBytes { get; init; } = 150L * 1024 * 1024;
    public long MseAudioBufferQuotaBytes { get; init; } = 12L * 1024 * 1024;
    public int MaxPlayersPerProcess { get; init; } = 64;

    /// <summary>Throws unless the dimensions are positive and within every video ceiling.</summary>
    public void CheckVideoDimensions(int width, int height)
    {
        if (width <= 0 || height <= 0)
            throw new MediaLimitExceededException("VideoDimensions", $"{width}x{height}", "positive");
        if (width > MaxVideoWidth)
            throw new MediaLimitExceededException(nameof(MaxVideoWidth), width, MaxVideoWidth);
        if (height > MaxVideoHeight)
            throw new MediaLimitExceededException(nameof(MaxVideoHeight), height, MaxVideoHeight);
        if ((long)width * height > MaxVideoPixels)
            throw new MediaLimitExceededException(nameof(MaxVideoPixels), (long)width * height, MaxVideoPixels);
    }

    public void CheckPacketSize(MediaTrackKind kind, int bytes)
    {
        int max = kind == MediaTrackKind.Video ? MaxVideoPacketBytes : MaxAudioPacketBytes;
        if (bytes < 0 || bytes > max)
            throw new MediaLimitExceededException(
                kind == MediaTrackKind.Video ? nameof(MaxVideoPacketBytes) : nameof(MaxAudioPacketBytes), bytes, max);
    }

    public void CheckAudioFormat(int sampleRate, int channels)
    {
        if (sampleRate <= 0 || sampleRate > MaxAudioSampleRate)
            throw new MediaLimitExceededException(nameof(MaxAudioSampleRate), sampleRate, MaxAudioSampleRate);
        if (channels <= 0 || channels > MaxAudioChannels)
            throw new MediaLimitExceededException(nameof(MaxAudioChannels), channels, MaxAudioChannels);
    }
}

/// <summary>
/// A media resource broke a <see cref="MediaLimits"/> ceiling. The element maps this to
/// <c>MEDIA_ERR_DECODE</c> (or <c>MEDIA_ERR_SRC_NOT_SUPPORTED</c> before metadata).
/// </summary>
public sealed class MediaLimitExceededException : Exception
{
    public MediaLimitExceededException()
        : this("Unknown", "?", "?")
    {
    }

    public MediaLimitExceededException(string message)
        : base(message)
    {
        Limit = "Unknown";
        Value = "?";
        Maximum = "?";
    }

    public MediaLimitExceededException(string message, Exception innerException)
        : base(message, innerException)
    {
        Limit = "Unknown";
        Value = "?";
        Maximum = "?";
    }

    public MediaLimitExceededException(string limit, long value, long maximum)
        : this(limit, value.ToString(CultureInfo.InvariantCulture), maximum.ToString(CultureInfo.InvariantCulture))
    {
    }

    public MediaLimitExceededException(string limit, string value, string maximum)
        : base($"Media limit {limit} exceeded: {value} (maximum {maximum}).")
    {
        Limit = limit;
        Value = value;
        Maximum = maximum;
    }

    public string Limit { get; }
    public string Value { get; }
    public string Maximum { get; }

    /// <summary>Reports this breach as a <see cref="MediaEventKind.LimitExceeded"/> event.</summary>
    public void Report(IMediaLogSink sink, PlayerId player)
    {
        sink.Emit(player, MediaEventKind.LimitExceeded, MediaLogLevel.Warn, Message,
            ("limit", Limit), ("value", Value), ("max", Maximum));
    }
}
