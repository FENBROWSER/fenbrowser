using FenBrowser.Js.Ast;
using FenBrowser.Js.Bytecode;
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
// install the importer's import bindings as globals on the shared interpreter
// before compiling and running the importer's body.
//
// Simplifications relative to the full spec:
//   * One realm / one interpreter / one global object - imports are installed
//     directly into the global env rather than into a per-module
//     ModuleEnvironmentRecord. This is observably correct for the common cases
//     (no cross-realm imports) and avoids the deeper env-record surgery.
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

    public ModuleEvaluator(BytecodeInterpreter interpreter)
    {
        ArgumentNullException.ThrowIfNull(interpreter);
        _interpreter = interpreter;
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
    {
        if (_evaluated.TryGetValue(specifier, out var cached))
        {
            return cached.Exports;
        }

        if (!_sources.TryGetValue(specifier, out var source))
        {
            throw new InvalidOperationException(
                $"Module '{specifier}' has no registered source; call RegisterSource first.");
        }

        // Mark in-progress before recursion so a cyclic import sees us as
        // already-being-evaluated rather than triggering infinite recursion.
        var inProgress = new EvaluatedModule();
        _evaluated[specifier] = inProgress;

        var program = JsParser.ParseModule(new SourceText(source));

        // Phase 1 - resolve every import recursively. Each imported binding is
        // installed as a global on the interpreter so the module body can read
        // it by its local name.
        foreach (var stmt in program.Body)
        {
            if (stmt is ImportDeclarationNode import)
            {
                var targetExports = Evaluate(import.ModuleRequest);
                foreach (var entry in import.Entries)
                {
                    JsValue value;
                    if (entry.IsNamespaceImport)
                    {
                        value = BuildNamespaceObject(targetExports);
                    }
                    else if (entry.IsDefaultImport)
                    {
                        value = targetExports.TryGetValue("default", out var v) ? v : JsValue.Undefined;
                    }
                    else
                    {
                        value = targetExports.TryGetValue(entry.ImportName, out var v) ? v : JsValue.Undefined;
                    }
                    _interpreter.RegisterGlobalValue(entry.LocalName, value);
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
                var sourceExports = Evaluate(entry.ModuleRequest!);

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
                    inProgress.Exports[entry.ExportName!] = BuildNamespaceObject(sourceExports);
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
        var exportTargets = new List<(string ExportName, string LocalName)>();
        const string DefaultLocalAlias = "__fenjs_default__";

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
                    exportTargets.Add(("default", DefaultLocalAlias));
                    break;
                }
                case ExportDeclarationNode export:
                    if (export.LocalDeclaration is not null)
                    {
                        rewritten.Add(export.LocalDeclaration);
                    }
                    foreach (var entry in export.Entries)
                    {
                        if (entry.LocalName is { } local && entry.ExportName is { } exported)
                        {
                            exportTargets.Add((exported, local));
                        }
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
        _ = _interpreter.Execute(fn);

        // Phase 3 - harvest export values from the interpreter's globals.
        foreach (var (exportName, localName) in exportTargets)
        {
            inProgress.Exports[exportName] = _interpreter.TryReadGlobalValue(localName, out var v) ? v : JsValue.Undefined;
        }

        return inProgress.Exports;
    }

    private JsValue BuildNamespaceObject(IReadOnlyDictionary<string, JsValue> exports)
    {
        // The minimum namespace object: a plain object whose own enumerable
        // properties are the exports. The full spec's exotic namespace object
        // with frozen properties and Symbol.toStringTag is a follow-up.
        return _interpreter.AllocateNamespaceObject(exports);
    }

    private sealed class EvaluatedModule
    {
        public Dictionary<string, JsValue> Exports { get; } = new(StringComparer.Ordinal);
    }
}
