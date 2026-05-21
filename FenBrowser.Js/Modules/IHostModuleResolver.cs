namespace FenBrowser.Js.Modules;

// ECMA-262 16.2.1.7 HostLoadImportedModule (formerly HostResolveImportedModule).
//
// The engine knows how to parse and link modules but does not know how to fetch them
// or how to interpret a specifier ("./util.js", "react", "https://...", etc.). The
// host (FenBrowser renderer, standalone shell, test harness) implements this
// interface to translate a referrer + specifier pair into a ModuleRecord.
//
// Resolution semantics:
//   * Identity must be stable - calling Resolve twice for the same (referrer,
//     specifier) pair must return the same ModuleRecord. Implementations should
//     consult a ModuleMap before fetching.
//   * Failure is signaled by returning null. The caller (interpreter / module
//     evaluator) raises the appropriate JS error (TypeError for failed fetch,
//     SyntaxError for unparseable module source, etc.).
public interface IHostModuleResolver
{
    ModuleRecord? Resolve(ModuleRecord? referrer, string specifier);
}

// In-memory test resolver backed by a ModuleMap. Used by the standalone shell when
// loading from disk and by unit tests; the FenBrowser renderer plugs in a fetch-aware
// implementation that knows about origins, import maps, and CORS.
public sealed class InMemoryModuleResolver : IHostModuleResolver
{
    private readonly ModuleMap _map;
    private readonly int _defaultRealmId;

    public InMemoryModuleResolver(ModuleMap map, int defaultRealmId)
    {
        ArgumentNullException.ThrowIfNull(map);
        _map = map;
        _defaultRealmId = defaultRealmId;
    }

    public void Register(string specifier, ModuleRecord module)
    {
        ArgumentNullException.ThrowIfNull(specifier);
        ArgumentNullException.ThrowIfNull(module);
        _ = _map.TryAdd(_defaultRealmId, specifier, module);
    }

    public ModuleRecord? Resolve(ModuleRecord? referrer, string specifier)
    {
        ArgumentNullException.ThrowIfNull(specifier);
        var realmId = referrer?.RealmId ?? _defaultRealmId;
        return _map.TryGet(realmId, specifier, out var module) ? module : null;
    }
}
