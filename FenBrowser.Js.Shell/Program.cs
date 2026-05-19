using System.Reflection;
using System.Text.Json;
using FenBrowser.Js.Ast;
using FenBrowser.Js.AstValidation;
using FenBrowser.Js.Lexer;
using FenBrowser.Js.Parser;
using FenBrowser.Js.Source;

var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0";

if (args.Length == 0)
{
    Console.Error.WriteLine("Usage: fenjs --version | --eval <code> | --dump-tokens <file> | --dump-ast <file>");
    return 1;
}

if (args.Length == 1 && args[0] == "--version")
{
    Console.WriteLine($"fenjs {version}");
    return 0;
}

if (args.Length >= 2 && args[0] == "--eval")
{
    var code = string.Join(' ', args.Skip(1));
    if (string.IsNullOrWhiteSpace(code))
    {
        Console.Error.WriteLine("--eval requires source text.");
        return 2;
    }

    // Placeholder until parser/runtime wiring lands in later milestones.
    Console.WriteLine("undefined");
    return 0;
}

if (args.Length == 2 && args[0] == "--dump-tokens")
{
    var path = args[1];
    if (!File.Exists(path))
    {
        Console.Error.WriteLine($"File not found: {path}");
        return 4;
    }

    var source = new SourceText(File.ReadAllText(path), path);
    var lexer = new JsLexer(source);
    var tokens = lexer.LexAll()
        .Select(t => new
        {
            kind = t.Kind.ToString(),
            text = t.Text,
            span = new { t.Span.Start, t.Span.Length, t.Span.Line, t.Span.Column }
        })
        .ToArray();

    Console.WriteLine(JsonSerializer.Serialize(tokens, new JsonSerializerOptions { WriteIndented = true }));
    return 0;
}

if (args.Length == 2 && args[0] == "--dump-ast")
{
    var path = args[1];
    if (!File.Exists(path))
    {
        Console.Error.WriteLine($"File not found: {path}");
        return 4;
    }

    var source = new SourceText(File.ReadAllText(path), path);
    var ast = JsParser.ParseScript(source);
    var diagnostics = new FenBrowser.Js.Diagnostics.DiagnosticBag();
    new AstValidator().Validate(ast, diagnostics);

    var payload = new
    {
        ast = DumpProgram(ast),
        diagnostics = diagnostics.Items
    };
    Console.WriteLine(JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
    return 0;
}

Console.Error.WriteLine("Unsupported command.");
return 1;

static object DumpProgram(ProgramNode program)
{
    return new
    {
        type = program.Kind.ToString(),
        body = program.Body.Select(DumpStatement).ToArray(),
        span = DumpSpan(program.Span)
    };
}

static object DumpStatement(StatementNode statement)
{
    return statement switch
    {
        ExpressionStatementNode expr => new
        {
            type = "ExpressionStatement",
            expression = DumpExpression(expr.Expression),
            span = DumpSpan(expr.Span)
        },
        _ => new { type = "UnknownStatement", span = DumpSpan(statement.Span) }
    };
}

static object DumpExpression(ExpressionNode expression)
{
    return expression switch
    {
        IdentifierExpressionNode id => new { type = "Identifier", name = id.Name, span = DumpSpan(id.Span) },
        NumericLiteralExpressionNode number => new { type = "NumericLiteral", value = number.Value, raw = number.RawText, span = DumpSpan(number.Span) },
        StringLiteralExpressionNode str => new { type = "StringLiteral", value = str.Value, raw = str.RawText, span = DumpSpan(str.Span) },
        ParenthesizedExpressionNode paren => new { type = "ParenthesizedExpression", expression = DumpExpression(paren.Expression), span = DumpSpan(paren.Span) },
        BinaryExpressionNode bin => new
        {
            type = "BinaryExpression",
            @operator = bin.Operator,
            left = DumpExpression(bin.Left),
            right = DumpExpression(bin.Right),
            span = DumpSpan(bin.Span)
        },
        _ => new { type = "UnknownExpression", span = DumpSpan(expression.Span) }
    };
}

static object DumpSpan(SourceSpan span) => new { span.Start, span.Length, span.Line, span.Column };
