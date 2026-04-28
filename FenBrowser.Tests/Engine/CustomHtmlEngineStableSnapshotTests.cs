using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FenBrowser.FenEngine.Rendering;
using Xunit;

namespace FenBrowser.Tests.Engine
{
    public class CustomHtmlEngineStableSnapshotTests
    {
        [Fact]
        public async Task RenderAsync_JsEnabled_DelaysStableSnapshot_UntilPostScriptPass()
        {
            const string html = @"<!DOCTYPE html>
<html>
<head>
  <style>
    .pre { display:block; }
    .post { display:none; }
    body.ready .pre { display:none; }
    body.ready .post { display:block; }
  </style>
</head>
<body>
  <div class='pre'>pre</div>
  <div class='post'>post</div>
  <script>document.body.className = 'ready';</script>
</body>
</html>";

            using var engine = new CustomHtmlEngine
            {
                EnableJavaScript = true
            };

            var stableFlags = new List<bool>();
            engine.RepaintReady += _ => stableFlags.Add(engine.GetRenderSnapshot().HasStableStyles);

            await engine.RenderAsync(
                html,
                new Uri("https://stability.test/"),
                _ => Task.FromResult(string.Empty),
                _ => Task.FromResult<System.IO.Stream>(null),
                _ => { },
                viewportWidth: 1200,
                viewportHeight: 800);

            Assert.NotEmpty(stableFlags);
            Assert.Contains(false, stableFlags);
            Assert.True(stableFlags.Last(), "Expected the final repaint snapshot to be stable after the post-script pass.");
            Assert.True(engine.GetRenderSnapshot().HasStableStyles);
        }
    }
}