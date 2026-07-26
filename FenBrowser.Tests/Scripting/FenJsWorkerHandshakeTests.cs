using FenBrowser.FenEngine.Scripting;
using Xunit;

namespace FenBrowser.Tests.Scripting;

public sealed class FenJsWorkerHandshakeTests
{
    [Fact]
    public async Task ConstructorAndEvaluation_CompleteWithoutWorkerDeadlock()
    {
        var result = await Task.Run(() =>
        {
            var engine = new FenJsBrowserScriptEngine(
                new JsHostAdapter(
                    navigate: _ => { },
                    post: (_, _) => { },
                    status: _ => { },
                    log: _ => { }));

            return engine.Evaluate("String(1 + 1)")?.ToString();
        }).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("2", result);
    }
}
