using System.Linq;
using System.Text;
using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Runtime;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

public sealed class RegisterReuseTests
{
    private static BytecodeFunction Compile(string source)
        => new BytecodeCompiler().CompileScript(new SourceText(source));

    private static JsValue Run(string source)
    {
        var function = Compile(source);
        new BytecodeVerifier().Verify(function);
        return new BytecodeInterpreter().Execute(function);
    }

    private static string ManyStatements(string template, int count)
    {
        var builder = new StringBuilder();
        for (var i = 0; i < count; i++)
        {
            builder.AppendLine(string.Format(template, i));
        }

        return builder.ToString();
    }

    [Fact]
    public void VariableDeclarationsDoNotGrowTheRegisterFile()
    {
        // A register only ever holds an expression temporary, so a hundred
        // declarations in a row should cost the same file as one of them.
        var one = Compile("var v0 = (0 * 1) + (2 / 3) - (4 % 5);");
        var many = Compile(ManyStatements("var v{0} = ({0} * 1) + (2 / 3) - (4 % 5);", 100));

        Assert.True(
            many.RegisterCount <= one.RegisterCount + 2,
            $"register file grew with statement count: 1 statement = {one.RegisterCount}, " +
            $"100 statements = {many.RegisterCount}");
    }

    [Fact]
    public void ReturnStatementsDoNotGrowTheRegisterFile()
    {
        var many = Compile(
            "function f(a, b) {" +
            "  if (a) return (a * 1) + (b / 2) - (a % 3);" +
            ManyStatements("  if (a === {0}) return (a * {0}) + (b / 2) - (a % 3);", 100) +
            "  return 0;" +
            "}" +
            "f(1, 2);");

        // The body is a hundred near-identical returns; nothing is live across
        // them, so the file stays close to the widest single expression.
        Assert.True(many.RegisterCount < 40, $"register file was {many.RegisterCount}");
    }

    [Fact]
    public void ReusedRegistersDoNotClobberLiveValues()
    {
        // The loop counter, accumulator and iterator are held by enclosing
        // constructs across the statements that reuse registers beneath them.
        Assert.Equal(
            45d,
            Run(
                "var total = 0;" +
                "for (var i = 0; i < 10; i++) {" +
                "  var doubled = i * 2;" +
                "  var halved = doubled / 2;" +
                "  total = total + halved;" +
                "}" +
                "total;").AsNumber());
    }

    [Fact]
    public void ReuseSurvivesNestedControlFlow()
    {
        Assert.Equal(
            "a1b2c3",
            Run(
                "var out = '';" +
                "var items = ['a', 'b', 'c'];" +
                "for (var i = 0; i < items.length; i++) {" +
                "  var letter = items[i];" +
                "  if (letter) {" +
                "    var n = i + 1;" +
                "    try { out = out + letter + n; } catch (e) { out = 'err'; }" +
                "  }" +
                "}" +
                "out;").AsString());
    }

    [Fact]
    public void ReuseSurvivesGeneratorSuspension()
    {
        // A generator's register file is saved and restored across a yield, so
        // reuse must not have handed a live value's slot to a later statement.
        Assert.Equal(
            "1|2|3",
            Run(
                "function* g() { var a = 1; yield a; var b = a + 1; yield b; var c = b + 1; yield c; }" +
                "var parts = [];" +
                "for (var v of g()) { parts.push(v); }" +
                "parts.join('|');").AsString());
    }

    [Fact]
    public void ReuseSurvivesClosureCapture()
    {
        // Closures capture environment slots, never registers, so reusing a
        // register after the statement that made the closure is safe.
        Assert.Equal(
            30d,
            Run(
                "var fns = [];" +
                "for (var i = 0; i < 3; i++) {" +
                "  var base = (i + 1) * 10;" +
                "  fns.push(function () { return base; });" +
                "}" +
                "fns[2]();").AsNumber());
    }

    [Fact]
    public void CompletionValueSurvivesReuse()
    {
        // The completion register is register 0, below every statement mark.
        Assert.Equal(7d, Run("var a = 3; var b = 4; a + b;").AsNumber());
    }
}
