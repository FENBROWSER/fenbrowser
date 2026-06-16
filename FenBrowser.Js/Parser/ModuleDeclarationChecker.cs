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

        // ECMA-262 15.2.1.1: It is a Syntax Error if ModuleItemList Contains super
        // or Contains NewTarget.
        CheckModuleTopLevelRestrictions(program.Body);
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

    // ECMA-262 15.2.1.1: It is a Syntax Error if ModuleItemList Contains super
    // or Contains NewTarget. Walk only top-level statement heads — don't descend
    // into nested functions or classes where super and new.target are valid.
    private static void CheckModuleTopLevelRestrictions(IReadOnlyList<StatementNode> statements)
    {
        foreach (var stmt in statements)
        {
            CheckStatementForModuleRestrictions(stmt);
        }
    }

    private static void CheckStatementForModuleRestrictions(StatementNode stmt)
    {
        switch (stmt)
        {
            case ExpressionStatementNode exprStmt:
                CheckExpressionForModuleRestrictions(exprStmt.Expression);
                break;
            case BlockStatementNode block:
                CheckModuleTopLevelRestrictions(block.Statements);
                break;
            case IfStatementNode ifStmt:
                CheckExpressionForModuleRestrictions(ifStmt.Test);
                CheckStatementForModuleRestrictions(ifStmt.Consequent);
                if (ifStmt.Alternate is { } elseStmt)
                    CheckStatementForModuleRestrictions(elseStmt);
                break;
            case ForStatementNode forStmt:
                if (forStmt.Initializer is ExpressionStatementNode initExpr)
                    CheckExpressionForModuleRestrictions(initExpr.Expression);
                if (forStmt.Test is { } test)
                    CheckExpressionForModuleRestrictions(test);
                if (forStmt.Update is { } update)
                    CheckExpressionForModuleRestrictions(update);
                if (forStmt.Body is { } body)
                    CheckStatementForModuleRestrictions(body);
                break;
            case ForInStatementNode forIn:
                CheckExpressionForModuleRestrictions(forIn.Iterable);
                CheckStatementForModuleRestrictions(forIn.Body);
                break;
            case ForOfStatementNode forOf:
                CheckExpressionForModuleRestrictions(forOf.Iterable);
                CheckStatementForModuleRestrictions(forOf.Body);
                break;
            case WhileStatementNode whileStmt:
                CheckExpressionForModuleRestrictions(whileStmt.Test);
                CheckStatementForModuleRestrictions(whileStmt.Body);
                break;
            case DoWhileStatementNode doWhile:
                CheckStatementForModuleRestrictions(doWhile.Body);
                CheckExpressionForModuleRestrictions(doWhile.Test);
                break;
            case SwitchStatementNode switchStmt:
                CheckExpressionForModuleRestrictions(switchStmt.Discriminant);
                foreach (var c in switchStmt.Cases)
                    foreach (var s in c.Consequent)
                        CheckStatementForModuleRestrictions(s);
                break;
            case ReturnStatementNode returnStmt:
                if (returnStmt.Argument is { } retExpr)
                    CheckExpressionForModuleRestrictions(retExpr);
                break;
            case ThrowStatementNode throwStmt:
                CheckExpressionForModuleRestrictions(throwStmt.Argument);
                break;
            case TryCatchStatementNode tryCatch:
                CheckModuleTopLevelRestrictions(new[] { tryCatch.TryBlock });
                if (tryCatch.CatchBlock is { } cb)
                    CheckStatementForModuleRestrictions(cb);
                break;
            case TryFinallyStatementNode tryFinally:
                CheckModuleTopLevelRestrictions(new[] { tryFinally.TryBlock });
                if (tryFinally.FinallyBlock is { } fb)
                    CheckStatementForModuleRestrictions(fb);
                break;
            case TryCatchFinallyStatementNode tryCF:
                CheckModuleTopLevelRestrictions(new[] { tryCF.TryBlock });
                if (tryCF.CatchBlock is { } cb2)
                    CheckStatementForModuleRestrictions(cb2);
                if (tryCF.FinallyBlock is { } fb2)
                    CheckStatementForModuleRestrictions(fb2);
                break;
            case WithStatementNode withStmt:
                CheckExpressionForModuleRestrictions(withStmt.Object);
                CheckStatementForModuleRestrictions(withStmt.Body);
                break;
            case LabeledStatementNode labeled:
                CheckStatementForModuleRestrictions(labeled.Body);
                break;
            // Don't descend into functions, classes, generators, async functions
            // — super and new.target are valid inside those.
            case FunctionDeclarationNode:
            case ClassDeclarationNode:
                break;
            case ExportDeclarationNode export:
                if (export.LocalDeclaration is { } inner)
                {
                    // Only check variable declarations within exports;
                    // function/class exports don't have super/new.target at top level.
                    if (inner is VariableDeclarationStatementNode varStmt)
                        CheckVarDeclForModuleRestrictions(varStmt);
                }
                break;
            case VariableDeclarationStatementNode varDecl:
                CheckVarDeclForModuleRestrictions(varDecl);
                break;
        }
    }

    private static void CheckVarDeclForModuleRestrictions(VariableDeclarationStatementNode decl)
    {
        foreach (var d in decl.Declarators)
        {
            if (d.Initializer is { } init)
                CheckExpressionForModuleRestrictions(init);
        }
    }

    private static void CheckExpressionForModuleRestrictions(ExpressionNode expr)
    {
        switch (expr)
        {
            case SuperExpressionNode:
                throw new JsParserException("'super' is not allowed at the top level of a module.");
            case NewTargetExpressionNode:
                throw new JsParserException("'new.target' is not allowed at the top level of a module.");
            // Recurse into compound expressions but skip function/class expressions
            // where super and new.target are valid.
            case BinaryExpressionNode bin:
                CheckExpressionForModuleRestrictions(bin.Left);
                CheckExpressionForModuleRestrictions(bin.Right);
                break;
            case UnaryExpressionNode unary:
                CheckExpressionForModuleRestrictions(unary.Operand);
                break;
            case ConditionalExpressionNode cond:
                CheckExpressionForModuleRestrictions(cond.Test);
                CheckExpressionForModuleRestrictions(cond.Consequent);
                CheckExpressionForModuleRestrictions(cond.Alternate);
                break;
            case AssignmentExpressionNode assign:
                CheckExpressionForModuleRestrictions(assign.Left);
                CheckExpressionForModuleRestrictions(assign.Right);
                break;
            case LogicalAssignmentExpressionNode logAssign:
                CheckExpressionForModuleRestrictions(logAssign.Target);
                CheckExpressionForModuleRestrictions(logAssign.Value);
                break;
            case CallExpressionNode call:
                CheckExpressionForModuleRestrictions(call.Callee);
                foreach (var arg in call.Arguments)
                    CheckExpressionForModuleRestrictions(arg);
                break;
            case NewExpressionNode newExpr:
                CheckExpressionForModuleRestrictions(newExpr.Callee);
                foreach (var arg in newExpr.Arguments)
                    CheckExpressionForModuleRestrictions(arg);
                break;
            case MemberExpressionNode member:
                CheckExpressionForModuleRestrictions(member.Object);
                break;
            case ArrayLiteralExpressionNode arr:
                foreach (var el in arr.Elements)
                    CheckExpressionForModuleRestrictions(el);
                break;
            case ObjectLiteralExpressionNode obj:
                foreach (var prop in obj.Properties)
                {
                    if (prop.IsComputed && prop.ComputedKey is { } ck)
                        CheckExpressionForModuleRestrictions(ck);
                    CheckExpressionForModuleRestrictions(prop.Value);
                }
                break;
            case SpreadElementExpressionNode spread:
                CheckExpressionForModuleRestrictions(spread.Argument);
                break;
            case TemplateLiteralExpressionNode tmpl:
                foreach (var exprPart in tmpl.Expressions)
                    CheckExpressionForModuleRestrictions(exprPart);
                break;
            case TaggedTemplateExpressionNode tagged:
                CheckExpressionForModuleRestrictions(tagged.Tag);
                break;
            case ParenthesizedExpressionNode paren:
                CheckExpressionForModuleRestrictions(paren.Expression);
                break;
            // Don't descend into function/class/arrow expressions
            case FunctionExpressionNode:
            case ArrowFunctionExpressionNode:
            case ClassExpressionNode:
                break;
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
