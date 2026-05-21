using FenBrowser.Js.Environments;
using FenBrowser.Js.Runtime;

namespace FenBrowser.Js.Modules;

// ECMA-262 16.2.1.4 Abstract Module Records.
//
// Slots modeled here:
//   [[Realm]]       : the realm in which the module was evaluated (host-supplied).
//   [[Environment]] : the ModuleEnvironmentRecord that holds the module's bindings.
//                     Set during Link.
//   [[Namespace]]   : the module namespace object once GetModuleNamespace has been
//                     called; null until then.
//   [[HostDefined]] : opaque host data (currently unused; reserved for future host
//                     integration).
//   [[Status]]      : ModuleStatus value driving the link/evaluate state machine.
//
// Concrete subclasses (SourceTextModuleRecord, future SyntheticModuleRecord) override
// the abstract operations: GetExportedNames, ResolveExport, Link, Evaluate.
public abstract class ModuleRecord
{
    protected ModuleRecord(int realmId)
    {
        RealmId = realmId;
        Status = ModuleStatus.Unlinked;
    }

    public int RealmId { get; }

    public ModuleStatus Status { get; protected set; }

    public ModuleEnvironmentRecord? Environment { get; protected set; }

    public JsValue Namespace { get; protected set; } = JsValue.Undefined;

    public object? HostDefined { get; init; }

    // The error that terminated evaluation, if any. Set when Status transitions to
    // Evaluated via a failure path. JsValue.Undefined indicates a successful run.
    public JsValue EvaluationError { get; protected set; } = JsValue.Undefined;

    public bool HasEvaluationError => EvaluationError.Tag != JsValueTag.Undefined;

    // 16.2.1.4.1 GetExportedNames ( [ exportStarSet ] ). Returns the names this module
    // exports; subclasses override.
    public abstract IReadOnlyList<string> GetExportedNames(HashSet<ModuleRecord>? exportStarSet = null);

    // 16.2.1.4.2 ResolveExport ( exportName [ , resolveSet ] ). Returns either the
    // ResolvedBinding that the named export points to, an ambiguous marker, or null
    // when the export is unresolved.
    public abstract ResolvedBinding? ResolveExport(string exportName, HashSet<(ModuleRecord, string)>? resolveSet = null);

    // 16.2.1.5.1 / 16.2.1.5.2 Link / Evaluate driver methods. Subclasses provide the
    // real implementations; the base exposes status-transition helpers below.
    public abstract void Link();
    public abstract JsValue Evaluate();

    // Helper subclasses use to advance Status with explicit invariants. Centralized so
    // every state transition stays auditable.
    protected void TransitionStatus(ModuleStatus to)
    {
        Status = to;
    }

    protected void RecordEvaluationError(JsValue error)
    {
        EvaluationError = error;
        Status = ModuleStatus.Evaluated;
    }
}

// ECMA-262 9.1.1.5 / 16.2.1.2 ResolvedBinding records. The pair returned by
// ResolveExport identifies which Module's Environment owns the binding and under what
// local name. The sentinel `*namespace*` import-name is preserved to signal a
// namespace import target.
public readonly record struct ResolvedBinding(ModuleRecord Module, string BindingName)
{
    public const string NamespaceBindingName = "*namespace*";

    public bool IsNamespaceBinding => string.Equals(BindingName, NamespaceBindingName, StringComparison.Ordinal);
}
