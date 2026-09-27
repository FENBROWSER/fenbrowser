using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace FenBrowser.Media.Video;

/// <summary>
/// The wait between two picture selections must be a few milliseconds, but Windows
/// schedules waits on a 15.6 ms timer by default, which alone drops pictures at 60 fps.
/// While a player presents pictures it holds a 1 ms timer period, as the other engines
/// do during video playback; the period is released when the last player stops.
/// </summary>
public static class PresentationTimer
{
    private const uint PeriodMs = 1;
    private static readonly Lock s_gate = new();
    private static int s_holders;

    /// <summary>Requests the fine period; every call must be matched by <see cref="Release"/>.</summary>
    public static void Acquire()
    {
        if (!OperatingSystem.IsWindows())
            return;
        lock (s_gate)
        {
            if (s_holders++ == 0)
                _ = TimeBeginPeriod(PeriodMs);
        }
    }

    public static void Release()
    {
        if (!OperatingSystem.IsWindows())
            return;
        lock (s_gate)
        {
            if (s_holders > 0 && --s_holders == 0)
                _ = TimeEndPeriod(PeriodMs);
        }
    }

    [SupportedOSPlatform("windows")]
    [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    private static extern uint TimeBeginPeriod(uint period);

    [SupportedOSPlatform("windows")]
    [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
    private static extern uint TimeEndPeriod(uint period);
}
