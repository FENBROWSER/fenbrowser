using FenBrowser.Media.Sniffing;

namespace FenBrowser.Media.Tests;

public class MediaFixtureSniffTests
{
    [Theory]
    [MemberData(nameof(MediaFixtures.Files), MemberType = typeof(MediaFixtures))]
    public void Fixture_IsIntact(string file)
    {
        var fixture = MediaFixtures.Get(file);
        byte[] bytes = MediaFixtures.Read(file);

        Assert.Equal(fixture.Bytes, bytes.Length);
        Assert.Equal(fixture.Sha256, MediaFixtures.Sha256(bytes));
    }

    [Theory]
    [MemberData(nameof(MediaFixtures.Files), MemberType = typeof(MediaFixtures))]
    public void Fixture_SniffsAsTheManifestSays(string file)
    {
        byte[] bytes = MediaFixtures.Read(file);
        var header = bytes.AsSpan(0, Math.Min(bytes.Length, MediaSniffer.ResourceHeaderLength));

        Assert.Equal(MediaFixtures.Get(file).Sniff, MediaSniffer.Sniff(header));
    }

    [Fact]
    public void Manifest_CoversEveryFixtureFile()
    {
        var listed = MediaFixtures.All.Select(f => f.File).Concat(MediaFixtures.References.Select(r => r.File)).ToHashSet(StringComparer.Ordinal);
        var onDisk = Directory.EnumerateFiles(MediaFixtures.Directory)
            .Select(Path.GetFileName)
            .Where(n => n is not "manifest.json" and not ".gitattributes")
            .ToHashSet(StringComparer.Ordinal);

        Assert.Equal(listed.Order(), onDisk.Order()!);
    }
}
