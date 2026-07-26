using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace FenBrowser.Tooling
{
    internal static class WptShardPlanner
    {
        internal static void WritePlan(
            string outputDirectory,
            IEnumerable<string> discoveredTests,
            int shardCount,
            string suite,
            string historyPath)
        {
            var tests = FilterSuite(discoveredTests, suite)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(test => test, StringComparer.Ordinal)
                .ToArray();
            var history = ReadHistory(historyPath);
            var knownDurations = history.Values.SelectMany(value => value).OrderBy(value => value).ToArray();
            var fallbackSeconds = knownDurations.Length == 0
                ? 10d
                : knownDurations[knownDurations.Length / 2];
            var estimates = tests.ToDictionary(
                test => test,
                test => history.TryGetValue(test, out var samples)
                    ? Median(samples)
                    : fallbackSeconds,
                StringComparer.Ordinal);
            var shards = Balance(estimates, shardCount);

            Directory.CreateDirectory(outputDirectory);
            for (var index = 0; index < shards.Count; index++)
            {
                File.WriteAllLines(
                    Path.Combine(outputDirectory, $"shard_{index + 1:000}.include.txt"),
                    shards[index].Tests,
                    new UTF8Encoding(false));
            }

            var document = new
            {
                schemaVersion = 1,
                suite,
                shardCount,
                testCount = tests.Length,
                historySampleCount = knownDurations.Length,
                fallbackSeconds,
                algorithm = "longest-processing-time-first",
                shards = shards.Select((shard, index) => new
                {
                    shard = index + 1,
                    testCount = shard.Tests.Count,
                    estimatedSeconds = shard.EstimatedSeconds,
                    includeFile = $"shard_{index + 1:000}.include.txt"
                })
            };
            File.WriteAllText(
                Path.Combine(outputDirectory, "wpt.shard-plan.json"),
                JsonSerializer.Serialize(document, new JsonSerializerOptions { WriteIndented = true }),
                new UTF8Encoding(false));
        }

        internal static IReadOnlyList<Shard> Balance(
            IReadOnlyDictionary<string, double> estimatedDurations,
            int shardCount)
        {
            if (shardCount <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(shardCount));
            }

            var shards = Enumerable.Range(0, shardCount).Select(_ => new Shard()).ToArray();
            foreach (var item in estimatedDurations
                         .OrderByDescending(pair => pair.Value)
                         .ThenBy(pair => pair.Key, StringComparer.Ordinal))
            {
                var target = shards
                    .Select((shard, index) => new { shard, index })
                    .OrderBy(candidate => candidate.shard.EstimatedSeconds)
                    .ThenBy(candidate => candidate.index)
                    .First()
                    .shard;
                target.Tests.Add(item.Key);
                target.EstimatedSeconds += item.Value;
            }

            foreach (var shard in shards)
            {
                shard.Tests.Sort(StringComparer.Ordinal);
            }

            return shards;
        }

        internal static IEnumerable<string> FilterSuite(IEnumerable<string> tests, string suite)
        {
            foreach (var test in tests)
            {
                var worker = IsWorkerTest(test);
                if (string.Equals(suite, "workers", StringComparison.OrdinalIgnoreCase))
                {
                    if (worker)
                    {
                        yield return test;
                    }
                }
                else if (!string.Equals(suite, "normal", StringComparison.OrdinalIgnoreCase) || !worker)
                {
                    yield return test;
                }
            }
        }

        private static bool IsWorkerTest(string test)
        {
            return test.Contains(".worker.", StringComparison.OrdinalIgnoreCase) ||
                   test.Contains(".sharedworker.", StringComparison.OrdinalIgnoreCase) ||
                   test.Contains(".serviceworker.", StringComparison.OrdinalIgnoreCase) ||
                   test.Contains("/workers/", StringComparison.OrdinalIgnoreCase) ||
                   test.Contains("/service-workers/", StringComparison.OrdinalIgnoreCase);
        }

        private static Dictionary<string, List<double>> ReadHistory(string historyPath)
        {
            var result = new Dictionary<string, List<double>>(StringComparer.Ordinal);
            if (string.IsNullOrWhiteSpace(historyPath) || !Directory.Exists(historyPath))
            {
                return result;
            }

            foreach (var rawLog in Directory.EnumerateFiles(historyPath, "wpt.raw.json", SearchOption.AllDirectories))
            {
                var starts = new Dictionary<string, double>(StringComparer.Ordinal);
                foreach (var line in File.ReadLines(rawLog))
                {
                    try
                    {
                        using var document = JsonDocument.Parse(line);
                        var root = document.RootElement;
                        if (!root.TryGetProperty("action", out var actionValue) ||
                            !root.TryGetProperty("test", out var testValue) ||
                            !root.TryGetProperty("time", out var timeValue) ||
                            timeValue.ValueKind != JsonValueKind.Number)
                        {
                            continue;
                        }

                        var action = actionValue.GetString();
                        var test = testValue.GetString();
                        var time = timeValue.GetDouble();
                        if (string.IsNullOrWhiteSpace(test))
                        {
                            continue;
                        }

                        if (action == "test_start")
                        {
                            starts[test] = time;
                        }
                        else if (action == "test_end" && starts.Remove(test, out var started))
                        {
                            var seconds = NormalizeDurationSeconds(time - started);
                            if (seconds > 0)
                            {
                                if (!result.TryGetValue(test, out var samples))
                                {
                                    samples = new List<double>();
                                    result[test] = samples;
                                }
                                samples.Add(seconds);
                            }
                        }
                    }
                    catch (JsonException)
                    {
                    }
                }
            }

            return result;
        }

        private static double NormalizeDurationSeconds(double duration)
        {
            // mozlog timestamps are milliseconds since epoch.
            return duration / 1000d;
        }

        private static double Median(IReadOnlyCollection<double> values)
        {
            var ordered = values.OrderBy(value => value).ToArray();
            var middle = ordered.Length / 2;
            return ordered.Length % 2 == 0
                ? (ordered[middle - 1] + ordered[middle]) / 2d
                : ordered[middle];
        }

        internal sealed class Shard
        {
            public List<string> Tests { get; } = new();
            public double EstimatedSeconds { get; set; }
        }
    }
}
