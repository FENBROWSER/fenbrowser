using System;
using System.Collections.Generic;
using System.Text;

namespace FenBrowser.FenEngine.Core
{
    public abstract class AstNode
    {
        public abstract string TokenLiteral();
        public abstract string String();
    }

    public abstract class Statement : AstNode
    {
        public Token Token { get; set; } // The first token of the statement
        public override string TokenLiteral() => Token.Literal;
    }

    public abstract class Expression : AstNode
    {
        public Token Token { get; set; } // The first token of the expression
        public override string TokenLiteral() => Token.Literal;
    }

    public class Program : AstNode
    {
        public List<Statement> Statements { get; set; } = new List<Statement>();

        public override string TokenLiteral()
        {
            if (Statements.Count > 0)
            {
                return Statements[0].TokenLiteral();
            }
            return "";
        }

        public override string String()
        {
            var sb = new StringBuilder();
            foreach (var stmt in Statements)
            {
                sb.Append(stmt.String());
            }
            return sb.ToString();
        }
    }

    public class Identifier : Expression
    {
        public string Value { get; set; }
        public Expression DefaultValue { get; set; } // For default parameters: function(a = 1)
        public bool IsRest { get; set; } // For rest parameters: function(...args)
        public Expression DestructuringPattern { get; set; } // For destructuring parameters: function({a, b}) or function([x, y])

        public Identifier(Token token, string value)
        {
            Token = token;
            Value = value;
        }

        public override string String() => Value;
    }

    // Private identifier for private class members (#field, #method)
    public class PrivateIdentifier : Expression
    {
        public string Name { get; set; } // The name without the # prefix

        public PrivateIdentifier(Token token, string name)
        {
            Token = token;
            Name = name;
        }

        public override string String() => "#" + Name;
    }

    // Empty expression for recovery (;, trailing commas, etc.)
    public class EmptyExpression : Expression
    {
        public override string String() => "";
    }

    public enum DeclarationKind
    {
        Var,
        Let,
        Const
    }

    public class LetStatement : Statement
    {
        public Identifier Name { get; set; }
        public Expression DestructuringPattern { get; set; }
        public Expression Value { get; set; }
        public DeclarationKind Kind { get; set; } = DeclarationKind.Var; // Track var/let/const

        public override string String()
        {
            var sb = new StringBuilder();
            sb.Append(TokenLiteral() + " ");
            sb.Append(Name.String());
            sb.Append(" = ");
            if (Value != null)
            {
                sb.Append(Value.String());
            }
            sb.Append(";");
            return sb.ToString();
        }
    }

    public class ReturnStatement : Statement
    {
        public Expression ReturnValue { get; set; }

        public override string String()
        {
            var sb = new StringBuilder();
            sb.Append(TokenLiteral() + " ");
            if (ReturnValue != null)
            {
                sb.Append(ReturnValue.String());
            }
            sb.Append(";");
            return sb.ToString();
        }
    }

    public class ExpressionStatement : Statement
    {
        public Expression Expression { get; set; }

        public override string String()
        {
            if (Expression != null)
            {
                return Expression.String();
            }
            return "";
        }
    }

    public class IntegerLiteral : Expression
    {
        public long Value { get; set; }

        public override string String() => Token.Literal;
    }
    
    public class DoubleLiteral : Expression
    {
        public double Value { get; set; }
        public override string String() => Token.Literal;
    }

    public class BooleanLiteral : Expression
    {
        public bool Value { get; set; }
        public override string String() => Token.Literal;
    }

    public class StringLiteral : Expression
    {
        public string Value { get; set; }
        public override string String() => Token.Literal;
    }

    public class NullLiteral : Expression
    {
        public override string String() => "null";
    }

    public class UndefinedLiteral : Expression
    {
        public override string String() => "undefined";
    }

    public class PrefixExpression : Expression
    {
        public string Operator { get; set; }
        public Expression Right { get; set; }

        public override string String()
        {
            return $"({Operator}{Right.String()})";
        }
    }

    public class InfixExpression : Expression
    {
        public Expression Left { get; set; }
        public string Operator { get; set; }
        public Expression Right { get; set; }

        public override string String()
        {
            return $"({Left.String()} {Operator} {Right.String()})";
        }
    }

    public class BlockStatement : Statement
    {
        public List<Statement> Statements { get; set; } = new List<Statement>();

        public override string String()
        {
            var sb = new StringBuilder();
            foreach (var stmt in Statements)
            {
                sb.Append(stmt.String());
            }
            return sb.ToString();
        }
    }

    public class IfExpression : Expression
    {
        public Expression Condition { get; set; }
        public BlockStatement Consequence { get; set; }
        public BlockStatement Alternative { get; set; }

        public override string String()
        {
            var sb = new StringBuilder();
            sb.Append("if");
            sb.Append(Condition.String());
            sb.Append(" ");
            sb.Append(Consequence.String());
            if (Alternative != null)
            {
                sb.Append("else ");
                sb.Append(Alternative.String());
            }
            return sb.ToString();
        }
    }

    public class IfStatement : Statement
    {
        public Expression Condition { get; set; }
        public BlockStatement Consequence { get; set; }
        public BlockStatement Alternative { get; set; }

        public override string String()
        {
            var sb = new StringBuilder();
            sb.Append("if");
            sb.Append(Condition.String());
            sb.Append(" ");
            sb.Append(Consequence.String());
            if (Alternative != null)
            {
                sb.Append("else ");
                sb.Append(Alternative.String());
            }
            return sb.ToString();
        }
    }

    public class FunctionLiteral : Expression
    {
        public string Name { get; set; }
        public List<Identifier> Parameters { get; set; } = new List<Identifier>();
        public BlockStatement Body { get; set; }
        public bool IsGenerator { get; set; } = false; // function* syntax
        public bool IsAsync { get; set; } = false;
        public string Source { get; set; } // ES2019: Original source code

        public override string String()
        {
            var sb = new StringBuilder();
            if (IsAsync) sb.Append("async ");
            sb.Append(TokenLiteral());
            if (IsGenerator) sb.Append("*");
            sb.Append("(");
            var paramsStr = new List<string>();
            foreach (var p in Parameters)
            {
                paramsStr.Add(p.String());
            }
            sb.Append(string.Join(", ", paramsStr));
            sb.Append(") ");
            sb.Append(Body.String());
            return sb.ToString();
        }
    }
    

    public class FunctionDeclarationStatement : Statement
    {
        public FunctionLiteral Function { get; set; }

        public override string String()
        {
            return Function.String();
        }
    }

    /// <summary>
    /// yield expression for generator functions
    /// </summary>
    public class YieldExpression : Expression
    {
        public Expression Value { get; set; }
        public bool Delegate { get; set; } // yield* (delegation)

        public override string String()
        {
            if (Delegate)
                return $"yield* {Value?.String() ?? ""}";
            return $"yield {Value?.String() ?? ""}";
        }
    }
    
    /// <summary>
    /// ES2015 new.target meta property - returns constructor function in new call
    /// </summary>
    public class NewTargetExpression : Expression
    {
        public override string String() => "new.target";
    }

    /// <summary>
    /// ES2020 import.meta meta property - returns metadata object for the module
    /// </summary>
    public class ImportMetaExpression : Expression
    {
        public override string String() => "import.meta";
    }

    public class CallExpression : Expression
    {
        public Expression Function { get; set; } // Identifier or FunctionLiteral
        public List<Expression> Arguments { get; set; } = new List<Expression>();

        public override string String()
        {
            var sb = new StringBuilder();
            sb.Append(Function.String());
            sb.Append("(");
            var args = new List<string>();
            foreach (var a in Arguments)
            {
                args.Add(a.String());
            }
            sb.Append(string.Join(", ", args));
            sb.Append(")");
            return sb.ToString();
        }
    }

    public class NewExpression : Expression
    {
        public Expression Constructor { get; set; }
        public List<Expression> Arguments { get; set; } = new List<Expression>();

        public override string String()
        {
            var sb = new StringBuilder();
            sb.Append("new ");
            sb.Append(Constructor.String());
            sb.Append("(");
            var args = new List<string>();
            foreach (var arg in Arguments)
            {
                args.Add(arg.String());
            }
            sb.Append(string.Join(", ", args));
            sb.Append(")");
            return sb.ToString();
        }
    }

    public class MemberExpression : Expression
    {
        public Expression Object { get; set; } // Left side: document, el, etc.
        public string Property { get; set; } // Property name: "getElementById", "textContent"

        // Inline Caching properties
        public Types.Shape CachedShape { get; set; }
        public int CachedIndex { get; set; } = -1; // -1 indicates cache miss/uninitialized

        public override string String()
        {
            return $"{Object.String()}.{Property}";
        }
    }

    public class IndexExpression : Expression
    {
        public Expression Left { get; set; } // The object being indexed (e.g., arr)
        public Expression Index { get; set; } // The index (e.g., 0)

        public override string String()
        {
            return $"({Left.String()}[{Index.String()}])";
        }
    }

    public class AssignmentExpression : Expression
    {
        public Expression Left { get; set; }  // Can be Identifier or MemberExpression
        public Expression Right { get; set; } // Value to assign

        public override string String()
        {
            return $"{Left.String()} = {Right.String()}";
        }
    }

    // ES2021: Logical Assignment (||=, &&=, ??=)
    public class LogicalAssignmentExpression : Expression
    {
        public string Operator { get; set; } // ||=, &&=, ??=
        public Expression Left { get; set; }
        public Expression Right { get; set; }

        public override string String()
        {
            return $"{Left.String()} {Operator} {Right.String()}";
        }
    }

    public class TryStatement : Statement
    {
        public BlockStatement Block { get; set; }
        public BlockStatement CatchBlock { get; set; }
        public Identifier CatchParameter { get; set; } // e.g. catch(e)
        public BlockStatement FinallyBlock { get; set; }

        public override string String()
        {
            var sb = new StringBuilder();
            sb.Append("try ");
            sb.Append(Block.String());
            if (CatchBlock != null)
            {
                sb.Append(" catch");
                if (CatchParameter != null)
                {
                    sb.Append($"({CatchParameter.String()})");
                }
                sb.Append(" ");
                sb.Append(CatchBlock.String());
            }
            if (FinallyBlock != null)
            {
                sb.Append(" finally ");
                sb.Append(FinallyBlock.String());
            }
            return sb.ToString();
        }
    }

    public class ArrayLiteral : Expression
    {
        public List<Expression> Elements { get; set; } = new List<Expression>();

        public override string String()
        {
            var sb = new StringBuilder();
            sb.Append("[");
            var elements = new List<string>();
            foreach (var el in Elements)
            {
                elements.Add(el.String());
            }
            sb.Append(string.Join(", ", elements));
            sb.Append("]");
            return sb.ToString();
        }
    }

    public class ObjectLiteral : Expression
    {
        public Dictionary<string, Expression> Pairs { get; set; } = new Dictionary<string, Expression>();
        /// <summary>
        /// Maps placeholder keys (e.g. "__computed_0") to their computed key expressions.
        /// </summary>
        public Dictionary<string, Expression> ComputedKeys { get; set; } = new Dictionary<string, Expression>();

        public override string String()
        {
            var sb = new StringBuilder();
            sb.Append("{");
            var pairs = new List<string>();
            foreach (var pair in Pairs)
            {
                pairs.Add($"{pair.Key}: {pair.Value.String()}");
            }
            sb.Append(string.Join(", ", pairs));
            sb.Append("}");
            return sb.ToString();
        }
    }

    public class ThrowStatement : Statement
    {
        public Expression Value { get; set; }

        public override string String()
        {
            return $"throw {Value.String()};";
        }
    }

    /// <summary>
    /// Throw expression for use in expression contexts (arrow functions, etc.)
    /// </summary>
    public class ThrowExpression : Expression
    {
        public Expression Value { get; set; }

        public override string String()
        {
            return $"throw {Value.String()}";
        }
    }


    public class WhileStatement : Statement
    {
        public Expression Condition { get; set; }
        public BlockStatement Body { get; set; }

        public override string String()
        {
            return $"while ({Condition.String()}) {Body.String()}";
        }
    }

    public class ForStatement : Statement
    {
        public Statement Init { get; set; } // var i = 0; or i = 0;
        public Expression Condition { get; set; }
        public Statement Update { get; set; } // i++
        public BlockStatement Body { get; set; }

        public override string String()
        {
            var sb = new StringBuilder();
            sb.Append("for (");
            if (Init != null) sb.Append(Init.String());
            sb.Append("; ");
            if (Condition != null) sb.Append(Condition.String());
            sb.Append("; ");
            if (Update != null) sb.Append(Update.String());
            sb.Append(") ");
            sb.Append(Body.String());
            return sb.ToString();
        }
    }

    // Ternary conditional: condition ? consequent : alternate
    public class ConditionalExpression : Expression
    {
        public Expression Condition { get; set; }
        public Expression Consequent { get; set; }
        public Expression Alternate { get; set; }

        public override string String()
        {
            return $"({Condition.String()} ? {Consequent.String()} : {Alternate.String()})";
        }
    }

    // Arrow function: (params) => body or (params) => { statements }
    public class ArrowFunctionExpression : Expression
    {
        public List<Identifier> Parameters { get; set; } = new List<Identifier>();
        public AstNode Body { get; set; } // BlockStatement or Expression
        public bool IsAsync { get; set; } = false;

        public override string String()
        {
            var sb = new StringBuilder();
            sb.Append("(");
            var paramsStr = new List<string>();
            foreach (var p in Parameters)
            {
                paramsStr.Add(p.String());
            }
            sb.Append(string.Join(", ", paramsStr));
            sb.Append(") => ");
            if (Body is BlockStatement block)
            {
                sb.Append("{ ");
                sb.Append(block.String());
                sb.Append(" }");
            }
            else if (Body is Expression expr)
            {
                sb.Append(expr.String());
            }
            return sb.ToString();
        }
    }

    // for (variable in object) { body }
    public class ForInStatement : Statement
    {
        public Identifier Variable { get; set; }  // Loop variable (x in "for x in obj")
        public Expression DestructuringPattern { get; set; } // For destructuring: for (const {a,b} in obj)
        public Expression Object { get; set; }     // Object to iterate
        public BlockStatement Body { get; set; }

        public override string String()
        {
            return $"for ({Variable?.String() ?? DestructuringPattern?.String()} in {Object.String()}) {Body.String()}";
        }
    }

    // for (variable of iterable) { body } or for await (variable of asyncIterable) { body }
    public class ForOfStatement : Statement
    {
        public Identifier Variable { get; set; }  // Loop variable
        public Expression DestructuringPattern { get; set; } // For destructuring: for (const [a,b] of iterable)
        public Expression Iterable { get; set; }  // Iterable object
        public BlockStatement Body { get; set; }
        public bool IsAwait { get; set; } // ES2018: for await...of async iteration

        public override string String()
        {
            var awaitStr = IsAwait ? "await " : "";
            return $"for {awaitStr}({Variable?.String() ?? DestructuringPattern?.String()} of {Iterable.String()}) {Body.String()}";
        }
    }

    // Spread element: ...argument
    public class SpreadElement : Expression
    {
        public Expression Argument { get; set; }

        public override string String()
        {
            return $"...{Argument.String()}";
        }
    }

    // Decorator for classes, methods, and properties (Stage 3)
    public class Decorator : AstNode
    {
        public Expression Expression { get; set; } // Decorator expression (e.g., @decorator or @decorator(args))
        public Token Token { get; set; }

        public override string TokenLiteral() => Token?.Literal ?? "";

        public override string String()
        {
            return "@" + Expression?.String();
        }
    }

    public class MethodDefinition : Statement
    {
        public Identifier Key { get; set; }
        public FunctionLiteral Value { get; set; }
        public string Kind { get; set; } // "constructor", "method", "get", "set"
        public bool Static { get; set; }
        public bool IsPrivate { get; set; } // true if #methodName
        public bool Computed { get; set; } // true if [expr]() {}
        public List<Decorator> Decorators { get; set; } = new List<Decorator>(); // Stage 3 decorators

        public override string String()
        {
            var sb = new StringBuilder();
            if (Static) sb.Append("static ");
            if (IsPrivate) sb.Append("#");
            sb.Append(Key.String());
            sb.Append(Value.String());
            return sb.ToString();
        }
    }

    // Class field (property) declaration
    public class ClassProperty : Statement
    {
        public Identifier Key { get; set; }
        public Expression Value { get; set; }
        public bool Static { get; set; }
        public bool IsPrivate { get; set; } // true if #field
        public List<Decorator> Decorators { get; set; } = new List<Decorator>(); // Stage 3 decorators

        public override string String()
        {
            var sb = new StringBuilder();
            if (Static) sb.Append("static ");
            if (IsPrivate) sb.Append("#");
            sb.Append(Key.String());
            if (Value != null)
            {
                sb.Append(" = ");
                sb.Append(Value.String());
            }
            return sb.ToString();
        }
    }

    public class ClassStatement : Statement
    {
        public Identifier Name { get; set; }
        public Identifier SuperClass { get; set; }
        public BlockStatement Body { get; set; } // Contains MethodDefinitions
        public List<MethodDefinition> Methods { get; set; } = new List<MethodDefinition>();
        public List<ClassProperty> Properties { get; set; } = new List<ClassProperty>(); // Class fields (including private)
        public List<StaticBlock> StaticBlocks { get; set; } = new List<StaticBlock>(); // ES2022 static blocks
        public List<Decorator> Decorators { get; set; } = new List<Decorator>(); // Stage 3 decorators

        public override string String()
        {
            var sb = new StringBuilder();
            sb.Append("class ");
            sb.Append(Name.String());
            if (SuperClass != null)
            {
                sb.Append(" extends ");
                sb.Append(SuperClass.String());
            }
            sb.Append(" { ");
            foreach (var prop in Properties)
            {
                sb.Append(prop.String());
                sb.Append("; ");
            }
            foreach (var block in StaticBlocks)
            {
                sb.Append(block.String());
            }
            foreach (var method in Methods)
            {
                sb.Append(method.String());
            }
            sb.Append(" }");
            return sb.ToString();
        }
    }

    public class ClassExpression : Expression
    {
        public Identifier Name { get; set; }
        public Identifier SuperClass { get; set; }
        public List<MethodDefinition> Methods { get; set; } = new List<MethodDefinition>();
        public List<ClassProperty> Properties { get; set; } = new List<ClassProperty>();
        public List<StaticBlock> StaticBlocks { get; set; } = new List<StaticBlock>();
        public List<Decorator> Decorators { get; set; } = new List<Decorator>(); // Stage 3 decorators

        public override string String()
        {
            var sb = new StringBuilder();
            sb.Append("class");
            if (Name != null)
            {
                sb.Append(" " + Name.String());
            }
            if (SuperClass != null)
            {
                sb.Append(" extends ");
                sb.Append(SuperClass.String());
            }
            sb.Append(" { ");
            foreach (var prop in Properties)
            {
                sb.Append(prop.String());
                sb.Append("; ");
            }
            foreach (var method in Methods)
            {
                sb.Append(method.String());
            }
            sb.Append(" }");
            return sb.ToString();
        }
    }

    public class ImportSpecifier
    {
        public Identifier Local { get; set; }
        public Identifier Imported { get; set; }
    }

    public class ImportDeclaration : Statement
    {
        public string Source { get; set; }
        public List<ImportSpecifier> Specifiers { get; set; } = new List<ImportSpecifier>();

        public override string String()
        {
            var sb = new StringBuilder();
            sb.Append("import ");
            // Simplified string representation
            if (Specifiers.Count > 0)
            {
                sb.Append("{ ... }");
            }
            sb.Append(" from ");
            sb.Append($"\"{Source}\"");
            return sb.ToString();
        }
    }

    public class ExportSpecifier
    {
        public Identifier Local { get; set; }    // The local binding name (or "*" for export * from ...)
        public Identifier Exported { get; set; } // The exported name (alias)
        
        public string String()
        {
            if (Local != null && Exported != null && Local.Value != Exported.Value)
                return $"{Local.String()} as {Exported.String()}";
            return Local?.String() ?? "";
        }
    }

    public class ExportDeclaration : Statement
    {
        public Statement Declaration { get; set; } // export var x = 1;
        public Expression DefaultExpression { get; set; } // export default ...
        public List<ExportSpecifier> Specifiers { get; set; } = new List<ExportSpecifier>(); // export { x, y }
        public string Source { get; set; } // export ... from 'module'

        public override string String()
        {
            var sb = new StringBuilder();
            sb.Append("export ");
            if (DefaultExpression != null)
            {
                sb.Append("default ");
                sb.Append(DefaultExpression.String());
            }
            else if (Declaration != null)
            {
                sb.Append(Declaration.String());
            }
            else
            {
                if (Specifiers.Count > 0)
                {
                    // Check for export * 
                    if (Specifiers.Count == 1 && Specifiers[0].Local.Value == "*")
                    {
                         sb.Append(Specifiers[0].String());
                    }
                    else
                    {
                        sb.Append("{ ");
                        var specs = new List<string>();
                        foreach (var s in Specifiers) specs.Add(s.String());
                        sb.Append(string.Join(", ", specs));
                        sb.Append(" }");
                    }
                }
                if (Source != null)
                {
                    sb.Append(" from \"");
                    sb.Append(Source);
                    sb.Append("\"");
                }
            }
            return sb.ToString();
        }
    }

    public class AwaitExpression : Expression
    {
        public Expression Argument { get; set; }

        public override string String()
        {
            return $"await {Argument.String()}";
        }
    }

    public class AsyncFunctionExpression : Expression
    {
        public Identifier Name { get; set; }
        public List<Identifier> Parameters { get; set; } = new List<Identifier>();
        public BlockStatement Body { get; set; }

        public override string String()
        {
            var sb = new StringBuilder();
            sb.Append("async function");
            if (Name != null)
            {
                sb.Append(" ");
                sb.Append(Name.String());
            }
            sb.Append("(");
            var args = new List<string>();
            foreach (var a in Parameters)
            {
                args.Add(a.String());
            }
            sb.Append(string.Join(", ", args));
            sb.Append(") ");
            sb.Append(Body.String());
            return sb.ToString();
        }
    }

    // Regular expression literal: /pattern/flags
    public class RegexLiteral : Expression
    {
        public string Pattern { get; set; }
        public string Flags { get; set; }

        public override string String()
        {
            return $"/{Pattern}/{Flags}";
        }
    }

    // Switch statement: switch (expr) { case val: ...; default: ... }
    public class SwitchStatement : Statement
    {
        public Expression Discriminant { get; set; }
        public List<SwitchCase> Cases { get; set; } = new List<SwitchCase>();

        public override string String()
        {
            var sb = new StringBuilder();
            sb.Append($"switch ({Discriminant.String()}) {{ ");
            foreach (var c in Cases) sb.Append(c.String());
            sb.Append(" }");
            return sb.ToString();
        }
    }

    // Switch case: case val: ... or default: ...
    public class SwitchCase : AstNode
    {
        public Token Token { get; set; }
        public Expression Test { get; set; }  // null for default
        public List<Statement> Consequent { get; set; } = new List<Statement>();
        
        public override string TokenLiteral() => Token.Literal;
        public override string String()
        {
            var label = Test != null ? $"case {Test.String()}" : "default";
            return $"{label}: ...";
        }
    }

    // Labeled statement: label: statement
    public class LabeledStatement : Statement
    {
        public Identifier Label { get; set; }
        public Statement Body { get; set; }

        public override string String() => $"{Label.Value}: {Body.String()}";
    }

    // Break statement
    public class BreakStatement : Statement
    {
        public Identifier Label { get; set; }  // Optional label
        public override string String() => Label != null ? $"break {Label.Value};" : "break;";
    }

    // Continue statement
    public class ContinueStatement : Statement
    {
        public Identifier Label { get; set; }  // Optional label
        public override string String() => Label != null ? $"continue {Label.Value};" : "continue;";
    }

    // Template literal: `Hello ${name}!`
    public class TemplateLiteral : Expression
    {
        public List<TemplateElement> Quasis { get; set; } = new List<TemplateElement>();
        public List<Expression> Expressions { get; set; } = new List<Expression>();

        public override string String()
        {
            var sb = new StringBuilder("`");
            for (int i = 0; i < Quasis.Count; i++)
            {
                sb.Append(Quasis[i].Value);
                if (i < Expressions.Count)
                {
                    sb.Append("${");
                    sb.Append(Expressions[i].String());
                    sb.Append("}");
                }
            }
            sb.Append("`");
            return sb.ToString();
        }
    }

    // Template element (the string parts between ${})
    public class TemplateElement : AstNode
    {
        public Token Token { get; set; }
        public string Value { get; set; }
        public bool Tail { get; set; }  // Is this the last element?
        
        public override string TokenLiteral() => Token?.Literal ?? "";
        public override string String() => Value;
    }

    // Do-while statement: do { } while (condition);
    public class DoWhileStatement : Statement
    {
        public Expression Condition { get; set; }
        public BlockStatement Body { get; set; }

        public override string String()
        {
            return $"do {Body.String()} while ({Condition.String()});";
        }
    }

    // Tagged template literal: tag`template ${expr} literal`
    public class TaggedTemplateExpression : Expression
    {
        public Expression Tag { get; set; }  // The tag function (e.g., html, css, myTag)
        public List<string> Strings { get; set; } = new List<string>();  // String parts
        public List<Expression> Expressions { get; set; } = new List<Expression>();  // Interpolated expressions

        public override string String()
        {
            var sb = new StringBuilder();
            sb.Append(Tag.String());
            sb.Append("`");
            for (int i = 0; i < Strings.Count; i++)
            {
                sb.Append(Strings[i]);
                if (i < Expressions.Count)
                {
                    sb.Append("${");
                    sb.Append(Expressions[i].String());
                    sb.Append("}");
                }
            }
            sb.Append("`");
            return sb.ToString();
        }
    }

    // ES6+ Optional chaining: obj?.prop, obj?.[prop], obj?.method()
    public class OptionalChainExpression : Expression
    {
        public Expression Object { get; set; }      // Left side (can be any expression)
        public Expression Property { get; set; }    // For computed access: obj?.[expr]
        public string PropertyName { get; set; }    // For dot access: obj?.prop
        public bool IsComputed { get; set; }        // true for ?.[expr], false for ?.prop
        public bool IsCall { get; set; }            // true for obj?.()
        public List<Expression> Arguments { get; set; } = new List<Expression>(); // For ?.()

        public override string String()
        {
            if (IsCall)
            {
                var args = string.Join(", ", Arguments.ConvertAll(a => a.String()));
                return $"{Object.String()}?.({args})";
            }
            if (IsComputed)
                return $"{Object.String()}?.[{Property.String()}]";
            return $"{Object.String()}?.{PropertyName}";
        }
    }

    // ES6+ Nullish coalescing: a ?? b
    public class NullishCoalescingExpression : Expression
    {
        public Expression Left { get; set; }
        public Expression Right { get; set; }

        public override string String()
        {
            return $"({Left.String()} ?? {Right.String()})";
        }
    }

    // ES6+ BigInt literal: 123n
    public class BigIntLiteral : Expression
    {
        public string Value { get; set; }  // Store as string to preserve precision

        public override string String() => $"{Value}n";
    }

    // ES6+ Class static block: static { ... }
    public class StaticBlock : Statement
    {
        public BlockStatement Body { get; set; }

        public override string String()
        {
            return $"static {{ {Body.String()} }}";
        }
    }

    // ES6+ Exponentiation: a ** b
    public class ExponentiationExpression : Expression
    {
        public Expression Left { get; set; }
        public Expression Right { get; set; }

        public override string String()
        {
            return $"({Left.String()} ** {Right.String()})";
        }
    }

    // ES6+ Compound assignment expression: a += b, a -= b, etc.
    public class CompoundAssignmentExpression : Expression
    {
        public Expression Left { get; set; }
        public string Operator { get; set; }  // "+=", "-=", "*=", "/=", "%=", "**=", etc.
        public Expression Right { get; set; }

        public override string String()
        {
            return $"({Left.String()} {Operator} {Right.String()})";
        }
    }

    // ES6+ Bitwise expressions for completeness
    public class BitwiseExpression : Expression
    {
        public Expression Left { get; set; }
        public string Operator { get; set; }  // "&", "|", "^", "<<", ">>", ">>>"
        public Expression Right { get; set; }

        public override string String()
        {
            return $"({Left.String()} {Operator} {Right.String()})";
        }
    }

    // ES6+ Bitwise NOT: ~a
    public class BitwiseNotExpression : Expression
    {
        public Expression Operand { get; set; }

        public override string String()
        {
            return $"(~{Operand.String()})";
        }
    }

    // ES5.1 with statement: with (object) { body }
    public class WithStatement : Statement
    {
        public Expression Object { get; set; }
        public Statement Body { get; set; }

        public override string String()
        {
            return $"with ({Object.String()}) {Body.String()}";
        }
    }
}
