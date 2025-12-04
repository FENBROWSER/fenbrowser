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

        public Identifier(Token token, string value)
        {
            Token = token;
            Value = value;
        }

        public override string String() => Value;
    }

    // Empty expression for recovery (;, trailing commas, etc.)
    public class EmptyExpression : Expression
    {
        public override string String() => "";
    }

    public class LetStatement : Statement
    {
        public Identifier Name { get; set; }
        public Expression DestructuringPattern { get; set; }
        public Expression Value { get; set; }

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

    public class FunctionLiteral : Expression
    {
        public string Name { get; set; }
        public List<Identifier> Parameters { get; set; } = new List<Identifier>();
        public BlockStatement Body { get; set; }

        public override string String()
        {
            var sb = new StringBuilder();
            sb.Append(TokenLiteral());
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
        public Expression Object { get; set; }     // Object to iterate
        public BlockStatement Body { get; set; }

        public override string String()
        {
            return $"for ({Variable.String()} in {Object.String()}) {Body.String()}";
        }
    }

    // for (variable of iterable) { body }
    public class ForOfStatement : Statement
    {
        public Identifier Variable { get; set; }  // Loop variable
        public Expression Iterable { get; set; }  // Iterable object
        public BlockStatement Body { get; set; }

        public override string String()
        {
            return $"for ({Variable.String()} of {Iterable.String()}) {Body.String()}";
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

    public class MethodDefinition : Statement
    {
        public Identifier Key { get; set; }
        public FunctionLiteral Value { get; set; }
        public string Kind { get; set; } // "constructor", "method", "get", "set"
        public bool Static { get; set; }

        public override string String()
        {
            var sb = new StringBuilder();
            if (Static) sb.Append("static ");
            sb.Append(Key.String());
            sb.Append(Value.String());
            return sb.ToString();
        }
    }

    public class ClassStatement : Statement
    {
        public Identifier Name { get; set; }
        public Identifier SuperClass { get; set; }
        public BlockStatement Body { get; set; } // Contains MethodDefinitions
        public List<MethodDefinition> Methods { get; set; } = new List<MethodDefinition>();

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

    public class ExportDeclaration : Statement
    {
        public Statement Declaration { get; set; } // export var x = 1;
        public Expression DefaultExpression { get; set; } // export default ...
        public List<ImportSpecifier> Specifiers { get; set; } // export { x, y }
        public string Source { get; set; } // export ... from 'module'

        public override string String()
        {
            return "export ...";
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
}
