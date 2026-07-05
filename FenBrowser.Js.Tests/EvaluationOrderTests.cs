using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Runtime;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

public sealed class EvaluationOrderTests
{
    private static JsValue Run(string source)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        return new BytecodeInterpreter().Execute(fn);
    }

    [Fact]
    public void NewCalleeCommaExpressionInitializesConstructorBeforeConstruct()
    {
        var result = Run(@"
            var r = {};
            (function(target, w) {
                target.L = (
                    new ((w = function(d, a) { this.d = d; this.a = a; }, w).pD = new w(1, 2), w)(8, 9),
                    new w(7, 10),
                    w
                );
            })(r);
            '' + r.L.pD.d + ',' + r.L.pD.a + ',' + (new r.L(3, 4)).a;
        ");

        Assert.Equal("1,2,4", result.AsString());
    }

    [Fact]
    public void CommaExpressionMemberAssignmentUsesPriorIdentifierAssignment()
    {
        var result = Run(@"
            var r = {};
            (function(target, w) {
                target.D = (
                    (w = function(d, a) { this.d = d; this.a = a; }, w.D = new w(0, 1), w).gu = new w(2, 3),
                    w.Ev = new w(4, 5),
                    w
                );
            })(r);
            '' + r.D.D.a + ',' + r.D.gu.d + ',' + r.D.Ev.a;
        ");

        Assert.Equal("1,2,5", result.AsString());
    }

    [Fact]
    public void MemberAssignmentEvaluatesObjectAndKeyBeforeRightHandSide()
    {
        var result = Run(@"
            var log = '';
            var target = {};
            function getObject() { log = log + 'O'; return target; }
            function getKey() { log = log + 'K'; return 'value'; }
            function getValue() { log = log + 'R'; return 42; }
            getObject()[getKey()] = getValue();
            log + ':' + target.value;
        ");

        Assert.Equal("OKR:42", result.AsString());
    }
}
