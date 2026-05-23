using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Runtime;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

public sealed class SetPredicateTests
{
    private static bool RunBool(string source)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        return new BytecodeInterpreter().Execute(fn).AsBoolean();
    }

    [Fact] public void SubsetTrue() => Assert.True(RunBool("new Set([1,2]).isSubsetOf(new Set([1,2,3]));"));
    [Fact] public void SubsetFalseBecauseLarger() => Assert.False(RunBool("new Set([1,2,3]).isSubsetOf(new Set([1]));"));
    [Fact] public void SubsetFalseBecauseMissing() => Assert.False(RunBool("new Set([1,4]).isSubsetOf(new Set([1,2,3]));"));
    [Fact] public void SubsetEqualSetsTrue() => Assert.True(RunBool("new Set([1,2]).isSubsetOf(new Set([1,2]));"));

    [Fact] public void SupersetTrue() => Assert.True(RunBool("new Set([1,2,3]).isSupersetOf(new Set([1,2]));"));
    [Fact] public void SupersetFalse() => Assert.False(RunBool("new Set([1,2]).isSupersetOf(new Set([1,2,3]));"));

    [Fact] public void DisjointTrue() => Assert.True(RunBool("new Set([1,2]).isDisjointFrom(new Set([3,4]));"));
    [Fact] public void DisjointFalse() => Assert.False(RunBool("new Set([1,2]).isDisjointFrom(new Set([2,3]));"));
}
