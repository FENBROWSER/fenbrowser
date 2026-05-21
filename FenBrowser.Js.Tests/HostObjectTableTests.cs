using FenBrowser.Js.Host;
using Xunit;

namespace FenBrowser.Js.Tests;

public sealed class HostObjectTableTests
{
    [Fact]
    public void RegisterAndResolveRoundTrips()
    {
        var table = new HostObjectTable();
        var marker = new object();
        var entry = NewEntry();

        var handle = table.Register(marker, entry);
        var resolution = table.Resolve(handle, NewContext());

        Assert.True(resolution.IsOk);
        Assert.Same(marker, resolution.HostObject);
        Assert.Equal(1, table.LiveCount);
    }

    [Fact]
    public void ResolveRejectsCrossRealmAccess()
    {
        var table = new HostObjectTable();
        var handle = table.Register(new object(), NewEntry(realmId: 7));

        var resolution = table.Resolve(handle, NewContext(currentRealmId: 99));

        Assert.False(resolution.IsOk);
        Assert.Contains("realm", resolution.Reason);
    }

    [Fact]
    public void ResolveRejectsDocumentEpochMismatch()
    {
        var table = new HostObjectTable();
        var handle = table.Register(new object(), NewEntry(documentEpoch: new DocumentEpoch(5)));

        var resolution = table.Resolve(handle, NewContext(currentDocumentEpoch: new DocumentEpoch(6)));

        Assert.False(resolution.IsOk);
        Assert.Contains("document", resolution.Reason);
    }

    [Fact]
    public void ResolveRejectsNavigationEpochMismatch()
    {
        var table = new HostObjectTable();
        var handle = table.Register(new object(), NewEntry(navigationEpoch: new NavigationEpoch(2)));

        var resolution = table.Resolve(handle, NewContext(currentNavigationEpoch: new NavigationEpoch(3)));

        Assert.False(resolution.IsOk);
        Assert.Contains("navigated", resolution.Reason);
    }

    [Fact]
    public void FreeMarksSlotInvalidAndRecyclesWithNewGeneration()
    {
        var table = new HostObjectTable();
        var handle = table.Register(new object(), NewEntry());

        Assert.True(table.Free(handle));
        Assert.Equal(0, table.LiveCount);

        var resolution = table.Resolve(handle, NewContext());
        Assert.False(resolution.IsOk);

        // Re-register: same index, bumped generation. The stale handle remains
        // invalid against the new slot, so previously-captured handles cannot
        // resurrect freed objects.
        var newHandle = table.Register(new object(), NewEntry());
        Assert.Equal(handle.Index, newHandle.Index);
        Assert.NotEqual(handle.Generation, newHandle.Generation);

        Assert.False(table.Resolve(handle, NewContext()).IsOk);
        Assert.True(table.Resolve(newHandle, NewContext()).IsOk);
    }

    [Fact]
    public void FreeOnUnknownOrAlreadyFreedHandleReturnsFalse()
    {
        var table = new HostObjectTable();
        var handle = table.Register(new object(), NewEntry());

        Assert.True(table.Free(handle));
        Assert.False(table.Free(handle));
        Assert.False(table.Free(new FenBrowser.Js.Runtime.HostObjectHandle(999, 1, 0, 1)));
    }

    [Fact]
    public void OutOfRangeIndexIsRejectedGracefully()
    {
        var table = new HostObjectTable();
        var resolution = table.Resolve(
            new FenBrowser.Js.Runtime.HostObjectHandle(42, 1, 0, 1),
            NewContext());

        Assert.False(resolution.IsOk);
    }

    private static HostObjectEntry NewEntry(
        int realmId = 0,
        DocumentEpoch documentEpoch = default,
        NavigationEpoch navigationEpoch = default)
    {
        return new HostObjectEntry(
            Generation: 0,
            Kind: HostObjectKind.Other,
            RealmId: realmId,
            Origin: "https://test",
            DocumentEpoch: documentEpoch.Value == 0 ? DocumentEpoch.Initial : documentEpoch,
            NavigationEpoch: navigationEpoch.Value == 0 ? NavigationEpoch.Initial : navigationEpoch,
            FrameId: 0,
            PermissionFlags: 0);
    }

    private static HostObjectResolveContext NewContext(
        int currentRealmId = 0,
        DocumentEpoch currentDocumentEpoch = default,
        NavigationEpoch currentNavigationEpoch = default)
    {
        return new HostObjectResolveContext(
            currentRealmId,
            currentDocumentEpoch.Value == 0 ? DocumentEpoch.Initial : currentDocumentEpoch,
            currentNavigationEpoch.Value == 0 ? NavigationEpoch.Initial : currentNavigationEpoch);
    }
}
