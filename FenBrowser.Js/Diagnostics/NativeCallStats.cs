namespace FenBrowser.Js.Diagnostics;

/// <summary>
/// Whether every native call is timed and counted by name.
/// </summary>
/// <remarks>
/// The per-builtin table answers "which built-in is this page spending its time
/// in", which is worth having — but it costs two reads of the performance
/// counter and a string-keyed dictionary update per call, and a bundle makes
/// millions of them. Measured on a call to <c>Math.floor</c>, the accounting
/// cost about a third of the call. Pages pay for it only when asked.
/// </remarks>
public static class NativeCallStats
{
    public static readonly bool Enabled = string.Equals(
        System.Environment.GetEnvironmentVariable("FEN_FENJS_NATIVE_STATS"),
        "1",
        System.StringComparison.Ordinal);
}
