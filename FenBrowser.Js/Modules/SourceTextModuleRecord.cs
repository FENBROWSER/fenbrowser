using FenBrowser.Js.Environments;
using FenBrowser.Js.Runtime;

namespace FenBrowser.Js.Modules;

// ECMA-262 16.2.1.6 Source Text Module Records.
//
// Slots beyond the abstract base:
//   [[ECMAScriptCode]]        - opaque parsed-AST handle (the parser owns the
//                               concrete representation; this layer just stores a
//                               reference so tests can verify roundtrip).
//   [[ImportEntries]]         - list of ImportEntry records collected during parse.
//   [[LocalExportEntries]]    - exports whose binding lives in this module.
//   [[IndirectExportEntries]] - exports re-exported under a specific name from another
//                               module ("export { x } from 'm'" or "export * as ns
//                               from 'm'").
//   [[StarExportEntries]]     - "export * from 'm'" re-exports.
//   ResolveModuleRequest      - callback used to find a dependency module by specifier.
public sealed class SourceTextModuleRecord : ModuleRecord
{
    private readonly Func<string, ModuleRecord?> _resolveModuleRequest;

    public SourceTextModuleRecord(
        int realmId,
        object? parsedCode,
        IReadOnlyList<ImportEntry> importEntries,
        IReadOnlyList<ExportEntry> localExportEntries,
        IReadOnlyList<ExportEntry> indirectExportEntries,
        IReadOnlyList<ExportEntry> starExportEntries,
        Func<string, ModuleRecord?>? resolveModuleRequest = null)
        : base(realmId)
    {
        ParsedCode = parsedCode;
        ImportEntries = importEntries ?? Array.Empty<ImportEntry>();
        LocalExportEntries = localExportEntries ?? Array.Empty<ExportEntry>();
        IndirectExportEntries = indirectExportEntries ?? Array.Empty<ExportEntry>();
        StarExportEntries = starExportEntries ?? Array.Empty<ExportEntry>();
        _resolveModuleRequest = resolveModuleRequest ?? (_ => null);
    }

    public object? ParsedCode { get; }
    public IReadOnlyList<ImportEntry> ImportEntries { get; }
    public IReadOnlyList<ExportEntry> LocalExportEntries { get; }
    public IReadOnlyList<ExportEntry> IndirectExportEntries { get; }
    public IReadOnlyList<ExportEntry> StarExportEntries { get; }

    // 16.2.1.6.1 GetExportedNames(exportStarSet).
    //
    // exportStarSet detects cycles among `export * from "..."` chains - per spec, if
    // this module is already in the set we return an empty list to break the cycle.
    public override IReadOnlyList<string> GetExportedNames(HashSet<ModuleRecord>? exportStarSet = null)
    {
        exportStarSet ??= new HashSet<ModuleRecord>();
        if (!exportStarSet.Add(this))
        {
            return Array.Empty<string>();
        }

        var exportedNames = new List<string>();

        foreach (var entry in LocalExportEntries)
        {
            if (entry.ExportName is { } name)
            {
                exportedNames.Add(name);
            }
        }

        foreach (var entry in IndirectExportEntries)
        {
            if (entry.ExportName is { } name)
            {
                exportedNames.Add(name);
            }
        }

        foreach (var entry in StarExportEntries)
        {
            if (entry.ModuleRequest is not { } request)
            {
                continue;
            }

            var dependency = _resolveModuleRequest(request);
            if (dependency is null)
            {
                continue;
            }

            foreach (var name in dependency.GetExportedNames(exportStarSet))
            {
                // "default" is not re-exported by `export * from "...";` per
                // 16.2.1.6.1 step 7.b.iii.
                if (string.Equals(name, "default", StringComparison.Ordinal))
                {
                    continue;
                }

                if (!exportedNames.Contains(name))
                {
                    exportedNames.Add(name);
                }
            }
        }

        return exportedNames;
    }

    // 16.2.1.6.2 ResolveExport(exportName, resolveSet).
    //
    // resolveSet detects cycles among indirect/star re-exports. The return type is
    // nullable: null means the name is unresolved (the caller raises SyntaxError);
    // an ambiguous match is represented by the static `Ambiguous` sentinel below.
    public override ResolvedBinding? ResolveExport(string exportName, HashSet<(ModuleRecord, string)>? resolveSet = null)
    {
        ArgumentNullException.ThrowIfNull(exportName);

        resolveSet ??= new HashSet<(ModuleRecord, string)>();
        if (!resolveSet.Add((this, exportName)))
        {
            return null;
        }

        foreach (var entry in LocalExportEntries)
        {
            if (string.Equals(entry.ExportName, exportName, StringComparison.Ordinal))
            {
                return new ResolvedBinding(this, entry.LocalName ?? exportName);
            }
        }

        foreach (var entry in IndirectExportEntries)
        {
            if (!string.Equals(entry.ExportName, exportName, StringComparison.Ordinal))
            {
                continue;
            }

            if (entry.ModuleRequest is not { } request)
            {
                continue;
            }

            var dependency = _resolveModuleRequest(request);
            if (dependency is null)
            {
                return null;
            }

            if (string.Equals(entry.ImportName, ExportEntry.AllButDefaultExports, StringComparison.Ordinal))
            {
                return new ResolvedBinding(dependency, ResolvedBinding.NamespaceBindingName);
            }

            return dependency.ResolveExport(entry.ImportName ?? exportName, resolveSet);
        }

        if (string.Equals(exportName, "default", StringComparison.Ordinal))
        {
            // 16.2.1.6.2 step 5: default is never re-exported by star.
            return null;
        }

        ResolvedBinding? starResolution = null;
        foreach (var entry in StarExportEntries)
        {
            if (entry.ModuleRequest is not { } request)
            {
                continue;
            }

            var dependency = _resolveModuleRequest(request);
            if (dependency is null)
            {
                continue;
            }

            var resolution = dependency.ResolveExport(exportName, resolveSet);
            if (resolution is null)
            {
                continue;
            }

            if (starResolution is null)
            {
                starResolution = resolution;
                continue;
            }

            // Two distinct bindings -> ambiguous, signal with default-struct sentinel.
            if (starResolution.Value != resolution.Value)
            {
                return null;
            }
        }

        return starResolution;
    }

    // 16.2.1.5.1 Link. At this milestone the per-source linking algorithm (which
    // creates and pre-populates the module env, resolves imports through dependencies,
    // and runs InitializeEnvironment) is not yet wired - we model the status
    // transition so dependent modules can chain through Link/Linked. Once the
    // interpreter migration to env records lands (B.6.x), this method will perform
    // real InitializeEnvironment per the spec.
    public override void Link()
    {
        TransitionStatus(ModuleStatus.Linking);
        Environment ??= new ModuleEnvironmentRecord(outerEnv: null);
        TransitionStatus(ModuleStatus.Linked);
    }

    // 16.2.1.5.2 Evaluate. Same caveat as Link - the real top-level execution arrives
    // with interpreter integration. For now we transition Evaluating -> Evaluated and
    // return undefined so dependent code paths can be exercised.
    public override JsValue Evaluate()
    {
        TransitionStatus(ModuleStatus.Evaluating);
        TransitionStatus(ModuleStatus.Evaluated);
        return JsValue.Undefined;
    }
}
