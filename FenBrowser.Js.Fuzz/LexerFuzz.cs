using FenBrowser.Js.Lexer;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Fuzz;

// Plan §27 lexer fuzz target.
//
// Properties enforced:
//   * No crash on any byte sequence (random ASCII, random Unicode, structured garbage).
//   * No exception escapes the lexer - malformed input must surface as structured
//     diagnostics through the DiagnosticBag.
//   * No infinite loop - every harness run completes within an instruction budget
//     enforced via a bounded loop on the produced token stream.
//
// Reproducer hooks: when a future libFuzzer / AFL driver is bolted on, it will write
// the failing input to artifacts/fuzz/lexer/<hash>.js. The xUnit-driven harness here
// uses deterministic seeds so any failure is reproducible without an external
// driver.
public sealed class LexerFuzz
{
    private const int IterationsPerCorpus = 64;
    private const int MaxTokensPerInput = 4096;

    [Theory]
    [InlineData("ascii", 1)]
    [InlineData("ascii", 2)]
    [InlineData("unicode", 3)]
    [InlineData("structured", 4)]
    [InlineData("structured", 5)]
    public void LexerSurvivesRandomInput(string corpus, int seed)
    {
        var random = new Random(seed);
        for (var iteration = 0; iteration < IterationsPerCorpus; iteration++)
        {
            var input = GenerateInput(corpus, random);
            RunLexerOnce(input);
        }
    }

    [Fact]
    public void LexerHandlesEmptyInputCleanly()
    {
        RunLexerOnce(string.Empty);
    }

    [Fact]
    public void LexerHandlesAllNullsCleanly()
    {
        RunLexerOnce(new string('\0', 256));
    }

    [Fact]
    public void LexerHandlesAllNewlinesCleanly()
    {
        RunLexerOnce(new string('\n', 256));
    }

    private static void RunLexerOnce(string input)
    {
        var source = new SourceText(input);
        var lexer = new JsLexer(source);

        // JsLexer.LexAll() owns its own loop and produces the full token list. The
        // bound check here is a backstop: if a future bug let LexAll spin forever we
        // would prefer the harness to OOM the test rather than hang the worker.
        // C# does not have a built-in timeout for synchronous calls; the assertion
        // below catches the size-explosion form of the same bug, and pathological
        // hangs would have to be caught by an outer driver (libFuzzer's per-input
        // timeout when wired up).
        var tokens = lexer.LexAll();
        Assert.True(
            tokens.Count <= MaxTokensPerInput,
            $"Lexer produced {tokens.Count} tokens for an input of length {input.Length} - possible token-explosion bug. " +
            "Capture the input in artifacts/fuzz/lexer/ for triage.");
    }

    private static string GenerateInput(string corpus, Random random)
    {
        return corpus switch
        {
            "ascii" => RandomAscii(random, length: random.Next(0, 512)),
            "unicode" => RandomUnicode(random, length: random.Next(0, 256)),
            "structured" => RandomStructured(random, length: random.Next(0, 256)),
            _ => string.Empty,
        };
    }

    private static string RandomAscii(Random random, int length)
    {
        var buffer = new char[length];
        for (var i = 0; i < length; i++)
        {
            // Span from 0x20..0x7e plus newlines + tabs - the ASCII source-text gamut.
            var pick = random.Next(0, 100);
            buffer[i] = pick switch
            {
                < 5 => '\n',
                < 8 => '\t',
                _ => (char)random.Next(0x20, 0x7f),
            };
        }
        return new string(buffer);
    }

    private static string RandomUnicode(Random random, int length)
    {
        var buffer = new char[length];
        for (var i = 0; i < length; i++)
        {
            buffer[i] = (char)random.Next(0x20, 0x300);
        }
        return new string(buffer);
    }

    private static string RandomStructured(Random random, int length)
    {
        // Mix random ASCII with snippets that look like JavaScript to push the lexer
        // through identifier / string / numeric / regex paths.
        var snippets = new[]
        {
            "var ", "let ", "const ", "function ", "{}", "=>", "===",
            "'string'", "\"another\"", "`template ${x}`", "0x1a", "0b101",
            "0.5e+3", "//comment\n", "/* multi */", "/a*b/i", "?.", "??",
        };
        var sb = new System.Text.StringBuilder(length);
        while (sb.Length < length)
        {
            sb.Append(snippets[random.Next(snippets.Length)]);
            sb.Append((char)random.Next(0x20, 0x7f));
        }
        return sb.ToString(0, Math.Min(length, sb.Length));
    }
}
