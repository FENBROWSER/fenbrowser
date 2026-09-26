using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FenBrowser.Media.Tests;

/// <summary>
/// The generated fixtures in <c>test_assets/media</c>, described by <c>manifest.json</c>
/// (see <c>scripts/media/gen_fixtures.py</c>).
/// </summary>
public static class MediaFixtures
{
    private static readonly Lazy<(string Directory, Manifest Manifest)> s_loaded = new(Load);

    public static string Directory => s_loaded.Value.Directory;

    public static IReadOnlyList<Fixture> All => s_loaded.Value.Manifest.Fixtures;

    public static IReadOnlyList<Reference> References => s_loaded.Value.Manifest.References ?? [];

    public static Fixture Get(string file) => All.Single(f => f.File == file);

    public static byte[] Read(string file) => File.ReadAllBytes(Path.Combine(Directory, file));

    /// <summary>xUnit member data: one row per fixture file name.</summary>
    public static TheoryData<string> Files()
    {
        var data = new TheoryData<string>();
        foreach (var fixture in All)
            data.Add(fixture.File);
        return data;
    }

    private static (string, Manifest) Load()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, "test_assets", "media");
            string manifestPath = Path.Combine(candidate, "manifest.json");
            if (File.Exists(manifestPath))
            {
                var manifest = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(manifestPath))
                    ?? throw new InvalidDataException("manifest.json is empty.");
                return (candidate, manifest);
            }
        }

        throw new DirectoryNotFoundException("test_assets/media/manifest.json was not found above " + AppContext.BaseDirectory);
    }

    public static string Sha256(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    public sealed record Manifest(
        [property: JsonPropertyName("fixtures")] List<Fixture> Fixtures,
        [property: JsonPropertyName("references")] List<Reference>? References);

    /// <summary>ffmpeg's decode of a fixture, for decoders that are not bit-exact with libavcodec.</summary>
    public sealed record Reference(
        [property: JsonPropertyName("file")] string File,
        [property: JsonPropertyName("source")] string Source,
        [property: JsonPropertyName("bytes")] long Bytes,
        [property: JsonPropertyName("sha256")] string Sha256,
        [property: JsonPropertyName("note")] string Note);

    public sealed record Fixture(
        [property: JsonPropertyName("file")] string File,
        [property: JsonPropertyName("bytes")] long Bytes,
        [property: JsonPropertyName("sha256")] string Sha256,
        [property: JsonPropertyName("sniff")] string? Sniff,
        [property: JsonPropertyName("note")] string Note,
        [property: JsonPropertyName("container")] string Container,
        [property: JsonPropertyName("durationSeconds")] double? DurationSeconds,
        [property: JsonPropertyName("streams")] List<Stream> Streams);

    public sealed record Stream(
        [property: JsonPropertyName("index")] int Index,
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("codec")] string Codec,
        [property: JsonPropertyName("timeBase")] string? TimeBase,
        [property: JsonPropertyName("width")] int? Width,
        [property: JsonPropertyName("height")] int? Height,
        [property: JsonPropertyName("sampleRate")] int? SampleRate,
        [property: JsonPropertyName("channels")] int? Channels);
}
