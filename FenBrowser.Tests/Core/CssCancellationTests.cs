using System;
using System.Threading;
using System.Threading.Tasks;
using FenBrowser.Core.Dom.V2;
using FenBrowser.FenEngine.Rendering;
using Xunit;

namespace FenBrowser.Tests.Core;

public sealed class CssCancellationTests
{
    [Fact]
    public async Task ComputeWithResultAsync_CancelsOutstandingStylesheetFetch()
    {
        var document = new Document();
        var html = document.CreateElement("html");
        var head = document.CreateElement("head");
        var link = document.CreateElement("link");
        link.SetAttribute("rel", "stylesheet");
        link.SetAttribute("href", "/blocked.css");
        head.AppendChild(link);
        html.AppendChild(head);
        document.AppendChild(html);

        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            CssLoader.ComputeWithResultAsync(
                html,
                new Uri("https://cancel.test/"),
                _ => Task.Delay(Timeout.InfiniteTimeSpan).ContinueWith(_ => string.Empty),
                cancellationToken: cancellation.Token));
    }
}
