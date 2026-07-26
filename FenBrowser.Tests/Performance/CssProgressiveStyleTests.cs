using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FenBrowser.Core.Css;
using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Rendering;
using FenBrowser.FenEngine.Rendering.Css;
using Xunit;

namespace FenBrowser.Tests.Performance
{
    [Collection("Performance diagnostics")]
    public class CssProgressiveStyleTests
    {
        [Fact]
        public async Task ComputeAsyncPublishesLocalStylesBeforeExternalStylesheetCompletes()
        {
            const string html = """
                <!doctype html>
                <html>
                  <head>
                    <style>#target { color: red; }</style>
                    <link rel="stylesheet" href="/slow.css">
                  </head>
                  <body><div id="target">target</div></body>
                </html>
                """;
            var baseUri = new Uri("https://test.local/");
            var root = new HtmlParser(html, baseUri).Parse().DocumentElement;
            var target = root.OwnerDocument.GetElementById("target");
            var releaseExternal = new TaskCompletionSource<string>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var progressiveReady = new TaskCompletionSource<Dictionary<Node, CssComputed>>(
                TaskCreationOptions.RunContinuationsAsynchronously);

            var computeTask = CssLoader.ComputeAsync(
                root,
                baseUri,
                _ => releaseExternal.Task,
                viewportWidth: 800,
                viewportHeight: 600,
                progressiveStylesReady: styles => progressiveReady.TrySetResult(styles));

            var firstCompleted = await Task.WhenAny(
                progressiveReady.Task,
                Task.Delay(TimeSpan.FromSeconds(2)));
            Assert.Same(progressiveReady.Task, firstCompleted);
            Assert.False(computeTask.IsCompleted);

            var progressive = await progressiveReady.Task;
            Assert.Equal("red", progressive[target].Map["color"]);

            releaseExternal.SetResult("#target { color: blue; }");
            var final = await computeTask;

            Assert.Equal("blue", final[target].Map["color"]);
        }
    }
}
