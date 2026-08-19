using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Rendering;
using Xunit;

namespace FenBrowser.Tests.Performance;

[Collection("Performance diagnostics")]
public sealed class CssDocumentIsolationTests
{
    [Fact]
    public async Task ConcurrentDocuments_KeepVariablesAndKeyframesIsolated()
    {
        CssLoader.ClearCaches();
        var first = Parse("red", "0.1");
        var second = Parse("blue", "0.9");

        await Task.WhenAll(
            CssLoader.ComputeWithResultAsync(first.Root, first.BaseUri, null, 800, 600),
            CssLoader.ComputeWithResultAsync(second.Root, second.BaseUri, null, 1200, 900));

        var firstFrames = CssLoader.GetKeyframes("pulse", first.Target);
        var secondFrames = CssLoader.GetKeyframes("pulse", second.Target);
        Assert.Equal("0.1", firstFrames.Frames[0].Properties["opacity"]);
        Assert.Equal("0.9", secondFrames.Frames[0].Properties["opacity"]);
        Assert.NotSame(firstFrames, secondFrames);
    }

    [Fact]
    public async Task UnrelatedDocuments_DoNotShareAComputeGate()
    {
        CssLoader.ClearCaches();
        var first = Parse("red", "0.1", includeExternalSheet: true);
        var second = Parse("blue", "0.9", includeExternalSheet: true);
        var started = 0;
        var bothStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<string> Fetch(Uri _)
        {
            if (Interlocked.Increment(ref started) == 2) bothStarted.TrySetResult();
            await release.Task;
            return ".target { display: block; }";
        }

        var computations = Task.WhenAll(
            CssLoader.ComputeWithResultAsync(first.Root, first.BaseUri, Fetch, 800, 600),
            CssLoader.ComputeWithResultAsync(second.Root, second.BaseUri, Fetch, 1200, 900));
        await bothStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        release.SetResult();
        await computations;

        Assert.Equal(2, Volatile.Read(ref started));
    }

    private static (Element Root, Element Target, Uri BaseUri) Parse(
        string color,
        string opacity,
        bool includeExternalSheet = false)
    {
        var link = includeExternalSheet ? "<link rel='stylesheet' href='/site.css'>" : string.Empty;
        var uri = new Uri($"https://{color}.isolation.test/");
        var html = "<!doctype html><html><head>" + link + "<style>" +
            $":root {{ --accent: {color}; }}" +
            $"@keyframes pulse {{ from {{ opacity: {opacity}; }} to {{ opacity: 1; }} }}" +
            ".target { color: var(--accent); animation-name: pulse; }" +
            "</style></head><body><div class='target'></div></body></html>";
        var document = new HtmlParser(html, uri).Parse();
        var root = document.DocumentElement;
        var target = root.Descendants().OfType<Element>().First(e => e.ClassList.Contains("target"));
        return (root, target, uri);
    }
}
