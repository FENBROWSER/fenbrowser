using System;
using System.IO;
using System.Threading.Tasks;
using FenBrowser.FenEngine.Rendering;
using Xunit;

namespace FenBrowser.Tests.Performance;

[Collection("Performance diagnostics")]
public sealed class ImagePrewarmCriticalPathTests
{
    [Fact]
    public async Task RenderAsyncReturnsBeforeImagePrewarmFetchCompletes()
    {
        using var engine = new CustomHtmlEngine();
        var fetchStarted = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFetch = new TaskCompletionSource<Stream>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var renderTask = engine.RenderAsync(
            """
            <!doctype html>
            <html>
              <head><style>.hero { background-image: url('/background.png'); }</style></head>
              <body><img src="/hero.png"><div class="hero">hero</div></body>
            </html>
            """,
            new Uri("https://images.test/"),
            _ => Task.FromResult(string.Empty),
            _ =>
            {
                fetchStarted.TrySetResult(true);
                return releaseFetch.Task;
            },
            _ => { },
            viewportWidth: 320,
            viewportHeight: 200,
            forceJavascript: false);

        var renderCompleted = await Task.WhenAny(
            renderTask,
            Task.Delay(TimeSpan.FromSeconds(3)));
        Assert.Same(renderTask, renderCompleted);
        Assert.NotNull(await renderTask);

        var prewarmStarted = await Task.WhenAny(
            fetchStarted.Task,
            Task.Delay(TimeSpan.FromSeconds(2)));
        Assert.Same(fetchStarted.Task, prewarmStarted);

        releaseFetch.TrySetResult(null);
    }
}
