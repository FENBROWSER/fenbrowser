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

        public Identifier(Token token, string value)
        {
            Token = token;
            Value = value;
        }

        public override string String() => Value;
    }

    public class LetStatement : Statement
    {
        public Identifier Name { get; set; }
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
}
