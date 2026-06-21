using System.Collections.Generic;
using FenBrowser.Js.Ast;

namespace FenBrowser.Js.Parser;

// ECMA-262 static semantics "early errors" for lexical declarations, run at parse
// time (so test262 negative `phase: parse` tests observe a SyntaxError):
//
//   - Block / Script / FunctionBody / CaseBlock: It is a Syntax Error if the
//     LexicallyDeclaredNames contains any duplicate entries (14.2.1, 16.1.1, ...).
//   - ... if any element of the LexicallyDeclaredNames also occurs in the
//     VarDeclaredNames.
//
// Deliberately CONSERVATIVE to avoid false positives that would reject valid code:
// only a let/const/class binding is treated as the "lexical" name that must be
// unique. A clash is reported when such a name collides with another let/const/class,
// with a function declaration, or with a var anywhere in the same var-scope. We do NOT
// flag function-vs-function or function/var/var-vs-var clashes — Annex B 3.3 web-compat
// semantics make several of those legal in sloppy mode, and mis-flagging them would
// break working programs.
internal static class LexicalDeclarationChecker
{
    public static void Check(ProgramNode program, bool strictMode)
    {
        _strictMode = strictMode;
        CheckScope(program.Body);
    }

    private static bool _strictMode;

    // Validate one lexical scope (the statement list of a Script/FunctionBody/Block/
    // CaseBlock), then recurse into the nested scopes it contains.
    //
    // `treatFunctionsAsLexical`: a plain Block relaxes the duplicate/var early errors
    // for FunctionDeclarations (Annex B.3.2.4 web-compat: `{ function f(){} function f(){} }`
    // and `{ function f(){} var f; }` are legal in sloppy code). A switch CaseBlock has no
    // such Annex B relaxation, so its function names are full LexicallyDeclaredNames and
    // participate in every early error. Callers pass true for switch CaseBlocks.
    private static void CheckScope(IReadOnlyList<StatementNode> statements, bool treatFunctionsAsLexical = false)
    {
        var lexNames = new HashSet<string>(System.StringComparer.Ordinal);
        var funcNames = new HashSet<string>(System.StringComparer.Ordinal);

        // Top-level declarations of this scope.
        foreach (var stmt in statements)
        {
            switch (Unwrap(stmt))
            {
                case VariableDeclarationStatementNode v when v.Kind != "var":
                    foreach (var name in DeclaredNames(v))
                    {
                        if (!lexNames.Add(name))
                            throw new JsParserException($"Identifier '{name}' has already been declared.");
                    }
                    break;
                case ClassDeclarationNode c when c.Name is { Length: > 0 }:
                    if (!lexNames.Add(c.Name))
                        throw new JsParserException($"Identifier '{c.Name}' has already been declared.");
                    break;
                case FunctionDeclarationNode f when f.Name is { Length: > 0 }:
                    if (treatFunctionsAsLexical)
                    {
                        if (!lexNames.Add(f.Name))
                            throw new JsParserException($"Identifier '{f.Name}' has already been declared.");
                    }
                    else
                    {
                        funcNames.Add(f.Name);
                    }
                    break;
            }
        }

        if (lexNames.Count > 0)
        {
            // A lexical name may not also be a function declaration in this scope.
            foreach (var fn in funcNames)
            {
                if (lexNames.Contains(fn))
                    throw new JsParserException($"Identifier '{fn}' has already been declared.");
            }

            // ... nor a var declared anywhere within this scope's var-region (var
            // hoists through nested non-function blocks).
            var varNames = new HashSet<string>(System.StringComparer.Ordinal);
            foreach (var stmt in statements)
            {
                CollectVarNames(stmt, varNames);
            }
            foreach (var lex in lexNames)
            {
                if (varNames.Contains(lex))
                    throw new JsParserException($"Identifier '{lex}' has already been declared.");
            }
        }

        // Recurse into nested scopes (their own lexical levels).
        foreach (var stmt in statements)
        {
            RecurseNested(stmt);
        }
    }

    // `label: stmt` does not introduce a scope; see through it to the labelled body.
    private static StatementNode Unwrap(StatementNode stmt)
        => stmt is LabeledStatementNode labeled ? Unwrap(labeled.Body) : stmt;

    private static IEnumerable<string> DeclaredNames(VariableDeclarationStatementNode decl)
    {
        var names = new List<string>();
        foreach (var d in decl.Declarators)
        {
            if (d.BindingPattern is { } pattern)
                CollectPatternNames(pattern, names);
            else if (d.Identifier is { Length: > 0 })
                names.Add(d.Identifier);
        }
        return names;
    }

    private static void CollectPatternNames(BindingPatternNode pattern, List<string> names)
    {
        switch (pattern)
        {
            case IdentifierBindingPatternNode id:
                names.Add(id.Name);
                break;
            case ArrayBindingPatternNode arr:
                foreach (var el in arr.Elements)
                    if (el.Target is { } t) CollectPatternNames(t, names);
                break;
            case ObjectBindingPatternNode obj:
                foreach (var p in obj.Properties)
                    CollectPatternNames(p.Target, names);
                if (obj.Rest is { } rest) CollectPatternNames(rest, names);
                break;
        }
    }

    // Collect `var` names that hoist into this var-scope: descend through every
    // nested statement EXCEPT function/class bodies (which begin their own var-scope).
    private static void CollectVarNames(StatementNode stmt, HashSet<string> outNames)
    {
        switch (stmt)
        {
            case VariableDeclarationStatementNode v when v.Kind == "var":
                foreach (var name in DeclaredNames(v)) outNames.Add(name);
                break;
            case BlockStatementNode block:
                foreach (var s in block.Statements) CollectVarNames(s, outNames);
                break;
            case IfStatementNode ifs:
                CollectVarNames(ifs.Consequent, outNames);
                if (ifs.Alternate is { } alt) CollectVarNames(alt, outNames);
                break;
            case WhileStatementNode w:
                CollectVarNames(w.Body, outNames);
                break;
            case DoWhileStatementNode dw:
                CollectVarNames(dw.Body, outNames);
                break;
            case WithStatementNode with:
                CollectVarNames(with.Body, outNames);
                break;
            case LabeledStatementNode lab:
                CollectVarNames(lab.Body, outNames);
                break;
            case ForStatementNode f:
                if (f.Initializer is { } init) CollectVarNames(init, outNames);
                CollectVarNames(f.Body, outNames);
                break;
            case ForInStatementNode fi:
                CollectVarNames(fi.Initializer, outNames);
                CollectVarNames(fi.Body, outNames);
                break;
            case ForOfStatementNode fo:
                CollectVarNames(fo.Initializer, outNames);
                CollectVarNames(fo.Body, outNames);
                break;
            case ForAwaitOfStatementNode fa:
                CollectVarNames(fa.Initializer, outNames);
                CollectVarNames(fa.Body, outNames);
                break;
            case TryCatchStatementNode tc:
                CollectVarNames(tc.TryBlock, outNames);
                CollectVarNames(tc.CatchBlock, outNames);
                break;
            case TryFinallyStatementNode tf:
                CollectVarNames(tf.TryBlock, outNames);
                CollectVarNames(tf.FinallyBlock, outNames);
                break;
            case TryCatchFinallyStatementNode tcf:
                CollectVarNames(tcf.TryBlock, outNames);
                CollectVarNames(tcf.CatchBlock, outNames);
                CollectVarNames(tcf.FinallyBlock, outNames);
                break;
            case SwitchStatementNode sw:
                foreach (var c in sw.Cases)
                    foreach (var s in c.Consequent) CollectVarNames(s, outNames);
                break;
            // FunctionDeclarationNode / ClassDeclarationNode: their bodies open a new
            // var-scope, so their inner `var`s do not hoist here. The declaration name
            // itself is var-like only at function/script top level (Annex B), which we
            // intentionally do not treat as a lexical conflict.
        }
    }

    // ECMA-262 13.15.1 (try Statement: Static Semantics: Early Errors):
    //   - BoundNames of CatchParameter must not contain duplicate entries.
    //   - No BoundName of CatchParameter may also occur in the LexicallyDeclaredNames
    //     of the catch Block.
    //   - No BoundName of CatchParameter may also occur in the VarDeclaredNames of the
    //     catch Block — except when CatchParameter is a single BindingIdentifier, which
    //     Annex B.3.4 permits to coexist with a `var` of the same name.
    private static void CheckCatchParameter(string catchIdentifier, BindingPatternNode? catchPattern,
        IReadOnlyList<StatementNode> catchStatements)
    {
        var boundNames = new List<string>();
        var isPattern = catchPattern is not null;
        if (catchPattern is not null)
        {
            CollectPatternNames(catchPattern, boundNames);
        }
        else if (catchIdentifier is not ("<no-binding>" or "<pattern>"))
        {
            boundNames.Add(catchIdentifier);
        }

        if (boundNames.Count == 0)
        {
            return;
        }

        var seen = new HashSet<string>(System.StringComparer.Ordinal);
        foreach (var name in boundNames)
        {
            if (!seen.Add(name))
                throw new JsParserException($"Identifier '{name}' has already been declared.");
        }

        // LexicallyDeclaredNames of the catch Block (top-level let/const/class + functions).
        var lexNames = new HashSet<string>(System.StringComparer.Ordinal);
        foreach (var stmt in catchStatements)
        {
            switch (Unwrap(stmt))
            {
                case VariableDeclarationStatementNode v when v.Kind != "var":
                    foreach (var n in DeclaredNames(v)) lexNames.Add(n);
                    break;
                case ClassDeclarationNode c when c.Name is { Length: > 0 }:
                    lexNames.Add(c.Name);
                    break;
                case FunctionDeclarationNode f when f.Name is { Length: > 0 }:
                    lexNames.Add(f.Name);
                    break;
            }
        }
        foreach (var name in seen)
        {
            if (lexNames.Contains(name))
                throw new JsParserException($"Identifier '{name}' has already been declared.");
        }

        // A binding pattern also conflicts with any `var` of the same name in the block;
        // a single binding identifier does not (Annex B.3.4).
        if (isPattern)
        {
            var varNames = new HashSet<string>(System.StringComparer.Ordinal);
            foreach (var stmt in catchStatements) CollectVarNames(stmt, varNames);
            foreach (var name in seen)
            {
                if (varNames.Contains(name))
                    throw new JsParserException($"Identifier '{name}' has already been declared.");
            }
        }
    }

    // Recurse into the nested lexical scopes a statement contains so each is checked
    // at its own level. Block/case/loop/try bodies are scopes; function and class
    // bodies are handled by their own parse-time validation.
    private static void RecurseNested(StatementNode stmt)
    {
        switch (Unwrap(stmt))
        {
            case BlockStatementNode block:
                CheckScope(block.Statements);
                break;
            case IfStatementNode ifs:
                RecurseNested(ifs.Consequent);
                if (ifs.Alternate is { } alt) RecurseNested(alt);
                break;
            case WhileStatementNode w:
                RecurseNested(w.Body);
                break;
            case DoWhileStatementNode dw:
                RecurseNested(dw.Body);
                break;
            case WithStatementNode with:
                RecurseNested(with.Body);
                break;
            case ForStatementNode f:
                RecurseNested(f.Body);
                break;
            case ForInStatementNode fi:
                RecurseNested(fi.Body);
                break;
            case ForOfStatementNode fo:
                RecurseNested(fo.Body);
                break;
            case ForAwaitOfStatementNode fa:
                RecurseNested(fa.Body);
                break;
            case TryCatchStatementNode tc:
                CheckScope(tc.TryBlock.Statements);
                CheckCatchParameter(tc.CatchIdentifier, tc.CatchPattern, tc.CatchBlock.Statements);
                CheckScope(tc.CatchBlock.Statements);
                break;
            case TryFinallyStatementNode tf:
                CheckScope(tf.TryBlock.Statements);
                CheckScope(tf.FinallyBlock.Statements);
                break;
            case TryCatchFinallyStatementNode tcf:
                CheckScope(tcf.TryBlock.Statements);
                CheckCatchParameter(tcf.CatchIdentifier, tcf.CatchPattern, tcf.CatchBlock.Statements);
                CheckScope(tcf.CatchBlock.Statements);
                CheckScope(tcf.FinallyBlock.Statements);
                break;
            case SwitchStatementNode sw:
                // A switch CaseBlock is a single lexical scope spanning all clauses.
                // Function declarations in a CaseBlock contribute to its
                // LexicallyDeclaredNames even in sloppy script code; the Annex B
                // block-function relaxation does not apply to switch clauses.
                var caseStatements = new List<StatementNode>();
                foreach (var c in sw.Cases) caseStatements.AddRange(c.Consequent);
                CheckScope(caseStatements, treatFunctionsAsLexical: true);
                break;
        }
    }
}
