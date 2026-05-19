using System.Reflection;

var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0";

if (args.Length == 0)
{
    Console.Error.WriteLine("Usage: fenjs --version | --eval <code>");
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

Console.Error.WriteLine("Unsupported command.");
return 1;
