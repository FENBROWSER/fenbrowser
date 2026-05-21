using FenBrowser.Js.Modules;
using Xunit;

namespace FenBrowser.Js.Tests;

public sealed class ModuleMapTests
{
    [Fact]
    public void NewMapIsEmpty()
    {
        var map = new ModuleMap();

        Assert.Equal(0, map.Count);
        Assert.False(map.TryGet(0, "any", out _));
    }

    [Fact]
    public void TryAddInsertsAndReturnsTrueFirstTime()
    {
        var map = new ModuleMap();
        var module = NewModule();

        Assert.True(map.TryAdd(0, "mod", module));
        Assert.Equal(1, map.Count);

        Assert.True(map.TryGet(0, "mod", out var fetched));
        Assert.Same(module, fetched);
    }

    [Fact]
    public void TryAddIsIdempotentAndPreservesIdentity()
    {
        // Module identity is stable: a second resolve for the same specifier must
        // return the original module, not a freshly-parsed one. The map enforces
        // this by refusing to overwrite.
        var map = new ModuleMap();
        var first = NewModule();
        var second = NewModule();

        Assert.True(map.TryAdd(0, "mod", first));
        Assert.False(map.TryAdd(0, "mod", second));

        Assert.True(map.TryGet(0, "mod", out var fetched));
        Assert.Same(first, fetched);
    }

    [Fact]
    public void DifferentRealmsHaveSeparateEntries()
    {
        var map = new ModuleMap();
        var a = NewModule();
        var b = NewModule();

        map.TryAdd(realmId: 1, "mod", a);
        map.TryAdd(realmId: 2, "mod", b);

        Assert.Equal(2, map.Count);
        map.TryGet(1, "mod", out var fetchedA);
        map.TryGet(2, "mod", out var fetchedB);
        Assert.Same(a, fetchedA);
        Assert.Same(b, fetchedB);
    }

    [Fact]
    public void ClearRemovesAllEntries()
    {
        var map = new ModuleMap();
        map.TryAdd(0, "mod", NewModule());
        map.TryAdd(0, "other", NewModule());

        map.Clear();

        Assert.Equal(0, map.Count);
    }

    private static SourceTextModuleRecord NewModule() => new(
        realmId: 0,
        parsedCode: null,
        importEntries: Array.Empty<ImportEntry>(),
        localExportEntries: Array.Empty<ExportEntry>(),
        indirectExportEntries: Array.Empty<ExportEntry>(),
        starExportEntries: Array.Empty<ExportEntry>());
}
