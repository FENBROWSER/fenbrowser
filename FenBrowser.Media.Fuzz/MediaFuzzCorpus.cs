namespace FenBrowser.Media.Fuzz;

/// <summary>
/// Seed inputs and a structure-aware mutator for the media fuzz targets.
/// </summary>
/// <remarks>
/// Seeds are the headers of the generated fixtures in <c>test_assets/media</c> plus a few
/// hand-built edge cases, so mutations start from real container layouts instead of noise.
/// Every run uses a fixed seed; a failure message carries the input as hex so it can be
/// pasted into a unit test.
/// </remarks>
internal static class MediaFuzzCorpus
{
    public const int HeaderLength = 1445;

    private static readonly Lazy<IReadOnlyList<byte[]>> s_seeds = new(LoadSeeds);

    public static IReadOnlyList<byte[]> Seeds => s_seeds.Value;

    // Byte runs that steer mutations into the interesting branches of the parsers.
    private static readonly byte[][] s_tokens =
    [
        [0x1A, 0x45, 0xDF, 0xA3],            // EBML magic
        [0x42, 0x82],                        // EBML DocType ID
        "webm"u8.ToArray(),
        "matroska"u8.ToArray(),
        "ftyp"u8.ToArray(),
        "mp4"u8.ToArray(),
        "mp41"u8.ToArray(),
        "isom"u8.ToArray(),
        "OggS"u8.ToArray(),
        "RIFF"u8.ToArray(),
        "WAVE"u8.ToArray(),
        "ID3"u8.ToArray(),
        [0xFF, 0xFB, 0x90, 0x64],            // MPEG-1 Layer III header
        [0xFF, 0xF3, 0x80, 0x00],            // MPEG-2 Layer III header
        [0xFF, 0xFF, 0xFF, 0xFF],
        [0x00, 0x00, 0x00, 0x00],
        [0x80], [0x40], [0x01], [0x00], [0xFF],
    ];

    public static string Hex(ReadOnlySpan<byte> bytes) => Convert.ToHexString(bytes);

    /// <summary>Returns a new input derived from <paramref name="seed"/> by 1–8 random mutations.</summary>
    public static byte[] Mutate(byte[] seed, Random random)
    {
        var data = new List<byte>(seed);
        int rounds = random.Next(1, 9);
        for (int r = 0; r < rounds; r++)
        {
            switch (random.Next(8))
            {
                case 0 when data.Count > 0: // flip one bit
                    {
                        int i = random.Next(data.Count);
                        data[i] ^= (byte)(1 << random.Next(8));
                        break;
                    }

                case 1 when data.Count > 0: // overwrite one byte
                    data[random.Next(data.Count)] = (byte)random.Next(256);
                    break;
                case 2: // insert a token
                    {
                        var token = s_tokens[random.Next(s_tokens.Length)];
                        data.InsertRange(random.Next(data.Count + 1), token);
                        break;
                    }

                case 3 when data.Count > 0: // overwrite with a token
                    {
                        var token = s_tokens[random.Next(s_tokens.Length)];
                        int at = random.Next(data.Count);
                        for (int i = 0; i < token.Length && at + i < data.Count; i++)
                            data[at + i] = token[i];
                        break;
                    }

                case 4 when data.Count > 0: // truncate
                    {
                        int keep = random.Next(data.Count);
                        data.RemoveRange(keep, data.Count - keep);
                        break;
                    }
                case 5 when data.Count >= 4: // corrupt a big-endian size field
                    {
                        int at = random.Next(data.Count - 3);
                        uint value = random.Next(4) switch
                        {
                            0 => 0,
                            1 => uint.MaxValue,
                            2 => (uint)random.Next(64),
                            _ => (uint)random.Next(),
                        };
                        data[at] = (byte)(value >> 24);
                        data[at + 1] = (byte)(value >> 16);
                        data[at + 2] = (byte)(value >> 8);
                        data[at + 3] = (byte)value;
                        break;
                    }

                case 6 when data.Count > 1: // delete a run
                    {
                        int at = random.Next(data.Count);
                        data.RemoveRange(at, Math.Min(data.Count - at, random.Next(1, 16)));
                        break;
                    }

                default: // splice in part of another seed
                    {
                        var other = Seeds[random.Next(Seeds.Count)];
                        if (other.Length == 0)
                            break;
                        int from = random.Next(other.Length);
                        int count = Math.Min(other.Length - from, random.Next(1, 64));
                        data.InsertRange(random.Next(data.Count + 1), other.AsSpan(from, count).ToArray());
                        break;
                    }
            }
        }

        if (data.Count > HeaderLength)
            data.RemoveRange(HeaderLength, data.Count - HeaderLength);
        return [.. data];
    }

    private static IReadOnlyList<byte[]> LoadSeeds()
    {
        List<byte[]> seeds =
        [
            [],
            [0x1A, 0x45, 0xDF, 0xA3, 0x42, 0x82, 0x84],
            [0x00, 0x00, 0x00, 0x0C, 0x66, 0x74, 0x79, 0x70, 0x6D, 0x70, 0x34, 0x32],
            [0xFF, 0xFB, 0x90, 0x64],
        ];

        string? dir = FindFixtureDirectory();
        if (dir is null)
            throw new DirectoryNotFoundException("test_assets/media was not found; the fuzz corpus needs the generated fixtures.");

        foreach (string file in Directory.EnumerateFiles(dir).Order(StringComparer.Ordinal))
        {
            string name = Path.GetFileName(file);
            if (name is "manifest.json" or ".gitattributes")
                continue;
            byte[] bytes = File.ReadAllBytes(file);
            seeds.Add(bytes.AsSpan(0, Math.Min(bytes.Length, HeaderLength)).ToArray());
        }

        return seeds;
    }

    private static string? FindFixtureDirectory()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, "test_assets", "media");
            if (File.Exists(Path.Combine(candidate, "manifest.json")))
                return candidate;
        }

        return null;
    }
}
