using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using FenBrowser.Core;
using FenBrowser.Core.Logging;
using FenBrowser.FenEngine.Media;
using FenBrowser.Js.Runtime;
using FenBrowser.Media;
using FenBrowser.Media.Buffers;
using FenBrowser.Media.Diagnostics;
using FenBrowser.Media.Pipeline;
using FenBrowser.Media.Types;

namespace FenBrowser.FenEngine.Scripting;

/// <summary>
/// The WebCodecs decoders (https://w3c.github.io/webcodecs/ §3): <c>AudioDecoder</c> and
/// <c>VideoDecoder</c> over the same demuxer-less decoder registry the media element
/// uses, so a page gets exactly the codecs this engine has. The control flow - state
/// machine, queues, callbacks, the error path - is WebIDL work and lives in the prelude;
/// these natives are one decoder each: configure, decode, flush, reset, close.
/// </summary>
public sealed partial class FenJsBrowserScriptEngine
{
    private readonly Dictionary<int, WebCodecsDecoderSession> _webCodecsDecoders = new();
    private int _nextWebCodecsDecoder = 1;

    /// <summary>One configured WebCodecs decoder and the frames it has produced.</summary>
    private sealed class WebCodecsDecoderSession : IDisposable
    {
        public IMediaDecoder<AudioBlock>? Audio { get; init; }
        public IMediaDecoder<VideoFrame>? Video { get; init; }
        public MediaPipelineContext Context { get; init; } = null!;
        public AudioOutput AudioSink { get; } = new();
        public VideoOutput VideoSink { get; } = new();

        public void Dispose()
        {
            AudioSink.DisposeAll();
            VideoSink.DisposeAll();
            Audio?.DisposeAsync().AsTask().GetAwaiter().GetResult();
            Video?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        public sealed class AudioOutput : IDecodeOutput<AudioBlock>
        {
            public List<AudioBlock> Items { get; } = [];
            public void Emit(AudioBlock item) => Items.Add(item);
            public void DisposeAll()
            {
                foreach (var item in Items)
                    item.Dispose();
                Items.Clear();
            }
        }

        public sealed class VideoOutput : IDecodeOutput<VideoFrame>
        {
            public List<VideoFrame> Items { get; } = [];
            public void Emit(VideoFrame item) => Items.Add(item);
            public void DisposeAll()
            {
                foreach (var item in Items)
                    item.Dispose();
                Items.Clear();
            }
        }
    }

    private void InstallFenJsWebCodecsDecoders()
    {
        _interpreter.RegisterGlobalValue("__fenCodecSupported",
            _interpreter.AllocateNativeFunction("__fenCodecSupported", (_, args) => JsValue.FromBoolean(IsWebCodecsConfigSupported(args)), length: 2));
        _interpreter.RegisterGlobalValue("__fenCodecConfigure",
            _interpreter.AllocateNativeFunction("__fenCodecConfigure", (_, args) => JsValue.FromInt32(ConfigureWebCodecsDecoder(args)), length: 3));
        _interpreter.RegisterGlobalValue("__fenCodecDecode",
            _interpreter.AllocateNativeFunction("__fenCodecDecode", (_, args) => DecodeWebCodecsChunk(args), length: 5));
        _interpreter.RegisterGlobalValue("__fenCodecFlush",
            _interpreter.AllocateNativeFunction("__fenCodecFlush", (_, args) => FlushWebCodecsDecoder(args), length: 1));
        _interpreter.RegisterGlobalValue("__fenCodecReset",
            _interpreter.AllocateNativeFunction("__fenCodecReset", (_, args) => ResetWebCodecsDecoder(args), length: 1));
        _interpreter.RegisterGlobalValue("__fenCodecClose",
            _interpreter.AllocateNativeFunction("__fenCodecClose", (_, args) => CloseWebCodecsDecoder(args), length: 1));
    }

    /// <summary>Reads a WebCodecs decoder configuration out of the native arguments.</summary>
    private static CodecConfig? ReadWebCodecsConfig(IReadOnlyList<JsValue> args, int at, Func<int, string?> str, Func<int, double> num, Func<int, byte[]> bytes)
    {
        _ = args;
        string? kind = str(at);
        string? codecString = str(at + 1);
        if (codecString is null)
            return null;

        // The codec string is taken as the page wrote it: one padded with whitespace names
        // no registered codec, which is what WebCodecs expects it to answer.
        var parsed = CodecString.Parse(codecString);
        if (parsed is null)
            return null;

        bool wantsVideo = kind == "video";
        if (wantsVideo != (parsed.Kind == MediaTrackKind.Video))
            return null;

        return new CodecConfig(
            parsed.Kind,
            parsed.Codec,
            parsed.Raw,
            SampleRate: (int)num(at + 2),
            Channels: (int)num(at + 3),
            Width: (int)num(at + 4),
            Height: (int)num(at + 5),
            PcmFormat: parsed.PcmFormat,
            Extradata: bytes(at + 6));
    }

    private bool IsWebCodecsConfigSupported(IReadOnlyList<JsValue> args)
    {
        var config = ReadWebCodecsConfig(args, 0, i => ReadOptionalString(args, i), i => ReadNumber(args, i), i => ReadBytes(args, i));
        return config is not null && MediaEngineServices.Decoders.GetSupport(config) != DecoderSupport.Unsupported;
    }

    /// <summary>Selects and configures a decoder; returns its handle, or a negative error.</summary>
    private int ConfigureWebCodecsDecoder(IReadOnlyList<JsValue> args)
    {
        var config = ReadWebCodecsConfig(args, 0, i => ReadOptionalString(args, i), i => ReadNumber(args, i), i => ReadBytes(args, i));
        if (config is null || !HasTheDescriptionItNeeds(config))
            return -1;

        var context = new MediaPipelineContext(PlayerId.Next(), MediaLimits.Default, MediaEngineServices.Log);
        try
        {
            if (config.Kind == MediaTrackKind.Video)
            {
                var decoder = DecoderSelector.SelectAsync(MediaEngineServices.Decoders.GetVideoCandidates(config), config, context, CancellationToken.None)
                    .AsTask().GetAwaiter().GetResult();
                if (decoder is null)
                    return -1;
                return Register(new WebCodecsDecoderSession { Video = decoder, Context = context });
            }
            else
            {
                var decoder = DecoderSelector.SelectAsync(MediaEngineServices.Decoders.GetAudioCandidates(config), config, context, CancellationToken.None)
                    .AsTask().GetAwaiter().GetResult();
                if (decoder is null)
                    return -1;
                return Register(new WebCodecsDecoderSession { Audio = decoder, Context = context });
            }
        }
        catch (Exception ex) when (ex is MediaDecoderException or MediaFormatException or MediaLimitExceededException or MediaUnsupportedException)
        {
            EngineLogCompat.Warn($"[FenJsBridge] WebCodecs decoder configure failed: {ex.Message}", LogCategory.Media);
            return -1;
        }

        int Register(WebCodecsDecoderSession session)
        {
            int handle = _nextWebCodecsDecoder++;
            _webCodecsDecoders[handle] = session;
            return handle;
        }
    }

    /// <summary>
    /// Codecs whose stream cannot be read without the out-of-band header WebCodecs calls
    /// the description: Vorbis and FLAC always, and Opus once it goes beyond stereo and
    /// needs the channel mapping table of RFC 7845.
    /// </summary>
    private static bool HasTheDescriptionItNeeds(CodecConfig config) => config.Codec switch
    {
        MediaCodec.Vorbis or MediaCodec.Flac => !config.Extradata.IsEmpty,
        MediaCodec.Opus when config.Channels > 2 => config.Extradata.Length >= 10,
        _ => true,
    };

    private WebCodecsDecoderSession? Session(IReadOnlyList<JsValue> args) =>
        args.Count > 0 && _webCodecsDecoders.TryGetValue((int)ReadNumber(args, 0), out var session) ? session : null;

    /// <summary>
    /// Decodes one chunk. The answer is the pictures or audio the decoder had ready,
    /// which for a stream with reordering is not necessarily this chunk's.
    /// </summary>
    private JsValue DecodeWebCodecsChunk(IReadOnlyList<JsValue> args)
    {
        if (Session(args) is not { } session)
            return JsValue.Null;

        var bytes = ReadBytes(args, 1);
        long pts = (long)ReadNumber(args, 2);
        long duration = (long)ReadNumber(args, 3);
        bool key = args.Count > 4 && args[4].Tag == JsValueTag.Boolean && args[4].AsBoolean();

        try
        {
            var kind = session.Video is not null ? MediaTrackKind.Video : MediaTrackKind.Audio;
            using var packet = EncodedPacket.Copy(
                MediaLimits.Default, kind, trackId: 0, bytes,
                MediaTime.FromMicroseconds(pts), MediaTime.FromMicroseconds(pts),
                MediaTime.FromMicroseconds(duration), key);
            if (session.Video is { } video)
                video.DecodeAsync(packet, session.VideoSink, CancellationToken.None).AsTask().GetAwaiter().GetResult();
            else
                session.Audio!.DecodeAsync(packet, session.AudioSink, CancellationToken.None).AsTask().GetAwaiter().GetResult();
            return TakeOutputs(session);
        }
        catch (Exception ex) when (ex is MediaDecoderException or MediaFormatException or MediaLimitExceededException)
        {
            return JsValue.Null;
        }
    }

    private JsValue FlushWebCodecsDecoder(IReadOnlyList<JsValue> args)
    {
        if (Session(args) is not { } session)
            return JsValue.Null;
        try
        {
            if (session.Video is { } video)
                video.DrainAsync(session.VideoSink, CancellationToken.None).AsTask().GetAwaiter().GetResult();
            else
                session.Audio!.DrainAsync(session.AudioSink, CancellationToken.None).AsTask().GetAwaiter().GetResult();
            return TakeOutputs(session);
        }
        catch (Exception ex) when (ex is MediaDecoderException or MediaFormatException or MediaLimitExceededException)
        {
            return JsValue.Null;
        }
    }

    private JsValue ResetWebCodecsDecoder(IReadOnlyList<JsValue> args)
    {
        if (Session(args) is { } session)
        {
            session.AudioSink.DisposeAll();
            session.VideoSink.DisposeAll();
            try
            {
                session.Video?.ResetAsync(CancellationToken.None).AsTask().GetAwaiter().GetResult();
                session.Audio?.ResetAsync(CancellationToken.None).AsTask().GetAwaiter().GetResult();
            }
            catch (Exception ex) when (ex is MediaDecoderException or MediaFormatException)
            {
                // A decoder that cannot flush is closed by the caller next.
                EngineLogCompat.Warn($"[FenJsBridge] WebCodecs decoder reset failed: {ex.Message}", LogCategory.Media);
            }
        }

        return JsValue.Undefined;
    }

    private JsValue CloseWebCodecsDecoder(IReadOnlyList<JsValue> args)
    {
        if (args.Count > 0 && _webCodecsDecoders.Remove((int)ReadNumber(args, 0), out var session))
            session.Dispose();
        return JsValue.Undefined;
    }

    /// <summary>The decoded audio and pictures as plain objects the prelude turns into AudioData and VideoFrame.</summary>
    private JsValue TakeOutputs(WebCodecsDecoderSession session)
    {
        var results = new List<JsValue>();
        foreach (var block in session.AudioSink.Items)
        {
            using (block)
            {
                var samples = block.Samples;
                var bytes = new byte[samples.Length * sizeof(float)];
                Buffer.BlockCopy(samples.ToArray(), 0, bytes, 0, bytes.Length);
                results.Add(_interpreter.AllocateObject(new Dictionary<string, JsValue>
                {
                    ["kind"] = JsValue.FromString("audio"),
                    ["timestamp"] = JsValue.FromNumber(block.Timestamp.Microseconds),
                    ["duration"] = JsValue.FromNumber(block.Duration.Microseconds),
                    ["sampleRate"] = JsValue.FromInt32(block.SampleRate),
                    ["numberOfChannels"] = JsValue.FromInt32(block.Channels),
                    ["numberOfFrames"] = JsValue.FromInt32(block.FrameCount),
                    // The pipeline carries planar float samples, which is WebCodecs' f32-planar.
                    ["format"] = JsValue.FromString("f32-planar"),
                    ["data"] = CreateUint8ArrayFromBytes(bytes),
                }));
            }
        }

        session.AudioSink.Items.Clear();

        foreach (var frame in session.VideoSink.Items)
        {
            using (frame)
            {
                var bytes = new byte[frame.TotalBytes];
                int offset = 0;
                var strides = new List<JsValue>();
                for (int plane = 0; plane < frame.PlaneCount; plane++)
                {
                    var source = frame.GetPlane(plane);
                    source.CopyTo(bytes.AsSpan(offset));
                    offset += source.Length;
                    strides.Add(JsValue.FromInt32(frame.GetStride(plane)));
                }

                results.Add(_interpreter.AllocateObject(new Dictionary<string, JsValue>
                {
                    ["kind"] = JsValue.FromString("video"),
                    ["timestamp"] = JsValue.FromNumber(frame.Timestamp.Microseconds),
                    ["duration"] = JsValue.FromNumber(frame.Duration.Microseconds),
                    ["codedWidth"] = JsValue.FromInt32(frame.Width),
                    ["codedHeight"] = JsValue.FromInt32(frame.Height),
                    ["format"] = JsValue.FromString(frame.Format switch
                    {
                        VideoPixelFormat.I420 => "I420",
                        VideoPixelFormat.Nv12 => "NV12",
                        _ => "BGRA",
                    }),
                    ["strides"] = _interpreter.AllocateArray(strides.ToArray()),
                    ["data"] = CreateUint8ArrayFromBytes(bytes),
                }));
            }
        }

        session.VideoSink.Items.Clear();
        return _interpreter.AllocateArray(results.ToArray());
    }

    private byte[] ReadBytes(IReadOnlyList<JsValue> args, int index) =>
        index < args.Count ? ExtractBytesFromArrayLike(args[index]) : Array.Empty<byte>();
}
