using FenBrowser.Js.Ast;
using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Environments;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Parser;
using FenBrowser.Js.Runtime;
using FenBrowser.Js.Source;

namespace FenBrowser.Js.Modules;

// E.6 minimum viable Link/Evaluate driver.
//
// The spec splits module work into Link (resolve every ImportEntry against the
// target module's export table) and Evaluate (run the module body). This class
// collapses both into a single recursive Evaluate(specifier): when an importer
// names a module that hasn't been evaluated yet, we recursively evaluate the
// target first, capturing its exports into a per-specifier dictionary, then
// install the importer's bindings in its module environment before compiling
// and running the module body.
//
// Simplifications relative to the full spec:
//   * One realm / one interpreter / one global object. Each source module has
//     its own ModuleEnvironmentRecord chained to that realm's global environment.
//     Imported values are currently initialized snapshots rather than full live
//     indirect bindings.
//   * Cycle detection is via Status (Evaluating during recursion); a cyclic
//     import sees the partial exports of the in-progress module - matching the
//     spec's TDZ behaviour for unresolved imports.
//   * Supports only the most common forms today: `import x from "mod"` (default),
//     `import { a, b as c } from "mod"` (named), `export default expr`,
//     `export var/let/const x`, `export function f`, `export class C`.
//
// Used by ShellSmokeTests and any embedder that pre-registers module sources;
// E.6.next will add the recursive parse path that fetches via IHostModuleResolver.
public sealed class ModuleEvaluator
{
    private readonly BytecodeInterpreter _interpreter;
    private readonly Dictionary<string, EvaluatedModule> _evaluated = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _sources = new(StringComparer.Ordinal);
    private readonly Func<string, string?>? _hostSourceResolver;

    public ModuleEvaluator(BytecodeInterpreter interpreter)
        : this(interpreter, hostSourceResolver: null)
    {
    }

    // Hosts that fetch on demand pass a resolver delegate that maps a module
    // specifier to its source text (or null if the specifier is unknown). The
    // evaluator first checks RegisterSource entries; on a miss it calls the
    // resolver, which is intended to model HostLoadImportedModule
    // (ECMA-262 16.2.1.7) for hosts that own the fetch path.
    public ModuleEvaluator(BytecodeInterpreter interpreter, Func<string, string?>? hostSourceResolver)
    {
        ArgumentNullException.ThrowIfNull(interpreter);
        _interpreter = interpreter;
        _hostSourceResolver = hostSourceResolver;
        _interpreter.DynamicImportResolver = EvaluateDynamicImport;
    }

    // Pre-seed the source for a module specifier. Hosts that fetch over HTTP
    // would substitute this with an IHostModuleResolver call; tests register
    // sources inline.
    public void RegisterSource(string specifier, string source)
    {
        ArgumentNullException.ThrowIfNull(specifier);
        ArgumentNullException.ThrowIfNull(source);
        _sources[specifier] = source;
    }

    // Evaluate the module identified by `specifier`. Returns its exports dictionary.
    public IReadOnlyDictionary<string, JsValue> Evaluate(string specifier)
        => Evaluate(specifier, referrer: null);

    private IReadOnlyDictionary<string, JsValue> Evaluate(string specifier, string? referrer)
    {
        specifier = ResolveModuleSpecifier(specifier, referrer);

        if (_evaluated.TryGetValue(specifier, out var cached))
        {
            return cached.Exports;
        }

        if (!_sources.TryGetValue(specifier, out var source))
        {
            source = _hostSourceResolver?.Invoke(specifier);
            if (source is null)
            {
                throw new InvalidOperationException(
                    $"Module '{specifier}' has no registered source and the host resolver returned null.");
            }

            // Cache so subsequent re-imports of the same specifier don't refetch.
            _sources[specifier] = source;
        }

        // Mark in-progress before recursion so a cyclic import sees us as
        // already-being-evaluated rather than triggering infinite recursion.
        var inProgress = new EvaluatedModule(_interpreter.CreateModuleEnvironment(specifier));
        _evaluated[specifier] = inProgress;

        var program = JsParser.ParseModule(new SourceText(source));
        var exportTargets = new List<(string ExportName, string LocalName)>();
        const string DefaultLocalAlias = "__fenjs_default__";
        foreach (var stmt in program.Body)
        {
            if (stmt is not ExportDeclarationNode export)
            {
                continue;
            }

            if (export.DefaultExpression is not null)
            {
                exportTargets.Add(("default", DefaultLocalAlias));
                inProgress.ExportBindings["default"] = DefaultLocalAlias;
                continue;
            }

            foreach (var entry in export.Entries)
            {
                if (entry.LocalName is { } local && entry.ExportName is { } exported)
                {
                    exportTargets.Add((exported, local));
                    inProgress.ExportBindings[exported] = local;
                }
            }
        }

        // Phase 1 - resolve every import recursively. Each imported binding is
        // installed in this module's environment so the body can read it without
        // leaking or colliding with another module's top-level declarations.
        foreach (var stmt in program.Body)
        {
            if (stmt is ImportDeclarationNode import)
            {
                var targetExports = Evaluate(import.ModuleRequest, specifier);
                var targetSpecifier = ResolveModuleSpecifier(import.ModuleRequest, specifier);
                var targetModule = _evaluated[targetSpecifier];
                foreach (var entry in import.Entries)
                {
                    JsValue value;
                    if (entry.IsNamespaceImport)
                    {
                        value = BuildNamespaceObject(targetModule);
                    }
                    else if (entry.IsDefaultImport)
                    {
                        value = targetExports.TryGetValue("default", out var v) ? v : JsValue.Undefined;
                    }
                    else
                    {
                        value = targetExports.TryGetValue(entry.ImportName, out var v) ? v : JsValue.Undefined;
                    }
                    var createResult = inProgress.Environment.CreateImmutableBinding(entry.LocalName, strict: true);
                    if (createResult != BindingOpResult.Ok)
                    {
                        throw new InvalidOperationException(
                            $"Could not create import binding '{entry.LocalName}': {createResult}.");
                    }

                    var initializeResult = inProgress.Environment.InitializeBinding(entry.LocalName, value);
                    if (initializeResult != BindingOpResult.Ok)
                    {
                        throw new InvalidOperationException(
                            $"Could not initialize import binding '{entry.LocalName}': {initializeResult}.");
                    }
                }
            }
        }

        // Phase 1b - resolve re-exports. ECMA-262 16.2.3.7 ExportEntries with a
        // non-null ModuleRequest are re-exports; we evaluate the target module
        // (recursive call which hits the cache when already evaluated) and copy
        // its exports into our own table per the entry's flavour.
        foreach (var stmt in program.Body)
        {
            if (stmt is not ExportDeclarationNode export) continue;
            foreach (var entry in export.Entries)
            {
                if (!entry.IsReexport) continue;
                var sourceExports = Evaluate(entry.ModuleRequest!, specifier);
                var sourceSpecifier = ResolveModuleSpecifier(entry.ModuleRequest!, specifier);
                var sourceModule = _evaluated[sourceSpecifier];

                if (entry.IsStarReexport)
                {
                    // export * from 'mod' - re-publish every named export of the
                    // source EXCEPT "default" per 16.2.3.7 step 7.b.iii.
                    foreach (var kv in sourceExports)
                    {
                        if (string.Equals(kv.Key, "default", StringComparison.Ordinal)) continue;
                        inProgress.Exports[kv.Key] = kv.Value;
                    }
                }
                else if (entry.IsNamespaceReexport)
                {
                    // export * as ns from 'mod' - publish a namespace object
                    // whose name is the export name, exposing all source exports.
                    inProgress.Exports[entry.ExportName!] = BuildNamespaceObject(sourceModule);
                }
                else
                {
                    // export { a as b } from 'mod' - take source.[importName]
                    // and publish under exportName.
                    var value = sourceExports.TryGetValue(entry.ImportName!, out var v) ? v : JsValue.Undefined;
                    inProgress.Exports[entry.ExportName!] = value;
                }
            }
        }

        // Phase 2 - rewrite the module body so the executable forms compile
        // through the existing pipeline. Each ExportDeclarationNode becomes its
        // inner declaration (or `var __default = expr;` for export default).
        var rewritten = new List<StatementNode>(program.Body.Count);

        foreach (var stmt in program.Body)
        {
            switch (stmt)
            {
                case ImportDeclarationNode:
                    // Already handled above; skip from the executable body.
                    break;
                case ExportDeclarationNode export when export.DefaultExpression is not null:
                {
                    // Synthesize `var __fenjs_default__ = <expr>;` then mark the
                    // alias for export under the name "default".
                    var declarator = new VariableDeclaratorNode(DefaultLocalAlias, export.DefaultExpression, export.Span);
                    var decl = new VariableDeclarationStatementNode("var",
                        new[] { declarator }, export.Span);
                    rewritten.Add(decl);
                    break;
                }
                case ExportDeclarationNode export:
                    if (export.LocalDeclaration is not null)
                    {
                        rewritten.Add(export.LocalDeclaration);
                    }
                    break;
                default:
                    rewritten.Add(stmt);
                    break;
            }
        }

        var rewrittenProgram = new ProgramNode(ProgramKind.Module, rewritten, program.Span);
        var fn = new BytecodeCompiler().CompileProgram(rewrittenProgram);
        new BytecodeVerifier().Verify(fn);
        _ = _interpreter.ExecuteWithEnvironment(fn, inProgress.Environment);

        // Phase 3 - harvest export values from this module's environment.
        foreach (var (exportName, localName) in exportTargets)
        {
            inProgress.Exports[exportName] =
                _interpreter.TryReadBinding(inProgress.Environment, localName, out var v)
                    ? v
                    : JsValue.Undefined;
        }

        return inProgress.Exports;
    }

    private JsValue EvaluateDynamicImport(string specifier, string? referrer)
    {
        var resolvedSpecifier = ResolveModuleSpecifier(specifier, referrer);
        _ = Evaluate(resolvedSpecifier, referrer);
        return BuildNamespaceObject(_evaluated[resolvedSpecifier]);
    }

    private static string ResolveModuleSpecifier(string specifier, string? referrer)
    {
        if (string.IsNullOrWhiteSpace(referrer) ||
            !Uri.TryCreate(referrer, UriKind.Absolute, out var referrerUri) ||
            !Uri.TryCreate(referrerUri, specifier, out var resolvedUri))
        {
            return specifier;
        }

        return resolvedUri.AbsoluteUri;
    }

    private JsValue BuildNamespaceObject(IReadOnlyDictionary<string, JsValue> exports)
    {
        // The minimum namespace object: a plain object whose own enumerable
        // properties are the exports. The full spec's exotic namespace object
        // with frozen properties and Symbol.toStringTag is a follow-up.
        return _interpreter.AllocateNamespaceObject(exports);
    }

    private JsValue BuildNamespaceObject(EvaluatedModule module)
    {
        return module.ExportBindings.Count > 0
            ? _interpreter.AllocateModuleNamespaceObject(module.Environment, module.ExportBindings)
            : BuildNamespaceObject(module.Exports);
    }

    private sealed class EvaluatedModule
    {
        public EvaluatedModule(ModuleEnvironmentRecord environment)
        {
            Environment = environment;
        }

        public ModuleEnvironmentRecord Environment { get; }
        public Dictionary<string, JsValue> Exports { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, string> ExportBindings { get; } = new(StringComparer.Ordinal);
    }
}
