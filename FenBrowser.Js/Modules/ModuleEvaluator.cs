using FenBrowser.Js.Ast;
using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Environments;
using FenBrowser.Js.Heap;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Parser;
using FenBrowser.Js.Runtime;
using FenBrowser.Js.Source;

namespace FenBrowser.Js.Modules;

// E.6 Link/Evaluate driver.
//
// Direct named/default imports whose target export resolves to a local binding are
// represented as ModuleEnvironmentRecord import bindings, so reads stay live when
// the exporter mutates `let`/`var` state. Namespace imports and unresolved re-export
// chains still use the namespace/export table path until full ResolveExport linking
// is implemented.
public sealed class ModuleEvaluator : IHeapRootSource
{
    private readonly BytecodeInterpreter _interpreter;
    private readonly Dictionary<string, EvaluatedModule> _evaluated = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _sources = new(StringComparer.Ordinal);
    private readonly Func<string, string?>? _hostSourceResolver;
    private readonly Func<string, string?, string?>? _hostSpecifierResolver;

    public ModuleEvaluator(BytecodeInterpreter interpreter)
        : this(interpreter, hostSourceResolver: null)
    {
    }

    public ModuleEvaluator(BytecodeInterpreter interpreter, Func<string, string?>? hostSourceResolver)
        : this(interpreter, hostSourceResolver, hostSpecifierResolver: null)
    {
    }

    /// <summary>
    /// <paramref name="hostSpecifierResolver"/> is HTML's "resolve a module
    /// specifier" (ECMA-262 16.2.1.7 HostLoadImportedModule leaves the meaning of a
    /// specifier to the host): given a specifier and the referrer module's key it
    /// returns the module key to load, or null to fall back to plain URL
    /// resolution. A browser host answers bare specifiers such as "react" from the
    /// document's import map here.
    /// </summary>
    public ModuleEvaluator(
        BytecodeInterpreter interpreter,
        Func<string, string?>? hostSourceResolver,
        Func<string, string?, string?>? hostSpecifierResolver)
    {
        ArgumentNullException.ThrowIfNull(interpreter);
        _interpreter = interpreter;
        _hostSourceResolver = hostSourceResolver;
        _hostSpecifierResolver = hostSpecifierResolver;
        _interpreter.DynamicImportResolver = EvaluateDynamicImport;
        // The module map outlives every evaluation: a later import reads an
        // earlier module's environment and export values straight out of
        // _evaluated, which the collector cannot see on its own.
        _interpreter.Heap.AddRootSource(this);
    }

    /// <summary>
    /// The module map is a GC root: each evaluated module's environment (its
    /// live bindings) and harvested export values must survive every
    /// collection for as long as the map that hands them to later importers.
    /// </summary>
    public void TraceRoots(IHeapTracer tracer)
    {
        ArgumentNullException.ThrowIfNull(tracer);
        foreach (var module in _evaluated.Values)
        {
            module.Environment.Trace(tracer);
            foreach (var value in module.Exports.Values)
            {
                if (value.Tag == JsValueTag.Object)
                {
                    tracer.TraceRoot("module-export", value.AsObjectHandle());
                }
            }
            if (module.Namespace.Tag == JsValueTag.Object)
            {
                tracer.TraceRoot("module-namespace", module.Namespace.AsObjectHandle());
            }
        }
    }

    public void RegisterSource(string specifier, string source)
    {
        ArgumentNullException.ThrowIfNull(specifier);
        ArgumentNullException.ThrowIfNull(source);
        _sources[specifier] = source;
    }

    /// <summary>
    /// The static module requests of a module source, in source order and without
    /// duplicates: every `import ... from "x"` plus every `export ... from "x"`.
    /// Hosts use this to fetch a module graph ahead of evaluation (HTML "fetch the
    /// descendants of a module script"), which walks all requests of a module in
    /// parallel rather than blocking on each one inside Evaluate. Returns an empty
    /// list when the source does not parse; Evaluate reports the syntax error.
    /// </summary>
    public static IReadOnlyList<string> CollectModuleRequests(string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        ProgramNode program;
        try
        {
            program = JsParser.ParseModule(new SourceText(source));
        }
        catch (Exception)
        {
            return Array.Empty<string>();
        }

        var requests = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var stmt in program.Body)
        {
            switch (stmt)
            {
                case ImportDeclarationNode import:
                    if (seen.Add(import.ModuleRequest))
                        requests.Add(import.ModuleRequest);
                    break;
                case ExportDeclarationNode export:
                    foreach (var entry in export.Entries)
                    {
                        if (entry.ModuleRequest is { } request && seen.Add(request))
                            requests.Add(request);
                    }
                    break;
            }
        }
        return requests;
    }

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

            _sources[specifier] = source;
        }

        // Publish the environment before recursing so cycles can create indirect
        // bindings to this module even while its declarations are still in TDZ.
        var inProgress = new EvaluatedModule(_interpreter.CreateModuleEnvironment(specifier));
        _evaluated[specifier] = inProgress;

        var sourceText = new SourceText(source, specifier);
        var program = JsParser.ParseModule(sourceText);
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

        // Phase 1 - resolve imports recursively. Direct imports use the module
        // environment's indirect-binding primitive whenever the target export maps
        // to a real local binding. This preserves live-binding semantics instead of
        // snapshotting the current JsValue into the importer.
        foreach (var stmt in program.Body)
        {
            if (stmt is not ImportDeclarationNode import)
                continue;

            var targetExports = Evaluate(import.ModuleRequest, specifier);
            var targetSpecifier = ResolveModuleSpecifier(import.ModuleRequest, specifier);
            var targetModule = _evaluated[targetSpecifier];

            foreach (var entry in import.Entries)
            {
                if (entry.IsNamespaceImport)
                {
                    CreateInitializedImmutableImport(
                        inProgress.Environment,
                        entry.LocalName,
                        BuildNamespaceObject(targetModule));
                    continue;
                }

                var importName = entry.IsDefaultImport ? "default" : entry.ImportName;
                if (!string.IsNullOrEmpty(importName) &&
                    targetModule.ExportBindings.TryGetValue(importName, out var targetLocalName))
                {
                    var result = inProgress.Environment.CreateImportBinding(
                        entry.LocalName,
                        targetModule.Environment,
                        targetLocalName);
                    if (result != BindingOpResult.Ok)
                    {
                        throw new InvalidOperationException(
                            $"Could not create live import binding '{entry.LocalName}' -> " +
                            $"'{targetSpecifier}:{targetLocalName}': {result}.");
                    }
                    continue;
                }

                // Re-export-only targets do not yet carry a full ResolveExport graph.
                // Keep the existing immutable fallback for those uncommon paths rather
                // than claiming a live binding that points at no target environment slot.
                var snapshot = !string.IsNullOrEmpty(importName) &&
                               targetExports.TryGetValue(importName, out var exportedValue)
                    ? exportedValue
                    : JsValue.Undefined;
                CreateInitializedImmutableImport(inProgress.Environment, entry.LocalName, snapshot);
            }
        }

        // Phase 1b - resolve re-exports. These values are still copied into the export
        // table; a future full ResolveExport graph should turn named/star re-exports
        // into indirect bindings as well.
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
                    foreach (var kv in sourceExports)
                    {
                        if (string.Equals(kv.Key, "default", StringComparison.Ordinal)) continue;
                        inProgress.Exports[kv.Key] = kv.Value;
                    }
                }
                else if (entry.IsNamespaceReexport)
                {
                    inProgress.Exports[entry.ExportName!] = BuildNamespaceObject(sourceModule);
                }
                else
                {
                    var value = sourceExports.TryGetValue(entry.ImportName!, out var v) ? v : JsValue.Undefined;
                    inProgress.Exports[entry.ExportName!] = value;
                }
            }
        }

        // Phase 2 - rewrite executable module statements through the existing compiler.
        var rewritten = new List<StatementNode>(program.Body.Count);

        foreach (var stmt in program.Body)
        {
            switch (stmt)
            {
                case ImportDeclarationNode:
                    break;
                case ExportDeclarationNode export when export.DefaultExpression is not null:
                {
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
        var fn = new BytecodeCompiler().CompileModule(rewrittenProgram, sourceText);
        new BytecodeVerifier().Verify(fn);
        _ = _interpreter.ExecuteWithEnvironment(fn, inProgress.Environment);

        // Phase 3 - harvest local export values for legacy/fallback consumers. Module
        // namespace objects built from ExportBindings read the environment live.
        foreach (var (exportName, localName) in exportTargets)
        {
            inProgress.Exports[exportName] =
                _interpreter.TryReadBinding(inProgress.Environment, localName, out var v)
                    ? v
                    : JsValue.Undefined;
        }

        return inProgress.Exports;
    }

    private static void CreateInitializedImmutableImport(
        ModuleEnvironmentRecord environment,
        string localName,
        JsValue value)
    {
        var createResult = environment.CreateImmutableBinding(localName, strict: true);
        if (createResult != BindingOpResult.Ok)
        {
            throw new InvalidOperationException(
                $"Could not create import binding '{localName}': {createResult}.");
        }

        var initializeResult = environment.InitializeBinding(localName, value);
        if (initializeResult != BindingOpResult.Ok)
        {
            throw new InvalidOperationException(
                $"Could not initialize import binding '{localName}': {initializeResult}.");
        }
    }

    private JsValue EvaluateDynamicImport(string specifier, string? referrer)
    {
        var resolvedSpecifier = ResolveModuleSpecifier(specifier, referrer);
        _ = Evaluate(resolvedSpecifier, referrer);
        return BuildNamespaceObject(_evaluated[resolvedSpecifier]);
    }

    private string ResolveModuleSpecifier(string specifier, string? referrer)
    {
        if (_hostSpecifierResolver?.Invoke(specifier, referrer) is { } hostResolved)
        {
            return hostResolved;
        }

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
        return _interpreter.AllocateNamespaceObject(exports);
    }

    // ECMA-262 16.2.1.10 GetModuleNamespace: a module has one namespace object,
    // so `import * as a` and a later `import()` of the same module compare equal.
    private JsValue BuildNamespaceObject(EvaluatedModule module)
    {
        if (module.Namespace.Tag != JsValueTag.Object)
        {
            module.Namespace = module.ExportBindings.Count > 0
                ? _interpreter.AllocateModuleNamespaceObject(module.Environment, module.ExportBindings)
                : BuildNamespaceObject(module.Exports);
        }

        return module.Namespace;
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
        public JsValue Namespace = JsValue.Undefined;
    }
}
