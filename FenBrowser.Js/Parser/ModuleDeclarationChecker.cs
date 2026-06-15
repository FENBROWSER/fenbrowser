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
                    declared.Add(fn.Name);
                    break;
                case ClassDeclarationNode cls when cls.Name is { Length: > 0 }:
                    declared.Add(cls.Name);
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

    private static void CollectDeclaredNamesFromInner(StatementNode stmt, HashSet<string> declared)
    {
        switch (stmt)
        {
            case VariableDeclarationStatementNode varDecl:
                CollectVarDeclaredNames(varDecl, declared);
                break;
            case FunctionDeclarationNode fn when fn.Name is { Length: > 0 }:
                declared.Add(fn.Name);
                break;
            case ClassDeclarationNode cls when cls.Name is { Length: > 0 }:
                declared.Add(cls.Name);
                break;
        }
    }

    private static void CollectVarDeclaredNames(VariableDeclarationStatementNode decl, HashSet<string> declared)
    {
        foreach (var d in decl.Declarators)
        {
            foreach (var name in GetDeclaratorBoundNames(d))
            {
                declared.Add(name);
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
                if (entry.IsLocalExport && entry.LocalName is { } localName)
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
