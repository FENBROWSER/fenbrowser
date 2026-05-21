using FenBrowser.Js.AstValidation;
using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Parser;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Fuzz;

// Plan §27 runtime fuzz target.
//
// Two complementary modes:
//   * Random-noise: parse + compile + verify on random byte sequences. The pipeline
//     must reject with a structured exception (JsParserException,
//     UnsupportedFeatureException, or InvalidOperationException from the verifier).
//     No unhandled exception type is permitted to escape.
//   * Bounded-valid: a curated corpus of small terminating programs runs the full
//     pipeline through to a successful BytecodeInterpreter.Execute. This catches
//     interpreter regressions that fail on legitimate code while the noise mode
//     exercises hardening.
//
// We deliberately do NOT fuzz random source through the interpreter: the engine has
// no instruction budget yet (plan §14.2 gap), so an unbounded `while(true){}` would
// hang the test process. Bounded execution lives on the curated corpus only until
// the interpreter gains a budget setting in a later commit.
public sealed class RuntimeFuzz
{
    private const int IterationsPerSeed = 32;

    [Theory]
    [InlineData(101)]
    [InlineData(102)]
    [InlineData(103)]
    public void NoisePipelineRejectsCleanly(int seed)
    {
        var random = new Random(seed);
        var compiler = new BytecodeCompiler();
        var verifier = new BytecodeVerifier();
        for (var iteration = 0; iteration < IterationsPerSeed; iteration++)
        {
            var input = RandomNoise(random);
            TryCompileAndVerify(compiler, verifier, input);
        }
    }

    [Theory]
    [MemberData(nameof(BoundedValidPrograms))]
    public void BoundedValidProgramsRunEndToEnd(string source)
    {
        var compiler = new BytecodeCompiler();
        var fn = compiler.CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        var result = new BytecodeInterpreter().Execute(fn);

        // Result intentionally unchecked - the property under test is "this snippet
        // does not throw an unexpected exception". Snippet-specific value checks
        // belong in ObjectAndBytecodeTests, not in fuzz.
        _ = result;
    }

    public static IEnumerable<object[]> BoundedValidPrograms()
    {
        yield return new object[] { "1 + 2;" };
        yield return new object[] { "let x = 1; x = x + 1; x;" };
        yield return new object[] { "function add(a, b) { return a + b; } add(2, 3);" };
        yield return new object[] { "let s = 'hello'; s + ' world';" };
        yield return new object[] { "let o = { a: 1, b: 2 }; o.a + o.b;" };
        yield return new object[] { "let a = [1, 2, 3]; a.length;" };
        yield return new object[] { "let r = 0; for (let i = 0; i < 5; i = i + 1) { r = r + i; } r;" };
        yield return new object[] { "let n = 0; while (n < 3) { n = n + 1; } n;" };
        yield return new object[] { "try { throw 1; } catch (e) { e + 1; }" };
        yield return new object[] { "let f = (x) => x * 2; f(21);" };
    }

    private static void TryCompileAndVerify(BytecodeCompiler compiler, BytecodeVerifier verifier, string input)
    {
        try
        {
            var fn = compiler.CompileScript(new SourceText(input));
            verifier.Verify(fn);
        }
        catch (JsParserException)
        {
            // Expected: malformed input rejected at parse time.
        }
        catch (UnsupportedFeatureException)
        {
            // Expected: syntactically valid but engine-unsupported feature.
        }
        catch (InvalidOperationException)
        {
            // Expected: verifier caught an invariant violation produced by a partly-
            // formed compile output. Counts as a clean rejection path.
        }
    }

    private static string RandomNoise(Random random)
    {
        var length = random.Next(0, 256);
        var pieces = new[]
        {
            "let ", "const ", "function ", "{", "}", "(", ")", "[", "]",
            ";", ",", "=", "+", "-", "*", "/", " ", "'x'", "1",
            "return ", "throw ", "try{", "}catch(e){", "}", "if(", ")",
        };
        var sb = new System.Text.StringBuilder(length);
        while (sb.Length < length)
        {
            sb.Append(pieces[random.Next(pieces.Length)]);
            if (random.Next(0, 6) == 0)
            {
                sb.Append((char)random.Next(0x20, 0x7f));
            }
        }
        return sb.ToString(0, Math.Min(length, sb.Length));
    }
}
