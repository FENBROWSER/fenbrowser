using System.Reflection;
using System.Text.Json;
using FenBrowser.Js.Lexer;
using FenBrowser.Js.Source;

var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0";

if (args.Length == 0)
{
    Console.Error.WriteLine("Usage: fenjs --version | --eval <code> | --dump-tokens <file>");
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

Console.Error.WriteLine("Unsupported command.");
return 1;
