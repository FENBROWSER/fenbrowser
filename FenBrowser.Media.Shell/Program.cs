using FenBrowser.Media.Pipeline;
using FenBrowser.Media.Shell;

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

var demuxers = new DemuxerRegistry();
var decoders = new DecoderRegistry();
FenBrowser.Media.MediaFormats.RegisterBuiltIn(demuxers, decoders);
var fenplay = new Fenplay(Console.Out, Console.Error, demuxers, decoders);
return await fenplay.RunAsync(args, cts.Token);
