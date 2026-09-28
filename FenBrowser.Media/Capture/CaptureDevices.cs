using FenBrowser.Media.Streams;
using FenBrowser.Media.WebAudio;

namespace FenBrowser.Media.Capture;

/// <summary>
/// One capture device a page may ask getUserMedia for (mediacapture-main 9.2 MediaDeviceInfo):
/// the platform's identifier, never shown to a page as is, and what the device produces.
/// </summary>
public sealed record CaptureDeviceInfo(
    string DeviceId,
    string GroupId,
    string Label,
    MediaTrackKind Kind,
    int Width = 0,
    int Height = 0,
    double FrameRate = 0,
    int SampleRate = 0,
    int Channels = 0);

/// <summary>
/// An open device: its audio as an <see cref="AudioTrackPipe"/> or its pictures as a
/// <see cref="VideoTrackSource"/> - the sources a MediaStreamTrack carries - until disposed.
/// </summary>
public interface ICaptureSession : IDisposable
{
    CaptureDeviceInfo Device { get; }

    AudioTrackPipe? Audio { get; }

    VideoTrackSource? Video { get; }
}

/// <summary>A platform's capture devices (ADR-0003's counterpart for input).</summary>
public interface ICaptureDeviceProvider
{
    IReadOnlyList<CaptureDeviceInfo> Devices { get; }

    /// <summary>Opens a device, or returns null when it is gone or cannot be opened.</summary>
    ICaptureSession? Open(string deviceId);
}

/// <summary>No devices: what a machine without a microphone or camera offers.</summary>
public sealed class NoCaptureDeviceProvider : ICaptureDeviceProvider
{
    public static readonly NoCaptureDeviceProvider Instance = new();

    public IReadOnlyList<CaptureDeviceInfo> Devices => [];

    public ICaptureSession? Open(string deviceId) => null;
}

/// <summary>
/// The capture devices the browser offers. <c>FEN_MEDIA_FAKE_DEVICES=1</c> replaces the
/// platform's with a fake microphone and camera, the way Chromium's
/// <c>--use-fake-device-for-media-stream</c> does for testing.
/// </summary>
public static class CaptureDevices
{
    private static ICaptureDeviceProvider? _provider;

    public static ICaptureDeviceProvider Provider
    {
        get => Volatile.Read(ref _provider) ?? DefaultProvider();
        set => Volatile.Write(ref _provider, value);
    }

    /// <summary>
    /// <c>FEN_MEDIA_FAKE_UI=1</c>: a capture request is granted without asking, like
    /// Chromium's <c>--use-fake-ui-for-media-stream</c>.
    /// </summary>
    public static bool AutoGrant =>
        string.Equals(Environment.GetEnvironmentVariable("FEN_MEDIA_FAKE_UI"), "1", StringComparison.Ordinal);

    private static ICaptureDeviceProvider DefaultProvider()
    {
        var provider = string.Equals(Environment.GetEnvironmentVariable("FEN_MEDIA_FAKE_DEVICES"), "1", StringComparison.Ordinal)
            ? (ICaptureDeviceProvider)new FakeCaptureDeviceProvider()
            : NoCaptureDeviceProvider.Instance;
        Interlocked.CompareExchange(ref _provider, provider, null);
        return _provider!;
    }
}
