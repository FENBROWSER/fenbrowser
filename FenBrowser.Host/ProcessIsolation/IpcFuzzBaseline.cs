using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using FenBrowser.Host.ProcessIsolation.Network;
using FenBrowser.Host.ProcessIsolation.Targets;

namespace FenBrowser.Host.ProcessIsolation
{
    public sealed class IpcFuzzBaselineResult
    {
        public string Channel { get; init; }
        public int CasesRun { get; set; }
        public int Successes { get; set; }
        public int Failures { get; set; }
        public List<string> FailureSamples { get; } = new();
    }

    public static class IpcFuzzBaseline
    {
        public static IReadOnlyList<IpcFuzzBaselineResult> RunDefaultSuite()
        {
            return new[]
            {
                RunRendererEnvelopeSuite(),
                RunNetworkEnvelopeSuite(),
                RunTargetEnvelopeSuite()
            };
        }

        private static IpcFuzzBaselineResult RunRendererEnvelopeSuite()
        {
            var seedEnvelope = new RendererIpcEnvelope
            {
                Type = RendererIpcMessageType.FrameReady.ToString(),
                TabId = 7,
                CorrelationId = Guid.NewGuid().ToString("N"),
                Token = "seed-token",
                Payload = RendererIpc.SerializePayload(new RendererFrameReadyPayload
                {
                    Url = "https://example.test/",
                    FrameTimestampUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    SurfaceWidth = 1280,
                    SurfaceHeight = 720,
                    DirtyRegionCount = 1,
                    HasDamage = true,
                    FrameSequenceNumber = 42
                })
            };

            return RunSuite(
                "renderer-ipc",
                RendererIpc.SerializeEnvelope(seedEnvelope),
                line =>
                {
                    if (!RendererIpc.TryDeserializeEnvelope(line, out var envelope))
                    {
                        return true;
                    }

                    _ = RendererIpc.DeserializePayload<RendererFrameReadyPayload>(envelope);
                    _ = RendererIpc.DeserializePayload<RendererNavigatePayload>(envelope);
                    return true;
                });
        }

        private static IpcFuzzBaselineResult RunNetworkEnvelopeSuite()
        {
            var seedEnvelope = new NetworkIpcEnvelope
            {
                Type = NetworkIpcMessageType.FetchRequest.ToString(),
                RequestId = Guid.NewGuid().ToString("N"),
                CapabilityToken = "cap-token",
                Payload = NetworkIpc.SerializePayload(new NetworkFetchRequestPayload
                {
                    Url = "https://example.test/data.json",
                    Method = "POST",
                    Mode = "cors",
                    Credentials = "same-origin",
                    Referrer = "https://origin.test/",
                    InitiatorOrigin = "https://origin.test"
                })
            };

            return RunSuite(
                "network-ipc",
                NetworkIpc.Serialize(seedEnvelope),
                line =>
                {
                    if (!NetworkIpc.TryDeserialize(line, out var envelope))
                    {
                        return true;
                    }

                    _ = NetworkIpc.DeserializePayload<NetworkFetchRequestPayload>(envelope);
                    _ = NetworkIpc.DeserializePayload<NetworkFetchResponseHeadPayload>(envelope);
                    _ = NetworkIpc.DeserializePayload<NetworkFetchFailedPayload>(envelope);
                    return true;
                });
        }

        private static IpcFuzzBaselineResult RunTargetEnvelopeSuite()
        {
            var seedEnvelope = new TargetIpcEnvelope
            {
                Type = TargetIpcMessageType.Ready.ToString(),
                RequestId = Guid.NewGuid().ToString("N"),
                CapabilityToken = "target-token",
                Payload = TargetIpc.SerializePayload(new TargetReadyPayload
                {
                    ProcessKind = "gpu",
                    SandboxProfile = "gpu_process",
                    Capabilities = "gpu,shared-memory,compositor",
                    ProcessId = 1234
                })
            };

            return RunSuite(
                "target-ipc",
                TargetIpc.Serialize(seedEnvelope),
                line =>
                {
                    if (!TargetIpc.TryDeserialize(line, out var envelope))
                    {
                        return true;
                    }

                    _ = TargetIpc.DeserializePayload<TargetReadyPayload>(envelope);
                    return true;
                });
        }

        private static IpcFuzzBaselineResult RunSuite(string channel, string seedLine, Func<string, bool> evaluateCase)
        {
            var result = new IpcFuzzBaselineResult { Channel = channel };
            foreach (var candidate in GenerateCandidates(seedLine))
            {
                result.CasesRun++;
                try
                {
                    if (evaluateCase(candidate))
                    {
                        result.Successes++;
                    }
                    else
                    {
                        result.Failures++;
                        AddFailureSample(result, candidate, "evaluator-returned-false");
                    }
                }
                catch (Exception ex)
                {
                    result.Failures++;
                    AddFailureSample(result, candidate, ex.GetType().Name + ": " + ex.Message);
                }
            }

            return result;
        }

        private static IEnumerable<string> GenerateCandidates(string seed)
        {
            yield return string.Empty;
            yield return " ";
            yield return "{";
            yield return "[]";
            yield return "\"not-json-object\"";
            yield return seed;
            yield return seed + "\ntrailing";
            yield return seed.Replace("\"type\":", "\"TYPE\":", StringComparison.Ordinal);
            yield return seed.Replace("\"payload\":", "\"payload\":null,", StringComparison.Ordinal);
            yield return seed.Replace("\"timestampUnixMs\":", "\"timestampUnixMs\":\"oops\",", StringComparison.Ordinal);
            yield return seed.Replace("\"requestId\":", "\"requestId\":123,", StringComparison.Ordinal);
            yield return seed.Replace("\"tabId\":", "\"tabId\":\"bad\",", StringComparison.Ordinal);
            yield return seed.Replace("\"capabilityToken\":", "\"capabilityToken\":{\"nested\":true},", StringComparison.Ordinal);
            yield return seed.Replace("\"token\":", "\"token\":[1,2,3],", StringComparison.Ordinal);
            yield return seed.Replace("https://example.test/", new string('A', 4096), StringComparison.Ordinal);
            yield return seed.Replace("https://example.test/data.json", "javascript:alert(1)", StringComparison.Ordinal);
            yield return seed.Replace("\"payload\":\"", "\"payload\":{\"raw\":", StringComparison.Ordinal);
            yield return seed.Replace("\\/", "/", StringComparison.Ordinal);
            yield return BuildRepeated(seed, 4);
            yield return seed[..Math.Max(1, seed.Length / 2)];
            yield return CorruptUtf16(seed);

            foreach (var mutation in BitFlipMutations(seed, 24))
            {
                yield return mutation;
            }
        }

        private static IEnumerable<string> BitFlipMutations(string seed, int maxCases)
        {
            if (string.IsNullOrEmpty(seed))
            {
                yield break;
            }

            var bytes = Encoding.UTF8.GetBytes(seed);
            var limit = Math.Min(maxCases, bytes.Length);
            for (int i = 0; i < limit; i++)
            {
                var clone = (byte[])bytes.Clone();
                clone[i] ^= (byte)(1 << (i % 8));
                yield return Encoding.UTF8.GetString(clone);
            }
        }

        private static string BuildRepeated(string seed, int repeatCount)
            => string.Concat(Enumerable.Repeat(seed, repeatCount));

        private static string CorruptUtf16(string seed)
            => seed + '\uD800';

        private static void AddFailureSample(IpcFuzzBaselineResult result, string candidate, string reason)
        {
            if (result.FailureSamples.Count >= 8)
            {
                return;
            }

            var trimmed = candidate.Length > 160 ? candidate[..160] : candidate;
            result.FailureSamples.Add($"{reason} :: {trimmed}");
        }
    }
}
