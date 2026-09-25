using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

// The background JIT compiler and the interpreter both create inline-cache
// sites for the same function. Whoever asks first must win for everyone, or the
// compiled code reads a site the interpreter never fills in.
public sealed class CacheSitePublicationTests
{
    private static BytecodeFunction Compile() =>
        new BytecodeCompiler().CompileScript(new SourceText("var o = { a: 1 }; o.a = o.a + 1; o.a;"));

    [Fact]
    public async Task ConcurrentRequestsForOneSiteGetTheSameInstance()
    {
        for (var round = 0; round < 50; round++)
        {
            var function = Compile();
            using var start = new ManualResetEventSlim();
            var tasks = Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
            {
                start.Wait();
                return (Load: function.EnsureLoadCacheSite(1), Store: function.EnsureStoreCacheSite(1), Array: function.EnsureCallICs());
            })).ToArray();
            start.Set();
            var results = await Task.WhenAll(tasks);

            var first = results[0];
            Assert.All(results, result =>
            {
                Assert.Same(first.Load, result.Load);
                Assert.Same(first.Store, result.Store);
                Assert.Same(first.Array, result.Array);
            });
            Assert.Same(first.Load, function.LoadCacheSites![1]);
            Assert.Same(first.Store, function.StoreCacheSites![1]);
        }
    }

    [Fact]
    public void AnOffsetOutsideTheFunctionGetsADetachedSite()
    {
        var function = Compile();
        var outside = function.EnsureLoadCacheSite(function.InstructionArray.Length + 5);

        Assert.DoesNotContain(outside, function.LoadCacheSites!);
    }

    [Fact]
    public void AnExecutionCopyStartsWithoutFeedback()
    {
        var function = Compile();
        _ = function.EnsureLoadCacheSite(1);

        var copy = function.CreateExecutionCopy();

        Assert.Null(copy.LoadCacheSites);
        Assert.NotSame(function.EnsureLoadCacheSite(1), copy.EnsureLoadCacheSite(1));
    }
}
