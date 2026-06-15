using FenBrowser.Js.Ast;
using FenBrowser.Js.Modules;

namespace FenBrowser.Js.Parser;

// ECMA-262 16.2.1.7.1 ParseModule static-semantics early errors for module
// declarations. Run at parse time so test262 negative `phase: parse` tests
// observe a SyntaxError.
//
// Checks performed:
//   1. The ExportedNames of ModuleItemList has no duplicate entries.
//   2. Every LocalName in LocalExportEntries resolves to a declared name
//      (var, let, const, function, class) in the module body.
//   3. The BoundNames of all ImportDeclarations contains no duplicate entries
//      across the entire module.
internal static class ModuleDeclarationChecker
{
    public static void Check(ProgramNode program)
    {
        if (program.Kind != ProgramKind.Module)
        {
            return;
        }

        var exportedNames = new HashSet<string>(System.StringComparer.Ordinal);
        var importedLocalNames = new HashSet<string>(System.StringComparer.Ordinal);
        var declaredNames = new HashSet<string>(System.StringComparer.Ordinal);

        // Pass 1: collect declared names from non-import, non-export declarations
        // and check duplicate import local names.
        CollectDeclaredNames(program.Body, declaredNames);
        CheckImportDuplicates(program.Body, importedLocalNames);
        CheckExportDuplicatesAndBindings(program.Body, exportedNames, importedLocalNames, declaredNames);

        // ECMA-262 16.2.1.7.1: ContainsDuplicateLabels of ModuleItemList with « » must be false.
        CheckDuplicateLabels(program.Body, new HashSet<string>(System.StringComparer.Ordinal));
    }

    // ECMA-262 ContainsDuplicateLabels for ModuleItemList
    private static void CheckDuplicateLabels(IReadOnlyList<StatementNode> statements, HashSet<string> labelSet)
    {
        foreach (var stmt in statements)
        {
            CheckStatementDuplicateLabels(stmt, labelSet);
        }
    }

    private static void CheckStatementDuplicateLabels(StatementNode stmt, HashSet<string> labelSet)
    {
        switch (stmt)
        {
            case LabeledStatementNode labeled:
                if (!labelSet.Add(labeled.Label))
                    throw new JsParserException($"Duplicate label '{labeled.Label}'.");
                CheckStatementDuplicateLabels(labeled.Body, labelSet);
                labelSet.Remove(labeled.Label);
                break;
            case BlockStatementNode block:
                foreach (var s in block.Statements)
                    CheckStatementDuplicateLabels(s, labelSet);
                break;
            case IfStatementNode ifStmt:
                CheckStatementDuplicateLabels(ifStmt.Consequent, labelSet);
                if (ifStmt.Alternate is { } elseStmt)
                    CheckStatementDuplicateLabels(elseStmt, labelSet);
                break;
            case ForStatementNode forStmt:
                if (forStmt.Body is { } body)
                    CheckStatementDuplicateLabels(body, labelSet);
                break;
            case ForInStatementNode forIn:
                CheckStatementDuplicateLabels(forIn.Body, labelSet);
                break;
            case ForOfStatementNode forOf:
                CheckStatementDuplicateLabels(forOf.Body, labelSet);
                break;
            case WhileStatementNode whileStmt:
                CheckStatementDuplicateLabels(whileStmt.Body, labelSet);
                break;
            case DoWhileStatementNode doWhile:
                CheckStatementDuplicateLabels(doWhile.Body, labelSet);
                break;
            case SwitchStatementNode switchStmt:
                foreach (var c in switchStmt.Cases)
                    foreach (var s in c.Consequent)
                        CheckStatementDuplicateLabels(s, labelSet);
                break;
            case TryCatchStatementNode tryCatch:
                CheckStatementDuplicateLabels(tryCatch.TryBlock, labelSet);
                if (tryCatch.CatchBlock is { } cb)
                    CheckStatementDuplicateLabels(cb, labelSet);
                break;
            case TryFinallyStatementNode tryFinally:
                CheckStatementDuplicateLabels(tryFinally.TryBlock, labelSet);
                if (tryFinally.FinallyBlock is { } fb)
                    CheckStatementDuplicateLabels(fb, labelSet);
                break;
            case TryCatchFinallyStatementNode tryCF:
                CheckStatementDuplicateLabels(tryCF.TryBlock, labelSet);
                if (tryCF.CatchBlock is { } cb2)
                    CheckStatementDuplicateLabels(cb2, labelSet);
                if (tryCF.FinallyBlock is { } fb2)
                    CheckStatementDuplicateLabels(fb2, labelSet);
                break;
            case WithStatementNode withStmt:
                CheckStatementDuplicateLabels(withStmt.Body, labelSet);
                break;
        }
    }

    private static void CollectDeclaredNames(IReadOnlyList<StatementNode> statements, HashSet<string> declared)
    {
        foreach (var stmt in statements)
        {
            switch (stmt)
            {
                case VariableDeclarationStatementNode varDecl:
                    CollectVarDeclaredNames(varDecl, declared);
                    break;
                case FunctionDeclarationNode fn when fn.Name is { Length: > 0 }:
                    AddDeclaredName(declared, fn.Name);
                    break;
                case ClassDeclarationNode cls when cls.Name is { Length: > 0 }:
                    AddDeclaredName(declared, cls.Name);
                    break;
                // Export declarations may wrap an inner declaration whose names
                // count as declared. Re-export forms and `export default` have
                // no inner declaration and are skipped here.
                case ExportDeclarationNode export when export.LocalDeclaration is { } inner:
                    CollectDeclaredNamesFromInner(inner, declared);
                    break;
                case ImportDeclarationNode:
                    break;
            }
        }
    }

    private static void AddDeclaredName(HashSet<string> declared, string name)
    {
        // Skip synthetic bindings (e.g. __pattern0 from destructuring, *default* from export default)
        if (name.StartsWith("__", StringComparison.Ordinal) || name.StartsWith("*", StringComparison.Ordinal))
            return;
        if (!declared.Add(name))
        {
            throw new JsParserException(
                $"Identifier '{name}' has already been declared. (duplicate lexical declaration in module)");
        }
    }

    private static bool IsSyntheticPatternBinding(string name) =>
        name.StartsWith("__", StringComparison.Ordinal) ||
        name.StartsWith("*", StringComparison.Ordinal);

    private static void CollectDeclaredNamesFromInner(StatementNode stmt, HashSet<string> declared)
    {
        switch (stmt)
        {
            case VariableDeclarationStatementNode varDecl:
                CollectVarDeclaredNames(varDecl, declared);
                break;
            case FunctionDeclarationNode fn when fn.Name is { Length: > 0 }:
                AddDeclaredName(declared, fn.Name);
                break;
            case ClassDeclarationNode cls when cls.Name is { Length: > 0 }:
                AddDeclaredName(declared, cls.Name);
                break;
        }
    }

    private static void CollectVarDeclaredNames(VariableDeclarationStatementNode decl, HashSet<string> declared)
    {
        foreach (var d in decl.Declarators)
        {
            foreach (var name in GetDeclaratorBoundNames(d))
            {
                AddDeclaredName(declared, name);
            }
        }
    }

    private static void CheckImportDuplicates(IReadOnlyList<StatementNode> statements, HashSet<string> imported)
    {
        foreach (var stmt in statements)
        {
            if (stmt is not ImportDeclarationNode import) continue;
            foreach (var entry in import.Entries)
            {
                if (!imported.Add(entry.LocalName))
                {
                    throw new JsParserException(
                        $"Identifier '{entry.LocalName}' has already been declared. (duplicate import binding)");
                }
            }
        }
    }

    private static void CheckExportDuplicatesAndBindings(
        IReadOnlyList<StatementNode> statements,
        HashSet<string> exported,
        HashSet<string> imported,
        HashSet<string> declared)
    {
        foreach (var stmt in statements)
        {
            if (stmt is not ExportDeclarationNode export) continue;

            foreach (var entry in export.Entries)
            {
                // Star re-exports (export * from) have null ExportName — skip.
                if (entry.ExportName is not { } exportName)
                {
                    continue;
                }

                // Check for duplicate export names (16.2.1.7.1 step 9).
                if (!exported.Add(exportName))
                {
                    throw new JsParserException(
                        $"Duplicate export of '{exportName}'.");
                }

                // For local exports (no ModuleRequest), verify the binding
                // references a declared name (16.2.1.7.1 step 10).
                // Skip synthetic pattern names (destructuring patterns get
                // individual names checked separately).
                if (entry.IsLocalExport && entry.LocalName is { } localName
                    && !IsSyntheticPatternBinding(localName))
                {
                    if (!declared.Contains(localName) && !imported.Contains(localName))
                    {
                        throw new JsParserException(
                            $"Export '{exportName}' references undeclared binding '{localName}'.");
                    }
                }
            }
        }
    }

    private static IEnumerable<string> GetDeclaratorBoundNames(VariableDeclaratorNode declarator)
    {
        if (declarator.BindingPattern is not null)
        {
            foreach (var name in CollectBindingPatternNames(declarator.BindingPattern))
            {
                yield return name;
            }
        }
        else if (declarator.Identifier is { Length: > 0 })
        {
            yield return declarator.Identifier;
        }
    }

    private static IEnumerable<string> CollectBindingPatternNames(BindingPatternNode pattern)
    {
        switch (pattern)
        {
            case IdentifierBindingPatternNode id:
                if (id.Name is { Length: > 0 })
                {
                    yield return id.Name;
                }
                break;
            case ArrayBindingPatternNode arr:
                foreach (var el in arr.Elements)
                {
                    if (el.Target is { } target)
                    {
                        foreach (var name in CollectBindingPatternNames(target))
                        {
                            yield return name;
                        }
                    }
                }
                break;
            case ObjectBindingPatternNode obj:
                foreach (var prop in obj.Properties)
                {
                    foreach (var name in CollectBindingPatternNames(prop.Target))
                    {
                        yield return name;
                    }
                }
                if (obj.Rest is { } rest)
                {
                    foreach (var name in CollectBindingPatternNames(rest))
                    {
                        yield return name;
                    }
                }
                break;
        }
    }
}
