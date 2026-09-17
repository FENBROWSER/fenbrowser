using FenBrowser.Media.Pipeline;
using FenBrowser.Media.Shell;

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

var fenplay = new Fenplay(Console.Out, Console.Error, new DemuxerRegistry());
return await fenplay.RunAsync(args, cts.Token);
