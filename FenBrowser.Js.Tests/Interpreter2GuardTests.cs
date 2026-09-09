using System.Diagnostics;
using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Interpreter2;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

/// <summary>
/// A runaway script must stop, whatever shape it takes.
/// </summary>
/// <remarks>
/// The register-window loop samples the wall-clock deadline rather than testing
/// it on every instruction, and where it samples has now been changed twice -
/// once because a per-entry countdown could never fire, and once to move the
/// check off the per-instruction path entirely. Both times the failure would
/// have been a page that cannot be stopped, which is the worst thing this loop
/// could do, and neither time was there a test saying so.
///
/// The awkward shape is a native builtin driving a short JavaScript callback:
/// the loop is re-entered for every call, so nothing accumulates inside one
/// entry and there is no back edge to hang a check on.
/// </remarks>
[Collection(nameof(Interpreter2ParityTests))]
public sealed class Interpreter2GuardTests
{
    private const int DeadlineMs = 400;

    /// <summary>Generous: the point is that it stops at all, not exactly when.</summary>
    private const int AllowedMs = 30_000;

    [Theory]
    // A loop in JavaScript: the back edge is the obvious place to check.
    [InlineData("while (true) { }")]
    // A loop whose body is a call, so the check must survive entering a frame.
    [InlineData("function step() { return 1; } while (true) { step(); }")]
    // Unbounded recursion, which has no back edge at all.
    [InlineData("function down(n) { return down(n + 1); } down(0);")]
    // A native driving a short callback: the loop is re-entered per call, so
    // nothing accumulates within one entry and there is no back edge either.
    [InlineData("var p = new Proxy([], { get: function (_, k) " +
                "{ return k === 'length' ? 2147483647 : 1; } }); " +
                "Array.prototype.indexOf.call(p, 'never-there');")]
    [InlineData("var p = new Proxy([], { get: function (_, k) " +
                "{ return k === 'length' ? 2147483647 : 1; } }); " +
                "Array.prototype.lastIndexOf.call(p, 'never-there');")]
    public void TheDeadlineStopsIt(string source)
    {
        foreach (var onNewLoop in new[] { false, true })
        {
            var previous = Interp2Options.Enabled;
            Interp2Options.Enabled = onNewLoop;
            try
            {
                var function = new BytecodeCompiler().CompileScript(new SourceText(source));
                new BytecodeVerifier().Verify(function);
                var interpreter = new BytecodeInterpreter { WallClockTimeoutMs = DeadlineMs };

                var watch = Stopwatch.StartNew();
                Assert.ThrowsAny<Exception>(() => interpreter.Execute(function));
                watch.Stop();

                Assert.True(
                    watch.ElapsedMilliseconds < AllowedMs,
                    $"{(onNewLoop ? "new" : "old")} loop ran {watch.ElapsedMilliseconds}ms " +
                    $"against a {DeadlineMs}ms deadline");
            }
            finally
            {
                Interp2Options.Enabled = previous;
            }
        }
    }
}
