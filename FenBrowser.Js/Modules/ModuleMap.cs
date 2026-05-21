namespace FenBrowser.Js.Modules;

// Per-realm cache of fetched/parsed modules keyed by (referrer-realm, specifier).
//
// Spec context: HTML's "import map" interacts with this cache, but at the ECMAScript
// layer the model is "two requests for the same specifier from the same realm return
// the same Module Record". HostResolveImportedModule consults this map first; if
// missing, it fetches/parses and inserts so subsequent imports observe the same
// instance (preserves live-binding identity).
//
// Keyed by `int realmId` because Realm objects are not yet first-class in the engine
// (Realm support lands with host integration). Treating the realm as an opaque
// integer keeps the map decoupled from the eventual Realm type.
public sealed class ModuleMap
{
    private readonly Dictionary<Key, ModuleRecord> _cache = new();

    public int Count => _cache.Count;

    public bool TryGet(int realmId, string specifier, out ModuleRecord? module)
    {
        ArgumentNullException.ThrowIfNull(specifier);
        if (_cache.TryGetValue(new Key(realmId, specifier), out var found))
        {
            module = found;
            return true;
        }

        module = null;
        return false;
    }

    // Returns true when the module was newly added; false when an entry already
    // existed (caller can decide to assert / overwrite / ignore). Existing entries
    // are NOT overwritten - module identity must be stable per the cache contract.
    public bool TryAdd(int realmId, string specifier, ModuleRecord module)
    {
        ArgumentNullException.ThrowIfNull(specifier);
        ArgumentNullException.ThrowIfNull(module);
        return _cache.TryAdd(new Key(realmId, specifier), module);
    }

    public void Clear() => _cache.Clear();

    public IEnumerable<(int RealmId, string Specifier, ModuleRecord Module)> EnumerateForTest()
    {
        foreach (var (key, value) in _cache)
        {
            yield return (key.RealmId, key.Specifier, value);
        }
    }

    private readonly record struct Key(int RealmId, string Specifier);
}
