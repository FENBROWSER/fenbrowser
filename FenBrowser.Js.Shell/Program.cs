using System.Reflection;
using System.Text.Json;
using FenBrowser.Js.Ast;
using FenBrowser.Js.AstValidation;
using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Heap;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Lexer;
using FenBrowser.Js.Parser;
using FenBrowser.Js.Runtime;
using FenBrowser.Js.Source;
using FenBrowser.Js.Test262;

var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0";

if (args.Length == 0)
{
    Console.Error.WriteLine("Usage: fenjs --version | --eval [code] | --file <file> | --test262 <path> | --test262-file <file> | --dump-tokens <file> | --dump-ast <file> | --dump-bytecode <file> [--gc-before-every-alloc|--gc-after-every-alloc|--gc-random|--verify-heap-before-gc|--verify-heap-after-gc|--trace-gc]");
    return 1;
}

var gcStressMode = ParseGcStressMode(args);
var verifyBeforeGc = args.Contains("--verify-heap-before-gc", StringComparer.Ordinal);
var verifyAfterGc = args.Contains("--verify-heap-after-gc", StringComparer.Ordinal);
var traceGc = args.Contains("--trace-gc", StringComparer.Ordinal);

if (args.Length == 1 && args[0] == "--version")
{
    Console.WriteLine($"fenjs {version}");
    return 0;
}

if (args.Length >= 1 && args[0] == "--eval")
{
    var code = args.Length > 1 ? string.Join(' ', args.Skip(1)) : string.Empty;
    return ExecuteSource(code, "<eval>", gcStressMode, verifyBeforeGc, verifyAfterGc, traceGc);
}

if (args.Length == 2 && args[0] == "--file")
{
    var path = args[1];
    if (!File.Exists(path))
    {
        Console.Error.WriteLine($"File not found: {path}");
        return 4;
    }

    return ExecuteSource(File.ReadAllText(path), path, gcStressMode, verifyBeforeGc, verifyAfterGc, traceGc);
}

if (args.Length >= 1 && args[0] == "--test262")
{
    return RunTest262FromShell(args, singleFile: false);
}

if (args.Length >= 1 && args[0] == "--test262-file")
{
    return RunTest262FromShell(args, singleFile: true);
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

#pragma warning disable IL2026, IL3050 // JSON of anonymous types in dev-tool mode
    Console.WriteLine(JsonSerializer.Serialize(tokens, new JsonSerializerOptions { WriteIndented = true }));
#pragma warning restore IL2026, IL3050
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
    try
    {
        var ast = JsParser.ParseScript(source);
        var diagnostics = new FenBrowser.Js.Diagnostics.DiagnosticBag();
        new AstValidator().Validate(ast, diagnostics);

        var payload = new
        {
            ast = DumpProgram(ast),
            diagnostics = diagnostics.Items
        };
#pragma warning disable IL2026, IL3050 // JSON of anonymous types in dev-tool mode
        Console.WriteLine(JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
#pragma warning restore IL2026, IL3050
        return 0;
    }
    catch (UnsupportedFeatureException ex)
    {
        WriteError("unsupported", ex.Message, path);
        return 21;
    }
    catch (JsParserException ex)
    {
        WriteError("parse", ex.Message, path);
        return 22;
    }
    catch (Exception ex)
    {
        WriteError("runtime-fatal", ex.Message, path);
        return 25;
    }
}

if (args.Length == 2 && args[0] == "--dump-bytecode")
{
    var path = args[1];
    if (!File.Exists(path))
    {
        Console.Error.WriteLine($"File not found: {path}");
        return 4;
    }

    try
    {
        var compiler = new BytecodeCompiler();
        var function = compiler.CompileScript(new SourceText(File.ReadAllText(path), path));
        new BytecodeVerifier().Verify(function);
        var payload = new
        {
            registers = function.RegisterCount,
            constants = function.Constants.Select((c, i) => new { index = i, value = FormatValue(c), tag = c.Tag.ToString() }).ToArray(),
            variables = function.VariableSlots.OrderBy(kv => kv.Value).Select(kv => new { name = kv.Key, slot = kv.Value }).ToArray(),
            instructions = function.Instructions.Select((ins, ip) => new { ip, op = ins.OpCode.ToString(), ins.A, ins.B, ins.C, ins.D }).ToArray()
        };
#pragma warning disable IL2026, IL3050 // JSON of anonymous types in dev-tool mode
        Console.WriteLine(JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
#pragma warning restore IL2026, IL3050
        return 0;
    }
    catch (UnsupportedFeatureException ex)
    {
        WriteError("unsupported", ex.Message, path);
        return 21;
    }
    catch (JsParserException ex)
    {
        WriteError("parse", ex.Message, path);
        return 22;
    }
    catch (InvalidOperationException ex)
    {
        WriteError("compile", ex.Message, path);
        return 24;
    }
    catch (Exception ex)
    {
        WriteError("runtime-fatal", ex.Message, path);
        return 25;
    }
}

Console.Error.WriteLine("Unsupported command.");
return 1;

static GcStressMode ParseGcStressMode(string[] rawArgs)
{
    if (rawArgs.Contains("--gc-before-every-alloc", StringComparer.Ordinal))
    {
        return GcStressMode.BeforeEveryAlloc;
    }

    if (rawArgs.Contains("--gc-after-every-alloc", StringComparer.Ordinal))
    {
        return GcStressMode.AfterEveryAlloc;
    }

    if (rawArgs.Contains("--gc-random", StringComparer.Ordinal))
    {
        return GcStressMode.Random;
    }

    return GcStressMode.None;
}

static int ExecuteSource(
    string code,
    string sourceName,
    GcStressMode gcStressMode,
    bool verifyBeforeGc,
    bool verifyAfterGc,
    bool traceGc)
{
    var heap = new JsHeap(
        gcStressMode,
        verifyHeapBeforeGc: verifyBeforeGc,
        verifyHeapAfterGc: verifyAfterGc);
    var isolate = new JsIsolate(heap);
    using var scope = isolate.EnterHandleScope();
    _ = isolate.AllocateObjectInScope(scope, new FenBrowser.Js.Objects.JsObject(), AllocationSite.Current());
    if (verifyBeforeGc || verifyAfterGc)
    {
        heap.CollectGarbage();
    }

    if (traceGc)
    {
        Console.Error.WriteLine($"[trace-gc] mode={gcStressMode} collections={heap.GcCollectionCount} marked={heap.LastGcMarkedCells} swept={heap.LastGcSweptCells} live={heap.LiveCellCount}");
    }

    var compiler = new BytecodeCompiler();
    try
    {
        var function = compiler.CompileScript(new SourceText(code, sourceName));
        new BytecodeVerifier().Verify(function);
        var result = new BytecodeInterpreter().Execute(function);
        Console.WriteLine(FormatValue(result));
    }
    catch (UnsupportedFeatureException ex)
    {
        WriteError("unsupported", ex.Message, sourceName);
        return 21;
    }
    catch (JsParserException ex)
    {
        WriteError("parse", ex.Message, sourceName);
        return 22;
    }
    catch (JsThrownException ex)
    {
        WriteError("runtime-throw", $"Uncaught throw: {FormatValue(ex.Value)}", sourceName);
        return 23;
    }
    catch (InvalidOperationException ex)
    {
        WriteError("compile", ex.Message, sourceName);
        return 24;
    }
    catch (Exception ex)
    {
        WriteError("runtime-fatal", ex.Message, sourceName);
        return 25;
    }

    return 0;
}

static int RunTest262FromShell(string[] rawArgs, bool singleFile)
{
    if (rawArgs.Length < 2 || string.IsNullOrWhiteSpace(rawArgs[1]))
    {
        Console.Error.WriteLine(singleFile ? "--test262-file requires a file path." : "--test262 requires a test262 root, directory, or file path.");
        return 2;
    }

    var target = rawArgs[1];
    string? explicitRoot = null;
    var outputPath = Path.Combine("Results", "test262", singleFile ? "fenjs-test262-file.json" : "fenjs-test262.json");
    var max = 200;
    var timeoutMs = 5000;
    var engine = "FenJS";
    string? expectationsPath = null;
    string? featuresCsv = null;
    string? supportedFeaturesCsv = null;
    var list = false;
    var dryRun = false;
    var parserSubset = false;
    var runtimeSubset = false;

    for (var i = 2; i < rawArgs.Length; i++)
    {
        switch (rawArgs[i])
        {
            case "--root" when i + 1 < rawArgs.Length:
            case "--test262-root" when i + 1 < rawArgs.Length:
                explicitRoot = rawArgs[++i];
                break;
            case "--out" when i + 1 < rawArgs.Length:
            case "--output" when i + 1 < rawArgs.Length:
                outputPath = rawArgs[++i];
                break;
            case "--max" when i + 1 < rawArgs.Length && int.TryParse(rawArgs[i + 1], out var parsedMax):
                max = parsedMax;
                i++;
                break;
            case "--timeout-ms" when i + 1 < rawArgs.Length && int.TryParse(rawArgs[i + 1], out var parsedTimeout):
                timeoutMs = parsedTimeout;
                i++;
                break;
            case "--engine" when i + 1 < rawArgs.Length:
                engine = rawArgs[++i];
                break;
            case "--expectations" when i + 1 < rawArgs.Length:
                expectationsPath = rawArgs[++i];
                break;
            case "--features" when i + 1 < rawArgs.Length:
                featuresCsv = rawArgs[++i];
                break;
            case "--supported-features" when i + 1 < rawArgs.Length:
                supportedFeaturesCsv = rawArgs[++i];
                break;
            case "--list":
                list = true;
                break;
            case "--dry-run":
                dryRun = true;
                break;
            case "--parser-subset":
                parserSubset = true;
                break;
            case "--runtime-subset":
                runtimeSubset = true;
                break;
        }
    }

    if (!list && !dryRun && !parserSubset && !runtimeSubset)
    {
        runtimeSubset = true;
    }

    var root = ResolveTest262Root(explicitRoot, target);
    if (!Directory.Exists(root))
    {
        Console.Error.WriteLine($"test262 root not found: {root}");
        return 3;
    }

    var runner = new Test262Runner();
    return runner.Run(
        root,
        list,
        dryRun,
        parserSubset,
        runtimeSubset,
        dashboard: false,
        verifyGates: false,
        outputPath,
        max,
        timeoutMs,
        engine,
        expectationsPath,
        inputPath: null,
        previousPath: null,
        test262Path: singleFile ? null : target,
        test262File: singleFile ? target : null,
        featuresCsv,
        supportedFeaturesCsv);
}

static string ResolveTest262Root(string? explicitRoot, string targetPath)
{
    if (!string.IsNullOrWhiteSpace(explicitRoot))
    {
        return explicitRoot;
    }

    var candidate = Path.GetFullPath(targetPath);
    if (Directory.Exists(Path.Combine(candidate, "test")) && Directory.Exists(Path.Combine(candidate, "harness")))
    {
        return candidate;
    }

    var directory = File.Exists(candidate) ? Path.GetDirectoryName(candidate) : candidate;
    while (!string.IsNullOrWhiteSpace(directory))
    {
        if (Directory.Exists(Path.Combine(directory, "test")) && Directory.Exists(Path.Combine(directory, "harness")))
        {
            return directory;
        }

        directory = Directory.GetParent(directory)?.FullName;
    }

    return Path.Combine("external", "test262");
}

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
        BlockStatementNode block => new
        {
            type = "BlockStatement",
            statements = block.Statements.Select(DumpStatement).ToArray(),
            span = DumpSpan(block.Span)
        },
        VariableDeclarationStatementNode decl => new
        {
            type = "VariableDeclaration",
            kind = decl.Kind,
            declarations = decl.Declarators.Select(d => new
            {
                type = "VariableDeclarator",
                id = d.Identifier,
                init = d.Initializer is null ? null : DumpExpression(d.Initializer),
                span = DumpSpan(d.Span)
            }).ToArray(),
            span = DumpSpan(decl.Span)
        },
        IfStatementNode ifs => new
        {
            type = "IfStatement",
            test = DumpExpression(ifs.Test),
            consequent = DumpStatement(ifs.Consequent),
            alternate = ifs.Alternate is null ? null : DumpStatement(ifs.Alternate),
            span = DumpSpan(ifs.Span)
        },
        WhileStatementNode ws => new
        {
            type = "WhileStatement",
            test = DumpExpression(ws.Test),
            body = DumpStatement(ws.Body),
            span = DumpSpan(ws.Span)
        },
        ReturnStatementNode ret => new
        {
            type = "ReturnStatement",
            argument = ret.Argument is null ? null : DumpExpression(ret.Argument),
            span = DumpSpan(ret.Span)
        },
        FunctionDeclarationNode fn => new
        {
            type = "FunctionDeclaration",
            name = fn.Name,
            parameters = fn.Parameters.ToArray(),
            body = DumpStatement(fn.Body),
            span = DumpSpan(fn.Span)
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
        BooleanLiteralExpressionNode boolean => new { type = "BooleanLiteral", value = boolean.Value, raw = boolean.RawText, span = DumpSpan(boolean.Span) },
        NullLiteralExpressionNode n => new { type = "NullLiteral", raw = n.RawText, span = DumpSpan(n.Span) },
        ParenthesizedExpressionNode paren => new { type = "ParenthesizedExpression", expression = DumpExpression(paren.Expression), span = DumpSpan(paren.Span) },
        BinaryExpressionNode bin => new
        {
            type = "BinaryExpression",
            @operator = bin.Operator,
            left = DumpExpression(bin.Left),
            right = DumpExpression(bin.Right),
            span = DumpSpan(bin.Span)
        },
        AssignmentExpressionNode assign => new
        {
            type = "AssignmentExpression",
            left = DumpExpression(assign.Left),
            right = DumpExpression(assign.Right),
            span = DumpSpan(assign.Span)
        },
        CallExpressionNode call => new
        {
            type = "CallExpression",
            callee = DumpExpression(call.Callee),
            arguments = call.Arguments.Select(DumpExpression).ToArray(),
            span = DumpSpan(call.Span)
        },
        ArrowFunctionExpressionNode arrow => new
        {
            type = "ArrowFunctionExpression",
            parameters = arrow.Parameters.ToArray(),
            blockBody = arrow.BlockBody is null ? null : DumpStatement(arrow.BlockBody),
            expressionBody = arrow.ExpressionBody is null ? null : DumpExpression(arrow.ExpressionBody),
            span = DumpSpan(arrow.Span)
        },
        ObjectLiteralExpressionNode obj => new
        {
            type = "ObjectLiteral",
            properties = obj.Properties.Select(p => new
            {
                key = p.Key,
                computed = p.IsComputed,
                computedKey = p.ComputedKey is null ? null : DumpExpression(p.ComputedKey),
                value = DumpExpression(p.Value),
                span = DumpSpan(p.Span)
            }).ToArray(),
            span = DumpSpan(obj.Span)
        },
        ArrayLiteralExpressionNode arr => new
        {
            type = "ArrayLiteral",
            elements = arr.Elements.Select(DumpExpression).ToArray(),
            span = DumpSpan(arr.Span)
        },
        MemberExpressionNode mem => new
        {
            type = "MemberExpression",
            @object = DumpExpression(mem.Object),
            property = mem.Computed ? null : mem.Property,
            computed = mem.Computed,
            propertyExpression = mem.PropertyExpression is null ? null : DumpExpression(mem.PropertyExpression),
            span = DumpSpan(mem.Span)
        },
        UnaryExpressionNode unary => new
        {
            type = "UnaryExpression",
            @operator = unary.Operator,
            operand = DumpExpression(unary.Operand),
            span = DumpSpan(unary.Span)
        },
        ConditionalExpressionNode cond => new
        {
            type = "ConditionalExpression",
            test = DumpExpression(cond.Test),
            consequent = DumpExpression(cond.Consequent),
            alternate = DumpExpression(cond.Alternate),
            span = DumpSpan(cond.Span)
        },
        FunctionExpressionNode fn => new
        {
            type = "FunctionExpression",
            name = fn.Name,
            parameters = fn.Parameters.ToArray(),
            body = DumpStatement(fn.Body),
            span = DumpSpan(fn.Span)
        },
        NewExpressionNode ne => new
        {
            type = "NewExpression",
            callee = DumpExpression(ne.Callee),
            arguments = ne.Arguments.Select(DumpExpression).ToArray(),
            span = DumpSpan(ne.Span)
        },
        RegexLiteralExpressionNode re => new
        {
            type = "RegexLiteral",
            raw = re.RawText,
            span = DumpSpan(re.Span)
        },
        _ => new { type = "UnknownExpression", span = DumpSpan(expression.Span) }
    };
}

static object DumpSpan(SourceSpan span) => new { span.Start, span.Length, span.Line, span.Column };

static string FormatValue(JsValue value)
{
    return value.Tag switch
    {
        JsValueTag.Undefined => "undefined",
        JsValueTag.Null => "null",
        JsValueTag.Boolean => value.AsBoolean() ? "true" : "false",
        JsValueTag.Int32 => value.AsInt32().ToString(System.Globalization.CultureInfo.InvariantCulture),
        JsValueTag.Number => value.AsNumber().ToString("R", System.Globalization.CultureInfo.InvariantCulture),
        JsValueTag.String => value.AsString(),
        JsValueTag.Object => "[object]",
        JsValueTag.HostObject => "[host-object]",
        _ => value.Tag.ToString()
    };
}

static void WriteError(string kind, string message, string source)
{
    var payload = new ShellErrorPayload
    {
        Error = new ShellErrorInfo
        {
            Kind = kind,
            Message = message,
            Source = source
        }
    };

    Console.Error.WriteLine(JsonSerializer.Serialize(payload, Test262JsonContext.Default.ShellErrorPayload));
}
