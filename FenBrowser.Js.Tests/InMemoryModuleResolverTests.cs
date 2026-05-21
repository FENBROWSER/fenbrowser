using FenBrowser.Js.Modules;
using Xunit;

namespace FenBrowser.Js.Tests;

public sealed class InMemoryModuleResolverTests
{
    [Fact]
    public void ResolveReturnsNullForUnknownSpecifier()
    {
        var resolver = new InMemoryModuleResolver(new ModuleMap(), defaultRealmId: 0);

        Assert.Null(resolver.Resolve(referrer: null, "missing"));
    }

    [Fact]
    public void RegisterAndResolveRoundTripsTheSameModule()
    {
        var map = new ModuleMap();
        var resolver = new InMemoryModuleResolver(map, defaultRealmId: 0);
        var module = NewModule();

        resolver.Register("mod", module);

        Assert.Same(module, resolver.Resolve(referrer: null, "mod"));
    }

    [Fact]
    public void ResolveHonorsReferrerRealmWhenPresent()
    {
        var map = new ModuleMap();
        var inRealm0 = NewModule(realmId: 0);
        var inRealm1 = NewModule(realmId: 1);
        map.TryAdd(0, "mod", inRealm0);
        map.TryAdd(1, "mod", inRealm1);
        var resolver = new InMemoryModuleResolver(map, defaultRealmId: 0);

        var referrerInRealm1 = NewModule(realmId: 1);

        Assert.Same(inRealm0, resolver.Resolve(referrer: null, "mod"));
        Assert.Same(inRealm1, resolver.Resolve(referrerInRealm1, "mod"));
    }

    private static SourceTextModuleRecord NewModule(int realmId = 0) => new(
        realmId: realmId,
        parsedCode: null,
        importEntries: Array.Empty<ImportEntry>(),
        localExportEntries: Array.Empty<ExportEntry>(),
        indirectExportEntries: Array.Empty<ExportEntry>(),
        starExportEntries: Array.Empty<ExportEntry>());
}
