namespace FenBrowser.Js.Builtins;

// Per-realm collection of builtin modules. The registry owns:
//   * insertion-ordered registration (modules install in deterministic order so a
//     module that depends on another - e.g. Promise needing %Promise.prototype% - sees
//     a fully-built dependency),
//   * duplicate-name rejection (two modules claiming the same Name is a configuration
//     bug, not a silent overwrite),
//   * a one-shot materialize pass that calls each module's GetBindings(context) exactly
//     once and returns the flattened binding list ready for the interpreter (or any
//     other host) to install on its global object.
//
// The registry itself is realm-agnostic - it never touches the heap. The Materialize
// step is the only place the context is involved, so a host that wants to share a
// registry definition across realms can register modules once at startup and call
// Materialize per realm.
public sealed class BuiltinRegistry
{
    private readonly List<IBuiltinModule> _modules = new();
    private readonly HashSet<string> _names = new(StringComparer.Ordinal);

    public IReadOnlyList<IBuiltinModule> Modules => _modules;

    // Register a module. Throws ArgumentException when another module with the same
    // Name is already present so misconfigured hosts fail loudly at startup.
    public BuiltinRegistry Register(IBuiltinModule module)
    {
        ArgumentNullException.ThrowIfNull(module);

        if (string.IsNullOrEmpty(module.Name))
        {
            throw new ArgumentException("Builtin module name must be a non-empty string.", nameof(module));
        }

        if (!_names.Add(module.Name))
        {
            throw new ArgumentException($"Builtin module '{module.Name}' is already registered.", nameof(module));
        }

        _modules.Add(module);
        return this;
    }

    public bool Contains(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return _names.Contains(name);
    }

    // Build every registered module's bindings against the given context, preserving
    // registration order. Each module is queried exactly once; the caller is expected
    // to install the resulting BuiltinBinding entries onto its global object using
    // whatever DefineOwnProperty surface the host has.
    public IReadOnlyList<BuiltinBinding> Materialize(IBuiltinContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var result = new List<BuiltinBinding>(_modules.Count);
        foreach (var module in _modules)
        {
            var bindings = module.GetBindings(context);
            if (bindings is null)
            {
                continue;
            }

            foreach (var binding in bindings)
            {
                result.Add(binding);
            }
        }

        return result;
    }
}
