using FenBrowser.Js.Host;
using Xunit;

namespace FenBrowser.Js.Tests;

public sealed class HostObjectEntryTests
{
    [Fact]
    public void EntryRoundTripsAllFields()
    {
        var entry = new HostObjectEntry(
            Generation: 3,
            Kind: HostObjectKind.DomElement,
            RealmId: 7,
            Origin: "https://example.test",
            DocumentEpoch: new DocumentEpoch(5),
            NavigationEpoch: new NavigationEpoch(2),
            FrameId: 11,
            PermissionFlags: 0b101);

        Assert.Equal(3, entry.Generation);
        Assert.Equal(HostObjectKind.DomElement, entry.Kind);
        Assert.Equal(7, entry.RealmId);
        Assert.Equal("https://example.test", entry.Origin);
        Assert.Equal(5L, entry.DocumentEpoch.Value);
        Assert.Equal(2L, entry.NavigationEpoch.Value);
        Assert.Equal(11, entry.FrameId);
        Assert.Equal(0b101, entry.PermissionFlags);
    }

    [Fact]
    public void EntriesWithDifferentEpochsAreNotEqual()
    {
        var a = new HostObjectEntry(
            Generation: 1, Kind: HostObjectKind.DomNode, RealmId: 0,
            Origin: "https://a", DocumentEpoch: new DocumentEpoch(1),
            NavigationEpoch: new NavigationEpoch(1), FrameId: 0, PermissionFlags: 0);
        var b = a with { DocumentEpoch = new DocumentEpoch(2) };

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void RecordEqualityHoldsForIdenticalEntries()
    {
        var a = new HostObjectEntry(
            Generation: 1, Kind: HostObjectKind.DomNode, RealmId: 0,
            Origin: "https://a", DocumentEpoch: new DocumentEpoch(1),
            NavigationEpoch: new NavigationEpoch(1), FrameId: 0, PermissionFlags: 0);
        var b = a;

        Assert.Equal(a, b);
    }
}
