using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

/// <summary>
/// Calling a missing member must name it (ECMA-262 does not fix the message text,
/// but pages and people read it), matching the "x.foo is not a function" shape.
/// </summary>
public class CalleeNameInTypeErrorTests
{
    private static string RunString(string src)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(src));
        new BytecodeVerifier().Verify(fn);
        return new BytecodeInterpreter().Execute(fn).AsString();
    }

    [Fact]
    public void MissingMethodOnVariableNamesReceiverAndProperty()
    {
        var msg = RunString("var doc = {}; try { doc.createNodeIterator(); } catch (e) { e.message }");
        Assert.StartsWith("doc.createNodeIterator is not a function", msg);
    }

    [Fact]
    public void MissingMethodOnChainedPropertyNamesWholeChain()
    {
        var msg = RunString("var o = { a: {} }; try { o.a.foo(1, 2); } catch (e) { e.message }");
        Assert.StartsWith("o.a.foo is not a function", msg);
    }

    [Fact]
    public void UndefinedVariableCalleeNamesTheVariable()
    {
        var msg = RunString("var f; try { f(); } catch (e) { e.message }");
        Assert.StartsWith("f is not a function", msg);
    }
}
