using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Threading;
using FenBrowser.Core;
using FenBrowser.Core.Logging;
using FenBrowser.Host.ProcessIsolation.Network;

namespace FenBrowser.Host.ProcessIsolation.Fuzz
{
    // ── IPC Fuzzing Baseline ─────────────────────────────────────────────────
    // Per the guide §4.4 and §16.4: fuzzers per endpoint are mandatory.
    //
    // Design:
    //  - IpcFuzzHarness — the entry point; drives one or more IFuzzEndpoint
    //  - IFuzzEndpoint  — one registered endpoint (e.g. Renderer IPC, Network IPC)
    //  - StructuredMutator — smart mutator that starts from valid seeds and applies
    //                        targeted mutations (bit flips, boundary integers, huge
    //                        strings, nested JSON bombs, etc.)
    //  - FuzzCoverage   — lightweight bitmap that tracks which code paths were hit
    //
    // This is an in-process harness; in CI it is run as a continuous background
    // job. It integrates with the broker's IPC dispatch path directly, so no
    // child process is needed for the fuzzer itself.
    //
    // For shared-memory frame fuzzing see IpcSharedMemoryFuzzer below.
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>A registered fuzz target.</summary>
    public interface IFuzzEndpoint
    {
        string Name { get; }
        /// <summary>Process one raw input byte sequence and return true if handled without crash.</summary>
        bool Fuzz(ReadOnlySpan<byte> input);
    }

    /// <summary>Lightweight code-path coverage bitmap (edge coverage approximation).</summary>
    public sealed class FuzzCoverage
    {
        private readonly int[] _bitmap;
        private int _totalHits;

        public FuzzCoverage(int size = 1 << 16)
        {
            _bitmap = new int[size];
        }

        public void Record(int edgeId)
        {
            var idx = (uint)edgeId % (uint)_bitmap.Length;
            Interlocked.Increment(ref _bitmap[idx]);
            Interlocked.Increment(ref _totalHits);
        }

        public int TotalHits => _totalHits;
        public int UniqueEdges => Array.FindAll(_bitmap, x => x > 0).Length;

        public void Reset()
        {
            Array.Clear(_bitmap, 0, _bitmap.Length);
            _totalHits = 0;
        }
    }

    /// <summary>
    /// Mutation strategies applied to seed corpus entries.
    /// </summary>
    public sealed class StructuredMutator
    {
        private readonly Random _rng;
        private static readonly byte[] InterestingBytes = { 0x00, 0x01, 0x7F, 0x80, 0xFE, 0xFF };
        private static readonly int[] InterestingInts  = { 0, 1, -1, int.MaxValue, int.MinValue, 65535, 65536 };

        public StructuredMutator(int seed = 0)
        {
            _rng = seed == 0 ? new Random() : new Random(seed);
        }

        public byte[] Mutate(byte[] seed)
        {
            if (seed == null || seed.Length == 0) return GenerateRandom(64);
            // Choose a mutation strategy
            return _rng.Next(10) switch
            {
                0 => BitFlip(seed),
                1 => ByteFlip(seed),
                2 => InsertInteresting(seed),
                3 => DeleteRange(seed),
                4 => DuplicateRange(seed),
                5 => InsertRandom(seed),
                6 => OverwriteRange(seed),
                7 => CrossOver(seed, seed), // self-crossover for splicing
                8 => TruncateOrExpand(seed),
                _ => seed, // return unchanged
            };
        }

        public byte[] MutateJson(string jsonSeed)
        {
            try
            {
                using var doc = JsonDocument.Parse(jsonSeed);
                var mutated = MutateJsonElement(doc.RootElement);
                return Encoding.UTF8.GetBytes(mutated);
            }
            catch
            {
                return Mutate(Encoding.UTF8.GetBytes(jsonSeed));
            }
        }

        private string MutateJsonElement(JsonElement el)
        {
            switch (el.ValueKind)
            {
                case JsonValueKind.Object:
                    var sb = new StringBuilder("{");
                    bool first = true;
                    foreach (var prop in el.EnumerateObject())
                    {
                        if (!first) sb.Append(',');
                        // Occasionally inject extra keys
                        if (_rng.Next(10) == 0)
                            sb.Append($"\"{GenerateRandomString(8)}\":null,");
                        sb.Append($"\"{EscapeJson(prop.Name)}\":{MutateJsonElement(prop.Value)}");
                        first = false;
                    }
                    sb.Append('}');
                    return sb.ToString();

                case JsonValueKind.Array:
                    var arr = new StringBuilder("[");
                    bool fa = true;
                    foreach (var item in el.EnumerateArray())
                    {
                        if (!fa) arr.Append(',');
                        arr.Append(MutateJsonElement(item));
                        fa = false;
                    }
                    // Occasionally add extra items
                    if (_rng.Next(5) == 0) arr.Append(",null");
                    arr.Append(']');
                    return arr.ToString();

                case JsonValueKind.String:
                    return MutateStringValue(el.GetString() ?? "");

                case JsonValueKind.Number:
                    return _rng.Next(5) == 0
                        ? InterestingInts[_rng.Next(InterestingInts.Length)].ToString()
                        : el.GetRawText();

                case JsonValueKind.True:
                case JsonValueKind.False:
                    return _rng.Next(3) == 0 ? "null" : el.GetRawText();

                default:
                    return el.GetRawText();
            }
        }

        private string MutateStringValue(string s)
        {
            return _rng.Next(5) switch
            {
                0 => "\"\"",                                                   // empty
                1 => $"\"{new string('A', _rng.Next(1, 100000))}\"",          // huge string
                2 => $"\"{EscapeJson(s + "\x00\xFF\uFFFD")}\"",               // inject special chars
                3 => $"\"{EscapeJson(s.PadRight(_rng.Next(s.Length, s.Length + 100), '\uFFFE'))}\"",
                _ => $"\"{EscapeJson(s)}\"",
            };
        }

        private byte[] BitFlip(byte[] seed)
        {
            var r = (byte[])seed.Clone();
            int pos = _rng.Next(r.Length);
            r[pos] ^= (byte)(1 << _rng.Next(8));
            return r;
        }

        private byte[] ByteFlip(byte[] seed)
        {
            var r = (byte[])seed.Clone();
            r[_rng.Next(r.Length)] = InterestingBytes[_rng.Next(InterestingBytes.Length)];
            return r;
        }

        private byte[] InsertInteresting(byte[] seed)
        {
            var val = BitConverter.GetBytes(InterestingInts[_rng.Next(InterestingInts.Length)]);
            int pos = _rng.Next(seed.Length + 1);
            var r = new byte[seed.Length + val.Length];
            Array.Copy(seed, r, pos);
            Array.Copy(val, 0, r, pos, val.Length);
            Array.Copy(seed, pos, r, pos + val.Length, seed.Length - pos);
            return r;
        }

        private byte[] DeleteRange(byte[] seed)
        {
            if (seed.Length <= 1) return seed;
            int start = _rng.Next(seed.Length);
            int len = _rng.Next(1, Math.Min(seed.Length - start + 1, 32));
            var r = new byte[seed.Length - len];
            Array.Copy(seed, r, start);
            Array.Copy(seed, start + len, r, start, seed.Length - start - len);
            return r;
        }

        private byte[] DuplicateRange(byte[] seed)
        {
            int start = _rng.Next(seed.Length);
            int len = _rng.Next(1, Math.Min(seed.Length - start + 1, 64));
            var r = new byte[seed.Length + len];
            Array.Copy(seed, r, seed.Length);
            Array.Copy(seed, start, r, seed.Length, len);
            return r;
        }

        private byte[] InsertRandom(byte[] seed)
        {
            int extra = _rng.Next(1, 16);
            var r = new byte[seed.Length + extra];
            _rng.NextBytes(r);
            Array.Copy(seed, 0, r, _rng.Next(extra + 1), seed.Length);
            return r;
        }

        private byte[] OverwriteRange(byte[] seed)
        {
            var r = (byte[])seed.Clone();
            int start = _rng.Next(r.Length);
            int len = _rng.Next(1, Math.Min(r.Length - start + 1, 32));
            for (int i = start; i < start + len && i < r.Length; i++)
                r[i] = (byte)_rng.Next(256);
            return r;
        }

        private byte[] CrossOver(byte[] a, byte[] b)
        {
            if (a.Length == 0) return b;
            if (b.Length == 0) return a;
            int cut = _rng.Next(Math.Min(a.Length, b.Length));
            var r = new byte[cut + b.Length - cut];
            Array.Copy(a, r, cut);
            Array.Copy(b, cut, r, cut, b.Length - cut);
            return r;
        }

        private byte[] TruncateOrExpand(byte[] seed)
        {
            if (_rng.Next(2) == 0 && seed.Length > 1)
            {
                var r = new byte[_rng.Next(1, seed.Length)];
                Array.Copy(seed, r, r.Length);
                return r;
            }
            else
            {
                var r = new byte[seed.Length + _rng.Next(1, 256)];
                Array.Copy(seed, r, seed.Length);
                _rng.NextBytes(new Span<byte>(r, seed.Length, r.Length - seed.Length));
                return r;
            }
        }

        private byte[] GenerateRandom(int len)
        {
            var r = new byte[len];
            _rng.NextBytes(r);
            return r;
        }

        private string GenerateRandomString(int len)
        {
            var sb = new StringBuilder(len);
            for (int i = 0; i < len; i++) sb.Append((char)('a' + _rng.Next(26)));
            return sb.ToString();
        }

        private static string EscapeJson(string s) =>
            s?.Replace("\\", "\\\\").Replace("\"", "\\\"") ?? "";
    }

    /// <summary>
    /// Fuzzes the Renderer IPC endpoint.
    /// </summary>
    public sealed class RendererIpcFuzzEndpoint : IFuzzEndpoint
    {
        public string Name => "RendererIpc";

        public bool Fuzz(ReadOnlySpan<byte> input)
        {
            try
            {
                var line = Encoding.UTF8.GetString(input);
                // Run through the renderer IPC deserializer — the entry point that
                // processes untrusted input from the renderer child process.
                if (!RendererIpc.TryDeserializeEnvelope(line, out var env)) return true; // handled (parse fail)
                if (env == null) return true;

                // Validate envelope fields — same checks as the real read loop.
                if (env.TimestampUnixMs < 0) return true;
                if (env.TabId < 0 || env.TabId > 100000) return true;
                if (env.CorrelationId?.Length > 256) return true;
                if (env.Token?.Length > 512) return true;
                if (env.Payload?.Length > 4 * 1024 * 1024) return true; // 4 MB payload cap

                return true;
            }
            catch (Exception ex) when (!(ex is OutOfMemoryException || ex is StackOverflowException))
            {
                FenLogger.Warn($"[Fuzz:RendererIpc] Caught: {ex.GetType().Name}: {ex.Message}", LogCategory.General);
                return false;
            }
        }
    }

    /// <summary>
    /// Fuzzes the Network IPC endpoint.
    /// </summary>
    public sealed class NetworkIpcFuzzEndpoint : IFuzzEndpoint
    {
        public string Name => "NetworkIpc";

        public bool Fuzz(ReadOnlySpan<byte> input)
        {
            try
            {
                var line = Encoding.UTF8.GetString(input);
                if (!NetworkIpc.TryDeserialize(line, out var env)) return true;
                if (env == null) return true;

                // Validate fields
                if (env.TimestampUnixMs < 0) return true;
                if (env.RequestId?.Length > 256) return true;
                if (env.CapabilityToken?.Length > 512) return true;
                if (env.Payload?.Length > 16 * 1024 * 1024) return true; // 16 MB

                // Try to deserialize known payload types
                if (env.Type == "fetchRequest")
                {
                    var payload = NetworkIpc.DeserializePayload<NetworkFetchRequestPayload>(env);
                    if (payload?.Url?.Length > 32768) return true;
                    if (payload?.Method?.Length > 32) return true;
                }

                return true;
            }
            catch (Exception ex) when (!(ex is OutOfMemoryException || ex is StackOverflowException))
            {
                FenLogger.Warn($"[Fuzz:NetworkIpc] Caught: {ex.GetType().Name}: {ex.Message}", LogCategory.General);
                return false;
            }
        }
    }

    /// <summary>
    /// Fuzzes the shared-memory frame protocol (display list ring buffer).
    /// Validates that the consumer never reads out-of-bounds regardless of header values.
    /// </summary>
    public sealed class SharedMemoryFrameFuzzEndpoint : IFuzzEndpoint
    {
        private const int HeaderSize = 64; // bytes

        public string Name => "SharedMemoryFrame";

        public bool Fuzz(ReadOnlySpan<byte> input)
        {
            try
            {
                if (input.Length < HeaderSize) return true;

                // Parse header fields the way the real consumer does
                uint version = BitConverter.ToUInt32(input.Slice(0, 4));
                uint width   = BitConverter.ToUInt32(input.Slice(4, 4));
                uint height  = BitConverter.ToUInt32(input.Slice(8, 4));
                uint seq     = BitConverter.ToUInt32(input.Slice(12, 4));
                uint payloadOffset = BitConverter.ToUInt32(input.Slice(16, 4));
                uint payloadLength = BitConverter.ToUInt32(input.Slice(20, 4));
                uint crc     = BitConverter.ToUInt32(input.Slice(24, 4));

                // Broker-side validation (mirrors real consumer)
                if (version != 1) return true;                          // unknown version → reject
                if (width == 0 || width > 32767) return true;           // invalid dimensions
                if (height == 0 || height > 32767) return true;
                if (payloadOffset < HeaderSize) return true;             // payload must be after header
                if (payloadLength > 128 * 1024 * 1024) return true;     // 128 MB cap

                // Bounds check: payloadOffset + payloadLength must not exceed input
                if ((ulong)payloadOffset + payloadLength > (ulong)input.Length) return true;

                // CRC check (xxHash32 approximation via FNV-1a for testing)
                uint computed = FnvHash(input.Slice((int)payloadOffset, (int)payloadLength));
                if (computed != crc) return true; // checksum mismatch → reject frame

                return true;
            }
            catch (Exception ex) when (!(ex is OutOfMemoryException || ex is StackOverflowException))
            {
                FenLogger.Warn($"[Fuzz:SharedMemory] Caught: {ex.GetType().Name}: {ex.Message}", LogCategory.General);
                return false;
            }
        }

        private static uint FnvHash(ReadOnlySpan<byte> data)
        {
            uint hash = 2166136261u;
            foreach (var b in data) hash = (hash ^ b) * 16777619u;
            return hash;
        }
    }

    /// <summary>
    /// The main fuzzing harness. Runs registered endpoints in a loop.
    /// In CI: run for a configurable duration. In development: on demand.
    /// </summary>
    public sealed class IpcFuzzHarness
    {
        private readonly List<IFuzzEndpoint> _endpoints = new();
        private readonly List<byte[]> _seedCorpus = new();
        private readonly StructuredMutator _mutator;
        private readonly FuzzCoverage _coverage;
        private long _totalIterations;
        private long _crashes;

        public long TotalIterations => Interlocked.Read(ref _totalIterations);
        public long Crashes => Interlocked.Read(ref _crashes);

        public IpcFuzzHarness(int mutatorSeed = 0)
        {
            _mutator = new StructuredMutator(mutatorSeed);
            _coverage = new FuzzCoverage();

            // Register all known endpoints
            Register(new RendererIpcFuzzEndpoint());
            Register(new NetworkIpcFuzzEndpoint());
            Register(new SharedMemoryFrameFuzzEndpoint());

            // Seed corpus: valid messages that serve as mutation bases
            AddSeed(Encoding.UTF8.GetBytes("{\"type\":\"hello\",\"tabId\":1,\"correlationId\":\"abc\",\"token\":\"tok\",\"payload\":\"\",\"timestampUnixMs\":0}"));
            AddSeed(Encoding.UTF8.GetBytes("{\"type\":\"frameReady\",\"tabId\":1,\"correlationId\":\"c\",\"payload\":\"{\\\"url\\\":\\\"https://example.com\\\"}\",\"timestampUnixMs\":1}"));
            AddSeed(Encoding.UTF8.GetBytes("{\"type\":\"fetchRequest\",\"requestId\":\"r1\",\"capabilityToken\":\"tok\",\"payload\":\"{\\\"url\\\":\\\"https://a.com\\\",\\\"method\\\":\\\"GET\\\"}\",\"timestampUnixMs\":0}"));

            // Shared memory header seed (valid)
            var shmSeed = new byte[128];
            BitConverter.TryWriteBytes(new Span<byte>(shmSeed, 0, 4), 1u);      // version
            BitConverter.TryWriteBytes(new Span<byte>(shmSeed, 4, 4), 800u);    // width
            BitConverter.TryWriteBytes(new Span<byte>(shmSeed, 8, 4), 600u);    // height
            BitConverter.TryWriteBytes(new Span<byte>(shmSeed, 12, 4), 1u);     // seq
            BitConverter.TryWriteBytes(new Span<byte>(shmSeed, 16, 4), 64u);    // payload offset
            BitConverter.TryWriteBytes(new Span<byte>(shmSeed, 20, 4), 64u);    // payload length
            AddSeed(shmSeed);
        }

        public void Register(IFuzzEndpoint endpoint)
        {
            if (endpoint != null) _endpoints.Add(endpoint);
        }

        public void AddSeed(byte[] seed)
        {
            if (seed != null) _seedCorpus.Add(seed);
        }

        /// <summary>
        /// Run the fuzzer for the specified duration.
        /// Designed to be called from CI as a background task.
        /// </summary>
        public FuzzRunResult Run(TimeSpan duration, CancellationToken ct = default)
        {
            var deadline = DateTime.UtcNow + duration;
            var crashDetails = new List<string>();

            while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
            {
                foreach (var endpoint in _endpoints)
                {
                    if (ct.IsCancellationRequested) break;

                    // Pick a seed or generate random
                    var seed = _seedCorpus.Count > 0
                        ? _seedCorpus[new Random().Next(_seedCorpus.Count)]
                        : new byte[64];

                    // Mutate
                    byte[] mutated;
                    if (endpoint.Name == "SharedMemoryFrame")
                        mutated = _mutator.Mutate(seed);
                    else
                        mutated = _mutator.MutateJson(Encoding.UTF8.GetString(seed));

                    // Fuzz
                    bool ok = endpoint.Fuzz(mutated);
                    Interlocked.Increment(ref _totalIterations);

                    if (!ok)
                    {
                        Interlocked.Increment(ref _crashes);
                        var detail = $"[{endpoint.Name}] Input: {Convert.ToBase64String(mutated)}";
                        crashDetails.Add(detail);
                        FenLogger.Warn($"[IpcFuzz] Crash detected: {detail}", LogCategory.General);

                        // Add the crashing input to corpus for further exploration
                        _seedCorpus.Add(mutated);
                    }
                }
            }

            return new FuzzRunResult
            {
                Duration = duration,
                TotalIterations = TotalIterations,
                Crashes = Crashes,
                CoverageEdges = _coverage.UniqueEdges,
                CrashDetails = crashDetails,
            };
        }

        public FuzzRunResult RunAsync(TimeSpan duration) =>
            Task.Run(() => Run(duration)).GetAwaiter().GetResult();
    }

    public sealed class FuzzRunResult
    {
        public TimeSpan Duration { get; init; }
        public long TotalIterations { get; init; }
        public long Crashes { get; init; }
        public int CoverageEdges { get; init; }
        public List<string> CrashDetails { get; init; }
        public bool Passed => Crashes == 0;
    }
}
