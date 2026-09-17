using System.Globalization;
using System.Text.Json;
using FenBrowser.Media.Diagnostics;
using FenBrowser.Media.Pipeline;
using FenBrowser.Media.Sniffing;

namespace FenBrowser.Media.Shell;

/// <summary>
/// The <c>fenplay</c> commands. Output goes to the given writers so tests can drive it.
/// </summary>
public sealed class Fenplay
{
    public const int ExitOk = 0;
    public const int ExitUsage = 2;
    public const int ExitInputError = 3;

    private static readonly JsonSerializerOptions s_json = new() { WriteIndented = true };

    private readonly TextWriter _out;
    private readonly TextWriter _err;
    private readonly DemuxerRegistry _demuxers;

    public Fenplay(TextWriter output, TextWriter error, DemuxerRegistry demuxers)
    {
        _out = output;
        _err = error;
        _demuxers = demuxers;
    }

    public const string Usage = """
        fenplay - FenBrowser media engine tool

        usage:
          fenplay probe <file> [--json] [--log]   sniff a file and choose a demuxer
          fenplay limits                          print the default media limits
          fenplay help                            show this text

        Exit codes: 0 ok, 2 usage error, 3 unreadable or unsupported input.
        """;

    public async Task<int> RunAsync(IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        if (args.Count == 0 || args[0] is "help" or "-h" or "--help")
        {
            await _out.WriteLineAsync(Usage).ConfigureAwait(false);
            return args.Count == 0 ? ExitUsage : ExitOk;
        }

        switch (args[0])
        {
            case "probe":
                return await ProbeAsync(args.Skip(1).ToList(), cancellationToken).ConfigureAwait(false);
            case "limits":
                await _out.WriteLineAsync(JsonSerializer.Serialize(MediaLimits.Default, s_json)).ConfigureAwait(false);
                return ExitOk;
            default:
                await _err.WriteLineAsync($"fenplay: unknown command '{args[0]}'. Try 'fenplay help'.").ConfigureAwait(false);
                return ExitUsage;
        }
    }

    private async Task<int> ProbeAsync(List<string> args, CancellationToken cancellationToken)
    {
        bool json = args.Remove("--json");
        bool log = args.Remove("--log");
        if (args.Count != 1 || args[0].StartsWith('-'))
        {
            await _err.WriteLineAsync("usage: fenplay probe <file> [--json] [--log]").ConfigureAwait(false);
            return ExitUsage;
        }

        string path = args[0];
        byte[] header;
        long length;
        try
        {
            await using var file = File.OpenRead(path);
            length = file.Length;
            header = new byte[(int)Math.Min(length, MediaSniffer.ResourceHeaderLength)];
            await file.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await _err.WriteLineAsync($"fenplay: cannot read '{path}': {ex.Message}").ConfigureAwait(false);
            return ExitInputError;
        }

        IMediaLogSink sink = log ? new TextMediaLogSink(_err) : NullMediaLogSink.Instance;
        var context = new MediaPipelineContext(PlayerId.Next(), MediaLimits.Default, sink);
        string? sniffed = MediaSniffer.Sniff(header);
        var demuxer = _demuxers.Select(header, sniffed, context);

        var report = new ProbeReport(
            Path.GetFileName(path),
            length,
            sniffed,
            demuxer?.Name,
            _demuxers.Factories.Select(f => f.Name).ToArray());

        if (json)
        {
            await _out.WriteLineAsync(JsonSerializer.Serialize(report, s_json)).ConfigureAwait(false);
        }
        else
        {
            await _out.WriteLineAsync($"file:      {report.File} ({report.Bytes.ToString(CultureInfo.InvariantCulture)} bytes)").ConfigureAwait(false);
            await _out.WriteLineAsync($"sniffed:   {report.Sniffed ?? "undefined"}").ConfigureAwait(false);
            await _out.WriteLineAsync($"demuxer:   {report.Demuxer ?? "none"}").ConfigureAwait(false);
            await _out.WriteLineAsync($"available: {(report.Registered.Length == 0 ? "(no demuxers registered yet)" : string.Join(", ", report.Registered))}").ConfigureAwait(false);
        }

        return demuxer is null ? ExitInputError : ExitOk;
    }

    public sealed record ProbeReport(string File, long Bytes, string? Sniffed, string? Demuxer, string[] Registered);
}

/// <summary>Writes media events as one line each, for <c>fenplay --log</c>.</summary>
public sealed class TextMediaLogSink(TextWriter writer) : IMediaLogSink
{
    public bool IsEnabled(MediaLogLevel level) => true;

    public void Log(in MediaLogEvent logEvent)
    {
        string fields = logEvent.Fields is { Count: > 0 } f
            ? " " + string.Join(" ", f.Select(kv => $"{kv.Key}={kv.Value}"))
            : "";
        writer.WriteLine($"[{logEvent.Level}] {logEvent.Player} {logEvent.Kind}: {logEvent.Message}{fields}");
    }
}
