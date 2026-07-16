using FenBrowser.FenEngine.Rendering;

namespace FenBrowser.Tests.Scripting;

[Collection("Engine Tests")]
public sealed class CustomHtmlEngineNavigationGenerationTests
{
    [Fact]
    public async Task OlderRender_CannotPublishAfterNewerRenderCompletes()
    {
        var firstCssRequested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstCss = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var engine = new CustomHtmlEngine
        {
            EnableJavaScript = false,
            EnableIncrementalParseRepaint = false
        };

        var firstRender = engine.RenderAsync(
            "<html><head><link rel='stylesheet' href='slow.css'></head><body><p id='first'>first</p></body></html>",
            new Uri("https://first.example/"),
            async _ =>
            {
                firstCssRequested.TrySetResult();
                await releaseFirstCss.Task;
                return "#first { color: red; }";
            },
            static _ => Task.FromResult<Stream>(Stream.Null),
            static _ => { },
            800,
            600);

        await firstCssRequested.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await engine.RenderAsync(
            "<html><body><p id='second'>second</p></body></html>",
            new Uri("https://second.example/"),
            static _ => Task.FromResult(string.Empty),
            static _ => Task.FromResult<Stream>(Stream.Null),
            static _ => { },
            800,
            600);

        releaseFirstCss.TrySetResult();
        await firstRender.WaitAsync(TimeSpan.FromSeconds(5));

        var snapshot = engine.GetRenderSnapshot();
        Assert.NotNull(snapshot.Root?.QuerySelector("#second"));
        Assert.Null(snapshot.Root?.QuerySelector("#first"));
        Assert.All(snapshot.Styles.Keys, node => Assert.Same(snapshot.Root?.OwnerDocument, node.OwnerDocument));
        Assert.Equal("https://second.example/", engine.LastRenderTelemetry.Url);
    }
}
