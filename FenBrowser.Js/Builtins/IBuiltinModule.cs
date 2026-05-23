namespace FenBrowser.Js.Builtins;

// Contract every builtin module (Math, JSON, Date, the error constructors, etc.)
// implements so that BuiltinRegistry can install them onto a realm's global without
// hard-coding the per-builtin construction logic into BytecodeInterpreter.
//
// A module is a stateless factory: GetBindings is called once per realm, lazily, when
// the registry needs to materialize the module's exports. The context provides heap
// access for allocation and coercion services (ToNumber, etc.) that route through the
// interpreter's reentrancy-safe paths.
//
// Modules carry their ECMA-262 spec section via [EcmaSpecReference] on the
// implementing type so audits can map each module to its spec home without parsing
// the module's name.
public interface IBuiltinModule
{
    // Stable identifier used by BuiltinRegistry for lookup and ordering. Must match
    // the spec name where one exists ("Math", "JSON", "Date", "GlobalConstants"); the
    // registry rejects duplicates so modules can rely on the name being unique within
    // a realm.
    string Name { get; }

    // Lazy build of the module's exports. Implementations are free to allocate heap
    // objects, install methods, etc.; the registry calls this exactly once per realm
    // per module and pins any ObjectHandle values it receives via WriteBarrier-friendly
    // ordinary global property installs.
    IReadOnlyList<BuiltinBinding> GetBindings(IBuiltinContext context);
}
