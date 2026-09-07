using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;

namespace FenBrowser.FenEngine.Scripting
{
    /// <summary>
    /// Instruments the script engine's coordination locks, enabled by
    /// FEN_FENJS_LOCKPROBE=1.
    /// </summary>
    /// <remarks>
    /// A CPU profile of a page load is mostly threads waiting, and a sampling
    /// profiler can say that a thread sat in Monitor.Enter without saying which
    /// lock, who was holding it, or for how long. That is the question a single
    /// engine-wide lock raises, so this records it directly: per acquisition
    /// site, how often it was taken, how often it had to block, how long it
    /// waited, and how long it then held the lock shut against everyone else.
    ///
    /// Sites name themselves through the caller attributes, so adding one is a
    /// textual substitution and there is no table to keep in step with the code.
    /// When disabled the probe is a plain Monitor.Enter/Exit pair and records
    /// nothing.
    /// </remarks>
    internal readonly struct ScriptEngineLockProbe : IDisposable
    {
        internal static readonly bool Enabled = string.Equals(
            Environment.GetEnvironmentVariable("FEN_FENJS_LOCKPROBE"),
            "1",
            StringComparison.Ordinal);

        private sealed class SiteStat
        {
            public long Acquisitions;
            public long Contended;
            public long WaitTicks;
            public long HoldTicks;
            public long MaxHoldTicks;
            public long MaxWaitTicks;
        }

        private static readonly object StatsGate = new object();
        private static readonly Dictionary<string, SiteStat> Stats =
            new Dictionary<string, SiteStat>(StringComparer.Ordinal);

        // Aggregates say the lock is held too long; they cannot say by what. A
        // single 9-second hold and ninety 100ms holds look similar in a total,
        // and only one of them is a bug worth chasing, so every hold past the
        // threshold is kept individually with whatever the caller could name
        // about the work it was doing.
        private const double LongHoldMs = 50.0;
        private const int MaxLongHolds = 400;

        private static readonly List<(string Site, string Label, double Ms, double AtMs)> LongHolds =
            new List<(string, string, double, double)>();

        private static readonly long ProbeStartTicks = Stopwatch.GetTimestamp();

        private readonly object _gate;
        private readonly string _site;
        private readonly string _label;
        private readonly long _acquiredTicks;

        private ScriptEngineLockProbe(object gate, string site, string label, long acquiredTicks)
        {
            _gate = gate;
            _site = site;
            _label = label;
            _acquiredTicks = acquiredTicks;
        }

        /// <summary>
        /// Takes <paramref name="gate"/>, recording what it cost to get in.
        /// </summary>
        internal static ScriptEngineLockProbe Hold(
            object gate,
            string label = null,
            [CallerMemberName] string member = "",
            [CallerLineNumber] int line = 0)
        {
            if (!Enabled)
            {
                Monitor.Enter(gate);
                return new ScriptEngineLockProbe(gate, null, null, 0);
            }

            var site = member + ":" + line.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var start = Stopwatch.GetTimestamp();

            // TryEnter answers the question a stopwatch cannot: whether anyone
            // was actually in the way. An uncontended acquisition costs the same
            // handful of nanoseconds either way, so timing alone would not
            // separate "waited" from "walked straight in".
            var contended = !Monitor.TryEnter(gate);
            if (contended)
            {
                Monitor.Enter(gate);
            }

            var acquired = Stopwatch.GetTimestamp();
            var waited = acquired - start;

            lock (StatsGate)
            {
                if (!Stats.TryGetValue(site, out var stat))
                {
                    stat = new SiteStat();
                    Stats[site] = stat;
                }

                stat.Acquisitions++;
                if (contended)
                {
                    stat.Contended++;
                    stat.WaitTicks += waited;
                    if (waited > stat.MaxWaitTicks) stat.MaxWaitTicks = waited;
                }
            }

            return new ScriptEngineLockProbe(gate, site, label, acquired);
        }

        public void Dispose()
        {
            if (_site is null)
            {
                Monitor.Exit(_gate);
                return;
            }

            var held = Stopwatch.GetTimestamp() - _acquiredTicks;

            // Release before recording: the stats lock is a second lock, and
            // holding the engine lock across it would make the probe itself a
            // source of the contention it is measuring.
            Monitor.Exit(_gate);

            var heldMs = 1000.0 * held / Stopwatch.Frequency;

            lock (StatsGate)
            {
                if (Stats.TryGetValue(_site, out var stat))
                {
                    stat.HoldTicks += held;
                    if (held > stat.MaxHoldTicks) stat.MaxHoldTicks = held;
                }

                if (heldMs >= LongHoldMs && LongHolds.Count < MaxLongHolds)
                {
                    LongHolds.Add((
                        _site,
                        _label,
                        heldMs,
                        1000.0 * (_acquiredTicks - ProbeStartTicks) / Stopwatch.Frequency));
                }
            }
        }

        internal static string Report(int top = 20)
        {
            if (!Enabled) return "[FenJsLock] probe disabled (set FEN_FENJS_LOCKPROBE=1)";

            KeyValuePair<string, SiteStat>[] snapshot;
            lock (StatsGate)
            {
                snapshot = new List<KeyValuePair<string, SiteStat>>(Stats).ToArray();
            }

            if (snapshot.Length == 0) return "[FenJsLock] no acquisitions recorded";

            var frequency = (double)Stopwatch.Frequency;
            long totalAcquisitions = 0, totalContended = 0;
            double totalWaitMs = 0, totalHoldMs = 0;
            foreach (var entry in snapshot)
            {
                totalAcquisitions += entry.Value.Acquisitions;
                totalContended += entry.Value.Contended;
                totalWaitMs += 1000.0 * entry.Value.WaitTicks / frequency;
                totalHoldMs += 1000.0 * entry.Value.HoldTicks / frequency;
            }

            Array.Sort(snapshot, (a, b) => b.Value.HoldTicks.CompareTo(a.Value.HoldTicks));

            var text = new StringBuilder();
            text.Append("[FenJsLock] acquisitions=").Append(totalAcquisitions)
                .Append(" contended=").Append(totalContended)
                .Append(" (").Append(totalAcquisitions == 0 ? 0 : 100.0 * totalContended / totalAcquisitions)
                .Append("%) totalHeld=").Append(totalHoldMs.ToString("F0"))
                .Append("ms totalWaited=").Append(totalWaitMs.ToString("F0")).AppendLine("ms");
            text.AppendLine("    site                                   taken   blocked     heldMs    maxHeld     waitMs    maxWait");

            for (var i = 0; i < snapshot.Length && i < top; i++)
            {
                var site = snapshot[i].Key;
                var stat = snapshot[i].Value;
                text.Append("    ")
                    .Append((site.Length > 36 ? site.Substring(0, 36) : site).PadRight(38))
                    .Append(stat.Acquisitions.ToString().PadLeft(8))
                    .Append(stat.Contended.ToString().PadLeft(10))
                    .Append((1000.0 * stat.HoldTicks / frequency).ToString("F0").PadLeft(11))
                    .Append((1000.0 * stat.MaxHoldTicks / frequency).ToString("F0").PadLeft(11))
                    .Append((1000.0 * stat.WaitTicks / frequency).ToString("F0").PadLeft(11))
                    .Append((1000.0 * stat.MaxWaitTicks / frequency).ToString("F0").PadLeft(11))
                    .AppendLine();
            }

            (string Site, string Label, double Ms, double AtMs)[] holds;
            lock (StatsGate)
            {
                holds = LongHolds.ToArray();
            }

            if (holds.Length > 0)
            {
                Array.Sort(holds, (a, b) => b.Ms.CompareTo(a.Ms));
                text.Append("[FenJsLock] longest individual holds (>=")
                    .Append(LongHoldMs.ToString("F0")).Append("ms), ")
                    .Append(holds.Length).AppendLine(" recorded");
                text.AppendLine("        heldMs      atMs  site / what it was running");
                var shown = holds.Length < 15 ? holds.Length : 15;
                for (var i = 0; i < shown; i++)
                {
                    var what = holds[i].Label ?? holds[i].Site;
                    text.Append("    ")
                        .Append(holds[i].Ms.ToString("F0").PadLeft(10))
                        .Append(holds[i].AtMs.ToString("F0").PadLeft(10))
                        .Append("  ")
                        .AppendLine(what.Length > 150 ? what.Substring(0, 150) : what);
                }
            }

            return text.ToString();
        }
    }

    /// <summary>Surfaces the lock probe to tools outside this assembly.</summary>
    public static class BrowserScriptEngineDiagnostics
    {
        public static string LockReport() => ScriptEngineLockProbe.Report();
    }
}
