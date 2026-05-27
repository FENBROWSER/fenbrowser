using System;
using System.Collections.Generic;
using FenBrowser.Js.Host;
using FenBrowser.Js.Runtime;
using Xunit;

namespace FenBrowser.Js.Fuzz;

// Audit doc §8.c. Randomized lifecycle fuzz on HostObjectTable. Every
// Resolve must either succeed (the handle currently matches a live slot
// for the active realm/epochs) or return Invalid with a reason. The
// table must NEVER throw and must NEVER dereference a freed/stale slot.
//
// The three crashes from audit §1 (Stale heap handle) live in the JS
// heap, not this table -- but the architectural contract being tested
// here ("any structurally-malformed handle resolves to Invalid, not a
// crash") is the same one those crashes violated.
public sealed class HostHandleFuzz
{
    private const int IterationsPerSeed = 4000;

    [Theory]
    [InlineData(901)]
    [InlineData(902)]
    [InlineData(903)]
    [InlineData(904)]
    public void RandomLifecycle_NeverCrashes_AndStaleHandlesAlwaysResolveInvalid(int seed)
    {
        var rnd = new Random(seed);
        var table = new HostObjectTable();
        var ctx = new HostObjectResolveContext(
            CurrentRealmId: 1,
            CurrentDocumentEpoch: DocumentEpoch.Initial,
            CurrentNavigationEpoch: NavigationEpoch.Initial);

        // Live handles + a shadow set of definitely-stale handles for
        // negative oracle coverage.
        var live = new List<HostObjectHandle>();
        var stale = new List<HostObjectHandle>();

        for (var i = 0; i < IterationsPerSeed; i++)
        {
            // Choose an action weighted toward register/resolve so the
            // table grows large enough to exercise slot recycling.
            var action = rnd.Next(0, 100);
            if (action < 35)
            {
                // Register
                var entry = new HostObjectEntry(
                    Generation: 0,
                    Kind: HostObjectKind.Other,
                    RealmId: 1,
                    Origin: "https://test.example",
                    DocumentEpoch: DocumentEpoch.Initial,
                    NavigationEpoch: NavigationEpoch.Initial,
                    FrameId: 0,
                    PermissionFlags: 0);
                var h = table.Register(new object(), entry);
                live.Add(h);
            }
            else if (action < 55 && live.Count > 0)
            {
                // Free a random live handle. Recycling MUST bump generation
                // so the freed handle becomes stale.
                var idx = rnd.Next(live.Count);
                var h = live[idx];
                live.RemoveAt(idx);
                Assert.True(table.Free(h));
                stale.Add(h);
                // Double-free must return false, not crash.
                Assert.False(table.Free(h));
            }
            else if (action < 75 && live.Count > 0)
            {
                // Resolve a known-live handle. Must be Ok.
                var h = live[rnd.Next(live.Count)];
                var r = table.Resolve(h, ctx);
                Assert.True(r.IsOk, $"live handle resolved Invalid: {r.Reason}");
                Assert.NotNull(r.HostObject);
            }
            else if (action < 85 && stale.Count > 0)
            {
                // Resolve a known-stale handle. Must be Invalid.
                var h = stale[rnd.Next(stale.Count)];
                var r = table.Resolve(h, ctx);
                Assert.False(r.IsOk, "stale handle resolved Ok — would be UAF in production");
                Assert.NotNull(r.Reason);
            }
            else if (action < 92)
            {
                // Random structurally-malformed handle. Must NOT crash;
                // must return Invalid (index OOB / wrong gen / etc.).
                var bogus = new HostObjectHandle(
                    Index: rnd.Next(0, ushort.MaxValue),
                    Generation: rnd.Next(0, ushort.MaxValue),
                    RealmId: rnd.Next(0, ushort.MaxValue),
                    DocumentEpoch: rnd.Next(0, ushort.MaxValue));
                var r = table.Resolve(bogus, ctx);
                // It MIGHT randomly happen to match a live slot for the
                // default realm/epoch. The contract is only "doesn't crash".
                if (!r.IsOk) Assert.NotNull(r.Reason);
            }
            else if (action < 96 && live.Count > 0)
            {
                // Resolve a live handle under a DIFFERENT realm. Must be Invalid.
                var h = live[rnd.Next(live.Count)];
                var otherRealm = new HostObjectResolveContext(
                    CurrentRealmId: 999,
                    CurrentDocumentEpoch: ctx.CurrentDocumentEpoch,
                    CurrentNavigationEpoch: ctx.CurrentNavigationEpoch);
                var r = table.Resolve(h, otherRealm);
                Assert.False(r.IsOk);
                Assert.Contains("realm", r.Reason ?? "", StringComparison.OrdinalIgnoreCase);
            }
            else if (live.Count > 0)
            {
                // Resolve under a navigated DocumentEpoch. Must be Invalid.
                var h = live[rnd.Next(live.Count)];
                var navigated = new HostObjectResolveContext(
                    CurrentRealmId: ctx.CurrentRealmId,
                    CurrentDocumentEpoch: ctx.CurrentDocumentEpoch.Next(),
                    CurrentNavigationEpoch: ctx.CurrentNavigationEpoch);
                var r = table.Resolve(h, navigated);
                Assert.False(r.IsOk);
            }
        }

        // Final invariant: live count matches our tracked list.
        Assert.Equal(live.Count, table.LiveCount);
    }

    [Fact]
    public void RecycledSlot_OldHandleResolvesInvalid_NewHandleResolvesOk()
    {
        // Spec'd contract: when Free + Register hit the same index, the
        // generation MUST advance so the old handle is stale.
        var table = new HostObjectTable();
        var entry = new HostObjectEntry(
            Generation: 0,
            Kind: HostObjectKind.Other,
            RealmId: 1,
            Origin: "https://test.example",
            DocumentEpoch: DocumentEpoch.Initial,
            NavigationEpoch: NavigationEpoch.Initial,
            FrameId: 0,
            PermissionFlags: 0);
        var ctx = new HostObjectResolveContext(1, DocumentEpoch.Initial, NavigationEpoch.Initial);

        var firstHandle = table.Register(new object(), entry);
        Assert.True(table.Resolve(firstHandle, ctx).IsOk);

        Assert.True(table.Free(firstHandle));
        Assert.False(table.Resolve(firstHandle, ctx).IsOk);

        var secondHandle = table.Register(new object(), entry);
        // Same slot, bumped generation.
        Assert.Equal(firstHandle.Index, secondHandle.Index);
        Assert.NotEqual(firstHandle.Generation, secondHandle.Generation);
        Assert.True(table.Resolve(secondHandle, ctx).IsOk);
        // First handle still stale even after recycle.
        var staleResolve = table.Resolve(firstHandle, ctx);
        Assert.False(staleResolve.IsOk);
        Assert.Contains("generation", staleResolve.Reason ?? "", StringComparison.OrdinalIgnoreCase);
    }
}
